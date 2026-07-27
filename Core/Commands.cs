using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LatteCompiler
{
    // ===== 共享子命令插件（compile 与 test 注册同一个插件类，语义同 M26）=====

    /// <summary>--verbose：控制台输出 verbose 级日志（默认只显示 Warning 及以上）。</summary>
    public class VerboseOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--verbose",
            Description = "控制台输出 verbose 级日志（默认只显示 Warning 及以上）",
        };
    }

    /// <summary>--log-to：全部日志（含 verbose）以 JSONL 写入指定文件。</summary>
    public class LogToOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--log-to",
            Description = "全部日志（含 verbose）以 JSONL 写入指定文件",
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

    /// <summary>--file：要编译的 Latte 源文件（1 个或多个）。</summary>
    public class FileOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--file",
            Description = "要编译的 Latte 源文件（1 个或多个）",
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

    // ===== 顶层 COMMAND 插件 =====

    /// <summary>compile：编译 Latte 源文件（当前无后端，执行词法+语法解析）。</summary>
    public class CompileCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "compile",
            Description = "编译 Latte 源文件（当前无后端，执行词法+语法解析）",
        };

        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = new ICommandLineOption[]
        {
            new FileOption(),
            new ParseOnlyOption(),
            new DumpAstOption(),
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
            string? dumpPath = result.Get("--dump-ast")?[0];

            int failed = 0;
            StreamWriter? dumpWriter = null;
            try
            {
                if (dumpPath != null)
                {
                    dumpWriter = new StreamWriter(dumpPath, append: false, encoding: new UTF8Encoding(false));
                }
                foreach (var file in files)
                {
                    try
                    {
                        var ast = ParseFile(file);
                        // AST 输出：--dump-ast 写文件；否则 --parse-only 输出到 stdout
                        if (dumpWriter != null)
                        {
                            AstJsonlSerializer.Serialize(ast, dumpWriter);
                        }
                        else if (parseOnly)
                        {
                            AstJsonlSerializer.Serialize(ast, Console.Out);
                        }
                        if (!parseOnly)
                        {
                            Console.WriteLine($"解析成功: {file}");
                        }
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
            if (dumpPath != null) Console.WriteLine($"AST dumped to {dumpPath}");
            return failed > 0 ? 1 : 0;
        }

        // 词法 + 语法解析；词法/语法错误原样抛出，由 Execute 逐文件捕获
        private static ASTNode ParseFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(stream);
            var lexer = new Lexer();
            // GetAwaiter().GetResult() 不包 AggregateException：词法错误原样抛出
            var tokens = lexer.Tokenize(reader, path).GetAwaiter().GetResult();
            var parser = new Parser();
            return parser.Parse(tokens);
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

            if (result.Has("--all"))
            {
                return Tests.TestRunner.RunAllSuites();
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
                return Tests.TestRunner.RunSuites(numbers);
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
