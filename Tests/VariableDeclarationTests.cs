using System;

namespace RigiCompiler.Tests
{
    // 变量声明解析测试：全管线驱动，断言 AST 树产物
    // （AstDescribe 精确描述串 + 结构事实）。
    public class VariableDeclarationTests
    {
        public static void TestBasicDeclarations()
        {
            CompilerTestTools.Section("Basic Variable Declarations");

            TestDecl("var x = 42", "var x = Int(42,I32)");
            TestDecl("const name = \"Hello\"", "const name = Str(\"Hello\")");
            TestDecl("var flag = true", "var flag = Bool(True)");

            CompilerTestTools.Blank();
        }

        public static void TestTypedDeclarations()
        {
            CompilerTestTools.Section("Typed Variable Declarations");

            TestDecl("var x: i32 = 42", "var x: i32 = Int(42,I32)");
            TestDecl("const name: String = \"Hello\"", "const name: String = Str(\"Hello\")");
            TestDecl("var count: i64", "var count: i64");

            CompilerTestTools.Blank();
        }

        public static void TestNullableDeclarations()
        {
            CompilerTestTools.Section("Nullable Type Declarations");

            TestDecl("var x: i32? = null", "var x: i32? = Null");
            TestDecl("var name: String? = \"test\"", "var name: String? = Str(\"test\")");

            CompilerTestTools.Blank();
        }

        public static void TestGenericDeclarations()
        {
            CompilerTestTools.Section("Generic Type Declarations");

            TestDecl("var list: List\\<String>", "var list: List<String>");
            TestDecl("const map: Map\\<String,i32>", "const map: Map<String,i32>");

            CompilerTestTools.Blank();
        }

        // AST 结构断言（AGENTS §5：快照不作为唯一验证方式）
        public static void TestStructuralAssertions()
        {
            CompilerTestTools.Section("Structural Assertions");

            var decl = (VariableDeclarationASTNode)CompilerTestTools.ParseFirstDecl("var x: i32 = 42");
            CaseAssertions.CheckTrue("Initializer Root 存在", decl.Initializer != null);
            CaseAssertions.CheckTrue("Initializer 已填充", decl.Initializer!.IsAttached);
            CaseAssertions.CheckTrue("Initializer 内容是 IntLiteral 包装",
                decl.Initializer.Expression is LiteralExpressionASTNode);
            CaseAssertions.CheckTrue("Initializer Root 的 Parent 是声明节点",
                ReferenceEquals(decl.Initializer.Parent, decl));
            CaseAssertions.CheckTrue("TypeAnnotation 的 Parent 是声明节点",
                ReferenceEquals(decl.TypeAnnotation!.Parent, decl));
            CaseAssertions.CheckTrue("声明挂在 Root 下", decl.Parent is RootASTNode);

            // 回归：无初始化时 Initializer 必须为 null（不是空 Root）
            var noInit = (VariableDeclarationASTNode)CompilerTestTools.ParseFirstDecl("var count: i64");
            CaseAssertions.CheckTrue("无初始化时 Initializer 为 null", noInit.Initializer == null);

            CompilerTestTools.Blank();
        }

        // 解构声明（S7f，SYNTAX §18：var (a, b) = pair；与单名形态互斥）
        public static void TestDestructuringDeclarations()
        {
            CompilerTestTools.Section("Destructuring Declarations");

            TestDecl("var (a, b) = pair", "var (a, b) = Path(pair, [])");
            TestDecl("const (k, v) = getPair()", "const (k, v) = Path(getPair(), [])");

            // 结构断言（快照不作为唯一验证方式，AGENTS §5）
            var decl = (VariableDeclarationASTNode)CompilerTestTools.ParseFirstDecl("var (a, b) = pair");
            CaseAssertions.CheckTrue("DestructureNames 非空且保序",
                decl.DestructureNames is { Count: 2 } && decl.DestructureNames[0] == "a"
                && decl.DestructureNames[1] == "b");
            CaseAssertions.CheckTrue("单名 Name 保持空串（互斥）", decl.Name == "");
            CaseAssertions.CheckTrue("解构的 Initializer 已填充", decl.Initializer is { IsAttached: true });

            // 错误路径
            CaseAssertions.CheckParseError("var () = pair（空名字列表）",
                () => CompilerTestTools.ParseFirstDecl("var () = pair"),
                "Expected a name in destructuring declaration");
            CaseAssertions.CheckParseError("var (a, b); （无初始化器）",
                () => CompilerTestTools.ParseFirstDecl("var (a, b);"),
                "requires '=' with an initializer");
            CaseAssertions.CheckParseError("var (a b) = pair（缺逗号）",
                () => CompilerTestTools.ParseFirstDecl("var (a b) = pair"),
                "Expected ',' or ')' in destructuring declaration");

            CompilerTestTools.Blank();
        }

        // 辅助：解析声明并比对 AST 描述串
        private static void TestDecl(string code, string expectedDesc)
        {
            try
            {
                var node = CompilerTestTools.ParseFirstDecl(code);
                CaseAssertions.Check(code, AstDescribe.Decl(node), expectedDesc);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }


        internal static TestSuiteData Spec { get; } = new("VariableDeclaration",
        [
            (nameof(TestBasicDeclarations), TestBasicDeclarations),
            (nameof(TestTypedDeclarations), TestTypedDeclarations),
            (nameof(TestNullableDeclarations), TestNullableDeclarations),
            (nameof(TestGenericDeclarations), TestGenericDeclarations),
            (nameof(TestDestructuringDeclarations), TestDestructuringDeclarations),
            (nameof(TestStructuralAssertions), TestStructuralAssertions),
        ], sectionTitle: "VariableDeclaration");
    }
}
