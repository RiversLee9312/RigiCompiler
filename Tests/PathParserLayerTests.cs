using System;

namespace RigiCompiler.Tests
{
    // 符号路径解析测试（PathParserLayer）：独立 Layer 驱动
    // （CompilerTestTools.ParseWithLayer + TestRootParserLayer 垫底），
    // 断言 AstDescribe.Symbol 描述串 + 结构事实。
    // 覆盖：基本路径、泛型（\< 开启，SYNTAX §3.6）、M31 不完整结构错误
    // （尾点/双点/泛型未闭合/\ 悬空腹，此前均被静默吞并）、span 回填。
    public class PathParserLayerTests
    {
        // ===== 1. 基本路径 =====
        public static void TestBasicPaths()
        {
            CompilerTestTools.Section("Basic Symbol Paths");

            TestPath("foo", "foo");
            TestPath("a.b.c", "a.b.c");

            CompilerTestTools.Blank();
        }

        // ===== 2. 泛型路径（SYNTAX §3.6：\< 开启）=====
        public static void TestGenericPaths()
        {
            CompilerTestTools.Section("Generic Symbol Paths");

            TestPath("List\\<i32>", "List<i32>");
            // 嵌套泛型的连续闭合符 >>（Lexer 不合并 > 系列）
            TestPath("Map\\<String, List\\<i32>>", "Map<String,List<i32>>");

            CompilerTestTools.Blank();
        }

        // ===== 3. 错误用例（M31：不完整结构一律报错）=====
        public static void TestErrorCases()
        {
            CompilerTestTools.Section("Path Error Cases (expect ParserException)");

            // 尾点空腹遇 EOF：结构不完整
            CaseAssertions.CheckParseError("foo. (EOF)",
                () => ParsePath("foo."), "Unexpected end of file");
            // 尾点空腹遇换行（lineBreakSensitive）：同样报错
            CaseAssertions.CheckParseError("foo.\\n (line break)",
                () => ParsePath("foo.\n"), "expected element name");
            // 连续双点（表达式符号引用语境不允许 ... 可变标记）
            CaseAssertions.CheckParseError("foo..bar",
                () => ParsePath("foo..bar"), "Unexpected '.' in symbol path");
            // 泛型列表未闭合遇 EOF
            CaseAssertions.CheckParseError("List\\<i32 (EOF)",
                () => ParsePath("List\\<i32"), "Unexpected end of file");
            // \ 后必须紧跟 <（SYNTAX §3.6）
            CaseAssertions.CheckParseError("a \\ b",
                () => ParsePath("a \\ b"), "Expected '<' after '\\'");
            // \ 悬空腹遇 EOF：期待 < 时输入结束
            CaseAssertions.CheckParseError("a\\ (EOF)",
                () => ParsePath("a\\"), "Unexpected end of file");

            CompilerTestTools.Blank();
        }

        // ===== 4. AST 结构断言（AGENTS §5：快照不作为唯一验证方式）=====
        public static void TestStructuralAssertions()
        {
            CompilerTestTools.Section("Structural Assertions");

            var basic = ParsePath("a.b.c");
            CaseAssertions.CheckTrue("a.b.c: 三个符号元素", basic.symbol.elements.Count == 3);
            CaseAssertions.CheckTrue("a.b.c: 元素名依次是 a/b/c",
                basic.symbol.elements[0].name == "a" &&
                basic.symbol.elements[1].name == "b" &&
                basic.symbol.elements[2].name == "c");

            var generic = ParsePath("Map\\<String, List\\<i32>>");
            CaseAssertions.CheckTrue("Map\\<...>: 单个顶层元素", generic.symbol.elements.Count == 1);
            var map = generic.symbol.elements[0];
            CaseAssertions.CheckTrue("Map\\<...>: 两个泛型实参", map.generics.Count == 2);
            // 泛型下钻深度：第二个实参是 List<i32>，其自身还有一个泛型实参 i32
            // （g1：实参为完整 TypeReferenceASTNode，经其 TypeSymbol 下钻）
            CaseAssertions.CheckTrue("Map\\<...>: 嵌套泛型已下钻到 List<i32>",
                map.generics[1].TypeSymbol.symbol.elements.Count == 1 &&
                map.generics[1].TypeSymbol.symbol.elements[0].name == "List" &&
                map.generics[1].TypeSymbol.symbol.elements[0].generics.Count == 1 &&
                map.generics[1].TypeSymbol.symbol.elements[0].generics[0]
                    .TypeSymbol.symbol.elements[0].name == "i32");
            CaseAssertions.CheckTrue("span 非空（ReceiveSpan 由主循环回填）", generic.Span != null);

            CompilerTestTools.Blank();
        }

        // ===== 辅助 =====

        // 独立驱动 PathParserLayer：施工目标为 SymbolASTNode，原地填充其 symbol
        private static SymbolASTNode ParsePath(string code)
        {
            var node = new SymbolASTNode(null);
            CompilerTestTools.ParseWithLayer(new PathParserLayer(node, lineBreakSensitive: true), code);
            return node;
        }

        // 辅助：独立解析符号路径并比对 AstDescribe.Symbol 描述串
        private static void TestPath(string code, string expectedDesc)
        {
            try
            {
                var node = ParsePath(code);
                CaseAssertions.Check(code, AstDescribe.Symbol(node.symbol), expectedDesc);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }


        internal static TestSuiteData Spec { get; } = new("Path",
        [
            (nameof(TestBasicPaths), TestBasicPaths),
            (nameof(TestGenericPaths), TestGenericPaths),
            (nameof(TestErrorCases), TestErrorCases),
            (nameof(TestStructuralAssertions), TestStructuralAssertions),
        ], sectionTitle: "Path");
    }
}
