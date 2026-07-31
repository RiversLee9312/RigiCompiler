using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 命令行解析器测试：
    /// - 注册表完整性（三个 COMMAND、名字唯一、子命令唯一、Mask 字段合法、互斥引用存在）；
    /// - COMMAND 匹配与未知 COMMAND；
    /// - 子命令匹配（--x v 与 --x=v 两形态）、参数个数校验（含任意个数）、
    ///   互斥检测、游离参数、重复子命令、未知子命令、--run 零参数合法、
    ///   compile --file 多路径。
    /// CLI 端到端行为（菜单打印、文件编译）不在本套件内，手动验证。
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
            Check("compile 子命令齐全（--file/--parse-only/--dump-ast/--emit-bil/--sema-only/--verbose/--log-to）",
                new[] { "--file", "--parse-only", "--dump-ast", "--emit-bil", "--sema-only", "--verbose", "--log-to" }.All(compileSubs.Contains));
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
            CheckParseOk("compile --emit-bil --sema-only 不互斥", new[] { "compile", "--file", "a", "--emit-bil", "o", "--sema-only" },
                r => r.Has("--emit-bil") && r.Has("--sema-only"));
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

            Console.WriteLine($"=== CommandLineParser Tests Complete: {passCount} passed, {failCount} failed ===");
            return failCount;
        }
    }
}
