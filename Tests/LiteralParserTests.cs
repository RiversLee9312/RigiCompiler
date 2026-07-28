using System;

namespace LatteCompiler.Tests
{
    // 字面量解析测试：全管线驱动，断言 AST 树产物
    // （AstDescribe 精确描述串 + 结构事实），不断言控制台文本。
    public class LiteralParserTests
    {
        public static void TestIntLiterals()
        {
            TestHarness.Section("Integer Literals");

            TestLit("42", "Int(42,I32)");
            TestLit("0xFF", "Int(255,I32,hex)");
            TestLit("100L", "Int(100,I64)");
            TestLit("200S", "Int(200,I16)");
            TestLit("50B", "Int(50,I8)");
            TestLit("300U", "Int(300,U32)");
            TestLit("400UL", "Int(400,U64)");

            TestHarness.Blank();
        }

        // 进制前缀（M31，SYNTAX §3.3）：0x 十六进制、0b 二进制、0o 八进制，
        // 可与类型后缀组合（后缀字母不属于进制字符集时按后缀解析）
        public static void TestIntBasePrefixes()
        {
            TestHarness.Section("Integer Base Prefixes");

            TestLit("0b1010", "Int(10,I32,bin)");
            TestLit("0o777", "Int(511,I32,oct)");
            TestLit("0b1010B", "Int(10,I8,bin)");
            TestLit("0o17U", "Int(15,U32,oct)");
            TestLit("0xFFL", "Int(255,I64,hex)");

            TestHarness.Blank();
        }

        // 下划线分隔（M31，SYNTAX §3.3）：只允许数字之间的单个 _
        public static void TestUnderscoreSeparators()
        {
            TestHarness.Section("Underscore Separators");

            TestLit("1_000_000", "Int(1000000,I32)");
            TestLit("1_000L", "Int(1000,I64)");
            // 浮点整数部分同样允许下划线
            TestLit("1_000.5", "Float(1000.5)");

            TestHarness.Blank();
        }

        // 字面量错误用例（M31：进制/下划线/浮点残缺一律报错，不再静默吞并）
        public static void TestLiteralErrorCases()
        {
            TestHarness.Section("Literal Error Cases (expect ParserException)");

            // 裸进制前缀：前缀后没有数字
            TestHarness.CheckParseError("0x",
                () => TestHarness.ParseFirstDecl("0x"), "missing digits");
            // 连续下划线
            TestHarness.CheckParseError("1__000",
                () => TestHarness.ParseFirstDecl("1__000"), "underscore must appear singly between digits");
            // 结尾下划线
            TestHarness.CheckParseError("1_",
                () => TestHarness.ParseFirstDecl("1_"), "underscore must appear singly between digits");
            // 非法进制数字进入后缀位（2 不是二进制数字）
            TestHarness.CheckParseError("0b102",
                () => TestHarness.ParseFirstDecl("0b102"), "Invalid integer literal suffix");
            // 点后缺数字（M31 起不再静默吞点按整数收尾）
            TestHarness.CheckParseError("3.",
                () => TestHarness.ParseFirstDecl("3."), "Expected digit after '.' in float literal");
            // 点后非数字（成员访问请写 (3).foo）
            TestHarness.CheckParseError("3.foo",
                () => TestHarness.ParseFirstDecl("3.foo"), "Expected digit after '.' in float literal");

            TestHarness.Blank();
        }

        public static void TestFloatLiterals()
        {
            TestHarness.Section("Float Literals");

            TestLit("3.14", "Float(3.14)");
            TestLit("0.1f", "Float(0.1f)");
            TestLit("2.5", "Float(2.5)");

            TestHarness.Blank();
        }

        public static void TestBoolAndNull()
        {
            TestHarness.Section("Bool and Null Literals");

            TestLit("true", "Bool(True)");
            TestLit("false", "Bool(False)");
            TestLit("null", "Null");

            TestHarness.Blank();
        }

        public static void TestStringLiterals()
        {
            TestHarness.Section("String Literals");

            TestLit("\"Hello\"", "Str(\"Hello\")");
            TestLit("\"World ${x}\"", "Str(\"World ${x}\",interp)");
            // \$ 转义的字面 $ 不构成插值引导（词法期判定，M32）
            TestLit("\"World \\${x}\"", "Str(\"World ${x}\")");

            TestHarness.Blank();
        }

        // AST 结构断言（AGENTS §5：快照不作为唯一验证方式）
        public static void TestStructuralAssertions()
        {
            TestHarness.Section("Structural Assertions");

            var node = TestHarness.ParseFirstDecl("42");
            TestHarness.CheckTrue("顶层字面量以 LiteralExpression 包装", node is LiteralExpressionASTNode);
            var lit = (LiteralExpressionASTNode)node;
            TestHarness.CheckTrue("Literal 是 IntLiteralASTNode", lit.Literal is IntLiteralASTNode);
            TestHarness.CheckTrue("字面量的 Parent 是包装节点", ReferenceEquals(lit.Literal.Parent, lit));
            TestHarness.CheckTrue("包装节点挂在 Root 下", lit.Parent is RootASTNode);
            TestHarness.CheckTrue("span 非空", lit.Span != null);

            TestHarness.Blank();
        }

        // 辅助：解析单个字面量并比对 AST 描述串
        private static void TestLit(string code, string expectedDesc)
        {
            try
            {
                var node = TestHarness.ParseFirstDecl(code);
                TestHarness.Check(code, AstDescribe.Expr(node), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }

        public static int RunAll()
        {
            TestHarness.Reset();

            TestIntLiterals();
            TestIntBasePrefixes();
            TestUnderscoreSeparators();
            TestFloatLiterals();
            TestBoolAndNull();
            TestStringLiterals();
            TestLiteralErrorCases();
            TestStructuralAssertions();

            return TestHarness.Summary("Literal");
        }
    }
}
