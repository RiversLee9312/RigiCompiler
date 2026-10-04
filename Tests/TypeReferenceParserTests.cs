using System;

namespace RigiCompiler.Tests
{
    // 类型引用解析测试（TypeReferenceParserLayer）：
    // 独立 Layer 驱动（TestRootParserLayer 垫底）+ 全管线集成，
    // 断言 AST 树产物（AstDescribe.Type + 结构事实）。
    // 注意：rich/shared 是类型声明修饰符，不属于类型引用（SYNTAX §3.1.1）。
    public class TypeReferenceParserTests
    {
        public static void TestBasicTypes()
        {
            TestHarness.Section("Basic Type References");

            TestType("i32", "i32");
            TestType("String", "String");
            TestType("core.collections.List", "core.collections.List");

            TestHarness.Blank();
        }

        public static void TestNullableTypes()
        {
            TestHarness.Section("Nullable Type References");

            TestType("String?", "String?");
            TestType("i32?", "i32?");

            TestHarness.Blank();
        }

        public static void TestGenericTypes()
        {
            TestHarness.Section("Generic Type References");

            TestType("List\\<i32>", "List<i32>");
            TestType("Map\\<String, i32>", "Map<String,i32>");
            TestType("List\\<Map\\<String, i32>>", "List<Map<String,i32>>");
            TestType("List\\<i32>?", "List<i32>?");

            // g1：泛型实参内嵌可空（? 归实参，不归外层类型）
            TestType("Holder\\<i32?>", "Holder<i32?>");
            TestType("Map\\<i32?, String?>", "Map<i32?,String?>");
            TestType("Holder\\<i32?>?", "Holder<i32?>?");
            TestType("List\\<Map\\<String, i32?>?>", "List<Map<String,i32?>?>");

            TestHarness.Blank();
        }

        public static void TestErrorCases()
        {
            TestHarness.Section("Type Reference Error Cases");

            // `\` 后必须跟 `<`（SYNTAX §3.6）
            TestHarness.CheckParseError("List\\i32",
                () => ParseType("List\\i32"), "Expected '<' after '\\'");
            // 闭括号写成 `\>`（旧式残留）
            TestHarness.CheckParseError("List\\<i32\\>",
                () => ParseType("List\\<i32\\>"), "close with '>', not '\\>'");
            // 多余的闭合 `>`：TestRootParserLayer 只接受 EOF，漏消费即失败
            TestHarness.CheckParseError("List\\<i32>>",
                () => ParseType("List\\<i32>>"), "unconsumed token");
            // 逗号后悬空实参不得被 '>' 吞掉
            TestHarness.CheckParseError("List\\<i32,>",
                () => ParseType("List\\<i32,>"), "Expected type argument before '>'");

            TestHarness.Blank();
        }

        // 全管线集成：经变量声明驱动（含嵌套泛型 + 可空）
        public static void TestIntegratedParsing()
        {
            TestHarness.Section("Integrated via Variable Declaration");

            try
            {
                var node = TestHarness.ParseFirstDecl("var x: List\\<Map\\<String, i32>>?");
                TestHarness.Check("var x: List\\<Map\\<String, i32>>?",
                    AstDescribe.Decl(node), "var x: List<Map<String,i32>>?");
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue("集成解析 => 意外异常", false, ex.Message);
            }

            TestHarness.Blank();
        }

        // AST 结构断言（AGENTS §5：快照不作为唯一验证方式）
        public static void TestStructuralAssertions()
        {
            TestHarness.Section("Structural Assertions");

            var typeNode = ParseType("List\\<Map\\<String, i32>>?");
            TestHarness.CheckTrue("IsNullable 已置位", typeNode.IsNullable);
            TestHarness.CheckTrue("TypeSymbol 的 Parent 是类型节点",
                ReferenceEquals(typeNode.TypeSymbol.Parent, typeNode));
            TestHarness.CheckTrue("span 非空（层 span 由主循环回填）", typeNode.Span != null);
            var elements = typeNode.TypeSymbol.symbol.elements;
            TestHarness.CheckTrue("符号只有一个元素", elements.Count == 1);
            TestHarness.CheckTrue("嵌套泛型已下钻",
                elements[0].generics.Count == 1 &&
                elements[0].generics[0].TypeSymbol.symbol.elements[0].generics.Count == 2);

            // g1：实参是完整 TypeReferenceASTNode——? 挂实参而非外层，
            // 实参节点的 Parent 指向持有符号的 SymbolASTNode（Validator 同口径）
            var nullable = ParseType("Holder\\<i32?>?");
            TestHarness.CheckTrue("外层可空已置位", nullable.IsNullable);
            var arg = nullable.TypeSymbol.symbol.elements[0].generics[0];
            TestHarness.CheckTrue("实参可空已置位（i32?）", arg.IsNullable);
            TestHarness.CheckTrue("实参符号名是 i32",
                arg.TypeSymbol.symbol.elements[0].name == "i32");
            TestHarness.CheckTrue("实参 Parent 是外层 SymbolASTNode",
                ReferenceEquals(arg.Parent, nullable.TypeSymbol));
            TestHarness.CheckTrue("实参 span 非空", arg.Span != null);

            TestHarness.Blank();
        }

        // 辅助：独立驱动 TypeReferenceParserLayer 并比对 AstDescribe.Type 描述串
        private static void TestType(string code, string expectedDesc)
        {
            try
            {
                var typeNode = ParseType(code);
                TestHarness.Check(code, AstDescribe.Type(typeNode), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }

        private static TypeReferenceASTNode ParseType(string code)
        {
            var typeNode = new TypeReferenceASTNode(null);
            TestHarness.ParseWithLayer(new TypeReferenceParserLayer(typeNode), code);
            return typeNode;
        }

        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        internal static ParallelSuiteRunner.SuiteSpec Spec { get; } = new("TypeReference",
        [
            (nameof(TestBasicTypes), TestBasicTypes),
            (nameof(TestNullableTypes), TestNullableTypes),
            (nameof(TestGenericTypes), TestGenericTypes),
            (nameof(TestErrorCases), TestErrorCases),
            (nameof(TestIntegratedParsing), TestIntegratedParsing),
            (nameof(TestStructuralAssertions), TestStructuralAssertions),
        ], sectionTitle: "TypeReference");
    }
}
