using System.Text.Json;

namespace RigiCompiler.Tests;

/// <summary>稳定 ID 与旧驱动的机器映射；只表示明确列出的细粒度迁移范围。</summary>
public sealed record CaseDescriptor(string Id, string Suite, string Source, string Group,
    string Trait, string LegacyRef);

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
    private sealed record Entry(CaseDescriptor Descriptor, Func<(int Assertions, int Failures)> Run,
        Func<string?>? Skip = null);

    private static Entry Harness(CaseDescriptor descriptor, Action run, Func<string?>? skip = null) =>
        new(descriptor, () => { run(); return (TestHarness.PassCount + TestHarness.FailCount, TestHarness.FailCount); }, skip);

    private static readonly Entry[] Entries =
    [
        new(new("lexer.slash", "Lexer", "Tests/LexerFuzzTests.cs", "slash", "positive",
            "LexerFuzzTests.TestFixedCases:a / b"),
            () => LexerFuzzTests.RunTokenCase("a / b", "W(a) N(/) W(b) EOF")),
        new(new("lexer.multiline-comment", "Lexer", "Tests/LexerFuzzTests.cs", "comment", "positive",
            "LexerFuzzTests.TestFixedCases:multiline-comment"),
            () => LexerFuzzTests.RunTokenCase("/* multi\nline */a", "C( multi) LB C(line ) W(a) EOF")),
        Harness(new("parser.add", "Parser", "Tests/ExpressionParserTests.cs", "expression", "positive",
            "ExpressionParserTests.TestBinaryExpressions:var r = 1 + 2"),
            () => ExpressionParserTests.TestExpr("var r = 1 + 2", "Binary(Int(1,I32) + Int(2,I32))")),
        Harness(new("parser.no-precedence", "Parser", "Tests/ExpressionParserTests.cs", "expression", "negative",
            "ExpressionParserTests.TestErrorCases:var e1 = 1 + 2 * 3"),
            () => TestHarness.CheckParseError("无优先级", () => TestHarness.ParseRoot("var e1 = 1 + 2 * 3"), "没有运算符优先级")),
        Harness(new("semantic.wrapper-this-return-negative", "Semantic", "Tests/e2e/rigi/wrapper_this_return_negative.rg", "wrapper", "negative",
            "E2eCorpusTests.RunCase:wrapper_this_return_negative"),
            () => E2eCorpusTests.RunExactCase("wrapper_this_return_negative")),
        Harness(new("bil.reader.scalar-roundtrip", "BIL", "Tests/BilReaderTests.cs", "reader", "positive",
            "BilReaderTests.TestRoundTripBasics:基础字面量与运算"), BilReaderTests.RoundTripScalar),
        Harness(new("bil.verifier.reserved-this", "BIL", "Tests/BilVerifierTests.cs", "verifier", "negative",
            "BilVerifierTests.NegativeCases:保留名作局部变量"), BilVerifierTests.ReservedThisNegative),
        Harness(new("vm.hello-world", "VM", "Tests/BilVmTests.cs", "execution", "positive",
            "BilVmTests.TestHelloWorld"), BilVmTests.TestHelloWorld),
        Harness(new("native.hello-world-bil", "Native", "Tests/NativeE2ETests.cs", "execution", "positive",
            "NativeE2ETests.RunBilCase:hello world 小 BIL（Case hello world 的等价小模块）"),
            NativeE2ETests.RunPilotHelloBil, () => NativeE2ETests.PilotSkipReason),
    ];

    public static IReadOnlyList<CaseDescriptor> All { get; } = Array.AsReadOnly(Entries.Select(e => e.Descriptor).ToArray());
    public static CaseDescriptor? Find(string id) => All.FirstOrDefault(c => c.Id == id) ?? LegacyDispatcher.Describe(id);

    public static CaseOutcome Run(string id)
    {
        var entry = Entries.SingleOrDefault(e => e.Descriptor.Id == id);
        if (entry == null) return LegacyDispatcher.RunWorker(id);
        return ExecuteCaptured(id, entry.Run, entry.Skip);
    }

    internal static CaseOutcome ExecuteCaptured(string id, Func<(int Assertions, int Failures)> run, Func<string?>? skip = null)
    {
        var state = Logger.CaptureState();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output); Console.SetError(error);
        TestHarness.Reset(); Logger.Reset();
        try
        {
            // 协议探针仅在显式环境门控下改变结果，默认目录仍是真实语义测试。
            var probe = Environment.GetEnvironmentVariable("RIGI_TEST_PROBE");
            if (probe == "environment") return new(id, CaseStatus.Pass, 1, 0, null,
                $"cpu={Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT")};compute={Environment.GetEnvironmentVariable("RIGI_COMPUTE_WORKERS")};lld={Environment.GetEnvironmentVariable("RIGI_LLD_THREADS")};gc={Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit")};spawned={TestRunner.IsSpawned};tmp={Path.GetTempPath()}");
            if (probe == "nested")
            {
                try { LegacyDispatcher.RunTasksAsync([]).GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { return new(id, CaseStatus.Pass, 1, 0, null, "spawned 拒绝嵌套队列"); }
                return new(id, CaseStatus.Fail, 1, 1, null, "spawned 错误允许嵌套队列");
            }
            if (probe?.StartsWith("finite-delay:", StringComparison.Ordinal) == true)
                Thread.Sleep(int.Parse(probe["finite-delay:".Length..], System.Globalization.CultureInfo.InvariantCulture));
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
            var counts = probe == "fail-lexer"
                ? LexerFuzzTests.RunTokenCase("a / b", "故意错误的 token 期望") : run();
            if (probe == "fail-harness")
            {
                TestHarness.CheckTrue("故意 Harness 失败", false);
                counts = (counts.Assertions + 1, counts.Failures + 1);
            }
            if (counts.Assertions == 0 && TestHarness.SkipReason is { } unsupported)
                return new(id, CaseStatus.Skip, 0, 0, unsupported, output + error.ToString());
            if (counts.Assertions == 0) return new(id, CaseStatus.Fail, 1, 1, null, "单 case 未执行任何断言");
            return new(id, counts.Failures == 0 ? CaseStatus.Pass : CaseStatus.Fail,
                counts.Assertions, counts.Failures, null, output + error.ToString());
        }
        catch (OperationCanceledException ex) { return new(id, CaseStatus.Cancel, 0, 0, null, ex.Message); }
        catch (Exception ex) { return new(id, CaseStatus.Fail, TestHarness.PassCount + TestHarness.FailCount + 1,
            TestHarness.FailCount + 1, null, output + error.ToString() + ex); }
        finally
        {
            Console.SetOut(oldOut); Console.SetError(oldErr);
            Logger.RestoreState(state); TestHarness.Reset();
        }
    }
}
