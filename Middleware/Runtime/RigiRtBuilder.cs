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
    /// 源码、实际预处理快照、clang codegen 依赖与有效参数共同做缓存身份；
    /// 未命中从同一 cpp-output 快照编译，目录完整原子发布，真实目标布局恒验证。
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
        // 私有覆盖根可供性能冷/热实验复用；默认路径与既有用户缓存兼容。
        internal static string GetCacheRoot()
        {
            return Cache.ArtifactCache.Root("runtime-cache");
        }

        // 本次请求持有自己的 bitcode 副本，runtime 文件锁释放后才进入对象缓存。
        internal sealed class PreparedBitcode : IDisposable
        {
            internal string Path { get; }
            internal string? Identity { get; }
            private readonly string directory;
            internal PreparedBitcode(string directory, string path, string? identity)
            { this.directory = directory; Path = path; Identity = identity; }
            public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }
        }

        public static string EnsureBitcode(string clangPath, string targetTriple,
            out bool rebuilt, LibuvLayout? libuv = null)
        {
            var prepared = PrepareBitcode(clangPath, targetTriple, out rebuilt, libuv);
            // 旧 path-only API 无法交付 IDisposable；未缓存产物保留到进程退出。
            // 一律交付已验证的请求副本，不能在 cache I/O 退化后回指旧的坏 entry。
            // native 正式请求使用 PrepareBitcode 的词法生命期，不经过此兼容通路。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => prepared.Dispose();
            return prepared.Path;
        }

        internal static PreparedBitcode PrepareBitcode(string clangPath, string targetTriple,
            out bool rebuilt, LibuvLayout? libuv = null, bool library = false)
        {
            using var metric = PerformanceMetrics.Begin("runtime.ensure-bitcode");
            try
            {
                if (string.IsNullOrWhiteSpace(targetTriple))
                    throw new ArgumentException("rigi_rt 目标三元组不能为空", nameof(targetTriple));
                // 显式 SHA pin 是工具链授权约束，身份无法确认/缓存退化时仍必须执行。
                ToolchainResolver.Fingerprint(clangPath, Environment.GetEnvironmentVariable("RIGI_LLVM_SHA256"));
                var assembly = Assembly.GetExecutingAssembly();
                var sources = new List<(string FileName, string Text)>();
                foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
                {
                    if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                        !(name.EndsWith(".c", StringComparison.Ordinal) || name.EndsWith(".h", StringComparison.Ordinal))) continue;
                    using var stream = assembly.GetManifestResourceStream(name);
                    using var reader = new StreamReader(stream!, Encoding.UTF8);
                    sources.Add((name.Substring(ResourcePrefix.Length), reader.ReadToEnd()));
                }
                if (sources.Count == 0) throw new InvalidOperationException("rigi_rt 内嵌源缺失");
                var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rigi-rt-" + Guid.NewGuid().ToString("N"));
                Cache.ArtifactCache.PrivateDirectory(directory);
                try
                {
                    // 子进程环境是请求快照，不修改父进程；实际预处理吸收所有头文件/搜索环境。
                    var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                        .ToDictionary(x => (string)x.Key, x => (string)x.Value!, StringComparer.Ordinal);
                    foreach (var (name, text) in sources) WriteNewText(System.IO.Path.Combine(directory, name), text);
                    var unity = System.IO.Path.Combine(directory, "unity.c");
                    WriteNewText(unity, BuildUnitySource(sources, RtFeatureMacros));
                    var snapshot = System.IO.Path.Combine(directory, "runtime.i");
                    var output = System.IO.Path.Combine(directory, "rigi_rt.bc");
                    var preprocess = BuildCompileArgs(targetTriple, libuv, unity, snapshot);
                    if (library) preprocess.Add("-DRIGI_LIBRARY=1");
                    // PIC TargetMachine 不能修复 clang 已写进 bitcode 的 local-exec TLS 模型。
                    if (library && !OperatingSystem.IsWindows()) preprocess.Add("-fPIC");
                    preprocess.Remove("-emit-llvm"); preprocess.Remove("-c");
                    preprocess.Add("-E"); preprocess.Add("-P");
                    // assert 的 __FILE__ 已在预处理内展开，必须在展开前归一私有源目录。
                    preprocess.Add("-ffile-prefix-map=" + directory + "=rigi_rt");
                    var exit = ExternalProcess.Run(clangPath, preprocess, out _, out var stderr,
                        directory, environment, parentPhase: "runtime.preprocess", replaceEnvironment: true);
                    if (exit != 0 || !File.Exists(snapshot))
                        throw new InvalidOperationException($"rigi_rt 预处理失败（clang 退出码 {exit}）：\n{stderr.Trim()}");
                    var codegen = BuildCompileArgs(targetTriple, null, snapshot, output);
                    if (library && !OperatingSystem.IsWindows()) codegen.Add("-fPIC");
                    codegen.RemoveAll(a => a.StartsWith("-D", StringComparison.Ordinal));
                    codegen.Add("-x"); codegen.Add("cpp-output");
                    codegen.Add("-ffile-prefix-map=" + directory + "=rigi_rt");
                    // -x 是位置参数，必须先于 snapshot，避免驱动按 .i 的默认行为变化。
                    codegen.Remove(snapshot); codegen.Add("runtime.i");
                    var compiler = ToolchainIdentity.TryCapture(clangPath, codegen, directory, environment);
                    var canonicalArgs = string.Join("\n", preprocess.Concat(codegen)
                        .Select(a => a.Replace(directory, "<runtime-source>", StringComparison.Ordinal)));
                    var key = compiler == null ? null : Cache.ArtifactCache.Identity("runtime-snapshot-v2",
                        ComputeCacheIdentity(sources, BuildUnitySource(sources, RtFeatureMacros), compiler, preprocess
                            .Select(a => a.Replace(directory, "<runtime-source>", StringComparison.Ordinal)).ToArray()),
                        Cache.ArtifactCache.HashFile(snapshot), canonicalArgs, compiler);
                    void Build(string path)
                    {
                        var args = codegen.Select(a => a == output ? path : a).ToArray();
                        var result = ExternalProcess.Run(clangPath, args, out _, out var error,
                            directory, environment, parentPhase: "runtime.bitcode-compile", replaceEnvironment: true);
                        if (result != 0 || !File.Exists(path))
                            throw new InvalidOperationException($"rigi_rt 现场编译失败（clang 退出码 {result}）：\n{error.Trim()}");
                        // 不能将编译期间变换的工具链发布到旧身份下。
                        if (compiler != null && compiler != ToolchainIdentity.TryCapture(clangPath, codegen, directory, environment))
                            throw new InvalidOperationException("rigi_rt 编译期间工具链发生变化，请重试");
                    }
                    rebuilt = false;
                    var cached = key != null && Cache.ArtifactCache.TryMaterialize(GetCacheRoot(), key,
                        "rigi_rt.bc", output, Build, p => LlvmBitcode.ValidateRuntimeTarget(p, targetTriple), out rebuilt);
                    if (!cached)
                    {
                        Build(output); LlvmBitcode.ValidateRuntimeTarget(output, targetTriple);
                        rebuilt = true;
                        PerformanceMetrics.Event("cache", "runtime-bitcode", "bypass", key == null
                            ? "runtime-identity-unavailable" : "cache-io");
                    }
                    else PerformanceMetrics.Cache("runtime-bitcode", System.IO.Path.Combine(GetCacheRoot(), key!), rebuilt);
                    return new PreparedBitcode(directory, output, key);
                }
                catch
                {
                    try { Directory.Delete(directory, true); }
                    catch (Exception cleanup) when (Cache.ArtifactCache.IsCacheIo(cleanup)) { }
                    throw;
                }
            }
            catch (Exception exception) { metric?.Fail(exception); throw; }
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

        private static void WriteNewText(string path, string text)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }

    }
}
