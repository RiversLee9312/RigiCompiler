using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 类型声明解析测试（P3 M13 基础）
    ///
    /// 覆盖：
    /// 1. 简单 class 声明（空 body）
    /// 2. 带修饰符的 class（pub, open, abstract, etc.）
    /// 3. interface 声明
    /// 4. struct 声明
    /// 5. wrapper 声明
    ///
    /// 注：本阶段仅解析声明头部和空 body {}
    ///     成员解析（字段、方法）将在后续实现
    /// </summary>
    public class TypeDeclarationTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 简单 class 声明 =====
        public static void TestSimpleClass()
        {
            Console.WriteLine("=== Testing Simple Class Declaration ===");

            TestDeclaration("class Animal {}",
                "class Animal");

            TestDeclaration("class Dog {}",
                "class Dog");

            Console.WriteLine();
        }

        // ===== 2. 带修饰符的 class =====
        public static void TestClassWithModifiers()
        {
            Console.WriteLine("=== Testing Class with Modifiers ===");

            TestDeclaration("pub class Animal {}",
                "pub class Animal");

            TestDeclaration("pub open class Animal {}",
                "pub open class Animal");

            TestDeclaration("pub abstract class Shape {}",
                "pub abstract class Shape");

            TestDeclaration("pub shared class Session {}",
                "pub shared class Session");

            TestDeclaration("pub singleton class Config {}",
                "pub singleton class Config");

            Console.WriteLine();
        }

        // ===== 3. interface 声明 =====
        public static void TestInterface()
        {
            Console.WriteLine("=== Testing Interface Declaration ===");

            TestDeclaration("interface Drawable {}",
                "interface Drawable");

            TestDeclaration("pub interface Comparable {}",
                "pub interface Comparable");

            Console.WriteLine();
        }

        // ===== 4. struct 声明 =====
        public static void TestStruct()
        {
            Console.WriteLine("=== Testing Struct Declaration ===");

            TestDeclaration("struct Point {}",
                "struct Point");

            TestDeclaration("pub struct Vector2 {}",
                "pub struct Vector2");

            TestDeclaration("pub open struct BaseStruct {}",
                "pub open struct BaseStruct");

            TestDeclaration("pub rich struct Entry {}",
                "pub rich struct Entry");

            TestDeclaration("pub shared rich struct SharedEntry {}",
                "pub shared rich struct SharedEntry");

            Console.WriteLine();
        }

        // ===== 5. wrapper 声明 =====
        public static void TestWrapper()
        {
            Console.WriteLine("=== Testing Wrapper Declaration ===");

            TestDeclaration("wrapper Logged {}",
                "wrapper Logged");

            TestDeclaration("pub wrapper Cached {}",
                "pub wrapper Cached");

            Console.WriteLine();
        }

        // ===== 6. 全局字段与全局函数（复用同一个 DeclarationParserLayer）=====
        public static void TestGlobals()
        {
            Console.WriteLine("=== Testing Global Fields / Functions ===");

            TestDeclaration("var counter: i32", "var counter");
            TestDeclaration("pub const MAX: i32", "pub const MAX");
            TestDeclaration("pub func main() {}", "pub func main() {}");
            TestDeclaration("func add(a: i32, b: i32): i32 {}", "func add(a,b): i32 {}");
            TestDeclaration("pub static func helper()", "pub static func helper()");

            Console.WriteLine();
        }

        // ===== 7. 类成员（字段/方法/init/operator 全部复用同一路径）=====
        public static void TestMembers()
        {
            Console.WriteLine("=== Testing Type Members ===");

            TestDeclaration("class A { pub var name: String }",
                "class A {pub var name}");

            TestDeclaration("class A { pub func speak(): String {} }",
                "class A {pub func speak(): String {}}");

            TestDeclaration("class Point { pub init(x: i32, y: i32) {} }",
                "class Point {pub init(x,y) {}}");

            TestDeclaration("struct V { pub operator plus(o: V): V {} }",
                "struct V {pub operator plus(o): V {}}");

            TestDeclaration("interface D { func draw(c: Canvas) }",
                "interface D {func draw(c)}");

            TestDeclaration("class A { pub var x: i32\n priv var y: i32\n pub func f() {} }",
                "class A {pub var x, priv var y, pub func f() {}}");

            Console.WriteLine();
        }

        // ===== 8. 继承与接口 =====
        public static void TestInheritance()
        {
            Console.WriteLine("=== Testing Inheritance / Interfaces ===");

            TestDeclaration("class Dog : Animal {}", "class Dog : Animal");
            TestDeclaration("class C implements Drawable {}", "class C implements Drawable");
            TestDeclaration("class C : Shape implements Drawable {}",
                "class C : Shape implements Drawable");
            TestDeclaration("class C : Shape implements A, B {}",
                "class C : Shape implements A,B");
            TestDeclaration("pub interface I : Base {}", "pub interface I : Base");

            Console.WriteLine();
        }

        // ===== 9. 嵌套类型（递归复用本层，与顶层同一路径）=====
        public static void TestNestedTypes()
        {
            Console.WriteLine("=== Testing Nested Types ===");

            TestDeclaration("class Outer { class Inner {} }",
                "class Outer {class Inner}");

            TestDeclaration("class Outer { pub class Inner { pub var v: i32 } }",
                "class Outer {pub class Inner {pub var v}}");

            TestDeclaration("class Outer { struct S {} interface I {} }",
                "class Outer {struct S, interface I}");

            TestDeclaration("class A { class B { class C {} } }",
                "class A {class B {class C}}");

            Console.WriteLine();
        }

        // ===== 辅助方法 =====

        private static void TestDeclaration(string source, string expected)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var parser = new Parser();
                var root = (RootASTNode)parser.Parse(tokens);

                var formatted = FormatRoot(root);
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

        private static string FormatRoot(RootASTNode root)
        {
            if (root.Children.Count == 0)
                return "<empty>";

            return string.Join("; ", root.Children.ConvertAll(FormatDeclaration));
        }

        // 递归渲染：成员/嵌套类型走的是同一条路径（与 Parser 侧的复用结构对应）
        private static string FormatDeclaration(ASTNode node)
        {
            var head = node switch
            {
                ClassDeclarationASTNode c => Mods(c.Modifiers) + "class " + c.ClassName + Bases(c.BaseClass, c.Interfaces),
                // interface 用 `:` 继承父接口（SYNTAX §11），故按 `:` 渲染
                InterfaceDeclarationASTNode i => Mods(i.Modifiers) + "interface " + i.InterfaceName
                    + (i.BaseInterfaces.Count > 0
                        ? " : " + string.Join(",", i.BaseInterfaces.ConvertAll(FormatType))
                        : ""),
                StructDeclarationASTNode s => Mods(s.Modifiers) + "struct " + s.StructName + Bases(s.BaseStruct, s.Interfaces),
                EnumStructDeclarationASTNode e => Mods(e.Modifiers) + "enum struct " + e.EnumName,
                WrapperDeclarationASTNode w => Mods(w.Modifiers) + "wrapper " + w.WrapperName,
                VariableDeclarationASTNode v => Mods(v.Modifiers) + (v.IsConst ? "const " : "var ") + v.Name,
                CallableDeclarationASTNode f => FormatCallable(f),
                _ => $"<{node.GetType().Name}>"
            };

            if (node.Children.Count == 0) return head;
            return head + " {" + string.Join(", ", node.Children.ConvertAll(FormatDeclaration)) + "}";
        }

        private static string FormatCallable(CallableDeclarationASTNode f)
        {
            var kind = f.Kind switch
            {
                CallableKind.Operator => "operator ",
                CallableKind.Init => "",
                _ => "func "
            };
            var ps = string.Join(",", f.Parameters.Parameters.ConvertAll(p => p.Name));
            var ret = f.ReturnType != null ? ": " + FormatType(f.ReturnType) : "";
            var body = f.Body != null ? " {}" : "";
            return Mods(f.Modifiers) + kind + f.Name + "(" + ps + ")" + ret + body;
        }

        private static string Mods(System.Collections.Generic.List<string> m)
            => m.Count == 0 ? "" : string.Join(" ", m) + " ";

        private static string Bases(TypeReferenceASTNode? base_,
            System.Collections.Generic.List<TypeReferenceASTNode> ifaces)
        {
            var s = base_ != null ? " : " + FormatType(base_) : "";
            if (ifaces.Count > 0)
                s += " implements " + string.Join(",", ifaces.ConvertAll(FormatType));
            return s;
        }

        // 类型名取自 SymbolASTNode 的 element 链（a.b.c）
        private static string FormatType(TypeReferenceASTNode t)
        {
            var names = t.TypeSymbol.symbol.elements.ConvertAll(e => e.name);
            var s = names.Count == 0 ? "?" : string.Join(".", names);
            return t.IsNullable ? s + "?" : s;
        }

        // ===== 入口 =====
        public static void RunAll()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Type Declaration Tests (P3 M13 Basic)               ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestSimpleClass();
            TestClassWithModifiers();
            TestInterface();
            TestStruct();
            TestWrapper();
            TestGlobals();
            TestMembers();
            TestInheritance();
            TestNestedTypes();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }
}
