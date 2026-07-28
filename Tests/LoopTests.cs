using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 循环解析测试（roadmap #9，SYNTAX.md §7.3/§7.4）
    ///
    /// 覆盖：
    /// 1. for-each 循环
    /// 2. 范围循环（0 to 10 → Iterable 起点 + RangeTo 终点）
    /// 3. while 循环
    /// 4. do-while 循环
    /// 5. named 标签与 break@标签 / continue@标签
    /// 6. 嵌套循环
    /// 7. 错误用例
    ///
    /// 驱动方式：TestHarness.ParseBlock（TestRootParserLayer 垫底 + CodeBlockParserLayer
    /// 独立入口），源码以 { ... } 包裹；断言统一走 AstDescribe 描述串（M31 基建）。
    /// </summary>
    public class LoopTests
    {
        // ===== 1. for-each =====
        public static void TestForEachLoops()
        {
            TestHarness.Section("for-each Loops");

            TestBlock("{ for (item in collection) { print(item) } }",
                "[For(item, Sym(collection), [Call(Sym(print), [Sym(item)])])]");
            // 迭代表达式可以是调用
            TestBlock("{ for (x in getItems(1)) { print(x) } }",
                "[For(x, Call(Sym(getItems), [Int(1,I32)]), [Call(Sym(print), [Sym(x)])])]");

            TestHarness.Blank();
        }

        // ===== 2. 范围循环 =====
        public static void TestForRangeLoops()
        {
            TestHarness.Section("for-range Loops");

            TestBlock("{ for (i in 0 to 10) { print(i) } }",
                "[For(i, Range(Int(0,I32) to Int(10,I32)), [Call(Sym(print), [Sym(i)])])]");

            TestHarness.Blank();
        }

        // ===== 3. while =====
        public static void TestWhileLoops()
        {
            TestHarness.Section("while Loops");

            TestBlock("{ while (condition) { doSomething() } }",
                "[While(Sym(condition), [Call(Sym(doSomething), [])])]");

            TestHarness.Blank();
        }

        // ===== 4. do-while =====
        public static void TestDoWhileLoops()
        {
            TestHarness.Section("do-while Loops");

            TestBlock("{ do { doSomething() } while (condition) }",
                "[DoWhile(Sym(condition), [Call(Sym(doSomething), [])])]");

            TestHarness.Blank();
        }

        // ===== 5. named 标签 =====
        public static void TestNamedLoops()
        {
            TestHarness.Section("named Loops");

            TestBlock("{ for (i in 0 to 10) named outer { break@outer } }",
                "[For(i, Range(Int(0,I32) to Int(10,I32)), named outer, [Break@outer])]");
            TestBlock("{ while (true) named loop { break@loop } }",
                "[While(Bool(True), named loop, [Break@loop])]");
            TestBlock("{ do named loop { continue@loop } while (c) }",
                "[DoWhile(Sym(c), named loop, [Continue@loop])]");

            TestHarness.Blank();
        }

        // ===== 6. 嵌套循环 =====
        public static void TestNestedLoops()
        {
            TestHarness.Section("Nested Loops");

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

            TestHarness.Blank();
        }

        // ===== 7. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Loop Error Cases (expect ParserException)");

            // for 缺 (
            TestError("{ for item in collection { } }", "Expected '(' after for");
            // for 缺 in
            TestError("{ for (item collection) { } }", "Expected 'in' after loop variable");
            // while 缺 (
            TestError("{ while condition { } }", "Expected '(' after while");
            // do 缺 while
            TestError("{ do { } cond }", "Expected 'while' after do block");
            // 范围循环缺终点
            TestError("{ for (i in 0 to ) { } }", "Unexpected token at start of expression");
            // 循环体缺 {
            TestError("{ for (item in collection) print(item) }", "Expected 'named' or '{' after loop clause");

            TestHarness.Blank();
        }

        // ===== 测试辅助 =====

        // 解析一段 { ... } 代码块并比对 AstDescribe.Block 描述串
        private static void TestBlock(string code, string expectedDesc)
        {
            try
            {
                var block = TestHarness.ParseBlock(code);
                TestHarness.Check(Label(code), AstDescribe.Block(block), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{Label(code)} => 意外异常", false, ex.Message);
            }
        }

        // 错误校验：解析必须抛出 ParserException/LexerException 且消息含片段
        private static void TestError(string code, string expectedMessagePart)
        {
            TestHarness.CheckParseError(Label(code), () => TestHarness.ParseBlock(code), expectedMessagePart);
        }

        // 标签：多行源码的 \n 转义显示
        private static string Label(string code) => code.Replace("\n", "\\n");

        // ===== 入口 =====
        public static int RunAll()
        {
            TestHarness.Reset();

            TestForEachLoops();
            TestForRangeLoops();
            TestWhileLoops();
            TestDoWhileLoops();
            TestNamedLoops();
            TestNestedLoops();
            TestErrorCases();

            return TestHarness.Summary("Loop");
        }
    }
}
