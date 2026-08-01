using System;

namespace LatteCompiler.Tests
{
    // 变量声明解析测试：全管线驱动，断言 AST 树产物
    // （AstDescribe 精确描述串 + 结构事实）。
    public class VariableDeclarationTests
    {
        public static void TestBasicDeclarations()
        {
            TestHarness.Section("Basic Variable Declarations");

            TestDecl("var x = 42", "var x = Int(42,I32)");
            TestDecl("const name = \"Hello\"", "const name = Str(\"Hello\")");
            TestDecl("var flag = true", "var flag = Bool(True)");

            TestHarness.Blank();
        }

        public static void TestTypedDeclarations()
        {
            TestHarness.Section("Typed Variable Declarations");

            TestDecl("var x: i32 = 42", "var x: i32 = Int(42,I32)");
            TestDecl("const name: String = \"Hello\"", "const name: String = Str(\"Hello\")");
            TestDecl("var count: i64", "var count: i64");

            TestHarness.Blank();
        }

        public static void TestNullableDeclarations()
        {
            TestHarness.Section("Nullable Type Declarations");

            TestDecl("var x: i32? = null", "var x: i32? = Null");
            TestDecl("var name: String? = \"test\"", "var name: String? = Str(\"test\")");

            TestHarness.Blank();
        }

        public static void TestGenericDeclarations()
        {
            TestHarness.Section("Generic Type Declarations");

            TestDecl("var list: List\\<String>", "var list: List<String>");
            TestDecl("const map: Map\\<String,i32>", "const map: Map<String,i32>");

            TestHarness.Blank();
        }

        // AST 结构断言（AGENTS §5：快照不作为唯一验证方式）
        public static void TestStructuralAssertions()
        {
            TestHarness.Section("Structural Assertions");

            var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var x: i32 = 42");
            TestHarness.CheckTrue("Initializer Root 存在", decl.Initializer != null);
            TestHarness.CheckTrue("Initializer 已填充", decl.Initializer!.IsAttached);
            TestHarness.CheckTrue("Initializer 内容是 IntLiteral 包装",
                decl.Initializer.Expression is LiteralExpressionASTNode);
            TestHarness.CheckTrue("Initializer Root 的 Parent 是声明节点",
                ReferenceEquals(decl.Initializer.Parent, decl));
            TestHarness.CheckTrue("TypeAnnotation 的 Parent 是声明节点",
                ReferenceEquals(decl.TypeAnnotation!.Parent, decl));
            TestHarness.CheckTrue("声明挂在 Root 下", decl.Parent is RootASTNode);

            // 回归：无初始化时 Initializer 必须为 null（不是空 Root）
            var noInit = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var count: i64");
            TestHarness.CheckTrue("无初始化时 Initializer 为 null", noInit.Initializer == null);

            TestHarness.Blank();
        }

        // 解构声明（S7f，SYNTAX §18：var (a, b) = pair；与单名形态互斥）
        public static void TestDestructuringDeclarations()
        {
            TestHarness.Section("Destructuring Declarations");

            TestDecl("var (a, b) = pair", "var (a, b) = Path(pair, [])");
            TestDecl("const (k, v) = getPair()", "const (k, v) = Path(getPair(), [])");

            // 结构断言（快照不作为唯一验证方式，AGENTS §5）
            var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var (a, b) = pair");
            TestHarness.CheckTrue("DestructureNames 非空且保序",
                decl.DestructureNames is { Count: 2 } && decl.DestructureNames[0] == "a"
                && decl.DestructureNames[1] == "b");
            TestHarness.CheckTrue("单名 Name 保持空串（互斥）", decl.Name == "");
            TestHarness.CheckTrue("解构的 Initializer 已填充", decl.Initializer is { IsAttached: true });

            // 错误路径
            TestHarness.CheckParseError("var () = pair（空名字列表）",
                () => TestHarness.ParseFirstDecl("var () = pair"),
                "Expected a name in destructuring declaration");
            TestHarness.CheckParseError("var (a, b); （无初始化器）",
                () => TestHarness.ParseFirstDecl("var (a, b);"),
                "requires '=' with an initializer");
            TestHarness.CheckParseError("var (a b) = pair（缺逗号）",
                () => TestHarness.ParseFirstDecl("var (a b) = pair"),
                "Expected ',' or ')' in destructuring declaration");

            TestHarness.Blank();
        }

        // 辅助：解析声明并比对 AST 描述串
        private static void TestDecl(string code, string expectedDesc)
        {
            try
            {
                var node = TestHarness.ParseFirstDecl(code);
                TestHarness.Check(code, AstDescribe.Decl(node), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }

        public static int RunAll()
        {
            TestHarness.Reset();

            TestBasicDeclarations();
            TestTypedDeclarations();
            TestNullableDeclarations();
            TestGenericDeclarations();
            TestDestructuringDeclarations();
            TestStructuralAssertions();

            return TestHarness.Summary("VariableDeclaration");
        }
    }
}
