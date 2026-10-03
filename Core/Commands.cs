using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;

namespace RigiCompiler
{
    // ===== 共享子命令插件（compile 与 test 注册同一个插件类，语义同 M26）=====

    /// <summary>--verbose：控制台与日志文件输出 verbose 级日志（默认只显示 Warning 及以上）。</summary>
    public class VerboseOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--verbose",
            Description = "控制台与日志文件输出 verbose 级日志（默认只显示 Warning 及以上）",
        };
    }

    /// <summary>--log-to：日志以 JSONL 写入指定文件（verbose 需同时传 --verbose）。</summary>
    public class LogToOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--log-to",
            Description = "日志以 JSONL 写入指定文件（verbose 需同时传 --verbose）",
            ArgsHint = "<路径>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>共享日志子命令的应用：在 Execute 主体之前调用；失败返回错误文本。</summary>
    internal static class LoggerOptions
    {
        public static string? Apply(CommandLineParseResult result)
        {
            if (result.Has("--verbose")) Logger.EnableVerbose();
            var paths = result.Get("--log-to");
            if (paths != null)
            {
                try
                {
                    Logger.OpenLogFile(paths[0]);
                }
                catch (Exception ex)
                {
                    return $"无法打开日志文件 {paths[0]}: {ex.Message}";
                }
            }
            return null;
        }
    }

    // ===== compile 的子命令插件 =====

    /// <summary>--file：要编译的 Rigi 源文件（1 个或多个）。</summary>
    public class FileOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--file",
            Description = "要编译的 Rigi 源文件（1 个或多个）",
            ArgsHint = "<路径...>",
            MinArgs = 1,
            MaxArgs = int.MaxValue,
        };
    }

    /// <summary>--parse-only：只解析，AST 以 JSONL 输出（默认 stdout，给 --dump-ast 则写文件）。</summary>
    public class ParseOnlyOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--parse-only",
            Description = "只解析：AST 以 JSONL 输出到 stdout（同时给 --dump-ast 则写文件）",
            MutuallyExclusive = { "--emit-bil", "--sema-only", "--explain-dispatch" },
        };
    }

    /// <summary>--dump-ast：解析成功后把 AST 以 JSONL 写入指定文件（两种模式都支持）。</summary>
    public class DumpAstOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--dump-ast",
            Description = "解析成功后把 AST 以 JSONL 写入指定文件",
            ArgsHint = "<路径>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>--emit-bil：语义分析通过后把 BIL 文本写入指定文件（按命名空间切分多文件）。与 --parse-only/--sema-only/--explain-dispatch 互斥。</summary>
    public class EmitBilOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--emit-bil",
            Description = "语义分析通过后发射 BIL：按命名空间切分多文件——全局命名空间写入指定路径，其余命名空间追加 .<ns> 后缀（与 --parse-only/--sema-only/--explain-dispatch 互斥）",
            ArgsHint = "<路径>",
            MinArgs = 1,
            MaxArgs = 1,
            MutuallyExclusive = { "--parse-only", "--sema-only", "--explain-dispatch" },
        };
    }

    /// <summary>--sema-only：只做语义分析（P1–P3），输出诊断后结束，不发射 BIL。与 --parse-only/--emit-bil/--explain-dispatch 互斥。</summary>
    public class SemaOnlyOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--sema-only",
            Description = "只做语义分析（P1–P3）：输出诊断后结束，不发射 BIL（与 --parse-only/--emit-bil/--explain-dispatch 互斥）",
            MutuallyExclusive = { "--parse-only", "--emit-bil", "--explain-dispatch" },
        };
    }

    /// <summary>--explain-dispatch：语义分析通过后把 wrapper 派发链报告打印到 stdout（RUNTIME §15）。与 --parse-only/--emit-bil/--sema-only 互斥。</summary>
    public class ExplainDispatchOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--explain-dispatch",
            Description = "语义分析通过后把 wrapper 派发链报告打印到 stdout（与 --parse-only/--emit-bil/--sema-only 互斥）",
            MutuallyExclusive = { "--parse-only", "--emit-bil", "--sema-only" },
        };
    }

    // ===== test 的子命令插件 =====

    /// <summary>--all：运行全部测试套件（CI 入口）。与 --run 互斥。</summary>
    public class AllOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--all",
            Description = "运行全部测试套件（任意失败返回非零退出码）",
            MutuallyExclusive = { "--run" },
        };
    }

    /// <summary>--run：按编号运行指定套件（编号见菜单，可多个；不带编号打印菜单）。与 --all 互斥。</summary>
    public class RunOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--run",
            Description = "按编号运行指定套件（编号见 test 菜单，可多个；不带编号打印菜单）",
            ArgsHint = "[编号...]",
            MinArgs = 0,
            MaxArgs = int.MaxValue,
            MutuallyExclusive = { "--all" },
        };
    }

    /// <summary>--suite-args：把可选参数传给被跑套件（未实现带参入口的套件忽略）。</summary>
    public class SuiteArgsOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--suite-args",
            Description = "传给被跑套件的可选参数（未实现带参入口的套件忽略）",
            ArgsHint = "[值...]",
            MinArgs = 0,
            MaxArgs = int.MaxValue,
        };
    }

    /// <summary>--spawned：标记测试子进程（fuzz 套件在进程内跑完自己的区间，不再并行派生）。</summary>
    public class SpawnedOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--spawned",
            Description = "标记测试子进程（fuzz 套件在进程内跑完自己的区间，不再并行派生）",
        };
    }

    // ===== 顶层 COMMAND 插件 =====

    /// <summary>compile：编译 Rigi 源文件（词法+语法+语义分析，--emit-bil 发射 BIL）。</summary>
    public class CompileCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "compile",
            Description = "编译 Rigi 源文件（词法+语法+语义分析，--emit-bil 发射 BIL）",
        };

        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = new ICommandLineOption[]
        {
            new FileOption(),
            new ParseOnlyOption(),
            new DumpAstOption(),
            new EmitBilOption(),
            new SemaOnlyOption(),
            new ExplainDispatchOption(),
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

            // compile 必须给 --file；--parse-only / --dump-ast 对 --file 的依赖也由此覆盖
            var files = result.Get("--file");
            if (files == null)
            {
                Console.Error.WriteLine("compile 需要 --file <路径...> 指定源文件");
                return 2;
            }

            bool parseOnly = result.Has("--parse-only");
            bool semaOnly = result.Has("--sema-only");
            bool explainDispatch = result.Has("--explain-dispatch");
            string? dumpPath = result.Get("--dump-ast")?[0];
            string? emitBilPath = result.Get("--emit-bil")?[0];

            int failed = 0;
            int dumped = 0;
            // 语义管线的用户源集合（--parse-only 不走语义，不收集）
            var userRoots = new List<RootASTNode>();
            StreamWriter? dumpWriter = null;
            // --dump-ast 输出路径不可写（目录不存在/权限不足等）属环境错误：
            // 对齐 --log-to 的措辞风格友好报错，退出码 2（此前无 catch 直接崩溃）
            if (dumpPath != null)
            {
                try
                {
                    dumpWriter = new StreamWriter(dumpPath, append: false, encoding: new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Error("Compile", $"无法打开 AST 输出文件 {dumpPath}: {ex.Message}");
                    return 2;
                }
            }
            try
            {
                var parsed = Frontend.ParseFiles(files);
                for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
                {
                    var file = files[fileIndex];
                    parsed[fileIndex].Logs.Replay();
                    try
                    {
                        var ast = parsed[fileIndex].GetRoot();
                        if (!parseOnly) userRoots.Add(ast);
                        // AST 输出：--dump-ast 写文件；否则 --parse-only 输出到 stdout。
                        // 多文件时先输出一行 {"file":...} 元记录分隔（M31）：
                        // 消费方靠它切分段落（id 每文件从 1 重排），字段名键控不依赖顺序
                        if (dumpWriter != null)
                        {
                            if (files.Count > 1)
                            {
                                dumpWriter.WriteLine(JsonSerializer.Serialize(
                                    new Dictionary<string, string> { ["file"] = file }));
                            }
                            AstJsonlSerializer.Serialize(ast, dumpWriter);
                            dumped++;
                        }
                        else if (parseOnly)
                        {
                            if (files.Count > 1)
                            {
                                Console.Out.WriteLine(JsonSerializer.Serialize(
                                    new Dictionary<string, string> { ["file"] = file }));
                            }
                            AstJsonlSerializer.Serialize(ast, Console.Out);
                            dumped++;
                        }
                        // --explain-dispatch 报告走 stdout 数据流，跳过「解析成功」噪声
                        if (!parseOnly && !explainDispatch)
                        {
                            Console.WriteLine($"解析成功: {file}");
                        }
                    }
                    catch (CompilerInternalException ex)
                    {
                        // 内部编译器错误（AST 不变量被破坏 = 编译器自身 bug）：
                        // 与用户语法错误严格区分（M31：此前 catch-all 把两类混为一谈）
                        Console.Error.WriteLine($"内部编译器错误 {file}: {ex.Message}");
                        failed++;
                    }
                    catch (Exception ex)
                    {
                        // 单文件失败不中断后续文件，最后统一以非零退出码反映
                        Console.Error.WriteLine($"编译失败 {file}: {ex.Message}");
                        failed++;
                    }
                }
            }
            finally
            {
                dumpWriter?.Dispose();
            }
            // 全部失败时不宣称 dumped（M31：此前无条件打印误导）
            if (dumpPath != null && dumped > 0) Console.WriteLine($"AST dumped to {dumpPath}");
            // 任一文件解析失败：语义管线不再推进（CompilationUnit 需要完整源集）
            if (failed > 0) return 1;
            if (parseOnly) return 0;
            // 语义管线（S6）：stdlib + 用户源 → P1–P3（--emit-bil 时继续 P4 发射；
            // --explain-dispatch 打印派发链报告后结束）
            return RunSemanticPipeline(files[0], userRoots, semaOnly, explainDispatch, emitBilPath);
        }

        // 词法 + 语法解析；词法/语法错误原样抛出，由 Execute 逐文件捕获
        internal static RootASTNode ParseFile(string path)
        {
            return Frontend.Parse(new SourceInput(File.ReadAllText(path), path));
        }

        // 语义管线的编译单元组装（compile 与 run 共用）：stdlib（在前）+ 用户源
        internal static CompilationUnit BuildUnit(List<RootASTNode> userRoots)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(PerformanceMetrics.Measure("frontend.stdlib", StdlibSources.ParseAll));
            roots.AddRange(userRoots);
            return new CompilationUnit(roots.ToArray());
        }

        // P1–P3 语义 pass + 诊断输出门槛（compile 与 run 共用；S6 同序）：
        // 诊断统一经 Logger 输出后有 Error 即停。失败返回 null，exitCode=1。
        internal static IReadOnlyList<BoundFunctionBody>? RunSemaPasses(
            CompilationUnit unit, out int exitCode)
        {
            exitCode = 1;
            var declarations = PerformanceMetrics.Measure("semantic.P1", () => DeclarationCollector.Collect(unit), hasErrors: () => unit.Diagnostics.HasErrors);
            PerformanceMetrics.Measure("semantic.P2", () => DeclarationResolver.Resolve(unit, declarations), hasErrors: () => unit.Diagnostics.HasErrors);
            var bodies = PerformanceMetrics.Measure("semantic.P3", () => Binder.Bind(unit, declarations), hasErrors: () => unit.Diagnostics.HasErrors);
            EmitDiagnostics(unit.Diagnostics, 0);
            if (unit.Diagnostics.HasErrors) return null;
            exitCode = 0;
            return bodies;
        }

        // P4 发射（Lowerer → BilEmitter，moduleName 取第一个源文件的去扩展名
        // 文件名）+ 新增诊断门槛 + BilVerifier 验证（M58，§21：产出非法即
        // 编译器 bug——响亮失败逐条输出验证错误）。验证对象是 merged 模块
        // （切片仅作写盘打包——跨切片引用合并后才可解析，§17）。
        // compile --emit-bil 与 run 共用；失败返回 null，exitCode 为 1。
        internal static BilEmitResult? LowerAndVerify(CompilationUnit unit,
            IReadOnlyList<BoundFunctionBody> bodies, string moduleName, out int exitCode)
        {
            exitCode = 1;
            // P4 也会产生诊断（如未覆盖节点）：发射后只输出新增部分，有 Error 不推进
            int emitted = unit.Diagnostics.Diagnostics.Count;
            var lowered = PerformanceMetrics.Measure("lowering.P4a", () => Lowerer.Lower(unit, bodies), hasErrors: () => unit.Diagnostics.HasErrors);
            var emitResult = PerformanceMetrics.Measure("lowering.P4b", () => BilEmitter.EmitWithSlices(unit, lowered, moduleName), hasErrors: () => unit.Diagnostics.HasErrors);
            EmitDiagnostics(unit.Diagnostics, emitted);
            if (unit.Diagnostics.HasErrors) return null;
            var verificationErrors = PerformanceMetrics.Measure("bil.verifier", () => BilVerifier.Verify(emitResult.Merged), isFailure: errors => errors.Count > 0);
            if (verificationErrors.Count > 0)
            {
                foreach (var error in verificationErrors)
                {
                    Logger.Error("BilVerifier", error.ToString());
                }
                return null;
            }
            exitCode = 0;
            return emitResult;
        }

        // 语义管线（S6）的 compile 特有分支：--explain-dispatch / --sema-only /
        // §17 切片写盘；管线本体见 BuildUnit / RunSemaPasses / LowerAndVerify
        // （与 run 命令共用，避免两处漂移）
        private static int RunSemanticPipeline(string firstFile, List<RootASTNode> userRoots,
            bool semaOnly, bool explainDispatch, string? emitBilPath)
        {
            var unit = BuildUnit(userRoots);
            // P1–P3 为累积式设计：pass 间尽量继续以最大化报错（ARCHITECTURE §8），
            // 三段跑完后统一输出诊断再判 HasErrors（与 BilEmitterTests 的全管线同序）
            var bodies = RunSemaPasses(unit, out int exitCode);
            if (bodies == null) return exitCode;
            if (explainDispatch)
            {
                // 报告走 stdout 数据流（日志/诊断已走 stderr）
                Console.Out.Write(DispatchExplainer.Explain(unit));
                return 0;
            }
            if (semaOnly) return 0;
            if (emitBilPath != null)
            {
                // P4 + BilVerifier（与 run 共用）；有 Error/验证错误不落盘
                var emitResult = LowerAndVerify(unit, bodies,
                    Path.GetFileNameWithoutExtension(firstFile), out exitCode);
                if (emitResult == null) return exitCode;
                // §17 命名空间切分写盘：全局命名空间切片写 --emit-bil 指定
                // 路径；其余切片同目录追加 .<命名空间> 后缀。空切片（无声明
                // 无 fn）不写。BIL 输出路径不可写（目录不存在/权限不足等）
                // 属环境错误：对齐 --log-to 的措辞风格友好报错，退出码 2
                var extension = Path.GetExtension(emitBilPath);
                if (extension.Length == 0) extension = ".bil";
                var directory = Path.GetDirectoryName(emitBilPath) ?? "";
                var baseName = Path.GetFileNameWithoutExtension(emitBilPath);
                foreach (var (ns, slice) in emitResult.Slices)
                {
                    if (slice.LocalSymbols.Count == 0 && slice.Functions.Count == 0) continue;
                    var path = ns.Length == 0
                        ? emitBilPath
                        : Path.Combine(directory, baseName + "." + ns + extension);
                    try
                    {
                        File.WriteAllText(path, BilWriter.Write(slice), new UTF8Encoding(false));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Logger.Error("Compile", $"无法写入 BIL 输出文件 {path}: {ex.Message}");
                        return 2;
                    }
                    Console.WriteLine($"BIL emitted to {path}");
                }
            }
            return 0;
        }

        // 诊断经 Logger 输出（Severity Error→错误级、Warning→警告级，控制台走 stderr）。
        // 格式 {sourceName}:{行}:{列} [{Phase}] {Message}（行/列 1 起始）；
        // Span 为 null 的编译单元级诊断省略位置段。skip 跳过此前已输出的条数。
        internal static void EmitDiagnostics(DiagnosticBag diagnostics, int skip)
        {
            for (int i = skip; i < diagnostics.Diagnostics.Count; i++)
            {
                var d = diagnostics.Diagnostics[i];
                var message = d.Span is { } span
                    ? $"{span.sourceName}:{span.Start.line}:{span.Start.column} [{d.Phase}] {d.Message}"
                    : $"[{d.Phase}] {d.Message}";
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    Logger.Error("Semantic", message);
                }
                else
                {
                    Logger.Warning("Semantic", message);
                }
            }
        }
    }

    /// <summary>test：运行测试套件（裸用或 --run 不带编号时打印套件菜单）。</summary>
    /// <summary>稳定 JSON 清单：只枚举套件、用例和门控，不执行测试。</summary>
    public class InventoryOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--inventory",
            Description = "输出测试覆盖清单 JSON（不执行测试）",
            MutuallyExclusive = { "--run", "--all", "--suite-args", "--spawned" },
        };
    }

    // worker 仅执行一个稳定 ID；不与旧 suite 调度混用。
    public sealed class TestWorkerOption(string name, int arguments) : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = name, Description = "隔离测试 worker 协议", MinArgs = arguments, MaxArgs = arguments,
            MutuallyExclusive = { "--run", "--all", "--inventory", "--suite-args" },
        };
    }

    public class TestCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "test",
            Description = "运行测试套件（裸用打印套件菜单）",
        };

        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = new ICommandLineOption[]
        {
            new InventoryOption(),
            new TestWorkerOption("--worker", 0),
            new TestWorkerOption("--case-id", 1),
            new TestWorkerOption("--result-file", 1),
            new AllOption(),
            new RunOption(),
            new SuiteArgsOption(),
            new SpawnedOption(),
            new VerboseOption(),
            new LogToOption(),
        };

        public int Execute(CommandLineParseResult result)
        {
            if (LoggerOptions.Apply(result) is { } loggerError)
            {
                Console.Error.WriteLine(loggerError);
                return 2;
            }

            if (result.Has("--worker"))
            {
                var id = result.Get("--case-id")?.SingleOrDefault();
                var path = result.Get("--result-file")?.SingleOrDefault();
                if (id == null || path == null || !result.Has("--spawned"))
                {
                    Console.Error.WriteLine("worker 需要 --case-id、--result-file 与 --spawned");
                    return 2;
                }
                Tests.TestRunner.IsSpawned = true;
                var outcome = Tests.CaseCatalog.Run(id);
                outcome.Write(path);
                if (Tests.CaseCatalog.Find(id) == null) return 2;
                return outcome.Status switch
                {
                    Tests.CaseStatus.Pass or Tests.CaseStatus.Skip => 0,
                    Tests.CaseStatus.Cancel => 130,
                    _ => 1,
                };
            }
            if (result.Has("--case-id") || result.Has("--result-file"))
            {
                Console.Error.WriteLine("--case-id 与 --result-file 仅适用于 --worker");
                return 2;
            }

            if (result.Has("--inventory"))
            {
                Tests.TestInventory.Write(Console.OpenStandardOutput());
                return 0;
            }

            IReadOnlyList<string>? suiteArgs = result.Get("--suite-args");

            // --spawned 只用于并行 fuzz 的测试子进程：设置后 fuzz 套件在进程内
            // 跑完自己的区间，不再二次派生。由 TestRunner 透传给套件。
            Tests.TestRunner.IsSpawned = result.Has("--spawned");

            if (result.Has("--all"))
            {
                // 退出码即失败用例总数；clamp 防 Unix 8 位退出码回绕假绿
                return Math.Min(Tests.TestRunner.RunAllSuites(suiteArgs), 255);
            }

            var runArgs = result.Get("--run");
            if (runArgs is { Count: > 0 })
            {
                // 编号 = TestRunner 注册表顺序（1 起）
                var numbers = new List<int>();
                foreach (var a in runArgs)
                {
                    if (!int.TryParse(a, out int n) || n < 1 || n > Tests.TestRunner.SuiteCount)
                    {
                        Console.Error.WriteLine($"无效测试编号: {a}（合法范围 1~{Tests.TestRunner.SuiteCount}，用 test 查看菜单）");
                        return 2;
                    }
                    numbers.Add(n);
                }
                return Math.Min(Tests.TestRunner.RunSuites(numbers, suiteArgs), 255);
            }

            if (suiteArgs != null)
            {
                Console.Error.WriteLine("--suite-args 仅在 test --run 或 test --all 时生效");
                return 2;
            }

            if (result.Has("--spawned"))
            {
                Console.Error.WriteLine("--spawned 仅在 test --run 或 test --all 时生效");
                return 2;
            }

            // test 裸用 / --run 不带编号 → 打印套件菜单后退出
            Tests.TestRunner.PrintMenu();
            return 0;
        }
    }

    /// <summary>vm：加载并执行 BIL 文件（多文件合并为一个模块后运行入口函数）。</summary>
    public class BilFileOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--file",
            Description = "要加载执行的 BIL 文件（1 个或多个，合并为一个模块）",
            ArgsHint = "<路径...>",
            MinArgs = 1,
            MaxArgs = int.MaxValue,
        };
    }

    /// <summary>vm：指令步数上限（正整数；缺省不限制）。超过时受控终止。</summary>
    public class MaxStepsOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--max-steps",
            Description = "VM 指令步数上限（正整数；缺省不限制）。超过时以受控错误终止（VmStepLimitException，退出码 1）",
            ArgsHint = "<N>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>vm：--entry-point：模块存在多个 entrypoint fn 时显式指定入口（BIL 符号）。</summary>
    public class EntryPointOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--entry-point",
            Description = "显式指定入口 fn 的 BIL 符号（模块存在多个 entrypoint fn 时必需；缺省恰一个才自动选中）",
            ArgsHint = "<符号>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>vm：加载并执行 BIL 文件（多文件合并为一个模块后运行入口函数）。</summary>
    public class VmCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "vm",
            Description = "加载并执行 BIL 文件（多文件合并为一个模块后运行入口函数）",
        };

        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = new ICommandLineOption[]
        {
            new BilFileOption(),
            new MaxStepsOption(),
            new EntryPointOption(),
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

            // CLI 默认也有高预算，防止不受信 BIL 无限递归永久占住进程。
            long maxSteps = 1_000_000_000;
            if (result.Has("--max-steps"))
            {
                string raw = result.Get("--max-steps")![0];
                if (!long.TryParse(raw, out maxSteps) || maxSteps < 1)
                {
                    Console.Error.WriteLine("--max-steps 需要正整数，收到：" + raw);
                    return 2;
                }
            }

            var files = result.Get("--file");
            if (files == null)
            {
                Console.Error.WriteLine("vm 需要 --file <路径...> 指定 BIL 文件");
                return 2;
            }

            // 逐个解析并合并为一个模块；符号/资源/函数重复即报错
            var module = new BilModule();
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
                BilModule parsed;
                try
                {
                    parsed = BilReader.Read(text);
                }
                catch (BilParseException ex)
                {
                    Console.Error.WriteLine($"解析失败 {file}: {ex.Message}");
                    return 1;
                }
                if (!BilModuleMerger.Merge(module, parsed, out var duplicate))
                {
                    Console.Error.WriteLine($"合并失败 {file}: 符号重复 \"{duplicate}\"");
                    return 1;
                }
            }

            // 合并后验证：错误逐条输出到 stderr
            var verificationErrors = BilVerifier.Verify(module);
            if (verificationErrors.Count > 0)
            {
                foreach (var error in verificationErrors)
                {
                    Console.Error.WriteLine(error.ToString());
                }
                return 1;
            }

            // entrypoint 解析（§17）——vm 与 run 共用 TryResolveEntryPoint
            string? entryPointArg = result.Get("--entry-point")?[0];
            if (!TryResolveEntryPoint(module, entryPointArg, out string? entryPoint))
            {
                return 2;
            }

            // 运行：VM stdout/stderr 原样写对应流；同步/异步异常（含步数上限）→ stderr 退出码 1
            BilVmResult run;
            try
            {
                run = BilVm.Run(module, maxSteps, entryPoint);
            }
            catch (VmException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            Console.Out.Write(run.Stdout);
            Console.Error.Write(run.Stderr);
            if (run.Exception != null)
            {
                Console.Error.WriteLine(run.Exception.Message);
                return 1;
            }
            return 0;
        }

        // entrypoint 解析（§17，vm 与 run 共用）：--entry-point 显式指定时须在候选内；
        // 缺省恰一个自动选中（out entryPoint 保持 null = 自动选中，与历史行为一致），
        // 零个/多个报错（多个时列出候选并提示 --entry-point）——模块形状/用法错误
        // 归退出码 2（调用方收 false 后返回 2）。失败逐条输出到 stderr。
        internal static bool TryResolveEntryPoint(BilModule module,
            string? explicitEntryPoint, out string? entryPoint)
        {
            entryPoint = explicitEntryPoint;
            var entrypoints = new List<BilSimpleMemberDeclaration>();
            foreach (var entry in module.LocalSymbols)
            {
                if (entry is BilSimpleMemberDeclaration member
                    && HasKeyword(member, BilKeyword.Entrypoint))
                {
                    entrypoints.Add(member);
                }
            }
            if (explicitEntryPoint != null)
            {
                if (!entrypoints.Exists(m => m.Symbol == explicitEntryPoint))
                {
                    Console.Error.WriteLine("--entry-point 指定的符号不是 entrypoint 方法: "
                        + explicitEntryPoint);
                    foreach (var entry in entrypoints)
                    {
                        Console.Error.WriteLine("  " + entry.Symbol);
                    }
                    return false;
                }
                return true;
            }
            if (entrypoints.Count == 0)
            {
                Console.Error.WriteLine("找不到入口：模块没有 entrypoint fn");
                return false;
            }
            if (entrypoints.Count > 1)
            {
                Console.Error.WriteLine("模块存在多个 entrypoint fn（用 --entry-point <符号> 显式指定）：");
                foreach (var entry in entrypoints)
                {
                    Console.Error.WriteLine("  " + entry.Symbol);
                }
                return false;
            }
            return true;
        }

        private static bool HasKeyword(BilSimpleMemberDeclaration member, BilKeyword keyword)
        {
            foreach (var modifier in member.Modifiers)
            {
                if (modifier is BilKeywordModifier keywordModifier
                    && keywordModifier.Keyword == keyword)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>help：显示帮助（无参打印概览；按名字寻址打印详情，见 CommandLineHelp）。</summary>
    public class HelpCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "help",
            Description = "显示帮助（无参打印概览）",
            ArgsHint = "[名字]",
            MinArgs = 0,
            MaxArgs = 1,
        };

        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = Array.Empty<ICommandLineOption>();

        public int Execute(CommandLineParseResult result)
        {
            if (result.CommandArgs.Count == 0)
            {
                CommandLineHelp.PrintOverview();
                return 0;
            }
            // 名字寻址：compile → COMMAND 详情；compile.file → 子命令详情（不带 -- 前缀）
            var name = result.CommandArgs[0];
            if (CommandLineHelp.PrintByName(name)) return 0;
            Console.Error.WriteLine($"help: 找不到 '{name}'（无参 help 可查看全部 COMMAND 与子命令）");
            return 2;
        }
    }
}
