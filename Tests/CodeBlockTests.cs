using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 代码块解析测试（roadmap #6，P2 语句系统核心）
    ///
    /// 覆盖：
    /// 1. 空块与变量声明语句（含 } 终止块内末语句）
    /// 2. 表达式语句
    /// 3. 赋值语句（符号/成员/索引目标）
    /// 4. return 语句（裸/带值/@标签）
    /// 5. break/continue 语句（含 @标签）
    /// 6. if 语句（无 else/有 else/else if 链）
    /// 7. 错误用例
    /// 8. 栈上变量的 @ 注解（wrapper 应用）
    ///
    /// 驱动方式：TestHarness.ParseBlock（TestRootParserLayer 垫底 + CodeBlockParserLayer
    /// 独立入口），源码以 { ... } 包裹；断言统一走 AstDescribe 描述串（M31 基建）。
    /// </summary>
    public class CodeBlockTests
    {
        // ===== 1. 变量声明语句 =====
        public static void TestVariableDeclarations()
        {
            TestHarness.Section("Variable Declarations in Block");

            TestBlock("{ }", "[]");
            TestBlock("{ var x = 42 }", "[var x = Int(42,I32)]");
            // 多语句 + } 终止块内末语句（无换行）
            TestBlock("{\n    var x = 1\n    var y: i32 = 2\n    const z = 3\n}",
                "[var x = Int(1,I32), var y: i32 = Int(2,I32), const z = Int(3,I32)]");
            // 无初始化声明（} 终止）
            TestBlock("{ var count: i64 }", "[var count: i64]");

            TestHarness.Blank();
        }

        // ===== 2. 表达式语句 =====
        public static void TestExpressionStatements()
        {
            TestHarness.Section("Expression Statements");

            TestBlock("{ foo(1) }", "[Path(foo(Int(1,I32)), [])]");
            TestBlock("{\n    foo(1)\n    obj.field\n}",
                "[Path(foo(Int(1,I32)), []), Path(obj, [.field])]");

            TestHarness.Blank();
        }

        // ===== 3. 赋值语句 =====
        public static void TestAssignStatements()
        {
            TestHarness.Section("Assign Statements");

            TestBlock("{ x = 5 }", "[Assign(Path(x, []) = Int(5,I32))]");
            // 纯符号路径目标保持 Symbol 形态
            TestBlock("{ obj.field = v }", "[Assign(Path(obj, [.field]) = Path(v, []))]");
            // 表达式底座上的成员/索引目标
            TestBlock("{ foo().field = (1 + 2) }",
                "[Assign(Path(foo(), [.field]) = Group(Binary(Int(1,I32) + Int(2,I32))))]");
            TestBlock("{ a[0] = v }", "[Assign(Path(a[Int(0,I32)], []) = Path(v, []))]");

            TestHarness.Blank();
        }

        // ===== 4. return 语句 =====
        public static void TestReturnStatements()
        {
            TestHarness.Section("return Statements");

            TestBlock("{ return }", "[Return]");
            TestBlock("{ return 42 }", "[Return(Int(42,I32))]");
            TestBlock("{\n    return x\n}", "[Return(Path(x, []))]");
            // @标签（SYNTAX §6.1：匿名值块的默认标签是 _）
            TestBlock("{ return@_ (x * 2) }",
                "[Return@_(Group(Binary(Path(x, []) * Int(2,I32))))]");

            TestHarness.Blank();
        }

        // ===== 5. break / continue 语句 =====
        public static void TestLoopControlStatements()
        {
            TestHarness.Section("break/continue Statements");

            TestBlock("{ break }", "[Break]");
            TestBlock("{ continue }", "[Continue]");
            TestBlock("{\n    break@outer\n    continue@inner\n}", "[Break@outer, Continue@inner]");

            TestHarness.Blank();
        }

        // ===== 6. if 语句 =====
        public static void TestIfStatements()
        {
            TestHarness.Section("if Statements");

            // 无 else
            TestBlock("{ if (x > 0) { foo() } }",
                "[IfStmt(Binary(Path(x, []) > Int(0,I32)), [Path(foo(), [])], <none>)]");
            // 有 else
            TestBlock("{\n    if (c) {\n        a()\n    } else {\n        b()\n    }\n}",
                "[IfStmt(Path(c, []), [Path(a(), [])], [Path(b(), [])])]");
            // else if 链
            TestBlock("{ if (a) { f() } else if (b) { g() } else { h() } }",
                "[IfStmt(Path(a, []), [Path(f(), [])], " +
                "IfStmt(Path(b, []), [Path(g(), [])], [Path(h(), [])]))]");
            // if 语句后跟其他语句（无 else 时正确交还 token）
            TestBlock("{\n    if (c) { a() }\n    b()\n}",
                "[IfStmt(Path(c, []), [Path(a(), [])], <none>), Path(b(), [])]");
            // if 语句体内的变量声明与赋值
            TestBlock("{ if (c) { var x = 1\n x = 2 } }",
                "[IfStmt(Path(c, []), [var x = Int(1,I32), Assign(Path(x, []) = Int(2,I32))], <none>)]");
            // 单链 / 括号化 and/or 条件合法
            TestBlock("{ if (a and b) { f() } }",
                "[IfStmt(Binary(Path(a, []) and Path(b, [])), [Path(f(), [])], <none>)]");
            TestBlock("{ if (a and (b or c)) { f() } }",
                "[IfStmt(Binary(Path(a, []) and Group(Binary(Path(b, []) or Path(c, [])))), [Path(f(), [])], <none>)]");
            TestBlock("{ if ((a and b) or c) { f() } }",
                "[IfStmt(Binary(Group(Binary(Path(a, []) and Path(b, []))) or Path(c, [])), [Path(f(), [])], <none>)]");

            TestHarness.Blank();
        }

        // ===== 7. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Code Block Error Cases (expect ParserException)");

            // 变量声明缺初始化
            TestError("{ var x = }", "Unexpected token at start of expression");
            // if 语句缺 {
            TestError("{ if (c) foo() }", "Expected '{' to start if-then block");
            // 赋值缺值
            TestError("{ x = }", "Unexpected token at start of expression");
            // return@ 缺标签
            TestError("{ return@ 1 }", "Expected label after 'return@'");
            // 块未闭合
            TestError("{ var x = 1", "Unexpected end of file");
            // if 条件三连 and/or：无优先级，必须括号化（SYNTAX §1.3）
            TestError("{ if (a and b and c) { f() } }", "没有运算符优先级");
            TestError("{ if (a or b or c) { f() } }", "没有运算符优先级");
            TestError("{ if (a and b or c) { f() } }", "没有运算符优先级");

            TestHarness.Blank();
        }

        // ===== 8. 栈上变量的 @ 注解（wrapper 应用，SYNTAX §14.3/§14.5，P5）=====
        public static void TestAnnotatedDeclarations()
        {
            TestHarness.Section("Annotated Declarations in Block");

            // 规范 §14.3 示例形态：值 wrapper 修饰栈上变量
            TestBlock("{\n    @Clamped(0, 100)\n    var health: i32 = 50\n}",
                "[@Clamped(Int(0,I32), Int(100,I32)) var health: i32 = Int(50,I32)]");
            // 无参注解（不写括号）
            TestBlock("{\n    @Logged\n    var x: i32\n}",
                "[@Logged var x: i32]");

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

            TestVariableDeclarations();
            TestExpressionStatements();
            TestAssignStatements();
            TestReturnStatements();
            TestLoopControlStatements();
            TestIfStatements();
            TestErrorCases();
            TestAnnotatedDeclarations();

            return TestHarness.Summary("CodeBlock");
        }
    }
}
