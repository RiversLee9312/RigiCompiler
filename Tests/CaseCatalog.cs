using System.Text.Json;

namespace RigiCompiler.Tests;

/// <summary>全量 provider 的稳定身份与来源映射；如实记录方法组、单输入或 seed 批次。</summary>
public sealed record CaseDescriptor(string Id, string Suite, string Source, string Group,
    string Trait, string LegacyRef, string Granularity = "provider-method", int InputCount = 1);

public enum CaseStatus { Pass, Fail, Skip, Cancel }

public sealed record CaseExecution(int ProcessId, double WallMilliseconds, double? ObservedTreeCpuMilliseconds,
    long? SampledPeakTreeRssBytes, int SamplingIntervalMilliseconds, ResourceRequest Lease);

/// <summary>断言数与 case 数分离；协议不依赖反射或控制台文本解析。</summary>
public sealed record CaseOutcome(string CaseId, CaseStatus Status, int Assertions, int Failures,
    string? SkipReason, string Diagnostics)
{
    public CaseExecution? Execution { get; init; }
    public void Write(string path)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteNumber("version", 1);
        json.WriteString("caseId", CaseId);
        json.WriteString("status", Status.ToString());
        json.WriteNumber("assertions", Assertions);
        json.WriteNumber("failures", Failures);
        json.WriteString("skipReason", SkipReason);
        json.WriteString("diagnostics", Diagnostics);
        if (Execution is { } execution)
        {
            json.WriteStartObject("execution");
            json.WriteNumber("processId", execution.ProcessId); json.WriteNumber("wallMilliseconds", execution.WallMilliseconds);
            if (execution.ObservedTreeCpuMilliseconds is { } cpu) json.WriteNumber("observedTreeCpuMilliseconds", cpu); else json.WriteNull("observedTreeCpuMilliseconds");
            if (execution.SampledPeakTreeRssBytes is { } rss) json.WriteNumber("sampledPeakTreeRssBytes", rss); else json.WriteNull("sampledPeakTreeRssBytes");
            json.WriteString("samplingScope", "linux-process-group; unavailable on other platforms");
            json.WriteNumber("samplingIntervalMilliseconds", execution.SamplingIntervalMilliseconds);
            json.WriteStartObject("lease");
            json.WriteNumber("cpuSlots", execution.Lease.CpuSlots); json.WriteNumber("memoryMiB", execution.Lease.MemoryMiB);
            json.WriteNumber("computeWorkers", execution.Lease.ComputeWorkers); json.WriteNumber("lldThreads", execution.Lease.LldThreads);
            json.WriteStartArray("exclusiveGroups"); foreach (var group in execution.Lease.ExclusiveGroups) json.WriteStringValue(group); json.WriteEndArray();
            json.WriteEndObject(); json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    public static CaseOutcome Read(string path, string expectedId)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = json.RootElement;
        var id = root.GetProperty("caseId").GetString();
        if (root.GetProperty("version").GetInt32() != 1 || id != expectedId)
            throw new InvalidDataException("worker 结果版本或 case ID 不匹配");
        if (!Enum.TryParse<CaseStatus>(root.GetProperty("status").GetString(), out var status)
            || !Enum.IsDefined(status)) throw new InvalidDataException("worker 状态无效");
        var outcome = new CaseOutcome(id!, status, root.GetProperty("assertions").GetInt32(),
            root.GetProperty("failures").GetInt32(), root.GetProperty("skipReason").GetString(),
            root.GetProperty("diagnostics").GetString() ?? "");
        if (root.TryGetProperty("execution", out var execution))
        {
            var lease = execution.GetProperty("lease");
            outcome = outcome with { Execution = new(execution.GetProperty("processId").GetInt32(), execution.GetProperty("wallMilliseconds").GetDouble(),
                execution.GetProperty("observedTreeCpuMilliseconds").ValueKind == JsonValueKind.Null ? null : execution.GetProperty("observedTreeCpuMilliseconds").GetDouble(),
                execution.GetProperty("sampledPeakTreeRssBytes").ValueKind == JsonValueKind.Null ? null : execution.GetProperty("sampledPeakTreeRssBytes").GetInt64(),
                execution.GetProperty("samplingIntervalMilliseconds").GetInt32(), new ResourceRequest(lease.GetProperty("cpuSlots").GetInt32(),
                    lease.GetProperty("memoryMiB").GetInt32(), lease.GetProperty("computeWorkers").GetInt32(), lease.GetProperty("lldThreads").GetInt32(),
                    lease.GetProperty("exclusiveGroups").EnumerateArray().Select(value => value.GetString()!).ToArray())) };
        }
        if (outcome.Assertions < 0 || outcome.Failures < 0 || outcome.Failures > outcome.Assertions
            || (status == CaseStatus.Pass && (outcome.Assertions == 0 || outcome.Failures != 0))
            || (status == CaseStatus.Fail && outcome.Failures == 0)
            || (status is CaseStatus.Skip or CaseStatus.Cancel && outcome.Failures != 0)
            || (status == CaseStatus.Skip && string.IsNullOrWhiteSpace(outcome.SkipReason)))
            throw new InvalidDataException("worker 断言计数或 Skip 理由无效");
        return outcome;
    }
}

public static class CaseCatalog
{
    private sealed record Entry(CaseDescriptor Descriptor, CaseSelection.TaskSpec Task);
    private static readonly Entry[] Entries = Build().ToArray();
    private static readonly IReadOnlyDictionary<string, Entry> ById = Entries.ToDictionary(entry => entry.Descriptor.Id, StringComparer.Ordinal);
    public static IReadOnlyList<CaseDescriptor> All { get; } = Array.AsReadOnly(Entries.Select(entry => entry.Descriptor).ToArray());

    // 稳定身份由 provider 标签派生，插入或重排其他动作不会改变既有 ID。
    internal static string StableId(string suite, string label) => (suite, label) switch
    {
        ("LexerFuzz", "lexer.slash") => "lexer.slash",
        ("LexerFuzz", "lexer.multiline-comment") => "lexer.multiline-comment",
        ("Expression", "parser.add") => "parser.add",
        ("Expression", "parser.no-precedence") => "parser.no-precedence",
        ("E2e", "wrapper_this_return_negative") => "semantic.wrapper-this-return-negative",
        ("BilReader", "bil.reader.scalar-roundtrip") => "bil.reader.scalar-roundtrip",
        ("BilVerifier", "bil.verifier.reserved-this") => "bil.verifier.reserved-this",
        ("BilVm", "TestHelloWorld") => "vm.hello-world",
        ("NativeE2E", "native.hello-world-bil") => "native.hello-world-bil",
        _ => "case." + suite + "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)))[..16].ToLowerInvariant(),
    };
    private static IEnumerable<Entry> Build()
    {
        foreach (var suite in TestSuiteCatalog.Names)
        {
            var inventory = TestInventory.Cases(suite)!.ToArray();
            IEnumerable<int[]> rows;
            if (suite is "SemanticsFuzz" or "StressFuzz") rows = inventory.Select(item => item.Index).Chunk(25);
            else if (suite == "LexerFuzz") rows = inventory.Where(item => !item.Label.StartsWith("fuzz-", StringComparison.Ordinal)).Select(item => new[] { item.Index })
                .Concat(inventory.Where(item => item.Label.StartsWith("fuzz-", StringComparison.Ordinal)).Select(item => item.Index).Chunk(100)).OrderBy(indices => indices[0]);
            else rows = inventory.Select(item => new[] { item.Index });
            foreach (var indices in rows)
            {
                var task = CaseSelection.Create(suite, indices);
                var labels = indices.Select(index => inventory[index].Label).ToArray();
                var granularity = task.Granularity;
                yield return new(new(task.Id, suite, "静态 provider：" + suite, granularity, "Full", string.Join(";", labels), granularity, indices.Length), task);
            }
        }
    }
    internal static CaseSelection.TaskSpec? FindExecution(string id) => ById.TryGetValue(id, out var entry) ? entry.Task : null;
    public static CaseDescriptor? Find(string id) => ById.TryGetValue(id, out var entry) ? entry.Descriptor : CaseSelection.Describe(id);

    public static CaseOutcome Run(string id)
    {
        var task = CaseSelection.Decode(id);
        if (task == null) return new(id, CaseStatus.Fail, 1, 1, null, "未知 worker ID");
        using var metric = PerformanceMetrics.Begin("test.suite", task.Suite);
        PerformanceMetrics.Event("test.task", task.Suite, task.Granularity, id);
        var outcome = ExecuteCaptured(id, () =>
        {
            if (task.Suite == "SemanticsFuzz") { SemanticsFuzzTests.RunSelected(task.Indices); return; }
            if (task.Suite == "StressFuzz") { StressFuzzTests.RunSelected(task.Indices); return; }
            var spec = TestInventory.Spec(task.Suite)!;
            foreach (var index in task.Indices)
            {
                try { spec.Cases[index].Run(); }
                catch (Exception exception) { CaseAssertions.CheckTrue(spec.Cases[index].Label + "：测试异常", false, exception.ToString()); }
            }
        }, () =>
        {
            if (task.Suite == "NativeE2E" && RigiCompiler.Middleware.Toolchain.ToolchainResolver.ResolveClang(null) == null) return "未找到 clang 工具链";
            var gated = TestInventory.Cases(task.Suite)!.Where(item => task.Indices.Contains(item.Index) && item.Slow && item.Gate != null
                && Environment.GetEnvironmentVariable(item.Gate) != "1").ToArray();
            return gated.Length > 0 && Environment.GetEnvironmentVariable("RIGI_TEST_EXPLICIT_SELECTION") != "1"
                ? "默认慢门控未开启：" + string.Join(", ", gated.Select(item => item.Label)) : null;
        });
        metric?.ExitCode(outcome.Status is CaseStatus.Pass or CaseStatus.Skip ? 0 : outcome.Status == CaseStatus.Cancel ? 130 : 1);
        return outcome;
    }

    internal static CaseOutcome ExecuteCaptured(string id, Action run, Func<string?>? skip = null)
    {
        var state = Logger.CaptureState();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        WorkerConsole.SetOut(output); WorkerConsole.SetError(error);
        using var assertions = CaseAssertions.Begin();
        Logger.Reset();
        try
        {
            // 协议探针仅在显式环境门控下改变结果，默认目录仍是真实语义测试。
            var probe = Environment.GetEnvironmentVariable("RIGI_TEST_PROBE");
            if (probe == "environment") return new(id, CaseStatus.Pass, 1, 0, null,
                $"cpu={Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT")};compute={Environment.GetEnvironmentVariable("RIGI_COMPUTE_WORKERS")};lld={Environment.GetEnvironmentVariable("RIGI_LLD_THREADS")};gc={Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit")};spawned={WorkerEnvironment.IsWorker};tmp={Path.GetTempPath()}");
            if (probe == "nested")
            {
                try { CaseWorkers.RunTasksAsync([]).GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { return new(id, CaseStatus.Pass, 1, 0, null, "spawned 拒绝嵌套队列"); }
                return new(id, CaseStatus.Fail, 1, 1, null, "spawned 错误允许嵌套队列");
            }
            if (probe?.StartsWith("finite-delay:", StringComparison.Ordinal) == true)
                Thread.Sleep(int.Parse(probe["finite-delay:".Length..], System.Globalization.CultureInfo.InvariantCulture));
            if (probe?.StartsWith("peer-barrier:", StringComparison.Ordinal) == true)
            {
                // 只在显式协议探针中使用跨进程屏障；串行 worker 无法满足会合条件。
                var directory = probe["peer-barrier:".Length..];
                File.WriteAllText(Path.Combine(directory, Environment.ProcessId + ".ready"), "ready");
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (Directory.GetFiles(directory, "*.ready").Length < 2)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("两个 worker 未真实同时执行");
                    Thread.Sleep(10);
                }
            }
            if (probe == "skip") return new(id, CaseStatus.Skip, 0, 0, "显式 Skip 协议探针", "");
            if (probe == "delay") Thread.Sleep(Timeout.Infinite);
            if (probe == "crash") Environment.Exit(17);
            if (probe?.StartsWith("grandchild-tree:", StringComparison.Ordinal) == true && OperatingSystem.IsLinux())
            {
                var child = new System.Diagnostics.ProcessStartInfo("/bin/bash") { UseShellExecute = false };
                foreach (var argument in new[] { "-c", "echo $$ > \"$1\"; sleep 120 & echo $! > \"$1.grandchild\"; wait", "--", probe["grandchild-tree:".Length..] })
                    child.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(child);
                Thread.Sleep(Timeout.Infinite);
            }
            if (probe?.StartsWith("orphan-pipe:", StringComparison.Ordinal) == true && OperatingSystem.IsLinux())
            {
                // 根早退但后台后代继承管道，验证父侧必须灭 group 而非只等 root。
                var child = new System.Diagnostics.ProcessStartInfo("/bin/bash") { UseShellExecute = false };
                foreach (var argument in new[] { "-c", "echo $$ > \"$1\"; sleep 120 & wait", "--", probe["orphan-pipe:".Length..] })
                    child.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(child);
                Environment.Exit(17);
            }
            if (skip?.Invoke() is { } reason) return new(id, CaseStatus.Skip, 0, 0, reason, "");
            if (probe == "fail-lexer") LexerFuzzTests.RunTokenCase("a / b", "故意错误的 token 期望");
            else run();
            if (probe == "fail-harness") CaseAssertions.CheckTrue("故意断言失败", false);
            if (assertions.AssertionCount == 0 && assertions.SkipReason is { } unsupported)
                return new(id, CaseStatus.Skip, 0, 0, unsupported, output + error.ToString());
            if (assertions.AssertionCount == 0) CaseAssertions.CheckTrue("单 case 必须执行断言", false);
            return new(id, assertions.FailureCount == 0 ? CaseStatus.Pass : CaseStatus.Fail,
                assertions.AssertionCount, assertions.FailureCount, null, output + error.ToString());
        }
        catch (OperationCanceledException ex) { return new(id, CaseStatus.Cancel, 0, 0, null, ex.Message); }
        catch (Exception exception)
        {
            CaseAssertions.CheckTrue("隔离动作异常", false, exception.ToString());
            return new(id, CaseStatus.Fail, assertions.AssertionCount, assertions.FailureCount, null, output + error.ToString());
        }
        finally
        {
            WorkerConsole.SetOut(oldOut); WorkerConsole.SetError(oldErr);
            Logger.RestoreState(state);
        }
    }
}
