using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Middleware.Runtime
{
    /// <summary>
    /// rigi_rt 现场编译为 LLVM bitcode（MIDDLEWARE_ARCHITECTURE §4.8 MW1 定稿）：
    /// rigi_rt/**/*.c 以 EmbeddedResource 内嵌进本程序集（参照
    /// Semantic/StdlibSources.cs 的 stdlib 内嵌模式），按全部源拼接的 SHA256
    /// 源码、clang 内容及有序目标编译参数共同做缓存目录身份；未命中则
    /// 解出源并调 clang 编成 rigi_rt.bc，命中与新产物均检验真实目标布局。
    /// </summary>
    public static class RigiRtBuilder
    {
        // 内嵌资源的逻辑名前缀（csproj EmbeddedResource 的 LogicalName 约定）
        private const string ResourcePrefix = "rigi_rt/";

        // glibc 特性宏集合（Linux 面唯一事实源，三处落点均由此派生：
        // clang 命令行 -D——先于 unity.c 第 1 行生效；unity.c 前导
        // #define——防未来其他编译入口遗漏 -D 时退化；内容哈希——特性
        // 集合变化必须换缓存键，旧 bitcode 不得复用）。
        // 依据（glibc 2.39 实测守卫 + WSL clang18 复现）：-std=c11 下
        // 显式定义 _POSIX_C_SOURCE 会抑制 glibc 默认派生的 _DEFAULT_SOURCE，
        // 而 __USE_MISC（= _DEFAULT_SOURCE 派生）是 fs.c 所需三者的最小
        // 公共守卫——realpath（stdlib.h，__USE_MISC|__USE_XOPEN_EXTENDED）、
        // syscall（unistd.h，__USE_MISC）、dirent DT_*（dirent.h，
        // __USE_MISC；d_type 字段本身不受守卫）。取 _DEFAULT_SOURCE 而非
        // _GNU_SOURCE：只放开所需面，不引入 GNU 版 strerror_r 等签名分叉
        // 风险（fs.c 的 RENAME_NOREPLACE 自钉常量，同纪律）。
        private static readonly string[] RtFeatureMacros =
        {
            "-D_POSIX_C_SOURCE=200809L", // POSIX 2008 面（clock_gettime、AT_FDCWD 等）
            "-D_DEFAULT_SOURCE",         // realpath / syscall / DT_*（__USE_MISC）
        };

        /// <summary>
        /// 确保 rigi_rt.bc 就绪并返回其全路径。
        /// rebuilt=false 表示命中缓存直接复用；true 表示本次现场编译。
        /// clang 退出码非 0 抛 <see cref="InvalidOperationException"/>（带 stderr 摘要）。
        /// libuv 非 null 时（LibuvResolver 命中）追加 -I&lt;include&gt; 与
        /// -DRIGI_HAS_LIBUV=1（MW11b 棒2 预留：rigi_rt 的 uv 用法一律包在
        /// #ifdef RIGI_HAS_LIBUV 内）；两个参数纳入内容哈希，命中/未命中间不串味。
        /// </summary>
        public static string EnsureBitcode(string clangPath, string targetTriple,
            out bool rebuilt, LibuvLayout? libuv = null)
        {
            if (string.IsNullOrWhiteSpace(targetTriple))
                throw new ArgumentException("rigi_rt 目标三元组不能为空", nameof(targetTriple));
            // 编译参数是缓存身份的唯一事实源；路径为每次构建私有目录，
            // 在哈希中用固定占位符，避免随机 build GUID 破坏命中。
            var compileArgs = BuildCompileArgs(targetTriple, libuv,
                "unity.c", "rigi_rt.bc");
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

            var unitySource = BuildUnitySource(sources, RtFeatureMacros);

            // ② 全部源拼接的 SHA256（按逻辑名序拼接，可重现）；libuv 编译参数
            //    （-I 目录 + RIGI_HAS_LIBUV 定义）一并入哈希，否则同一批源在
            //    libuv 命中/未命中间会命中同一缓存目录而串味
            var compilerHash = ToolchainResolver.Fingerprint(clangPath,
                Environment.GetEnvironmentVariable("RIGI_LLVM_SHA256"));
            Logger.Verbose("Middleware", $"clang={Path.GetFullPath(clangPath)} SHA256={compilerHash}");
            var hash = ComputeCacheIdentity(sources, unitySource, compilerHash, compileArgs);

            // ③ 缓存目录落用户私有数据区，避免 /tmp 等世界可写目录中的
            // 可预测路径被其他用户预置。POSIX 明确收紧为 0700。
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "rigi", "runtime-cache");
            Directory.CreateDirectory(cacheRoot);
            EnsurePrivateDirectory(cacheRoot);
            var cacheDir = Path.Combine(cacheRoot, hash.Substring(0, 16));
            Directory.CreateDirectory(cacheDir);
            EnsurePrivateDirectory(cacheDir);
            var bitcodePath = Path.Combine(cacheDir, "rigi_rt.bc");
            var manifestPath = Path.Combine(cacheDir, "rigi_rt.sha256");

            // ④ 命中时同时核对源身份与 bitcode 内容摘要。任一缺失或不符
            // 都视为污染缓存并重新编译，绝不把未知字节交给 LLVM 解析器。
            if (TryValidateCache(bitcodePath, manifestPath, hash))
            {
                LlvmBitcode.ValidateRuntimeTarget(bitcodePath, targetTriple);
                rebuilt = false;
                Logger.Verbose("Middleware", $"rigi_rt 命中缓存 {bitcodePath}");
                return bitcodePath;
            }

            // 每个竞争构建使用独立私有目录；最终发布用不覆盖的原子 Move，
            // 输掉竞态的一方只复核赢家产物，不会互相踩写。
            var buildDir = Path.Combine(cacheDir, "build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(buildDir);
            EnsurePrivateDirectory(buildDir);
            try
            {
                foreach (var (fileName, text) in sources)
                {
                    WriteNewText(Path.Combine(buildDir, fileName), text);
                }

                var unityPath = Path.Combine(buildDir, "unity.c");
                WriteNewText(unityPath, unitySource);

                // 与哈希中的同一参数表，仅将私有构建目录占位路径替换。
                var candidatePath = Path.Combine(buildDir, "rigi_rt.bc");
                var args = BuildCompileArgs(targetTriple, libuv, unityPath, candidatePath);
                var exitCode = ExternalProcess.Run(clangPath, args,
                    out _, out var stderr,
                    workingDirectory: buildDir);
                if (exitCode != 0 || !File.Exists(candidatePath))
                {
                    throw new InvalidOperationException(
                        $"rigi_rt 现场编译失败（clang 退出码 {exitCode}）：\n{stderr.Trim()}");
                }

                LlvmBitcode.ValidateRuntimeTarget(candidatePath, targetTriple);
                var bitcodeHash = ComputeFileSha256(candidatePath);
                try
                {
                    File.Move(candidatePath, bitcodePath, overwrite: false);
                    WriteNewText(manifestPath, hash + "\n" + bitcodeHash + "\n");
                }
                catch (IOException)
                {
                    // 另一进程可能已经发布；只接受完整且校验通过的赢家。
                    var validWinner = false;
                    // bitcode 与 manifest 是两个原子发布步骤；给赢家一个很短的
                    // 完成窗口，避免输家恰好落在两步之间时误报冲突。
                    for (var attempt = 0; attempt < 20 && !validWinner; attempt++)
                    {
                        validWinner = TryValidateCache(bitcodePath, manifestPath, hash);
                        if (!validWinner) System.Threading.Thread.Sleep(10);
                    }
                    if (validWinner)
                    {
                        LlvmBitcode.ValidateRuntimeTarget(bitcodePath, targetTriple);
                    }
                    if (!validWinner)
                    {
                        throw new InvalidOperationException("rigi_rt 并发缓存发布冲突且赢家产物校验失败");
                    }
                }
                rebuilt = true;
                Logger.Verbose("Middleware", $"rigi_rt 已现场编译 {bitcodePath}");
                return bitcodePath;
            }
            finally
            {
                if (Directory.Exists(buildDir)) Directory.Delete(buildDir, recursive: true);
            }
        }

        internal static string ComputeCacheIdentity(
            IReadOnlyList<(string FileName, string Text)> sources, string unitySource,
            string compilerHash, IReadOnlyList<string> compileArgs)
        {
            var builder = new StringBuilder();
            foreach (var (fileName, text) in sources)
                builder.Append(fileName).Append('\n').Append(text).Append('\n');
            builder.Append("unity-source\n").Append(unitySource);
            builder.Append("clang-sha256=").Append(compilerHash).Append('\n');
            foreach (var arg in compileArgs)
                builder.Append(arg.Length).Append(':').Append(arg).Append('\n');
            return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(builder.ToString())));
        }

        internal static List<string> BuildCompileArgs(string targetTriple,
            LibuvLayout? libuv, string unityPath, string bitcodePath)
        {
            var args = new List<string>
            {
                "--target=" + targetTriple, "-emit-llvm", "-c", "-O1",
                "-std=c11", "-Wall",
            };
            // Windows clang 驱动会根据本机 VS 安装给 msvc triple 追加
            // 版本号；仅 bitcode cc1 目标显式钉回 LLVM 模块的精确 triple。
            // 驱动本身仍负责本机 VS/SDK 头文件与 CRT 路径发现。
            if (OperatingSystem.IsWindows())
            {
                args.Add("-Xclang");
                args.Add("-triple");
                args.Add("-Xclang");
                args.Add(targetTriple);
            }
            args.AddRange(RtFeatureMacros);
            if (libuv != null)
            {
                args.Add("-I" + libuv.IncludeDir);
                args.Add("-DRIGI_HAS_LIBUV=1");
            }
            args.Add(unityPath);
            args.Add("-o");
            args.Add(bitcodePath);
            return args;
        }

        // 特性宏必须先于首个系统头；这里生成的同一文本既用于编译，也用于
        // 缓存身份。-DNAME=VALUE 只在首个 '=' 分隔，值中的 '=' 不应被改写。
        internal static string BuildUnitySource(
            IReadOnlyList<(string FileName, string Text)> sources,
            IReadOnlyList<string> featureMacros)
        {
            var unity = new StringBuilder();
            foreach (var macro in featureMacros)
            {
                var body = macro.Substring(2);
                var eq = body.IndexOf('=');
                unity.Append("#define ");
                if (eq < 0)
                {
                    unity.Append(body).Append(" 1");
                }
                else
                {
                    unity.Append(body, 0, eq).Append(' ')
                        .Append(body, eq + 1, body.Length - eq - 1);
                }
                unity.Append('\n');
            }
            foreach (var (fileName, _) in sources)
            {
                if (fileName.EndsWith(".c", StringComparison.Ordinal))
                {
                    unity.Append("#include \"").Append(fileName).Append("\"\n");
                }
            }
            return unity.ToString();
        }

        private static bool TryValidateCache(string bitcodePath, string manifestPath,
            string sourceHash)
        {
            if (!File.Exists(bitcodePath) || !File.Exists(manifestPath)) return false;
            try
            {
                var lines = File.ReadAllLines(manifestPath);
                return lines.Length >= 2
                    && StringComparer.Ordinal.Equals(lines[0], sourceHash)
                    && StringComparer.OrdinalIgnoreCase.Equals(lines[1],
                        ComputeFileSha256(bitcodePath));
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static void WriteNewText(string path, string text)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }

        private static void EnsurePrivateDirectory(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }
}
