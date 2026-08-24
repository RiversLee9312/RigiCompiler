using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// 通用外部进程封装：UseShellExecute=false + 重定向 stdout/stderr，
    /// 双路异步读防死锁（缓冲填满互等），不设超时（工具链调用可能慢）。
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
            out string stdout, out string stderr, string? workingDirectory = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            if (workingDirectory != null)
            {
                startInfo.WorkingDirectory = workingDirectory;
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
                // 先开双路异步读再等退出：同步顺序读会因子进程缓冲填满而互等死锁
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                stdout = stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
                return process.ExitCode;
            }
        }
    }
}
