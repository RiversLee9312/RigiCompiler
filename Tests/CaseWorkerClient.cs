using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using RigiCompiler.PerfBaseline;

namespace RigiCompiler.Tests;

/// <summary>所有 TUnit 发现行共享同一父进程预算。</summary>
public interface ICaseWorkerLeaseProvider
{
    ValueTask<ResourceLease> AcquireAsync(CaseDescriptor descriptor, CancellationToken cancellationToken);
}

public sealed class BoundedCaseWorkerLeases : ICaseWorkerLeaseProvider
{
    private readonly ResourceBudget budget;
    public BoundedCaseWorkerLeases(int? capacity = null)
    {
        if (capacity is { } count && (count < 1 || count > ResourceBudget.Shared.Capacity.CpuSlots))
            throw new ArgumentOutOfRangeException(nameof(capacity));
        budget = capacity is { } slots ? new ResourceBudget(ResourceBudget.Shared.Capacity with { CpuSlots = slots }) : ResourceBudget.Shared;
    }
    public ValueTask<ResourceLease> AcquireAsync(CaseDescriptor descriptor, CancellationToken cancellationToken)
    {
        var request = CaseSelection.Resources(descriptor);
        return budget.AcquireAsync(request with { CpuSlots = Math.Min(budget.Capacity.CpuSlots, request.CpuSlots) }, cancellationToken);
    }
}

/// <summary>每 case 进程隔离：父进程拥有临时根、结果文件、灭树与排空责任。</summary>
public sealed class CaseWorkerClient(ICaseWorkerLeaseProvider? leases = null)
{
    private static readonly BoundedCaseWorkerLeases DefaultLeases = new();
    private readonly ICaseWorkerLeaseProvider leases = leases ?? DefaultLeases;

    public async Task<CaseOutcome> RunAsync(string caseId, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null, string? probe = null)
    {
        var descriptor = CaseCatalog.Find(caseId);
        if (descriptor == null) return Failure(caseId, "未知 case ID");
        try
        {
            await using var lease = await leases.AcquireAsync(descriptor, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await RunIsolatedAsync(caseId, cancellationToken, timeout ?? CaseSelection.TimeoutFor(caseId), probe, lease);
        }
        catch (OperationCanceledException) { return Cancelled(caseId); }
        catch (Exception ex) { return Failure(caseId, ex.ToString()); }
    }

    private static CaseOutcome Failure(string id, string diagnostic) => new(id, CaseStatus.Fail, 1, 1, null, diagnostic);
    private static CaseOutcome Cancelled(string id) => new(id, CaseStatus.Cancel, 0, 0, null, "测试已取消，worker 树已终止并排空");

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "AOT worker 由当前测试宿主路径定位，编译器由独立变量定位")]
    private static ProcessStartInfo WorkerStart(string caseId, string resultPath, string root, string? probe)
    {
        var artifact = Environment.GetEnvironmentVariable("RIGI_TEST_HOST");
        if (string.IsNullOrWhiteSpace(artifact)) artifact = TestArtifacts.Host;
        if (string.IsNullOrWhiteSpace(artifact))
            throw new InvalidOperationException("无法定位隔离测试宿主");
        artifact = Path.GetFullPath(artifact);
        if (!File.Exists(artifact)) throw new FileNotFoundException("未找到测试 worker 宿主", artifact);
        var info = new ProcessStartInfo
        {
            FileName = artifact, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = root,
        };
        if (artifact.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var runtimeConfig = Path.ChangeExtension(artifact, ".runtimeconfig.json");
            var dependencies = Path.ChangeExtension(artifact, ".deps.json");
            if (!File.Exists(runtimeConfig) || !File.Exists(dependencies))
                throw new FileNotFoundException("测试宿主 DLL 需要同目录 runtimeconfig/deps");
            info.FileName = "dotnet";
            foreach (var argument in new[] { "exec", "--runtimeconfig", runtimeConfig, "--depsfile", dependencies, artifact })
                info.ArgumentList.Add(argument);
        }
        foreach (var argument in new[] { "--isolated-worker", "--case-id", caseId, "--result-file", resultPath })
            info.ArgumentList.Add(argument);
        // 子 fixtures 与 LLVM 临时目录都从请求根派生，不改变 HOME 或用户 cache。
        foreach (var variable in new[] { "TMPDIR", "TEMP", "TMP" }) info.Environment[variable] = root;
        if (probe != null) info.Environment["RIGI_TEST_PROBE"] = probe;
        return info;
    }

    private static void Terminate(ProcessIsolation isolation, ContainedProcess process)
    {
        isolation.Kill(process);
        // Attach 失败或 setsid 尚未建组时，空 job/group 无法灭根；仅兜底本次创建的进程。
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
    }

    private static async Task<CaseOutcome> RunIsolatedAsync(string id, CancellationToken cancellationToken,
        TimeSpan timeout, string? probe, ResourceLease lease)
    {
        var root = Path.Combine(Path.GetTempPath(), "rigi_test_worker_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resultPath = Path.Combine(root, "result.json");
            using var isolation = new ProcessIsolation();
            var info = WorkerStart(id, resultPath, root, probe);
            lease.ApplyEnvironment(info);
            var clock = Stopwatch.StartNew();
            using var process = isolation.Start(info);
            var sample = new ProcessTreeSampler(process.Id);
            CaseOutcome Measured(CaseOutcome outcome) => outcome with {
                Execution = new(process.Id, clock.Elapsed.TotalMilliseconds, OperatingSystem.IsLinux() ? sample.CpuMilliseconds : null, OperatingSystem.IsLinux() ? sample.PeakRssBytes : null, 100, lease.Request) };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            Task<string>? stdout = null, stderr = null;
            try
            {
                process.StandardInput.Close();
                stdout = process.StandardOutput.ReadToEndAsync();
                stderr = process.StandardError.ReadToEndAsync();
                // 根进程早退时管道仍可能由孙进程持有；同一个截止 token 管根等待与排空。
                var rootWait = process.WaitForExitAsync(deadline.Token);
                while (!rootWait.IsCompleted)
                {
                    sample.Capture();
                    await Task.WhenAny(rootWait, Task.Delay(100, deadline.Token));
                }
                await rootWait;
                sample.Capture();
                await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
                var output = await stdout + await stderr;
                if (!File.Exists(resultPath)) return Measured(Failure(id, $"worker 退出 {process.ExitCode}，缺少结果\n{output}"));
                var outcome = CaseOutcome.Read(resultPath, id);
                var expectedExit = outcome.Status switch { CaseStatus.Pass or CaseStatus.Skip => 0, CaseStatus.Cancel => 130, _ => 1 };
                if (process.ExitCode != expectedExit)
                    return Measured(Failure(id, $"worker 状态与退出码矛盾：{outcome.Status}/{process.ExitCode}\n{outcome.Diagnostics}\n{output}"));
                if (CaseCatalog.Find(id)?.Suite == "NativeE2E" && (outcome.Diagnostics + output)
                    .Contains("warning: linking two modules of different", StringComparison.OrdinalIgnoreCase))
                    return Measured(Failure(id, "LLVM 模块目标失配警告\n" + outcome.Diagnostics + output));
                return Measured(outcome with { Diagnostics = outcome.Diagnostics + output });
            }
            catch (OperationCanceledException)
            {
                Terminate(isolation, process);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (stdout != null && stderr != null) await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
                return Measured(cancellationToken.IsCancellationRequested ? Cancelled(id) : Failure(id, "worker 超时，进程树已终止并排空"));
            }
            finally
            {
                // 成功或根提前退出也清理同组后代，排空后才允许删除请求根。
                Terminate(isolation, process);
                if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (stdout != null && stderr != null) await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
