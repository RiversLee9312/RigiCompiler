using RigiCompiler.Tests;
using TUnit.Core;

namespace RigiCompiler.TUnitTests;

[Category("Protocol")]
public class WorkerContractTests
{
    private static readonly CaseWorkerClient Worker = new();

    [Test]
    [Category("CompilerBudget")]
    public async Task CompilerChildUsesGrantedBudget()
    {
        // 默认 E2e worker 为 512MiB 总 lease / 256MiB GC，真实跑 stdlib
        // 与两用户文件的完整编译，不能用 --spawned 直跑绕过 child 环境。
        var task = LegacyDispatcher.Select(TestRunner.GetSuiteNumber("E2e"), ["rich_return_nullable"]).Single();
        var result = await Worker.RunAsync(task.Id, timeout: TimeSpan.FromMinutes(2));
        Check(result.Status == CaseStatus.Pass && result.Execution?.Lease.MemoryMiB == 512,
            "compiler 必须消费已授予子预算：" + result.Diagnostics);
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    [Test]
    public async Task HarnessFailure()
    {
        var result = await Worker.RunAsync("lexer.slash", probe: "fail-harness");
        Check(result.Status == CaseStatus.Fail && result.Assertions == 2 && result.Failures == 1,
            "Harness 失败必须进入 typed Fail");
    }

    [Test]
    public async Task LexerOwnFailure()
    {
        var result = await Worker.RunAsync("lexer.slash", probe: "fail-lexer");
        Check(result.Status == CaseStatus.Fail && result.Assertions == 1 && result.Failures == 1,
            "Lexer 私有计数失败必须进入 typed Fail");
    }

    [Test]
    public async Task TypedSkip()
    {
        var result = await Worker.RunAsync("lexer.slash", probe: "skip");
        Check(result.Status == CaseStatus.Skip && result.SkipReason == "显式 Skip 协议探针", "Skip 理由必须保留");
    }

    [Test]
    public void ExplicitSkip() => Skip.Test("显式 Skip 协议探针");

    [Test]
    public async Task ExitWithoutResult()
    {
        var result = await Worker.RunAsync("lexer.slash", probe: "crash");
        Check(result.Status == CaseStatus.Fail && result.Diagnostics.Contains("17"), "异常退出不得假绿");
    }

    [Test]
    public async Task Timeout()
    {
        var result = await Worker.RunAsync("lexer.slash", timeout: TimeSpan.FromMilliseconds(300), probe: "delay");
        Check(result.Status == CaseStatus.Fail && result.Diagnostics.Contains("超时"), "超时必须失败并灭树");
    }

    [Test]
    public async Task ExplicitDeadlineOverridesHeavyDefault()
    {
        foreach (var id in new[] { "legacy/NativeE2E/557", "legacy/BilEmitter/suite" })
        {
            var result = await Worker.RunAsync(id, timeout: TimeSpan.FromMilliseconds(300), probe: "delay");
            Check(result.Status == CaseStatus.Fail && result.Diagnostics.Contains("超时")
                && result.Execution?.WallMilliseconds < 10_000,
                "重型默认截止不能覆盖调用方显式短期限，超时仍须灭树排空：" + id);
        }
    }

    [Test]
    public async Task ActiveCancellation()
    {
        using var cancellation = new CancellationTokenSource(300);
        var result = await Worker.RunAsync("lexer.slash", cancellation.Token, probe: "delay");
        Check(result.Status == CaseStatus.Cancel, "运行中的取消必须灭树并返回 Cancel");
    }

    [Test]
    public async Task PendingCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await Worker.RunAsync("lexer.slash", cancellation.Token);
        Check(result.Status == CaseStatus.Cancel, "开始前取消不得启动 worker");
    }

    [Test]
    public async Task EarlyRootExitWithInheritedPipe()
    {
        if (!OperatingSystem.IsLinux()) Skip.Test("Linux session/group 专项；Windows 原子 Job 由 Windows CI 覆盖");
        var root = Path.Combine(Path.GetTempPath(), "rigi_worker_contract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidPath = Path.Combine(root, "child.pid");
        try
        {
            var result = await Worker.RunAsync("lexer.slash", timeout: TimeSpan.FromSeconds(2), probe: "orphan-pipe:" + pidPath);
            Check(result.Status == CaseStatus.Fail && result.Diagnostics.Contains("超时"), "根早退后继承管道必须有界灭树排空");
            Check(File.Exists(pidPath), "后台后代必须实际启动");
            var pid = int.Parse(File.ReadAllText(pidPath).Trim());
            var statusPath = $"/proc/{pid}/status";
            Check(!File.Exists(statusPath) || File.ReadAllText(statusPath).Split('\n').Any(line => line.StartsWith("State:") && line.Contains('Z')),
                "worker 后台后代不得仍在执行");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task GrandchildTreeCleanup()
    {
        if (!OperatingSystem.IsLinux()) Skip.Test("Linux三层进程组专项");
        var root = Path.Combine(Path.GetTempPath(), "rigi_tree_contract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "child.pid");
        try
        {
            var outcome = await Worker.RunAsync("lexer.slash", timeout: TimeSpan.FromSeconds(2), probe: "grandchild-tree:" + path);
            Check(outcome.Status == CaseStatus.Fail && outcome.Diagnostics.Contains("超时"), "三层树超时必须失败");
            foreach (var pidPath in new[] { path, path + ".grandchild" })
            {
                Check(File.Exists(pidPath), "子/孙进程必须真实启动");
                int pid = int.Parse(File.ReadAllText(pidPath).Trim());
                var stat = $"/proc/{pid}/stat";
                Check(!File.Exists(stat) || File.ReadAllText(stat).Split(' ')[2] == "Z", "子/孙进程不应继续执行");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task UnknownId()
    {
        var result = await Worker.RunAsync("unknown.case");
        Check(result.Status == CaseStatus.Fail && result.Diagnostics.Contains("未知"), "未知 ID 不得回退全套");
    }
}
