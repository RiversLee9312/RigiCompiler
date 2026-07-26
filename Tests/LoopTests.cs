using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 循环解析测试（roadmap #9，SYNTAX.md §7.3/§7.4）
    ///
    /// 覆盖：
    /// 1. for-each 循环
    /// 2. 范围循环（0 to 10 → RangeExpression）
    /// 3. while 循环
    /// 4. do-while 循环
    /// 5. named 标签与 break@标签 / continue@标签
    /// 6. 嵌套循环
    /// 7. 错误用例
    ///
    /// 驱动方式：Parser.Parse(tokens, new CodeBlockParserLayer(block)) 独立入口，
    /// 源码以 { ... } 包裹。
    /// </summary>
    public class LoopTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. for-each =====
        public static void TestForEachLoops()
        {
            Console.WriteLine("=== Testing for-each Loops ===");

            TestBlock("{ for (item in collection) { print(item) } }",
                "[For(item, Sym(collection), [Call(Sym(print), [Sym(item)])])]");
            // 迭代表达式可以是调用
            TestBlock("{ for (x in getItems(1)) { print(x) } }",
                "[For(x, Call(Sym(getItems), [Int(1,I32)]), [Call(Sym(print), [Sym(x)])])]");

            Console.WriteLine();
        }

        // ===== 2. 范围循环 =====
        public static void TestForRangeLoops()
        {
            Console.WriteLine("=== Testing for-range Loops ===");

            TestBlock("{ for (i in 0 to 10) { print(i) } }",
                "[For(i, Range(Int(0,I32) to Int(10,I32)), [Call(Sym(print), [Sym(i)])])]");

            Console.WriteLine();
        }

        // ===== 3. while =====
        public static void TestWhileLoops()
        {
            Console.WriteLine("=== Testing while Loops ===");

            TestBlock("{ while (condition) { doSomething() } }",
                "[While(Sym(condition), [Call(Sym(doSomething), [])])]");

            Console.WriteLine();
        }

        // ===== 4. do-while =====
        public static void TestDoWhileLoops()
        {
            Console.WriteLine("=== Testing do-while Loops ===");

            TestBlock("{ do { doSomething() } while (condition) }",
                "[DoWhile(Sym(condition), [Call(Sym(doSomething), [])])]");

            Console.WriteLine();
        }

        // ===== 5. named 标签 =====
        public static void TestNamedLoops()
        {
            Console.WriteLine("=== Testing named Loops ===");

            TestBlock("{ for (i in 0 to 10) named outer { break@outer } }",
                "[For(i, Range(Int(0,I32) to Int(10,I32)), named outer, [Break@outer])]");
            TestBlock("{ while (true) named loop { break@loop } }",
                "[While(Bool(True), named loop, [Break@loop])]");
            TestBlock("{ do named loop { continue@loop } while (c) }",
                "[DoWhile(Sym(c), named loop, [Continue@loop])]");

            Console.WriteLine();
        }

        // ===== 6. 嵌套循环 =====
        public static void TestNestedLoops()
        {
            Console.WriteLine("=== Testing Nested Loops ===");

            // SYNTAX §7.4 示例
            TestBlock("{\n" +
                      "    for (i in 0 to 10) named outer {\n" +
                      "        for (j in 0 to 10) named inner {\n" +
                      "            if (someCondition) {\n" +
                      "                break@outer\n" +
                      "            }\n" +
                      "        }\n" +
                      "    }\n" +
                      "}",
                "[For(i, Range(Int(0,I32) to Int(10,I32)), named outer, " +
                "[For(j, Range(Int(0,I32) to Int(10,I32)), named inner, " +
                "[IfStmt(Sym(someCondition), [Break@outer], <none>)])])]");

            Console.WriteLine();
        }

        // ===== 7. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Loop Error Cases (expect ParserException) ===");

            // for 缺 (
            TestError("{ for item in collection { } }", "for 缺 (");
            // for 缺 in
            TestError("{ for (item collection) { } }", "for 缺 in");
            // while 缺 (
            TestError("{ while condition { } }", "while 缺 (");
            // do 缺 while
            TestError("{ do { } cond }", "do 缺 while");
            // 范围循环缺终点
            TestError("{ for (i in 0 to ) { } }", "范围循环缺终点");
            // 循环体缺 {
            TestError("{ for (item in collection) print(item) }", "循环体缺 {");

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        // 解析一段 { ... } 代码块，返回块节点
        private static CodeBlockASTNode ParseBlock(string code)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(code);
            var parser = new Parser();
            var block = new CodeBlockASTNode(null);
            parser.Parse(tokens, new TestRootParserLayer(), new CodeBlockParserLayer(block));
            return block;
        }

        // 结构校验：块内语句的描述串必须与期望完全一致
        private static void TestBlock(string code, string expectedDesc)
        {
            try
            {
                var block = ParseBlock(code);
                string actual = DescribeBlock(block);
                if (actual == expectedDesc)
                {
                    Pass(code, actual);
                }
                else
                {
                    Fail(code, $"expected {expectedDesc}, got {actual}");
                }
            }
            catch (Exception ex)
            {
                Fail(code, $"unexpected exception: {ex.Message}");
            }
        }

        // 错误校验：解析必须抛出 ParserException
        private static void TestError(string code, string reason)
        {
            try
            {
                ParseBlock(code);
                Fail(code, $"expected ParserException ({reason}), but parse succeeded");
            }
            catch (ParserException)
            {
                Console.WriteLine($"  [PASS] {code}  (rejected: {reason})");
                passCount++;
            }
            catch (Exception ex)
            {
                Fail(code, $"expected ParserException ({reason}), got {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Pass(string code, string result)
        {
            Console.WriteLine($"  [PASS] {code}");
            Console.WriteLine($"      => {result}");
            passCount++;
        }

        private static void Fail(string code, string message)
        {
            Console.WriteLine($"  [FAIL] {code}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== AST 描述 =====

        // 描述代码块：[stmt, stmt, ...]
        private static string DescribeBlock(CodeBlockASTNode block)
        {
            return "[" + string.Join(", ", block.Children.Select(DescribeStatement)) + "]";
        }

        // 描述语句节点
        private static string DescribeStatement(ASTNode node)
        {
            return node switch
            {
                AssignStatementASTNode a =>
                    $"Assign({DescribeExpression(a.Target.Expression)} = {DescribeExpression(a.Value.Expression)})",
                ReturnStatementASTNode r =>
                    $"Return{(r.Label != null ? "@" + r.Label : "")}" +
                    $"{(r.Value != null ? $"({DescribeExpression(r.Value!.Expression)})" : "")}",
                LoopControlStatementASTNode l =>
                    $"{(l.IsBreak ? "Break" : "Continue")}{(l.Label != null ? "@" + l.Label : "")}",
                IfStatementASTNode i => DescribeIf(i),
                LoopStatementASTNode l => DescribeLoop(l),
                ExpressionRootASTNode root => DescribeExpression(root.Expression),
                ExpressionASTNode e => DescribeExpression(e),
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 描述 if 语句
        private static string DescribeIf(IfStatementASTNode i)
        {
            string elsePart = i.ElseBranch switch
            {
                null => "<none>",
                CodeBlockASTNode b => DescribeBlock(b),
                IfStatementASTNode nested => DescribeIf(nested),
                _ => $"<{i.ElseBranch.GetType().Name}>"
            };
            return $"IfStmt({DescribeExpression(i.Condition.Expression)}, {DescribeBlock(i.ThenBlock)}, {elsePart})";
        }

        // 描述循环语句：For(var, iterable, [named,] [body]) / While(cond, ...) / DoWhile(cond, ...)
        private static string DescribeLoop(LoopStatementASTNode l)
        {
            string label = l.Label != null ? $", named {l.Label}" : "";
            return l.Kind switch
            {
                LoopKind.For =>
                    $"For({l.VariableName}, {DescribeExpression(l.Iterable!.Expression)}{label}, {DescribeBlock(l.Body)})",
                LoopKind.While =>
                    $"While({DescribeExpression(l.Condition!.Expression)}{label}, {DescribeBlock(l.Body)})",
                _ =>
                    $"DoWhile({DescribeExpression(l.Condition!.Expression)}{label}, {DescribeBlock(l.Body)})"
            };
        }

        // 把表达式节点描述为紧凑的结构串，用于精确比对
        private static string DescribeExpression(ASTNode? node)
        {
            return node switch
            {
                null => "<null>",
                LiteralExpressionASTNode lit => DescribeExpression(lit.Literal),
                IntLiteralASTNode i => $"Int({i.Value},{i.IntType}{(i.IsHex ? ",hex" : "")})",
                StringLiteralASTNode s => $"Str(\"{s.Value}\")",
                BoolLiteralASTNode b => $"Bool({b.Value})",
                SymbolReferenceASTNode sref => $"Sym({DescribeSymbol(sref.Symbol.symbol)})",
                BinaryExpressionASTNode b =>
                    $"Binary({DescribeExpression(b.Left.Expression)} {b.Operator} {DescribeExpression(b.Right.Expression)})",
                GroupExpressionASTNode g => $"Group({DescribeExpression(g.InnerExpression.Expression)})",
                CallExpressionASTNode c =>
                    $"Call({DescribeExpression(c.Callee.Expression)}, [{string.Join(", ", c.Arguments.Select(DescribeArgument))}])",
                RangeExpressionASTNode r =>
                    $"Range({DescribeExpression(r.From.Expression)} to {DescribeExpression(r.To.Expression)})",
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 描述实参（具名时为 name:value）
        private static string DescribeArgument(ArgumentASTNode arg)
        {
            return arg.Name != null
                ? $"{arg.Name}:{DescribeExpression(arg.Value.Expression)}"
                : DescribeExpression(arg.Value.Expression);
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
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Loop Tests                        ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestForEachLoops();
            TestForRangeLoops();
            TestWhileLoops();
            TestDoWhileLoops();
            TestNamedLoops();
            TestNestedLoops();
            TestErrorCases();

            Console.WriteLine($"=== Loop Tests Complete: {passCount} passed, {failCount} failed ===\n");

            return failCount;
        }
    }
}
