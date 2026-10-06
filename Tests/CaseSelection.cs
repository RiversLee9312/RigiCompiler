using System.Globalization;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

/// <summary>兼容选择与资源声明的纯映射；不持有全量执行循环。</summary>
public static class CaseSelection
{
    public sealed record TaskSpec(string Id, string Suite, IReadOnlyList<int> Indices, IReadOnlyList<string> Args,
        string Granularity, TimeSpan? Timeout = null);

    private static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Binder"] = ["WRAP-001", "WRAP-001-review"], ["BilEmitter"] = ["WRAP-001", "WRAP-001-review"],
        ["CommandLineParser"] = ["PERF-001"], ["Middleware"] = ["COMP-003"],
    };
    internal static IReadOnlyList<string> GroupsFor(string suite) => Groups.TryGetValue(suite, out var groups) ? groups : [];

    private static string Id(string suite, IReadOnlyList<int> indices, IReadOnlyList<string> args) =>
        indices.Count == 1 && suite is not ("SemanticsFuzz" or "StressFuzz")
            && !(suite == "LexerFuzz" && TestInventory.Cases(suite)!.Single(item => item.Index == indices[0]).Label.StartsWith("fuzz-", StringComparison.Ordinal))
            ? CaseCatalog.StableId(suite, TestInventory.Cases(suite)!.Single(c => c.Index == indices[0]).Label)
            : "seed/" + suite + "/" + string.Join(',', indices);
    internal static TaskSpec Create(string suite, IReadOnlyList<int> indices, IReadOnlyList<string>? args = null, TimeSpan? timeout = null) =>
        new(Id(suite, indices, args ?? []), suite, indices.ToArray(), args?.ToArray() ?? [],
            suite is "SemanticsFuzz" or "StressFuzz" || suite == "LexerFuzz" && TestInventory.Cases(suite)!
                .Single(item => item.Index == indices[0]).Label.StartsWith("fuzz-", StringComparison.Ordinal)
                ? "seed-batch" : suite is "E2e" or "NativeE2E" ? "single-input" : "provider-method",
            timeout ?? (suite is "SemanticsFuzz" or "StressFuzz" ? System.Threading.Timeout.InfiniteTimeSpan
                // 完整 Native 对拍包含进程内 whole-program O2，正常冷优化可超过九分钟。
                : suite == "NativeE2E" ? TimeSpan.FromMinutes(TestInventory.Cases(suite)!
                    .Where(c => indices.Contains(c.Index)).Select(c => c.TimeoutMinutes ?? 45).DefaultIfEmpty(45).Max())
                : null));

    // 选择、Decode 与直接 case 客户端共用截止；显式调用方覆盖仍由客户端优先使用。
    public static TimeSpan TimeoutFor(string id) => Decode(id)?.Timeout ?? TimeSpan.FromMinutes(9);

    internal static IReadOnlyList<string> SelectLabels(IReadOnlyList<string> known, IReadOnlyList<string> selected)
    {
        if (selected.Count == 0 || selected.Any(label => !known.Contains(label, StringComparer.Ordinal)))
            throw new ArgumentException("存在未知精确标签或选择为空");
        return known.Where(label => selected.Contains(label, StringComparer.Ordinal)).ToArray();
    }

    public static IReadOnlyList<TaskSpec> Select(int number, IReadOnlyList<string>? args = null)
    {
        if (number < 1 || number > TestSuiteCatalog.Count) throw new ArgumentOutOfRangeException(nameof(number));
        var suite = TestSuiteCatalog.Names.ElementAt(number - 1); args ??= [];
        // 命名定向组只选择已发现的真实动作，没有整套 ID 回退。
        if (args.Count == 1 && GroupsFor(suite).Contains(args[0], StringComparer.OrdinalIgnoreCase)
            && StaticTestProviders.GroupLabels(suite, args[0]) is { } labels)
            args = new[] { "label" }.Concat(labels).ToArray();
        var inventory = TestInventory.Cases(suite)?.ToArray();
        if (inventory == null || inventory.Length == 0) throw new ArgumentException($"{suite} 没有可发现 provider");
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
        // 普通 provider 一个动作一行；fuzz 只对真实 seed 列表分批，保留稀疏序号。
        if (suite == "LexerFuzz")
        {
            var inventory = TestInventory.Cases(suite)!.ToArray();
            var fixedCases = indices.Where(index => !inventory[index].Label.StartsWith("fuzz-", StringComparison.Ordinal))
                .Select(index => Create(suite, [index], timeout: timeout));
            var seeded = indices.Where(index => inventory[index].Label.StartsWith("fuzz-", StringComparison.Ordinal))
                .Chunk(100).Select(batch => Create(suite, batch, timeout: timeout));
            return fixedCases.Concat(seeded).OrderBy(task => task.Indices[0]).ToArray();
        }
        int size = suite is "SemanticsFuzz" or "StressFuzz" ? 25 : 1;
        return indices.Chunk(size).Select(batch => Create(suite, batch, timeout: timeout)).ToArray();
    }
    internal static TaskSpec? Decode(string id)
    {
        if (CaseCatalog.FindExecution(id) is { } registered) return registered;
        var parts = id.Split('/');
        if (parts.Length != 3 || parts[0] != "seed" || parts[1] is not ("SemanticsFuzz" or "StressFuzz" or "LexerFuzz")) return null;
        var inventory = TestInventory.Cases(parts[1])!.ToArray();
        var values = parts[2].Split(',');
        if (values.Length == 0 || values.Length > (parts[1] == "LexerFuzz" ? 100 : 25)
            || values.Any(value => !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= inventory.Length)) return null;
        var indices = values.Select(int.Parse).ToArray();
        return indices.SequenceEqual(indices.Distinct().Order()) ? Create(parts[1], indices) : null;
    }
    internal static CaseDescriptor? Describe(string id) => Decode(id) is { } task
        ? new(id, task.Suite, "fuzz provider", task.Granularity, "Full", string.Join(',', task.Indices), task.Granularity, task.Indices.Count) : null;

    internal static ResourceRequest Resources(CaseDescriptor descriptor) =>
        Resources(descriptor, Environment.GetEnvironmentVariable("RIGI_TEST_NATIVE_CPU_SLOTS"));

    internal static ResourceRequest Resources(CaseDescriptor descriptor, string? nativeCpuSlots)
    {
        bool native = descriptor.Suite is "Native" or "NativeE2E" or "Middleware";
        bool compilerParallel = descriptor.Suite == "CompilerParallel";
        bool concurrency = descriptor.Suite.StartsWith("BilVm", StringComparison.Ordinal) || descriptor.Suite.StartsWith("VmFs", StringComparison.Ordinal);
        if (Decode(descriptor.Id) is { } task && task.Suite == "NativeE2E")
        {
            concurrency = TestInventory.Cases(task.Suite)!.Any(c => task.Indices.Contains(c.Index) && c.ComputeWorkers >= 4);
        }
        int cpu = concurrency || compilerParallel ? Math.Min(4, ResourceBudget.Shared.Capacity.CpuSlots) : 1;
        if (descriptor.Suite == "NativeE2E" && concurrency)
        {
            int slots = 4;
            if (nativeCpuSlots != null && (!int.TryParse(nativeCpuSlots, NumberStyles.None, CultureInfo.InvariantCulture, out slots)
                || slots is < 1 or > 4))
                throw new ArgumentException("RIGI_TEST_NATIVE_CPU_SLOTS 必须是 1..4 的十进制整数");
            // CPU 槽位是整例外层编译任务的调度权重；本机可降低串行 LLVM O2 持有的权重。
            // Runtime Compute 的真实并发人数独立保留为四，不能用该吞吐选项削弱并发覆盖。
            cpu = Math.Min(slots, ResourceBudget.Shared.Capacity.CpuSlots);
        }
        // 真并发覆盖始终至少四 Compute；它们可共享较少物理 slots，GC/IO 另有线程。
        var caseMemory = Decode(descriptor.Id) is { Indices.Count: > 0 } selected
            ? TestInventory.Cases(selected.Suite)?.Where(c => selected.Indices.Contains(c.Index)).Select(c => c.MemoryMiB).DefaultIfEmpty(512).Max() ?? 512
            : 512;
        return new(cpu, Math.Max(caseMemory, native || compilerParallel ? 2048 : 512), concurrency ? 4 : 1, 1);
    }
    public static ResourceRequest ResourcesFor(string id) => Resources(CaseCatalog.Find(id) ?? throw new ArgumentException("未知 case ID"));
    // 契约测试显式传配置，不改全局环境，避免与其它并发发现行串扰。
    internal static ResourceRequest ResourcesFor(string id, string? nativeCpuSlots) =>
        Resources(CaseCatalog.Find(id) ?? throw new ArgumentException("未知 case ID"), nativeCpuSlots);

}
