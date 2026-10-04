using System;

namespace RigiCompiler.Tests
{
    // 属性访问器解析测试（PropertyAccessorParserLayer，SYNTAX.md §9.4）：全管线驱动，
    // 取对应声明节点，断言 AstDescribe.VarDecl 描述串（含完整初始化器表达式描述）。
    //
    // 覆盖：
    // 1. 完整形态：get/set + 修饰符 + (value: _) 参数 + 自定义体 + 初始化器
    // 2. 编译器生成访问器：pub get / priv set（无参无体，仅访问控制）
    // 3. 计算属性：(_: _) 参数（无 backing field）
    // 4. 三类定义位置：类字段 / 全局变量 / 栈上局部变量（统一经由 VariableDeclarationParserLayer）
    // 5. 错误用例：重复访问器、backing field 不一致、空块、无参带体、非法参数名
    public class PropertyAccessorTests
    {
        // ===== 1. 访问器形态 =====
        public static void TestAccessorForms()
        {
            TestHarness.Section("Testing Accessor Forms");

            // 完整形态：修饰符 + (value: _) + 自定义体 + 初始化器
            TestDeclaration(
                "var width: i32 { pub get(value: _) { return value } priv set(value: _) { log(value) } } = 100",
                "var width: i32 {pub get(value){}, priv set(value){}} = Int(100,I32)");

            // 编译器生成访问器（仅访问控制，无参无体；自动访问器以换行分隔——
            // 与成员声明的换行分隔规则一致）
            TestDeclaration(
                "var height: i32 {\n    pub get\n    priv set\n} = 200",
                "var height: i32 {pub get, priv set} = Int(200,I32)");

            // 计算属性（_: _，无 backing field）
            TestDeclaration(
                "var area: i32 { get(_: _) { return (width * height) } }",
                "var area: i32 {get(_){}}");

            // 仅 getter / 仅 setter
            TestDeclaration(
                "var x: i32 { get(value: _) { return value } }",
                "var x: i32 {get(value){}}");
            TestDeclaration(
                "var y: i32 { set(value: _) { log(value) } }",
                "var y: i32 {set(value){}}");

            // const + 计算 getter
            TestDeclaration(
                "const name: String { get(_: _) { return \"x\" } }",
                "const name: String {get(_){}}");

            // 计算 get+set 带修饰符
            TestDeclaration(
                "var z: i32 { pub get(_: _) { return 1 } priv set(_: _) { log(1) } }",
                "var z: i32 {pub get(_){}, priv set(_){}}");

            TestHarness.Blank();
        }

        // ===== 2. 跨行书写（规范示例原样）=====
        public static void TestMultiLine()
        {
            TestHarness.Section("Testing Multi-Line Accessor Block");

            TestDeclaration(
                "var width: i32 {\n" +
                "    pub get(value: _) {\n" +
                "        return value\n" +
                "    }\n" +
                "    priv set(value: _) {\n" +
                "        log(value)\n" +
                "    }\n" +
                "} = 100",
                "var width: i32 {pub get(value){}, priv set(value){}} = Int(100,I32)");

            // 自动访问器以换行收尾（无修饰符）
            TestDeclaration(
                "var c: i32 {\n    get\n    set\n} = 0",
                "var c: i32 {get, set} = Int(0,I32)");

            TestHarness.Blank();
        }

        // ===== 3. 三类定义位置（§9.4）=====
        public static void TestPositions()
        {
            TestHarness.Section("Testing Accessor Positions");

            // 类字段（DeclarationParserLayer → VariableDeclarationParserLayer）
            TestNode(
                "class Size { pub var width: i32 { get(value: _) { return value } priv set(value: _) { log(value) } } }",
                root => ((ClassDeclarationASTNode)root.Declarations[0]).Members[0],
                "pub var width: i32 {get(value){}, priv set(value){}}");

            // 全局变量
            TestDeclaration(
                "var g: i32 {\n    pub get\n    priv set\n} = 1",
                "var g: i32 {pub get, priv set} = Int(1,I32)");

            // 栈上局部变量（函数体 CodeBlock → VariableDeclarationParserLayer）
            TestNode(
                "func example() { var localCounter: i32 { get(value: _) { return value } set(value: _) { log(value) } } = 0 }",
                root => ((CallableDeclarationASTNode)root.Declarations[0]).Body!.Statements[0],
                "var localCounter: i32 {get(value){}, set(value){}} = Int(0,I32)");

            TestHarness.Blank();
        }

        // ===== 4. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Testing Error Cases (expect ParserException)");

            // 同一块内重复 get
            TestHarness.CheckParseError(
                "var x: i32 { get(value: _) { return value } get(value: _) { return value } }",
                () => TestHarness.ParseRoot("var x: i32 { get(value: _) { return value } get(value: _) { return value } }"),
                "Duplicate 'get' accessor");

            // get/set 的 backing field 需求不一致（§9.4 一致性规则）
            TestHarness.CheckParseError(
                "var x: i32 { get(value: _) { return value } set(_: _) { log(1) } }",
                () => TestHarness.ParseRoot("var x: i32 { get(value: _) { return value } set(_: _) { log(1) } }"),
                "must agree on whether a backing field is required");

            // 空访问器块
            TestHarness.CheckParseError(
                "var x: i32 { }",
                () => TestHarness.ParseRoot("var x: i32 { }"),
                "at least one 'get' or 'set'");

            // 无参数却带体（规范形态：体必须跟在 (value: _) 或 (_: _) 之后）
            TestHarness.CheckParseError(
                "var x: i32 { get { return 1 } }",
                () => TestHarness.ParseRoot("var x: i32 { get { return 1 } }"),
                "Expected '(' for accessor parameters");

            // 非法参数名（只允许 value 或 _）
            TestHarness.CheckParseError(
                "var x: i32 { get(v: _) { return v } }",
                () => TestHarness.ParseRoot("var x: i32 { get(v: _) { return v } }"),
                "Expected 'value' or '_' as accessor parameter");

            // 已提交访问器之后的游离修饰符：priv 无 get/set 归属，不得静默吞掉
            TestHarness.CheckParseError(
                "var x: i32 { pub get\\n priv\\n }（尾随游离修饰符）",
                () => TestHarness.ParseRoot("var x: i32 {\n    pub get\n    priv\n}"),
                "Expected 'get' or 'set' after accessor modifier");

            // 块内只有修饰符、没有任何 get/set
            TestHarness.CheckParseError(
                "var x: i32 { priv }（只有修饰符）",
                () => TestHarness.ParseRoot("var x: i32 { priv }"),
                "Expected 'get' or 'set' after accessor modifier");

            TestHarness.Blank();
        }

        // ===== 测试辅助 =====

        // 解析完整源码并取根节点第一个子节点（顶层声明路径）
        private static void TestDeclaration(string source, string expected)
        {
            TestNode(source, root => root.Declarations[0], expected);
        }

        // 辅助：解析后按 pick 取目标声明节点，比对 AstDescribe.VarDecl 描述串
        private static void TestNode(string source, Func<RootASTNode, ASTNode> pick, string expected)
        {
            try
            {
                var root = TestHarness.ParseRoot(source);
                var node = pick(root);
                if (node is not VariableDeclarationASTNode decl)
                {
                    TestHarness.CheckTrue(Label(source), false,
                        $"expected VariableDeclarationASTNode, got {node.GetType().Name}");
                    return;
                }

                TestHarness.Check(Label(source), AstDescribe.VarDecl(decl), expected);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{Label(source)} => 意外异常", false, ex.Message);
            }
        }

        // 标签：多行源码的换行转义显示
        private static string Label(string source) => source.Replace("\n", "\\n");

        // ===== 入口 =====
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        internal static ParallelSuiteRunner.SuiteSpec Spec { get; } = new("PropertyAccessor",
        [
            (nameof(TestAccessorForms), TestAccessorForms),
            (nameof(TestMultiLine), TestMultiLine),
            (nameof(TestPositions), TestPositions),
            (nameof(TestErrorCases), TestErrorCases),
        ], sectionTitle: "PropertyAccessor");
    }
}
