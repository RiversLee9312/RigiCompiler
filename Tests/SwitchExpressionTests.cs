using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// switch 表达式解析测试（roadmap #8 表达式模式，SYNTAX.md §7.2）
    ///
    /// 覆盖：
    /// 1. 值匹配分支（不含 _ ）
    /// 2. 模式匹配分支（含 _ ，_ 代表被检查的值）
    /// 3. default 分支
    /// 4. 单行书写
    /// 5. 错误用例：缺 default、缺 ->
    /// 6. AST 结构断言（Root 填充 / 类型 / Parent 链）
    /// </summary>
    public class SwitchExpressionTests
    {
        // ===== 1. 值匹配 =====
        public static void TestValueMatch()
        {
            TestHarness.Section("Testing switch Value Match");

            TestExpr("var r = switch(expr) {\n" +
                     "    (1) -> { \"one\" }\n" +
                     "    (2) -> { \"two\" }\n" +
                     "    default -> { \"other\" }\n" +
                     "}",
                "Switch(Sym(expr), [Int(1,I32) -> Str(\"one\"), Int(2,I32) -> Str(\"two\")], " +
                "default -> Str(\"other\"))");

            TestHarness.Blank();
        }

        // ===== 2. 模式匹配 =====
        public static void TestPatternMatch()
        {
            TestHarness.Section("Testing switch Pattern Match");

            TestExpr("var r = switch(n) {\n" +
                     "    (_ > 10) -> { \"big\" }\n" +
                     "    (_ == (3 + 4)) -> { \"seven\" }\n" +
                     "    default -> { \"small\" }\n" +
                     "}",
                "Switch(Sym(n), " +
                "[Binary(Sym(_) > Int(10,I32)) -> Str(\"big\"), " +
                "Binary(Sym(_) == Group(Binary(Int(3,I32) + Int(4,I32)))) -> Str(\"seven\")], " +
                "default -> Str(\"small\"))");

            TestHarness.Blank();
        }

        // ===== 3. 单行书写 =====
        public static void TestSingleLine()
        {
            TestHarness.Section("Testing switch Single-line");

            TestExpr("var r = switch(x) { (1) -> { 1 } default -> { 0 } }",
                "Switch(Sym(x), [Int(1,I32) -> Int(1,I32)], default -> Int(0,I32))");

            TestHarness.Blank();
        }

        // ===== 4. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Testing switch Error Cases (expect ParserException)");

            // 缺 default（switch 表达式必须包含 default 分支，SYNTAX §7.2）
            TestHarness.CheckParseError("var r = switch(x) { (1) -> { 1 } }",
                () => TestHarness.ParseRoot("var r = switch(x) { (1) -> { 1 } }"),
                "switch 表达式必须包含 default 分支");
            // 分支缺 ->
            TestHarness.CheckParseError("var r = switch(x) { (1) { 1 } default -> { 0 } }",
                () => TestHarness.ParseRoot("var r = switch(x) { (1) { 1 } default -> { 0 } }"),
                "Expected '->' after switch case pattern");
            // 缺 selector 的 (
            TestHarness.CheckParseError("var r = switch x { (1) -> { 1 } default -> { 0 } }",
                () => TestHarness.ParseRoot("var r = switch x { (1) -> { 1 } default -> { 0 } }"),
                "Expected '(' after switch");

            TestHarness.Blank();
        }

        // ===== 5. AST 结构断言（AGENTS §5：快照不作为唯一验证方式）=====
        public static void TestStructuralAssertions()
        {
            TestHarness.Section("Structural Assertions");

            var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(
                "var r = switch(x) { (1) -> { 1 } default -> { 0 } }");
            TestHarness.CheckTrue("Initializer Root 存在", decl.Initializer != null);
            TestHarness.CheckTrue("Initializer 已填充", decl.Initializer!.IsAttached);
            TestHarness.CheckTrue("内容表达式是 SwitchExpression",
                decl.Initializer.Expression is SwitchExpressionASTNode);

            var sw = (SwitchExpressionASTNode)decl.Initializer.Expression;
            TestHarness.CheckTrue("Selector Root 已 Attach", sw.Selector.IsAttached);
            TestHarness.CheckTrue("DefaultBody Root 存在且已 Attach",
                sw.DefaultBody != null && sw.DefaultBody.IsAttached);
            TestHarness.CheckTrue("分支数为 1", sw.Cases.Count == 1);
            TestHarness.CheckTrue("分支 Pattern Root 已 Attach", sw.Cases[0].Pattern.IsAttached);
            TestHarness.CheckTrue("分支 Body Root 已 Attach", sw.Cases[0].Body.IsAttached);
            TestHarness.CheckTrue("Selector Root 的 Parent 是 switch 节点",
                ReferenceEquals(sw.Selector.Parent, sw));
            TestHarness.CheckTrue("分支的 Parent 是 switch 节点",
                ReferenceEquals(sw.Cases[0].Parent, sw));
            TestHarness.CheckTrue("DefaultBody 的 Parent 是 switch 节点",
                ReferenceEquals(sw.DefaultBody!.Parent, sw));
            TestHarness.CheckTrue("switch 节点挂在 Initializer Root 下",
                ReferenceEquals(sw.Parent, decl.Initializer));

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

            TestValueMatch();
            TestPatternMatch();
            TestSingleLine();
            TestErrorCases();
            TestStructuralAssertions();

            return TestHarness.Summary("SwitchExpression");
        }
    }
}
