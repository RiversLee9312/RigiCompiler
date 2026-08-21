using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 泛型解析与 > 系列运算符组合测试
    ///
    /// 背景：
    /// - Lexer 不再合并 > 系列 notation（>>、>=、>>>），
    ///   使嵌套泛型的连续闭合符 >> 成为两个独立 > token；
    /// - 比较/移位运算符（>=、>>、>>>）由 ExpressionParserLayer 在
    ///   运算符状态下重新组合相邻的 >、= token 得到。
    ///
    /// 覆盖：
    /// 1. 嵌套泛型类型引用（两层、三层、可空组合）
    /// 2. > 系列运算符的重新组合（>=、>>、>>>）
    /// 3. 回归：单 >、<=、== 不受影响
    /// 4. 错误用例：移位与比较混用未加括号
    /// 5. AST 结构断言（泛型嵌套结构 / 运算符重组 / Parent 链）
    /// </summary>
    public class GenericParsingTests
    {
        // ===== 1. 嵌套泛型类型引用 =====
        public static void TestNestedGenericTypes()
        {
            TestHarness.Section("Testing Nested Generic Type References");

            // 单层回归
            TestType("var a: List\\<String>", "List<String>");
            // 两层嵌套：末尾 >> 是两个独立闭合符
            TestType("var b: List\\<Map\\<String, i32>>", "List<Map<String,i32>>");
            // 三层嵌套
            TestType("var c: List\\<List\\<List\\<i32>>>", "List<List<List<i32>>>");
            // 嵌套 + 可空
            TestType("var d: List\\<Map\\<String, i32>>?", "List<Map<String,i32>>?");
            // g1：实参内嵌可空（? 挂实参）与内外双可空
            TestType("var f: Map\\<i32?, String?>", "Map<i32?,String?>");
            TestType("var g: Holder\\<i32?>?", "Holder<i32?>?");
            TestType("var h: List\\<Map\\<String, i32?>?>", "List<Map<String,i32?>?>");
            // 嵌套泛型 + 初始化表达式
            TestDecl("var e: List\\<List\\<i32>> = null", "List<List<i32>>", "Null");

            TestHarness.Blank();
        }

        // ===== 1.5 泛型与小于号的无歧义共存 =====
        public static void TestGenericVsLessThan()
        {
            TestHarness.Section("Testing \\< Generics vs < Less-Than");

            // < 现在只是小于号（在 \\< 语法下不再歧义）
            TestExpr("var lt = a < b", "Binary(Path(a, []) < Path(b, []))");
            // 泛型路径引用（不带调用）
            TestExpr("var gp = Span.alloc\\<f32>", "Path(Span, [.alloc<f32>])");
            // 同一行内泛型与小于号共存
            TestExpr("var cmp = x < y", "Binary(Path(x, []) < Path(y, []))");
            // 泛型闭合与比较运算符以空白隔开：仍是比较，不是多余闭合
            TestExpr("var gt = foo\\<i32> > x", "Binary(Path(foo<i32>, []) > Path(x, []))");
            TestExpr("var ge = foo\\<i32> >= x", "Binary(Path(foo<i32>, []) >= Path(x, []))");

            TestHarness.Blank();
        }

        // ===== 2. > 系列运算符组合 =====
        public static void TestCombinedOperators()
        {
            TestHarness.Section("Testing Combined > Operators");

            // >= 由 > 和 = 组合
            TestExpr("var r = a >= b", "Binary(Path(a, []) >= Path(b, []))");
            // >> 由两个 > 组合（括号内）
            TestExpr("var s = (a >> 2)", "Group(Binary(Path(a, []) >> Int(2,I32)))");
            // >>> 由三个 > 组合
            TestExpr("var t = (a >>> 2)", "Group(Binary(Path(a, []) >>> Int(2,I32)))");

            TestHarness.Blank();
        }

        // ===== 3. 回归：其他比较运算符不受影响 =====
        public static void TestRegressionOperators()
        {
            TestHarness.Section("Testing Operator Regression");

            TestExpr("var g = a > b", "Binary(Path(a, []) > Path(b, []))");
            TestExpr("var l = a <= b", "Binary(Path(a, []) <= Path(b, []))");
            TestExpr("var e = a == b", "Binary(Path(a, []) == Path(b, []))");

            TestHarness.Blank();
        }

        // ===== 4. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Testing Error Cases (expect ParserException)");

            // 移位与比较混用，违反"无运算符优先级"
            TestHarness.CheckParseError("var x = (a >> b >= c)",
                () => TestHarness.ParseRoot("var x = (a >> b >= c)"),
                "没有运算符优先级");
            // 非法运算符组合
            TestHarness.CheckParseError("var y = (a >== b)",
                () => TestHarness.ParseRoot("var y = (a >== b)"),
                "Unexpected token at start of expression");
            // 旧语法：裸 < 不再是泛型开启符（\\< 才是）
            TestHarness.CheckParseError("var old: List<String>",
                () => TestHarness.ParseRoot("var old: List<String>"),
                "Expected '=' or line break after type annotation");
            // \ 后必须紧跟 <
            TestHarness.CheckParseError("var w = a \\ b",
                () => TestHarness.ParseRoot("var w = a \\ b"),
                "Expected '<' after '\\'");

            // 全管线负例：类型引用位置多一个 `>`
            TestHarness.CheckParseError("var x: List\\<i32>> = null",
                () => TestHarness.ParseRoot("var x: List\\<i32>> = null"),
                "Expected '=' or line break after type annotation");
            // 全管线负例：类型引用位置闭括号写成 `\>`
            TestHarness.CheckParseError("var x: List\\<i32\\> = null",
                () => TestHarness.ParseRoot("var x: List\\<i32\\> = null"),
                "close with '>', not '\\>'");
            // 全管线负例：表达式泛型调用多一个 `>`（不得静默解析为比较）
            TestHarness.CheckParseError("var x = foo\\<i32>>(1)",
                () => TestHarness.ParseRoot("var x = foo\\<i32>>(1)"),
                "Unexpected extra '>' after generic argument list");
            // 全管线负例：表达式泛型调用闭括号写成 `\>`
            TestHarness.CheckParseError("var x = foo\\<i32\\>(1)",
                () => TestHarness.ParseRoot("var x = foo\\<i32\\>(1)"),
                "close with '>', not '\\>'");
            // 全管线负例：约束位置多一个 `>`
            TestHarness.CheckParseError("class A\\<T extends List\\<i32>>> {}",
                () => TestHarness.ParseRoot("class A\\<T extends List\\<i32>>> {}"),
                "Unexpected token after type name");
            // 约束位置闭括号写成 `\>`
            TestHarness.CheckParseError("class A\\<T extends List\\<i32\\>> {}",
                () => TestHarness.ParseRoot("class A\\<T extends List\\<i32\\>> {}"),
                "close with '>', not '\\>'");

            TestHarness.Blank();
        }

        // ===== 5. AST 结构断言（AGENTS §5：快照不作为唯一验证方式）=====
        public static void TestStructuralAssertions()
        {
            TestHarness.Section("Structural Assertions");

            // 类型侧：嵌套泛型结构
            var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(
                "var e: List\\<List\\<i32>> = null");
            TestHarness.CheckTrue("TypeAnnotation 存在", decl.TypeAnnotation != null);
            TestHarness.CheckTrue("TypeAnnotation 的 Parent 是声明节点",
                ReferenceEquals(decl.TypeAnnotation!.Parent, decl));
            var outerElem = decl.TypeAnnotation!.TypeSymbol.symbol.elements[0];
            TestHarness.CheckTrue("外层类型名是 List", outerElem.name == "List");
            TestHarness.CheckTrue("外层泛型实参数为 1", outerElem.generics.Count == 1);
            var innerElem = outerElem.generics[0].TypeSymbol.symbol.elements[0];
            TestHarness.CheckTrue("内层类型名是 List", innerElem.name == "List");
            TestHarness.CheckTrue("内层泛型实参是 i32",
                innerElem.generics.Count == 1 &&
                innerElem.generics[0].TypeSymbol.symbol.elements[0].name == "i32");
            TestHarness.CheckTrue("Initializer Root 存在且已填充",
                decl.Initializer != null && decl.Initializer.IsAttached);

            // 表达式侧：>= 重组为单个二元运算
            var cmp = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var r = a >= b");
            TestHarness.CheckTrue("内容表达式是 BinaryExpression",
                cmp.Initializer!.Expression is BinaryExpressionASTNode);
            var bin = (BinaryExpressionASTNode)cmp.Initializer.Expression;
            TestHarness.CheckTrue("运算符重组为 >=", bin.Operator == ">=");
            TestHarness.CheckTrue("Left/Right Root 均已 Attach",
                bin.Left.IsAttached && bin.Right.IsAttached);
            TestHarness.CheckTrue("Left Root 的 Parent 是二元节点",
                ReferenceEquals(bin.Left.Parent, bin));
            TestHarness.CheckTrue("Right Root 的 Parent 是二元节点",
                ReferenceEquals(bin.Right.Parent, bin));
            TestHarness.CheckTrue("二元节点挂在 Initializer Root 下",
                ReferenceEquals(bin.Parent, cmp.Initializer));

            TestHarness.Blank();
        }

        // ===== 测试辅助 =====

        // 校验类型标注的 AstDescribe 描述串
        private static void TestType(string code, string expectedType)
        {
            try
            {
                var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(code);
                string actual = decl.TypeAnnotation != null
                    ? AstDescribe.Type(decl.TypeAnnotation) : "<null>";
                TestHarness.Check(Label(code), actual, expectedType);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{Label(code)} => 意外异常", false, ex.Message);
            }
        }

        // 校验初始化表达式的 AstDescribe 描述串
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

        // 同时校验类型标注与初始化表达式（单个用例一次比对）
        private static void TestDecl(string code, string expectedType, string expectedInit)
        {
            try
            {
                var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(code);
                string type = decl.TypeAnnotation != null
                    ? AstDescribe.Type(decl.TypeAnnotation) : "<null>";
                string init = decl.Initializer != null
                    ? AstDescribe.Expr(decl.Initializer.Expression) : "<null>";
                TestHarness.Check(Label(code), $"{type} = {init}", $"{expectedType} = {expectedInit}");
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

            TestNestedGenericTypes();
            TestGenericVsLessThan();
            TestCombinedOperators();
            TestRegressionOperators();
            TestErrorCases();
            TestStructuralAssertions();

            return TestHarness.Summary("GenericParsing");
        }
    }
}
