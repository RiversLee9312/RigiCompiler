using System;
using System.Collections.Generic;
using System.Text;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 属性访问器解析测试（PropertyAccessorParserLayer，SYNTAX.md §9.4）
    ///
    /// 覆盖：
    /// 1. 完整形态：get/set + 修饰符 + (value: _) 参数 + 自定义体 + 初始化器
    /// 2. 编译器生成访问器：pub get / priv set（无参无体，仅访问控制）
    /// 3. 计算属性：(_: _) 参数（无 backing field）
    /// 4. 三类定义位置：类字段 / 全局变量 / 栈上局部变量（统一经由 VariableDeclarationParserLayer）
    /// 5. 错误用例：重复访问器、backing field 不一致、空块、无参带体、非法参数名
    ///
    /// 测试驱动方式：完整 Parse(tokens) 后取对应声明节点描述比对。
    /// </summary>
    public class PropertyAccessorTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 访问器形态 =====
        public static void TestAccessorForms()
        {
            Console.WriteLine("=== Testing Accessor Forms ===");

            // 完整形态：修饰符 + (value: _) + 自定义体 + 初始化器
            TestDeclaration(
                "var width: i32 { pub get(value: _) { return value } priv set(value: _) { log(value) } } = 100",
                "var width: i32 {pub get(value){}, priv set(value){}} = LiteralExpressionASTNode");

            // 编译器生成访问器（仅访问控制，无参无体；自动访问器以换行分隔——
            // 与成员声明的换行分隔规则一致）
            TestDeclaration(
                "var height: i32 {\n    pub get\n    priv set\n} = 200",
                "var height: i32 {pub get, priv set} = LiteralExpressionASTNode");

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

            Console.WriteLine();
        }

        // ===== 2. 跨行书写（规范示例原样）=====
        public static void TestMultiLine()
        {
            Console.WriteLine("=== Testing Multi-Line Accessor Block ===");

            TestDeclaration(
                "var width: i32 {\n" +
                "    pub get(value: _) {\n" +
                "        return value\n" +
                "    }\n" +
                "    priv set(value: _) {\n" +
                "        log(value)\n" +
                "    }\n" +
                "} = 100",
                "var width: i32 {pub get(value){}, priv set(value){}} = LiteralExpressionASTNode");

            // 自动访问器以换行收尾（无修饰符）
            TestDeclaration(
                "var c: i32 {\n    get\n    set\n} = 0",
                "var c: i32 {get, set} = LiteralExpressionASTNode");

            Console.WriteLine();
        }

        // ===== 3. 三类定义位置（§9.4）=====
        public static void TestPositions()
        {
            Console.WriteLine("=== Testing Accessor Positions ===");

            // 类字段（DeclarationParserLayer → VariableDeclarationParserLayer）
            TestNode(
                "class Size { pub var width: i32 { get(value: _) { return value } priv set(value: _) { log(value) } } }",
                root => root.Children[0].Children[0],
                "pub var width: i32 {get(value){}, priv set(value){}}");

            // 全局变量
            TestDeclaration(
                "var g: i32 {\n    pub get\n    priv set\n} = 1",
                "var g: i32 {pub get, priv set} = LiteralExpressionASTNode");

            // 栈上局部变量（函数体 CodeBlock → VariableDeclarationParserLayer）
            TestNode(
                "func example() { var localCounter: i32 { get(value: _) { return value } set(value: _) { log(value) } } = 0 }",
                root => ((CallableDeclarationASTNode)root.Children[0]).Body!.Children[0],
                "var localCounter: i32 {get(value){}, set(value){}} = LiteralExpressionASTNode");

            Console.WriteLine();
        }

        // ===== 4. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Error Cases (expect ParserException) ===");

            // 同一块内重复 get
            TestError(
                "var x: i32 { get(value: _) { return value } get(value: _) { return value } }",
                "重复 get 访问器");

            // get/set 的 backing field 需求不一致（§9.4 一致性规则）
            TestError(
                "var x: i32 { get(value: _) { return value } set(_: _) { log(1) } }",
                "backing field 不一致");

            // 空访问器块
            TestError("var x: i32 { }", "空访问器块");

            // 无参数却带体（规范形态：体必须跟在 (value: _) 或 (_: _) 之后）
            TestError("var x: i32 { get { return 1 } }", "无参带体");

            // 非法参数名（只允许 value 或 _）
            TestError("var x: i32 { get(v: _) { return v } }", "非法参数名");

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        // 解析完整源码并取根节点第一个子节点（顶层声明路径）
        private static void TestDeclaration(string source, string expected)
        {
            TestNode(source, root => root.Children[0], expected);
        }

        private static void TestNode(string source, Func<RootASTNode, ASTNode> pick, string expected)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var parser = new Parser();
                var root = (RootASTNode)parser.Parse(tokens);

                var node = pick(root);
                if (node is not VariableDeclarationASTNode decl)
                {
                    Fail(source, $"expected VariableDeclarationASTNode, got {node.GetType().Name}");
                    return;
                }

                string actual = Describe(decl);
                if (actual == expected)
                {
                    Console.WriteLine($"  [PASS] {source}");
                    Console.WriteLine($"      => {actual}");
                    passCount++;
                }
                else
                {
                    Fail(source, $"expected {expected}, got {actual}");
                }
            }
            catch (Exception ex)
            {
                Fail(source, $"unexpected exception: {ex.Message}");
            }
        }

        private static void TestError(string source, string reason)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var parser = new Parser();
                parser.Parse(tokens);
                Fail(source, $"expected ParserException ({reason}), but parse succeeded");
            }
            catch (ParserException)
            {
                Console.WriteLine($"  [PASS] {source}  (rejected: {reason})");
                passCount++;
            }
            catch (Exception ex)
            {
                Fail(source, $"expected ParserException ({reason}), got {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Fail(string source, string message)
        {
            Console.WriteLine($"  [FAIL] {source}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== AST 描述 =====

        private static string Describe(VariableDeclarationASTNode node)
        {
            var sb = new StringBuilder();
            if (node.Modifiers.Count > 0) sb.Append(string.Join(" ", node.Modifiers)).Append(' ');
            sb.Append(node.IsConst ? "const " : "var ").Append(node.Name);
            if (node.TypeAnnotation != null) sb.Append(": ").Append(DescribeType(node.TypeAnnotation));

            var accessors = new List<string>();
            if (node.Getter != null) accessors.Add(DescribeAccessor(node.Getter));
            if (node.Setter != null) accessors.Add(DescribeAccessor(node.Setter));
            if (accessors.Count > 0)
                sb.Append(" {").Append(string.Join(", ", accessors)).Append('}');

            // 初始化器只验证存在与节点种类（表达式本身的描述由表达式套件负责）
            if (node.Initializer != null) sb.Append(" = ").Append(node.Initializer.Expression.GetType().Name);
            return sb.ToString();
        }

        private static string DescribeAccessor(PropertyAccessorASTNode a)
        {
            var s = a.Modifiers.Count > 0 ? string.Join(" ", a.Modifiers) + " " : "";
            s += a.Kind == AccessorKind.Get ? "get" : "set";
            if (a.Body != null)
                s += a.HasBackingField ? "(value){}" : "(_){}" ;
            return s;
        }

        // 类型名取自 SymbolASTNode 的 element 链（a.b.c）
        private static string DescribeType(TypeReferenceASTNode t)
        {
            var names = t.TypeSymbol.symbol.elements.ConvertAll(e => e.name);
            var s = names.Count == 0 ? "?" : string.Join(".", names);
            return t.IsNullable ? s + "?" : s;
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Property Accessor Tests (P3 §9.4)                   ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestAccessorForms();
            TestMultiLine();
            TestPositions();
            TestErrorCases();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            return failCount;
        }
    }
}
