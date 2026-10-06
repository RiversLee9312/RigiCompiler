using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Gate;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Passes;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Tests
{
    public static partial class MiddlewareTests
    {
        // Gate 职责；与主文件共享同一类型、字段及生命周期。

        // ===== Gate 门禁 =====

        private static void TestGateRejectsParseError()
        {
            var result = BilGate.Accept("这不是 BIL 文本", "bad.bil");
            CaseAssertions.CheckTrue("解析垃圾被拒绝", !result.IsAccepted);
            CaseAssertions.CheckTrue("解析错误带文件名", result.Errors.Count > 0
                && result.Errors[0].Contains("bad.bil"), result.Errors.FirstOrDefault() ?? "");
        }

        private static void TestGateRejectsVerifierError()
        {
            var result = BilGate.Accept(UndeclaredVarBil, "undeclared.bil");
            CaseAssertions.CheckTrue("验证器违规被拒绝", !result.IsAccepted);
            CaseAssertions.CheckTrue("验证错误提及违规变量", result.Errors.Count > 0
                && result.Errors[0].Contains("$missing"), result.Errors.FirstOrDefault() ?? "");
        }

        // §11.4 收紧：bool 的 bin.and 属类型非法，Gate 门禁必须拒绝
        private const string BoolBitwiseBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_T = bool true,\n" +
            "    R_Zero = i32 0\n" +
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
            "        .bool a,\n" +
            "        .bool b,\n" +
            "        .bool r,\n" +
            "        .i32 x\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_T) $a\n" +
            "        load res(R_T) $b\n" +
            "        bin.and $a $b $r\n" +
            "        load res(R_Zero) $x\n" +
            "        ret $x\n" +
            "    }\n" +
            "}\n";

        private static void TestGateRejectsBoolBitwise()
        {
            var result = BilGate.Accept(BoolBitwiseBil, "boolbit.bil");
            CaseAssertions.CheckTrue("bool bin.and 被门禁拒绝", !result.IsAccepted);
            CaseAssertions.CheckTrue("拒绝消息含 opcode 与类型",
                result.Errors.Any(e => e.Contains("bin.and") && e.Contains(".bool")),
                result.Errors.FirstOrDefault() ?? "");
        }

        private static void TestGateAcceptsValidModule()
        {
            var result = BilGate.Accept(MinimalValidBil, "ok.bil");
            CaseAssertions.CheckTrue("合法模块放行", result.IsAccepted,
                string.Join("; ", result.Errors));
            CaseAssertions.CheckTrue("放行模块非空", result.Module != null);
            CaseAssertions.CheckTrue("放行模块函数数", result.Module!.Functions.Count == 1);

            // 编译器真实产物过门禁（复用中端全管线驱动，杜绝手编样例漂移）
            var (_, emittedTextModule, emittedText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 { return 0 }\n");
            emittedText = BilWriter.Write(emittedTextModule);
            var emitted = BilGate.Accept(emittedText, "emitted.bil");
            CaseAssertions.CheckTrue("编译器产物过门禁", emitted.IsAccepted,
                string.Join("; ", emitted.Errors.Take(3)));
        }

    }
}
