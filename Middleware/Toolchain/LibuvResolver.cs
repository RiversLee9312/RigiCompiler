using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RigiCompiler.Middleware.Toolchain
{
    /// <summary>
    /// libuv 静态库解析结果（include 目录 + 静态库全路径）。
    /// </summary>
    public sealed class LibuvLayout
    {
        /// <summary>libuv 公开头文件目录（含 uv.h 及 uv/ 子头）。</summary>
        public string IncludeDir { get; }
        /// <summary>静态库全路径（win = uv_a.lib；linux = libuv_a.a）。</summary>
        public string StaticLibPath { get; }

        public LibuvLayout(string includeDir, string staticLibPath)
        {
            IncludeDir = includeDir;
            StaticLibPath = staticLibPath;
        }
    }

    /// <summary>
    /// libuv 静态库解析（MIDDLEWARE_ARCHITECTURE §2 MW11b 定稿的解析顺序，
    /// 模仿 ToolchainResolver）：
    /// ① 调用方给的 --libuv-dir 目录；② 环境变量 RIGI_LIBUV；
    /// ③ 仓库缓存 tools/.libuv/&lt;rid&gt;/（tools/Fetch-Libuv.ps1 产出）；
    /// ④ 编译器 exe 旁 .libuv/&lt;rid&gt;/（发布形态）。
    /// 前三个来源的目录布局统一为 &lt;dir&gt;/include/uv.h + &lt;dir&gt;/lib/&lt;静态库&gt;。
    /// 返回 null = 全部来源均未找到（native --out 按现状降级链接，
    /// Alarm 面届时 abort——见 §2 降级约定）。
    /// </summary>
    public static class LibuvResolver
    {
        // 缓存目录识别用 RID：Windows→win-x64，Linux→linux-x64
        private static string Rid =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";

        // 静态库文件名（tools/Fetch-Libuv.ps1 缓存布局契约）
        private static string StaticLibName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "uv_a.lib" : "libuv_a.a";

        /// <summary>
        /// 按解析顺序查找 libuv 布局；返回 null = 全部来源均未找到。
        /// </summary>
        /// <param name="libuvDir">调用方 --libuv-dir 目录（可 null）。</param>
        public static LibuvLayout? Resolve(string? libuvDir)
        {
            // ① 调用方给的 --libuv-dir 目录
            if (!string.IsNullOrEmpty(libuvDir) && TryLayout(libuvDir, out var fromArg))
            {
                return fromArg;
            }

            // ② 环境变量 RIGI_LIBUV：同样的目录布局
            var rigiLibuv = Environment.GetEnvironmentVariable("RIGI_LIBUV");
            if (!string.IsNullOrEmpty(rigiLibuv) && TryLayout(rigiLibuv, out var fromEnv))
            {
                return fromEnv;
            }

            // ③ 仓库缓存 tools/.libuv/<rid>/：从 AppContext.BaseDirectory 向上最多
            //    6 级直接探测目标路径（同 ToolchainResolver ③ 的纪律）
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth <= 6 && dir != null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", ".libuv", Rid);
                if (TryLayout(candidate, out var fromCache))
                {
                    return fromCache;
                }
            }

            // ④ 编译器 exe 旁 .libuv/<rid>/（发布形态：publish 目录随带）
            var besideExe = Path.Combine(AppContext.BaseDirectory, ".libuv", Rid);
            if (TryLayout(besideExe, out var fromPublish))
            {
                return fromPublish;
            }

            return null;
        }

        // 校验 <dir>/include/uv.h + <dir>/lib/<静态库> 齐备则采纳
        private static bool TryLayout(string dir, out LibuvLayout? layout)
        {
            var includeDir = Path.Combine(dir, "include");
            var staticLib = Path.Combine(dir, "lib", StaticLibName);
            if (File.Exists(Path.Combine(includeDir, "uv.h")) && File.Exists(staticLib))
            {
                layout = new LibuvLayout(includeDir, staticLib);
                return true;
            }
            layout = null;
            return false;
        }

        /// <summary>
        /// 平台系统库链接参数（静态链接 libuv 需追加）：
        /// win 九个（psapi user32 advapi32 iphlpapi userenv ws2_32 dbghelp ole32
        /// shell32，lld-link 语义 = -l&lt;名&gt;）；linux = pthread + dl（现代 glibc
        /// 免 dl/rt，兼容起见带上 -ldl）。
        /// </summary>
        public static string[] SystemLibraryArgs()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new[]
                {
                    "-lpsapi", "-luser32", "-ladvapi32", "-liphlpapi", "-luserenv",
                    "-lws2_32", "-ldbghelp", "-lole32", "-lshell32",
                };
            }
            return new[] { "-lpthread", "-ldl" };
        }

        /// <summary>
        /// 返回人类可读的解析顺序说明（供 verbose/错误消息）。
        /// </summary>
        public static string DescribeSearchOrder()
        {
            return
                "libuv 解析顺序（MIDDLEWARE_ARCHITECTURE §2）：\n" +
                "  ① --libuv-dir <dir>：     <dir>/include/uv.h + <dir>/lib/" + StaticLibName + "\n" +
                "  ② 环境变量 RIGI_LIBUV：   $RIGI_LIBUV/include/uv.h + $RIGI_LIBUV/lib/" + StaticLibName + "\n" +
                $"  ③ 仓库缓存：             <仓库根>/tools/.libuv/{Rid}/（tools/Fetch-Libuv.ps1 产出）\n" +
                $"  ④ 编译器 exe 旁：        <exe 目录>/.libuv/{Rid}/（发布形态）\n" +
                "全部未命中时按无 libuv 降级链接；请先运行 tools/Fetch-Libuv.ps1 获取。";
        }
    }
}
