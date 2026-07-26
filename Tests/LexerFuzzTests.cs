using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// Lexer fuzz 测试（M25）：Slash 层（/、/=、//、/*）、EOF 支持、注释处理的高强度验证。
    ///
    /// 三层验证：
    /// 1. 固定用例：除法/注释/EOF/未闭合结构的精确 token 序列断言；
    /// 2. 随机 fuzz（固定种子，可复现）：纯随机字符流 + 结构化片段拼接 + 合法源码变异，
    ///    校验不变量——不崩（只允许 LexerException）、EOF 存在且唯一、位置单调不回退；
    /// 3. Parser 集成：注释 token 由 Parser 主循环统一跳过，语句中的注释不再炸 Parser。
    ///
    /// fuzz 循环期间控制台重定向到 TextWriter.Null（避免几十万行 VERBOSE 日志）。
    /// </summary>
    public static class LexerFuzzTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 固定用例：精确 token 序列 =====

        public static void TestFixedCases()
        {
            Console.WriteLine("=== Testing Fixed Lexer Cases (slash/comment/EOF) ===");

            // 除法（M25 前直接报 "Incorrect comment block or line start"）
            ExpectTokens("a / b", "W(a) N(/) W(b) EOF");
            ExpectTokens("a/b", "W(a) N(/) W(b) EOF");
            ExpectTokens("a / b / c", "W(a) N(/) W(b) N(/) W(c) EOF");
            // 除法赋值
            ExpectTokens("a /= b", "W(a) N(/=) W(b) EOF");
            // 行注释（无尾换行也要正确收尾）
            ExpectTokens("// hello", "C( hello) EOF");
            ExpectTokens("a // c\nb", "W(a) C( c) LB W(b) EOF");
            // 行注释不吞换行（换行是语句终止符）
            ExpectTokens("// c\na", "C( c) LB W(a) EOF");
            // 块注释
            ExpectTokens("/* block */a", "C( block ) W(a) EOF");
            ExpectTokens("/* multi\nline */a", "C( multi\nline ) W(a) EOF");
            ExpectTokens("/**/a", "C() W(a) EOF");
            // 表达式中间的块注释
            ExpectTokens("a /* c */ b", "W(a) C( c ) W(b) EOF");
            // 斜杠家族相邻形态
            ExpectTokens("a //", "W(a) C() EOF");
            ExpectTokens("a /= b", "W(a) N(/=) W(b) EOF");
            // 空输入：只有 EOF
            ExpectTokens("", "EOF");
            // 纯空白
            ExpectTokens("   \n  ", "LB EOF");

            Console.WriteLine();
        }

        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Lexer Error Cases (expect LexerException) ===");

            // 未闭合块注释（M25 前静默丢 token）
            ExpectLexerError("/* unterminated");
            ExpectLexerError("a /* unterminated");
            // 未闭合字符串
            ExpectLexerError("\"unterminated");
            // 非法字符
            ExpectLexerError("`");

            Console.WriteLine();
        }

        // ===== 2. 随机 fuzz =====

        private static readonly char[] CharPool =
            "abcxyz_019 \n\n\t/=*.\"'\\(){}[]<>,:@$+-".ToCharArray();

        private static readonly string[] FragmentPool =
        {
            "var", "const", "func", "if", "class", "import", "return",
            " ", "  ", "\n", "\n\n", "x", "abc", "_p", "0", "42", "0x1F",
            "\"str\"", "\"a\\nb\"", "'c'",
            "// comment\n", "//c", "/* block */", "/* multi\nline */",
            "/", "/=", "*", "+", "-", "=", "==", "<", ">", "\\<", "(", ")", "{", "}",
            "[", "]", ".", ",", ":", "@", "->", "?"
        };

        private static readonly string[] ValidSeeds =
        {
            "var x = 42\nfunc main() { print(x) }\n",
            "class A { pub var name: String }\n",
            "for (i in 0 to 10) { print(i) }\n",
            "import core.collections.{List, Map}\n",
            "var r = 1 + (2 * 3)\n",
            "var q = a / b\n"
        };

        public static void TestRandomFuzz()
        {
            Console.WriteLine("=== Testing Random Fuzz (seeded, 6000 cases) ===");

            var rng = new Random(20260726);
            int failedBefore = failCount;

            // fuzz 循环期间屏蔽 VERBOSE 日志（每字符一条，百万行级）
            var originalOut = Console.Out;
            Console.SetOut(TextWriter.Null);
            try
            {
                // 2a. 纯随机字符流 ×2500
                for (int i = 0; i < 2500; i++)
                {
                    FuzzOne(RandomFromPool(rng, CharPool, 200), "纯随机");
                }
                // 2b. 结构化片段拼接 ×2500
                for (int i = 0; i < 2500; i++)
                {
                    FuzzOne(RandomFromFragments(rng), "结构化");
                }
                // 2c. 合法源码变异 ×1000
                for (int i = 0; i < 1000; i++)
                {
                    FuzzOne(Mutate(rng, ValidSeeds[rng.Next(ValidSeeds.Length)]), "变异");
                }
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            int fuzzCases = 2500 + 2500 + 1000;
            int fuzzFailures = failCount - failedBefore;
            Console.WriteLine($"  fuzz 汇总：{fuzzCases - fuzzFailures} passed, {fuzzFailures} failed");
            Console.WriteLine();
        }

        // 单个 fuzz 用例：不崩（只允许 LexerException）+ 不变量校验
        private static void FuzzOne(string source, string category)
        {
            try
            {
                var tokens = new Lexer().Tokenize(source);
                string? problem = CheckInvariants(tokens);
                if (problem != null)
                {
                    failCount++;
                    ReportFuzzFailure(source, category, problem);
                }
            }
            catch (LexerException)
            {
                // 非法输入的合法拒绝
            }
            catch (Exception ex)
            {
                failCount++;
                ReportFuzzFailure(source, category,
                    $"非 LexerException 异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // fuzz 失败详情（恢复到真实控制台后排队打印——此处直接缓冲到静态列表，
        // RunAll 末尾统一输出前 10 条，避免刷屏）
        private static readonly List<string> fuzzFailureLog = new();

        private static void ReportFuzzFailure(string source, string category, string problem)
        {
            fuzzFailureLog.Add(
                $"  [FAIL] ({category}) {DescribeSource(source)}\n      => {problem}");
        }

        // 不变量：EOF 存在且唯一（恰在末尾）；token 位置单调不回退、范围不颠倒
        private static string? CheckInvariants(List<Token> tokens)
        {
            if (tokens.Count == 0) return "token 流为空（至少应有 EOF）";
            if (tokens[tokens.Count - 1] is not EndOfFileToken) return "末尾缺少 EOF token";
            if (tokens.Count(t => t is EndOfFileToken) != 1) return "EOF token 不唯一";

            long lastLine = 0;
            foreach (var t in tokens)
            {
                if (t.CharRange.Start.line < lastLine)
                    return $"token 位置回退 @ {t} (line {t.CharRange.Start.line} < {lastLine})";
                lastLine = t.CharRange.Start.line;
                if (t.CharRange.End.line < t.CharRange.Start.line)
                    return $"token 范围颠倒 @ {t}";
            }
            return null;
        }

        private static string RandomFromPool(Random rng, char[] pool, int maxLen)
        {
            int len = rng.Next(0, maxLen);
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++)
            {
                sb.Append(pool[rng.Next(pool.Length)]);
            }
            return sb.ToString();
        }

        private static string RandomFromFragments(Random rng)
        {
            int count = rng.Next(1, 20);
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                sb.Append(FragmentPool[rng.Next(FragmentPool.Length)]);
            }
            return sb.ToString();
        }

        // 单点变异 ×1-3：随机位置插入斜杠家族/引号/换行等高风险字符
        private static string Mutate(Random rng, string seed)
        {
            var chars = seed.ToCharArray().ToList();
            var risky = new[] { '/', '/', '*', '"', '\'', '\n', '\\', '=' };
            int mutations = rng.Next(1, 4);
            for (int i = 0; i < mutations; i++)
            {
                chars.Insert(rng.Next(chars.Count + 1), risky[rng.Next(risky.Length)]);
            }
            return new string(chars.ToArray());
        }

        // ===== 3. Parser 集成：注释统一跳过 =====

        public static void TestParserIntegration()
        {
            Console.WriteLine("=== Testing Parser Integration (comments skipped centrally) ===");

            // 语句中的行注释（M25 前：CommentToken 炸掉 CodeBlock 等中间层）
            ExpectParseStatements(
                "{ var x = 1 // trailing\n var y = 2 }", 2,
                "行尾注释不干扰下一条语句");
            ExpectParseStatements(
                "{ /* block */ var x = 1\n var y /* mid */ = 2 }", 2,
                "块注释出现在语句任意位置");
            ExpectParseStatements(
                "{ // leading\n var x = 1 }", 1,
                "块首行注释");
            ExpectParseStatements(
                "{ var q = a / b // div\n var r = 1 }", 2,
                "除法与行尾注释组合");

            Console.WriteLine();
        }

        private static void ExpectParseStatements(string code, int expectedCount, string name)
        {
            try
            {
                var tokens = new Lexer().Tokenize(code);
                var block = new CodeBlockASTNode(null);
                new Parser().Parse(tokens, new TestRootParserLayer(), new CodeBlockParserLayer(block));
                if (block.Children.Count == expectedCount)
                {
                    Console.WriteLine($"  [PASS] {name}");
                    passCount++;
                }
                else
                {
                    Fail(code, $"{name}: expected {expectedCount} statements, got {block.Children.Count}");
                }
            }
            catch (Exception ex)
            {
                Fail(code, $"{name}: unexpected {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ===== 测试辅助 =====

        private static string DescribeTokens(List<Token> tokens)
        {
            return string.Join(" ", tokens.Select(t => t switch
            {
                WordToken w => $"W({w.Content})",
                StringToken s => $"S({s.Content})",
                CommentToken c => $"C({c.Content})",
                LineBreakToken => "LB",
                NotationToken n => $"N({n.Content})",
                EndOfFileToken => "EOF",
                _ => $"?{t.Type}"
            }));
        }

        private static void ExpectTokens(string code, string expected)
        {
            try
            {
                var tokens = new Lexer().Tokenize(code);
                string actual = DescribeTokens(tokens);
                if (actual == expected)
                {
                    Console.WriteLine($"  [PASS] {DescribeSource(code)}  => {actual}");
                    passCount++;
                }
                else
                {
                    Fail(code, $"expected {expected}, got {actual}");
                }
            }
            catch (Exception ex)
            {
                Fail(code, $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ExpectLexerError(string code)
        {
            try
            {
                new Lexer().Tokenize(code);
                Fail(code, "expected LexerException, but tokenize succeeded");
            }
            catch (LexerException)
            {
                Console.WriteLine($"  [PASS] {DescribeSource(code)}  (rejected)");
                passCount++;
            }
            catch (Exception ex)
            {
                Fail(code, $"expected LexerException, got {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string DescribeSource(string code)
        {
            return $"\"{code.Replace("\n", "\\n")}\"";
        }

        private static void Fail(string code, string message)
        {
            Console.WriteLine($"  [FAIL] {DescribeSource(code)}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Lexer Fuzz Tests                  ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;
            fuzzFailureLog.Clear();

            TestFixedCases();
            TestErrorCases();
            TestRandomFuzz();
            TestParserIntegration();

            // fuzz 失败详情限量输出（前 10 条）
            foreach (var line in fuzzFailureLog.Take(10))
            {
                Console.WriteLine(line);
            }
            if (fuzzFailureLog.Count > 10)
            {
                Console.WriteLine($"  ...（其余 {fuzzFailureLog.Count - 10} 条省略）");
            }

            Console.WriteLine($"=== Lexer Fuzz Tests Complete: {passCount} passed, {failCount} failed ===");
            return failCount;
        }
    }
}
