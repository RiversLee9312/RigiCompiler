using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// Seq 块解析测试（roadmap #11，SYNTAX.md §6）
    ///
    /// 覆盖：
    /// 1. 简单 seq 块
    /// 2. volatile seq
    /// 3. using 资源绑定（单个/多个）
    /// 4. named 标签
    /// 5. 组合：volatile + using + named
    /// 6. seq 作为表达式（return@seq）
    /// 7. 错误用例
    ///
    /// 驱动方式：Parser.Parse(tokens, new CodeBlockParserLayer(block)) 独立入口，
    /// 源码以 { ... } 包裹。
    /// </summary>
    public class SeqBlockTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 简单 seq 块 =====
        public static void TestSimpleSeq()
        {
            Console.WriteLine("=== Testing Simple Seq Blocks ===");

            TestBlock("{ seq { var x = 1 } }",
                "[Seq([Var(x = Int(1,I32))])]");

            TestBlock("{ seq { var x = 1\nvar y = 2 } }",
                "[Seq([Var(x = Int(1,I32)), Var(y = Int(2,I32))])]");

            Console.WriteLine();
        }

        // ===== 2. volatile seq =====
        public static void TestVolatileSeq()
        {
            Console.WriteLine("=== Testing Volatile Seq ===");

            TestBlock("{ volatile seq { operation() } }",
                "[Seq(volatile, [Call(Sym(operation), [])])]");

            Console.WriteLine();
        }

        // ===== 3. using 资源绑定 =====
        public static void TestUsingBindings()
        {
            Console.WriteLine("=== Testing Using Bindings ===");

            // 单个 using
            TestBlock("{ seq using(const file = open()) { use(file) } }",
                "[Seq(using(const file = Call(Sym(open), [])), [Call(Sym(use), [Sym(file)])])]");

            // 多个 using
            TestBlock("{\n" +
                      "    seq using(const f = openFile())\n" +
                      "    using(var s = openStream(f)) {\n" +
                      "        read(s)\n" +
                      "    }\n" +
                      "}",
                "[Seq(using(const f = Call(Sym(openFile), [])), " +
                "using(var s = Call(Sym(openStream), [Sym(f)])), " +
                "[Call(Sym(read), [Sym(s)])])]");

            // using 带类型标注
            TestBlock("{ seq using(const res: Resource = get()) { use(res) } }",
                "[Seq(using(const res: Resource = Call(Sym(get), [])), [Call(Sym(use), [Sym(res)])])]");

            Console.WriteLine();
        }

        // ===== 4. named 标签 =====
        public static void TestNamedLabel()
        {
            Console.WriteLine("=== Testing Named Labels ===");

            TestBlock("{ seq named myBlock { compute() } }",
                "[Seq(named myBlock, [Call(Sym(compute), [])])]");

            TestBlock("{ seq named outer { seq named inner { work() } } }",
                "[Seq(named outer, [Seq(named inner, [Call(Sym(work), [])])])]");

            Console.WriteLine();
        }

        // ===== 5. 组合 =====
        public static void TestCombinations()
        {
            Console.WriteLine("=== Testing Combinations ===");

            // volatile + using + named
            TestBlock("{ volatile seq using(const x = init()) named block { process(x) } }",
                "[Seq(volatile, using(const x = Call(Sym(init), [])), named block, [Call(Sym(process), [Sym(x)])])]");

            // 多个 using + named
            TestBlock("{\n" +
                      "    seq using(const a = getA())\n" +
                      "    using(const b = getB())\n" +
                      "    named mySeq {\n" +
                      "        work(a, b)\n" +
                      "    }\n" +
                      "}",
                "[Seq(using(const a = Call(Sym(getA), [])), " +
                "using(const b = Call(Sym(getB), [])), " +
                "named mySeq, " +
                "[Call(Sym(work), [Sym(a), Sym(b)])])]");

            Console.WriteLine();
        }

        // ===== 6. seq 作为表达式（return@seq）=====
        public static void TestSeqAsExpression()
        {
            Console.WriteLine("=== Testing Seq as Expression ===");

            // TODO: seq 作为表达式需要在 ExpressionParserLayer 中识别
            // 目前 seq 仅作为语句工作
            Console.WriteLine("  (Seq as expression: TODO - requires ExpressionParserLayer integration)");

            Console.WriteLine();
        }

        // ===== 7. 错误用例 =====
        public static void TestInvalidCases()
        {
            Console.WriteLine("=== Testing Invalid Cases ===");

            // volatile 后没有 seq
            TestInvalidBlock("{ volatile { operation() } }",
                "Expected 'seq' after 'volatile'");

            // using 后缺少 (
            TestInvalidBlock("{ seq using const x = 1 { } }",
                "Expected '(' after 'using'");

            // using 内不是 var/const
            TestInvalidBlock("{ seq using(x = 1) { } }",
                "Expected 'const' or 'var' in using clause");

            // named 标签以数字开头
            TestInvalidBlock("{ seq named 123block { } }",
                "Label name cannot start with a digit");

            Console.WriteLine();
        }

        // ===== 辅助方法 =====

        private static void TestBlock(string source, string expected)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var block = new CodeBlockASTNode(null);
                var parser = new Parser();
                parser.Parse(tokens, new CodeBlockParserLayer(block));

                var formatted = FormatBlock(block);
                if (formatted == expected)
                {
                    Console.WriteLine($"PASS: {source.Replace("\n", "\\n")}");
                    passCount++;
                }
                else
                {
                    Console.WriteLine($"FAIL: {source.Replace("\n", "\\n")}");
                    Console.WriteLine($"  Expected: {expected}");
                    Console.WriteLine($"  Got:      {formatted}");
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL (Exception): {source.Replace("\n", "\\n")}");
                Console.WriteLine($"  Expected: {expected}");
                Console.WriteLine($"  Exception: {ex.Message}");
                failCount++;
            }
        }

        private static void TestInvalidBlock(string source, string expectedError)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var block = new CodeBlockASTNode(null);
                var parser = new Parser();
                parser.Parse(tokens, new CodeBlockParserLayer(block));

                Console.WriteLine($"FAIL: {source.Replace("\n", "\\n")} (应该失败但成功了)");
                Console.WriteLine($"  Expected error: {expectedError}");
                failCount++;
            }
            catch (ParserException ex)
            {
                if (ex.Message.Contains(expectedError))
                {
                    Console.WriteLine($"PASS: {source.Replace("\n", "\\n")} (正确失败)");
                    passCount++;
                }
                else
                {
                    Console.WriteLine($"FAIL: {source.Replace("\n", "\\n")} (错误信息不匹配)");
                    Console.WriteLine($"  Expected error: {expectedError}");
                    Console.WriteLine($"  Got error: {ex.Message}");
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {source.Replace("\n", "\\n")} (意外异常类型)");
                Console.WriteLine($"  Expected error: {expectedError}");
                Console.WriteLine($"  Got exception: {ex.Message}");
                failCount++;
            }
        }

        private static string FormatBlock(CodeBlockASTNode block)
        {
            var statements = block.Children.Select(FormatStatement).ToList();
            return $"[{string.Join(", ", statements)}]";
        }

        private static string FormatStatement(ASTNode node)
        {
            return node switch
            {
                SeqBlockStatementASTNode seq => FormatSeq(seq),
                VariableDeclarationASTNode v => FormatVarDecl(v),
                ReturnStatementASTNode r => FormatReturn(r),
                ExpressionASTNode e => DescribeExpression(e),
                _ => $"<{node.GetType().Name}>"
            };
        }

        private static string FormatSeq(SeqBlockStatementASTNode seq)
        {
            var parts = new List<string>();

            if (seq.IsVolatile)
            {
                parts.Add("volatile");
            }

            foreach (var binding in seq.UsingBindings)
            {
                var constVar = binding.IsConst ? "const" : "var";
                var type = binding.Type != null
                    ? $": {binding.Type.TypeSymbol.symbol.elements[0].name} "
                    : " ";
                var init = DescribeExpression(binding.Initializer);
                parts.Add($"using({constVar} {binding.VariableName}{type}= {init})");
            }

            if (seq.Label != null)
            {
                parts.Add($"named {seq.Label}");
            }

            var body = FormatBlock(seq.Body);
            parts.Add(body);

            return $"Seq({string.Join(", ", parts)})";
        }

        private static string FormatVarDecl(VariableDeclarationASTNode v)
        {
            var init = v.Initializer != null ? $" = {DescribeExpression(v.Initializer)}" : "";
            return $"Var({v.Name}{init})";
        }

        private static string FormatReturn(ReturnStatementASTNode r)
        {
            var label = r.Label != null ? $"@{r.Label}" : "";
            var value = r.Value != null ? $"({DescribeExpression(r.Value)})" : "";
            return $"Return{label}{value}";
        }

        // 把表达式节点描述为紧凑的结构串
        private static string DescribeExpression(ASTNode? node)
        {
            return node switch
            {
                null => "<null>",
                LiteralExpressionASTNode lit => DescribeExpression(lit.LiteralNode),
                IntLiteralASTNode i => $"Int({i.Value},{i.IntType})",
                StringLiteralASTNode s => $"Str(\"{s.Value}\")",
                BoolLiteralASTNode b => $"Bool({b.Value})",
                SymbolReferenceASTNode sref => $"Sym({DescribeSymbol(sref.Symbol.symbol)})",
                CallExpressionASTNode c =>
                    $"Call({DescribeExpression(c.Callee)}, [{string.Join(", ", c.Arguments.Select(a => DescribeExpression(a.Value)))}])",
                SeqBlockStatementASTNode seq => FormatSeq(seq),
                _ => $"<{node.GetType().Name}>"
            };
        }

        private static string DescribeSymbol(Symbol symbol)
        {
            var parts = new List<string>();
            foreach (var element in symbol.elements)
            {
                string part = element.name;
                if (element.generics.Count > 0)
                {
                    part += "<" + string.Join(",", element.generics.Select(g => DescribeSymbol(g))) + ">";
                }
                parts.Add(part);
            }
            return string.Join(".", parts);
        }

        // ===== 入口 =====
        public static void RunAll()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Seq Block Parser Tests (roadmap #11, P2)            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestSimpleSeq();
            TestVolatileSeq();
            TestUsingBindings();
            TestNamedLabel();
            TestCombinations();
            TestSeqAsExpression();
            TestInvalidCases();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }
}
