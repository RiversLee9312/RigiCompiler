using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// typeOf 表达式解析测试（SYNTAX.md §3.7）
    ///
    /// 覆盖：
    /// 1. typeOf(值)
    /// 2. typeOf(调用/成员等复合表达式)
    /// 3. typeOf 结果参与后缀链
    /// 4. 错误用例：缺 (、缺操作数
    /// 5. AST 结构断言（Root 填充 / 类型 / Parent 链）
    /// </summary>
    public class TypeOfExpressionTests
    {
        // ===== 1. 基本形式 =====
        public static void TestBasicTypeOf()
        {
            CompilerTestTools.Section("Testing Basic typeOf");

            TestExpr("var t = typeOf(box)", "TypeOf(Path(box, []))");
            TestExpr("var t = typeOf(12)", "TypeOf(Int(12,I32))");

            CompilerTestTools.Blank();
        }

        // ===== 2. 复合操作数 =====
        public static void TestComplexOperands()
        {
            CompilerTestTools.Section("Testing typeOf with Complex Operands");

            TestExpr("var t = typeOf(foo(1))", "TypeOf(Path(foo(Int(1,I32)), []))");
            TestExpr("var t = typeOf(obj.field)", "TypeOf(Path(obj, [.field]))");

            CompilerTestTools.Blank();
        }

        // ===== 3. 后缀链 =====
        public static void TestSuffixAfterTypeOf()
        {
            CompilerTestTools.Section("Testing Suffix after typeOf");

            TestExpr("var t = typeOf(box).name", "Path((TypeOf(Path(box, []))), [.name])");

            CompilerTestTools.Blank();
        }

        // ===== 4. 错误用例 =====
        public static void TestErrorCases()
        {
            CompilerTestTools.Section("Testing typeOf Error Cases (expect ParserException)");

            // 缺 (
            CaseAssertions.CheckParseError("var t = typeOf box",
                () => CompilerTestTools.ParseRoot("var t = typeOf box"),
                "Expected '(' after typeOf");
            // 缺操作数
            CaseAssertions.CheckParseError("var t = typeOf()",
                () => CompilerTestTools.ParseRoot("var t = typeOf()"),
                "Unexpected token at start of expression");

            CompilerTestTools.Blank();
        }

        // ===== 5. AST 结构断言（AGENTS §5：快照不作为唯一验证方式）=====
        public static void TestStructuralAssertions()
        {
            CompilerTestTools.Section("Structural Assertions");

            var decl = (VariableDeclarationASTNode)CompilerTestTools.ParseFirstDecl("var t = typeOf(box)");
            CaseAssertions.CheckTrue("Initializer Root 存在", decl.Initializer != null);
            CaseAssertions.CheckTrue("Initializer 已填充", decl.Initializer!.IsAttached);
            CaseAssertions.CheckTrue("内容表达式是 TypeOfExpression",
                decl.Initializer.Expression is TypeOfExpressionASTNode);

            var typeOf = (TypeOfExpressionASTNode)decl.Initializer.Expression;
            CaseAssertions.CheckTrue("Operand Root 已 Attach", typeOf.Operand.IsAttached);
            CaseAssertions.CheckTrue("Operand 内容是路径表达式",
                typeOf.Operand.Expression is PathExpressionASTNode);
            CaseAssertions.CheckTrue("Operand Root 的 Parent 是 typeOf 节点",
                ReferenceEquals(typeOf.Operand.Parent, typeOf));
            CaseAssertions.CheckTrue("typeOf 节点挂在 Initializer Root 下",
                ReferenceEquals(typeOf.Parent, decl.Initializer));

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

        // 用例标签：被测源码串（多行时 \n 转义显示）
        private static string Label(string code) => code.Replace("\n", "\\n");

        // ===== 入口 =====


        internal static TestSuiteData Spec { get; } = new("TypeOf",
        [
            (nameof(TestBasicTypeOf), TestBasicTypeOf),
            (nameof(TestComplexOperands), TestComplexOperands),
            (nameof(TestSuffixAfterTypeOf), TestSuffixAfterTypeOf),
            (nameof(TestErrorCases), TestErrorCases),
            (nameof(TestStructuralAssertions), TestStructuralAssertions),
            (nameof(TestPlaceOf), TestPlaceOf),
        ], sectionTitle: "TypeOf");

        private static void TestPlaceOf()
        {
            CompilerTestTools.Section("placeOf 专用表达式");
            TestExpr("var p = placeOf value", "PlaceOf(Path(value, []))");
            TestExpr("var p = placeOf (value)", "PlaceOf(Group(Path(value, [])))");
            TestExpr("var p = placeOf/*分隔*/(value)", "PlaceOf(Group(Path(value, [])))");
            TestExpr("var p = placeOf (1 + 2)", "PlaceOf(Group(Binary(Int(1,I32) + Int(2,I32))))");
            CaseAssertions.CheckParseError("placeOf(x) 不接受伪调用语法",
                () => CompilerTestTools.ParseRoot("var p = placeOf(x)"), "requires separation");
            TestExpr("var p = placeOf obj.field", "PlaceOf(Path(obj, [.field]))");
            TestExpr("var p = placeOf factory()", "PlaceOf(Path(factory(), []))");
            TestExpr("var p = (placeOf obj).dispose()",
                "Path((Group(PlaceOf(Path(obj, [])))), [.dispose()])");
            var decl = (VariableDeclarationASTNode)CompilerTestTools.ParseFirstDecl("var p = placeOf value");
            var place = (PlaceOfExpressionASTNode)decl.Initializer!.Expression;
            CaseAssertions.CheckTrue("PlaceOf 的父链完整",
                ReferenceEquals(place.Parent, decl.Initializer)
                && ReferenceEquals(place.Operand.Parent, place)
                && ReferenceEquals(place.Operand.Expression.Parent, place.Operand));
            CaseAssertions.CheckParseError("placeOf 缺操作数",
                () => CompilerTestTools.ParseRoot("var p = placeOf"), "Unexpected end of file");
            CaseAssertions.CheckParseError("placeOf 不引入二元优先级",
                () => CompilerTestTools.ParseRoot("var p = placeOf a + b"), "");
        }
    }
}
