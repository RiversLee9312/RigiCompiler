using System;

namespace LatteCompiler.Tests
{
    // Lambda 表达式解析测试（roadmap #21，SYNTAX.md §5）：全管线驱动，
    // 断言初始化表达式的 AstDescribe 描述串。
    //
    // 覆盖：
    // 1. 完整形式 func{(params): ReturnType -> body}
    // 2. 空参/多参/默认参数
    // 3. 泛型 lambda（泛型形参在形参列表之后）
    // 4. async lambda
    // 5. trailing lambda（expr{...} 脱糖为调用）
    // 6. 跨行书写
    // 7. 错误用例：缺 :、缺 ->、缺 body、async 后非 func
    public class LambdaExpressionTests
    {
        // ===== 1. 基本形式 =====
        public static void TestBasicLambdas()
        {
            TestHarness.Section("Testing Basic Lambdas");

            TestLambda("var f = func{(x: i32): i32 -> (x + 1)}",
                "Lambda([x: i32]): i32 -> Group(Binary(Sym(x) + Int(1,I32)))");
            TestLambda("var f = func{(): i32 -> 42}",
                "Lambda([]): i32 -> Int(42,I32)");
            TestLambda("var add = func{(x: i32, y: i32): i32 -> (x + y)}",
                "Lambda([x: i32, y: i32]): i32 -> Group(Binary(Sym(x) + Sym(y)))");
            // 默认参数（复用 ParameterListParserLayer）
            TestLambda("var f = func{(x: i32 = 5): i32 -> x}",
                "Lambda([x: i32 = Int(5,I32)]): i32 -> Sym(x)");
            // 可变参数（复用 ParameterListParserLayer）
            TestLambda("var f = func{(numbers: i32...): i32 -> 0}",
                "Lambda([numbers: i32...]): i32 -> Int(0,I32)");

            TestHarness.Blank();
        }

        // ===== 2. 泛型 lambda =====
        public static void TestGenericLambdas()
        {
            TestHarness.Section("Testing Generic Lambdas");

            // SYNTAX §5.1：泛型形参列表在形参列表之后
            TestLambda("var f = func{(width: TSize)\\<TSize extends Size>: TSize -> width}",
                "Lambda([width: TSize])\\<TSize extends Size>: TSize -> Sym(width)");

            TestHarness.Blank();
        }

        // ===== 3. async lambda =====
        public static void TestAsyncLambdas()
        {
            TestHarness.Section("Testing Async Lambdas");

            TestLambda("var loader = async func{(id: i32): SharedUser -> loadUserNow(id)}",
                "Lambda async([id: i32]): SharedUser -> Call(Sym(loadUserNow), [Sym(id)])");

            TestHarness.Blank();
        }

        // ===== 4. trailing lambda =====
        public static void TestTrailingLambdas()
        {
            TestHarness.Section("Testing Trailing Lambdas");

            // expr{...} 脱糖为以 lambda 为唯一实参的调用
            TestLambda("var r = list.map{(item: String): i32 -> item.length}",
                "Call(Sym(list.map), [Lambda([item: String]): i32 -> Sym(item.length)])");
            // lambda 作为普通实参
            TestLambda("var r = foo(func{(x: i32): i32 -> x})",
                "Call(Sym(foo), [Lambda([x: i32]): i32 -> Sym(x)])");

            TestHarness.Blank();
        }

        // ===== 5. 跨行书写 =====
        public static void TestMultiLineLambdas()
        {
            TestHarness.Section("Testing Multi-line Lambdas");

            TestLambda("var f = func{(x: i32): i32 ->\n    (x + 1)\n}",
                "Lambda([x: i32]): i32 -> Group(Binary(Sym(x) + Int(1,I32)))");

            TestHarness.Blank();
        }

        // ===== 6. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Testing Lambda Error Cases (expect ParserException)");

            // 缺 :（返回类型标注）
            TestHarness.CheckParseError("var f = func{(x: i32) -> (x + 1)}",
                () => TestHarness.ParseRoot("var f = func{(x: i32) -> (x + 1)}"),
                "Expected ':' or ");
            // 缺 ->
            TestHarness.CheckParseError("var f = func{(x: i32): i32 (x + 1)}",
                () => TestHarness.ParseRoot("var f = func{(x: i32): i32 (x + 1)}"),
                "Expected '->' before lambda body");
            // 缺 body
            TestHarness.CheckParseError("var f = func{(x: i32): i32 -> }",
                () => TestHarness.ParseRoot("var f = func{(x: i32): i32 -> }"),
                "Unexpected token at start of expression");
            // async 后不是 func
            TestHarness.CheckParseError("var f = async x",
                () => TestHarness.ParseRoot("var f = async x"),
                "Expected 'func' after 'async'");
            // 缺 {
            TestHarness.CheckParseError("var f = func(x: i32): i32 -> (x + 1)",
                () => TestHarness.ParseRoot("var f = func(x: i32): i32 -> (x + 1)"),
                "Expected '{' to start lambda body");

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
            TestGenericLambdas();
            TestAsyncLambdas();
            TestTrailingLambdas();
            TestMultiLineLambdas();
            TestErrorCases();

            return TestHarness.Summary("Lambda");
        }
    }
}
