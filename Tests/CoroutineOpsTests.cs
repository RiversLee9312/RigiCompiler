using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 协程操作解析测试（roadmap #12，SYNTAX.md §7.5）
    ///
    /// 覆盖：
    /// 1. await 表达式（一元前缀运算符）
    /// 2. yield 语句（裸 yield / 带 alarm）
    /// 3. await 和 yield 组合使用
    ///
    /// 驱动方式：
    /// - await：TestHarness.ParseWithLayer(new ExpressionParserLayer(exprRoot), code)
    /// - yield：TestHarness.ParseBlock(code)
    /// </summary>
    public class CoroutineOpsTests
    {
        // ===== 1. await 表达式 =====
        public static void TestAwaitExpression()
        {
            TestHarness.Section("Await Expression");

            // 简单 await
            TestExpression("await task",
                "Unary(await Path(task, []))");

            // await 函数调用
            TestExpression("await loadUser(42)",
                "Unary(await Path(loadUser(Int(42,I32)), []))");

            // await 表达式作为变量初始化
            TestBlock("{ const user = await loadUser(id) }",
                "[const user = Unary(await Path(loadUser(Path(id, [])), []))]");

            // await 复杂表达式
            TestExpression("await getTask().execute()",
                "Unary(await Path(getTask(), [.execute()]))");

            TestHarness.Blank();
        }

        // ===== 2. yield 语句 =====
        public static void TestYieldStatement()
        {
            TestHarness.Section("Yield Statement");

            // 裸 yield
            TestBlock("{ yield }",
                "[Yield]");

            // yield 带 alarm
            TestBlock("{ yield pollingAlarm }",
                "[Yield(Path(pollingAlarm, []))]");

            TestBlock("{ yield sleep(1000) }",
                "[Yield(Path(sleep(Int(1000,I32)), []))]");

            // 多个 yield
            TestBlock("{\n    yield\n    yield alarm\n}",
                "[Yield, Yield(Path(alarm, []))]");

            TestHarness.Blank();
        }

        // ===== 3. await 和 yield 组合 =====
        public static void TestAwaitYieldCombination()
        {
            TestHarness.Section("Await and Yield Combination");

            // await 和 yield 混用
            TestBlock("{\n" +
                      "    const result = await fetchData()\n" +
                      "    yield sleep(100)\n" +
                      "    process(result)\n" +
                      "}",
                "[const result = Unary(await Path(fetchData(), [])), Yield(Path(sleep(Int(100,I32)), [])), Path(process(Path(result, [])), [])]");

            // 循环中的 await 和 yield
            TestBlock("{\n" +
                      "    for (id in ids) {\n" +
                      "        const user = await loadUser(id)\n" +
                      "        yield\n" +
                      "    }\n" +
                      "}",
                "[For(id, Path(ids, []), [const user = Unary(await Path(loadUser(Path(id, [])), [])), Yield])]");

            TestHarness.Blank();
        }

        // ===== 4. await 的不同上下文 =====
        public static void TestAwaitInDifferentContexts()
        {
            TestHarness.Section("Await in Different Contexts");

            // await 在 if 条件中
            TestBlock("{\n" +
                      "    if (await checkPermission()) {\n" +
                      "        doSomething()\n" +
                      "    }\n" +
                      "}",
                "[IfStmt(Unary(await Path(checkPermission(), [])), [Path(doSomething(), [])], <none>)]");

            // await 在变量赋值中再传递
            TestBlock("{ const data = await getData()\nprocess(data) }",
                "[const data = Unary(await Path(getData(), [])), Path(process(Path(data, [])), [])]");

            // await 在 return 中
            TestBlock("{ return await compute() }",
                "[Return(Unary(await Path(compute(), [])))]");

            TestHarness.Blank();
        }

        // ===== 辅助方法 =====

        // 独立 ExpressionParserLayer 驱动，比对表达式 AST 描述串
        private static void TestExpression(string source, string expected)
        {
            try
            {
                var root = new RootASTNode();
                var exprRoot = new ExpressionRootASTNode(root);
                TestHarness.ParseWithLayer(new ExpressionParserLayer(exprRoot), source);
                TestHarness.Check(source, AstDescribe.Expr(exprRoot.Expression), expected);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{source} => 意外异常", false, ex.Message);
            }
        }

        // 独立 CodeBlockParserLayer 驱动，比对代码块 AST 描述串
        private static void TestBlock(string source, string expected)
        {
            try
            {
                var block = TestHarness.ParseBlock(source);
                TestHarness.Check(source.Replace("\n", "\\n"), AstDescribe.Block(block), expected);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{source.Replace("\n", "\\n")} => 意外异常", false, ex.Message);
            }
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            TestHarness.Reset();

            TestAwaitExpression();
            TestYieldStatement();
            TestAwaitYieldCombination();
            TestAwaitInDifferentContexts();

            return TestHarness.Summary("CoroutineOps");
        }
    }
}
