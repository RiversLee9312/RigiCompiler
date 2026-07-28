using System;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 多行字符串测试（M32，SYNTAX §3.3 Swift 风格严格多行）。
    /// 三层验证：
    /// 1. Lexer 内容拼装：缩进剥除 / 转义 / 引号规则 / 行尾归一；
    /// 2. 错误路径：开界同行内容、闭界行内出现、缩进不足、未知转义、未闭合；
    /// 3. 引号分流回归与 Parser AST 级：StringToken 复用（Parser 零改动）、
    ///    StringLiteralASTNode 值与插值标记、token span。
    /// </summary>
    public static class MultilineStringTests
    {
        // ===== 1. Lexer 内容拼装 =====

        public static void TestContent()
        {
            TestHarness.Section("Multiline Content");

            // 基本：无缩进
            ExpectString("\"\"\"\nhello\n\"\"\"", "hello");
            // 闭界缩进为剥除基准
            ExpectString("\"\"\"\n    hello\n    world\n    \"\"\"", "hello\nworld");
            // 空多行字符串
            ExpectString("\"\"\"\n\"\"\"", "");
            // 全空白内容行输出空行（纯空行 / 不足缩进的空白行均可）
            ExpectString("\"\"\"\n    a\n\n    b\n    \"\"\"", "a\n\nb");
            ExpectString("\"\"\"\n    a\n  \n    b\n    \"\"\"", "a\n\nb");
            // 行内单个 " 与 "" 免转义
            ExpectString("\"\"\"\nsay \"hi\" and \"\" ok\n\"\"\"", "say \"hi\" and \"\" ok");
            // 转义与单行一致；剥除缩进先于转义处理
            ExpectString("\"\"\"\n    \\n\\t\\\\\\$\n    \"\"\"", "\n\t\\$");
            // 内容中的三引号须转义（\" 不参与终止判定）
            ExpectString("\"\"\"\n\\\"\"\"\n\"\"\"", "\"\"\"");
            // 行尾归一：\r\n 内容换行恒为 \n
            ExpectString("\"\"\"\r\na\r\nb\r\n\"\"\"", "a\nb");

            TestHarness.Blank();
        }

        // ===== 2. 错误路径 =====

        public static void TestErrors()
        {
            TestHarness.Section("Multiline Errors");

            // 开界 """ 同行直接写内容
            ExpectError("\"\"\"hello\n\"\"\"", "must begin with a newline");
            // 闭界 """ 出现在内容行中间（前面已有非空白内容）
            ExpectError("\"\"\"\nabc \"\"\"\n\"\"\"", "must be on its own line");
            // 内容行前导空白不足闭界缩进
            ExpectError("\"\"\"\n  hello\n    \"\"\"", "less indented");
            // 未知转义
            ExpectError("\"\"\"\n\\q\n\"\"\"", "Unknown escape sequence");
            // 反斜杠后紧跟真实换行（不支持行接续）
            ExpectError("\"\"\"\nab\\\ncd\n\"\"\"", "Unexpected line break after \\");
            // 未闭合：开界后 EOF / 内容中 EOF
            ExpectError("\"\"\"", "Unterminated multi-line string literal");
            ExpectError("\"\"\"\nabc\n", "Unterminated multi-line string literal");

            TestHarness.Blank();
        }

        // ===== 3. 引号分流回归（"" 与单行串不受影响）=====

        public static void TestQuoteRegression()
        {
            TestHarness.Section("Quote Dispatch Regression");

            // 空字符串 ""：内容与 token 序列
            ExpectString("\"\"", "");
            ExpectTokens("\"\" x",
                new[] { typeof(StringToken), typeof(WordToken), typeof(EndOfFileToken) });
            // 连续两个空字符串
            ExpectTokens("\"\" \"\"",
                new[] { typeof(StringToken), typeof(StringToken), typeof(EndOfFileToken) });
            // 单行字符串不受影响
            ExpectString("\"Hello\"", "Hello");
            ExpectString("\"a\\nb\"", "a\nb");
            // 单行串未闭合仍报错
            ExpectError("\"abc", "Line break symbol appears in string");
            // 多行闭合后同行代码继续分词
            ExpectTokens("\"\"\"\nhi\n\"\"\" x",
                new[] { typeof(StringToken), typeof(WordToken), typeof(EndOfFileToken) });

            TestHarness.Blank();
        }

        // ===== 4. token span =====

        public static void TestSpans()
        {
            TestHarness.Section("Multiline Spans");

            // 多行 token 覆盖开界 """ 到闭界 """（左闭右开）
            var str = (StringToken)new Lexer().Tokenize("\"\"\"\nhi\n\"\"\"")[0];
            TestHarness.Check("多行 span Start", Pos(str.CharRange.Start), "1:1");
            TestHarness.Check("多行 span End", Pos(str.CharRange.End), "3:4");
            // 空串 "" 占两列
            var empty = (StringToken)new Lexer().Tokenize("\"\"")[0];
            TestHarness.Check("空串 span Start", Pos(empty.CharRange.Start), "1:1");
            TestHarness.Check("空串 span End", Pos(empty.CharRange.End), "1:3");

            TestHarness.Blank();
        }

        // ===== 5. 插值标记（词法期判定，\$ 转义不算）=====

        public static void TestInterpolationFlag()
        {
            TestHarness.Section("Interpolation Flag");

            // 单行
            ExpectInterp("\"${x}\"", true);
            ExpectInterp("\"\\${x}\"", false);
            ExpectInterp("\"$${x}\"", true);   // 第二个 $ 是插值引导
            ExpectInterp("\"a$\"", false);
            // 多行
            ExpectInterp("\"\"\"\n${x}\n\"\"\"", true);
            ExpectInterp("\"\"\"\n\\${x}\n\"\"\"", false);
            // ${ 必须同行相邻（行间有 \n 分隔）
            ExpectInterp("\"\"\"\na$\n{b}\n\"\"\"", false);

            TestHarness.Blank();
        }

        // ===== 6. Parser AST 级 =====

        public static void TestAst()
        {
            TestHarness.Section("Multiline AST");

            TestLit("\"\"\"\nhello\n\"\"\"", "Str(\"hello\")");
            TestLit("\"\"\"\n    a\n\n    b\n    \"\"\"", "Str(\"a\n\nb\")");
            // 插值标记与单行一致（求值留待语义阶段）
            TestLit("\"\"\"\n${x}\n\"\"\"", "Str(\"${x}\",interp)");
            // \$ 转义的字面 $ 不构成插值引导
            TestLit("\"\"\"\n\\${x}\n\"\"\"", "Str(\"${x}\")");

            // 结构断言（快照不作为唯一验证方式，AGENTS §5）
            var node = TestHarness.ParseFirstDecl("\"\"\"\nhi\n\"\"\"");
            TestHarness.CheckTrue("顶层字面量以 LiteralExpression 包装", node is LiteralExpressionASTNode);
            var lit = (LiteralExpressionASTNode)node;
            TestHarness.CheckTrue("Literal 是 StringLiteralASTNode", lit.Literal is StringLiteralASTNode);
            var s = (StringLiteralASTNode)lit.Literal;
            TestHarness.CheckTrue("无插值标记", !s.HasInterpolation);
            TestHarness.CheckTrue("字面量的 Parent 是包装节点", ReferenceEquals(s.Parent, lit));
            TestHarness.CheckTrue("span 非空", lit.Span != null);

            TestHarness.Blank();
        }

        // ===== 辅助 =====

        // 词法断言：源码应产出恰好一个 StringToken（+EOF），内容精确比对
        private static void ExpectString(string code, string expectedContent)
        {
            try
            {
                var tokens = new Lexer().Tokenize(code);
                if (tokens.Count == 2 && tokens[0] is StringToken str && tokens[1] is EndOfFileToken)
                {
                    TestHarness.Check($"lex {Describe(code)}", str.Content, expectedContent);
                }
                else
                {
                    TestHarness.CheckTrue($"lex {Describe(code)} => token 流不是单字符串", false,
                        string.Join(" ", tokens.Select(t => t.ToString())));
                }
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"lex {Describe(code)} => 意外异常", false, ex.Message);
            }
        }

        private static void ExpectTokens(string code, Type[] expectedTypes)
        {
            try
            {
                var tokens = new Lexer().Tokenize(code);
                var actual = tokens.Select(t => t.GetType()).ToArray();
                TestHarness.CheckTrue($"tok {Describe(code)}",
                    actual.SequenceEqual(expectedTypes),
                    string.Join(" ", actual.Select(t => t.Name)));
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"tok {Describe(code)} => 意外异常", false, ex.Message);
            }
        }

        // 插值标记词法断言：源码首个 token 应为 StringToken，标记精确比对
        private static void ExpectInterp(string code, bool expected)
        {
            try
            {
                var tokens = new Lexer().Tokenize(code);
                if (tokens[0] is StringToken str)
                {
                    TestHarness.CheckTrue($"interp {Describe(code)}", str.HasInterpolation == expected,
                        $"HasInterpolation={str.HasInterpolation}");
                }
                else
                {
                    TestHarness.CheckTrue($"interp {Describe(code)} => 首 token 不是字符串", false,
                        tokens[0].ToString());
                }
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"interp {Describe(code)} => 意外异常", false, ex.Message);
            }
        }

        private static void ExpectError(string code, string messagePart)
        {
            TestHarness.CheckParseError($"err {Describe(code)}",
                () => new Lexer().Tokenize(code), messagePart);
        }

        // AST 断言：解析单个字面量并比对 AST 描述串（与 LiteralParserTests.TestLit 同约定）
        private static void TestLit(string code, string expectedDesc)
        {
            try
            {
                var node = TestHarness.ParseFirstDecl(code);
                TestHarness.Check($"ast {Describe(code)}", AstDescribe.Expr(node), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"ast {Describe(code)} => 意外异常", false, ex.Message);
            }
        }

        private static string Pos(CharPosition p) => $"{p.line}:{p.column}";

        private static string Describe(string code) =>
            code.Replace("\r", "\\r").Replace("\n", "\\n");

        public static int RunAll()
        {
            TestHarness.Reset();

            TestContent();
            TestErrors();
            TestQuoteRegression();
            TestSpans();
            TestInterpolationFlag();
            TestAst();

            return TestHarness.Summary("MultilineString");
        }
    }
}
