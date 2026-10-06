using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// switch 解析测试（roadmap #8，SYNTAX.md §7.2：表达式与语句两种形态）
    ///
    /// 覆盖：
    /// 1. 值匹配分支（不含 _ ）
    /// 2. 模式匹配分支（含 _ ，_ 代表被检查的值）
    /// 3. default 分支
    /// 4. 单行书写
    /// 5. 表达式形态：多语句分支体（return@_ / named + return@标签）
    /// 6. 语句形态：代码块内 switch 语句（含多语句分支体）
    /// 7. 错误用例：缺 default（两种形态同规则）、缺 ->
    /// 8. AST 结构断言（块填充 / 类型 / Parent 链 / named 标签）
    ///
    /// 分支体统一为代码块：「单表达式分支隐式取值」是「块内恰好一条
    /// ExpressionStatement」的语义规则，解析层无特判。
    /// </summary>
    public class SwitchExpressionTests
    {
        // ===== 1. 值匹配 =====
        public static void TestValueMatch()
        {
            CompilerTestTools.Section("Testing switch Value Match");

            TestExpr("var r = switch(expr) {\n" +
                     "    (1) -> { \"one\" }\n" +
                     "    (2) -> { \"two\" }\n" +
                     "    default -> { \"other\" }\n" +
                     "}",
                "Switch(Path(expr, []), [Int(1,I32) -> [Str(\"one\")], Int(2,I32) -> [Str(\"two\")]], " +
                "default -> [Str(\"other\")])");

            CompilerTestTools.Blank();
        }

        // ===== 2. 模式匹配 =====
        public static void TestPatternMatch()
        {
            CompilerTestTools.Section("Testing switch Pattern Match");

            TestExpr("var r = switch(n) {\n" +
                     "    (_ > 10) -> { \"big\" }\n" +
                     "    (_ == (3 + 4)) -> { \"seven\" }\n" +
                     "    default -> { \"small\" }\n" +
                     "}",
                "Switch(Path(n, []), " +
                "[Binary(Path(_, []) > Int(10,I32)) -> [Str(\"big\")], " +
                "Binary(Path(_, []) == Group(Binary(Int(3,I32) + Int(4,I32)))) -> [Str(\"seven\")]], " +
                "default -> [Str(\"small\")])");

            CompilerTestTools.Blank();
        }

        // ===== 3. 单行书写 =====
        public static void TestSingleLine()
        {
            CompilerTestTools.Section("Testing switch Single-line");

            TestExpr("var r = switch(x) { (1) -> { 1 } default -> { 0 } }",
                "Switch(Path(x, []), [Int(1,I32) -> [Int(1,I32)]], default -> [Int(0,I32)])");

            CompilerTestTools.Blank();
        }

        // ===== 4. 表达式形态：多语句分支体 + named（SYNTAX §7.2）=====
        public static void TestMultiStatementCaseBodies()
        {
            CompilerTestTools.Section("Testing switch Expression Multi-statement Case Bodies");

            // 多语句分支体：return@_ 显式产出分支值（匿名分支体的默认标签是 _）
            TestExpr("var r = switch(x) {\n" +
                     "    (1) -> { return@_ \"one\" }\n" +
                     "    (_ > 10) -> {\n" +
                     "        logBig(x)\n" +
                     "        return@_ \"big\"\n" +
                     "    }\n" +
                     "    default -> { return@_ \"other\" }\n" +
                     "}",
                "Switch(Path(x, []), " +
                "[Int(1,I32) -> [Return@_(Str(\"one\"))], " +
                "Binary(Path(_, []) > Int(10,I32)) -> [Path(logBig(Path(x, [])), []), Return@_(Str(\"big\"))]], " +
                "default -> [Return@_(Str(\"other\"))])");

            // named 命名后 return@标签 穿透内层匿名块
            TestExpr("var r = switch(x) named match {\n" +
                     "    (1) -> { return@_ \"one\" }\n" +
                     "    (_ > 10) -> {\n" +
                     "        seq { return@match \"big\" }\n" +
                     "    }\n" +
                     "    default -> { return@match \"other\" }\n" +
                     "}",
                "Switch(Path(x, []), named match, " +
                "[Int(1,I32) -> [Return@_(Str(\"one\"))], " +
                "Binary(Path(_, []) > Int(10,I32)) -> [Seq([Return@match(Str(\"big\"))])]], " +
                "default -> [Return@match(Str(\"other\"))])");

            CompilerTestTools.Blank();
        }

        // ===== 5. 语句形态（SYNTAX §7.2：结果值被丢弃，分支体为完整代码块）=====
        public static void TestSwitchStatement()
        {
            CompilerTestTools.Section("Testing switch Statement Form");

            // 基本语句形态
            TestBlock("{ switch(x) { (1) -> { handleOne() } default -> { handleOther() } } }",
                "[SwitchStmt(Path(x, []), [Int(1,I32) -> [Path(handleOne(), [])]], " +
                "default -> [Path(handleOther(), [])])]");

            // 多语句分支体 + 语句结束后正确交还 token
            TestBlock("{\n" +
                      "    switch(expr) {\n" +
                      "        (_ > 10) -> {\n" +
                      "            logBig(expr)\n" +
                      "            handleBig()\n" +
                      "        }\n" +
                      "        default -> { handleOther() }\n" +
                      "    }\n" +
                      "    done()\n" +
                      "}",
                "[SwitchStmt(Path(expr, []), " +
                "[Binary(Path(_, []) > Int(10,I32)) -> [Path(logBig(Path(expr, [])), []), Path(handleBig(), [])]], " +
                "default -> [Path(handleOther(), [])]), " +
                "Path(done(), [])]");

            CompilerTestTools.Blank();
        }

        // ===== 6. 错误用例 =====
        public static void TestErrorCases()
        {
            CompilerTestTools.Section("Testing switch Error Cases (expect ParserException)");

            // 缺 default（两种形态都必须包含 default 分支，SYNTAX §7.2）
            CaseAssertions.CheckParseError("var r = switch(x) { (1) -> { 1 } }",
                () => CompilerTestTools.ParseRoot("var r = switch(x) { (1) -> { 1 } }"),
                "switch 必须包含 default 分支");
            CaseAssertions.CheckParseError("{ switch(x) { (1) -> { 1 } } }（语句形态缺 default）",
                () => CompilerTestTools.ParseBlock("{ switch(x) { (1) -> { 1 } } }"),
                "switch 必须包含 default 分支");
            // 分支缺 ->
            CaseAssertions.CheckParseError("var r = switch(x) { (1) { 1 } default -> { 0 } }",
                () => CompilerTestTools.ParseRoot("var r = switch(x) { (1) { 1 } default -> { 0 } }"),
                "Expected '->' after switch case pattern");
            // 缺 selector 的 (
            CaseAssertions.CheckParseError("var r = switch x { (1) -> { 1 } default -> { 0 } }",
                () => CompilerTestTools.ParseRoot("var r = switch x { (1) -> { 1 } default -> { 0 } }"),
                "Expected '(' after switch");
            // named 后缺标签名
            CaseAssertions.CheckParseError("var r = switch(x) named { (1) -> { 1 } default -> { 0 } }",
                () => CompilerTestTools.ParseRoot("var r = switch(x) named { (1) -> { 1 } default -> { 0 } }"),
                "Expected label name after 'named'");

            CompilerTestTools.Blank();
        }

        // ===== 7. AST 结构断言（AGENTS §5：快照不作为唯一验证方式）=====
        public static void TestStructuralAssertions()
        {
            CompilerTestTools.Section("Structural Assertions");

            // --- 表达式形态 ---
            var decl = (VariableDeclarationASTNode)CompilerTestTools.ParseFirstDecl(
                "var r = switch(x) { (1) -> { 1 } default -> { 0 } }");
            CaseAssertions.CheckTrue("Initializer Root 存在", decl.Initializer != null);
            CaseAssertions.CheckTrue("Initializer 已填充", decl.Initializer!.IsAttached);
            CaseAssertions.CheckTrue("内容表达式是 SwitchExpression",
                decl.Initializer.Expression is SwitchExpressionASTNode);

            var sw = (SwitchExpressionASTNode)decl.Initializer.Expression;
            CaseAssertions.CheckTrue("Selector Root 已 Attach", sw.Selector.IsAttached);
            CaseAssertions.CheckTrue("DefaultBody 存在且恰好一条语句（单表达式分支）",
                sw.DefaultBody != null && sw.DefaultBody.Statements.Count == 1 &&
                sw.DefaultBody.Statements[0] is ExpressionStatementASTNode);
            CaseAssertions.CheckTrue("分支数为 1", sw.Cases.Count == 1);
            CaseAssertions.CheckTrue("分支 Pattern Root 已 Attach", sw.Cases[0].Pattern.IsAttached);
            CaseAssertions.CheckTrue("分支 Body 是代码块且恰好一条语句（单表达式分支）",
                sw.Cases[0].Body.Statements.Count == 1 &&
                sw.Cases[0].Body.Statements[0] is ExpressionStatementASTNode);
            CaseAssertions.CheckTrue("无 named 时 Label 为 null", sw.Label == null);
            CaseAssertions.CheckTrue("Selector Root 的 Parent 是 switch 节点",
                ReferenceEquals(sw.Selector.Parent, sw));
            CaseAssertions.CheckTrue("分支的 Parent 是 switch 节点",
                ReferenceEquals(sw.Cases[0].Parent, sw));
            CaseAssertions.CheckTrue("DefaultBody 的 Parent 是 switch 节点",
                ReferenceEquals(sw.DefaultBody!.Parent, sw));
            CaseAssertions.CheckTrue("switch 节点挂在 Initializer Root 下",
                ReferenceEquals(sw.Parent, decl.Initializer));

            // --- 语句形态 ---
            var block = CompilerTestTools.ParseBlock(
                "{ switch(x) { (1) -> { a()\n b() } default -> { c() } } }");
            CaseAssertions.CheckTrue("块内首条语句是 SwitchStatement",
                block.Statements.Count == 1 && block.Statements[0] is SwitchStatementASTNode);
            var stmt = (SwitchStatementASTNode)block.Statements[0];
            CaseAssertions.CheckTrue("语句 Selector Root 已 Attach", stmt.Selector.IsAttached);
            CaseAssertions.CheckTrue("语句 DefaultBody 存在", stmt.DefaultBody != null);
            CaseAssertions.CheckTrue("语句分支数为 1", stmt.Cases.Count == 1);
            CaseAssertions.CheckTrue("语句 Selector Root 的 Parent 是 switch 语句节点",
                ReferenceEquals(stmt.Selector.Parent, stmt));
            CaseAssertions.CheckTrue("语句分支的 Parent 是 switch 语句节点",
                ReferenceEquals(stmt.Cases[0].Parent, stmt));
            CaseAssertions.CheckTrue("语句 DefaultBody 的 Parent 是 switch 语句节点",
                ReferenceEquals(stmt.DefaultBody!.Parent, stmt));
            CaseAssertions.CheckTrue("switch 语句节点挂在代码块下",
                ReferenceEquals(stmt.Parent, block));
            CaseAssertions.CheckTrue("语句分支体是完整代码块（可写多条语句）",
                stmt.Cases[0].Body.Statements.Count == 2);

            // named 标签的结构事实（表达式形态）
            var namedDecl = (VariableDeclarationASTNode)CompilerTestTools.ParseFirstDecl(
                "var r = switch(x) named match { (1) -> { return@match 1 } default -> { return@match 0 } }");
            var namedSw = (SwitchExpressionASTNode)namedDecl.Initializer!.Expression;
            CaseAssertions.CheckTrue("named 标签写入 Label", namedSw.Label == "match");

            CompilerTestTools.Blank();
        }

        // ===== 测试辅助 =====

        // 结构校验：初始化表达式的 AstDescribe 描述串必须与期望完全一致
        private static void TestExpr(string code, string expectedDesc)
        {
            try
            {
                var decl = (VariableDeclarationASTNode)CompilerTestTools.ParseFirstDecl(code);
                CaseAssertions.Check(Label(code), AstDescribe.Expr(decl.Initializer!.Expression), expectedDesc);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"{Label(code)} => 意外异常", false, ex.Message);
            }
        }

        // 语句形态校验：代码块独立驱动，比对 AstDescribe.Block 描述串
        private static void TestBlock(string code, string expectedDesc)
        {
            try
            {
                var block = CompilerTestTools.ParseBlock(code);
                CaseAssertions.Check(Label(code), AstDescribe.Block(block), expectedDesc);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"{Label(code)} => 意外异常", false, ex.Message);
            }
        }

        // 用例标签：被测源码串（多行时 \n 转义显示）
        private static string Label(string code) => code.Replace("\n", "\\n");

        // ===== 入口 =====


        internal static TestSuiteData Spec { get; } = new("SwitchExpression",
        [
            (nameof(TestValueMatch), TestValueMatch),
            (nameof(TestPatternMatch), TestPatternMatch),
            (nameof(TestSingleLine), TestSingleLine),
            (nameof(TestMultiStatementCaseBodies), TestMultiStatementCaseBodies),
            (nameof(TestSwitchStatement), TestSwitchStatement),
            (nameof(TestErrorCases), TestErrorCases),
            (nameof(TestStructuralAssertions), TestStructuralAssertions),
        ], sectionTitle: "SwitchExpression");
    }
}
