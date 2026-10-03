using System.Diagnostics;
using RigiCompiler.PerfBaseline;

namespace RigiCompiler.Modules;

public sealed record ModuleProcessResult(int ExitCode, string Stdout, string Stderr);

public static class ModuleHooks
{
    public static async Task<IReadOnlyList<ModuleProcessResult>> RunAsync(ModuleBuildContext context,
        ModuleHookPhase phase, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var results = new List<ModuleProcessResult>();
        foreach (var hook in context.Hooks(phase))
        {
            var info = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                WorkingDirectory = context.Module.Root, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            // cmd /c 解析原始命令文本，不使用 CRT argv 的反斜杠引号编码。
            if (OperatingSystem.IsWindows()) info.Arguments = "/d /s /c \"" + hook.Command + "\"";
            else { info.ArgumentList.Add("-c"); info.ArgumentList.Add(hook.Command); }
            foreach (var pair in context.ChildEnvironment()) info.Environment[pair.Key] = pair.Value;
            var result = await RunProcessAsync(info, timeout ?? TimeSpan.FromMinutes(10), cancellationToken);
            results.Add(result);
            if (result.ExitCode != 0) throw new ModuleConfigurationException(
                $"module {context.Module.ModuleId}: hook {phase} 失败（exit {result.ExitCode}）\n{result.Stderr}");
        }
        return results;
    }

    internal static async Task<ModuleProcessResult> RunProcessAsync(ProcessStartInfo info,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var isolation = new ProcessIsolation();
        using var process = isolation.Start(info);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // 根等待与双路 drain 共用截止；正常退出同样结束本次残留后代。
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            isolation.Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new ModuleConfigurationException($"模块子进程超时（{timeout.TotalSeconds}s），进程树已终止");
        }
    }
}
