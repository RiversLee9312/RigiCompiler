using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// if 表达式解析测试（roadmap #7 表达式模式，SYNTAX.md §7.1）
    ///
    /// 覆盖：
    /// 1. 基本形式 if (cond) { then } else { else }
    /// 2. 嵌套 if 表达式
    /// 3. 跨行书写
    /// 4. if 表达式作为实参
    /// 5. 错误用例：缺 (、缺 else（表达式形式必须有 else）
    /// 6. AST 结构断言（Root 填充 / 类型 / Parent 链）
    /// </summary>
    public class IfExpressionTests
    {
        // ===== 1. 基本形式 =====
        public static void TestBasicIfExpressions()
        {
            TestHarness.Section("Testing Basic if Expressions");

            TestExpr("var r = if (x > 0) { x } else { opposite(x) }",
                "If(Binary(Sym(x) > Int(0,I32)), Sym(x), Call(Sym(opposite), [Sym(x)]))");
            TestExpr("var r = if (flag) { 1 } else { 2 }",
                "If(Sym(flag), Int(1,I32), Int(2,I32))");

            TestHarness.Blank();
        }

        // ===== 2. 嵌套 if =====
        public static void TestNestedIfExpressions()
        {
            TestHarness.Section("Testing Nested if Expressions");

            TestExpr("var r = if (a) { if (b) { 1 } else { 2 } } else { 3 }",
                "If(Sym(a), If(Sym(b), Int(1,I32), Int(2,I32)), Int(3,I32))");

            TestHarness.Blank();
        }

        // ===== 3. 跨行书写 =====
        public static void TestMultiLineIfExpressions()
        {
            TestHarness.Section("Testing Multi-line if Expressions");

            TestExpr("var r = if (x > 0) {\n    x\n} else {\n    opposite(x)\n}",
                "If(Binary(Sym(x) > Int(0,I32)), Sym(x), Call(Sym(opposite), [Sym(x)]))");

            TestHarness.Blank();
        }

        // ===== 4. if 表达式作为实参 =====
        public static void TestIfExpressionAsArgument()
        {
            TestHarness.Section("Testing if Expression as Argument");

            TestExpr("var v = foo(if (c) { 1 } else { 2 })",
                "Call(Sym(foo), [If(Sym(c), Int(1,I32), Int(2,I32))])");

            TestHarness.Blank();
        }

        // ===== 5. 错误用例 =====
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
            // 缺 then 分支 }
            TestHarness.CheckParseError("var r = if (x > 0) { x else { y }",
                () => TestHarness.ParseRoot("var r = if (x > 0) { x else { y }"),
                "Expected '}' to close if-then branch");

            TestHarness.Blank();
        }

        // ===== 6. AST 结构断言（AGENTS §5：快照不作为唯一验证方式）=====
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
            TestHarness.CheckTrue("Then Root 已 Attach", ifExpr.ThenExpression.IsAttached);
            TestHarness.CheckTrue("Else Root 已 Attach", ifExpr.ElseExpression.IsAttached);
            TestHarness.CheckTrue("Condition Root 的 Parent 是 if 节点",
                ReferenceEquals(ifExpr.Condition.Parent, ifExpr));
            TestHarness.CheckTrue("Then Root 的 Parent 是 if 节点",
                ReferenceEquals(ifExpr.ThenExpression.Parent, ifExpr));
            TestHarness.CheckTrue("Else Root 的 Parent 是 if 节点",
                ReferenceEquals(ifExpr.ElseExpression.Parent, ifExpr));
            TestHarness.CheckTrue("if 节点挂在 Initializer Root 下",
                ReferenceEquals(ifExpr.Parent, decl.Initializer));

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
            TestErrorCases();
            TestStructuralAssertions();

            return TestHarness.Summary("IfExpression");
        }
    }
}
