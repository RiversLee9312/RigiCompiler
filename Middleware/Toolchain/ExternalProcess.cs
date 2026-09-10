using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler.Middleware.Toolchain
{
    /// <summary>
    /// 通用外部进程封装：UseShellExecute=false + 重定向 stdout/stderr，
    /// 双路异步读防死锁（缓冲填满互等），默认十分钟超时并终止子进程树。
    /// stderr 不吞——内容原样返回给调用方记录。
    /// </summary>
    public static class ExternalProcess
    {
        /// <summary>
        /// 启动进程并等其退出，返回退出码。
        /// 进程启动失败（文件不存在等）抛 <see cref="InvalidOperationException"/>，
        /// 带 exe 路径与建议（跑 tools/Fetch-LlvmToolchain.ps1）。
        /// </summary>
        public static int Run(string exe, IReadOnlyList<string> args,
            out string stdout, out string stderr, string? workingDirectory = null,
            IReadOnlyDictionary<string, string>? environment = null,
            int timeoutMilliseconds = 600_000)
        {
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // 子进程（clang/lld、rigi 产物）一律 UTF-8 交互：不设时 .NET 回退
                // 控制台代码页（Windows en-US 为 CP437），中文 stderr 会解码成乱码
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            if (workingDirectory != null)
            {
                startInfo.WorkingDirectory = workingDirectory;
            }
            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Process.Start 返回 null");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                or FileNotFoundException)
            {
                throw new InvalidOperationException(
                    $"无法启动外部进程 {exe}（文件不存在或不可执行）：{ex.Message}。" +
                    "若为 C 工具链缺失，请先运行 tools/Fetch-LlvmToolchain.ps1", ex);
            }

            using (process)
            {
                var elapsed = Stopwatch.StartNew();
                // 先开双路异步读再等退出：同步顺序读会因子进程缓冲填满而互等死锁
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds)
                    || !Task.WhenAll(stdoutTask, stderrTask).Wait(
                        (int)Math.Max(0, timeoutMilliseconds - elapsed.ElapsedMilliseconds)))
                {
                    // 只终止本次创建的进程树；不能让挂死的链接器/产物继续占资源。
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* 与自然退出竞争。 */ }
                    process.WaitForExit(5_000);
                    throw new InvalidOperationException(
                        $"外部进程 {exe} 超过 {timeoutMilliseconds}ms，已请求终止进程树");
                }
                stdout = stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
                return process.ExitCode;
            }
        }
    }
}
