using RigiCompiler;
using RigiCompiler.Tests;
using System.Text.Json;
using TUnit.Core;

namespace RigiCompiler.TUnitTests;

[Category("Dispatcher")]
public class LegacyParallelTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    [Test]
    public void EverySuiteHasEnumerableCoverage()
    {
        using var output = new MemoryStream();
        TestInventory.Write(output);
        using var manifest = JsonDocument.Parse(output.ToArray());
        foreach (var suite in manifest.RootElement.GetProperty("suites").EnumerateArray())
        {
            var name = suite.GetProperty("name").GetString()!;
            var inventory = suite.GetProperty("cases").EnumerateArray().ToArray();
            Check(inventory.Length > 0 && inventory.Select(c => c.GetProperty("label").GetString()).Distinct().Count() == inventory.Length,
                "目录不能漏套件、空覆盖或重复标签：" + name);
            var tasks = LegacyDispatcher.Select(TestRunner.GetSuiteNumber(name));
            var expected = inventory.Where(c => !c.GetProperty("slow").GetBoolean() || c.GetProperty("gateEnabled").GetBoolean())
                .Select(c => c.GetProperty("index").GetInt32()).ToArray();
            Check(tasks.All(t => t.Indices.Count > 0) && tasks.SelectMany(t => t.Indices).SequenceEqual(expected),
                "默认队列须恰好覆盖全部非门控输入，不能回退整套 worker：" + name);
        }
    }

    [Test]
    public async Task MigratedWorkersReallyOverlap()
    {
        if (ResourceBudget.Shared.Capacity.CpuSlots < 2) Skip.Test("真实 worker 会合需要两个 CPU slots");
        // 先在共同预算中保留整对资源，再在这两个槽内派发，避免无关大请求
        // 排在两个会合者之间造成 FIFO 死锁；不能绕过父进程物理资源计费。
        await using var reserved = await ResourceBudget.Shared.AcquireAsync(new(2, 1024));
        var directory = Path.Combine(Path.GetTempPath(), "rigi-peer-barrier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tasks = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("Literal"), ["0", "1"]);
            Check(tasks.Count == 2, "两个测试方法须有独立 worker 身份");
            var worker = new CaseWorkerClient(new BoundedCaseWorkerLeases(2));
            var results = await Task.WhenAll(tasks.Select(t => worker.RunAsync(t.Id,
                timeout: TimeSpan.FromSeconds(45), probe: "peer-barrier:" + directory)));
            Check(results.All(r => r.Status == CaseStatus.Pass && r.Assertions > 0),
                string.Join('\n', results.Select(r => r.Diagnostics)));
            Check(results.Select(r => r.Execution!.ProcessId).Distinct().Count() == 2
                && Directory.GetFiles(directory, "*.ready").Length == 2,
                "须由两个真实独立进程同时会合，不得只用时间阈值冒称并行");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task PrivateCountersRemainRealAssertions()
    {
        var selected = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("CommandLineParser"), ["label", "TestRegistryIntegrity"])
            .Concat(LegacyDispatcher.Select(TestRunner.GetSuiteNumber("LexerFuzz"), ["5", "6"])).ToArray();
        var outcomes = await LegacyDispatcher.RunTasksAsync(selected);
        Check(outcomes.All(o => o.Status == CaseStatus.Pass), string.Join('\n', outcomes.Select(o => o.Diagnostics)));
        Check(outcomes[0].Assertions > 5 && outcomes[1].Assertions == 2,
            "旧私有计数器与两个全局 fuzz 输入须保留实际断言数，不能变成退出码计数");
    }
}
