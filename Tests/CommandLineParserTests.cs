using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 命令行解析器测试：
    /// - 注册表完整性（五个 COMMAND、名字唯一、子命令唯一、Mask 字段合法、互斥引用存在）；
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
            Check("注册表恰好五个 COMMAND", commands.Length == 5);

            var names = commands.Select(c => c.Mask.Name).ToList();
            Check("COMMAND 名字唯一", names.Distinct().Count() == names.Count);
            Check("包含 compile/test/vm/native/help",
                names.Contains("compile") && names.Contains("test")
                && names.Contains("vm") && names.Contains("native") && names.Contains("help"));
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
            Check("test 子命令齐全（--all/--run/--suite-args/--verbose/--log-to）",
                new[] { "--all", "--run", "--suite-args", "--verbose", "--log-to" }.All(testSubs.Contains));
            var vm = commands.First(c => c.Mask.Name == "vm");
            var vmSubs = vm.SubCommands.Select(s => s.Mask.Name).ToList();
            Check("vm 子命令齐全（--file/--max-steps/--entry-point/--verbose/--log-to）",
                new[] { "--file", "--max-steps", "--entry-point", "--verbose", "--log-to" }.All(vmSubs.Contains));
            var native = commands.First(c => c.Mask.Name == "native");
            var nativeSubs = native.SubCommands.Select(s => s.Mask.Name).ToList();
            Check("native 子命令齐全（--file/--out/--libuv-dir/--verbose/--log-to）",
                new[] { "--file", "--out", "--libuv-dir", "--verbose", "--log-to" }.All(nativeSubs.Contains));
            Check("help 无子命令", help.SubCommands.Count == 0);
            Console.WriteLine();
        }

        public static void TestCommandMatching()
        {
            Console.WriteLine("=== Testing COMMAND matching ===");

            CheckParseOk("compile 匹配", new[] { "compile", "--file", "a.rg" },
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

            CheckParseOk("--file 空格形态多路径", new[] { "compile", "--file", "a.rg", "b.rg" },
                r => r.Get("--file") is { Count: 2 } f && f[0] == "a.rg" && f[1] == "b.rg");
            CheckParseOk("--file= 形态", new[] { "compile", "--file=a.rg" },
                r => r.Get("--file") is { Count: 1 } f && f[0] == "a.rg");
            CheckParseOk("--file= 与空格形态混用", new[] { "compile", "--file=a.rg", "b.rg" },
                r => r.Get("--file") is { Count: 2 });
            CheckParseOk("--dump-ast= 形态", new[] { "compile", "--file", "a.rg", "--dump-ast=o.jsonl" },
                r => r.Get("--dump-ast") is { Count: 1 } d && d[0] == "o.jsonl");
            CheckParseOk("无参子命令", new[] { "compile", "--file", "a.rg", "--parse-only" },
                r => r.Has("--parse-only") && r.Get("--parse-only")!.Count == 0);
            CheckParseOk("--run 零参数合法", new[] { "test", "--run" },
                r => r.Get("--run") is { Count: 0 });
            CheckParseOk("--run 多编号", new[] { "test", "--run", "1", "3" },
                r => r.Get("--run") is { Count: 2 } n && n[0] == "1" && n[1] == "3");
            CheckParseOk("--suite-args 零参数合法", new[] { "test", "--run", "1", "--suite-args" },
                r => r.Has("--suite-args") && r.Get("--suite-args")!.Count == 0);
            CheckParseOk("--suite-args 多值", new[] { "test", "--run", "43", "--suite-args", "0", "100" },
                r => r.Get("--suite-args") is { Count: 2 } a && a[0] == "0" && a[1] == "100"
                    && r.Get("--run") is { Count: 1 } n && n[0] == "43");
            CheckParseOk("--suite-args= 形态", new[] { "test", "--run", "43", "--suite-args=0", "100" },
                r => r.Get("--suite-args") is { Count: 2 } a && a[0] == "0" && a[1] == "100");
            CheckParseOk("--suite-args 在 --run 之前", new[] { "test", "--suite-args", "10", "20", "--run", "43" },
                r => r.Get("--suite-args") is { Count: 2 } a && a[0] == "10" && a[1] == "20"
                    && r.Get("--run") is { Count: 1 } n && n[0] == "43");
            CheckParseOk("--all 与 --suite-args 可同现", new[] { "test", "--all", "--suite-args", "0", "100" },
                r => r.Has("--all") && r.Get("--suite-args") is { Count: 2 });
            CheckParseOk("vm --file 空格形态多路径", new[] { "vm", "--file", "a.bil", "b.bil" },
                r => r.Command.Mask.Name == "vm" && r.Get("--file") is { Count: 2 } f
                    && f[0] == "a.bil" && f[1] == "b.bil");
            CheckParseOk("vm --file= 形态", new[] { "vm", "--file=a.bil" },
                r => r.Get("--file") is { Count: 1 } f && f[0] == "a.bil");
            CheckParseOk("vm --entry-point 空格形态", new[] { "vm", "--file", "a.bil",
                    "--entry-point", "app::$main()@.i32" },
                r => r.Get("--entry-point") is { Count: 1 } e && e[0] == "app::$main()@.i32");
            CheckParseOk("vm --entry-point= 形态", new[] { "vm", "--file=a.bil",
                    "--entry-point=$main()@.i32" },
                r => r.Get("--entry-point") is { Count: 1 } e && e[0] == "$main()@.i32");
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
            CheckParseOk("--suite-args 任意个数（3 个）", new[] { "test", "--run", "1", "--suite-args", "a", "b", "c" },
                r => r.Get("--suite-args")!.Count == 3);
            CheckParseError("--max-steps 缺参数报错", new[] { "vm", "--file", "a.bil", "--max-steps" }, "参数个数");
            CheckParseOk("--max-steps 一个参数", new[] { "vm", "--file", "a.bil", "--max-steps", "100" },
                r => r.Get("--max-steps")!.Count == 1 && r.Get("--max-steps")![0] == "100");
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

            CheckParseError("compile 裸参数报游离", new[] { "compile", "a.rg" }, "游离参数");
            CheckParseError("test 裸参数报游离", new[] { "test", "1" }, "游离参数");
            CheckParseError("子命令前的裸参数报游离", new[] { "compile", "a.rg", "--file", "b" }, "游离参数");
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

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_cli_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var src = Path.Combine(dir, "hello.rg");
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

        // ===== vm 命令端到端 =====
        public static void TestVmCommand()
        {
            Console.WriteLine("=== Testing vm 命令端到端 ===");

            // 缺 --file → 退出码 2
            var missing = RunVm("vm");
            Check("vm 缺 --file 退出码 2", missing.Code == 2);

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_vm_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // 正常执行：println 原样进 stdout
                var (_, module, _) = BilTestHarness.EmitBilUnit(
                    "pub func main(): i32 {\n" +
                    "    core.io.Console.println(\"vm ok\")\n" +
                    "    return 42\n" +
                    "}\n");
                var okPath = Path.Combine(dir, "main.bil");
                File.WriteAllText(okPath, BilWriter.Write(module), new UTF8Encoding(false));
                var ok = RunVm("vm", "--file", okPath);
                Check("vm 正常执行退出码 0", ok.Code == 0);
                Check("vm stdout 精确", ok.Out == "vm ok\n");

                // VM 异常（除零）→ stderr 输出异常信息、退出码 1
                var (_, divModule, _) = BilTestHarness.EmitBilUnit(
                    "pub func main(): i32 {\n" +
                    "    var a: i32 = 1\n" +
                    "    var b: i32 = 0\n" +
                    "    return (a / b)\n" +
                    "}\n");
                var divPath = Path.Combine(dir, "div.bil");
                File.WriteAllText(divPath, BilWriter.Write(divModule), new UTF8Encoding(false));
                var div = RunVm("vm", "--file", divPath);
                Check("vm 异常退出码 1", div.Code == 1);
                Check("vm 异常信息含除零", div.Err.Contains("除以零"));

                // bug13②：运行期无匹配 init（new.indirect，§14.2 运行期解析）
                // → 错误信息走 stderr、退出码非零（不得静默中止退出码 0）
                var noInitPath = Path.Combine(dir, "noinit.bil");
                File.WriteAllText(noInitPath, NoMatchingInitBil, new UTF8Encoding(false));
                var noInit = RunVm("vm", "--file", noInitPath);
                Check("运行期无匹配 init 退出码非零", noInit.Code == 1);
                Check("无匹配 init 错误走 stderr", noInit.Err.Contains("不匹配任何 init"));
                Check("无匹配 init 不污染 stdout", noInit.Out.Length == 0);

                // --max-steps：非法 N → 退出码 2；过小上限 → 退出码 1 且消息含步数；足额上限不改变正常执行
                var badZero = RunVm("vm", "--file", okPath, "--max-steps", "0");
                Check("--max-steps 0 退出码 2", badZero.Code == 2);
                Check("--max-steps 0 提示正整数", badZero.Err.Contains("正整数"));
                var badTok = RunVm("vm", "--file", okPath, "--max-steps", "abc");
                Check("--max-steps 非数字退出码 2", badTok.Code == 2);
                var limited = RunVm("vm", "--file", okPath, "--max-steps", "1");
                Check("--max-steps 1 退出码 1", limited.Code == 1);
                Check("--max-steps 1 消息含步数上限", limited.Err.Contains("步数超过上限"));
                var ample = RunVm("vm", "--file", okPath, "--max-steps", "1000000");
                Check("--max-steps 足额退出码 0", ample.Code == 0);
                Check("--max-steps 足额 stdout 不变", ample.Out == "vm ok\n");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
            Console.WriteLine();
        }

        // ===== §17：BIL 命名空间切分写盘 + --entry-point 端到端 =====
        public static void TestEmitBilSlicesAndEntryPoint()
        {
            Console.WriteLine("=== Testing §17 命名空间切分与 --entry-point 端到端 ===");

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_slice_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // 单入口命名空间源：compile --emit-bil 按命名空间切分多文件——
                // 全局命名空间无内容时不写 base.bil；stdlib 各命名空间成独立切片
                var src = Path.Combine(dir, "app.rg");
                File.WriteAllText(src,
                    "namespace app\n" +
                    "@EntryPoint\n" +
                    "pub func main(): i32 {\n" +
                    "    core.io.Console.println(\"ns entry\")\n" +
                    "    return 0\n" +
                    "}\n");
                var outBase = Path.Combine(dir, "app.bil");
                Check("切分编译退出码 0", RunCompile("compile", "--file", src,
                    "--emit-bil", outBase) == 0);
                Check("命名空间切片已写盘", File.Exists(Path.Combine(dir, "app.app.bil")));
                Check("core 切片已写盘", File.Exists(Path.Combine(dir, "app.core.bil")));
                Check("core.io 切片已写盘", File.Exists(Path.Combine(dir, "app.core.io.bil")));
                Check("空全局切片不落盘", !File.Exists(outBase));

                // 全部切片合并执行：@EntryPoint 命名空间 main 自动选中
                var slices = Directory.GetFiles(dir, "app*.bil");
                var run = RunVm(new[] { "vm", "--file" }.Concat(slices).ToArray());
                Check("切片合并执行退出码 0", run.Code == 0);
                Check("切片合并执行 stdout", run.Out == "ns entry\n");

                // 双入口（命名空间 @EntryPoint + 全局裸 main 约定）：
                // 缺省退出码 2 且提示 --entry-point；显式指定后正常运行
                var src2 = Path.Combine(dir, "global.rg");
                File.WriteAllText(src2,
                    "pub func main(): i32 {\n" +
                    "    core.io.Console.println(\"global main\")\n" +
                    "    return 0\n" +
                    "}\n");
                var outBase2 = Path.Combine(dir, "multi.bil");
                Check("双入口编译退出码 0", RunCompile("compile", "--file", src, src2,
                    "--emit-bil", outBase2) == 0);
                Check("双入口全局切片已写盘", File.Exists(outBase2));
                var multiSlices = Directory.GetFiles(dir, "multi*.bil");
                var blocked = RunVm(new[] { "vm", "--file" }.Concat(multiSlices).ToArray());
                Check("多入口缺省退出码 2", blocked.Code == 2);
                Check("多入口报文提示 --entry-point", blocked.Err.Contains("--entry-point"));
                Check("多入口报文列出候选", blocked.Err.Contains("app::$main()@.i32")
                    && blocked.Err.Contains("$main()@.i32"));
                var picked = RunVm(new[] { "vm", "--file" }.Concat(multiSlices)
                    .Concat(new[] { "--entry-point", "app::$main()@.i32" }).ToArray());
                Check("--entry-point 选中执行退出码 0", picked.Code == 0);
                Check("--entry-point 选中 stdout", picked.Out == "ns entry\n");
                var badPick = RunVm(new[] { "vm", "--file" }.Concat(multiSlices)
                    .Concat(new[] { "--entry-point", "app::$nope()@.i32" }).ToArray());
                Check("--entry-point 非入口符号退出码 2", badPick.Code == 2);
                Check("--entry-point 非入口报文", badPick.Err.Contains("不是 entrypoint"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
            Console.WriteLine();
        }

        // 运行期无匹配 init 的手写模块（bug13② 负例）：OnlyI64 仅有
        // init(x: .i64)，new.indirect 以 i32 实参构造——§14.2 运行期
        // init 表解析失败，经 NoSuchMethod 通道中止
        private const string NoMatchingInitBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"noinit\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_Arg = i32 7\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .type OnlyI64 = class pub {\n" +
            "        .field OnlyI64#x@.i64 pub var\n" +
            "        .method OnlyI64$init(x:.i64)@.void pub init\n" +
            "    }\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "}\n" +
            "\n" +
            "fn(OnlyI64$init(x:.i64)@.void) {\n" +
            "    .args {\n" +
            "        .return = .void,\n" +
            "        .this = OnlyI64,\n" +
            "        x = .i64\n" +
            "    }\n" +
            "    .vars {\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        set.field $x $.this field(OnlyI64#x@.i64)\n" +
            "        ret\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .typeid<OnlyI64> tid,\n" +
            "        .i32 a,\n" +
            "        OnlyI64 o\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        getid.type type(OnlyI64) $tid\n" +
            "        load res(R_Arg) $a\n" +
            "        new.indirect $tid $o [$a]\n" +
            "        ret $a\n" +
            "    }\n" +
            "}\n";

        // 驱动 vm COMMAND 端到端，捕获 stdout/stderr
        private static (int Code, string Out, string Err) RunVm(params string[] args)
        {
            if (!CommandLineParser.TryParse(args, out var result, out var error))
            {
                throw new InvalidOperationException($"测试构造的命令行应解析成功: {error}");
            }
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            try
            {
                int code = new VmCommand().Execute(result!);
                return (code, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
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
            TestVmCommand();
            TestEmitBilSlicesAndEntryPoint();

            Console.WriteLine($"=== CommandLineParser Tests Complete: {passCount} passed, {failCount} failed ===");
            return failCount;
        }
    }
}
