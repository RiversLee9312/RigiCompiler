using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using RigiCompiler.Bil;

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

    /// <summary>--emit-bil：语义分析通过后把 BIL 文本写入指定文件。与 --parse-only/--sema-only/--explain-dispatch 互斥。</summary>
    public class EmitBilOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--emit-bil",
            Description = "语义分析通过后把 BIL 文本写入指定文件（与 --parse-only/--sema-only/--explain-dispatch 互斥）",
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
                foreach (var file in files)
                {
                    try
                    {
                        var ast = ParseFile(file);
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
        private static RootASTNode ParseFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(stream);
            var lexer = new Lexer();
            // GetAwaiter().GetResult() 不包 AggregateException：词法错误原样抛出
            var tokens = lexer.Tokenize(reader, path).GetAwaiter().GetResult();
            var parser = new Parser();
            return (RootASTNode)parser.Parse(tokens);
        }

        // 语义管线（S6）：stdlib（在前）+ 用户源组 CompilationUnit → P1 → P2 → P3，
        // 诊断统一经 Logger 输出后有 Error 即停（返回 1）；--sema-only 到此结束；
        // --explain-dispatch 打印派发链报告到 stdout 后结束（S11f / RUNTIME §15）；
        // --emit-bil 继续 P4（Lowerer → BilEmitter）→ BilVerifier 验证（M58，
        // 非法即报错不落盘）→ BilWriter 把 BIL 文本写文件，
        // moduleName 取第一个源文件的去扩展名文件名
        private static int RunSemanticPipeline(string firstFile, List<RootASTNode> userRoots,
            bool semaOnly, bool explainDispatch, string? emitBilPath)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.AddRange(userRoots);
            var unit = new CompilationUnit(roots.ToArray());
            // P1–P3 为累积式设计：pass 间尽量继续以最大化报错（ARCHITECTURE §8），
            // 三段跑完后统一输出诊断再判 HasErrors（与 BilEmitterTests 的全管线同序）
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            var bodies = Binder.Bind(unit, declarations);
            EmitDiagnostics(unit.Diagnostics, 0);
            if (unit.Diagnostics.HasErrors) return 1;
            if (explainDispatch)
            {
                // 报告走 stdout 数据流（日志/诊断已走 stderr）
                Console.Out.Write(DispatchExplainer.Explain(unit));
                return 0;
            }
            if (semaOnly) return 0;
            if (emitBilPath != null)
            {
                // P4 也会产生诊断（如未覆盖节点）：发射后只输出新增部分，有 Error 不落盘
                int emitted = unit.Diagnostics.Diagnostics.Count;
                var lowered = Lowerer.Lower(unit, bodies);
                var module = BilEmitter.Emit(unit, lowered, Path.GetFileNameWithoutExtension(firstFile));
                EmitDiagnostics(unit.Diagnostics, emitted);
                if (unit.Diagnostics.HasErrors) return 1;
                // BIL 验证器（M58，§21）：产出非法即编译器 bug——响亮失败，
                // 逐条输出验证错误，不落盘
                var verificationErrors = BilVerifier.Verify(module);
                if (verificationErrors.Count > 0)
                {
                    foreach (var error in verificationErrors)
                    {
                        Logger.Error("BilVerifier", error.ToString());
                    }
                    return 1;
                }
                // BIL 输出路径不可写（目录不存在/权限不足等）属环境错误：
                // 对齐 --log-to 的措辞风格友好报错，退出码 2（此前无保护直接崩溃）
                try
                {
                    File.WriteAllText(emitBilPath, BilWriter.Write(module), new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Error("Compile", $"无法写入 BIL 输出文件 {emitBilPath}: {ex.Message}");
                    return 2;
                }
                Console.WriteLine($"BIL emitted to {emitBilPath}");
            }
            return 0;
        }

        // 诊断经 Logger 输出（Severity Error→错误级、Warning→警告级，控制台走 stderr）。
        // 格式 {sourceName}:{行}:{列} [{Phase}] {Message}（行/列 1 起始）；
        // Span 为 null 的编译单元级诊断省略位置段。skip 跳过此前已输出的条数。
        private static void EmitDiagnostics(DiagnosticBag diagnostics, int skip)
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
    public class TestCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "test",
            Description = "运行测试套件（裸用打印套件菜单）",
        };

        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = new ICommandLineOption[]
        {
            new AllOption(),
            new RunOption(),
            new SuiteArgsOption(),
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

            IReadOnlyList<string>? suiteArgs = result.Get("--suite-args");

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

            // test 裸用 / --run 不带编号 → 打印套件菜单后退出
            Tests.TestRunner.PrintMenu();
            return 0;
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
