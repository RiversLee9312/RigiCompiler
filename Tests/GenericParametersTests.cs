using System;

namespace RigiCompiler.Tests
{
    // 泛型参数列表解析测试（GenericParametersParserLayer）：独立 Layer 驱动
    // （TestHarness.ParseWithLayer），断言 AstDescribe.Generics 描述串。
    //
    // 覆盖 SYNTAX.md §3.6 的泛型声明语法（\<...> 列表）：
    // 1. 参数声明子句：TElement、多参数、out/in 型变
    // 2. 约束子句：extends / supers / with
    // 3. 可变泛型参数：TArgs...、named TValues...、可变 + 约束
    // 4. wrapper canonical 形态：named TNamedArgs..., TUnnamedArgs..., TReturn
    // 5. 错误用例：缺 \ 、缺 <、named 缺 ...、点数不足、空列表、复杂类型当参数名
    public class GenericParametersTests
    {
        // ===== 1. 参数声明子句 =====
        public static void TestParameters()
        {
            TestHarness.Section("Testing Generic Parameter Clauses");

            TestParse("\\<TElement>", "\\<TElement>");
            TestParse("\\<TInput, TResult>", "\\<TInput, TResult>");
            TestParse("\\<out TElement>", "\\<out TElement>");
            TestParse("\\<in TElement>", "\\<in TElement>");
            TestParse("\\<out T, in U, V>", "\\<out T, in U, V>");
            TestParse("\\<shared T>", "\\<shared T>");
            TestParse("\\<shared out T, in shared U, V>", "\\<shared out T, shared in U, V>");
            TestParse("\\<shared named T...>", "\\<shared named T...>");
            TestParse("\\<shared T with Serializable>", "\\<shared T, T with Serializable>");

            TestHarness.Blank();
        }

        // ===== 2. 约束子句 =====
        public static void TestConstraints()
        {
            TestHarness.Section("Testing Generic Constraint Clauses");

            TestParse("\\<TItem extends Comparable>",
                "\\<TItem, TItem extends Comparable>");
            TestParse("\\<Serializable supers BaseType>",
                "\\<Serializable, Serializable supers BaseType>");
            TestParse("\\<TItem extends Comparable, Serializable supers BaseType>",
                "\\<TItem, Serializable, TItem extends Comparable, Serializable supers BaseType>");
            TestParse("\\<TItem with Serializable>",
                "\\<TItem, TItem with Serializable>");
            // 型变参数 + 约束
            TestParse("\\<out TElement extends Comparable>",
                "\\<out TElement, TElement extends Comparable>");
            // 复杂类型作为约束 Target
            TestParse("\\<List\\<T> extends Collection\\<T>>",
                "\\<List<T> extends Collection<T>>");

            TestHarness.Blank();
        }

        // ===== 3. 可变泛型参数 =====
        public static void TestVariadicParameters()
        {
            TestHarness.Section("Testing Variadic Generic Parameters");

            TestParse("\\<TArgs...>", "\\<TArgs...>");
            TestParse("\\<named TValues...>", "\\<named TValues...>");
            // 可变参数 + with 约束（Target 隐含为前面的参数）
            TestParse("\\<named TValues... with Serializable>",
                "\\<named TValues..., TValues with Serializable>");

            TestHarness.Blank();
        }

        // ===== 4. wrapper canonical 形态 =====
        public static void TestWrapperCanonicalShape()
        {
            TestHarness.Section("Testing Wrapper Canonical Shape");

            TestParse("\\<named TNamedArgs..., TUnnamedArgs..., TReturn>",
                "\\<named TNamedArgs..., TUnnamedArgs..., TReturn>");

            TestHarness.Blank();
        }

        // ===== 5. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Testing Error Cases (expect ParserException)");

            TestHarness.CheckParseError("<TElement>",
                () => ParseGenericParams("<TElement>"),
                "Expected '\\' to start");
            TestHarness.CheckParseError("\\<shared shared T>",
                () => ParseGenericParams("\\<shared shared T>"), "Duplicate 'shared'");
            TestHarness.CheckParseError("\\<shared out in T>",
                () => ParseGenericParams("\\<shared out in T>"), "conflicting variance");
            TestHarness.CheckParseError("\\<shared class>",
                () => ParseGenericParams("\\<shared class>"), "Expected type parameter name");
            TestHarness.CheckParseError("\\TElement>",
                () => ParseGenericParams("\\TElement>"),
                "Expected '<' after");
            TestHarness.CheckParseError("\\<named TValues>",
                () => ParseGenericParams("\\<named TValues>"),
                "'named' variadic parameter requires '...'");
            TestHarness.CheckParseError("\\<TArgs..>",
                () => ParseGenericParams("\\<TArgs..>"),
                "Expected '...' for variadic parameter");
            TestHarness.CheckParseError("\\<>",
                () => ParseGenericParams("\\<>"),
                "Expected type parameter");
            TestHarness.CheckParseError("\\<List\\<String>, T>",
                () => ParseGenericParams("\\<List\\<String>, T>"),
                "must be a plain identifier");

            TestHarness.Blank();
        }

        // ===== 测试辅助 =====

        // 以 GenericParametersParserLayer 为起始层独立解析 \<...> 片段
        private static GenericParameterListASTNode ParseGenericParams(string code)
        {
            var node = new GenericParameterListASTNode(null);
            TestHarness.ParseWithLayer(new GenericParametersParserLayer(node), code);
            return node;
        }

        // 辅助：独立解析泛型形参列表并比对 AST 描述串
        private static void TestParse(string code, string expectedDesc)
        {
            try
            {
                var node = ParseGenericParams(code);
                TestHarness.Check(code, AstDescribe.Generics(node), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            TestHarness.Reset();

            TestParameters();
            TestConstraints();
            TestVariadicParameters();
            TestWrapperCanonicalShape();
            TestErrorCases();

            return TestHarness.Summary("GenericParameters");
        }
    }
}
