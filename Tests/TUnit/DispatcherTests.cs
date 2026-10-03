using RigiCompiler.Tests;
using RigiCompiler;
using TUnit.Core;

namespace RigiCompiler.TUnitTests;

[Category("Dispatcher")]
public class DispatcherTests
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
            try { LegacyDispatcher.Select(TestRunner.GetSuiteNumber(suite), args); throw new InvalidOperationException("未知选择被接受：" + suite); }
            catch (ArgumentException) { }
        }
    }
    [Test]
    public void SparseAndHonestGranularity()
    {
        var sparse = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("SemanticsFuzz"), ["indices", "121", "0", "60", "60"]);
        Check(sparse.SelectMany(task => task.Indices).SequenceEqual(new[] { 0, 60, 121 }), "稀疏索引必须去重排序而不扩为连续区间");
        Check(sparse.Single().Granularity == "seed-batch", "fuzz 如实报告批次");
        Check(sparse.Single().Timeout == Timeout.InfiniteTimeSpan, "fuzz 默认保持不限时");
        var timed = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("SemanticsFuzz"), ["0", "1", "child-timeout-ms=123"]);
        Check(timed.Single().Timeout == TimeSpan.FromMilliseconds(123), "显式fuzz截止保留");
        var monolith = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("LexerFuzz"));
        Check(monolith.Single().Indices.Count == 0 && monolith.Single().Granularity == "suite-exit", "Lexer6000未拆分不得伪造case");
        var nativeDefault = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("NativeE2E"));
        Check(nativeDefault.All(task => task.Indices.Count == 1), "native重例逐case");
    }
    [Test]
    public void ModuleHeavyDeadlineBoundaries()
    {
        var module = TestRunner.GetSuiteNumber("Module");
        var native = LegacyDispatcher.Select(module, ["24", "27"]);
        Check(native.Count == 4 && native.All(t => t.Indices.Count == 1 && t.Granularity == "case" && t.Timeout == null),
            "真实 Native 模块各自使用 worker 默认截止，不合批也不抬期限");
        var numeric = LegacyDispatcher.Select(module, ["0", "7"]);
        Check(numeric.Select(t => string.Join(',', t.Indices)).SequenceEqual(new[] { "0,1,2,3", "4", "5", "6", "7" }),
            "轻批在重型 case 前后切开并保持顺序");
        var named = LegacyDispatcher.Select(module, ["label", "A1.StrictConfiguration", "A2.TypedResources", "A2.OriginCompilation", "E4.FullModuleLibraries"]);
        Check(named.Select(t => t.Id).SequenceEqual(new[] { "legacy/Module/0,4", "legacy/Module/5", "legacy/Module/27" }),
            "精确标签与数值选择共用边界，重型 case 不夹入轻批");
        var reader = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("BilReader"), ["0", "3"]);
        Check(reader.Count == 1 && reader[0].Indices.SequenceEqual(new[] { 0, 1, 2, 3 }), "旧轻量 suite 的小批契约保留");
    }
    [Test]
    public void HeavyAndLightDeadlines()
    {
        foreach (var (suite, minutes) in new[] { ("Binder", 60), ("BilEmitter", 75), ("Lowerer", 30) })
        {
            var task = LegacyDispatcher.Select(TestRunner.GetSuiteNumber(suite)).Single();
            Check(task.Timeout == TimeSpan.FromMinutes(minutes) && LegacyDispatcher.TimeoutFor(task.Id) == task.Timeout,
                "完整编译整组与直接 ID 必须共用有限重型截止：" + suite);
        }
        var native = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("NativeE2E"), ["label", "JSON 写侧对拍"]).Single();
        Check(native.Timeout == TimeSpan.FromMinutes(45) && LegacyDispatcher.TimeoutFor(native.Id) == native.Timeout,
            "whole-program O2 对拍与直接 ID 必须共用截止");
        foreach (var id in new[] { "native.hello-world-bil", "parser.add", "legacy/Binder/suite/WRAP-001",
            "legacy/BilEmitter/suite/WRAP-001", "legacy/BilEmitter/suite/WRAP-001-review", "legacy/Module/27" })
            Check(LegacyDispatcher.TimeoutFor(id) == TimeSpan.FromMinutes(9), "轻例与定向组保留九分钟：" + id);
        foreach (var group in new[] { "WRAP-001", "WRAP-001-review" })
        {
            var task = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("BilEmitter"), [group]).Single();
            Check(task.Timeout == null && LegacyDispatcher.TimeoutFor(task.Id) == TimeSpan.FromMinutes(9),
                "BilEmitter 定向组不能继承完整整套期限：" + group);
        }
        Check(LegacyDispatcher.TimeoutFor("legacy/SemanticsFuzz/0,1") == Timeout.InfiniteTimeSpan,
            "直接 fuzz ID 同样保留默认不限时");
        foreach (var (suite, from, to) in new[] { ("E2e", "111", "114"), ("BilVmStress", "36", "39") })
        {
            var tasks = LegacyDispatcher.Select(TestRunner.GetSuiteNumber(suite), [from, to]);
            Check(tasks.Count == 4 && tasks.All(t => t.Indices.Count == 1 && t.Timeout == null
                && LegacyDispatcher.TimeoutFor(t.Id) == TimeSpan.FromMinutes(9)),
                "完整编译/多轮 VM 各自执行而不提高单例截止：" + suite);
        }
    }
    [Test]
    public void NativeConcurrencyProfiles()
    {
        foreach (var label in new[] { "MQ 四生产者跨执行器广播与封存排空", "MQ OOP 复杂生命周期与分段回收", "mw13_shared_cycle_concurrent_release" })
        {
            var task = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("NativeE2E"), ["label", label]).Single();
            var resources = LegacyDispatcher.ResourcesFor(task.Id);
            Check(resources.ComputeWorkers == 4 && resources.CpuSlots == Math.Min(4, ResourceBudget.Shared.Capacity.CpuSlots), "MQ并发profile不可降为1");
        }
    }
    [Test]
    public async Task UnsupportedPlatformIsTypedSkip()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("Linux非junction平台专项");
        var task = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("VmFsJunctionDelete")).Single();
        var result = await new CaseWorkerClient().RunAsync(task.Id);
        Check(result.Status == CaseStatus.Skip && result.Assertions == 0 && result.SkipReason != null, "不支持的平台必须Skip，不是零断言绿或假失败");
    }
    [Test]
    public async Task CrossSuiteSparseExecution()
    {
        var tasks = new[] { "SemanticsFuzz", "StressFuzz" }.SelectMany(suite => LegacyDispatcher.Select(
            TestRunner.GetSuiteNumber(suite), ["indices", "0", "60", "121"])).ToArray();
        var outcomes = await LegacyDispatcher.RunTasksAsync(tasks);
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
        var result = await new CaseWorkerClient().RunAsync("legacy/BilVmTask/0", probe: "environment");
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
        Check(result.Status == CaseStatus.Pass && result.Diagnostics.Contains("拒绝嵌套"), "spawned必须拒绝嵌套dispatcher");
    }
}
