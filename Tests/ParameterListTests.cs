using System;

namespace RigiCompiler.Tests
{
    // 函数形参列表解析测试（ParameterListParserLayer，roadmap #5）：独立 Layer 驱动
    // （TestHarness.ParseWithLayer），断言 AstDescribe.Params 描述串。
    //
    // 覆盖 SYNTAX.md §4 的形参语法：
    // 1. 普通参数（含泛型类型）
    // 2. 默认参数（含表达式默认值）
    // 3. 位置可变参数 i32...
    // 4. 具名可变参数 named String...
    // 5. 混合与空列表
    // 6. 括号内续行（§1.1，M31）
    // 7. init 参数映射（§9.3，allowMapping，含跨行）
    // 8. 保留参数名（§14.4，method wrapper canonical）：.name 前导点原样入 Name
    // 9. 错误用例：缺类型标注、缺类型、点数不足、缺默认值、named 无 ...（M31）、
    //    保留参数名非标识符/裸 .
    public class ParameterListTests
    {
        // ===== 1. 普通参数 =====
        public static void TestPlainParameters()
        {
            TestHarness.Section("Testing Plain Parameters");

            TestParse("(a: i32)", "[a: i32]");
            TestParse("(a: i32, b: String)", "[a: i32, b: String]");
            // 泛型类型参数
            TestParse("(a: List\\<i32>)", "[a: List<i32>]");
            // 可空类型参数
            TestParse("(name: String?)", "[name: String?]");

            TestHarness.Blank();
        }

        // ===== 2. 默认参数 =====
        public static void TestDefaultParameters()
        {
            TestHarness.Section("Testing Default Parameters");

            TestParse("(name: String = \"World\")", "[name: String = Str(\"World\")]");
            // 表达式作为默认值
            TestParse("(a: i32 = 1 + 2)", "[a: i32 = Binary(Int(1,I32) + Int(2,I32))]");

            TestHarness.Blank();
        }

        // ===== 3. 可变参数 =====
        public static void TestVariadicParameters()
        {
            TestHarness.Section("Testing Variadic Parameters");

            TestParse("(numbers: i32...)", "[numbers: i32...]");
            TestParse("(options: named String...)", "[options: named String...]");

            TestHarness.Blank();
        }

        // ===== 4. 混合与空列表 =====
        public static void TestMixedParameters()
        {
            TestHarness.Section("Testing Mixed and Empty Parameters");

            TestParse("()", "[]");
            TestParse("(a: i32, b: String = \"x\", rest: named i32...)",
                "[a: i32, b: String = Str(\"x\"), rest: named i32...]");

            TestHarness.Blank();
        }

        // ===== 5. 括号内续行（SYNTAX §1.1，M31）=====
        public static void TestLineContinuation()
        {
            TestHarness.Section("Testing Line Continuation");

            // ( 已出现即允许自然续行：逗号后换行按空白处理
            TestParse("(a: i32,\nb: i32)", "[a: i32, b: i32]");
            TestParse("(a: i32,\n b: String = \"x\")", "[a: i32, b: String = Str(\"x\")]");

            TestHarness.Blank();
        }

        // ===== 6. init 参数映射（§9.3，allowMapping）=====
        public static void TestInitParameterMapping()
        {
            TestHarness.Section("Testing Init Parameter Mapping");

            // init(_ -> x, _ -> y) 的形参列表部分（跨行）
            TestParse("(_ -> x,\n _ -> y)", "[_ -> x, _ -> y]", allowMapping: true);

            TestHarness.Blank();
        }

        // ===== 7. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Testing Error Cases (expect ParserException)");

            TestHarness.CheckParseError("(a)",
                () => ParseParameterList("(a)"),
                "Expected ':' after parameter name");
            TestHarness.CheckParseError("(a: )",
                () => ParseParameterList("(a: )"),
                "Expected parameter type");
            TestHarness.CheckParseError("(a: i32..)",
                () => ParseParameterList("(a: i32..)"),
                "Expected '...' for variadic parameter");
            TestHarness.CheckParseError("(a: i32 = )",
                () => ParseParameterList("(a: i32 = )"),
                "Unexpected token at start of expression");
            // named 仅用于具名可变参数（§4.3）：没有 ... 的 named 是错误（M31，全管线驱动）
            TestHarness.CheckParseError("func f(options: named String)",
                () => TestHarness.ParseRoot("func f(options: named String)"),
                "'named' variadic parameter requires '...'");
            // 保留参数名（§14.4）：. 后必须是合法标识符
            TestHarness.CheckParseError("(.123: i32)（保留参数名不是标识符）",
                () => ParseParameterList("(.123: i32)"),
                "Expected identifier after '.' in reserved parameter name");
            TestHarness.CheckParseError("(.: i32)（裸 . 无标识符）",
                () => ParseParameterList("(.: i32)"),
                "Expected identifier after '.' in reserved parameter name");
            // init 映射目标字段名必须是合法标识符（_ -> 123 数字词拒绝）
            TestHarness.CheckParseError("(_ -> 123)（映射目标不是标识符）",
                () => ParseParameterList("(_ -> 123)", allowMapping: true),
                "Expected field name after '->' in init parameter mapping");

            TestHarness.Blank();
        }

        // ===== 8. 保留参数名（SYNTAX §14.4，method wrapper canonical 形态）=====
        public static void TestReservedParameterNames()
        {
            TestHarness.Section("Testing Reserved Parameter Names (.name)");

            // 正例：.name 前导点原样入 Name（描述串 + 结构断言双验证）
            var node = ParseParameterList("(.name: String)");
            TestHarness.Check("(.name: String)", AstDescribe.Params(node), "[.name: String]");
            TestHarness.CheckTrue("(.name: String) => Name 保留前导点",
                node.Parameters.Count == 1 && node.Parameters[0].Name == ".name",
                $"Name = {(node.Parameters.Count == 1 ? node.Parameters[0].Name : "<无参数>")}");
            // 与普通参数、具名可变参数混排（SYNTAX §14.4 的 canonical 示例形状）
            TestParse("(.name: String, args: named Any...)", "[.name: String, args: named Any...]");

            TestHarness.Blank();
        }

        // ===== 测试辅助 =====

        // 以 ParameterListParserLayer 为起始层独立解析 (...) 片段
        // （allowMapping：init 形参列表允许 _ -> field 映射，§9.3）
        private static ParameterListASTNode ParseParameterList(string code, bool allowMapping = false)
        {
            var node = new ParameterListASTNode(null);
            TestHarness.ParseWithLayer(new ParameterListParserLayer(node, allowMapping), code);
            return node;
        }

        // 辅助：独立解析形参列表并比对 AST 描述串（label 中 \n 转义显示）
        private static void TestParse(string code, string expectedDesc, bool allowMapping = false)
        {
            try
            {
                var node = ParseParameterList(code, allowMapping);
                TestHarness.Check(code.Replace("\n", "\\n"), AstDescribe.Params(node), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code.Replace("\n", "\\n")} => 意外异常", false, ex.Message);
            }
        }

        // ===== 入口 =====
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        internal static ParallelSuiteRunner.SuiteSpec Spec { get; } = new("ParameterList",
        [
            (nameof(TestPlainParameters), TestPlainParameters),
            (nameof(TestDefaultParameters), TestDefaultParameters),
            (nameof(TestVariadicParameters), TestVariadicParameters),
            (nameof(TestMixedParameters), TestMixedParameters),
            (nameof(TestLineContinuation), TestLineContinuation),
            (nameof(TestInitParameterMapping), TestInitParameterMapping),
            (nameof(TestErrorCases), TestErrorCases),
            (nameof(TestReservedParameterNames), TestReservedParameterNames),
        ], sectionTitle: "ParameterList");
    }
}
