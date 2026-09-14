using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RigiCompiler.Middleware.Toolchain
{
    /// <summary>
    /// mimalloc 静态库解析结果（静态库全路径；rigi_rt 侧只经 extern 声明引用
    /// mi_* 符号，include 目录仅为布局契约与将来使用而保留）。
    /// </summary>
    public sealed class MimallocLayout
    {
        /// <summary>静态库全路径（win = mimalloc.lib；linux = libmimalloc.a）。</summary>
        public string StaticLibPath { get; }

        public MimallocLayout(string staticLibPath)
        {
            StaticLibPath = staticLibPath;
        }
    }

    /// <summary>
    /// mimalloc 静态库解析（GC Phase 2，解析顺序模仿 LibuvResolver）：
    /// ① 调用方给的 --mimalloc-dir 目录；② 环境变量 RIGI_MIMALLOC；
    /// ③ 仓库缓存 tools/.mimalloc/&lt;rid&gt;/（tools/Fetch-Mimalloc.ps1 产出）；
    /// ④ 编译器 exe 旁 .mimalloc/&lt;rid&gt;/（发布形态）。
    /// 前三个来源的目录布局统一为 &lt;dir&gt;/include/mimalloc.h + &lt;dir&gt;/lib/&lt;静态库&gt;。
    /// 返回 null = 全部来源均未找到（native --out 编译期明确拒绝——track 台账
    /// 是 rigi_rt 对象分配唯一收口，缺 mimalloc 符号必然链接失败，响亮早失败）。
    /// </summary>
    public static class MimallocResolver
    {
        // 缓存目录识别用 RID：Windows→win-x64，Linux→linux-x64
        private static string Rid =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";

        // 静态库文件名（tools/Fetch-Mimalloc.ps1 缓存布局契约；官方预编译包
        // 内 release 变体，非 debug/secure）
        private static string StaticLibName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mimalloc.lib" : "libmimalloc.a";

        /// <summary>
        /// 按解析顺序查找 mimalloc 布局；返回 null = 全部来源均未找到。
        /// </summary>
        /// <param name="mimallocDir">调用方 --mimalloc-dir 目录（可 null）。</param>
        public static MimallocLayout? Resolve(string? mimallocDir)
        {
            // ① 调用方给的 --mimalloc-dir 目录
            if (!string.IsNullOrEmpty(mimallocDir) && TryLayout(mimallocDir, out var fromArg))
            {
                return fromArg;
            }

            // ② 环境变量 RIGI_MIMALLOC：同样的目录布局
            var rigiMimalloc = Environment.GetEnvironmentVariable("RIGI_MIMALLOC");
            if (!string.IsNullOrEmpty(rigiMimalloc) && TryLayout(rigiMimalloc, out var fromEnv))
            {
                return fromEnv;
            }

            // ③ 仓库缓存 tools/.mimalloc/<rid>/：从 AppContext.BaseDirectory 向上
            //    最多 6 级直接探测目标路径（同 ToolchainResolver ③ 的纪律）
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth <= 6 && dir != null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", ".mimalloc", Rid);
                if (TryLayout(candidate, out var fromCache))
                {
                    return fromCache;
                }
            }

            // ④ 编译器 exe 旁 .mimalloc/<rid>/（发布形态：publish 目录随带）
            var besideExe = Path.Combine(AppContext.BaseDirectory, ".mimalloc", Rid);
            if (TryLayout(besideExe, out var fromPublish))
            {
                return fromPublish;
            }

            return null;
        }

        // 校验 <dir>/include/mimalloc.h + <dir>/lib/<静态库> 齐备则采纳
        private static bool TryLayout(string dir, out MimallocLayout? layout)
        {
            var includeDir = Path.Combine(dir, "include");
            var staticLib = Path.Combine(dir, "lib", StaticLibName);
            if (File.Exists(Path.Combine(includeDir, "mimalloc.h")) && File.Exists(staticLib))
            {
                layout = new MimallocLayout(staticLib);
                return true;
            }
            layout = null;
            return false;
        }

        /// <summary>
        /// 平台系统库链接参数（静态链接 mimalloc 需追加，lld 按序解析）：
        /// win = advapi32（官方静态库 prim.obj 的 large-page 启用路径引用
        /// OpenProcessToken/LookupPrivilegeValueA/AdjustTokenPrivileges，实测
        /// 最小集）；linux = pthread + dl（官方 CMake 同款： pthread 用于
        /// TLS/自旋兜底，dl 用于 dladdr 符号回溯）。
        /// </summary>
        public static string[] SystemLibraryArgs()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new[] { "-ladvapi32" };
            }
            return new[] { "-lpthread", "-ldl" };
        }

        /// <summary>
        /// 返回人类可读的解析顺序说明（供 verbose/错误消息）。
        /// </summary>
        public static string DescribeSearchOrder()
        {
            return
                "mimalloc 解析顺序（GC_OPTIMIZATION_PLAN §2.4）：\n" +
                "  ① --mimalloc-dir <dir>： <dir>/include/mimalloc.h + <dir>/lib/" + StaticLibName + "\n" +
                "  ② 环境变量 RIGI_MIMALLOC：$RIGI_MIMALLOC/include/mimalloc.h + $RIGI_MIMALLOC/lib/" + StaticLibName + "\n" +
                $"  ③ 仓库缓存：             <仓库根>/tools/.mimalloc/{Rid}/（tools/Fetch-Mimalloc.ps1 产出）\n" +
                $"  ④ 编译器 exe 旁：        <exe 目录>/.mimalloc/{Rid}/（发布形态）\n" +
                "全部未命中时编译期拒绝；请先运行 tools/Fetch-Mimalloc.ps1 获取。";
        }
    }
}
