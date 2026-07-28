using System;
using System.IO;
using System.Linq;

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

        // 字符字面量（SYNTAX §3.3）：单引号内恰好一个字符或一个转义序列，类型 char
        public static void TestCharLiterals()
        {
            TestHarness.Section("Char Literals");

            TestLit("'A'", "Char('A')");
            TestLit("'\\n'", "Char('\n')");
            TestLit("'\\''", "Char(''')");
            TestLit("'\\\\'", "Char('\\')");

            TestHarness.Blank();
        }

        // 字符字面量错误（LexerException：空 / 多字符 / 未知转义 / 未闭合，§3.3）
        public static void TestCharErrorCases()
        {
            TestHarness.Section("Char Error Cases (expect LexerException)");

            // 空字面量 ''
            TestHarness.CheckParseError("''",
                () => TestHarness.ParseFirstDecl("''"), "Empty character literal");
            // 多字符 'ab'（收到第二个内容字符即报错）
            TestHarness.CheckParseError("'ab'",
                () => TestHarness.ParseFirstDecl("'ab'"), "exactly one character");
            // 未知转义（与字符串同一套转义表）
            TestHarness.CheckParseError("'\\q'",
                () => TestHarness.ParseFirstDecl("'\\q'"), "Unknown escape sequence");
            // 未闭合：开界后直接 EOF（冲刷帧虚拟换行触发）
            TestHarness.CheckParseError("' (EOF)",
                () => TestHarness.ParseFirstDecl("'"), "Unterminated character literal");
            // 未闭合：内容字符后 EOF
            TestHarness.CheckParseError("'a (EOF)",
                () => TestHarness.ParseFirstDecl("'a"), "Unterminated character literal");
            // 未闭合：内容字符后真实换行
            TestHarness.CheckParseError("'a <换行>",
                () => TestHarness.ParseFirstDecl("'a\n"), "Unterminated character literal");
            // 反斜杠后紧跟真实换行（不支持行接续）
            TestHarness.CheckParseError("'\\ <换行>",
                () => TestHarness.ParseFirstDecl("'\\\n'"), "Unexpected line break after \\");

            TestHarness.Blank();
        }

        // 字符字面量结构断言与 Lexer 级检查（快照不作为唯一验证方式，AGENTS §5）
        public static void TestCharStructural()
        {
            TestHarness.Section("Char Structural & Lexer Level");

            // AST 结构
            var node = TestHarness.ParseFirstDecl("'A'");
            TestHarness.CheckTrue("顶层字面量以 LiteralExpression 包装", node is LiteralExpressionASTNode);
            var lit = (LiteralExpressionASTNode)node;
            TestHarness.CheckTrue("Literal 是 CharLiteralASTNode", lit.Literal is CharLiteralASTNode);
            var ch = (CharLiteralASTNode)lit.Literal;
            TestHarness.CheckTrue("Value 为 'A'", ch.Value == 'A');
            TestHarness.CheckTrue("字面量的 Parent 是包装节点", ReferenceEquals(ch.Parent, lit));
            TestHarness.CheckTrue("span 非空", lit.Span != null);

            // 转义值在词法期展开（'\n' → 换行符）
            var esc = (CharLiteralASTNode)((LiteralExpressionASTNode)
                TestHarness.ParseFirstDecl("'\\n'")).Literal;
            TestHarness.CheckTrue("'\\n' 的 Value 是换行符", esc.Value == '\n');

            // Lexer 级：token 类型、值与 span（左闭右开，'A' 占 1:1-1:4）
            var tokens = new Lexer().Tokenize("'A'");
            TestHarness.CheckTrue("token 流为 [Char, EOF]",
                tokens.Count == 2 && tokens[0] is CharToken && tokens[1] is EndOfFileToken,
                string.Join(" ", tokens.Select(t => t.ToString())));
            TestHarness.CheckTrue("CharToken.Value 为 'A'", ((CharToken)tokens[0]).Value == 'A');
            TestHarness.Check("char token span Start", Pos(tokens[0].CharRange.Start), "1:1");
            TestHarness.Check("char token span End", Pos(tokens[0].CharRange.End), "1:4");

            // JSONL 往返：char 标量字段经 Serializer/Deserializer 的 char 分支配对不丢失
            // （Deserializer 产物强制过 ASTIntegrityValidator，往返成功即验证 Validator 兼容）
            var root = TestHarness.ParseRoot("'A'");
            var writer = new StringWriter();
            AstJsonlSerializer.Serialize(root, writer);
            var roundTripped = AstJsonlDeserializer.Deserialize(new StringReader(writer.ToString()));
            var rtLit = (CharLiteralASTNode)((LiteralExpressionASTNode)roundTripped.Declarations[0]).Literal;
            TestHarness.CheckTrue("JSONL 往返后 Value 不丢失", rtLit.Value == 'A');

            TestHarness.Blank();
        }

        private static string Pos(CharPosition p) => $"{p.line}:{p.column}";

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
            TestCharLiterals();
            TestLiteralErrorCases();
            TestCharErrorCases();
            TestStructuralAssertions();
            TestCharStructural();

            return TestHarness.Summary("Literal");
        }
    }
}
