using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 命令行解析器测试：
    /// - 注册表完整性（三个 COMMAND、名字唯一、子命令唯一、Mask 字段合法、互斥引用存在）；
    /// - COMMAND 匹配与未知 COMMAND；
    /// - 子命令匹配（--x v 与 --x=v 两形态）、参数个数校验（含任意个数）、
    ///   互斥检测、游离参数、重复子命令、未知子命令、--run 零参数合法、
    ///   compile --file 多路径；
    /// - 少量 compile 端到端：--dump-ast/--emit-bil 输出路径不可写时友好
    ///   报错且返回非零退出码（此前无 catch 直接崩溃）。
    /// 其余 CLI 端到端行为（菜单打印、正常文件编译）不在本套件内，手动验证。
    /// </summary>
    public static class CommandLineParserTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        public static void TestRegistryIntegrity()
        {
            Console.WriteLine("=== Testing registry integrity ===");

            var commands = CommandLineRegistry.Commands;
            Check("注册表恰好三个 COMMAND", commands.Length == 3);

            var names = commands.Select(c => c.Mask.Name).ToList();
            Check("COMMAND 名字唯一", names.Distinct().Count() == names.Count);
            Check("包含 compile/test/help",
                names.Contains("compile") && names.Contains("test") && names.Contains("help"));
            Check("COMMAND 名字不带 -- 前缀", commands.All(c => !c.Mask.Name.StartsWith("--")));

            foreach (var cmd in commands)
            {
                var subNames = cmd.SubCommands.Select(s => s.Mask.Name).ToList();
                Check($"{cmd.Mask.Name} 子命令名字唯一", subNames.Distinct().Count() == subNames.Count);
                Check($"{cmd.Mask.Name} 子命令都带 -- 前缀", cmd.SubCommands.All(s => s.Mask.Name.StartsWith("--")));
                Check($"{cmd.Mask.Name} 所有 Mask 名字非空且 MinArgs<=MaxArgs",
                    cmd.SubCommands.All(s => s.Mask.Name.Length > 0 && s.Mask.MinArgs >= 0 && s.Mask.MinArgs <= s.Mask.MaxArgs)
                    && cmd.Mask.Name.Length > 0 && cmd.Mask.MinArgs >= 0 && cmd.Mask.MinArgs <= cmd.Mask.MaxArgs);
                // 互斥引用必须指向同一 COMMAND 下真实存在的子命令
                Check($"{cmd.Mask.Name} 互斥引用都存在",
                    cmd.SubCommands.All(s => s.Mask.MutuallyExclusive.All(subNames.Contains)));
            }

            var compile = commands.First(c => c.Mask.Name == "compile");
            var test = commands.First(c => c.Mask.Name == "test");
            var help = commands.First(c => c.Mask.Name == "help");
            var compileSubs = compile.SubCommands.Select(s => s.Mask.Name).ToList();
            var testSubs = test.SubCommands.Select(s => s.Mask.Name).ToList();
            Check("compile 子命令齐全（--file/--parse-only/--dump-ast/--emit-bil/--sema-only/--explain-dispatch/--verbose/--log-to）",
                new[] { "--file", "--parse-only", "--dump-ast", "--emit-bil", "--sema-only", "--explain-dispatch", "--verbose", "--log-to" }.All(compileSubs.Contains));
            Check("test 子命令齐全（--all/--run/--verbose/--log-to）",
                new[] { "--all", "--run", "--verbose", "--log-to" }.All(testSubs.Contains));
            Check("help 无子命令", help.SubCommands.Count == 0);
            Console.WriteLine();
        }

        public static void TestCommandMatching()
        {
            Console.WriteLine("=== Testing COMMAND matching ===");

            CheckParseOk("compile 匹配", new[] { "compile", "--file", "a.latte" },
                r => r.Command.Mask.Name == "compile");
            CheckParseOk("test 匹配", new[] { "test" }, r => r.Command.Mask.Name == "test");
            CheckParseOk("help 匹配", new[] { "help" }, r => r.Command.Mask.Name == "help");
            CheckParseError("未知 COMMAND 报错", new[] { "bogus" }, "未知 COMMAND");
            CheckParseError("-- 开头的 COMMAND 不被接受", new[] { "--all" }, "未知 COMMAND");
            Console.WriteLine();
        }

        public static void TestSubCommandMatching()
        {
            Console.WriteLine("=== Testing sub-command matching (空格 / = 两形态) ===");

            CheckParseOk("--file 空格形态多路径", new[] { "compile", "--file", "a.latte", "b.latte" },
                r => r.Get("--file") is { Count: 2 } f && f[0] == "a.latte" && f[1] == "b.latte");
            CheckParseOk("--file= 形态", new[] { "compile", "--file=a.latte" },
                r => r.Get("--file") is { Count: 1 } f && f[0] == "a.latte");
            CheckParseOk("--file= 与空格形态混用", new[] { "compile", "--file=a.latte", "b.latte" },
                r => r.Get("--file") is { Count: 2 });
            CheckParseOk("--dump-ast= 形态", new[] { "compile", "--file", "a.latte", "--dump-ast=o.jsonl" },
                r => r.Get("--dump-ast") is { Count: 1 } d && d[0] == "o.jsonl");
            CheckParseOk("无参子命令", new[] { "compile", "--file", "a.latte", "--parse-only" },
                r => r.Has("--parse-only") && r.Get("--parse-only")!.Count == 0);
            CheckParseOk("--run 零参数合法", new[] { "test", "--run" },
                r => r.Get("--run") is { Count: 0 });
            CheckParseOk("--run 多编号", new[] { "test", "--run", "1", "3" },
                r => r.Get("--run") is { Count: 2 } n && n[0] == "1" && n[1] == "3");
            CheckParseError("未知子命令报错", new[] { "test", "--bogus" }, "未知子命令");
            CheckParseError("重复子命令报错", new[] { "compile", "--file", "a", "--file", "b" }, "重复");
            Console.WriteLine();
        }

        public static void TestArgCountValidation()
        {
            Console.WriteLine("=== Testing arg count validation ===");

            CheckParseError("--dump-ast 缺参数报错", new[] { "compile", "--file", "a", "--dump-ast" }, "参数个数");
            CheckParseError("--dump-ast 多参数报错", new[] { "compile", "--file", "a", "--dump-ast", "o1", "o2" }, "参数个数");
            CheckParseError("--parse-only 带参数报错", new[] { "compile", "--file", "a", "--parse-only", "x" }, "参数个数");
            CheckParseError("--file 零参数报错（MinArgs=1）", new[] { "compile", "--file" }, "参数个数");
            CheckParseOk("--file 任意个数（3 个）", new[] { "compile", "--file", "a", "b", "c" },
                r => r.Get("--file")!.Count == 3);
            CheckParseOk("help 一个裸参数", new[] { "help", "compile" },
                r => r.CommandArgs.Count == 1 && r.CommandArgs[0] == "compile");
            CheckParseError("help 两个裸参数报错（MaxArgs=1）", new[] { "help", "a", "b" }, "参数个数");
            Console.WriteLine();
        }

        public static void TestMutualExclusion()
        {
            Console.WriteLine("=== Testing mutual exclusion ===");

            CheckParseError("test --all --run 互斥", new[] { "test", "--all", "--run", "1" }, "互斥");
            CheckParseError("test --run --all 互斥（反向）", new[] { "test", "--run", "1", "--all" }, "互斥");
            CheckParseError("compile --parse-only --emit-bil 互斥", new[] { "compile", "--file", "a", "--parse-only", "--emit-bil", "o" }, "互斥");
            CheckParseError("compile --emit-bil --parse-only 互斥（反向）", new[] { "compile", "--file", "a", "--emit-bil", "o", "--parse-only" }, "互斥");
            CheckParseError("compile --parse-only --sema-only 互斥", new[] { "compile", "--file", "a", "--parse-only", "--sema-only" }, "互斥");
            CheckParseError("compile --emit-bil --sema-only 互斥（语义矛盾：只分析不发射 vs 发射）",
                new[] { "compile", "--file", "a", "--emit-bil", "o", "--sema-only" }, "互斥");
            CheckParseError("compile --sema-only --emit-bil 互斥（反向）",
                new[] { "compile", "--file", "a", "--sema-only", "--emit-bil", "o" }, "互斥");
            CheckParseError("compile --explain-dispatch --emit-bil 互斥",
                new[] { "compile", "--file", "a", "--explain-dispatch", "--emit-bil", "o" }, "互斥");
            CheckParseError("compile --explain-dispatch --sema-only 互斥",
                new[] { "compile", "--file", "a", "--explain-dispatch", "--sema-only" }, "互斥");
            CheckParseError("compile --parse-only --explain-dispatch 互斥",
                new[] { "compile", "--file", "a", "--parse-only", "--explain-dispatch" }, "互斥");
            CheckParseOk("compile --explain-dispatch 合法",
                new[] { "compile", "--file", "a", "--explain-dispatch" },
                r => r.Has("--explain-dispatch"));
            CheckParseOk("compile 子命令之间无互斥", new[] { "compile", "--file", "a", "--parse-only", "--dump-ast", "o" },
                r => r.Has("--parse-only") && r.Has("--dump-ast"));
            Console.WriteLine();
        }

        public static void TestStrayArgs()
        {
            Console.WriteLine("=== Testing stray args ===");

            CheckParseError("compile 裸参数报游离", new[] { "compile", "a.latte" }, "游离参数");
            CheckParseError("test 裸参数报游离", new[] { "test", "1" }, "游离参数");
            CheckParseError("子命令前的裸参数报游离", new[] { "compile", "a.latte", "--file", "b" }, "游离参数");
            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine($"  [PASS] {name}");
                passCount++;
            }
            else
            {
                Fail(name, "断言不成立");
            }
        }

        // 期望解析成功，并满足对结果的进一步断言
        private static void CheckParseOk(string name, string[] args, Func<CommandLineParseResult, bool> assert)
        {
            if (!CommandLineParser.TryParse(args, out var result, out var error))
            {
                Fail(name, $"应解析成功，实际报错: {error}");
                return;
            }
            Check(name, assert(result!));
        }

        // 期望解析失败，且错误文本包含指定片段
        private static void CheckParseError(string name, string[] args, string errorContains)
        {
            if (CommandLineParser.TryParse(args, out _, out var error))
            {
                Fail(name, $"应解析失败（{errorContains}），实际成功");
                return;
            }
            Check(name, error != null && error.Contains(errorContains));
        }

        private static void Fail(string name, string message)
        {
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== CLI 端到端：输出路径不可写 =====
        public static void TestOutputPathErrors()
        {
            Console.WriteLine("=== Testing output path errors (compile 端到端) ===");

            var dir = Path.Combine(Path.GetTempPath(), $"latte_cli_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var src = Path.Combine(dir, "hello.latte");
                File.WriteAllText(src, "pub func main(): i32 { return 0 }\n");
                // 不存在目录下的输出路径：StreamWriter/File.WriteAllText 抛
                // DirectoryNotFoundException（IOException 子类）——此前无 catch 直接崩溃
                var missing = Path.Combine(dir, "no_such_dir");

                int dumpCode = RunCompile("compile", "--file", src,
                    "--dump-ast", Path.Combine(missing, "x.jsonl"));
                Check("--dump-ast 不可写路径返回非零", dumpCode != 0);

                var bilPath = Path.Combine(missing, "x.bil");
                int emitCode = RunCompile("compile", "--file", src, "--emit-bil", bilPath);
                Check("--emit-bil 不可写路径返回非零", emitCode != 0);
                Check("--emit-bil 失败后不落盘", !File.Exists(bilPath));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
            Console.WriteLine();
        }

        // 驱动 compile COMMAND 端到端（测试构造的命令行应解析成功）
        private static int RunCompile(params string[] args)
        {
            if (!CommandLineParser.TryParse(args, out var result, out var error))
            {
                throw new InvalidOperationException($"测试构造的命令行应解析成功: {error}");
            }
            return new CompileCommand().Execute(result!);
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  CommandLineParser Tests           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestRegistryIntegrity();
            TestCommandMatching();
            TestSubCommandMatching();
            TestArgCountValidation();
            TestMutualExclusion();
            TestStrayArgs();
            TestOutputPathErrors();

            Console.WriteLine($"=== CommandLineParser Tests Complete: {passCount} passed, {failCount} failed ===");
            return failCount;
        }
    }
}
