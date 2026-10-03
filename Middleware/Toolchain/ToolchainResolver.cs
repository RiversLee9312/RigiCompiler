using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RigiCompiler.Middleware.Toolchain
{
    /// <summary>
    /// C 工具链（clang）解析（MIDDLEWARE_ARCHITECTURE §2 定稿的解析顺序）：
    /// ① 调用方给的 --toolchain 目录；② 环境变量 RIGI_LLVM；
    /// ③ 仓库缓存 tools/.llvm/&lt;rid&gt;/；④ PATH 逐目录查 clang 存在性。
    /// 前三个来源的目录布局统一为 &lt;dir&gt;/bin/clang(.exe)。
    /// </summary>
    public static class ToolchainResolver
    {
        // 用户指定的工具链是显式信任输入。允许部署方钉住摘要；无钉值时
        // 仍记录实际内容身份，不能把“文件存在”表述为已验证发行来源。
        public static string Fingerprint(string path, string? expectedSha256 = null)
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.IsNullOrEmpty(expectedSha256)
                && !StringComparer.OrdinalIgnoreCase.Equals(actual, expectedSha256.Trim()))
                throw new InvalidOperationException($"工具链 SHA256 不匹配：{path}");
            return actual;
        }

        // 缓存目录识别用 RID：Windows→win-x64，Linux→linux-x64
        private static string Rid =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";

        // clang 可执行文件名（Windows 带 .exe）
        private static string ClangExeName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "clang.exe" : "clang";

        /// <summary>
        /// 按 §2 顺序解析 clang 全路径；返回 null = 全部来源均未找到。
        /// </summary>
        /// <param name="toolchainDir">调用方 --toolchain 目录（可 null）。</param>
        public static string? ResolveClang(string? toolchainDir)
        {
            // ① 调用方给的 --toolchain 目录：<dir>/bin/clang(.exe)
            if (!string.IsNullOrEmpty(toolchainDir))
            {
                var candidate = Path.Combine(toolchainDir, "bin", ClangExeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // ② 环境变量 RIGI_LLVM：同样按 <dir>/bin/ 查
            var rigiLlvm = Environment.GetEnvironmentVariable("RIGI_LLVM");
            if (!string.IsNullOrEmpty(rigiLlvm))
            {
                var candidate = Path.Combine(rigiLlvm, "bin", ClangExeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // ③ 仓库缓存 tools/.llvm/<rid>/bin/clang：从 AppContext.BaseDirectory
            //    向上最多 6 级找含该路径的目录（AOT 单文件发布下靠 .git/csproj
            //    定位仓库根不可靠，改为直接探测目标路径）；找不到就跳过该来源
            //    （CI/发布环境走 PATH 或预装）
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth <= 6 && dir != null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", ".llvm", Rid, "bin", ClangExeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // ④ PATH 逐目录查 clang 存在性（where/which 语义：不试启动，只查文件）
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var entry in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(entry))
                    {
                        continue;
                    }
                    var candidate = Path.Combine(entry.Trim(), ClangExeName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        internal static string? ResolveArchiver(string? toolchainDir)
        {
            var explicitPath = Environment.GetEnvironmentVariable("RIGI_AR");
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
            var names = OperatingSystem.IsWindows() ? new[] { "llvm-lib.exe", "lib.exe", "llvm-ar.exe", "ar.exe" }
                : new[] { "llvm-ar", "ar" };
            var directories = new System.Collections.Generic.List<string>();
            foreach (var root in new[] { toolchainDir, Environment.GetEnvironmentVariable("RIGI_LLVM") })
                if (!string.IsNullOrEmpty(root)) directories.Add(Path.Combine(root, "bin"));
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth <= 6 && dir != null; depth++, dir = dir.Parent)
                directories.Add(Path.Combine(dir.FullName, "tools", ".llvm", Rid, "bin"));
            directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => !string.IsNullOrWhiteSpace(d)));
            foreach (var directory in directories)
                foreach (var name in names)
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
            return null;
        }

        /// <summary>
        /// 返回人类可读的解析顺序说明（供错误消息）。
        /// </summary>
        public static string DescribeSearchOrder()
        {
            return
                "clang 解析顺序（MIDDLEWARE_ARCHITECTURE §2）：\n" +
                "  ① --toolchain <dir>：       <dir>/bin/clang(.exe)\n" +
                "  ② 环境变量 RIGI_LLVM：      $RIGI_LLVM/bin/clang(.exe)\n" +
                $"  ③ 仓库缓存：               <仓库根>/tools/.llvm/{Rid}/bin/clang(.exe)\n" +
                "  ④ PATH：                   逐目录查 clang(.exe) 存在性\n" +
                "全部未命中时请先运行 tools/Fetch-LlvmToolchain.ps1 获取工具链。";
        }
    }
}
