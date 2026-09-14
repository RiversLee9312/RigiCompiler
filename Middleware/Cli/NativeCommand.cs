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

        private static int EmitAndLink(Bil.BilModule module, string? outPath,
            string? emitObjPath, string? emitLlPath, string? toolchainDir,
            string? libuvDir, string? mimallocDir, IReadOnlyList<string>? linkInputs)
        {
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
            var context = new MwContext(module);
            MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;
            using var llvmModule = ModuleBuilder.Build(context, mir);

            // .ll 黄金快照：合并 rigi_rt 前的模块文本（稳定、可读）
            if (emitLlPath != null)
            {
                File.WriteAllText(emitLlPath, llvmModule.PrintToString());
                Logger.Verbose("Middleware", $"已写出 LLVM IR {emitLlPath}");
            }

            // --emit-obj：合并/优化前的中间产物（调试用，无需工具链）
            if (emitObjPath != null)
            {
                if (!ObjectEmitter.TryEmitObject(llvmModule, emitObjPath, out var objError))
                {
                    Console.Error.WriteLine($"目标文件发射失败 {emitObjPath}: {objError}");
                    return 2;
                }
                Logger.Verbose("Middleware", $"已发射目标文件 {emitObjPath}");
            }

            if (outPath == null)
            {
                return 0;
            }

            // 链接路径：恰一个 entrypoint（rigi_entry 由 rigi_rt 的 main 调用）
            var entrypoints = 0;
            foreach (var fn in mir.Functions)
            {
                if (fn.IsEntrypoint)
                {
                    entrypoints++;
                }
            }
            if (entrypoints != 1)
            {
                Console.Error.WriteLine($"native --out 需要恰一个 entrypoint fn（当前 {entrypoints} 个）");
                return 2;
            }

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

            // rigi_rt 现场编译为 bitcode（内容哈希缓存）→ 进程内合并 → 统一优化
            string bitcode;
            try
            {
                bitcode = RigiRtBuilder.EnsureBitcode(clang, out _, libuv);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("rigi_rt 编译失败: " + ex.Message);
                return 2;
            }
            LlvmBitcode.MergeBitcodeFileInto(llvmModule, bitcode);
            LlvmBitcode.RunDefaultOptimization(llvmModule);

            var tempObject = Path.Combine(Path.GetTempPath(),
                "rigi_" + Guid.NewGuid().ToString("N") + ".o");
            try
            {
                if (!ObjectEmitter.TryEmitObject(llvmModule, tempObject, out var emitError))
                {
                    Console.Error.WriteLine($"目标文件发射失败: {emitError}");
                    return 2;
                }
                // clang 驱动 lld 链接（-fuse-ld=lld；CRT 发现交 clang）。
                // libuv 命中时追加静态库全路径 + 平台系统库（win 九个 /
                // linux pthread+dl，见 LibuvResolver.SystemLibraryArgs）
                var linkArgs = new List<string> { tempObject, "-o", outPath, "-fuse-ld=lld" };
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
                var linkExit = ExternalProcess.Run(clang, linkArgs,
                    out _, out var linkStderr);
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
                try { if (File.Exists(tempObject)) { File.Delete(tempObject); } }
                catch (IOException) { /* 临时文件清理失败不致命 */ }
            }
        }
    }
}