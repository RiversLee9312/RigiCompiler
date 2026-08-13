using System;

namespace RigiCompiler.Tests
{
    // Lambda 表达式解析测试（roadmap #21，SYNTAX.md §5.1）：全管线驱动，
    // 断言初始化表达式的 AstDescribe 描述串。
    //
    // 覆盖：
    // 1. 完整形式 func{(params): ReturnType -> body}
    // 2. 空参/多参/默认参数
    // 3. 泛型 lambda 负例（已删除：lambda 不支持泛型形参）
    // 4. async lambda（含 async + void）
    // 5. trailing lambda（expr{...} 脱糖为调用）
    // 6. 跨行书写
    // 7. 多语句块体：return@_ / named + return@标签（§5.1）
    // 8. void lambda（省略返回类型）：单表达式体 / 块体
    // 9. 裸 return 编译错误：lambda 体（含嵌套 seq/if/嵌套 lambda/单表达式体的
    //    if 表达式分支）一律禁止裸 return
    // 10. 错误用例：缺 ->、缺 body、泛型形参、表达式起始 async、双重 async
    // 11. AST 结构断言（Body/BlockBody 互斥、Label、ReturnType 可空、Parent 链）
    public class LambdaExpressionTests
    {
        // ===== 1. 基本形式 =====
        public static void TestBasicLambdas()
        {
            TestHarness.Section("Testing Basic Lambdas");

            TestLambda("var f = func{(x: i32): i32 -> (x + 1)}",
                "Lambda([x: i32]): i32 -> Group(Binary(Path(x, []) + Int(1,I32)))");
            TestLambda("var f = func{(): i32 -> 42}",
                "Lambda([]): i32 -> Int(42,I32)");
            TestLambda("var add = func{(x: i32, y: i32): i32 -> (x + y)}",
                "Lambda([x: i32, y: i32]): i32 -> Group(Binary(Path(x, []) + Path(y, [])))");
            // 默认参数（复用 ParameterListParserLayer）
            TestLambda("var f = func{(x: i32 = 5): i32 -> x}",
                "Lambda([x: i32 = Int(5,I32)]): i32 -> Path(x, [])");
            // 可变参数（复用 ParameterListParserLayer）
            TestLambda("var f = func{(numbers: i32...): i32 -> 0}",
                "Lambda([numbers: i32...]): i32 -> Int(0,I32)");

            TestHarness.Blank();
        }

        // ===== 2. 泛型 lambda 负例（SYNTAX §5.1：lambda 不支持泛型形参）=====
        public static void TestGenericLambdasRejected()
        {
            TestHarness.Section("Testing Generic Lambdas Rejected");

            const string genericMsg = "lambda 不支持泛型参数";
            TestHarness.CheckParseError(
                "var f = func{(width: TSize)\\<TSize extends Size>: TSize -> width}",
                () => TestHarness.ParseRoot(
                    "var f = func{(width: TSize)\\<TSize extends Size>: TSize -> width}"),
                genericMsg);
            TestHarness.CheckParseError(
                "var f = func{(x: T)\\<T>: T -> x}",
                () => TestHarness.ParseRoot("var f = func{(x: T)\\<T>: T -> x}"),
                genericMsg);

            TestHarness.Blank();
        }

        // ===== 3. async lambda =====
        public static void TestAsyncLambdas()
        {
            TestHarness.Section("Testing Async Lambdas");

            TestLambda("var loader = func{async (id: i32): SharedUser -> loadUserNow(id)}",
                "Lambda async([id: i32]): SharedUser -> Path(loadUserNow(Path(id, [])), [])");
            // async + void
            TestLambda("var a = func{async (x: i32) -> x}",
                "Lambda async([x: i32]) -> Path(x, [])");

            TestHarness.Blank();
        }

        // ===== 4. trailing lambda =====
        public static void TestTrailingLambdas()
        {
            TestHarness.Section("Testing Trailing Lambdas");

            // expr{...} 脱糖为以 lambda 为唯一实参的调用
            TestLambda("var r = list.map{(item: String): i32 -> item.length}",
                "Path(list, [.map(Lambda([item: String]): i32 -> Path(item, [.length]))])");
            // lambda 作为普通实参
            TestLambda("var r = foo(func{(x: i32): i32 -> x})",
                "Path(foo(Lambda([x: i32]): i32 -> Path(x, [])), [])");
            // trailing void lambda
            TestLambda("var r = list.forEach{(item: String) -> item}",
                "Path(list, [.forEach(Lambda([item: String]) -> Path(item, []))])");

            TestHarness.Blank();
        }

        // ===== 5. 跨行书写 =====
        public static void TestMultiLineLambdas()
        {
            TestHarness.Section("Testing Multi-line Lambdas");

            TestLambda("var f = func{(x: i32): i32 ->\n    (x + 1)\n}",
                "Lambda([x: i32]): i32 -> Group(Binary(Path(x, []) + Int(1,I32)))");

            TestHarness.Blank();
        }

        // ===== 6. 多语句块体（return@_ / named，SYNTAX §5.1）=====
        public static void TestBlockBodies()
        {
            TestHarness.Section("Testing Lambda Block Bodies");

            // 多语句块体：return@_ 显式产出返回值（匿名体的默认标签是 _）
            TestLambda("var f = func{(x: i32): i32 -> {\n" +
                       "    const doubled = (x * 2)\n" +
                       "    return@_ doubled\n" +
                       "}}",
                "Lambda([x: i32]): i32 -> " +
                "[const doubled = Group(Binary(Path(x, []) * Int(2,I32))), Return@_(Path(doubled, []))]");

            // named 命名后 return@标签 穿透内层匿名块（named 写在 -> 之后、体之前）
            TestLambda("var f = func{(x: i32): i32 -> named calc {\n" +
                       "    seq {\n" +
                       "        return@calc (x * 2)\n" +
                       "    }\n" +
                       "}}",
                "Lambda([x: i32]): i32 -> named calc " +
                "[Seq([Return@calc(Group(Binary(Path(x, []) * Int(2,I32))))])]");

            // trailing lambda 也可用块体
            TestLambda("var r = list.map{(item: String): i32 -> { return@_ item.length }}",
                "Path(list, [.map(Lambda([item: String]): i32 -> [Return@_(Path(item, [.length]))])])");

            TestHarness.Blank();
        }

        // ===== 7. void lambda（省略返回类型 = 无返回值，SYNTAX §5.1）=====
        public static void TestVoidLambdas()
        {
            TestHarness.Section("Testing Void Lambdas");

            // 单表达式体：无返回值时表达式语句语义（解析层只收形态，不校验 return@）
            TestLambda("var f = func{() -> (1 + 1)}",
                "Lambda([]) -> Group(Binary(Int(1,I32) + Int(1,I32)))");
            TestLambda("var f = func{(x: i32) -> x}",
                "Lambda([x: i32]) -> Path(x, [])");
            // 块体：无 return@ 在解析层合法（return@ 规则归语义层）
            TestLambda("var f = func{() -> { }}",
                "Lambda([]) -> []");
            TestLambda("var f = func{() -> {\n    const x = 1\n}}",
                "Lambda([]) -> [const x = Int(1,I32)]");
            // 有参 + 块体
            TestLambda("var f = func{(x: i32) -> { const y = x }}",
                "Lambda([x: i32]) -> [const y = Path(x, [])]");

            TestHarness.Blank();
        }

        // ===== 8. 裸 return 编译错误（SYNTAX §5.1：lambda 体内一律显式 return@）=====
        public static void TestBareReturnErrors()
        {
            TestHarness.Section("Testing Lambda Bare Return Errors (expect ParserException)");

            const string bareReturnMsg = "lambda 体内不允许裸 return";

            // 块体内直接裸 return
            TestHarness.CheckParseError("var f = func{(): i32 -> { return 1 }}",
                () => TestHarness.ParseRoot("var f = func{(): i32 -> { return 1 }}"),
                bareReturnMsg);
            // 无值裸 return 同样禁止
            TestHarness.CheckParseError("var f = func{(): i32 -> { return }}",
                () => TestHarness.ParseRoot("var f = func{(): i32 -> { return }}"),
                bareReturnMsg);
            // 嵌套 seq 块内的裸 return（标记向嵌套块传染）
            TestHarness.CheckParseError("var f = func{(): i32 -> { seq { return 1 } }}",
                () => TestHarness.ParseRoot("var f = func{(): i32 -> { seq { return 1 } }}"),
                bareReturnMsg);
            // 嵌套 if 语句块内的裸 return
            TestHarness.CheckParseError("var f = func{(): i32 -> { if (c) { return 1 } return@_ 0 }}",
                () => TestHarness.ParseRoot("var f = func{(): i32 -> { if (c) { return 1 } return@_ 0 }}"),
                bareReturnMsg);
            // 嵌套循环体内的裸 return
            TestHarness.CheckParseError("var f = func{(): i32 -> { while (c) { return 1 } return@_ 0 }}",
                () => TestHarness.ParseRoot("var f = func{(): i32 -> { while (c) { return 1 } return@_ 0 }}"),
                bareReturnMsg);
            // 嵌套 switch 语句分支内的裸 return
            TestHarness.CheckParseError(
                "var f = func{(): i32 -> { switch (x) { (1) -> { return 1 } default -> { } } return@_ 0 }}",
                () => TestHarness.ParseRoot(
                    "var f = func{(): i32 -> { switch (x) { (1) -> { return 1 } default -> { } } return@_ 0 }}"),
                bareReturnMsg);
            // 单表达式体内的 if 表达式分支块裸 return（if 表达式不是 lambda 边界）
            TestHarness.CheckParseError("var f = func{(): i32 -> if (c) { return 1 } else { return@_ 0 }}",
                () => TestHarness.ParseRoot("var f = func{(): i32 -> if (c) { return 1 } else { return@_ 0 }}"),
                bareReturnMsg);
            // lambda 内嵌 lambda：内层仍是 false 边界
            TestHarness.CheckParseError(
                "var f = func{(): i32 -> { var g = func{(): i32 -> { return 1 }}\n return@_ 0 }}",
                () => TestHarness.ParseRoot(
                    "var f = func{(): i32 -> { var g = func{(): i32 -> { return 1 }}\n return@_ 0 }}"),
                bareReturnMsg);
            // void 块体裸 return 同样禁止
            TestHarness.CheckParseError("var f = func{() -> { return }}",
                () => TestHarness.ParseRoot("var f = func{() -> { return }}"),
                bareReturnMsg);

            // 对照：return@_ / return@标签 合法（不报错，由块体用例覆盖快照）
            // 对照：函数体内裸 return 不受影响（函数不是 lambda 边界）
            var block = TestHarness.ParseBlock("{ return 1 }");
            TestHarness.CheckTrue("普通代码块内裸 return 合法",
                block.Statements.Count == 1 && block.Statements[0] is ReturnStatementASTNode);

            TestHarness.Blank();
        }

        // ===== 9. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Testing Lambda Error Cases (expect ParserException)");

            // 缺 ->
            TestHarness.CheckParseError("var f = func{(x: i32): i32 (x + 1)}",
                () => TestHarness.ParseRoot("var f = func{(x: i32): i32 (x + 1)}"),
                "Expected '->' before lambda body");
            // 缺 body
            TestHarness.CheckParseError("var f = func{(x: i32): i32 -> }",
                () => TestHarness.ParseRoot("var f = func{(x: i32): i32 -> }"),
                "Unexpected token at start of expression");
            // 表达式起始 async（旧写法 async func{...} 亦同）
            TestHarness.CheckParseError("var f = async x",
                () => TestHarness.ParseRoot("var f = async x"),
                "async lambda 写作 func{async (...)...}，'async' 不能出现在表达式起始位置");
            TestHarness.CheckParseError("var f = async func{(x: i32): i32 -> x}",
                () => TestHarness.ParseRoot("var f = async func{(x: i32): i32 -> x}"),
                "async lambda 写作 func{async (...)...}，'async' 不能出现在表达式起始位置");
            // 双重 async：第二次 async 落到形参列表
            TestHarness.CheckParseError("var f = func{async async (x: i32): i32 -> x}",
                () => TestHarness.ParseRoot("var f = func{async async (x: i32): i32 -> x}"),
                "Expected '('");
            // 缺 {
            TestHarness.CheckParseError("var f = func(x: i32): i32 -> (x + 1)",
                () => TestHarness.ParseRoot("var f = func(x: i32): i32 -> (x + 1)"),
                "Expected '{' to start lambda body");
            // named 后缺标签名
            TestHarness.CheckParseError("var f = func{(x: i32): i32 -> named { return@_ 1 }}",
                () => TestHarness.ParseRoot("var f = func{(x: i32): i32 -> named { return@_ 1 }}"),
                "Expected label name after 'named'");
            // 形参后既非 : 也非 ->
            TestHarness.CheckParseError("var f = func{(x: i32) { x }}",
                () => TestHarness.ParseRoot("var f = func{(x: i32) { x }}"),
                "Expected ':' or '->'");

            TestHarness.Blank();
        }

        // ===== 10. AST 结构断言（AGENTS §5：快照不作为唯一验证方式）=====
        public static void TestStructuralAssertions()
        {
            TestHarness.Section("Structural Assertions");

            // 单表达式体：Body 填充、BlockBody 为 null（互斥）
            var exprDecl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(
                "var f = func{(x: i32): i32 -> (x + 1)}");
            var exprLambda = (LambdaExpressionASTNode)exprDecl.Initializer!.Expression;
            TestHarness.CheckTrue("单表达式体 Body Root 存在且已填充",
                exprLambda.Body != null && exprLambda.Body.IsAttached);
            TestHarness.CheckTrue("单表达式体 BlockBody 为 null（互斥）",
                exprLambda.BlockBody == null);
            TestHarness.CheckTrue("无 named 时 Label 为 null", exprLambda.Label == null);
            TestHarness.CheckTrue("有返回类型时 ReturnType 非 null",
                exprLambda.ReturnType != null);
            TestHarness.CheckTrue("Body Root 的 Parent 是 lambda 节点",
                ReferenceEquals(exprLambda.Body!.Parent, exprLambda));

            // 块体：BlockBody 填充、Body 为 null（互斥）；named 写入 Label
            var blockDecl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(
                "var f = func{(x: i32): i32 -> named calc { return@calc x }}");
            var blockLambda = (LambdaExpressionASTNode)blockDecl.Initializer!.Expression;
            TestHarness.CheckTrue("块体 BlockBody 存在",
                blockLambda.BlockBody != null);
            TestHarness.CheckTrue("块体 Body 为 null（互斥）", blockLambda.Body == null);
            TestHarness.CheckTrue("named 标签写入 Label", blockLambda.Label == "calc");
            TestHarness.CheckTrue("BlockBody 的 Parent 是 lambda 节点",
                ReferenceEquals(blockLambda.BlockBody!.Parent, blockLambda));
            TestHarness.CheckTrue("块体内 return@标签 的 Label",
                blockLambda.BlockBody!.Statements.Count == 1 &&
                blockLambda.BlockBody.Statements[0] is ReturnStatementASTNode ret &&
                ret.Label == "calc");

            // void lambda：ReturnType 为 null
            var voidDecl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(
                "var f = func{() -> (1 + 1)}");
            var voidLambda = (LambdaExpressionASTNode)voidDecl.Initializer!.Expression;
            TestHarness.CheckTrue("void lambda ReturnType 为 null",
                voidLambda.ReturnType == null);
            TestHarness.CheckTrue("void 单表达式体 Body 已填充",
                voidLambda.Body != null && voidLambda.Body.IsAttached);

            TestHarness.Blank();
        }

        // ===== 测试辅助 =====

        // 辅助：解析变量声明并比对初始化表达式的 AST 描述串
        private static void TestLambda(string code, string expectedDesc)
        {
            try
            {
                var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(code);
                TestHarness.Check(Label(code), AstDescribe.Expr(decl.Initializer!.Expression), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{Label(code)} => 意外异常", false, ex.Message);
            }
        }

        // 标签：多行源码的换行转义显示
        private static string Label(string code) => code.Replace("\n", "\\n");

        // ===== 入口 =====
        public static int RunAll()
        {
            TestHarness.Reset();

            TestBasicLambdas();
            TestGenericLambdasRejected();
            TestAsyncLambdas();
            TestTrailingLambdas();
            TestMultiLineLambdas();
            TestBlockBodies();
            TestVoidLambdas();
            TestBareReturnErrors();
            TestErrorCases();
            TestStructuralAssertions();

            return TestHarness.Summary("Lambda");
        }
    }
}
