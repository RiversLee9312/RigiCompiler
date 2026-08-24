using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// native 端到端对拍套件（MIDDLEWARE_ARCHITECTURE §10 第 2 条）：同一 BIL
    /// 在 BIL VM（行为参考实现）与 native 产物上的可观察行为一致
    /// （stdout / 退出码）。用例走编译器真实产物（EmitBilUnit 全管线），
    /// 杜绝手编样例漂移。未找到 clang 工具链时整套 skip（不计失败）——
    /// CI 双平台 runner 预装 clang/lld 必跑。
    /// </summary>
    public static class NativeE2ETests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("native 对拍（VM vs 原生可执行）");
            var clang = ToolchainResolver.ResolveClang(null);
            if (clang == null)
            {
                Console.WriteLine("  （跳过：未找到 clang 工具链；" +
                    "开发机跑 tools/Fetch-LlvmToolchain.ps1 后本套件生效）");
                return TestHarness.Summary("NativeE2E");
            }

            RunCase("hello world",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"Hello, world!\")\n" +
                "    return 0\n" +
                "}\n");
            RunCase("字符串拼接",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var greeting = \"Hello, \" + \"rigi\"\n" +
                "    Console.println(greeting + \"!\")\n" +
                "    return 0\n" +
                "}\n");
            RunCase("print 无换行原样输出",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    Console.print(\"ab\")\n" +
                "    Console.print(\"cd\")\n" +
                "    return 0\n" +
                "}\n");
            RunCase("标量退出码",
                "pub func main(): i32 {\n" +
                "    return (6 * 7)\n" +
                "}\n");
            RunCase("if/else 分支",
                "pub func main(): i32 {\n" +
                "    var x = 10\n" +
                "    if (x > 5) { x = 1 } else { x = 2 }\n" +
                "    return x\n" +
                "}\n");
            RunCase("while 求和",
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    var i = 1\n" +
                "    while (i <= 10) {\n" +
                "        sum = sum + i\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            RunCase("do-while 先执行",
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    do {\n" +
                "        x = x + 1\n" +
                "    } while (x < 5)\n" +
                "    return x\n" +
                "}\n");
            RunCase("break/continue",
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    var i = 0\n" +
                "    while (i < 10) {\n" +
                "        i = i + 1\n" +
                "        if (i == 3) { continue }\n" +
                "        if (i > 7) { break }\n" +
                "        sum = sum + i\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            RunCase("嵌套标签 break@outer",
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    while (x < 10) named outer {\n" +
                "        while (x < 5) {\n" +
                "            x = x + 1\n" +
                "            if (x == 3) { break@outer }\n" +
                "            continue\n" +
                "        }\n" +
                "        x = x + 2\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            RunCase("短路求值降 if 块",
                "pub func main(): i32 {\n" +
                "    var a = true\n" +
                "    var b = false\n" +
                "    if ((a and b) or (a and (not b))) { return 7 }\n" +
                "    return 0\n" +
                "}\n");
            RunCase("switch 常量表",
                "pub func classify(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 10 }\n" +
                "        (2) -> { return 20 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return classify(2)\n" +
                "}\n");
            RunCase("pattern switch 降级链（call blk）",
                "pub func main(): i32 {\n" +
                "    var x = 5\n" +
                "    var label = switch (x) {\n" +
                "        (_ > 10) -> { return@_ 1 }\n" +
                "        default -> { return@_ 0 }\n" +
                "    }\n" +
                "    return label\n" +
                "}\n");
            return TestHarness.Summary("NativeE2E");
        }

        // 单用例：源 → 中端全管线 → BIL 文本 → VM 执行 + native 编译执行，
        // 比 stdout（行尾归一）与退出码（main 的 i32 返回）
        private static void RunCase(string label, string source)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var (_, module, _) = BilTestHarness.EmitBilUnit(source);
                var text = BilWriter.Write(module);

                // VM 侧（行为参考实现）
                var vm = BilVm.Run(BilReader.Read(text));
                TestHarness.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                var expectedExit = vm.ReturnValue is VmI32 value ? value.Value : 0;

                // native 侧：CLI 编译 → 进程执行
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
                TestHarness.CheckTrue(label + "：退出码一致",
                    runExit == expectedExit, $"native={runExit} vm={expectedExit}");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // Windows CRT stdout 文本模式把 \n 翻成 \r\n；比对面统一归一
        private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

        // 驱动 native COMMAND 端到端，捕获 stdout/stderr（同 MiddlewareTests 模式）
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
