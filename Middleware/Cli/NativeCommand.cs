using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Gate;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Middleware.Cli
{
    /// <summary>native --file：输入的 BIL 文件（1 个或多个，合并为一个模块过门禁）。</summary>
    public class NativeFileOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--file",
            Description = "输入的 BIL 文件（1 个或多个；§17 命名空间切片经多文件合并）",
            ArgsHint = "<路径...>",
            MinArgs = 1,
            MaxArgs = int.MaxValue,
        };
    }

    /// <summary>native --out：输出的原生可执行文件路径（clang 驱动 lld 链接 rigi_rt）。</summary>
    public class NativeOutOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--out",
            Description = "输出的原生可执行文件路径（win-x64 需自带 .exe 后缀）",
            ArgsHint = "<路径>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>native --emit-obj：只发射目标文件到指定路径（不链接，无需工具链）。</summary>
    public class NativeEmitObjOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--emit-obj",
            Description = "只发射原生目标文件到指定路径（不链接；rigi_rt 引用为未解析外部符号）",
            ArgsHint = "<路径>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>native --emit-ll：把合并 rigi_rt 前的 LLVM IR 文本写到指定路径（黄金快照产物）。</summary>
    public class NativeEmitLlOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--emit-ll",
            Description = "把合并 rigi_rt 前的 LLVM IR 文本写到指定路径（调试/黄金快照）",
            ArgsHint = "<路径>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>native --toolchain：显式指定 LLVM 工具链目录（解析顺序最优先）。</summary>
    public class NativeToolchainOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--toolchain",
            Description = "显式指定 LLVM 工具链目录（解析顺序：--toolchain → RIGI_LLVM → tools/.llvm → PATH）",
            ArgsHint = "<目录>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>native --libuv-dir：显式指定 libuv 静态库目录（解析顺序最优先）。</summary>
    public class NativeLibuvDirOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--libuv-dir",
            Description = "显式指定 libuv 目录（解析顺序：--libuv-dir → RIGI_LIBUV → tools/.libuv → 编译器旁 .libuv）",
            ArgsHint = "<目录>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>native --mimalloc-dir：显式指定 mimalloc 静态库目录（解析顺序最优先）。</summary>
    public class NativeMimallocDirOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--mimalloc-dir",
            Description = "显式指定 mimalloc 目录（解析顺序：--mimalloc-dir → RIGI_MIMALLOC → tools/.mimalloc → 编译器旁 .mimalloc）",
            ArgsHint = "<目录>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>
    /// native --link：非 rigi_rt native 库（RUNTIME §26 库解析留白，L6 定稿）
    /// 的额外链接输入——目标文件 / 静态库 / 导入库 / 共享库全路径，逐条原样
    /// 追加到 clang 链接行（.o/.obj/.a/.lib/.so 双平台同形态；win 的 .dll
    /// 经其导入库 .lib 链入）。仅随 --out 生效。
    /// </summary>
    public class NativeLinkOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--link",
            Description = "追加非 rigi_rt native 库的链接输入（目标文件/静态库/导入库/共享库路径，可多条）",
            ArgsHint = "<路径...>",
            MinArgs = 1,
            MaxArgs = int.MaxValue,
        };
    }

    /// <summary>
    /// native：Middleware 驱动（MIDDLEWARE_ARCHITECTURE §11 Cli/）——BIL 文本经
    /// Gate 门禁 → MwContext（符号表 + MIR）→ 进程内 LLVM 管线（模块构建 →
    /// rigi_rt bitcode 合并 → 优化 → .o）→ clang 驱动 lld 链接可执行文件。
    /// </summary>
    public class NativeCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "native",
            Description = "Middleware：BIL → 原生可执行文件（进程内 LLVM 管线 + clang/lld 链接 rigi_rt）",
        };

        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = new ICommandLineOption[]
        {
            new NativeFileOption(),
            new NativeOutOption(),
            new NativeEmitObjOption(),
            new NativeEmitLlOption(),
            new NativeToolchainOption(),
            new NativeLibuvDirOption(),
            new NativeMimallocDirOption(),
            new NativeLinkOption(),
            new VerboseOption(),
            new LogToOption(),
        };

        public int Execute(CommandLineParseResult result)
        {
            // --verbose / --log-to 在主体之前应用
            if (LoggerOptions.Apply(result) is { } loggerError)
            {
                Console.Error.WriteLine(loggerError);
                return 2;
            }

            var files = result.Get("--file");
            if (files == null)
            {
                Console.Error.WriteLine("native 需要 --file <路径...> 指定 BIL 文件");
                return 2;
            }
            var outPath = result.Get("--out")?[0];
            var emitObjPath = result.Get("--emit-obj")?[0];
            var emitLlPath = result.Get("--emit-ll")?[0];
            if (outPath == null && emitObjPath == null && emitLlPath == null)
            {
                Console.Error.WriteLine("native 需要 --out <路径> 或 --emit-obj <路径> 或 --emit-ll <路径> 指定产物");
                return 2;
            }

            // 读入全部 BIL 文本
            var inputs = new List<(string SourceName, string Text)>();
            foreach (var file in files)
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or FileNotFoundException or DirectoryNotFoundException)
                {
                    Console.Error.WriteLine($"无法读取 BIL 文件 {file}: {ex.Message}");
                    return 1;
                }
                inputs.Add((file, text));
            }

            // Gate 门禁：非法 BIL 逐条报 stderr 并拒绝（BIL §23）
            var gate = BilGate.Accept(inputs);
            if (!gate.IsAccepted)
            {
                foreach (var error in gate.Errors)
                {
                    Console.Error.WriteLine(error);
                }
                return 1;
            }
            Logger.Verbose("Middleware",
                $"门禁通过：{gate.Module!.Functions.Count} fn，符号段 {gate.Module.LocalSymbols.Count} 本地条目");

            try
            {
                return EmitAndLink(gate.Module!, outPath, emitObjPath, emitLlPath,
                    result.Get("--toolchain")?[0], result.Get("--libuv-dir")?[0],
                    result.Get("--mimalloc-dir")?[0], result.Get("--link"));
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("native: " + ex.Message);
                return 2;
            }
            catch (MwNotSupportedException ex)
            {
                Console.Error.WriteLine("native: " + ex.Message);
                return 2;
            }
            catch (CompilerInternalException ex)
            {
                Console.Error.WriteLine("native: 编译被拒绝：" + ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("native: 生成失败：" + ex.Message);
                return 2;
            }
        }

        // module --run 复用同一 EmitAndLink（merged 模块直接进管线，
        // 不经 Gate——模块由编译器进程内自产且已过 BilVerifier）；outKind 只
        // 影响 entrypoint 门槛报错里的命令形态（native --out / module --run）
        internal static int EmitAndLink(Bil.BilModule module, string? outPath,
            string? emitObjPath, string? emitLlPath, string? toolchainDir,
            string? libuvDir, string? mimallocDir, IReadOnlyList<string>? linkInputs,
            string outKind = "native --out", NativeBuildOptions? nativeBuild = null)
        {
            nativeBuild ??= NativeBuildOptions.Executable;
            var lldThreads = outPath == null ? null : LinkerThreads.Argument(Environment.GetEnvironmentVariable("RIGI_LLD_THREADS"));
            // --link 输入先验存在（L6：非 rigi_rt 库链接；仅随 --out 消费，
            // 但早失败优于链接期 lld 报错）
            if (linkInputs != null)
            {
                foreach (var input in linkInputs)
                {
                    if (!File.Exists(input))
                    {
                        Console.Error.WriteLine($"--link 链接输入不存在: {input}");
                        return 2;
                    }
                }
            }
            // BilModule 是可变模型；同一对象的请求串行消费，调用方不得并发回写。
            lock (module)
            {
                var errors = Bil.BilVerifier.Verify(module);
                if (errors.Count != 0)
                {
                    foreach (var error in errors) Console.Error.WriteLine(error);
                    return 1;
                }
                var context = new MwContext(module, nativeBuild);
                if (outPath == null)
                {
                    PerformanceMetrics.Event("cache", "native-object", "bypass", "diagnostic-artifact-request");
                    EmitObject(context, null, emitObjPath, emitLlPath, null);
                    return 0;
                }
                // 身份查询之前只检查本地声明与实际函数体，不把 block 修饰符当入口。
                var entrypoints = module.Functions.Count(fn => context.Symbols.FindMember(fn.Symbol)
                    is { IsExternal: false } member && member.HasKeyword(Bil.BilKeyword.Entrypoint));
                if (!nativeBuild.IsLibrary && entrypoints != 1)
                {
                    Console.Error.WriteLine($"{outKind} 需要恰一个 entrypoint fn（当前 {entrypoints} 个）");
                    return 2;
                }
                var target = LlvmHost.HostTriple;
                var layout = LlvmHost.HostDataLayout;
                var clang = ToolchainResolver.ResolveClang(toolchainDir);
                if (clang == null)
                {
                    Console.Error.WriteLine("找不到 clang。解析顺序：" + ToolchainResolver.DescribeSearchOrder());
                    return 2;
                }
                Logger.Verbose("Middleware", $"工具链 clang: {clang}");

                // libuv 静态库解析（--libuv-dir → RIGI_LIBUV → tools/.libuv → exe 旁）；
                // 命中则 rigi_rt 带 RIGI_HAS_LIBUV 编译且链接行追加静态库 + 系统库；
                // 未命中：编译期明确拒绝（review-20260910——旧行为是 rigi_rt 编
                // abort 桩、程序运行期才炸，已改为链接前失败）
                var libuv = LibuvResolver.Resolve(libuvDir);
                Logger.Verbose("Middleware", libuv != null
                    ? $"libuv: {libuv.StaticLibPath}"
                    : "libuv 未命中，降级链接。解析顺序：" + LibuvResolver.DescribeSearchOrder());
                // review-20260910（用户裁定）：libuv 缺失从此是编译期失败而非
                // 运行期 abort——workerLoop/alarm/定时器等运行面属 stdlib 公共
                // 形态（任何程序都经 Dispatcher workerLoop 承载 main），无 libuv
                // 的 stub 桩路径没有可信降级语义
                if (libuv == null)
                {
                    Console.Error.WriteLine("native 编译失败：未找到 libuv（协程/定时器/"
                        + "执行器运行面必需；缺失时旧行为是运行期 abort，现已改为编译期"
                        + "明确拒绝）。解析顺序：" + LibuvResolver.DescribeSearchOrder());
                    return 2;
                }

                // mimalloc 静态库解析（GC Phase 2：--mimalloc-dir → RIGI_MIMALLOC →
                // tools/.mimalloc → exe 旁）；track_malloc/free 是 rigi_rt 对象分配
                // 唯一收口（GC_OPTIMIZATION_PLAN §2.4），缺 mimalloc 符号必然链接
                // 失败，故同 libuv 纪律在编译期明确拒绝（响亮早失败优于链接期
                // lld 深处报 undefined symbol）
                var mimalloc = MimallocResolver.Resolve(mimallocDir);
                Logger.Verbose("Middleware", mimalloc != null
                    ? $"mimalloc: {mimalloc.StaticLibPath}"
                    : "mimalloc 未命中。解析顺序：" + MimallocResolver.DescribeSearchOrder());
                if (mimalloc == null)
                {
                    Console.Error.WriteLine("native 编译失败：未找到 mimalloc（rigi_rt "
                        + "track 台账的底层分配面必需；请先运行 tools/Fetch-Mimalloc.ps1）。"
                        + "解析顺序：" + MimallocResolver.DescribeSearchOrder());
                    return 2;
                }

                // runtime singleflight/filelock 完整释放后才开始 object singleflight。
                var archiver = nativeBuild.Kind == NativeBuildKind.StaticLibrary ? ToolchainResolver.ResolveArchiver(toolchainDir) : null;
                if (nativeBuild.Kind == NativeBuildKind.StaticLibrary && archiver == null)
                    throw new MwNotSupportedException("找不到静态库归档工具：RIGI_AR/LLVM 工具链/PATH 中的 llvm-ar、ar 或 lib.exe");
                var buildIdentity = nativeBuild.Identity + "|" + (archiver == null ? "" : archiver + ":" + ToolchainResolver.Fingerprint(archiver));
                using var runtime = RigiRtBuilder.PrepareBitcode(clang, target, out _, libuv, nativeBuild.IsLibrary);
                var requestDirectory = Path.Combine(Path.GetTempPath(), "rigi-native-" + Guid.NewGuid().ToString("N"));
                Cache.ArtifactCache.PrivateDirectory(requestDirectory);
                var tempObject = Path.Combine(requestDirectory, "program.o");
                try
                {
                    var diagnostic = emitObjPath != null || emitLlPath != null;
                    var key = diagnostic ? null : Cache.NativeObjectIdentity.TryCompute(module, target, layout, runtime.Identity, buildIdentity);
                    var rebuilt = false;
                    var cached = key != null && Cache.ArtifactCache.TryMaterialize(Cache.ArtifactCache.Root("object-cache"),
                        key, "program.o", tempObject,
                        path => EmitObject(context, path, null, null, runtime.Path), null, out rebuilt);
                    if (!cached)
                    {
                        PerformanceMetrics.Event("cache", "native-object", "bypass", diagnostic
                            ? "diagnostic-artifact-request" : runtime.Identity == null ? "runtime-identity-unavailable"
                            : key == null ? "backend-identity-unavailable" : "cache-io");
                        EmitObject(context, tempObject, emitObjPath, emitLlPath, runtime.Path);
                    }
                    else
                    {
                        var entry = Path.Combine(Cache.ArtifactCache.Root("object-cache"), key!, "program.o");
                        PerformanceMetrics.Cache("native-object", entry, rebuilt);
                        Logger.Verbose("Middleware", $"whole-program 对象缓存 {(rebuilt ? "miss" : "hit")}: {entry}");
                    }
                    if (nativeBuild.Kind == NativeBuildKind.StaticLibrary)
                    {
                        // 归档真实 merge/runtime/O2/PIC 对象；外部依赖另列，不嵌套其他 archive。
                        var librarian = Path.GetFileNameWithoutExtension(archiver!) is "lib" or "llvm-lib";
                        IReadOnlyList<string> args = librarian ? ["/NOLOGO", "/OUT:" + outPath, tempObject] : ["rcs", outPath, tempObject];
                        var exit = ExternalProcess.Run(archiver!, args, out _, out var stderr, parentPhase: "native.archive");
                        if (exit != 0) throw new MwNotSupportedException("归档失败：" + stderr);
                        return 0;
                    }
                    // clang 驱动 lld 链接（-fuse-ld=lld；CRT 发现交 clang）。
                    // libuv 命中时追加静态库全路径 + 平台系统库（win 九个 /
                    // linux pthread+dl，见 LibuvResolver.SystemLibraryArgs）
                    var linkArgs = new List<string>
                    {
                        "--target=" + target, tempObject, "-o", outPath,
                        "-fuse-ld=lld",
                    };
                    if (lldThreads != null) linkArgs.Add(lldThreads);
                    if (nativeBuild.Kind == NativeBuildKind.DynamicLibrary)
                    {
                        linkArgs.Add("-shared");
                        var exportFile = Path.Combine(requestDirectory, OperatingSystem.IsWindows() ? "exports.def" : "exports.map");
                        File.WriteAllText(exportFile, OperatingSystem.IsWindows() ? "EXPORTS\n" + string.Join("\n", nativeBuild.Exports.Select(e => e.Name))
                            : "{ global: " + string.Join(" ", nativeBuild.Exports.Select(e => e.Name + ";")) + " local: *; };\n");
                        linkArgs.Add(OperatingSystem.IsWindows() ? "-Wl,/DEF:" + exportFile : "-Wl,--version-script=" + exportFile);
                    }
                    // L6：用户 native 库链接输入紧随主目标文件（lld 按序解析，
                    // 外部符号在主目标之后满足）
                    if (linkInputs != null)
                    {
                        linkArgs.AddRange(linkInputs);
                    }
                    if (libuv != null)
                    {
                        linkArgs.Add(libuv.StaticLibPath);
                        linkArgs.AddRange(LibuvResolver.SystemLibraryArgs());
                    }
                    // GC Phase 2：mimalloc 静态库 + 其平台系统库（win advapi32 /
                    // linux pthread+dl，见 MimallocResolver.SystemLibraryArgs）。
                    // 紧随 libuv 之后追加，位于主目标文件之后（lld 按序解析，主
                    // 目标里 rigi_rt 的 mi_malloc_aligned/mi_free 引用由此满足）
                    linkArgs.Add(mimalloc.StaticLibPath);
                    linkArgs.AddRange(MimallocResolver.SystemLibraryArgs());
                    // MW12：macrogc.c 的 GC 协程承载线程用 pthread_create，
                    // 与 libuv 命中与否无关，linux 链接恒需 pthread
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        && libuv == null)
                    {
                        linkArgs.Add("-lpthread");
                    }
                    // 施工块 6-4：rigi_rt math.c 的 libm 依赖（sqrt/pow/超越
                    // 函数族）。Windows 数学函数在 ucrt（lld 默认链接）；
                    // 现代 glibc 数学符号已并入 libc，-lm 为旧发行版兜底
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        linkArgs.Add("-lm");
                    }
                    // 施工块 6-5：rigi_rt random.c 的 Windows BCryptGenRandom 依赖
                    // （bcrypt）；Linux getrandom 在 libc 内，无需额外库
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        linkArgs.Add("-lbcrypt");
                    }
                    // jsonfix：主线程栈保留与 rigi_stack_has_room 守卫预算联动
                    // （shim.c 自递归预算 1 MiB）。win PE 默认栈保留仅 1 MiB——
                    // 守卫预算必须恒小于真实保留，否则递归会在守卫触发前先触
                    // guard page；显式保留 8 MiB（预算 ~1.7× 于 256 层递归环，
                    // json_read_nested 深度安全语料口径）。lld-link 经 clang
                    // 驱动 -Wl, 透传；linux 主线程栈由宿主 ulimit 兜底（glibc
                    // 默认 8 MiB 同量级），不在此设限
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        linkArgs.Add("-Wl,/STACK:8388608");
                    }
                    // trace 仅在旁路测量开启时列出真正选择的库/CRT，不改变链接语义。
                    if (PerformanceMetrics.Enabled && OperatingSystem.IsLinux()) linkArgs.Add("-Wl,--trace");
                    NativeLinkRecord.Plan(clang, linkArgs);
                    var linkExit = ExternalProcess.Run(clang, linkArgs,
                        out var linkTrace, out var linkStderr, parentPhase: "native.final-link");
                    NativeLinkRecord.Actual(linkTrace);
                    if (linkExit != 0)
                    {
                        Console.Error.WriteLine($"链接失败（clang 退出码 {linkExit}）:\n{linkStderr}");
                        return 2;
                    }
                    Logger.Verbose("Middleware", $"已链接可执行文件 {outPath}");
                    return 0;
                }
                finally
                {
                    try { Directory.Delete(requestDirectory, true); }
                    catch (IOException) { /* 请求目录清理失败不改变编译结果。 */ }
                }
            }
        }

        // 完整 LLVM lease 覆盖 create/merge/O2/emit/dispose，绝不在此等待文件锁。
        private static void EmitObject(MwContext context, string? finalObject,
            string? debugObject, string? llvmText, string? bitcode)
        {
            MwPipeline.CreateDefault().Run(context);
            using var lease = LlvmHost.Enter();
            using var module = PerformanceMetrics.Measure("llvm.ir-build", () => ModuleBuilder.Build(context, context.Mir!));
            if (llvmText != null)
            {
                File.WriteAllText(llvmText, module.PrintToString());
                Logger.Verbose("Middleware", $"已写出 LLVM IR {llvmText}");
            }
            if (debugObject != null)
            {
                if (!ObjectEmitter.TryEmitObject(module, debugObject, out var error))
                    throw new MwNotSupportedException($"目标文件发射失败 {debugObject}: {error}");
                Logger.Verbose("Middleware", $"已发射目标文件 {debugObject}");
            }
            if (finalObject == null) return;
            PerformanceMetrics.Measure("llvm.merge", () => LlvmBitcode.MergeBitcodeFileInto(module, bitcode!));
            Logger.Verbose("Middleware", "LLVM 阶段 default<O2> 开始");
            PerformanceMetrics.Measure("llvm.optimize.O2", () => LlvmBitcode.RunDefaultOptimization(module));
            Logger.Verbose("Middleware", "LLVM 阶段 default<O2> 完成");
            if (!ObjectEmitter.TryEmitObject(module, finalObject, out var emitError))
                throw new MwNotSupportedException($"目标文件发射失败: {emitError}");
        }
    }
}
