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
            var compilerHash = ToolchainResolver.Fingerprint(clangPath,
                Environment.GetEnvironmentVariable("RIGI_LLVM_SHA256"));
            Logger.Verbose("Middleware", $"clang={Path.GetFullPath(clangPath)} SHA256={compilerHash}");
            using (var sha = SHA256.Create())
            {
                var builder = new StringBuilder();
                foreach (var (fileName, text) in sources)
                {
                    builder.Append(fileName).Append('\n').Append(text).Append('\n');
                }
                builder.Append("-D_POSIX_C_SOURCE=200809L\n");
                // 源相同但 clang 已更新时不能复用旧工具链产物。
                builder.Append("clang-sha256=").Append(compilerHash).Append('\n');
                if (libuv != null)
                {
                    builder.Append("-I").Append(libuv.IncludeDir).Append('\n')
                        .Append("-DRIGI_HAS_LIBUV=1\n");
                }
                hash = Convert.ToHexString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
            }

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
                var unityPath = Path.Combine(buildDir, "unity.c");
                WriteNewText(unityPath, unity.ToString());

            // win/linux 通用参数；不传目标三元组，用 clang 默认宿主目标。
            // libuv 命中时加 -I<include> 与 -DRIGI_HAS_LIBUV=1（棒2 的 uv 用法
            // 一律包在 #ifdef RIGI_HAS_LIBUV 内，未命中时零影响）
                var candidatePath = Path.Combine(buildDir, "rigi_rt.bc");
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
                args.Add(candidatePath);
                var exitCode = ExternalProcess.Run(clangPath, args,
                    out _, out var stderr,
                    workingDirectory: buildDir);
                if (exitCode != 0 || !File.Exists(candidatePath))
                {
                    throw new InvalidOperationException(
                        $"rigi_rt 现场编译失败（clang 退出码 {exitCode}）：\n{stderr.Trim()}");
                }

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
