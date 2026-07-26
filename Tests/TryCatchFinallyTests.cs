using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// Try-Catch-Finally 语句解析测试（roadmap #10，SYNTAX.md §8）
    ///
    /// 覆盖：
    /// 1. 简单 try-catch
    /// 2. 多个 catch 子句
    /// 3. 丢弃异常变量（_）
    /// 4. try-finally
    /// 5. try-catch-finally
    /// 6. 嵌套 try
    /// 7. 错误用例
    ///
    /// 驱动方式：Parser.Parse(tokens, new CodeBlockParserLayer(block)) 独立入口，
    /// 源码以 { ... } 包裹。
    /// </summary>
    public class TryCatchFinallyTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 简单 try-catch =====
        public static void TestSimpleTryCatch()
        {
            Console.WriteLine("=== Testing Simple Try-Catch ===");

            TestBlock("{ try { riskyOperation() } catch (e: IOException) { handleIO(e) } }",
                "[Try([Call(Sym(riskyOperation), [])], Catch(e: Sym(IOException), [Call(Sym(handleIO), [Sym(e)])]))]");

            Console.WriteLine();
        }

        // ===== 2. 多个 catch 子句 =====
        public static void TestMultipleCatch()
        {
            Console.WriteLine("=== Testing Multiple Catch Clauses ===");

            // SYNTAX.md §8 示例
            TestBlock("{\n" +
                      "    try {\n" +
                      "        riskyOperation()\n" +
                      "    } catch (e: IOException) {\n" +
                      "        handleIO(e)\n" +
                      "    } catch (e: RuntimeException) {\n" +
                      "        handleRuntime(e)\n" +
                      "    }\n" +
                      "}",
                "[Try([Call(Sym(riskyOperation), [])], " +
                "Catch(e: Sym(IOException), [Call(Sym(handleIO), [Sym(e)])]), " +
                "Catch(e: Sym(RuntimeException), [Call(Sym(handleRuntime), [Sym(e)])]))]");

            Console.WriteLine();
        }

        // ===== 3. 丢弃异常变量（_） =====
        public static void TestDiscardedExceptionVariable()
        {
            Console.WriteLine("=== Testing Discarded Exception Variable ===");

            TestBlock("{ try { doSomething() } catch (_: RuntimeException) { log() } }",
                "[Try([Call(Sym(doSomething), [])], Catch(_: Sym(RuntimeException), [Call(Sym(log), [])]))]");

            Console.WriteLine();
        }

        // ===== 4. try-finally =====
        public static void TestTryFinally()
        {
            Console.WriteLine("=== Testing Try-Finally ===");

            TestBlock("{ try { openFile() } finally(e) { cleanup(e) } }",
                "[Try([Call(Sym(openFile), [])], Finally(e, [Call(Sym(cleanup), [Sym(e)])]))]");

            Console.WriteLine();
        }

        // ===== 5. try-catch-finally =====
        public static void TestTryCatchFinally()
        {
            Console.WriteLine("=== Testing Try-Catch-Finally ===");

            // SYNTAX.md §8 完整示例
            TestBlock("{\n" +
                      "    try {\n" +
                      "        riskyOperation()\n" +
                      "    } catch (e: IOException) {\n" +
                      "        handleIO(e)\n" +
                      "    } catch (_: RuntimeException) {\n" +
                      "        doNothing()\n" +
                      "    } finally(e) {\n" +
                      "        cleanup()\n" +
                      "    }\n" +
                      "}",
                "[Try([Call(Sym(riskyOperation), [])], " +
                "Catch(e: Sym(IOException), [Call(Sym(handleIO), [Sym(e)])]), " +
                "Catch(_: Sym(RuntimeException), [Call(Sym(doNothing), [])]), " +
                "Finally(e, [Call(Sym(cleanup), [])]))]");

            Console.WriteLine();
        }

        // ===== 6. 嵌套 try =====
        public static void TestNestedTry()
        {
            Console.WriteLine("=== Testing Nested Try ===");

            TestBlock("{\n" +
                      "    try {\n" +
                      "        try {\n" +
                      "            inner()\n" +
                      "        } catch (e: InnerException) {\n" +
                      "            handleInner(e)\n" +
                      "        }\n" +
                      "    } catch (e: OuterException) {\n" +
                      "        handleOuter(e)\n" +
                      "    }\n" +
                      "}",
                "[Try([Try([Call(Sym(inner), [])], " +
                "Catch(e: Sym(InnerException), [Call(Sym(handleInner), [Sym(e)])]))], " +
                "Catch(e: Sym(OuterException), [Call(Sym(handleOuter), [Sym(e)])]))]");

            Console.WriteLine();
        }

        // ===== 7. 错误用例 =====
        public static void TestInvalidCases()
        {
            Console.WriteLine("=== Testing Invalid Cases ===");

            // try 后缺少 catch 或 finally
            TestInvalidBlock("{ try { operation() } }",
                "try 语句必须至少有一个 catch 或一个 finally 子句");

            // catch 后缺少类型
            TestInvalidBlock("{ try { operation() } catch (e) { handle(e) } }",
                "Expected ':' after catch variable");

            // finally 后缺少参数
            TestInvalidBlock("{ try { operation() } finally { cleanup() } }",
                "Expected '(' after 'finally'");

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
            if (node is TryCatchFinallyStatementASTNode tryNode)
            {
                var tryBlock = FormatBlock(tryNode.TryBlock);
                var catches = string.Join(", ", tryNode.CatchClauses.Select(FormatCatch));
                var finally_part = tryNode.FinallyBlock != null
                    ? $", Finally({tryNode.FinallyParameter}, {FormatBlock(tryNode.FinallyBlock)})"
                    : "";
                return $"Try({tryBlock}{(catches.Length > 0 ? ", " + catches : "")}{finally_part})";
            }
            else if (node is ExpressionASTNode expr)
            {
                return DescribeExpression(expr);
            }
            else
            {
                return $"Unknown({node.NodeType})";
            }
        }

        private static string FormatCatch(CatchClauseASTNode catchClause)
        {
            var varName = catchClause.VariableName ?? "_";
            // 检查 ExceptionType 和嵌套字段是否为 null
            if (catchClause.ExceptionType == null ||
                catchClause.ExceptionType.TypeSymbol == null ||
                catchClause.ExceptionType.TypeSymbol.symbol == null)
            {
                return $"Catch({varName}: <null type>, {FormatBlock(catchClause.Body)})";
            }
            var type = DescribeSymbol(catchClause.ExceptionType.TypeSymbol.symbol);
            var body = FormatBlock(catchClause.Body);
            return $"Catch({varName}: Sym({type}), {body})";
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
            Console.WriteLine("║  Try-Catch-Finally Parser Tests (roadmap #10, P2)    ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestSimpleTryCatch();
            TestMultipleCatch();
            TestDiscardedExceptionVariable();
            TestTryFinally();
            TestTryCatchFinally();
            TestNestedTry();
            TestInvalidCases();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }
}
