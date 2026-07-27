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
    /// 11. enum struct 的 [case 列表]（固定/参数化 case、显式判别值、错误用例）
    /// 12. init 参数映射（_ -> field，含默认值/显式名/混合形态、错误用例）
    /// 13. like 委托（§9.6）与 ext 扩展成员（§4.4）
    /// 14. @ 注解（wrapper 应用，§14.5，P5）
    /// 15. wrapper proxy 成员（.proxy.* specific/wildcard，§14.2/§14.6，P5）
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

        // ===== 11. enum struct 的 [case 列表]（SYNTAX §12）=====
        public static void TestEnumCases()
        {
            Console.WriteLine("=== Testing Enum Struct Case List ===");

            // 固定 case（规范 Direction 示例形态；init 参数映射 _ -> 为 M18 内容）
            TestDeclaration(
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    priv init(degrees: i32)\n" +
                "}[\n" +
                "    North(0),\n" +
                "    South(180),\n" +
                "    East(90),\n" +
                "    West(270)\n" +
                "]",
                "pub enum struct Direction {pub const degrees, priv init(degrees)}[North(0), South(180), East(90), West(270)]");

            // 参数化 case：_ 参数洞 + 具名实参
            TestDeclaration(
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(code: i32)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]",
                "pub enum struct RequestResult {pub const errorCode, pub init(code)}[Success(-1), Failed(errorCode = _)]");

            // 无实参的固定 case
            TestDeclaration("enum struct Color {}[Red, Green, Blue]",
                "enum struct Color[Red, Green, Blue]");

            // 显式判别值（§12.4）
            TestDeclaration(
                "pub enum struct SteadyABIEnum {}[\n" +
                "    First -> 0,\n" +
                "    Second -> 2,\n" +
                "    Third -> 1\n" +
                "]",
                "pub enum struct SteadyABIEnum[First -> 0, Second -> 2, Third -> 1]");

            // 参数化 case + 显式判别值
            TestDeclaration(
                "pub enum struct StableRequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(code: i32)\n" +
                "}[\n" +
                "    Success(-1) -> 0,\n" +
                "    Failed(errorCode = _) -> 1\n" +
                "]",
                "pub enum struct StableRequestResult {pub const errorCode, pub init(code)}[Success(-1) -> 0, Failed(errorCode = _) -> 1]");

            // 无 case 列表（允许缺省，等价于空列表）
            TestDeclaration("pub enum struct Empty {}", "pub enum struct Empty");

            // 错误用例
            TestError("enum struct E {}[A, A]", "case 名重复");
            TestError("enum struct E {}[A -> 0, B -> 0]", "判别值重复");
            TestError("enum struct E {}[A -> 0, B]", "显式/分配混用");
            TestError("enum struct E {}[A -> -1]", "负判别值");

            Console.WriteLine();
        }

        // ===== 12. init 参数映射（_ -> field，SYNTAX §9.3）=====
        public static void TestInitParameterMapping()
        {
            Console.WriteLine("=== Testing Init Parameter Mapping ===");

            // 规范 §9.3 示例形态：_ 映射 / 带默认值 / 显式参数名
            TestDeclaration(
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "    pub init(_ -> x = 0, _ -> y = 0)\n" +
                "    pub init(horizontal: i32 -> x, vertical: i32 -> y)\n" +
                "}",
                "pub class Point {pub var x, pub var y, pub init(_ -> x,_ -> y), pub init(_ -> x = 0,_ -> y = 0), pub init(horizontal: i32 -> x,vertical: i32 -> y)}");

            // 混合：映射参数 + 普通参数（带体）
            TestDeclaration(
                "pub class P {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x, label: String) {}\n" +
                "}",
                "pub class P {pub var x, pub init(_ -> x,label) {}}");

            // struct 的 init 映射（§10 Vector2 风格）
            TestDeclaration(
                "pub struct Vector2 {\n" +
                "    pub var x: float\n" +
                "    pub var y: float\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}",
                "pub struct Vector2 {pub var x, pub var y, pub init(_ -> x,_ -> y)}");

            // enum struct 规范示例（§12 Direction：M17 case 列表 + M18 init 映射会师）
            TestDeclaration(
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    priv init(_ -> degrees)\n" +
                "}[\n" +
                "    North(0),\n" +
                "    South(180)\n" +
                "]",
                "pub enum struct Direction {pub const degrees, priv init(_ -> degrees)}[North(0), South(180)]");

            // 错误：非 init 的形参列表不允许映射
            TestError("func f(x: i32 -> y) {}", "func 不允许参数映射");
            TestError("class A { func g(_ -> x) {} }", "成员函数不允许参数映射");

            // 错误：-> 后缺字段名
            TestError("class A { init(_ -> ) }", "缺字段名");

            Console.WriteLine();
        }

        // ===== 13. like 委托（§9.6）与 ext 扩展成员（§4.4）=====
        public static void TestLikeAndExtension()
        {
            Console.WriteLine("=== Testing like Delegation / ext Extension ===");

            // like 委托（规范 §9.6 示例形态）
            TestDeclaration(
                "pub class Apple : Fruit like pear {\n" +
                "    pub var pear: Pear = Pear()\n" +
                "}",
                "pub class Apple : Fruit like pear {pub var pear}");

            // like 跟在 implements 之后 / 无基类直接 like（解析层允许，语义待查）
            TestDeclaration("class A implements Drawable like d {}",
                "class A implements Drawable like d");
            TestDeclaration("class A like x {}", "class A like x");

            // 错误：非 class 不允许 like；like 后缺字段名
            TestError("struct S like x {}", "struct 不允许 like");
            TestError("class A like {}", "like 后缺字段名");

            // ext 扩展函数与扩展字段（规范 §4.4 示例形态）
            TestDeclaration("pub ext func String.reversed(): String {}",
                "pub ext func String.reversed(): String {}");
            TestDeclaration(
                "pub ext var String.isEmpty: bool { get(_: _) { return (this.length == 0) } }",
                "pub ext var String.isEmpty");

            // 错误：限定名必须有 ext 修饰（全局函数与全局变量均拒绝）
            TestError("func String.reversed(): String {}", "限定名缺 ext");
            TestError("var a.b: i32", "限定名缺 ext");

            Console.WriteLine();
        }

        // ===== 辅助方法 =====

        private static void TestError(string source, string reason)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var parser = new Parser();
                parser.Parse(tokens);
                Console.WriteLine($"FAIL: {source}");
                Console.WriteLine($"  Expected ParserException ({reason}), but parse succeeded");
                failCount++;
            }
            catch (ParserException)
            {
                Console.WriteLine($"PASS: {source}  (rejected: {reason})");
                passCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {source}");
                Console.WriteLine($"  Expected ParserException ({reason}), got {ex.GetType().Name}: {ex.Message}");
                failCount++;
            }
        }

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
            if (root.Declarations.Count == 0)
                return "<empty>";

            return string.Join("; ", root.Declarations.ConvertAll(FormatDeclaration));
        }

        // 递归渲染：成员/嵌套类型走的是同一条路径（与 Parser 侧的复用结构对应）
        private static string FormatDeclaration(ASTNode node)
        {
            var head = node switch
            {
                ClassDeclarationASTNode c => Mods(c.Modifiers) + "class " + c.ClassName
                    + FormatGenerics(c.GenericParameters) + Bases(c.BaseClass, c.Interfaces)
                    + (c.LikeTarget != null ? " like " + c.LikeTarget : ""),
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

            // @ 注解（SYNTAX §14.5）渲染在声明头之前（与源码书写位置一致）
            head = FormatAnnotations((IWrapperAttachable)node) + head;

            // 类型节点的成员容器（var/func 等无成员列表，视为空）
            var members = node switch
            {
                ClassDeclarationASTNode c => c.Members,
                InterfaceDeclarationASTNode i => i.Members,
                StructDeclarationASTNode s => s.Members,
                EnumStructDeclarationASTNode e => e.Members,
                WrapperDeclarationASTNode w => w.Members,
                _ => new List<ASTNode>()
            };

            if (members.Count == 0 && node is not EnumStructDeclarationASTNode) return head;

            var body = members.Count == 0
                ? ""
                : " {" + string.Join(", ", members.ConvertAll(FormatDeclaration)) + "}";
            // enum struct 的 [case 列表] 位于类型体 } 之后（SYNTAX §12）
            var cases = node is EnumStructDeclarationASTNode es ? FormatEnumCases(es) : "";
            return head + body + cases;
        }

        private static string FormatEnumCases(EnumStructDeclarationASTNode e)
        {
            if (e.Cases.Count == 0) return "";
            return "[" + string.Join(", ", e.Cases.ConvertAll(FormatEnumCase)) + "]";
        }

        private static string FormatEnumCase(EnumCaseASTNode c)
        {
            var s = c.CaseName;
            if (c.Arguments.Count > 0)
                s += "(" + string.Join(", ", c.Arguments.ConvertAll(FormatArgument)) + ")";
            if (c.DiscriminantValue != null)
                s += " -> " + c.DiscriminantValue.Value;
            return s;
        }

        private static string FormatArgument(ArgumentASTNode a)
        {
            var v = FormatExpr(a.Value.Expression);
            return a.Name != null ? a.Name + " = " + v : v;
        }

        // case 实参的紧凑渲染：只覆盖测试所需形态，复杂表达式回退为节点名
        private static string FormatExpr(ASTNode e) => e switch
        {
            LiteralExpressionASTNode lit => FormatExpr(lit.Literal),
            IntLiteralASTNode i => i.Value.ToString(),
            StringLiteralASTNode s => "\"" + s.Value + "\"",
            UnaryExpressionASTNode u => u.Operator + FormatExpr(u.Operand.Expression),
            SymbolReferenceASTNode sref => string.Join(".", sref.Symbol.symbol.elements.ConvertAll(el => el.name)),
            EnumCaseExpressionASTNode ec => "." + ec.CaseName,
            _ => $"<{e.GetType().Name}>"
        };

        // @ 注解渲染（SYNTAX §14.5）：@Name[(args)]，可叠加
        private static string FormatAnnotations(IWrapperAttachable node)
        {
            if (node.Annotations.Count == 0) return "";
            return string.Join(" ", node.Annotations.ConvertAll(FormatAnnotation)) + " ";
        }

        private static string FormatAnnotation(AnnotationASTNode a)
        {
            var s = "@" + string.Join(".", a.Name.symbol.elements.ConvertAll(el => el.name));
            if (a.HasArguments)
                s += "(" + string.Join(", ", a.Arguments.ConvertAll(FormatArgument)) + ")";
            return s;
        }

        private static string FormatCallable(CallableDeclarationASTNode f)
        {
            var kind = f.Kind switch
            {
                CallableKind.Operator => "operator ",
                CallableKind.Init => "",
                _ => "func "
            };
            var ps = string.Join(",", f.Parameters.Parameters.ConvertAll(FormatParam));
            var ret = f.ReturnType != null ? ": " + FormatType(f.ReturnType) : "";
            var body = f.Body != null ? " {}" : "";
            return Mods(f.Modifiers) + kind + f.Name + FormatGenerics(f.GenericParameters)
                + "(" + ps + ")" + ret + body;
        }

        private static string FormatParam(ParameterASTNode p)
        {
            var s = p.Name;
            // init 映射参数显式渲染类型（普通参数的类型渲染从简，保持既有风格）
            if (p.MappedFieldName != null && p.Type.TypeSymbol.symbol.elements.Count > 0)
                s += ": " + FormatType(p.Type);
            if (p.MappedFieldName != null)
                s += " -> " + p.MappedFieldName;
            if (p.DefaultValue != null)
                s += " = " + FormatExpr(p.DefaultValue.Expression);
            return s;
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

        // ===== 14. @ 注解（wrapper 应用，SYNTAX §14.5，P5）=====
        public static void TestAnnotations()
        {
            Console.WriteLine("=== Testing @ Annotations (Wrapper Applications) ===");

            // 编译器内建 wrapper：wrapper 类型标识（§14.2/14.3/14.4）
            TestDeclaration("@WrapperTarget(.Entity)\npub wrapper Logged {}",
                "@WrapperTarget(.Entity) pub wrapper Logged");
            TestDeclaration("@WrapperTarget(.Value)\npub wrapper Clamped {}",
                "@WrapperTarget(.Value) pub wrapper Clamped");
            // 用户 wrapper 应用：类型 / 函数 / 全局变量
            TestDeclaration("@Logged(\"DEBUG\")\npub class MyService {}",
                "@Logged(\"DEBUG\") pub class MyService");
            TestDeclaration("@Timed()\npub func heavyComputation() {}",
                "@Timed() pub func heavyComputation() {}");
            TestDeclaration("@Clamped(0, 100)\nvar health: i32 = 50",
                "@Clamped(0, 100) var health");
            // 多注解叠加（§14.5 示例形态）
            TestDeclaration("@Logged(\"DEBUG\")\n@Serializable()\npub class MyService {}",
                "@Logged(\"DEBUG\") @Serializable() pub class MyService");
            // 注解名可为路径
            TestDeclaration("@core.WrapperTarget(.Method)\npub wrapper Timed {}",
                "@core.WrapperTarget(.Method) pub wrapper Timed");

            Console.WriteLine();
        }

        // ===== 15. wrapper proxy 成员（.proxy.*，SYNTAX §14.2/§14.6，P5）=====
        public static void TestWrapperProxy()
        {
            Console.WriteLine("=== Testing Wrapper Proxy Members ===");

            // specific 方法代理（§14.2 示例形态）
            TestDeclaration(
                "pub wrapper Logged {\n" +
                "    operator .proxy.doSomething(arg: i32): String {}\n" +
                "}",
                "pub wrapper Logged {operator .proxy.doSomething(arg): String {}}");
            // specific 运算符代理 / getter 代理（带泛型）
            TestDeclaration(
                "wrapper W {\n" +
                "    operator .proxy.opr.plus(another: TTarget): TTarget {}\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {}\n" +
                "}",
                "wrapper W {operator .proxy.opr.plus(another): TTarget {}, " +
                "operator .proxy.get.name\\<TField>(value): TField {}}");
            // 四类 wildcard 共存（每类最多一个）
            TestDeclaration(
                "wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String): TReturn {}\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {}\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {}\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String): TReturn {}\n" +
                "}",
                "wrapper W {" +
                "operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol): TReturn {}, " +
                "operator .proxy.get.*\\<TValue>(symbol,value): TValue {}, " +
                "operator .proxy.set.*\\<TValue>(symbol,value) {}, " +
                "operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol): TReturn {}}");
            // value wrapper（§14.3）与 method wrapper（§14.4）的 proxy 形态
            TestDeclaration(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init(_ -> min, _ -> max)\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {}\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {}\n" +
                "}",
                "@WrapperTarget(.Value) pub wrapper Clamped {pub var min, pub var max, " +
                "pub init(_ -> min,_ -> max), operator .proxy.get\\<TValue>(value): TValue {}, " +
                "operator .proxy.set\\<TValue>(value) {}}");
            TestDeclaration(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn extends Object>(): TReturn {}\n" +
                "}",
                "@WrapperTarget(.Method) pub wrapper Timed {pub init(), " +
                "operator .proxy.call\\<TReturn extends Object>(): TReturn {}}");

            // 错误：proxy 名只能出现在 wrapper 体内
            TestError("pub class A { operator .proxy.f() {} }", "class 体内不允许 proxy 名");
            TestError("operator .proxy.f() {}", "全局函数不允许 proxy 名");
            // 错误：proxy 名首段必须是 proxy
            TestError("wrapper W { operator .foo.f() {} }", "proxy 名首段必须是 proxy");
            // 错误：同类 wildcard 重复（§14.6）
            TestError(
                "wrapper W {\n" +
                "    operator .proxy.*\\<T>(symbol: String) {}\n" +
                "    operator .proxy.*\\<T>(symbol: String) {}\n" +
                "}",
                "同类 wildcard 重复");
            // 错误：wildcard 的 * 必须收尾
            TestError("wrapper W { operator .proxy.*.f() {} }", "* 后不允许再有点");

            Console.WriteLine();
        }

        // ===== 入口 =====
        public static int RunAll()
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
            TestEnumCases();
            TestInitParameterMapping();
            TestLikeAndExtension();
            TestAnnotations();
            TestWrapperProxy();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            return failCount;
        }
    }
}
