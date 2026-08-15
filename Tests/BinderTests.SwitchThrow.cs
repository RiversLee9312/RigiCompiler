namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== switch 语句/表达式（S7d，SYNTAX §7.2）=====
        private static void TestSwitch()
        {
            TestHarness.Section("P3 Switch");

            // 语句形态：常量值匹配；全分支 return → GuaranteesReturn 升级（无缺返回诊断）
            var (unit, bodies) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (2) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("常量 switch 无诊断", unit);
            TestHarness.Check("常量 switch 语句形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Switch(Param(x,i32), " +
                "[Case(Int(1,i32), [Return(Int(1,i32))]); Case(Int(2,i32), [Return(Int(2,i32))])], " +
                "[Return(Int(0,i32))])])");

            // 混合分类：含 _ 的 case 记 IsPattern（_ 绑为占位，结果 bool）
            var (unit2, bodies2) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (_ > 10) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("混合 switch 无诊断", unit2);
            TestHarness.Check("pattern 分支占位绑定",
                BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Switch(Param(x,i32), " +
                "[Case(Int(1,i32), [Return(Int(1,i32))]); " +
                "CaseP(Binary(CmpGt, Placeholder(i32), Int(10,i32), bool), [Return(Int(2,i32))])], " +
                "[Return(Int(0,i32))])])");
            var switchStmt2 = (BoundSwitchStatement)((BoundBlock)BodyOf(bodies2, "f").Body).Statements[0];
            TestHarness.CheckTrue("IsPattern 分类显式记录",
                !switchStmt2.Cases[0].IsPattern && switchStmt2.Cases[1].IsPattern);
            var placeholder2 = (BoundSwitchPlaceholderExpression)
                ((BoundBinaryExpression)switchStmt2.Cases[1].Match).Left;
            TestHarness.CheckTrue("占位 Selector 回指引用相等（嵌套消歧）",
                ReferenceEquals(placeholder2.Selector, switchStmt2.Selector));

            // 表达式形态：单表达式分支隐式取值，全分支统一产值类型
            var (unit3, bodies3) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 10 }\n" +
                "        (_ > 5) -> { 20 }\n" +
                "        default -> { 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("switch 表达式无诊断", unit3);
            TestHarness.Check("switch 表达式（隐式取值）",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(SwitchExpr(Param(x,i32), " +
                "[Case(Int(1,i32), ValueBlock(_, i32, implicit, [ExprStmt(Int(10,i32))])); " +
                "CaseP(Binary(CmpGt, Placeholder(i32), Int(5,i32), bool), " +
                "ValueBlock(_, i32, implicit, [ExprStmt(Int(20,i32))]))], " +
                "ValueBlock(_, i32, implicit, [ExprStmt(Int(0,i32))]), i32))])");

            // 表达式形态 named：多语句分支显式 return@标签
            var (unit4, bodies4) = BindUnit(
                "func f(x: i32): String {\n" +
                "    return switch (x) named match {\n" +
                "        (1) -> { return@match \"one\" }\n" +
                "        default -> { var y = \"other\"\nreturn@match y }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("named switch 表达式无诊断", unit4);
            TestHarness.Check("named switch 表达式（return@标签）",
                BoundDescribe.Body(BodyOf(bodies4, "f")),
                "Body(f, [y: String], [Return(SwitchExpr(Param(x,i32), " +
                "[Case(Int(1,i32), ValueBlock(match, String, [ReturnValue(match, Str(\"one\",String))]))], " +
                "ValueBlock(match, String, [Decl(y, String, = Str(\"other\",String)); " +
                "ReturnValue(match, Local(y,String))]), String))])");

            // 值块穿透：值块末语句为 switch 且全分支 return@ → 路径终止 +
            // 穿透命中的 return@ 参与外层产值类型收集
            var (unit5, bodies5) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) {\n" +
                "        switch (x) {\n" +
                "            (1) -> { return@_ 1 }\n" +
                "            default -> { return@_ 2 }\n" +
                "        }\n" +
                "    } else { 0 }\n" +
                "}\n");
            CheckNoErrors("值块内 switch 穿透无诊断", unit5);
            TestHarness.CheckTrue("穿透后产值类型为 i32",
                ((BoundIfExpression)((BoundReturnStatement)
                    ((BoundBlock)BodyOf(bodies5, "f").Body).Statements[0]).Value!).Type.Name == "i32");

            // DA：全分支（含 default）都赋值 → 合并后可用
            var (unit6, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var y: i32\n" +
                "    switch (x) {\n" +
                "        (1) -> { y = 1 }\n" +
                "        default -> { y = 0 }\n" +
                "    }\n" +
                "    return y\n" +
                "}\n");
            CheckNoErrors("DA：全分支赋值合并后可用", unit6);

            // 诊断：值匹配非常量
            var (unit7, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var y = 1\n" +
                "    switch (x) {\n" +
                "        (y) -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("值匹配非常量", unit7.Diagnostics,
                "switch value-match case requires a compile-time constant");

            // 诊断：case 常量类型与 selector 不符
            var (unit8, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (\"s\") -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("常量类型与 selector 不符", unit8.Diagnostics,
                "switch case constant type must equal the selector type (got 'String' and 'i32')");

            // 诊断：pattern 结果非 bool
            var (unit9, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (_ + 1) -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("pattern 非 bool", unit9.Diagnostics,
                "switch pattern case must be bool (got 'i32')");

            // 诊断：DA 仅单分支赋值 → 合并后仍未赋值
            var (unit10, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var y: i32\n" +
                "    switch (x) {\n" +
                "        (1) -> { y = 1 }\n" +
                "        default -> { }\n" +
                "    }\n" +
                "    return y\n" +
                "}\n");
            TestHarness.CheckSemanticError("DA：单分支赋值合并后仍报未赋值", unit10.Diagnostics,
                "Use of unassigned local variable 'y'");

            // 诊断：switch 表达式产值类型不一致
            var (unit11, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 1 }\n" +
                "        default -> { \"s\" }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("switch 表达式产值类型不一致", unit11.Diagnostics,
                "switch expression branches produce different types ('i32' and 'String')");
        }

        // ===== throw（S7d，SYNTAX §8；异常根 core.Exception 进 bootstrap）=====
        private static void TestThrow()
        {
            TestHarness.Section("P3 Throw");

            // throw 终止路径：函数仅 throw 即满足「所有路径显式返回」
            var (unit, bodies) = BindUnit(
                "class MyException : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func f(): i32 {\n" +
                "    throw new MyException()\n" +
                "}\n");
            CheckNoErrors("throw 无诊断", unit);
            TestHarness.Check("throw 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Throw(New(MyException, []))])");
            var throwStmt = (BoundThrowStatement)((BoundBlock)BodyOf(bodies, "f").Body).Statements[0];
            TestHarness.CheckTrue("异常表达式定型为用户异常类",
                throwStmt.Exception.Type.Name == "MyException");

            // 异常根 core.Exception 已抽象化（用户裁定）：直接抛根本身被拒
            var (unit2, _) = BindUnit(
                "func g() {\n" +
                "    throw new core.Exception()\n" +
                "}\n");
            TestHarness.CheckSemanticError("直接抛 core.Exception 被拒", unit2.Diagnostics,
                "Cannot construct an instance of abstract type 'Exception'");

            // 诊断：throw 非异常类型
            var (unit3, _) = BindUnit(
                "func f() {\n" +
                "    throw 1\n" +
                "}\n");
            TestHarness.CheckSemanticError("throw 非异常类型", unit3.Diagnostics,
                "Cannot throw 'i32' (not compatible with 'Exception')");
        }
    }
}
