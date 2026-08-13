using System;

namespace RigiCompiler.Tests
{
    // Try-Catch-Finally 语句解析测试（roadmap #10，SYNTAX.md §8）：代码块独立驱动
    // （TestHarness.ParseBlock），断言 AstDescribe 精确描述串。
    // 覆盖：简单 try-catch / 多个 catch 子句 / 丢弃异常变量（_）/ try-finally /
    // try-catch-finally / 嵌套 try / 错误用例。
    public class TryCatchFinallyTests
    {
        // ===== 1. 简单 try-catch =====
        public static void TestSimpleTryCatch()
        {
            TestHarness.Section("Testing Simple Try-Catch");

            TestBlock("{ try { riskyOperation() } catch (e: IOException) { handleIO(e) } }",
                "[Try([Path(riskyOperation(), [])], [Catch(e: IOException, [Path(handleIO(Path(e, [])), [])])])]");

            TestHarness.Blank();
        }

        // ===== 2. 多个 catch 子句 =====
        public static void TestMultipleCatch()
        {
            TestHarness.Section("Testing Multiple Catch Clauses");

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
                "[Try([Path(riskyOperation(), [])], " +
                "[Catch(e: IOException, [Path(handleIO(Path(e, [])), [])]), " +
                "Catch(e: RuntimeException, [Path(handleRuntime(Path(e, [])), [])])])]");

            TestHarness.Blank();
        }

        // ===== 3. 丢弃异常变量（_） =====
        public static void TestDiscardedExceptionVariable()
        {
            TestHarness.Section("Testing Discarded Exception Variable");

            TestBlock("{ try { doSomething() } catch (_: RuntimeException) { log() } }",
                "[Try([Path(doSomething(), [])], [Catch(_: RuntimeException, [Path(log(), [])])])]");

            TestHarness.Blank();
        }

        // ===== 4. try-finally =====
        public static void TestTryFinally()
        {
            TestHarness.Section("Testing Try-Finally");

            TestBlock("{ try { openFile() } finally(e) { cleanup(e) } }",
                "[Try([Path(openFile(), [])], [], Finally(e, [Path(cleanup(Path(e, [])), [])]))]");

            TestHarness.Blank();
        }

        // ===== 5. try-catch-finally =====
        public static void TestTryCatchFinally()
        {
            TestHarness.Section("Testing Try-Catch-Finally");

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
                "[Try([Path(riskyOperation(), [])], " +
                "[Catch(e: IOException, [Path(handleIO(Path(e, [])), [])]), " +
                "Catch(_: RuntimeException, [Path(doNothing(), [])])], " +
                "Finally(e, [Path(cleanup(), [])]))]");

            TestHarness.Blank();
        }

        // ===== 6. 嵌套 try =====
        public static void TestNestedTry()
        {
            TestHarness.Section("Testing Nested Try");

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
                "[Try([Try([Path(inner(), [])], " +
                "[Catch(e: InnerException, [Path(handleInner(Path(e, [])), [])])])], " +
                "[Catch(e: OuterException, [Path(handleOuter(Path(e, [])), [])])])]");

            TestHarness.Blank();
        }

        // ===== 7. 错误用例 =====
        public static void TestInvalidCases()
        {
            TestHarness.Section("Testing Invalid Cases");

            // try 后缺少 catch 或 finally
            TestInvalidBlock("{ try { operation() } }",
                "try 语句必须至少有一个 catch 或一个 finally 子句");

            // catch 后缺少类型
            TestInvalidBlock("{ try { operation() } catch (e) { handle(e) } }",
                "Expected ':' after catch variable");

            // finally 后缺少参数
            TestInvalidBlock("{ try { operation() } finally { cleanup() } }",
                "Expected '(' after 'finally'");

            TestHarness.Blank();
        }

        // ===== 辅助 =====

        // 解析代码块并比对 AST 描述串（label：多行源码 \n 转义显示）
        private static void TestBlock(string source, string expectedDesc)
        {
            try
            {
                var block = TestHarness.ParseBlock(source);
                TestHarness.Check(source.Replace("\n", "\\n"), AstDescribe.Block(block), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{source.Replace("\n", "\\n")} => 意外异常", false, ex.Message);
            }
        }

        private static void TestInvalidBlock(string source, string expectedError)
        {
            TestHarness.CheckParseError(source.Replace("\n", "\\n"),
                () => TestHarness.ParseBlock(source), expectedError);
        }

        public static int RunAll()
        {
            TestHarness.Reset();

            TestSimpleTryCatch();
            TestMultipleCatch();
            TestDiscardedExceptionVariable();
            TestTryFinally();
            TestTryCatchFinally();
            TestNestedTry();
            TestInvalidCases();

            return TestHarness.Summary("TryCatchFinally");
        }
    }
}
