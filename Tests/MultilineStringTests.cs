using System;
using System.Linq;

namespace RigiCompiler.Tests
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
            CompilerTestTools.Section("Multiline Content");

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
            // 引号串紧跟反斜杠转义：挂起引号先归属内容，转义对不得插到引号之前
            ExpectString("\"\"\"\nab\"\\ncd\n\"\"\"", "ab\"\ncd");
            ExpectString("\"\"\"\nab\"\\tcd\n\"\"\"", "ab\"\tcd");
            ExpectString("\"\"\"\nab\"\\\\cd\n\"\"\"", "ab\"\\cd");
            ExpectString("\"\"\"\nab\"\"\\ncd\n\"\"\"", "ab\"\"\ncd");
            // 行尾归一：\r\n 内容换行恒为 \n
            ExpectString("\"\"\"\r\na\r\nb\r\n\"\"\"", "a\nb");

            CompilerTestTools.Blank();
        }

        // ===== 2. 错误路径 =====

        public static void TestErrors()
        {
            CompilerTestTools.Section("Multiline Errors");

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

            CompilerTestTools.Blank();
        }

        // ===== 3. 引号分流回归（"" 与单行串不受影响）=====

        public static void TestQuoteRegression()
        {
            CompilerTestTools.Section("Quote Dispatch Regression");

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

            CompilerTestTools.Blank();
        }

        // ===== 4. token span =====

        public static void TestSpans()
        {
            CompilerTestTools.Section("Multiline Spans");

            // 多行 token 覆盖开界 """ 到闭界 """（左闭右开）
            var str = (StringToken)new Lexer().Tokenize("\"\"\"\nhi\n\"\"\"")[0];
            CaseAssertions.Check("多行 span Start", Pos(str.CharRange.Start), "1:1");
            CaseAssertions.Check("多行 span End", Pos(str.CharRange.End), "3:4");
            // 空串 "" 占两列
            var empty = (StringToken)new Lexer().Tokenize("\"\"")[0];
            CaseAssertions.Check("空串 span Start", Pos(empty.CharRange.Start), "1:1");
            CaseAssertions.Check("空串 span End", Pos(empty.CharRange.End), "1:3");

            // 插值首段 span 修正（在 PushToken 之后以 token 头为基准）：
            // 开界 """ 与强制换行不属于段内容——Start 为开界行下一行行首（2:1），
            // End 回收引导的 $（"ab " 占 2:1–2:3，End 排他指向 $ 的 2:4）
            var interpTokens = new Lexer().Tokenize("\"\"\"\nab ${x}\n\"\"\"");
            var firstSeg = (StringToken)interpTokens[0];
            CaseAssertions.Check("多行插值首段内容", firstSeg.Content, "ab ");
            CaseAssertions.Check("多行插值首段 span Start", Pos(firstSeg.CharRange.Start), "2:1");
            CaseAssertions.Check("多行插值首段 span End", Pos(firstSeg.CharRange.End), "2:4");

            CompilerTestTools.Blank();
        }

        // ===== 5. 插值标记（词法期判定，\$ 转义不算）=====

        public static void TestInterpolationFlag()
        {
            CompilerTestTools.Section("Interpolation Flag");

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

            CompilerTestTools.Blank();
        }

        // ===== 6. Parser AST 级 =====

        public static void TestAst()
        {
            CompilerTestTools.Section("Multiline AST");

            TestLit("\"\"\"\nhello\n\"\"\"", "Str(\"hello\")");
            TestLit("\"\"\"\n    a\n\n    b\n    \"\"\"", "Str(\"a\n\nb\")");
            // 插值拆分与单行一致（S7f：段序列挂载，StrInterp）
            TestLit("\"\"\"\n${x}\n\"\"\"", "StrInterp(Path(x, []))");
            // \$ 转义的字面 $ 不构成插值引导
            TestLit("\"\"\"\n\\${x}\n\"\"\"", "Str(\"${x}\")");
            // S7f 多行插值拆分：字面量段随行首剥缩进，行间换行属于内容
            TestLit("\"\"\"\n    a ${x}\n    b\n    \"\"\"",
                "StrInterp(Str(\"a \"), Path(x, []), Str(\"\nb\"))");
            // 表达式可跨行（与主文件同规则：括号未闭合时换行透明，SYNTAX §1.1）
            TestLit("\"\"\"\n    ${(a +\n        1)}\n    \"\"\"",
                "StrInterp(Group(Binary(Path(a, []) + Int(1,I32))))");
            // 插值表达式内可含单行字符串字面量（多行内 " 免转义；含 {} 不计配平）
            TestLit("\"\"\"\n    ${f(\"a}b\")}\n    \"\"\"",
                "StrInterp(Path(f(Str(\"a}b\")), []))");
            // M53 帧机制：嵌套插值（多行宿主内再插值，帧栈递归）
            TestLit("\"\"\"\n    ${\"inner ${x}\"}\n    \"\"\"",
                "StrInterp(StrInterp(Str(\"inner \"), Path(x, [])))");
            // M53 帧机制：插值含 lambda（{} 计数正确）
            TestLit("\"\"\"\n    ${items.map(func{(i: i32): i32 -> (i + 1)})}\n    \"\"\"",
                "StrInterp(Path(items, [.map(Lambda([i: i32]): i32 -> Group(Binary(Path(i, []) + Int(1,I32))))]))");

            // 结构断言（快照不作为唯一验证方式，AGENTS §5）
            var node = CompilerTestTools.ParseFirstDecl("\"\"\"\nhi\n\"\"\"");
            CaseAssertions.CheckTrue("顶层字面量以 LiteralExpression 包装", node is LiteralExpressionASTNode);
            var lit = (LiteralExpressionASTNode)node;
            CaseAssertions.CheckTrue("Literal 是 StringLiteralASTNode", lit.Literal is StringLiteralASTNode);
            var s = (StringLiteralASTNode)lit.Literal;
            CaseAssertions.CheckTrue("无插值标记", !s.HasInterpolation);
            CaseAssertions.CheckTrue("字面量的 Parent 是包装节点", ReferenceEquals(s.Parent, lit));
            CaseAssertions.CheckTrue("span 非空", lit.Span != null);

            CompilerTestTools.Blank();
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
                    CaseAssertions.Check($"lex {Describe(code)}", str.Content, expectedContent);
                }
                else
                {
                    CaseAssertions.CheckTrue($"lex {Describe(code)} => token 流不是单字符串", false,
                        string.Join(" ", tokens.Select(t => t.ToString())));
                }
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"lex {Describe(code)} => 意外异常", false, ex.Message);
            }
        }

        private static void ExpectTokens(string code, Type[] expectedTypes)
        {
            try
            {
                var tokens = new Lexer().Tokenize(code);
                var actual = tokens.Select(t => t.GetType()).ToArray();
                CaseAssertions.CheckTrue($"tok {Describe(code)}",
                    actual.SequenceEqual(expectedTypes),
                    string.Join(" ", actual.Select(t => t.Name)));
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"tok {Describe(code)} => 意外异常", false, ex.Message);
            }
        }

        // 插值帧词法断言（M53）：源码 token 流是否含 InterpolationStartToken
        // （\$ 转义的字面 $ 不触发帧机制，与 M32 的标记判定同语义）
        private static void ExpectInterp(string code, bool expected)
        {
            try
            {
                var tokens = new Lexer().Tokenize(code);
                var hasFrame = tokens.Any(t => t is InterpolationStartToken);
                CaseAssertions.CheckTrue($"interp {Describe(code)}", hasFrame == expected,
                    $"InterpolationStart={(hasFrame ? "有" : "无")}；流={string.Join(" ", tokens.Select(t => t.ToString()))}");
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"interp {Describe(code)} => 意外异常", false, ex.Message);
            }
        }

        private static void ExpectError(string code, string messagePart)
        {
            CaseAssertions.CheckParseError($"err {Describe(code)}",
                () => new Lexer().Tokenize(code), messagePart);
        }

        // AST 断言：解析单个字面量并比对 AST 描述串（与 LiteralParserTests.TestLit 同约定）
        private static void TestLit(string code, string expectedDesc)
        {
            try
            {
                var node = CompilerTestTools.ParseFirstDecl(code);
                CaseAssertions.Check($"ast {Describe(code)}", AstDescribe.Expr(node), expectedDesc);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"ast {Describe(code)} => 意外异常", false, ex.Message);
            }
        }

        private static string Pos(CharPosition p) => $"{p.line}:{p.column}";

        private static string Describe(string code) =>
            code.Replace("\r", "\\r").Replace("\n", "\\n");


        internal static TestSuiteData Spec { get; } = new("MultilineString",
        [
            (nameof(TestContent), TestContent),
            (nameof(TestErrors), TestErrors),
            (nameof(TestQuoteRegression), TestQuoteRegression),
            (nameof(TestSpans), TestSpans),
            (nameof(TestInterpolationFlag), TestInterpolationFlag),
            (nameof(TestAst), TestAst),
        ], sectionTitle: "MultilineString");
    }
}
