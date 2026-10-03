using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 逃逸型 seq 表达式（体全路径 return@ 外层标签、自身不产值，P3 按
    /// ExpectedType 放开后）的 P4 lowering 回归：StructuredExitRouting 对
    /// Body.ValueType == null 的 LoweredSeqBlock 截断同块死后续（throw
    /// 同款哲学），消除结果局部在 dispatcher route==0 fall-through 静态
    /// 路径上的死读（BIL §21.4）。覆盖：逃逸型初始化变量的端到端正例、
    /// 跨两个值块的 return@ 正例、两条 P3 负例不退化。
    /// </summary>
    public static class EscapingSeqExprTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static ParallelSuiteRunner.SuiteSpec Spec => new(
            "EscapingSeqExpr", Cases, sectionTitle: "EscapingSeqExpr");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestEscapingSeqVarInitEndToEnd", TestEscapingSeqVarInitEndToEnd),
            ("TestEscapingSeqAcrossTwoValueBlocks", TestEscapingSeqAcrossTwoValueBlocks),
            ("TestEscapingSeqRequiresTypeAnnotation", TestEscapingSeqRequiresTypeAnnotation),
            ("TestFallThroughSeqStillRejected", TestFallThroughSeqStillRejected),
        };

        // 正例：逃逸型 seq 表达式初始化变量（var t: i32 = seq { 全路径
        // return@decide }），外层 named seq 产值——修复前 BIL verifier
        // §21.4 拦「$.s1 在赋值前被读取」，修复后过验证且 VM 输出正确
        private static void TestEscapingSeqVarInitEndToEnd()
        {
            TestHarness.Section("逃逸型 seq 表达式：变量初始化端到端");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = seq {\n" +
                "            if (flag) {\n" +
                "                return@decide \"first\"\n" +
                "            } else {\n" +
                "                return@decide \"second\"\n" +
                "            }\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(true))\n" +
                "    core.io.Console.println(pick(false))\n" +
                "    return 0\n" +
                "}\n";
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            CheckNoErrors("逃逸型 seq 变量初始化无诊断", unit);
            BilTestHarness.CheckBilValid("逃逸型 seq BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue("VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            TestHarness.Check("VM 输出 first/second", result.Stdout, "first\nsecond\n");
            TestHarness.CheckTrue("main 返回 0",
                result.ReturnValue is VmI32 exitCode && exitCode.Value == 0,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // 正例（变体 a）：三层嵌套——最内层逃逸型 seq 表达式 return@ 最
        // 外层标签（跨 mid/outer 两个值块，route 逐层 relay）
        private static void TestEscapingSeqAcrossTwoValueBlocks()
        {
            TestHarness.Section("逃逸型 seq 表达式：跨两个值块的 return@");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named outer {\n" +
                "        var a: i32 = seq named mid {\n" +
                "            var b: i32 = seq {\n" +
                "                if (flag) {\n" +
                "                    return@outer \"deep-first\"\n" +
                "                } else {\n" +
                "                    return@outer \"deep-second\"\n" +
                "                }\n" +
                "            }\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(true))\n" +
                "    core.io.Console.println(pick(false))\n" +
                "    return 0\n" +
                "}\n";
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            CheckNoErrors("跨两个值块 return@ 无诊断", unit);
            BilTestHarness.CheckBilValid("跨两个值块 return@ BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue("VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            TestHarness.Check("VM 输出 deep-first/deep-second", result.Stdout,
                "deep-first\ndeep-second\n");
        }

        // 负例不退化：逃逸型 seq 无类型标注（var t = seq { 全逃逸 }，
        // expectedType 缺失）仍报 P3 定型错误
        private static void TestEscapingSeqRequiresTypeAnnotation()
        {
            TestHarness.Section("逃逸型 seq 表达式：负例不退化");
            var (unit, _) = BindUnit(
                "func f(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t = seq {\n" +
                "            if (flag) {\n" +
                "                return@decide \"x\"\n" +
                "            } else {\n" +
                "                return@decide \"y\"\n" +
                "            }\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n");
            TestHarness.CheckSemanticError("逃逸型 seq 无类型标注仍拒绝", unit.Diagnostics,
                "a type annotation is required");
        }

        // 负例不退化：体落穿的不产值 seq 表达式仍报 must produce a value
        private static void TestFallThroughSeqStillRejected()
        {
            var (unit, _) = BindUnit(
                "func f(): i32 {\n" +
                "    return seq { var x = 1 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("落穿不产值 seq 仍拒绝", unit.Diagnostics,
                "seq expression must produce a value");
        }

        // 全管线（Parser → P1 → P2 → P3）驱动（同 BinderTests.BindUnit，
        // 私有设施不可复用，本类自带最小版）
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bodies) BindUnit(
            params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            return (unit, Binder.Bind(unit, declarations));
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
        }
    }
}
