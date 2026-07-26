using System;
using System.Linq;

namespace LatteCompiler.Tests
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
    /// - await：Parser.Parse(tokens, new ExpressionParserLayer(parent))
    /// - yield：Parser.Parse(tokens, new CodeBlockParserLayer(block))
    /// </summary>
    public class CoroutineOpsTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. await 表达式 =====
        public static void TestAwaitExpression()
        {
            Console.WriteLine("=== Testing Await Expression ===");

            // 简单 await
            TestExpression("await task",
                "Unary(await Sym(task))");

            // await 函数调用
            TestExpression("await loadUser(42)",
                "Unary(await Call(Sym(loadUser), [Int(42,I32)]))");

            // await 表达式作为变量初始化
            TestBlock("{ const user = await loadUser(id) }",
                "[Var(user = Unary(await Call(Sym(loadUser), [Sym(id)])))]");

            // await 复杂表达式
            TestExpression("await getTask().execute()",
                "Unary(await Call(MemberAccess(Call(Sym(getTask), []).execute), []))");

            Console.WriteLine();
        }

        // ===== 2. yield 语句 =====
        public static void TestYieldStatement()
        {
            Console.WriteLine("=== Testing Yield Statement ===");

            // 裸 yield
            TestBlock("{ yield }",
                "[Yield(<none>)]");

            // yield 带 alarm
            TestBlock("{ yield pollingAlarm }",
                "[Yield(Sym(pollingAlarm))]");

            TestBlock("{ yield sleep(1000) }",
                "[Yield(Call(Sym(sleep), [Int(1000,I32)]))]");

            // 多个 yield
            TestBlock("{\n    yield\n    yield alarm\n}",
                "[Yield(<none>), Yield(Sym(alarm))]");

            Console.WriteLine();
        }

        // ===== 3. await 和 yield 组合 =====
        public static void TestAwaitYieldCombination()
        {
            Console.WriteLine("=== Testing Await and Yield Combination ===");

            // await 和 yield 混用
            TestBlock("{\n" +
                      "    const result = await fetchData()\n" +
                      "    yield sleep(100)\n" +
                      "    process(result)\n" +
                      "}",
                "[Var(result = Unary(await Call(Sym(fetchData), []))), Yield(Call(Sym(sleep), [Int(100,I32)])), Call(Sym(process), [Sym(result)])]");

            // 循环中的 await 和 yield
            TestBlock("{\n" +
                      "    for (id in ids) {\n" +
                      "        const user = await loadUser(id)\n" +
                      "        yield\n" +
                      "    }\n" +
                      "}",
                "[For(id, Sym(ids), [Var(user = Unary(await Call(Sym(loadUser), [Sym(id)]))), Yield(<none>)])]");

            Console.WriteLine();
        }

        // ===== 4. await 的不同上下文 =====
        public static void TestAwaitInDifferentContexts()
        {
            Console.WriteLine("=== Testing Await in Different Contexts ===");

            // await 在 if 条件中
            TestBlock("{\n" +
                      "    if (await checkPermission()) {\n" +
                      "        doSomething()\n" +
                      "    }\n" +
                      "}",
                "[IfStmt(Unary(await Call(Sym(checkPermission), [])), [Call(Sym(doSomething), [])], <none>)]");

            // await 在变量赋值中再传递
            TestBlock("{ const data = await getData()\nprocess(data) }",
                "[Var(data = Unary(await Call(Sym(getData), []))), Call(Sym(process), [Sym(data)])]");

            // await 在 return 中
            TestBlock("{ return await compute() }",
                "[Return(Unary(await Call(Sym(compute), [])))]");

            Console.WriteLine();
        }

        // ===== 辅助方法 =====

        private static void TestExpression(string source, string expected)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var root = new RootASTNode();
                var layer = new ExpressionParserLayer(root);
                var parser = new Parser();
                parser.Parse(tokens, layer);

                var result = layer.GetResult();
                var formatted = DescribeExpression(result);
                if (formatted == expected)
                {
                    Console.WriteLine($"PASS: {source}");
                    passCount++;
                }
                else
                {
                    Console.WriteLine($"FAIL: {source}");
                    Console.WriteLine($"  Expected: {expected}");
                    Console.WriteLine($"  Got:      {formatted}");
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL (Exception): {source}");
                Console.WriteLine($"  Expected: {expected}");
                Console.WriteLine($"  Exception: {ex.Message}");
                failCount++;
            }
        }

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

        private static string FormatBlock(CodeBlockASTNode block)
        {
            var statements = block.Children.Select(FormatStatement).ToList();
            return $"[{string.Join(", ", statements)}]";
        }

        private static string FormatStatement(ASTNode node)
        {
            return node switch
            {
                YieldStatementASTNode y => $"Yield({(y.Alarm == null ? "<none>" : DescribeExpression(y.Alarm))})",
                VariableDeclarationASTNode v => $"Var({v.Name}{(v.Initializer != null ? " = " + DescribeExpression(v.Initializer) : "")})",
                ReturnStatementASTNode r => $"Return({(r.Value != null ? DescribeExpression(r.Value) : "<none>")})",
                IfStatementASTNode ifStmt => FormatIf(ifStmt),
                LoopStatementASTNode loop => FormatLoop(loop),
                ExpressionASTNode e => DescribeExpression(e),
                _ => $"<{node.GetType().Name}>"
            };
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
            return $"IfStmt({DescribeExpression(ifStmt.Condition)}, {FormatBlock(ifStmt.ThenBlock)}, {elsePart})";
        }

        private static string FormatLoop(LoopStatementASTNode loop)
        {
            return loop.Kind switch
            {
                LoopKind.For =>
                    $"For({loop.VariableName}, {DescribeExpression(loop.Iterable)}, {FormatBlock(loop.Body)})",
                _ => $"<{loop.Kind}>"
            };
        }

        private static string DescribeExpression(ASTNode? node)
        {
            return node switch
            {
                null => "<null>",
                LiteralExpressionASTNode lit => DescribeExpression(lit.LiteralNode),
                IntLiteralASTNode i => $"Int({i.Value},{i.IntType})",
                UnaryExpressionASTNode unary => $"Unary({unary.Operator} {DescribeExpression(unary.Operand)})",
                SymbolReferenceASTNode sref => $"Sym({sref.Symbol.symbol.elements[0].name})",
                CallExpressionASTNode c =>
                    $"Call({DescribeExpression(c.Callee)}, [{string.Join(", ", c.Arguments.Select(a => DescribeExpression(a.Value)))}])",
                MemberAccessASTNode m =>
                    $"MemberAccess({DescribeExpression(m.Object)}.{m.MemberName})",
                _ => $"<{node.GetType().Name}>"
            };
        }

        // ===== 入口 =====
        public static void RunAll()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Coroutine Operations Tests (roadmap #12, P2)        ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestAwaitExpression();
            TestYieldStatement();
            TestAwaitYieldCombination();
            TestAwaitInDifferentContexts();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }
}
