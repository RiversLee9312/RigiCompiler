using System;

namespace RigiCompiler.Tests
{
    // 实参列表解析测试（ArgumentListParserLayer）：实参列表挂在表达式节点上
    // （Call/Index/New），因此用全管线 var v = foo(...) / a[...] / new T(...) 驱动，
    // 取 Initializer 比对 AstDescribe.Expr 描述串（与 ExpressionParserTests 同形态）。
    // 覆盖：位置/具名/混合/空列表、new 构造参数、索引实参、
    // M31 括号内续行（SYNTAX §1.1）、空索引与未闭合错误。
    public class ArgumentListParserLayerTests
    {
        // ===== 1. 调用实参：位置/具名/混合/空 =====
        public static void TestCallArguments()
        {
            TestHarness.Section("Call Arguments");

            TestExpr("var v = foo(1, x)", "Path(foo(Int(1,I32), Path(x, [])), [])");
            TestExpr("var v = foo(name = 42)", "Path(foo(name:Int(42,I32)), [])");
            TestExpr("var v = foo(1, name = 2)", "Path(foo(Int(1,I32), name:Int(2,I32)), [])");
            TestExpr("var v = foo()", "Path(foo(), [])");

            TestHarness.Blank();
        }

        // ===== 2. new 构造参数 =====
        public static void TestNewArguments()
        {
            TestHarness.Section("New Arguments");

            TestExpr("var v = new User(id = 42)", "New(User, [id:Int(42,I32)])");

            TestHarness.Blank();
        }

        // ===== 3. 索引实参 =====
        public static void TestIndexArguments()
        {
            TestHarness.Section("Index Arguments");

            TestExpr("var v = a[0]", "Path(a[Int(0,I32)], [])");
            TestExpr("var v = a[0, 1]", "Path(a[Int(0,I32), Int(1,I32)], [])");

            TestHarness.Blank();
        }

        // ===== 4. 括号内续行（SYNTAX §1.1，M31）=====
        public static void TestLineContinuation()
        {
            TestHarness.Section("Line Continuation Inside Brackets");

            TestExpr("var v = foo(1,\n2)", "Path(foo(Int(1,I32), Int(2,I32)), [])");
            TestExpr("var v = a[0,\n1]", "Path(a[Int(0,I32), Int(1,I32)], [])");
            // 空实参列表也允许换行
            TestExpr("var v = foo(\n)", "Path(foo(), [])");

            TestHarness.Blank();
        }

        // ===== 5. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Argument List Error Cases (expect ParserException)");

            // 空索引不合法（M31）：foo() 允许空参，a[] 不允许
            TestHarness.CheckParseError("var v = a[]",
                () => TestHarness.ParseRoot("var v = a[]"), "Index argument list cannot be empty");
            // 实参列表必须由闭合括号结束，EOF 是不完整结构
            TestHarness.CheckParseError("var v = foo(1",
                () => TestHarness.ParseRoot("var v = foo(1"), "Unexpected end of file");

            TestHarness.Blank();
        }

        // 辅助：解析变量声明并比对初始化表达式的 AST 描述串（label 中 \n 转义显示）
        private static void TestExpr(string code, string expectedDesc)
        {
            try
            {
                var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(code);
                TestHarness.Check(code.Replace("\n", "\\n"),
                    AstDescribe.Expr(decl.Initializer!.Expression), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code.Replace("\n", "\\n")} => 意外异常", false, ex.Message);
            }
        }

        public static int RunAll()
        {
            TestHarness.Reset();

            TestCallArguments();
            TestNewArguments();
            TestIndexArguments();
            TestLineContinuation();
            TestErrorCases();

            return TestHarness.Summary("ArgumentList");
        }
    }
}
