using System.Globalization;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

/// <summary>同一父进程跨 suite 的动态 pending 队列；动作只在隔离 worker 中运行。</summary>
public static class LegacyDispatcher
{
    public sealed record TaskSpec(string Id, string Suite, IReadOnlyList<int> Indices, IReadOnlyList<string> Args,
        string Granularity, TimeSpan? Timeout = null);

    private static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Binder"] = ["WRAP-001", "WRAP-001-review"], ["BilEmitter"] = ["WRAP-001", "WRAP-001-review"],
        ["CommandLineParser"] = ["PERF-001"], ["Middleware"] = ["COMP-003"],
    };
    // 显式旧整组/定向组在一个测试方法内保留多份 stdlib AST 与
    // 语义图（Debug 命名局部可活到方法结束），不能套用单编译的 512MiB。
    private static readonly HashSet<string> CompilerBulkSuites = new(StringComparer.Ordinal)
    {
        "DeclarationResolver", "Binder", "BilEmitter", "Lowerer", "SmartCast", "StdlibSources",
    };
    internal static IReadOnlyList<string> GroupsFor(string suite) => Groups.TryGetValue(suite, out var groups) ? groups : [];

    private static string Id(string suite, IReadOnlyList<int> indices, IReadOnlyList<string> args) =>
        "legacy/" + suite + "/" + (indices.Count > 0 ? string.Join(',', indices) : "suite" + (args.Count == 0 ? "" : "/" + args[0]));
    private static TaskSpec Create(string suite, IReadOnlyList<int> indices, IReadOnlyList<string>? args = null, TimeSpan? timeout = null) =>
        new(Id(suite, indices, args ?? []), suite, indices.ToArray(), args?.ToArray() ?? [],
            indices.Count == 0 ? "suite-exit" : suite is "SemanticsFuzz" or "StressFuzz" ? "seed-batch" : indices.Count == 1 ? "case" : "case-batch",
            timeout ?? (suite is "SemanticsFuzz" or "StressFuzz" ? System.Threading.Timeout.InfiniteTimeSpan
                // 完整 Native 对拍包含进程内 whole-program O2，正常冷优化可超过九分钟。
                // 三个旧整组则累加多次 stdlib 编译；只给这些重型工作负载有限窗口。
                // 完整 BilEmitter 为共享预算满载时仍推进的整套编译留有限余量。
                : suite == "NativeE2E" ? TimeSpan.FromMinutes(TestInventory.Cases(suite)!
                    .Where(c => indices.Contains(c.Index)).Select(c => c.TimeoutMinutes ?? 45).DefaultIfEmpty(45).Max())
                : indices.Count == 0 && (args == null || args.Count == 0)
                    && suite is "Binder" or "BilEmitter" or "Lowerer"
                    ? TimeSpan.FromMinutes(suite == "Binder" ? 60 : suite == "BilEmitter" ? 75 : 30) : null));

    // dispatcher、Decode 与直接 case 客户端共用截止；显式调用方覆盖仍由客户端优先使用。
    public static TimeSpan TimeoutFor(string id) => Decode(id)?.Timeout ?? TimeSpan.FromMinutes(9);

    public static IReadOnlyList<TaskSpec> Select(int number, IReadOnlyList<string>? args = null)
    {
        if (number < 1 || number > TestRunner.SuiteCount) throw new ArgumentOutOfRangeException(nameof(number));
        var suite = TestRunner.SuiteNames.ElementAt(number - 1); args ??= [];
        // 命名定向组也映射到真实动作；显式旧 suite/group ID 仍由 Decode 兼容。
        if (args.Count == 1 && GroupsFor(suite).Contains(args[0], StringComparer.OrdinalIgnoreCase)
            && LegacySuiteSpecs.GroupLabels(suite, args[0]) is { } labels)
            args = new[] { "label" }.Concat(labels).ToArray();
        var inventory = TestInventory.Cases(suite)?.ToArray();
        if (inventory == null)
        {
            if (args.Count > 0 && (args.Count != 1 || !GroupsFor(suite).Contains(args[0], StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException($"{suite} 不支持此选择器");
            return [Create(suite, [], args)];
        }
        if (args.Count == 1 && args[0] == "COMP-003" && suite == "Middleware")
            return Batch(suite, inventory.Where(c => c.Label.StartsWith("TestComp003", StringComparison.Ordinal)).Select(c => c.Index).ToArray());
        TimeSpan? timeout = null;
        int[] indices;
        if (args.Count == 0)
            indices = inventory.Where(c => !c.Slow || c.Gate != null && Environment.GetEnvironmentVariable(c.Gate) == "1").Select(c => c.Index).ToArray();
        else if (args[0] == "label")
        {
            if (args.Count < 2 || args.Skip(1).Any(label => !inventory.Any(c => c.Label == label)))
                throw new ArgumentException($"{suite} 存在未知精确标签");
            indices = inventory.Where(c => args.Skip(1).Contains(c.Label, StringComparer.Ordinal)).Select(c => c.Index).ToArray();
        }
        else if (args[0] == "indices")
        {
            if (suite is not ("SemanticsFuzz" or "StressFuzz") || args.Count < 2
                || args.Skip(1).Any(value => !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int i) || i < 0 || i >= inventory.Length))
                throw new ArgumentException("indices 只接受当前 fuzz 预算内的全局序号");
            indices = args.Skip(1).Select(int.Parse).Distinct().Order().ToArray();
        }
        else if (int.TryParse(args[0], out int from))
        {
            if (args.Count is < 2 or > 3 || !int.TryParse(args[1], out int to) || from < 0 || to < from || to >= inventory.Length)
                throw new ArgumentException($"{suite} 范围无效");
            if (args.Count == 3)
            {
                const string prefix = "child-timeout-ms=";
                if (suite != "SemanticsFuzz" || !args[2].StartsWith(prefix, StringComparison.Ordinal)
                    || !int.TryParse(args[2][prefix.Length..], out int ms) || ms <= 0) throw new ArgumentException("child-timeout-ms 无效");
                timeout = TimeSpan.FromMilliseconds(ms);
            }
            indices = inventory.Where(c => c.Index >= from && c.Index <= to && (!c.Slow || c.Gate != null && Environment.GetEnvironmentVariable(c.Gate) == "1")).Select(c => c.Index).ToArray();
        }
        else if (suite is "NativeE2E" or "E2e")
        {
            // 旧子串选择仍可用，但每个输入必须匹配，不能悄悄忽略未知项。
            if (args.Any(filter => !inventory.Any(c => c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException($"{suite} 名称选择零匹配");
            var exact = args.Count == 1 && inventory.Any(c => c.Label.Equals(args[0], StringComparison.OrdinalIgnoreCase));
            indices = inventory.Where(c => exact ? c.Label.Equals(args[0], StringComparison.OrdinalIgnoreCase)
                : args.Any(filter => c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))).Select(c => c.Index).ToArray();
        }
        else throw new ArgumentException($"{suite} 未知选择器");
        if (indices.Length == 0) throw new ArgumentException($"{suite} 选择零用例");
        return Batch(suite, indices, timeout);
    }
    private static IReadOnlyList<TaskSpec> Batch(string suite, int[] indices, TimeSpan? timeout = null)
    {
        // 编译/VM 轻例按小批控制启动成本；native 较重逐 case，fuzz 保留稀疏全局序号。
        // E2e 的完整编译与 BilVmStress 的多轮 VM 回归各自有执行成本，
        // 单例避免四个输入叠在同一个九分钟窗口内，保留原每输入预算与全部轮数。
        int size = suite == "LexerFuzz" ? 100 : suite is "SemanticsFuzz" or "StressFuzz" ? 25
            : LegacySuiteSpecs.Find(suite) != null || suite is "NativeE2E" or "Middleware" or "E2e" or "BilVmStress" ? 1 : 4;
        if (suite != "Module")
            return indices.Chunk(size).Select(batch => Create(suite, batch, timeout: timeout)).ToArray();
        // 模块重型 case 会独立编译标准库/Native，不能在同一 worker 截止内累加多个 O2。
        // 复用实际资源声明；轻例仍小批，保持选择顺序与原 case 身份。
        var heavy = TestInventory.Cases(suite)!.Where(c => c.MemoryMiB >= 2048).Select(c => c.Index).ToHashSet();
        var tasks = new List<TaskSpec>();
        var pending = new List<int>();
        void Flush()
        {
            if (pending.Count == 0) return;
            tasks.Add(Create(suite, pending, timeout: timeout)); pending.Clear();
        }
        foreach (var index in indices)
            if (heavy.Contains(index))
            { Flush(); tasks.Add(Create(suite, [index], timeout: timeout)); }
            else
            { pending.Add(index); if (pending.Count == size) Flush(); }
        Flush();
        return tasks;
    }
    internal static TaskSpec? Decode(string id)
    {
        var parts = id.Split('/');
        if (parts.Length is < 3 or > 4 || parts[0] != "legacy" || TestRunner.GetSuiteNumber(parts[1]) < 1) return null;
        var suite = parts[1];
        if (parts[2] == "suite")
        {
            if (TestInventory.Cases(suite) != null && LegacySuiteSpecs.Find(suite) == null) return null;
            if (parts.Length == 4 && !GroupsFor(suite).Contains(parts[3], StringComparer.OrdinalIgnoreCase)) return null;
            return Create(suite, [], parts.Length == 4 ? [parts[3]] : []);
        }
        if (parts.Length != 3) return null;
        var inventory = TestInventory.Cases(suite)?.ToArray();
        if (inventory == null) return null;
        var values = parts[2].Split(',');
        if (values.Length == 0 || values.Length > (suite == "LexerFuzz" ? 100 : 25) || values.Any(v => !int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int i) || i < 0 || i >= inventory.Length)) return null;
        var indices = values.Select(int.Parse).ToArray();
        if (!indices.SequenceEqual(indices.Distinct().Order())) return null;
        return Create(suite, indices);
    }
    internal static CaseDescriptor? Describe(string id) => Decode(id) is { } task
        ? new(id, task.Suite, "legacy suite provider", task.Granularity, "legacy", task.Indices.Count > 0 ? string.Join(',', task.Indices) : string.Join(' ', task.Args)) : null;

    internal static ResourceRequest Resources(CaseDescriptor descriptor)
    {
        bool native = descriptor.Suite is "Native" or "NativeE2E" or "Middleware";
        bool compilerParallel = descriptor.Suite == "CompilerParallel";
        bool compilerBulk = CompilerBulkSuites.Contains(descriptor.Suite)
            && Decode(descriptor.Id) is { Indices.Count: 0 };
        bool concurrency = descriptor.Suite.StartsWith("BilVm", StringComparison.Ordinal) || descriptor.Suite.StartsWith("VmFs", StringComparison.Ordinal);
        if (Decode(descriptor.Id) is { } task && task.Suite == "NativeE2E")
        {
            concurrency = TestInventory.Cases(task.Suite)!.Any(c => task.Indices.Contains(c.Index) && c.ComputeWorkers >= 4);
        }
        int cpu = concurrency || compilerParallel ? Math.Min(4, ResourceBudget.Shared.Capacity.CpuSlots) : 1;
        // 真并发覆盖始终至少四 Compute；它们可共享较少物理 slots，GC/IO 另有线程。
        var caseMemory = Decode(descriptor.Id) is { Indices.Count: > 0 } selected
            ? TestInventory.Cases(selected.Suite)?.Where(c => selected.Indices.Contains(c.Index)).Select(c => c.MemoryMiB).DefaultIfEmpty(512).Max() ?? 512
            : 512;
        return new(cpu, Math.Max(caseMemory, native || compilerParallel || compilerBulk ? 2048 : 512), concurrency ? 4 : 1, 1);
    }
    public static ResourceRequest ResourcesFor(string id) => Resources(CaseCatalog.Find(id) ?? throw new ArgumentException("未知 case ID"));

    internal static CaseOutcome RunWorker(string id)
    {
        var task = Decode(id);
        if (task == null) return new(id, CaseStatus.Fail, 1, 1, null, "未知 legacy worker ID");
        using var metric = task.Indices.Count > 0 ? PerformanceMetrics.Begin("test.suite", task.Suite) : null;
        PerformanceMetrics.Event("test.task", task.Suite, task.Granularity, task.Id);
        var outcome = CaseCatalog.ExecuteCaptured(id, () =>
        {
            if (task.Indices.Count == 0)
            {
                int failed = TestRunner.RunSuiteInProcess(TestRunner.GetSuiteNumber(task.Suite), task.Args);
                // 私有计数单块只能诚实断言 suite 返回退出契约，不能伪造内部细粒断言。
                return (1, failed == 0 ? 0 : 1);
            }
            if (task.Suite == "SemanticsFuzz") { SemanticsFuzzTests.RunSelected(task.Indices); return SemanticsFuzzTests.SelectedCounts; }
            if (task.Suite == "StressFuzz") { StressFuzzTests.RunSelected(task.Indices); return StressFuzzTests.SelectedCounts; }
            var spec = TestInventory.Spec(task.Suite)!;
            foreach (var index in task.Indices)
            {
                try { spec.Cases[index].Run(); }
                catch (Exception ex) { TestHarness.CheckTrue(spec.Cases[index].Label + "：测试异常", false, ex.ToString()); }
            }
            return (TestHarness.PassCount + TestHarness.FailCount, TestHarness.FailCount);
        }, task.Suite == "NativeE2E" ? () => ToolchainResolver.ResolveClang(null) == null ? "未找到 clang 工具链" : null : null);
        metric?.ExitCode(outcome.Status is CaseStatus.Pass or CaseStatus.Skip ? 0 : outcome.Status == CaseStatus.Cancel ? 130 : 1);
        return outcome;
    }
    public static int RunIndices(string suite, int[] indices, TimeSpan? timeout = null) =>
        RunTasksAsync(Batch(suite, indices, timeout)).GetAwaiter().GetResult().Sum(outcome => outcome.Status == CaseStatus.Cancel ? 1 : outcome.Failures);

    public static async Task<IReadOnlyList<CaseOutcome>> RunTasksAsync(IReadOnlyList<TaskSpec> tasks, CancellationToken cancellationToken = default,
        Action<TaskSpec, CaseOutcome>? completed = null)
    {
        if (TestRunner.IsSpawned) throw new InvalidOperationException("--spawned worker 禁止嵌套调度");
        var client = new CaseWorkerClient();
        // 全部 suite 入同一 pending；budget 控制真实资源而非每 suite 各自开满 worker。
        async Task<CaseOutcome> Run(TaskSpec task)
        {
            var outcome = await client.RunAsync(task.Id, cancellationToken, task.Timeout);
            completed?.Invoke(task, outcome);
            return outcome;
        }
        // 完成通知按真实完成时刻发出；WhenAll 的返回值仍保持输入顺序。
        return await Task.WhenAll(tasks.Select(Run));
    }
    internal static int RunSuites(IReadOnlyList<int> numbers, IReadOnlyList<string>? args = null)
    {
        if (args is { Count: 1 } && args[0] == "list")
        {
            foreach (int number in numbers)
            {
                var cases = TestInventory.Cases(TestRunner.SuiteNames.ElementAt(number - 1));
                if (cases == null) return 2;
                foreach (var entry in cases) Console.WriteLine($"{entry.Index}: {entry.Label}");
            }
            return 0;
        }
        TaskSpec[] tasks;
        try { tasks = numbers.SelectMany(number => Select(number, args)).ToArray(); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        var progressGate = new object();
        int finished = 0;
        var outcomes = RunTasksAsync(tasks, completed: (task, outcome) =>
        {
            lock (progressGate)
                Console.WriteLine($"  [progress] {++finished}/{tasks.Length} {task.Id}: {outcome.Status}, {outcome.Assertions - outcome.Failures} passed, {outcome.Failures} failed");
        }).GetAwaiter().GetResult();
        foreach (var (task, outcome) in tasks.Zip(outcomes))
        {
            Console.WriteLine($"  [{outcome.Status}] {task.Id}: {outcome.Assertions - outcome.Failures} passed, {outcome.Failures} failed ({task.Granularity})");
            if (outcome.Status == CaseStatus.Skip) Console.WriteLine("    " + outcome.SkipReason);
            if (outcome.Status == CaseStatus.Fail) ParallelSuiteRunner.PrintFailureOutput(outcome.Diagnostics);
        }
        int failed = outcomes.Sum(o => o.Status == CaseStatus.Cancel ? 1 : o.Failures);
        Console.WriteLine($"=== dispatcher: {outcomes.Sum(o => o.Assertions - o.Failures)} passed, {failed} failed; {tasks.Length} tasks ===");
        return failed;
    }
}
