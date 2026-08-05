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

        // 科学计数法浮点字面量（SYNTAX §3.3）：e/E + 可选 +/- 符号 + 十进制
        // 指数数字；Lexer 不做语义（3.14e-5 拆 3/. /14e/-/5 五个 token），
        // 指数由本层组合状态机吸收
        public static void TestScientificNotationLiterals()
        {
            TestHarness.Section("Scientific Notation Literals");

            TestLit("3.14e5", "Float(314000)");
            TestLit("3.14E+5", "Float(314000)");
            TestLit("2e3", "Float(2000)");
            TestLit("2e+3", "Float(2000)");
            // 与 f/F 浮点后缀组合
            TestLit("1.5e3f", "Float(1500f)");

            // 结构/span 断言（快照不作为唯一验证方式，AGENTS §5）：
            // 吸收形态 3.14e-5 整个字面量单节点 span 覆盖全部组成 token
            var lit = (LiteralExpressionASTNode)TestHarness.ParseFirstDecl("3.14e-5");
            var fl = (FloatLiteralASTNode)lit.Literal;
            TestHarness.CheckTrue("3.14e-5 的 Value", fl.Value == 3.14e-5);
            TestHarness.CheckTrue("3.14e-5 非 f 后缀", !fl.IsFloat);
            TestHarness.Check("3.14e-5 包装节点 span Start", Pos(lit.Span!.Value.Start), "1:1");
            TestHarness.Check("3.14e-5 包装节点 span End", Pos(lit.Span!.Value.End), "1:8");
            TestHarness.CheckTrue("字面量节点与包装节点 span 一致",
                fl.Span!.Value.Start.offset == lit.Span!.Value.Start.offset &&
                fl.Span!.Value.End.offset == lit.Span!.Value.End.offset);

            TestHarness.Blank();
        }

        // 科学计数法错误形态（SYNTAX §3.3）：e/E 后无合法指数数字是编译错误，
        // 消息带完整已拼内容
        public static void TestScientificNotationErrorCases()
        {
            TestHarness.Section("Scientific Notation Error Cases (expect ParserException)");

            // 指数标记后 EOF
            TestHarness.CheckParseError("3.14e (EOF)",
                () => TestHarness.ParseFirstDecl("3.14e"), "Invalid float literal: '3.14e'");
            // 指数符号后 EOF
            TestHarness.CheckParseError("3.14e- (EOF)",
                () => TestHarness.ParseFirstDecl("3.14e-"), "Invalid float literal: '3.14e-'");
            // 指数符号后非数字 word
            TestHarness.CheckParseError("3.14e+x",
                () => TestHarness.ParseFirstDecl("3.14e+x"), "Invalid float literal: '3.14e+'");

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
            // S7f 插值拆分：段序列挂载（StrInterp），文本段为段级字面量结构
            TestLit("\"World ${x}\"", "StrInterp(Str(\"World \"), Path(x, []))");
            // \$ 转义的字面 $ 不构成插值引导（词法期判定，M32）
            TestLit("\"World \\${x}\"", "Str(\"World ${x}\")");

            TestHarness.Blank();
        }

        // 字符串插值（S7f，SYNTAX §3.8）：拆分快照 + 结构/span 断言 + 错误路径
        public static void TestStringInterpolation()
        {
            TestHarness.Section("String Interpolation");

            // 多段拆分（含运算符段；段表达式经子解析为完整 AST）
            TestLit("\"a${x}b${y + 1}c\"",
                "StrInterp(Str(\"a\"), Path(x, []), Str(\"b\"), " +
                "Binary(Path(y, []) + Int(1,I32)), Str(\"c\"))");
            // 单段纯表达式（无字面量段不产空段）
            TestLit("\"${x}\"", "StrInterp(Path(x, []))");
            // 调用段
            TestLit("\"${f(1, 2)}\"", "StrInterp(Path(f(Int(1,I32), Int(2,I32)), []))");
            // M53 帧机制：单行宿主内嵌套字符串字面量（引号在帧内分流，不闭合宿主）
            TestLit("\"a${\"b\"}c\"", "StrInterp(Str(\"a\"), Str(\"b\"), Str(\"c\"))");
            // 嵌套插值（帧栈递归）
            TestLit("\"${\"${x}\"}\"", "StrInterp(StrInterp(Path(x, [])))");
            // 插值含 lambda（{} 计数正确：lambda 体不吞宿主配平）
            TestLit("\"${items.map(func{(i: i32): i32 -> (i + 1)})}\"",
                "StrInterp(Path(items, [.map(Lambda([i: i32]): i32 -> Group(Binary(Path(i, []) + Int(1,I32))))]))");
            // 多段连续插值
            TestLit("\"a${x}b${y}c\"",
                "StrInterp(Str(\"a\"), Path(x, []), Str(\"b\"), Path(y, []), Str(\"c\"))");

            // 结构断言（快照不作为唯一验证方式，AGENTS §5）
            var lit = (LiteralExpressionASTNode)TestHarness.ParseFirstDecl("\"a${x}b\"");
            var str = (StringLiteralASTNode)lit.Literal;
            TestHarness.CheckTrue("插值段序列非空", str.InterpolationParts?.Count == 3);
            var textPart = str.InterpolationParts![0];
            TestHarness.CheckTrue("文本段是段级 LiteralExpression 结构",
                textPart.Text is LiteralExpressionASTNode
                && ((LiteralExpressionASTNode)textPart.Text!).Literal is StringLiteralASTNode
                && ((StringLiteralASTNode)((LiteralExpressionASTNode)textPart.Text!).Literal).Value == "a");
            TestHarness.CheckTrue("文本段 Parent 链（wrapper → 宿主字面量）",
                ReferenceEquals(textPart.Text!.Parent, str)
                && ReferenceEquals(
                    ((LiteralExpressionASTNode)textPart.Text!).Literal.Parent, textPart.Text));
            var exprPart = str.InterpolationParts[1];
            TestHarness.CheckTrue("表达式段 Root 已填充且 Parent 是宿主字面量",
                exprPart.Expression is { IsAttached: true }
                && ReferenceEquals(exprPart.Expression!.Parent, str));
            // 段 span（左闭右开）：源 "a${x}b" 中 a=1:2-1:3、x=1:5-1:6、b=1:7-1:8
            TestHarness.Check("文本段 a 的 span Start", Pos(textPart.Text!.Span!.Value.Start), "1:2");
            TestHarness.Check("文本段 a 的 span End", Pos(textPart.Text!.Span!.Value.End), "1:3");
            TestHarness.Check("表达式段 x 的 span Start",
                Pos(exprPart.Expression!.Span!.Value.Start), "1:5");
            TestHarness.Check("表达式段 x 的 span End",
                Pos(exprPart.Expression!.Span!.Value.End), "1:6");
            TestHarness.Check("文本段 b 的 span Start",
                Pos(str.InterpolationParts[2].Text!.Span!.Value.Start), "1:7");

            // JSONL 往返：插值段（carrier + 段级字面量 + 表达式子树）不丢失
            var root = TestHarness.ParseRoot("\"a${x}b\"");
            var writer = new StringWriter();
            AstJsonlSerializer.Serialize(root, writer);
            var roundTripped = AstJsonlDeserializer.Deserialize(new StringReader(writer.ToString()));
            var rtStr = (StringLiteralASTNode)((LiteralExpressionASTNode)
                roundTripped.Declarations[0]).Literal;
            TestHarness.CheckTrue("JSONL 往返后段序列完整",
                rtStr.InterpolationParts?.Count == 3
                && ((StringLiteralASTNode)((LiteralExpressionASTNode)
                    rtStr.InterpolationParts[0].Text!).Literal).Value == "a"
                && rtStr.InterpolationParts[1].Expression is { IsAttached: true }
                && ((StringLiteralASTNode)((LiteralExpressionASTNode)
                    rtStr.InterpolationParts[2].Text!).Literal).Value == "b");

            TestHarness.Blank();
        }

        // 插值错误路径（M53 帧机制）：未闭合引导（Lexer EOF 帧检查）、空插值
        // （表达式位置收到结束标记）、插值内多余 token、嵌套字符串未闭合
        public static void TestInterpolationErrorCases()
        {
            TestHarness.Section("Interpolation Error Cases (expect Lexer/ParserException)");

            TestHarness.CheckParseError("\"${",
                () => TestHarness.ParseFirstDecl("\"${\""), "Unclosed interpolation");
            TestHarness.CheckParseError("\"${}\"",
                () => TestHarness.ParseFirstDecl("\"${}\""),
                "Unexpected token at start of expression");
            TestHarness.CheckParseError("\"${a b}\"",
                () => TestHarness.ParseFirstDecl("\"${a b}\""),
                "Expected '}' to close interpolation");
            // 嵌套单行字符串未闭合：就近按单行字符串词法规则报
            TestHarness.CheckParseError("多行内 \"${\"a}\"",
                () => TestHarness.ParseFirstDecl("\"\"\"\n${\"a}\n\"\"\""),
                "Line break symbol appears in string");

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
            TestScientificNotationLiterals();
            TestScientificNotationErrorCases();
            TestBoolAndNull();
            TestStringLiterals();
            TestStringInterpolation();
            TestInterpolationErrorCases();
            TestCharLiterals();
            TestLiteralErrorCases();
            TestCharErrorCases();
            TestStructuralAssertions();
            TestCharStructural();

            return TestHarness.Summary("Literal");
        }
    }
}
