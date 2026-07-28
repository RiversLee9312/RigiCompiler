using System;

namespace LatteCompiler.Tests
{
    // throw 语句解析测试：代码块独立驱动（TestHarness.ParseBlock），
    // 断言 AstDescribe 精确描述串。
    // 覆盖：简单 throw / throw 表达式（new、调用）/ 配合 try-catch 使用 /
    // throw 在不同位置（if 分支、循环）/ 错误用例。
    public class ThrowStatementTests
    {
        // ===== 1. 简单 throw =====
        public static void TestSimpleThrow()
        {
            TestHarness.Section("Testing Simple Throw");

            TestBlock("{ throw error }",
                "[Throw(Sym(error))]");

            TestBlock("{ throw e }",
                "[Throw(Sym(e))]");

            TestHarness.Blank();
        }

        // ===== 2. throw 表达式 =====
        public static void TestThrowExpression()
        {
            TestHarness.Section("Testing Throw with Expression");

            TestBlock("{ throw new IOException() }",
                "[Throw(New(IOException, []))]");

            TestBlock("{ throw new IOException(\"File not found\") }",
                "[Throw(New(IOException, [Str(\"File not found\")]))]");

            TestBlock("{ throw getError() }",
                "[Throw(Call(Sym(getError), []))]");

            TestHarness.Blank();
        }

        // ===== 3. 配合 try-catch 使用 =====
        public static void TestThrowInTryCatch()
        {
            TestHarness.Section("Testing Throw in Try-Catch");

            TestBlock("{\n" +
                      "    try {\n" +
                      "        throw new RuntimeException()\n" +
                      "    } catch (e: RuntimeException) {\n" +
                      "        log(e)\n" +
                      "    }\n" +
                      "}",
                "[Try([Throw(New(RuntimeException, []))], [Catch(e: RuntimeException, [Call(Sym(log), [Sym(e)])])])]");

            TestBlock("{\n" +
                      "    try {\n" +
                      "        validate(data)\n" +
                      "    } catch (e: ValidationError) {\n" +
                      "        throw new ProcessingError(e)\n" +
                      "    }\n" +
                      "}",
                "[Try([Call(Sym(validate), [Sym(data)])], [Catch(e: ValidationError, [Throw(New(ProcessingError, [Sym(e)]))])])]");

            TestHarness.Blank();
        }

        // ===== 4. throw 在不同位置 =====
        public static void TestThrowInDifferentContexts()
        {
            TestHarness.Section("Testing Throw in Different Contexts");

            // if 分支中
            TestBlock("{\n" +
                      "    if (error) {\n" +
                      "        throw new Error()\n" +
                      "    }\n" +
                      "}",
                "[IfStmt(Sym(error), [Throw(New(Error, []))], <none>)]");

            // 循环中
            TestBlock("{\n" +
                      "    for (item in items) {\n" +
                      "        if (invalid(item)) {\n" +
                      "            throw new InvalidItemError(item)\n" +
                      "        }\n" +
                      "    }\n" +
                      "}",
                "[For(item, Sym(items), [IfStmt(Call(Sym(invalid), [Sym(item)]), [Throw(New(InvalidItemError, [Sym(item)]))], <none>)])]");

            TestHarness.Blank();
        }

        // ===== 5. 错误用例 =====
        public static void TestInvalidCases()
        {
            TestHarness.Section("Testing Invalid Cases");

            // throw 后缺少表达式：} 触发 ExpressionParserLayer 报错
            TestInvalidBlock("{ throw }",
                "Unexpected token at start of expression");

            // throw 后换行是错误（M31，SYNTAX §1.1：续行只来自未闭合的符号结构，
            // throw 不是括号结构，与裸 return/裸 yield 遇换行即收尾一致）
            TestInvalidBlock("{\n    throw\n    foo()\n}",
                "Expected exception expression after 'throw'");

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

            TestSimpleThrow();
            TestThrowExpression();
            TestThrowInTryCatch();
            TestThrowInDifferentContexts();
            TestInvalidCases();

            return TestHarness.Summary("Throw");
        }
    }
}
