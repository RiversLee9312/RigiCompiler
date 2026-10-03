using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 逃逸型 seq 表达式边角位置矩阵（遗留 4a/4b）。
    ///
    /// 4a——逃逸型 seq/if 表达式在条件位：P3 以 bool 期望类型给条件位
    /// 逃逸表达式定型（while/do-while/if 语句/if 表达式四处条件绑定
    /// 点），P4 侧 StructuredExitRouting 对循环 Judge 协议块抑制「逃逸
    /// region 同块后续截断」（条件写回是结构性指令，砍了即坏 BIL），
    /// LoopRewriter 把逃逸条件的写回改写为 false 字面量（写回动态不可
    /// 达、值任意，原样保留会读永不赋值的结果局部、§21.4 死读）。
    /// 覆盖：while 条件位逃逸方式（return@外层值块/throw——裸 return
    /// 穿透值块已按 §6.1 裁决禁止，钉负例）、
    /// do-while 条件位、多路径逃逸 seq 条件（route 经 loop region relay）、
    /// 逃逸 if 表达式条件位、if 语句条件位、混合 seq 条件位（hint 回归）。
    ///
    /// 4a 修不动的形态（无期望类型可传——for iterable 的元素类型、switch
    /// selector 的匹配类型、二元操作数、插值段类型都由表达式自身决定）
    /// 保持清晰 P3 诊断，负例钉住。
    ///
    /// 4b——逃逸 region 截断只到同块边界，外层块后续静态死但仍发射：
    /// 后 hint 时代「0 组为空 ⇒ 续点静态不可达」自动干净。刁难 e2e：
    /// 内层块内逃逸声明后读未赋值变量 + 块后死语句、else-if 透明包装
    /// 多层嵌套 + 死中套死 seq、死语句含赋值/调用/throw——副作用绝不执行。
    /// </summary>
    public static class EscapingSeqPositionTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static ParallelSuiteRunner.SuiteSpec Spec => new(
            "EscapingSeqPosition", Cases, sectionTitle: "EscapingSeqPosition");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestWhileConditionReturnAtLabel", TestWhileConditionReturnAtLabel),
            ("TestWhileConditionThrow", TestWhileConditionThrow),
            ("TestDoWhileConditionEscape", TestDoWhileConditionEscape),
            ("TestWhileConditionMultiPathEscape", TestWhileConditionMultiPathEscape),
            ("TestWhileConditionEscapingIfExpression", TestWhileConditionEscapingIfExpression),
            ("TestIfStatementConditionEscape", TestIfStatementConditionEscape),
            ("TestWhileConditionMixedSeq", TestWhileConditionMixedSeq),
            ("TestWhileConditionBareReturnRejected", TestWhileConditionBareReturnRejected),
            ("TestForIterableEscapeRejected", TestForIterableEscapeRejected),
            ("TestSwitchSelectorEscapeRejected", TestSwitchSelectorEscapeRejected),
            ("TestBinaryOperandEscapeRejected", TestBinaryOperandEscapeRejected),
            ("TestInterpolationEscapeRejected", TestInterpolationEscapeRejected),
            ("TestDeadFollowUpsAcrossInnerBlock", TestDeadFollowUpsAcrossInnerBlock),
            ("TestDeadFollowUpsMultiLayerNesting", TestDeadFollowUpsMultiLayerNesting),
            ("TestDeadFollowUpsWithSideEffects", TestDeadFollowUpsWithSideEffects),
        };

        // ===== 4a 正例 =====

        // while 条件位逃逸型 seq（return@外层值块）：逃逸发生、body 永不
        // 执行（route 经 loop region 逐层 relay 到外层值块）
        private static void TestWhileConditionReturnAtLabel()
        {
            TestHarness.Section("4a：while 条件位逃逸（return@外层值块）");
            var source =
                "pub func pick(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        while (seq { return@decide \"escaped\" }) {\n" +
                "            core.io.Console.println(\"body\")\n" +
                "        }\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick())\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("while 条件位 return@", source, "escaped\n");
        }

        // while 条件位裸 return 逃逸（SYNTAX §6.1 裁决）：裸 return 不得
        // 穿透值块边界——P3 专门诊断（要离开函数请改用 return@标签 或 throw）
        private static void TestWhileConditionBareReturnRejected()
        {
            TestHarness.Section("4a 负例：while 条件位裸 return 穿透值块拒绝");
            var (unit, _) = BindUnit(
                "func pick(): String {\n" +
                "    while (seq { return \"escaped\" }) {\n" +
                "    }\n" +
                "    return \"after\"\n" +
                "}\n");
            TestHarness.CheckSemanticError("while 条件位裸 return 穿透值块拒绝",
                unit.Diagnostics, "Bare 'return' cannot cross a value block boundary");
        }

        // while 条件位逃逸型 seq（throw）：异常穿透 loop region 被外层
        // try 捕获，body 永不执行
        private static void TestWhileConditionThrow()
        {
            TestHarness.Section("4a：while 条件位逃逸（throw）");
            var source =
                "class Boom : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "pub func pick(): String {\n" +
                "    try {\n" +
                "        while (seq { throw new Boom() }) {\n" +
                "            core.io.Console.println(\"body\")\n" +
                "        }\n" +
                "        return \"after\"\n" +
                "    } catch (e: Boom) {\n" +
                "        return \"caught\"\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick())\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("while 条件位 throw", source, "caught\n");
        }

        // do-while 条件位逃逸：体先执行一次，随后条件求值逃逸
        private static void TestDoWhileConditionEscape()
        {
            TestHarness.Section("4a：do-while 条件位逃逸");
            var source =
                "pub func pick(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        do {\n" +
                "            core.io.Console.println(\"once\")\n" +
                "        } while (seq { return@decide \"escaped\" })\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick())\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("do-while 条件位逃逸", source, "once\nescaped\n");
        }

        // while 条件位多路径逃逸型 seq（体内 if 双分支各 return@decide，
        // 逃逸值依路径不同——route relay 正确性依输出可辨）
        private static void TestWhileConditionMultiPathEscape()
        {
            TestHarness.Section("4a：while 条件位多路径逃逸 seq");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        while (seq {\n" +
                "            if (flag) {\n" +
                "                return@decide \"esc-true\"\n" +
                "            } else {\n" +
                "                return@decide \"esc-false\"\n" +
                "            }\n" +
                "        }) {\n" +
                "            core.io.Console.println(\"body\")\n" +
                "        }\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(true))\n" +
                "    core.io.Console.println(pick(false))\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("while 条件位多路径逃逸", source, "esc-true\nesc-false\n");
        }

        // while 条件位逃逸型 if 表达式（双分支全逃逸、自身不产值）：
        // 同一 Judge 协议保护的姊妹形态
        private static void TestWhileConditionEscapingIfExpression()
        {
            TestHarness.Section("4a：while 条件位逃逸 if 表达式");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        while (if (flag) { return@decide \"esc-true\" } else { return@decide \"esc-false\" }) {\n" +
                "            core.io.Console.println(\"body\")\n" +
                "        }\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(true))\n" +
                "    core.io.Console.println(pick(false))\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("while 条件位逃逸 if 表达式", source, "esc-true\nesc-false\n");
        }

        // if 语句条件位逃逸型 seq：整个 if 静态死（截断不发射），逃逸
        // 照常发生
        private static void TestIfStatementConditionEscape()
        {
            TestHarness.Section("4a：if 语句条件位逃逸");
            var source =
                "pub func pick(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        if (seq { return@decide \"escaped\" }) {\n" +
                "            return@decide \"true-branch\"\n" +
                "        } else {\n" +
                "            return@decide \"false-branch\"\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick())\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("if 语句条件位逃逸", source, "escaped\n");
        }

        // 混合 seq 在 while 条件位（一臂 return@_ 产值、一臂逃逸）：
        // §18.1 hint 已自然覆盖（region 有正常完成路径，无需截断抑制），
        // 本用例钉住回归——true 路径循环两轮后落 "after"、false 路径逃逸
        private static void TestWhileConditionMixedSeq()
        {
            TestHarness.Section("4a：混合 seq 条件位（hint 覆盖回归）");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var n = 0\n" +
                "        while (seq {\n" +
                "            if (flag) {\n" +
                "                return@_ (n < 2)\n" +
                "            } else {\n" +
                "                return@decide \"escaped\"\n" +
                "            }\n" +
                "        }) {\n" +
                "            n = n + 1\n" +
                "        }\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(true))\n" +
                "    core.io.Console.println(pick(false))\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("混合 seq 条件位", source, "after\nescaped\n");
        }

        // ===== 4a 负例（无期望类型可传，保持清晰 P3 诊断）=====

        // for iterable 位：元素类型由 iterable 自身决定，无期望类型可给
        // 逃逸 seq 定型——钉住「a type annotation is required」
        private static void TestForIterableEscapeRejected()
        {
            TestHarness.Section("4a 负例：for iterable 位保持清晰诊断");
            var (unit, _) = BindUnit(
                "func f(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        for (i in seq { return@decide \"escaped\" }) {\n" +
                "        }\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n");
            TestHarness.CheckSemanticError("for iterable 位逃逸 seq 拒绝", unit.Diagnostics,
                "a type annotation is required");
        }

        // switch selector 位：匹配类型由 selector 自身决定——同口径拒绝
        private static void TestSwitchSelectorEscapeRejected()
        {
            TestHarness.Section("4a 负例：switch selector 位保持清晰诊断");
            var (unit, _) = BindUnit(
                "func f(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        switch (seq { return@decide \"escaped\" }) {\n" +
                "            (1) -> { return@decide \"one\" }\n" +
                "            default -> { return@decide \"other\" }\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n");
            TestHarness.CheckSemanticError("switch selector 位逃逸 seq 拒绝", unit.Diagnostics,
                "a type annotation is required");
        }

        // 二元运算左操作数位：操作数类型由运算定型规则决定，无期望类型
        // 下传——同口径拒绝
        private static void TestBinaryOperandEscapeRejected()
        {
            TestHarness.Section("4a 负例：二元运算操作数位保持清晰诊断");
            var (unit, _) = BindUnit(
                "func f(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = (seq { return@decide \"escaped\" }) + 1\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n");
            TestHarness.CheckSemanticError("二元操作数位逃逸 seq 拒绝", unit.Diagnostics,
                "a type annotation is required");
        }

        // 插值段：段类型任意（toString 协议），无期望类型——同口径拒绝
        private static void TestInterpolationEscapeRejected()
        {
            TestHarness.Section("4a 负例：插值段位保持清晰诊断");
            var (unit, _) = BindUnit(
                "func f(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var s: String = \"x=${seq { return@decide \"escaped\" }}\"\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n");
            TestHarness.CheckSemanticError("插值段位逃逸 seq 拒绝", unit.Diagnostics,
                "a type annotation is required");
        }

        // ===== 4b 刁难 e2e：截断边界外的静态死后续 =====

        // 内层 if 块内逃逸型 seq 声明后紧跟读取未赋值变量的语句，块后再
        // 跟死语句——逃逸 region 截断只到内层块边界，外层后续仍发射但
        // 因 hint「0 组为空 ⇒ 续点不可达」对 verifier 干净；VM 断言死
        // 代码副作用绝不执行
        private static void TestDeadFollowUpsAcrossInnerBlock()
        {
            TestHarness.Section("4b：内层块边界外的静态死后续");
            var source =
                "pub func pick(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        if (true) {\n" +
                "            var t: i32 = seq { return@decide \"escaped\" }\n" +
                "            var dead = t + 1\n" +
                "            core.io.Console.println(\"dead-in-block\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"dead-after-block\")\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick())\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("内层块边界外死后续", source, "escaped\n");
        }

        // 多层嵌套（else-if 透明 BoundBlock 包装 + 内层 if）+ 死语句里
        // 再嵌 seq 表达式（死中套死）——全部不执行
        private static void TestDeadFollowUpsMultiLayerNesting()
        {
            TestHarness.Section("4b：多层嵌套 + 死中套死");
            var source =
                "pub func pick(c: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        if (c) {\n" +
                "            core.io.Console.println(\"branch-c\")\n" +
                "        } else if (true) {\n" +
                "            if (true) {\n" +
                "                var t: i32 = seq { return@decide \"escaped\" }\n" +
                "                var dead = t + 1\n" +
                "            }\n" +
                "            var s: String = seq {\n" +
                "                core.io.Console.println(\"dead-nested-seq\")\n" +
                "                return@_ \"x\"\n" +
                "            }\n" +
                "            core.io.Console.println(s)\n" +
                "        }\n" +
                "        core.io.Console.println(\"dead-outer\")\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(false))\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("多层嵌套死中套死", source, "escaped\n");
        }

        // 死语句含赋值/调用/throw——都不执行、不误导 verifier（调用
        // sideEffect 的 println 与 throw 的 Boom 均不得出现）
        private static void TestDeadFollowUpsWithSideEffects()
        {
            TestHarness.Section("4b：死语句含赋值/调用/throw");
            var source =
                "class Boom : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func sideEffect(): i32 {\n" +
                "    core.io.Console.println(\"dead-call\")\n" +
                "    return 1\n" +
                "}\n" +
                "pub func pick(): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var x = 0\n" +
                "        if (true) {\n" +
                "            var t: i32 = seq { return@decide \"escaped\" }\n" +
                "            x = sideEffect()\n" +
                "            throw new Boom()\n" +
                "        }\n" +
                "        return@decide \"after\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick())\n" +
                "    return 0\n" +
                "}\n";
            CheckEndToEnd("死语句副作用不执行", source, "escaped\n");
        }

        // ===== 设施 =====

        // 正例公共断言：全管线零诊断 → 过验证器 → VM 输出精确匹配
        // （死代码副作用以输出里没有它体现）→ main 返回 0
        private static void CheckEndToEnd(string label, string source, string expectedStdout)
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            CheckNoErrors(label + " 无诊断", unit);
            BilTestHarness.CheckBilValid(label + " BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue(label + " VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            TestHarness.Check(label + " VM 输出", result.Stdout, expectedStdout);
            TestHarness.CheckTrue(label + " main 返回 0",
                result.ReturnValue is VmI32 exitCode && exitCode.Value == 0,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // 全管线（Parser → P1 → P2 → P3）驱动（同 EscapingSeqExprTests
        // 的最小版——私有设施不可复用，本类自带）
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
