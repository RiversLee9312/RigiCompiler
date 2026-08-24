using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Middleware;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// Middleware MW0 套件：
    /// - Gate 门禁（BIL §23：解析错误与验证错误的 BIL 必须被拒，错误人类可读）；
    /// - 驻留符号表（类型/成员登记、外部引用、驻留=引用相等）；
    /// - 空模块 .o 发射（LLVMSharp 进程内管线冒烟，工具链契约）；
    /// - native CLI 端到端（参数校验、拒绝路径、发射落盘、stdout 纯净）。
    /// </summary>
    public static class MiddlewareTests
    {
        // 合法的最小手写模块（符号表用例覆盖类型/成员/外部引用/全局函数）
        private const string MinimalValidBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"symtest\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .type Point = class pub {\n" +
            "        .field Point#x@.i32 pub var\n" +
            "        .field Point#y@.i32 pub var\n" +
            "    }\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "    .type core.String = class pub {\n" +
            "        .method core.String$get_Length()@.i32 pub\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .i32 a\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_Zero) $a\n" +
            "        ret $a\n" +
            "    }\n" +
            "}\n";

        // 验证器必拒的手写模块：ret 引用未声明变量 $missing（§21 违规）
        private const string UndeclaredVarBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        ret $missing\n" +
            "    }\n" +
            "}\n";

        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("Middleware Gate 门禁");
            TestGateRejectsParseError();
            TestGateRejectsVerifierError();
            TestGateAcceptsValidModule();
            TestHarness.Section("Middleware 驻留符号表");
            TestSymbolTable();
            TestHarness.Section("Middleware 空模块 .o 发射");
            TestObjectEmission();
            TestHarness.Section("native CLI 端到端");
            TestNativeCli();
            return TestHarness.Summary("Middleware");
        }

        // ===== Gate 门禁 =====

        private static void TestGateRejectsParseError()
        {
            var result = BilGate.Accept("这不是 BIL 文本", "bad.bil");
            TestHarness.CheckTrue("解析垃圾被拒绝", !result.IsAccepted);
            TestHarness.CheckTrue("解析错误带文件名", result.Errors.Count > 0
                && result.Errors[0].Contains("bad.bil"), result.Errors.FirstOrDefault() ?? "");
        }

        private static void TestGateRejectsVerifierError()
        {
            var result = BilGate.Accept(UndeclaredVarBil, "undeclared.bil");
            TestHarness.CheckTrue("验证器违规被拒绝", !result.IsAccepted);
            TestHarness.CheckTrue("验证错误提及违规变量", result.Errors.Count > 0
                && result.Errors[0].Contains("$missing"), result.Errors.FirstOrDefault() ?? "");
        }

        private static void TestGateAcceptsValidModule()
        {
            var result = BilGate.Accept(MinimalValidBil, "ok.bil");
            TestHarness.CheckTrue("合法模块放行", result.IsAccepted,
                string.Join("; ", result.Errors));
            TestHarness.CheckTrue("放行模块非空", result.Module != null);
            TestHarness.CheckTrue("放行模块函数数", result.Module!.Functions.Count == 1);

            // 编译器真实产物过门禁（复用中端全管线驱动，杜绝手编样例漂移）
            var (_, _, emittedText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 { return 0 }\n");
            var emitted = BilGate.Accept(emittedText, "emitted.bil");
            TestHarness.CheckTrue("编译器产物过门禁", emitted.IsAccepted,
                string.Join("; ", emitted.Errors.Take(3)));
        }

        // ===== 驻留符号表 =====

        private static void TestSymbolTable()
        {
            var gate = BilGate.Accept(MinimalValidBil, "ok.bil");
            var context = new MwContext(gate.Module!);
            var symbols = context.Symbols;

            var point = symbols.FindType("Point");
            TestHarness.CheckTrue("类型已登记", point != null);
            TestHarness.CheckTrue("本地类型非外部", point is { IsExternal: false });
            TestHarness.CheckTrue("类型成员数", point!.Members.Count == 2);
            TestHarness.CheckTrue("类型成员 canonical", point.Members[0].Canonical == "Point#x@.i32");

            var memberX = symbols.FindMember("Point#x@.i32");
            TestHarness.CheckTrue("成员已登记", memberX != null);
            TestHarness.CheckTrue("驻留=引用相等（类型表↔成员表同一对象）",
                ReferenceEquals(point.Members[0], memberX));
            TestHarness.CheckTrue("成员 Owner 回指宿主类型",
                ReferenceEquals(memberX!.Owner, point));
            TestHarness.CheckTrue("驻留=引用相等（重复查找同一对象）",
                ReferenceEquals(symbols.FindType("Point"), point));

            var external = symbols.FindType("core.String");
            TestHarness.CheckTrue("外部类型已登记", external is { IsExternal: true });
            TestHarness.CheckTrue("外部类型成员已登记",
                symbols.FindMember("core.String$get_Length()@.i32") is { IsExternal: true });

            var main = symbols.FindMember("$main()@.i32");
            TestHarness.CheckTrue("全局函数已登记", main != null);
            TestHarness.CheckTrue("全局函数 Owner 为 null", main!.Owner == null);
            TestHarness.CheckTrue("GlobalMembers 含全局函数",
                symbols.GlobalMembers.Any(m => m.Canonical == "$main()@.i32"));

            TestHarness.CheckTrue("未登记符号查找为 null",
                symbols.FindType("Nope") == null && symbols.FindMember("Nope$f()@.void") == null);
        }

        // ===== 空模块 .o 发射 =====

        private static void TestObjectEmission()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var gate = BilGate.Accept(MinimalValidBil, "ok.bil");
                var context = new MwContext(gate.Module!);
                var objPath = Path.Combine(dir, "symtest.o");

                var ok = ObjectEmitter.TryEmitObject(context, objPath, out var error);
                TestHarness.CheckTrue("空模块 .o 发射成功", ok, error);
                TestHarness.CheckTrue(".o 已落盘且非空",
                    File.Exists(objPath) && new FileInfo(objPath).Length > 0);

                // 目标文件魔数：win-x64 → COFF（前 2 字节 machine 0x8664 小端）；
                // linux-x64 → ELF（0x7F 'E' 'L' 'F'）
                var head = File.ReadAllBytes(objPath).Take(4).ToArray();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    TestHarness.CheckTrue(".o 为 COFF（x64 machine 魔数）",
                        head[0] == 0x64 && head[1] == 0x86,
                        BitConverter.ToString(head));
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    TestHarness.CheckTrue(".o 为 ELF（魔数）",
                        head[0] == 0x7F && head[1] == 0x45 && head[2] == 0x4C && head[3] == 0x46,
                        BitConverter.ToString(head));
                }

                // 失败路径：不存在目录下的输出路径 → false + 可读错误（不抛崩）
                var badPath = Path.Combine(dir, "no_such_dir", "x.o");
                var fail = ObjectEmitter.TryEmitObject(context, badPath, out var failError);
                TestHarness.CheckTrue("不可写路径发射返回 false", !fail);
                TestHarness.CheckTrue("不可写路径错误可读", failError.Length > 0);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ===== native CLI 端到端 =====

        private static void TestNativeCli()
        {
            var missingFile = RunNative("native");
            TestHarness.CheckTrue("native 缺 --file 退出码 2", missingFile.Code == 2);

            var missingOut = RunNative("native", "--file", "x.bil");
            TestHarness.CheckTrue("native 缺 --out 退出码 2", missingOut.Code == 2);

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_cli_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var badPath = Path.Combine(dir, "bad.bil");
                File.WriteAllText(badPath, UndeclaredVarBil, new UTF8Encoding(false));
                var rejected = RunNative("native", "--file", badPath,
                    "--out", Path.Combine(dir, "bad.o"));
                TestHarness.CheckTrue("native 非法 BIL 退出码 1", rejected.Code == 1);
                TestHarness.CheckTrue("native 非法 BIL 错误走 stderr", rejected.Err.Contains("$missing"));
                TestHarness.CheckTrue("native 拒绝后不产 .o",
                    !File.Exists(Path.Combine(dir, "bad.o")));

                var okPath = Path.Combine(dir, "ok.bil");
                File.WriteAllText(okPath, MinimalValidBil, new UTF8Encoding(false));
                var objPath = Path.Combine(dir, "app.o");
                var accepted = RunNative("native", "--file", okPath, "--out", objPath);
                TestHarness.CheckTrue("native 合法 BIL 退出码 0", accepted.Code == 0, accepted.Err);
                TestHarness.CheckTrue("native 发射 .o 落盘",
                    File.Exists(objPath) && new FileInfo(objPath).Length > 0);
                TestHarness.CheckTrue("native stdout 纯净", accepted.Out.Length == 0,
                    accepted.Out);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // 驱动 native COMMAND 端到端，捕获 stdout/stderr（同 vm 套件模式）
        private static (int Code, string Out, string Err) RunNative(params string[] args)
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
                int code = new NativeCommand().Execute(result!);
                return (code, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        }
    }
}
