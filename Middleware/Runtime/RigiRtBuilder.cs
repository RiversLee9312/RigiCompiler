using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Middleware.Runtime
{
    /// <summary>
    /// rigi_rt 现场编译为 LLVM bitcode（MIDDLEWARE_ARCHITECTURE §4.8 MW1 定稿）：
    /// rigi_rt/**/*.c 以 EmbeddedResource 内嵌进本程序集（参照
    /// Semantic/StdlibSources.cs 的 stdlib 内嵌模式），按全部源拼接的 SHA256
    /// 内容哈希做缓存目录；未命中则解出源并调 clang 编成 rigi_rt.bc。
    /// </summary>
    public static class RigiRtBuilder
    {
        // 内嵌资源的逻辑名前缀（csproj EmbeddedResource 的 LogicalName 约定）
        private const string ResourcePrefix = "rigi_rt/";

        /// <summary>
        /// 确保 rigi_rt.bc 就绪并返回其全路径。
        /// rebuilt=false 表示命中缓存直接复用；true 表示本次现场编译。
        /// clang 退出码非 0 抛 <see cref="InvalidOperationException"/>（带 stderr 摘要）。
        /// libuv 非 null 时（LibuvResolver 命中）追加 -I&lt;include&gt; 与
        /// -DRIGI_HAS_LIBUV=1（MW11b 棒2 预留：rigi_rt 的 uv 用法一律包在
        /// #ifdef RIGI_HAS_LIBUV 内）；两个参数纳入内容哈希，命中/未命中间不串味。
        /// </summary>
        public static string EnsureBitcode(string clangPath, out bool rebuilt,
            LibuvLayout? libuv = null)
        {
            // ① 读出全部 rigi_rt 源文本（逻辑名序保证可重现）；.c 是编译
            //    单元，.h 仅解出供 #include（MW4 arc.h 起）
            var assembly = Assembly.GetExecutingAssembly();
            var names = new List<string>(assembly.GetManifestResourceNames());
            names.Sort(StringComparer.Ordinal);
            var sources = new List<(string FileName, string Text)>();
            foreach (var name in names)
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                    (!name.EndsWith(".c", StringComparison.Ordinal) &&
                     !name.EndsWith(".h", StringComparison.Ordinal)))
                {
                    continue;
                }
                using var stream = assembly.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream!, Encoding.UTF8);
                // 逻辑名 rigi_rt/shim.c → 文件名 shim.c（缓存目录内扁平落盘）
                sources.Add((name.Substring(ResourcePrefix.Length), reader.ReadToEnd()));
            }
            if (sources.Count == 0)
            {
                // 与 StdlibSources 同纪律：零匹配 = EmbeddedResource 配置失效，响亮失败
                throw new InvalidOperationException(
                    "rigi_rt 内嵌源缺失：程序集中未找到任何 rigi_rt/**/*.{c,h} 资源（EmbeddedResource 配置失效）");
            }

            // ② 全部源拼接的 SHA256（按逻辑名序拼接，可重现）；libuv 编译参数
            //    （-I 目录 + RIGI_HAS_LIBUV 定义）一并入哈希，否则同一批源在
            //    libuv 命中/未命中间会命中同一缓存目录而串味
            string hash;
            using (var sha = SHA256.Create())
            {
                var builder = new StringBuilder();
                foreach (var (fileName, text) in sources)
                {
                    builder.Append(fileName).Append('\n').Append(text).Append('\n');
                }
                builder.Append("-D_POSIX_C_SOURCE=200809L\n");
                if (libuv != null)
                {
                    builder.Append("-I").Append(libuv.IncludeDir).Append('\n')
                        .Append("-DRIGI_HAS_LIBUV=1\n");
                }
                hash = Convert.ToHexString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
            }

            // ③ 缓存目录：%TEMP%/rigi_rt_cache/<hash 前 16 位>/
            var cacheDir = Path.Combine(
                Path.GetTempPath(), "rigi_rt_cache", hash.Substring(0, 16));
            var bitcodePath = Path.Combine(cacheDir, "rigi_rt.bc");

            // ④ 命中缓存直接复用；否则解出源并现场编译
            if (File.Exists(bitcodePath))
            {
                rebuilt = false;
                Logger.Verbose("Middleware", $"rigi_rt 命中缓存 {bitcodePath}");
                return bitcodePath;
            }
            Directory.CreateDirectory(cacheDir);
            foreach (var (fileName, text) in sources)
            {
                File.WriteAllText(Path.Combine(cacheDir, fileName), text);
            }

            // unity build 决策（MW1 定稿）：工具链无 llvm-link，多 .c 逐个编成
            // .bc 后无法合并；-c 配多文件又禁止 -o。故生成 unity.c 逐文件
            // #include 聚合（仅 .c 编译单元；.h 已由上文解出到同目录）
            var unity = new StringBuilder();
            // unity 是单一翻译单元：POSIX 钟面（clock_gettime）必须在
            // 任何系统头之前可见。-std=c11 默认不暴露该声明。
            unity.Append("#define _POSIX_C_SOURCE 200809L\n");
            foreach (var (fileName, _) in sources)
            {
                if (fileName.EndsWith(".c", StringComparison.Ordinal))
                {
                    unity.Append("#include \"").Append(fileName).Append("\"\n");
                }
            }
            var unityPath = Path.Combine(cacheDir, "unity.c");
            File.WriteAllText(unityPath, unity.ToString());

            // win/linux 通用参数；不传目标三元组，用 clang 默认宿主目标。
            // libuv 命中时加 -I<include> 与 -DRIGI_HAS_LIBUV=1（棒2 的 uv 用法
            // 一律包在 #ifdef RIGI_HAS_LIBUV 内，未命中时零影响）
            var args = new List<string>
            {
                "-emit-llvm", "-c", "-O1", "-std=c11", "-Wall",
                "-D_POSIX_C_SOURCE=200809L",
            };
            if (libuv != null)
            {
                args.Add("-I" + libuv.IncludeDir);
                args.Add("-DRIGI_HAS_LIBUV=1");
            }
            args.Add(unityPath);
            args.Add("-o");
            args.Add(bitcodePath);
            var exitCode = ExternalProcess.Run(clangPath, args,
                out var stdout, out var stderr,
                workingDirectory: cacheDir);
            if (exitCode != 0 || !File.Exists(bitcodePath))
            {
                throw new InvalidOperationException(
                    $"rigi_rt 现场编译失败（clang 退出码 {exitCode}）：\n{stderr.Trim()}");
            }
            rebuilt = true;
            Logger.Verbose("Middleware", $"rigi_rt 已现场编译 {bitcodePath}");
            return bitcodePath;
        }
    }
}
