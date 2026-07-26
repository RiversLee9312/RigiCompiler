using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 类型声明解析测试（P3 M13/M14/M15 统一声明层）
    ///
    /// 覆盖：
    /// 1. 简单 class 声明（空 body）
    /// 2. 带修饰符的 class（pub, open, abstract, etc.）
    /// 3. interface 声明
    /// 4. struct 声明
    /// 5. wrapper 声明
    /// 6. 全局字段与全局函数
    /// 7. 类成员（字段/方法/init/operator）
    /// 8. 继承与 implements 列表
    /// 9. 嵌套类型（多层）
    /// 10. 声明上的泛型参数（类型/函数/operator，含约束与可变参数）
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

        // ===== 10. 声明上的泛型参数（复用 GenericParametersParserLayer，SYNTAX §3.6）=====
        public static void TestDeclarationGenericParameters()
        {
            Console.WriteLine("=== Testing Generic Parameters on Declarations ===");

            // 类型声明
            TestDeclaration("class Container\\<TElement> {}",
                "class Container\\<TElement>");
            TestDeclaration("interface Comparable\\<T> { func compareTo(other: T): i32 }",
                "interface Comparable\\<T> {func compareTo(other): i32}");
            TestDeclaration("class Producer\\<out TElement> {}",
                "class Producer\\<out TElement>");
            TestDeclaration("class Consumer\\<in TElement> {}",
                "class Consumer\\<in TElement>");
            TestDeclaration("pub struct Pair\\<TFirst, TSecond> {}",
                "pub struct Pair\\<TFirst, TSecond>");
            TestDeclaration("wrapper Logged\\<T> {}",
                "wrapper Logged\\<T>");

            // 泛型 + 继承 + implements（泛型列表在继承子句之前）
            TestDeclaration("class MyList\\<TElement> : List implements Iterable {}",
                "class MyList\\<TElement> : List implements Iterable");

            // 函数声明（全局与成员共用一条路径）
            TestDeclaration("func transform\\<TInput, TResult>(input: TInput): TResult {}",
                "func transform\\<TInput, TResult>(input): TResult {}");
            TestDeclaration("class A { func map\\<T>(x: T): T {} }",
                "class A {func map\\<T>(x): T {}}");
            TestDeclaration("struct V { pub operator plus\\<TAnother extends Addable>(another: TAnother): V {} }",
                "struct V {pub operator plus\\<TAnother extends Addable>(another): V {}}");

            // 约束子句（extends/supers/with；Target 裸标识符即隐含的泛型参数）
            TestDeclaration("func process\\<TItem extends Comparable, Serializable supers BaseType>(item: TItem): TItem {}",
                "func process\\<TItem extends Comparable, Serializable supers BaseType>(item): TItem {}");
            TestDeclaration("func dump\\<TItem with Serializable>(item: TItem) {}",
                "func dump\\<TItem with Serializable>(item) {}");

            // 可变 / 具名可变泛型参数
            TestDeclaration("func sum\\<TArgs...>(items: TArgs...) {}",
                "func sum\\<TArgs...>(items) {}");
            TestDeclaration("pub func update\\<named TValues... with Serializable>(configs: named TValues...): bool {}",
                "pub func update\\<named TValues..., TValues with Serializable>(configs): bool {}");

            // 型变参数 + 约束
            TestDeclaration("class Cache\\<out TElement extends Comparable> {}",
                "class Cache\\<out TElement, TElement extends Comparable>");

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
                ClassDeclarationASTNode c => Mods(c.Modifiers) + "class " + c.ClassName
                    + FormatGenerics(c.GenericParameters) + Bases(c.BaseClass, c.Interfaces),
                // interface 用 `:` 继承父接口（SYNTAX §11），故按 `:` 渲染
                InterfaceDeclarationASTNode i => Mods(i.Modifiers) + "interface " + i.InterfaceName
                    + FormatGenerics(i.GenericParameters)
                    + (i.BaseInterfaces.Count > 0
                        ? " : " + string.Join(",", i.BaseInterfaces.ConvertAll(FormatType))
                        : ""),
                StructDeclarationASTNode s => Mods(s.Modifiers) + "struct " + s.StructName
                    + FormatGenerics(s.GenericParameters) + Bases(s.BaseStruct, s.Interfaces),
                EnumStructDeclarationASTNode e => Mods(e.Modifiers) + "enum struct " + e.EnumName
                    + FormatGenerics(e.GenericParameters),
                WrapperDeclarationASTNode w => Mods(w.Modifiers) + "wrapper " + w.WrapperName
                    + FormatGenerics(w.GenericParameters),
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
            return Mods(f.Modifiers) + kind + f.Name + FormatGenerics(f.GenericParameters)
                + "(" + ps + ")" + ret + body;
        }

        // 泛型形参列表：与 GenericParametersTests 同一套 AST 结构，按声明上的位置内联渲染。
        // 注意约束的 Target 为裸标识符时即隐含的泛型参数（见 GenericConstraintASTNode 注释），
        // 因此 extends/supers/with 子句渲染在参数之后（如 \<named TValues..., TValues with Serializable>）
        private static string FormatGenerics(GenericParameterListASTNode? gp)
        {
            if (gp == null) return "";
            var parts = gp.Parameters.ConvertAll(FormatGenericParam);
            parts.AddRange(gp.Constraints.ConvertAll(FormatGenericConstraint));
            return "\\<" + string.Join(", ", parts) + ">";
        }

        private static string FormatGenericParam(GenericParameterASTNode p)
        {
            var prefix = p.Variance switch
            {
                GenericVariance.Out => "out ",
                GenericVariance.In => "in ",
                _ => p.IsNamedVariadic ? "named " : ""
            };
            var suffix = (p.IsVariadic || p.IsNamedVariadic) ? "..." : "";
            return prefix + p.Name + suffix;
        }

        private static string FormatGenericConstraint(GenericConstraintASTNode c)
        {
            var kind = c.Kind switch
            {
                GenericConstraintKind.Extends => "extends",
                GenericConstraintKind.Supers => "supers",
                _ => "with"
            };
            return FormatType(c.Target) + " " + kind + " " + FormatType(c.Bound);
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
            Console.WriteLine("║  Type Declaration Tests (P3 M13/M14)                 ║");
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
            TestDeclarationGenericParameters();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }
}
