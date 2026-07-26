using System;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// throw 语句解析测试
    ///
    /// 覆盖：
    /// 1. 简单 throw
    /// 2. throw 表达式
    /// 3. throw 构造异常
    /// 4. 配合 try-catch 使用
    /// 5. 错误用例
    ///
    /// 驱动方式：Parser.Parse(tokens, new CodeBlockParserLayer(block)) 独立入口，
    /// 源码以 { ... } 包裹。
    /// </summary>
    public class ThrowStatementTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 简单 throw =====
        public static void TestSimpleThrow()
        {
            Console.WriteLine("=== Testing Simple Throw ===");

            TestBlock("{ throw error }",
                "[Throw(Sym(error))]");

            TestBlock("{ throw e }",
                "[Throw(Sym(e))]");

            Console.WriteLine();
        }

        // ===== 2. throw 表达式 =====
        public static void TestThrowExpression()
        {
            Console.WriteLine("=== Testing Throw with Expression ===");

            TestBlock("{ throw new IOException() }",
                "[Throw(New(Sym(IOException), []))]");

            TestBlock("{ throw new IOException(\"File not found\") }",
                "[Throw(New(Sym(IOException), [Str(\"File not found\")]))]");

            TestBlock("{ throw getError() }",
                "[Throw(Call(Sym(getError), []))]");

            Console.WriteLine();
        }

        // ===== 3. 配合 try-catch 使用 =====
        public static void TestThrowInTryCatch()
        {
            Console.WriteLine("=== Testing Throw in Try-Catch ===");

            TestBlock("{\n" +
                      "    try {\n" +
                      "        throw new RuntimeException()\n" +
                      "    } catch (e: RuntimeException) {\n" +
                      "        log(e)\n" +
                      "    }\n" +
                      "}",
                "[Try([Throw(New(Sym(RuntimeException), []))], Catch(e: Sym(RuntimeException), [Call(Sym(log), [Sym(e)])]))]");

            TestBlock("{\n" +
                      "    try {\n" +
                      "        validate(data)\n" +
                      "    } catch (e: ValidationError) {\n" +
                      "        throw new ProcessingError(e)\n" +
                      "    }\n" +
                      "}",
                "[Try([Call(Sym(validate), [Sym(data)])], Catch(e: Sym(ValidationError), [Throw(New(Sym(ProcessingError), [Sym(e)]))]))]");

            Console.WriteLine();
        }

        // ===== 4. throw 在不同位置 =====
        public static void TestThrowInDifferentContexts()
        {
            Console.WriteLine("=== Testing Throw in Different Contexts ===");

            // if 分支中
            TestBlock("{\n" +
                      "    if (error) {\n" +
                      "        throw new Error()\n" +
                      "    }\n" +
                      "}",
                "[IfStmt(Sym(error), [Throw(New(Sym(Error), []))], <none>)]");

            // 循环中
            TestBlock("{\n" +
                      "    for (item in items) {\n" +
                      "        if (invalid(item)) {\n" +
                      "            throw new InvalidItemError(item)\n" +
                      "        }\n" +
                      "    }\n" +
                      "}",
                "[For(item, Sym(items), [IfStmt(Call(Sym(invalid), [Sym(item)]), [Throw(New(Sym(InvalidItemError), [Sym(item)]))], <none>)])]");

            Console.WriteLine();
        }

        // ===== 5. 错误用例 =====
        public static void TestInvalidCases()
        {
            Console.WriteLine("=== Testing Invalid Cases ===");

            // throw 后缺少表达式（这个测试可能会因为遇到 } 而失败）
            // 注：由于 throw 后立即是 }，ExpressionParserLayer 会报错
            TestInvalidBlock("{ throw }",
                "Unexpected token at start of expression");

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
                parser.Parse(tokens, new TestRootParserLayer(), new CodeBlockParserLayer(block));

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
                parser.Parse(tokens, new TestRootParserLayer(), new CodeBlockParserLayer(block));

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
                ThrowStatementASTNode t => $"Throw({DescribeExpression(t.Exception.Expression)})",
                TryCatchFinallyStatementASTNode tryCatch => FormatTryCatch(tryCatch),
                IfStatementASTNode ifStmt => FormatIf(ifStmt),
                LoopStatementASTNode loop => FormatLoop(loop),
                ExpressionStatementASTNode s => s.AssignValue != null
                    ? $"Assign({DescribeExpression(s.Expression.Expression)} = {DescribeExpression(s.AssignValue.Expression)})"
                    : DescribeExpression(s.Expression.Expression),
                ExpressionASTNode e => DescribeExpression(e),
                _ => $"<{node.GetType().Name}>"
            };
        }

        private static string FormatTryCatch(TryCatchFinallyStatementASTNode tryNode)
        {
            var tryBlock = FormatBlock(tryNode.TryBlock);
            var catches = string.Join(", ", tryNode.CatchClauses.Select(FormatCatch));
            var finally_part = tryNode.FinallyBlock != null
                ? $", Finally({tryNode.FinallyParameter}, {FormatBlock(tryNode.FinallyBlock)})"
                : "";
            return $"Try({tryBlock}{(catches.Length > 0 ? ", " + catches : "")}{finally_part})";
        }

        private static string FormatCatch(CatchClauseASTNode catchClause)
        {
            var varName = catchClause.VariableName ?? "_";
            var type = catchClause.ExceptionType.TypeSymbol.symbol.elements[0].name;
            var body = FormatBlock(catchClause.Body);
            return $"Catch({varName}: Sym({type}), {body})";
        }

        private static string FormatIf(IfStatementASTNode ifStmt)
        {
            string elsePart = ifStmt.ElseBranch switch
            {
                null => "<none>",
                CodeBlockASTNode b => FormatBlock(b),
                IfStatementASTNode nested => FormatIf(nested),
                _ => $"<{ifStmt.ElseBranch.GetType().Name}>"
            };
            return $"IfStmt({DescribeExpression(ifStmt.Condition.Expression)}, {FormatBlock(ifStmt.ThenBlock)}, {elsePart})";
        }

        private static string FormatLoop(LoopStatementASTNode loop)
        {
            return loop.Kind switch
            {
                LoopKind.For =>
                    $"For({loop.VariableName}, {DescribeExpression(loop.Iterable!.Expression)}, {FormatBlock(loop.Body)})",
                _ => $"<{loop.Kind}>"
            };
        }

        private static string DescribeExpression(ASTNode? node)
        {
            return node switch
            {
                null => "<null>",
                LiteralExpressionASTNode lit => DescribeExpression(lit.Literal),
                StringLiteralASTNode s => $"Str(\"{s.Value}\")",
                SymbolReferenceASTNode sref => $"Sym({sref.Symbol.symbol.elements[0].name})",
                CallExpressionASTNode c =>
                    $"Call({DescribeExpression(c.Callee.Expression)}, [{string.Join(", ", c.Arguments.Select(a => DescribeExpression(a.Value.Expression)))}])",
                NewExpressionASTNode n =>
                    $"New({DescribeExpression(n.Type)}, [{string.Join(", ", n.Arguments.Select(a => DescribeExpression(a.Value.Expression)))}])",
                TypeReferenceASTNode t => $"Sym({t.TypeSymbol.symbol.elements[0].name})",
                _ => $"<{node.GetType().Name}>"
            };
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Throw Statement Parser Tests                        ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestSimpleThrow();
            TestThrowExpression();
            TestThrowInTryCatch();
            TestThrowInDifferentContexts();
            TestInvalidCases();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            return failCount;
        }
    }
}
