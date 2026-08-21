using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 值块 ExpectedType 传播（bug1）+ if/switch 表达式逃逸形态（bug2，
    /// 与 seq 同口径放开）+ println 原子性（bug3）回归矩阵。
    /// bug1：return@ 值表达式按目标值块的 ExpectedType 上下文定型
    /// （enum shorthand `.Failed` 经 return@ 可用）——seq/if/switch/
    /// lambda/return 位置/嵌套/payload 七正例 + 两负例。
    /// bug2：if/switch 表达式全分支逃逸（各自无本块产值且全路径 return@
    /// 外层）不报错、按 expectedType 定型；lowering 截断死后续（throw
    /// 同款哲学）；BIL verifier §21.4 的 switch DA 出口改为全分支交集
    /// （恒执行且仅执行一个分支，与 if 同规则）。语句 seq 形态（用户
    /// 字面 repro）与 try/finally 逃逸副作用钉住；lambda 体与 and/or
    /// 右侧的条件求值不算外层终止（守卫负例）；break@ 跨值块边界维持
    /// S7c 拒绝。矩阵第 13/16 项已由 EscapingSeqExprTests 覆盖，不重复。
    /// bug3：println 单次 native print（BIL 形状锁）+ 双协程实跑行完整。
    /// </summary>
    public static class EscapingValueBlockTests
    {
        private const string OutcomeSource =
            "pub enum struct Outcome { }[\n" +
            "    Ok,\n" +
            "    Failed\n" +
            "]\n";

        public static int RunAll()
        {
            TestHarness.Reset();
            // bug1：ExpectedType 传播
            TestReturnAtEnumShorthandSeq();
            TestReturnAtEnumShorthandIfBranch();
            TestReturnAtEnumShorthandSwitchBranch();
            TestReturnAtEnumShorthandLambdaBody();
            TestReturnAtEnumShorthandReturnPosition();
            TestReturnAtEnumShorthandNestedSeq();
            TestReturnAtEnumShorthandPayloadCase();
            TestUnannotatedShorthandStillRejected();
            TestWrongEnumCaseStillRejected();
            // bug2：逃逸终止分析
            TestStatementSeqFormEndToEnd();
            TestEscapingIfExpressionEndToEnd();
            TestEscapingSwitchExpressionEndToEnd();
            TestEscapingSwitchWithoutAnnotation();
            TestLambdaBodyDoesNotTerminateOuter();
            TestConditionalEvaluationSideDoesNotTerminate();
            TestEscapingFinallySideEffectEndToEnd();
            TestBreakAtValueBlockStillRejected();
            // bug3：println 原子性
            TestPrintlnAtomicity();
            return TestHarness.Summary("EscapingValueBlock");
        }

        // ===== bug1：ExpectedType 传播 =====

        // 正例 1：带标注变量初始化的 seq 内 return@label .Failed
        private static void TestReturnAtEnumShorthandSeq()
        {
            TestHarness.Section("bug1：return@ 的 enum shorthand 定型");
            var (unit, _) = BindUnit(OutcomeSource +
                "func f(): Outcome {\n" +
                "    var r: Outcome = seq named a { return@a .Failed }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("seq 内 return@a .Failed 无诊断", unit);
        }

        // 正例 2：同形态在 if 表达式分支（分支值块 ExpectedType 回填生效）
        private static void TestReturnAtEnumShorthandIfBranch()
        {
            var (unit, _) = BindUnit(OutcomeSource +
                "func f(c: bool): Outcome {\n" +
                "    var r: Outcome = if (c) { return@_ .Failed } else { return@_ .Ok }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("if 分支 return@_ .Failed 无诊断", unit);
        }

        // 正例 3：同形态在 switch 表达式分支
        private static void TestReturnAtEnumShorthandSwitchBranch()
        {
            var (unit, _) = BindUnit(OutcomeSource +
                "func f(n: i32): Outcome {\n" +
                "    var r: Outcome = switch (n) {\n" +
                "        (1) -> { return@_ .Ok }\n" +
                "        default -> { return@_ .Failed }\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("switch 分支 return@_ .Failed 无诊断", unit);
        }

        // 正例 4：同形态在带返回类型的 lambda 块体（声明返回类型回填）
        private static void TestReturnAtEnumShorthandLambdaBody()
        {
            var unit = BindUnitWithStdlib(OutcomeSource +
                "func f() {\n" +
                "    var g = func{(): Outcome -> { return@_ .Failed }}\n" +
                "}\n");
            CheckNoErrors("lambda 块体 return@_ .Failed 无诊断", unit);
        }

        // 正例 5：函数返回位置（ReturnType 作 expectedType 穿入 seq）
        private static void TestReturnAtEnumShorthandReturnPosition()
        {
            var (unit, _) = BindUnit(OutcomeSource +
                "func f(): Outcome {\n" +
                "    return seq named x { return@x .Failed }\n" +
                "}\n");
            CheckNoErrors("return 位置 seq return@x .Failed 无诊断", unit);
        }

        // 正例 6：二层嵌套 seq，内层 return@外层 .Failed（外层 ExpectedType
        // 定 shorthand；内层 seq 自身靠标注 i32 定型——逃逸形态）
        private static void TestReturnAtEnumShorthandNestedSeq()
        {
            var (unit, _) = BindUnit(OutcomeSource +
                "func f(): Outcome {\n" +
                "    var r: Outcome = seq named outer {\n" +
                "        var t: i32 = seq { return@outer .Failed }\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("嵌套 seq return@outer .Failed 无诊断", unit);
        }

        // 正例 7：带 payload 的 enum case 经 return@
        private static void TestReturnAtEnumShorthandPayloadCase()
        {
            var (unit, _) = BindUnit(
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "\n" +
                "    pub init(_ -> errorCode)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]\n" +
                "func f(): RequestResult {\n" +
                "    var r: RequestResult = seq named a { return@a .Failed(404) }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("return@a .Failed(404) 无诊断", unit);
        }

        // 负例 8：无标注 var 的 seq 内 return@ .Failed——无期望类型，
        // shorthand 推断维持拒绝
        private static void TestUnannotatedShorthandStillRejected()
        {
            var (unit, _) = BindUnit(OutcomeSource +
                "func f(): Outcome {\n" +
                "    var x = seq named a { return@a .Failed }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("无标注 shorthand 仍拒绝", unit.Diagnostics,
                "Cannot infer the enum type");
        }

        // 负例 9：标注 Outcome 却 return@ 其他 enum 的 case——期望类型
        // 把 shorthand 解析锁定到 Outcome，类型错误保留
        private static void TestWrongEnumCaseStillRejected()
        {
            var (unit, _) = BindUnit(OutcomeSource +
                "enum struct Direction { }[\n" +
                "    North,\n" +
                "    South\n" +
                "]\n" +
                "func f(): Outcome {\n" +
                "    var r: Outcome = seq named a { return@a .North }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("他 enum case 仍拒绝", unit.Diagnostics,
                "Undefined case 'North' on 'Outcome'");
        }

        // ===== bug2：逃逸终止分析 =====

        // 正例 10[e2e]：语句 seq 形态（用户字面 repro——外层 seq named
        // decide 产值，内层语句 seq 的 if 双分支 return@decide）
        private static void TestStatementSeqFormEndToEnd()
        {
            TestHarness.Section("bug2：语句 seq 形态（用户 repro）端到端");
            var source = OutcomeSource +
                "pub func decide(flag: bool): Outcome {\n" +
                "    var ok: Outcome = .Ok\n" +
                "    var failed: Outcome = .Failed\n" +
                "    var result: Outcome = seq named decide {\n" +
                "        seq {\n" +
                "            if (flag) {\n" +
                "                return@decide ok\n" +
                "            } else {\n" +
                "                return@decide failed\n" +
                "            }\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "func report(tag: String, r: Outcome) {\n" +
                "    if (r is .Ok) {\n" +
                "        core.io.Console.println(tag + \":ok\")\n" +
                "    } else {\n" +
                "        core.io.Console.println(tag + \":failed\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    report(\"true\", decide(true))\n" +
                "    report(\"false\", decide(false))\n" +
                "    return 0\n" +
                "}\n";
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            CheckNoErrors("语句 seq 形态无诊断", unit);
            BilTestHarness.CheckBilValid("语句 seq 形态 BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue("VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            TestHarness.Check("VM 输出 true:ok/false:failed", result.Stdout,
                "true:ok\nfalse:failed\n");
        }

        // 正例 11[e2e]：if 表达式逃逸形态（两分支全 return@ 外层，表达式
        // 按标注 i32 定型；lowering 截断结果局部的死读）
        private static void TestEscapingIfExpressionEndToEnd()
        {
            TestHarness.Section("bug2：if 表达式逃逸形态端到端");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = if (flag) {\n" +
                "            return@decide \"first\"\n" +
                "        } else {\n" +
                "            return@decide \"second\"\n" +
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
            CheckNoErrors("逃逸型 if 表达式无诊断", unit);
            BilTestHarness.CheckBilValid("逃逸型 if 表达式 BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue("VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            TestHarness.Check("VM 输出 first/second", result.Stdout, "first\nsecond\n");
        }

        // 正例 12[e2e]：switch 表达式逃逸形态（值匹配 + pattern 两路径——
        // 值匹配产物 LoweredSwitch、pattern 产物 if 链外包 LoweredSeqBlock，
        // 两处截断分别覆盖）
        private static void TestEscapingSwitchExpressionEndToEnd()
        {
            TestHarness.Section("bug2：switch 表达式逃逸形态端到端");
            var valueMatch =
                "pub func pick(n: i32): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = switch (n) {\n" +
                "            (1) -> { return@decide \"one\" }\n" +
                "            (2) -> { return@decide \"two\" }\n" +
                "            default -> { return@decide \"other\" }\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(1))\n" +
                "    core.io.Console.println(pick(2))\n" +
                "    core.io.Console.println(pick(9))\n" +
                "    return 0\n" +
                "}\n";
            var (unit, module, _) = BilTestHarness.EmitBilUnit(valueMatch);
            CheckNoErrors("逃逸型 switch（值匹配）无诊断", unit);
            BilTestHarness.CheckBilValid("逃逸型 switch（值匹配）BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue("VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            TestHarness.Check("VM 输出 one/two/other", result.Stdout, "one\ntwo\nother\n");

            var pattern =
                "pub func pick(n: i32): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = switch (n) {\n" +
                "            (_ > 10) -> { return@decide \"big\" }\n" +
                "            (_ > 0) -> { return@decide \"small\" }\n" +
                "            default -> { return@decide \"neg\" }\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(100))\n" +
                "    core.io.Console.println(pick(5))\n" +
                "    core.io.Console.println(pick(0 - 3))\n" +
                "    return 0\n" +
                "}\n";
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(pattern);
            CheckNoErrors("逃逸型 switch（pattern）无诊断", unit2);
            BilTestHarness.CheckBilValid("逃逸型 switch（pattern）BIL 过验证器", module2);
            var result2 = BilVm.Run(module2);
            TestHarness.CheckTrue("VM 无异常（pattern）", result2.Exception == null,
                result2.Exception?.ToString() ?? "");
            TestHarness.Check("VM 输出 big/small/neg", result2.Stdout, "big\nsmall\nneg\n");
        }

        // 负例：if/switch 逃逸形态无类型标注（expectedType 缺失）维持报错，
        // 口径与 seq 对齐
        private static void TestEscapingSwitchWithoutAnnotation()
        {
            var (unit, _) = BindUnit(
                "func f(n: i32): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t = switch (n) {\n" +
                "            (1) -> { return@decide \"one\" }\n" +
                "            default -> { return@decide \"other\" }\n" +
                "        }\n" +
                "    }\n" +
                "    return result\n" +
                "}\n");
            TestHarness.CheckSemanticError("逃逸型 switch 无标注仍拒绝", unit.Diagnostics,
                "switch expression escapes on all paths without producing a value");
        }

        // 负例 14：lambda 守卫——值块体末语句是 lambda 声明，lambda 体内
        // 的 return@ 只在被调用时发生，不算外层路径终止（外层仍报
        // 终止性错误）
        private static void TestLambdaBodyDoesNotTerminateOuter()
        {
            var (unit, _) = BindUnit(
                "func f(): i32 {\n" +
                "    return seq {\n" +
                "        var g = func{(): i32 -> { return@_ 1 }}\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("lambda 体不算外层终止", unit.Diagnostics,
                "All code paths of a seq expression branch must explicitly return@ a value");
        }

        // 负例 15：条件求值守卫——逃逸型 seq 只出现在 and 右侧（右侧仅
        // 在左侧为真时求值），其逃逸不代表整体不落穿，外层仍报终止性
        // 错误（只看无条件求值侧）。注：and/or 右侧不带期望类型，内层
        // seq 自身同时报「a type annotation is required」，两错误并存
        private static void TestConditionalEvaluationSideDoesNotTerminate()
        {
            var (unit, _) = BindUnit(
                "func f(c: bool): i32 {\n" +
                "    var r: i32 = seq named outer {\n" +
                "        var b: bool = c and seq { return@outer 1 }\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("and 右侧逃逸不算外层终止", unit.Diagnostics,
                "All code paths of a seq expression branch must explicitly return@ a value");
        }

        // 正例 17[e2e]：逃逸路径上 try/finally 的 finally 副作用执行
        // （abrupt completion 经 finally 通道，cleanup 先于产值打印）
        private static void TestEscapingFinallySideEffectEndToEnd()
        {
            TestHarness.Section("bug2：逃逸路径 finally 副作用端到端");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = seq {\n" +
                "            try {\n" +
                "                if (flag) {\n" +
                "                    return@decide \"first\"\n" +
                "                } else {\n" +
                "                    return@decide \"second\"\n" +
                "                }\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"cleanup\")\n" +
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
            CheckNoErrors("逃逸路径 finally 无诊断", unit);
            BilTestHarness.CheckBilValid("逃逸路径 finally BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue("VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            TestHarness.Check("finally 副作用先于产值", result.Stdout,
                "cleanup\nfirst\ncleanup\nsecond\n");
        }

        // 负例 18：break@ 跨值块边界现状钉住——值块标签不在 loop 标签
        // 空间，S7c 以「Undefined loop label」清晰拒绝（现状记录，
        // 非本轮修复对象）
        private static void TestBreakAtValueBlockStillRejected()
        {
            var (unit, _) = BindUnit(
                "func f(): i32 {\n" +
                "    var r: i32 = seq named s {\n" +
                "        while (true) {\n" +
                "            break@s\n" +
                "        }\n" +
                "        return@s 1\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("break@ 值块标签仍拒绝", unit.Diagnostics,
                "Undefined loop label: 's'");
        }

        // ===== bug3：println 原子性 =====

        // 双协程并发 println("A")/println("B")：BIL 层锁定 println 只含
        // 一次 native print 调用（行原子性的结构保证）；VM 实跑断言输出
        // 逐行完整。权衡注记：VM 协程为 eager spawn，无挂起点的协程在
        // spawn 点即跑完，调度确定性导致无法在本环境真实触发交错——
        // 故交错防护以 BIL 形状锁为主、实跑行完整性为辅
        private static void TestPrintlnAtomicity()
        {
            TestHarness.Section("bug3：println 原子性");
            var source =
                "async func printA() {\n" +
                "    core.io.Console.println(\"A\")\n" +
                "}\n" +
                "async func printB() {\n" +
                "    core.io.Console.println(\"B\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = printA()\n" +
                "    var b = printB()\n" +
                "    await a\n" +
                "    await b\n" +
                "    return 0\n" +
                "}\n";
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            CheckNoErrors("println 并发无诊断", unit);
            // println lowering 后单次 native print（形状锁：load 换行 →
            // add 拼接 → 一次 invoke.noret print）
            BilTestHarness.CheckFnShape("println 单次 native print",
                module, "core.io::Console$.static.println(text:.string)@.void",
                ".vars { .string .t0, .string .t1 }\n" +
                "load res(#0) $.t0\n" +
                "add $text $.t0 $.t1\n" +
                "invoke.noret fn(core.io::Console$.static.print(text:.string)@.void) [$.t1]\n" +
                "ret\n");
            BilTestHarness.CheckBilValid("并发 println BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue("VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            // 行完整性：输出集合 ∈ {"A\nB\n", "B\nA\n"}，绝不出现交错
            TestHarness.CheckTrue("每行完整无交错",
                result.Stdout == "A\nB\n" || result.Stdout == "B\nA\n",
                "实际输出: " + result.Stdout.Replace("\n", "\\n"));
        }

        // 全管线（Parser → P1 → P2 → P3）驱动（同 EscapingSeqExprTests
        // 私有最小版——不含 stdlib）
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bodies) BindUnit(
            params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            return (unit, Binder.Bind(unit, declarations));
        }

        // 带 stdlib 的变体（lambda 依赖 core::Func/Action 族）
        private static CompilationUnit BindUnitWithStdlib(string userSource)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.Add(TestHarness.ParseRoot(userSource));
            var unit = new CompilationUnit(roots.ToArray());
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            Binder.Bind(unit, declarations);
            return unit;
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
        }
    }
}
