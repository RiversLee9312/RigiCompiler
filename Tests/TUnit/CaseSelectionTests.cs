using RigiCompiler.Tests;
using RigiCompiler;
using System.Diagnostics;
using TUnit.Core;

namespace RigiCompiler.TUnitTests;

[Category("CaseSelection")]
public class CaseSelectionTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    [Test]
    public void UnknownSelections()
    {
        foreach (var (suite, args) in new[] {
            ("BilReader", new[] { "label", "unknown-label" }), ("BilReader", new[] { "0", "999999" }),
            ("Binder", new[] { "unknown-group" }), ("E2e", new[] { "absent-case-TEST002" }),
            ("StressFuzz", new[] { "indices", "0", "999999" }) })
        {
            try { CaseSelection.Select(TestSuiteCatalog.GetNumber(suite), args); throw new InvalidOperationException("未知选择被接受：" + suite); }
            catch (ArgumentException) { }
        }
    }
    [Test]
    public void SparseAndHonestGranularity()
    {
        var sparse = CaseSelection.Select(TestSuiteCatalog.GetNumber("SemanticsFuzz"), ["indices", "121", "0", "60", "60"]);
        Check(sparse.SelectMany(task => task.Indices).SequenceEqual(new[] { 0, 60, 121 }), "稀疏索引必须去重排序而不扩为连续区间");
        Check(sparse.Single().Granularity == "seed-batch", "fuzz 如实报告批次");
        Check(sparse.Single().Timeout == Timeout.InfiniteTimeSpan, "fuzz 默认保持不限时");
        var timed = CaseSelection.Select(TestSuiteCatalog.GetNumber("SemanticsFuzz"), ["0", "1", "child-timeout-ms=123"]);
        Check(timed.Single().Timeout == TimeSpan.FromMilliseconds(123), "显式fuzz截止保留");
        var lexer = CaseSelection.Select(TestSuiteCatalog.GetNumber("LexerFuzz"));
        Check(lexer.Count > 1 && lexer.All(t => t.Indices.Count is > 0 and <= 100)
            && lexer.SelectMany(t => t.Indices).SequenceEqual(Enumerable.Range(0, 6007)),
            "Lexer 的五个固定组、全部6000输入和抽出的两个pilot须恰好覆盖，不能遗漏、重复或回退整套");
        Check(CaseCatalog.Find("legacy/LexerFuzz/suite") == null, "旧整套 ID 不得回退执行");
        foreach (var suite in new[] { "SemanticsFuzz", "StressFuzz" })
        {
            var single = CaseSelection.Select(TestSuiteCatalog.GetNumber(suite), ["0", "0"]).Single();
            Check(CaseCatalog.Find(single.Id)?.InputCount == 1, "单 seed 也必须可发现：" + suite);
        }
        var exactLexer = CaseSelection.Select(TestSuiteCatalog.GetNumber("LexerFuzz"), ["label", "fuzz-0000-纯随机"]).Single();
        Check(CaseCatalog.Find(exactLexer.Id)?.InputCount == 1, "精确 fuzz 标签必须可发现");
        var nativeDefault = CaseSelection.Select(TestSuiteCatalog.GetNumber("NativeE2E"));
        Check(nativeDefault.All(task => task.Indices.Count == 1), "native重例逐case");
    }
    [Test]
    public void ModuleHeavyDeadlineBoundaries()
    {
        var module = TestSuiteCatalog.GetNumber("Module");
        var native = CaseSelection.Select(module, ["24", "27"]);
        Check(native.Count == 4 && native.All(t => t.Indices.Count == 1 && t.Granularity == "provider-method" && t.Timeout == null),
            "真实 Native 模块各自使用 worker 默认截止，不合批也不抬期限");
        var numeric = CaseSelection.Select(module, ["0", "7"]);
        Check(numeric.Count == 8 && numeric.SelectMany(t => t.Indices).SequenceEqual(Enumerable.Range(0, 8)),
            "普通 provider 每个动作单独发现，不能再合成小批");
        var named = CaseSelection.Select(module, ["label", "A1.StrictConfiguration", "A2.TypedResources", "A2.OriginCompilation", "E4.FullModuleLibraries"]);
        Check(named.SelectMany(t => t.Indices).SequenceEqual(new[] { 0, 4, 5, 27 }) && named.All(t => t.Indices.Count == 1)
            && named.All(t => CaseCatalog.Find(t.Id) != null), "精确标签与数值选择共用稳定发现身份");
        var reader = CaseSelection.Select(TestSuiteCatalog.GetNumber("BilReader"), ["0", "3"]);
        Check(reader.Count == 4 && reader.SelectMany(t => t.Indices).SequenceEqual(new[] { 0, 1, 2, 3 }), "轻量 provider 同样独立发现");
    }
    [Test]
    public void HeavyAndLightDeadlines()
    {
        foreach (var suite in new[] { "Binder", "BilEmitter", "Lowerer" })
        {
            var tasks = CaseSelection.Select(TestSuiteCatalog.GetNumber(suite));
            Check(tasks.Count > 1 && tasks.All(t => t.Indices.Count == 1 && t.Timeout == null
                    && CaseSelection.TimeoutFor(t.Id) == TimeSpan.FromMinutes(9)),
                "完整入口拆成独立组，各自保留有限截止：" + suite);
            Check(CaseCatalog.Find("legacy/" + suite + "/suite") == null, "旧整套执行身份已移除：" + suite);
        }
        var native = CaseSelection.Select(TestSuiteCatalog.GetNumber("NativeE2E"), ["label", "JSON 写侧对拍"]).Single();
        Check(native.Timeout == TimeSpan.FromMinutes(75) && CaseSelection.TimeoutFor(native.Id) == native.Timeout,
            "JSON 写侧冷编译的有限截止必须同时适用于选择器与直接 ID");
        var ordinary = CaseSelection.Select(TestSuiteCatalog.GetNumber("NativeE2E"), ["0", "0"]).Single();
        Check(ordinary.Timeout == TimeSpan.FromMinutes(45) && CaseSelection.TimeoutFor(ordinary.Id) == ordinary.Timeout,
            "其他 Native 对拍不能继承 JSON 写侧的重型期限");
        var json = CaseSelection.ResourcesFor(native.Id);
        Check(json.MemoryMiB == 4096 && json.CpuSlots == 1 && json.ComputeWorkers == 1,
            "JSON 冷编译的 native 峰值须纳入预留，不能伪装为多线程 Compute 测试");
        Check(CaseSelection.ResourcesFor(ordinary.Id).MemoryMiB == 2048,
            "其他 Native 对拍保留原有内存 profile");
        foreach (var id in new[] { "native.hello-world-bil", "parser.add" }.Concat(
            CaseSelection.Select(TestSuiteCatalog.GetNumber("Module"), ["label", "E4.FullModuleLibraries"]).Select(task => task.Id)))
            Check(CaseSelection.TimeoutFor(id) == TimeSpan.FromMinutes(9), "轻例与定向组保留九分钟：" + id);
        foreach (var group in new[] { "WRAP-001", "WRAP-001-review" })
        {
            var tasks = CaseSelection.Select(TestSuiteCatalog.GetNumber("BilEmitter"), [group]);
            Check(tasks.Count > 1 && tasks.All(t => t.Timeout == null && t.Indices.Count == 1
                    && CaseSelection.TimeoutFor(t.Id) == TimeSpan.FromMinutes(9)),
                "BilEmitter 定向组不能继承完整整套期限：" + group);
        }
        Check(CaseSelection.TimeoutFor(CaseSelection.Select(TestSuiteCatalog.GetNumber("SemanticsFuzz"), ["0", "1"]).Single().Id) == Timeout.InfiniteTimeSpan,
            "直接 fuzz ID 同样保留默认不限时");
        foreach (var (suite, from, to) in new[] { ("E2e", "111", "114"), ("BilVmStress", "36", "39") })
        {
            var tasks = CaseSelection.Select(TestSuiteCatalog.GetNumber(suite), [from, to]);
            Check(tasks.Count == 4 && tasks.All(t => t.Indices.Count == 1 && t.Timeout == null
                && CaseSelection.TimeoutFor(t.Id) == TimeSpan.FromMinutes(9)),
                "完整编译/多轮 VM 各自执行而不提高单例截止：" + suite);
        }
    }
    [Test]
    public void NativeConcurrencyProfiles()
    {
        foreach (var label in new[] { "MQ 四生产者跨执行器广播与封存排空", "MQ OOP 复杂生命周期与分段回收", "mw13_shared_cycle_concurrent_release" })
        {
            var task = CaseSelection.Select(TestSuiteCatalog.GetNumber("NativeE2E"), ["label", label]).Single();
            var resources = CaseSelection.ResourcesFor(task.Id, nativeCpuSlots: null);
            Check(resources.ComputeWorkers == 4 && resources.CpuSlots == Math.Min(4, ResourceBudget.Shared.Capacity.CpuSlots), "MQ并发profile不可降为1");
            foreach (var slots in new[] { "1", "2", "3", "4" })
            {
                var configured = CaseSelection.ResourcesFor(task.Id, slots);
                Check(configured.CpuSlots == Math.Min(int.Parse(slots), ResourceBudget.Shared.Capacity.CpuSlots), "Native外层CPU权重须如实接受1..4");
                Check(configured.ComputeWorkers == 4, "吞吐选项不得降低Runtime Compute真实并发人数");
                Check(configured.MemoryMiB == resources.MemoryMiB && configured.LldThreads == resources.LldThreads
                    && configured.ExclusiveGroups.SequenceEqual(resources.ExclusiveGroups), "CPU权重选项不得改变内存/链接/互斥资源");
            }
            foreach (var invalid in new[] { "", "0", "5", "-1", "+1", " 1", "1 ", "1.0", "0x1", "4e0", "2147483648" })
            {
                try { CaseSelection.ResourcesFor(task.Id, invalid); throw new InvalidOperationException("非法Native CPU权重被接受：" + invalid); }
                catch (ArgumentException) { }
            }
            var singleCpu = CaseSelection.ResourcesFor(task.Id, "1");
            var budget = new ResourceBudget(new(1, singleCpu.MemoryMiB));
            using var lease = budget.AcquireAsync(singleCpu).AsTask().GetAwaiter().GetResult();
            var child = new ProcessStartInfo();
            lease.ApplyEnvironment(child);
            Check(child.Environment["RIGI_COMPUTE_WORKERS"] == "4" && child.Environment["RIGI_RESOURCE_LEASE_CPU_SLOTS"] == "1"
                && child.Environment["RIGI_JOBS"] == "1" && child.Environment["DOTNET_PROCESSOR_COUNT"] == "1",
                "子进程须真实保留Compute4并传递CPU1租约");
            Check(child.Environment["RIGI_RESOURCE_LEASE_MEMORY_MIB"] == resources.MemoryMiB.ToString()
                && child.Environment["DOTNET_GCHeapHardLimit"] == ((long)resources.MemoryMiB * 1024 * 1024 / 2).ToString("X"),
                "吞吐选项不得改变worker内存与GC限额");
        }
        foreach (var suite in new[] { "CompilerParallel", "BilVmTask", "VmFsIdentity" })
        {
            var task = CaseSelection.Select(TestSuiteCatalog.GetNumber(suite)).First();
            var original = CaseSelection.ResourcesFor(task.Id, nativeCpuSlots: null);
            var configured = CaseSelection.ResourcesFor(task.Id, "1");
            Check(original.CpuSlots == Math.Min(4, ResourceBudget.Shared.Capacity.CpuSlots)
                && configured.CpuSlots == original.CpuSlots && configured.ComputeWorkers == original.ComputeWorkers,
                "Native吞吐选项不得改动其它并发suite：" + suite);
        }
        var ordinary = CaseSelection.ResourcesFor("native.hello-world-bil", "4");
        Check(ordinary.CpuSlots == 1 && ordinary.ComputeWorkers == 1 && ordinary.MemoryMiB == 2048,
            "普通Native单核profile不受并发权重选项影响");
    }
    [Test]
    public async Task UnsupportedPlatformIsTypedSkip()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("Linux非junction平台专项");
        var task = CaseSelection.Select(TestSuiteCatalog.GetNumber("VmFsJunctionDelete"), ["0", "0"]).Single();
        var result = await new CaseWorkerClient().RunAsync(task.Id);
        Check(result.Status == CaseStatus.Skip && result.Assertions == 0 && result.SkipReason != null, "不支持的平台必须Skip，不是零断言绿或假失败");
    }
    [Test]
    public async Task CrossSuiteSparseExecution()
    {
        var tasks = new[] { "SemanticsFuzz", "StressFuzz" }.SelectMany(suite => CaseSelection.Select(
            TestSuiteCatalog.GetNumber(suite), ["indices", "0", "60", "121"])).ToArray();
        var outcomes = await CaseWorkers.RunTasksAsync(tasks);
        Check(outcomes.Count == tasks.Length && outcomes.All(outcome => outcome.Status == CaseStatus.Pass && outcome.Assertions == 3), "跨suite稀疏批次每个全局输入恰执行一次");
        foreach (var outcome in outcomes)
        {
            Check(outcome.Diagnostics.Contains("开始 case#0") && outcome.Diagnostics.Contains("开始 case#60")
                && outcome.Diagnostics.Contains("开始 case#121") && !outcome.Diagnostics.Contains("开始 case#1（"), "globalIndex进度应只含被选输入");
        }
    }
    [Test]
    public async Task ConcurrentCountsAreIsolated()
    {
        var worker = new CaseWorkerClient();
        var outcomes = await Task.WhenAll(worker.RunAsync("lexer.slash", probe: "fail-harness"), worker.RunAsync("parser.add"),
            worker.RunAsync("bil.reader.scalar-roundtrip"), worker.RunAsync("lexer.multiline-comment"));
        Check(outcomes[0].Status == CaseStatus.Fail && outcomes[0].Assertions == 2 && outcomes[0].Failures == 1, "失败计数不串扰");
        Check(outcomes.Skip(1).All(outcome => outcome.Status == CaseStatus.Pass && outcome.Failures == 0), "独立worker计数与输出不串扰");
    }
    [Test]
    public async Task ChildEnvironmentAndTempOwnership()
    {
        var result = await new CaseWorkerClient().RunAsync(CaseSelection.Select(TestSuiteCatalog.GetNumber("BilVmTask"), ["0", "0"]).Single().Id, probe: "environment");
        Check(result.Status == CaseStatus.Pass && result.Diagnostics.Contains("compute=4;") && result.Diagnostics.Contains("spawned=True;"), "真并发profile至少Compute4且spawned");
        var directory = result.Diagnostics.Split("tmp=", 2)[1].Trim();
        Check(!Directory.Exists(directory), "worker结束/排空后只清父请求临时根");
        var path = Path.Combine(Path.GetTempPath(), "rigi_execution_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            result.Write(path); var roundtrip = CaseOutcome.Read(path, result.CaseId);
            Check(roundtrip.Execution?.Lease.ComputeWorkers == 4 && roundtrip.Execution.ProcessId == result.Execution!.ProcessId,
                "JSON必须保留lease和真实执行采样");
        }
        finally { File.Delete(path); }
    }
    [Test]
    public async Task SpawnedRefusesNested()
    {
        var result = await new CaseWorkerClient().RunAsync("lexer.slash", probe: "nested");
        Check(result.Status == CaseStatus.Pass && result.Diagnostics.Contains("拒绝嵌套"), "隔离worker必须拒绝嵌套队列");
    }
}
