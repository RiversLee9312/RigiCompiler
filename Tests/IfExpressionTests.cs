using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// if 表达式解析测试（roadmap #7 表达式模式，SYNTAX.md §7.1）
    ///
    /// 覆盖：
    /// 1. 基本形式 if (cond) { then } else { else }（分支体统一为代码块；
    ///    「单表达式分支隐式取值」是「块内恰好一条 ExpressionStatement」的语义规则）
    /// 2. 嵌套 if 表达式
    /// 3. 跨行书写
    /// 4. if 表达式作为实参
    /// 5. 多语句分支体：return@_ / named + return@标签 显式产出分支值
    /// 6. 错误用例：缺 (、缺 else（表达式形式必须有 else）
    /// 7. AST 结构断言（块填充 / 类型 / Parent 链 / named 标签）
    /// </summary>
    public class IfExpressionTests
    {
        // ===== 1. 基本形式 =====
        public static void TestBasicIfExpressions()
        {
            TestHarness.Section("Testing Basic if Expressions");

            TestExpr("var r = if (x > 0) { x } else { opposite(x) }",
                "If(Binary(Path(x, []) > Int(0,I32)), [Path(x, [])], [Path(opposite(Path(x, [])), [])])");
            TestExpr("var r = if (flag) { 1 } else { 2 }",
                "If(Path(flag, []), [Int(1,I32)], [Int(2,I32)])");

            TestHarness.Blank();
        }

        // ===== 2. 嵌套 if =====
        public static void TestNestedIfExpressions()
        {
            TestHarness.Section("Testing Nested if Expressions");

            // 分支体是完整代码块：内层 if 要作为分支值须显式 return@_（代码块内
            // 的 if 一律按语句分发，见下条）
            TestExpr("var r = if (a) { return@_ if (b) { 1 } else { 2 } } else { 3 }",
                "If(Path(a, []), [Return@_(If(Path(b, []), [Int(1,I32)], [Int(2,I32)]))], [Int(3,I32)])");

            // 分支体内的嵌套 if 按语句解析（代码块分发）——不隐式取值
            TestExpr("var r = if (a) { if (b) { 1 } else { 2 } } else { 3 }",
                "If(Path(a, []), [IfStmt(Path(b, []), [Int(1,I32)], [Int(2,I32)])], [Int(3,I32)])");

            TestHarness.Blank();
        }

        // ===== 3. 跨行书写 =====
        public static void TestMultiLineIfExpressions()
        {
            TestHarness.Section("Testing Multi-line if Expressions");

            TestExpr("var r = if (x > 0) {\n    x\n} else {\n    opposite(x)\n}",
                "If(Binary(Path(x, []) > Int(0,I32)), [Path(x, [])], [Path(opposite(Path(x, [])), [])])");

            TestHarness.Blank();
        }

        // ===== 4. if 表达式作为实参 =====
        public static void TestIfExpressionAsArgument()
        {
            TestHarness.Section("Testing if Expression as Argument");

            TestExpr("var v = foo(if (c) { 1 } else { 2 })",
                "Path(foo(If(Path(c, []), [Int(1,I32)], [Int(2,I32)])), [])");

            TestHarness.Blank();
        }

        // ===== 5. 多语句分支体（return@_ / named + return@标签，SYNTAX §7.1）=====
        public static void TestMultiStatementBranches()
        {
            TestHarness.Section("Testing if Expression Multi-statement Branches");

            // 多语句分支体：return@_ 显式产出分支值（匿名分支体的默认标签是 _）
            TestExpr("var r = if (x > 0) {\n    logPositive(x)\n    return@_ x\n} else {\n    return@_ opposite(x)\n}",
                "If(Binary(Path(x, []) > Int(0,I32)), " +
                "[Path(logPositive(Path(x, [])), []), Return@_(Path(x, []))], " +
                "[Return@_(Path(opposite(Path(x, [])), []))])");

            // named 命名后 return@标签 穿透内层匿名块
            TestExpr("var r = if (x > 0) named check {\n    seq {\n        return@check x\n    }\n} else {\n    return@check opposite(x)\n}",
                "If(Binary(Path(x, []) > Int(0,I32)), named check, " +
                "[Seq([Return@check(Path(x, []))])], " +
                "[Return@check(Path(opposite(Path(x, [])), []))])");

            TestHarness.Blank();
        }

        // ===== 6. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Testing if Expression Error Cases (expect ParserException)");

            // 缺 (
            TestHarness.CheckParseError("var r = if x > 0 { x } else { y }",
                () => TestHarness.ParseRoot("var r = if x > 0 { x } else { y }"),
                "Expected '(' after if");
            // 缺 else（if 表达式必须包含 else 分支，SYNTAX §7.1）：
            // 源码在 then 块 } 后即结束，层在等 else 时收到 EOF
            TestHarness.CheckParseError("var r = if (x > 0) { x }",
                () => TestHarness.ParseRoot("var r = if (x > 0) { x }"),
                "Unexpected end of file");
            // 分支体是代码块：x else 是两条语句缺分隔，表达式语句后遇关键字报错
            TestHarness.CheckParseError("var r = if (x > 0) { x else { y }",
                () => TestHarness.ParseRoot("var r = if (x > 0) { x else { y }"),
                "Unexpected token after expression statement");
            // named 后缺标签名
            TestHarness.CheckParseError("var r = if (c) named { 1 } else { 2 }",
                () => TestHarness.ParseRoot("var r = if (c) named { 1 } else { 2 }"),
                "Expected label name after 'named'");

            TestHarness.Blank();
        }

        // ===== 7. AST 结构断言（AGENTS §5：快照不作为唯一验证方式）=====
        public static void TestStructuralAssertions()
        {
            TestHarness.Section("Structural Assertions");

            var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(
                "var r = if (x > 0) { x } else { opposite(x) }");
            TestHarness.CheckTrue("Initializer Root 存在", decl.Initializer != null);
            TestHarness.CheckTrue("Initializer 已填充", decl.Initializer!.IsAttached);
            TestHarness.CheckTrue("内容表达式是 IfExpression",
                decl.Initializer.Expression is IfExpressionASTNode);

            var ifExpr = (IfExpressionASTNode)decl.Initializer.Expression;
            TestHarness.CheckTrue("Condition Root 已 Attach", ifExpr.Condition.IsAttached);
            TestHarness.CheckTrue("ThenBody 是代码块且恰好一条语句（单表达式分支）",
                ifExpr.ThenBody.Statements.Count == 1 &&
                ifExpr.ThenBody.Statements[0] is ExpressionStatementASTNode);
            TestHarness.CheckTrue("ElseBody 是代码块且恰好一条语句（单表达式分支）",
                ifExpr.ElseBody.Statements.Count == 1 &&
                ifExpr.ElseBody.Statements[0] is ExpressionStatementASTNode);
            TestHarness.CheckTrue("无 named 时 Label 为 null", ifExpr.Label == null);
            TestHarness.CheckTrue("Condition Root 的 Parent 是 if 节点",
                ReferenceEquals(ifExpr.Condition.Parent, ifExpr));
            TestHarness.CheckTrue("ThenBody 的 Parent 是 if 节点",
                ReferenceEquals(ifExpr.ThenBody.Parent, ifExpr));
            TestHarness.CheckTrue("ElseBody 的 Parent 是 if 节点",
                ReferenceEquals(ifExpr.ElseBody.Parent, ifExpr));
            TestHarness.CheckTrue("if 节点挂在 Initializer Root 下",
                ReferenceEquals(ifExpr.Parent, decl.Initializer));

            // named 标签的结构事实
            var namedDecl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(
                "var r = if (c) named check { return@check 1 } else { return@check 2 }");
            var namedIf = (IfExpressionASTNode)namedDecl.Initializer!.Expression;
            TestHarness.CheckTrue("named 标签写入 Label", namedIf.Label == "check");
            var thenReturn = (ReturnStatementASTNode)namedIf.ThenBody.Statements[0];
            TestHarness.CheckTrue("分支体内 return@标签 的 Label",
                thenReturn.Label == "check");

            TestHarness.Blank();
        }

        // ===== 测试辅助 =====

        // 结构校验：初始化表达式的 AstDescribe 描述串必须与期望完全一致
        private static void TestExpr(string code, string expectedDesc)
        {
            try
            {
                var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(code);
                TestHarness.Check(Label(code), AstDescribe.Expr(decl.Initializer!.Expression), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{Label(code)} => 意外异常", false, ex.Message);
            }
        }

        // 用例标签：被测源码串（多行时 \n 转义显示）
        private static string Label(string code) => code.Replace("\n", "\\n");

        // ===== 入口 =====
        public static int RunAll()
        {
            TestHarness.Reset();

            TestBasicIfExpressions();
            TestNestedIfExpressions();
            TestMultiLineIfExpressions();
            TestIfExpressionAsArgument();
            TestMultiStatementBranches();
            TestErrorCases();
            TestStructuralAssertions();

            return TestHarness.Summary("IfExpression");
        }
    }
}
