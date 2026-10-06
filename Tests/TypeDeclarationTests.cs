using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 类型声明解析测试（P3 M13/M14/M15 统一声明层）：全管线驱动�?
    /// 断言整棵 Root �?AstDescribe 描述串；错误用例断言 ParserException 消息片段�?
    ///
    /// 覆盖�?
    /// 1. 简�?class 声明（空 body�?
    /// 2. 带修饰符�?class（pub, open, abstract, etc.�?
    /// 3. interface 声明
    /// 4. struct 声明
    /// 5. wrapper 声明
    /// 6. 全局字段与全局函数
    /// 7. 类成员（字段/方法/init/operator�?
    /// 8. 继承�?implements 列表
    /// 9. 嵌套类型（多层）
    /// 10. 声明上的泛型参数（类�?函数/operator，含约束与可变参数）
    /// 11. enum struct �?[case 列表]（固�?参数�?case、显式判别值、错误用例）
    /// 12. init 参数映射（_ -> field，含默认�?显式�?混合形态、错误用例）
    /// 13. like 委托（�?.6）与 ext 扩展成员（�?.4�?
    /// 14. @ 注解（wrapper 应用，�?4.5，P5�?
    /// 15. wrapper proxy 成员�?proxy.* specific/wildcard，�?4.2/§14.6，P5�?
    /// </summary>
    public class TypeDeclarationTests
    {
        // ===== 1. 简�?class 声明 =====
        public static void TestSimpleClass()
        {
            CompilerTestTools.Section("Simple Class Declaration");

            TestDecl("class Animal {}",
                "class Animal");

            TestDecl("class Dog {}",
                "class Dog");

            CompilerTestTools.Blank();
        }

        // ===== 2. 带修饰符�?class =====
        public static void TestClassWithModifiers()
        {
            CompilerTestTools.Section("Class with Modifiers");

            TestDecl("pub class Animal {}",
                "pub class Animal");

            TestDecl("pub open class Animal {}",
                "pub open class Animal");

            TestDecl("pub abstract class Shape {}",
                "pub abstract class Shape");

            TestDecl("pub shared class Session {}",
                "pub shared class Session");

            TestDecl("pub singleton class Config {}",
                "pub singleton class Config");

            CompilerTestTools.Blank();
        }

        // ===== 3. interface 声明 =====
        public static void TestInterface()
        {
            CompilerTestTools.Section("Interface Declaration");

            TestDecl("interface Drawable {}",
                "interface Drawable");

            TestDecl("pub interface Comparable {}",
                "pub interface Comparable");

            CompilerTestTools.Blank();
        }

        // ===== 4. struct 声明 =====
        public static void TestStruct()
        {
            CompilerTestTools.Section("Struct Declaration");

            TestDecl("struct Point {}",
                "struct Point");

            TestDecl("pub struct Vector2 {}",
                "pub struct Vector2");

            TestDecl("pub open struct BaseStruct {}",
                "pub open struct BaseStruct");

            TestDecl("pub rich struct Entry {}",
                "pub rich struct Entry");

            TestDecl("pub shared rich struct SharedEntry {}",
                "pub shared rich struct SharedEntry");

            CompilerTestTools.Blank();
        }

        // ===== 5. wrapper 声明 =====
        public static void TestWrapper()
        {
            CompilerTestTools.Section("Wrapper Declaration");

            TestDecl("wrapper Logged {}",
                "wrapper Logged");

            TestDecl("pub wrapper Cached {}",
                "pub wrapper Cached");

            CompilerTestTools.Blank();
        }

        // ===== 6. 全局字段与全局函数（复用同一�?DeclarationParserLayer�?====
        public static void TestGlobals()
        {
            CompilerTestTools.Section("Global Fields / Functions");

            TestDecl("var counter: i32", "var counter: i32");
            TestDecl("pub const MAX: i32", "pub const MAX: i32");
            TestDecl("pub func main() {}", "pub func main() {}");
            TestDecl("func add(a: i32, b: i32): i32 {}", "func add(a: i32,b: i32): i32 {}");
            TestDecl("pub static func helper()", "pub static func helper()");

            CompilerTestTools.Blank();
        }

        // ===== 7. 类成员（字段/方法/init/operator 全部复用同一路径�?====
        public static void TestMembers()
        {
            CompilerTestTools.Section("Type Members");

            TestDecl("class A { pub var name: String }",
                "class A {pub var name: String}");

            TestDecl("class A { pub func speak(): String {} }",
                "class A {pub func speak(): String {}}");

            TestDecl("class Point { pub init(x: i32, y: i32) {} }",
                "class Point {pub init(x: i32,y: i32) {}}");

            TestDecl("struct V { pub operator plus(o: V): V {} }",
                "struct V {pub operator plus(o: V): V {}}");
            TestDecl("struct F { pub operator and(o: F): F {} pub operator not(): F {} }",
                "struct F {pub operator and(o: F): F {}, pub operator not(): F {}}");

            TestDecl("interface D { func draw(c: Canvas) }",
                "interface D {func draw(c: Canvas)}");

            TestDecl("class A { pub var x: i32\n priv var y: i32\n pub func f() {} }",
                "class A {pub var x: i32, priv var y: i32, pub func f() {}}");

            CompilerTestTools.Blank();
        }

        // ===== 8. 继承与接�?=====
        public static void TestInheritance()
        {
            CompilerTestTools.Section("Inheritance / Interfaces");

            TestDecl("class Dog : Animal {}", "class Dog : Animal");
            TestDecl("class C implements Drawable {}", "class C implements Drawable");
            TestDecl("class C : Shape implements Drawable {}",
                "class C : Shape implements Drawable");
            TestDecl("class C : Shape implements A, B {}",
                "class C : Shape implements A,B");
            TestDecl("pub interface I : Base {}", "pub interface I : Base");

            CompilerTestTools.Blank();
        }

        // ===== 9. 嵌套类型（递归复用本层，与顶层同一路径�?====
        public static void TestNestedTypes()
        {
            CompilerTestTools.Section("Nested Types");

            TestDecl("class Outer { class Inner {} }",
                "class Outer {class Inner}");

            TestDecl("class Outer { pub class Inner { pub var v: i32 } }",
                "class Outer {pub class Inner {pub var v: i32}}");

            TestDecl("class Outer { struct S {} interface I {} }",
                "class Outer {struct S, interface I}");

            TestDecl("class A { class B { class C {} } }",
                "class A {class B {class C}}");

            CompilerTestTools.Blank();
        }

        // ===== 10. 声明上的泛型参数（复�?GenericParametersParserLayer，SYNTAX §3.6�?====
        public static void TestDeclarationGenericParameters()
        {
            CompilerTestTools.Section("Generic Parameters on Declarations");

            // 类型声明
            TestDecl("class Container\\<TElement> {}",
                "class Container\\<TElement>");
            TestDecl("interface Comparable\\<T> { func compareTo(other: T): i32 }",
                "interface Comparable\\<T> {func compareTo(other: T): i32}");
            TestDecl("class Producer\\<out TElement> {}",
                "class Producer\\<out TElement>");
            TestDecl("class Consumer\\<in TElement> {}",
                "class Consumer\\<in TElement>");
            TestDecl("pub struct Pair\\<TFirst, TSecond> {}",
                "pub struct Pair\\<TFirst, TSecond>");
            TestDecl("wrapper Logged\\<T> {}",
                "wrapper Logged\\<T>");

            // 泛型 + 继承 + implements（泛型列表在继承子句之前�?
            TestDecl("class MyList\\<TElement> : List implements Iterable {}",
                "class MyList\\<TElement> : List implements Iterable");

            // 函数声明（全局与成员共用一条路径）
            TestDecl("func transform\\<TInput, TResult>(input: TInput): TResult {}",
                "func transform\\<TInput, TResult>(input: TInput): TResult {}");
            TestDecl("class A { func map\\<T>(x: T): T {} }",
                "class A {func map\\<T>(x: T): T {}}");
            TestDecl("struct V { pub operator plus\\<TAnother extends Addable>(another: TAnother): V {} }",
                "struct V {pub operator plus\\<TAnother, TAnother extends Addable>(another: TAnother): V {}}");

            // 约束子句（extends/supers/with；Target 裸标识符即隐含的泛型参数，双注册�?Parameters�?
            TestDecl("func process\\<TItem extends Comparable, Serializable supers BaseType>(item: TItem): TItem {}",
                "func process\\<TItem, Serializable, TItem extends Comparable, Serializable supers BaseType>(item: TItem): TItem {}");
            TestDecl("func dump\\<TItem with Serializable>(item: TItem) {}",
                "func dump\\<TItem, TItem with Serializable>(item: TItem) {}");

            // 可变 / 具名可变泛型参数
            TestDecl("func sum\\<TArgs...>(items: TArgs...) {}",
                "func sum\\<TArgs...>(items: TArgs...) {}");
            TestDecl("pub func update\\<named TValues... with Serializable>(configs: named TValues...): bool {}",
                "pub func update\\<named TValues..., TValues with Serializable>(configs: named TValues...): bool {}");

            // 型变参数 + 约束
            TestDecl("class Cache\\<out TElement extends Comparable> {}",
                "class Cache\\<out TElement, TElement extends Comparable>");

            CompilerTestTools.Blank();
        }

        // ===== 11. enum struct �?[case 列表]（SYNTAX §12�?====
        public static void TestEnumCases()
        {
            CompilerTestTools.Section("Enum Struct Case List");

            // 固定 case（规�?Direction 示例形态）
            TestDecl(
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    priv init(degrees: i32)\n" +
                "}[\n" +
                "    North(0),\n" +
                "    South(180),\n" +
                "    East(90),\n" +
                "    West(270)\n" +
                "]",
                "pub enum struct Direction {pub const degrees: i32, priv init(degrees: i32)}" +
                "[North(Int(0,I32)), South(Int(180,I32)), East(Int(90,I32)), West(Int(270,I32))]");

            // 参数�?case：_ 参数�?+ 具名实参
            TestDecl(
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(code: i32)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]",
                "pub enum struct RequestResult {pub const errorCode: i32, pub init(code: i32)}" +
                "[Success(Int(-1,I32)), Failed(errorCode:Path(_, []))]");

            // 无实参的固定 case
            TestDecl("enum struct Color {}[Red, Green, Blue]",
                "enum struct Color[Red, Green, Blue]");

            // 显式判别值（§12.4�?
            TestDecl(
                "pub enum struct SteadyABIEnum {}[\n" +
                "    First -> 0,\n" +
                "    Second -> 2,\n" +
                "    Third -> 1\n" +
                "]",
                "pub enum struct SteadyABIEnum[First -> 0, Second -> 2, Third -> 1]");

            // 参数�?case + 显式判别�?
            TestDecl(
                "pub enum struct StableRequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(code: i32)\n" +
                "}[\n" +
                "    Success(-1) -> 0,\n" +
                "    Failed(errorCode = _) -> 1\n" +
                "]",
                "pub enum struct StableRequestResult {pub const errorCode: i32, pub init(code: i32)}" +
                "[Success(Int(-1,I32)) -> 0, Failed(errorCode:Path(_, [])) -> 1]");

            // �?case 列表（允许缺省，等价于空列表�?
            TestDecl("pub enum struct Empty {}", "pub enum struct Empty");

            // 错误用例（消息片段见 DeclarationParserLayer �?case 列表收尾校验�?
            TestError("enum struct E {}[A, A]", "Duplicate enum case name");
            TestError("enum struct E {}[A -> 0, B -> 0]", "Duplicate enum discriminant value");
            TestError("enum struct E {}[A -> 0, B]", "all have explicit discriminants");
            TestError("enum struct E {}[A -> -1]", "Expected non-negative integer as enum discriminant");

            CompilerTestTools.Blank();
        }

        // ===== 12. init 参数映射（_ -> field，SYNTAX §9.3�?====
        public static void TestInitParameterMapping()
        {
            CompilerTestTools.Section("Init Parameter Mapping");

            // 规范 §9.3 示例形态：_ 映射 / 带默认�?/ 显式参数�?
            TestDecl(
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "    pub init(_ -> x = 0, _ -> y = 0)\n" +
                "    pub init(horizontal: i32 -> x, vertical: i32 -> y)\n" +
                "}",
                "pub class Point {pub var x: i32, pub var y: i32, " +
                "pub init(_ -> x,_ -> y), " +
                "pub init(_ -> x = Int(0,I32),_ -> y = Int(0,I32)), " +
                "pub init(horizontal: i32 -> x,vertical: i32 -> y)}");

            // 混合：映射参�?+ 普通参数（带体�?
            TestDecl(
                "pub class P {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x, label: String) {}\n" +
                "}",
                "pub class P {pub var x: i32, pub init(_ -> x,label: String) {}}");

            // struct �?init 映射（�?0 Vector2 风格�?
            TestDecl(
                "pub struct Vector2 {\n" +
                "    pub var x: float\n" +
                "    pub var y: float\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}",
                "pub struct Vector2 {pub var x: float, pub var y: float, pub init(_ -> x,_ -> y)}");

            // enum struct 规范示例（�?2 Direction：case 列表 + init 映射会师�?
            TestDecl(
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    priv init(_ -> degrees)\n" +
                "}[\n" +
                "    North(0),\n" +
                "    South(180)\n" +
                "]",
                "pub enum struct Direction {pub const degrees: i32, priv init(_ -> degrees)}" +
                "[North(Int(0,I32)), South(Int(180,I32))]");

            // 错误：非 init 的形参列表不允许映射（allowMapping=false �?-> 无去处）
            TestError("func f(x: i32 -> y) {}", "after parameter type");
            TestError("class A { func g(_ -> x) {} }", "Expected ':' after parameter name");

            // 错误�?> 后缺字段�?
            TestError("class A { init(_ -> ) }", "Expected field name after '->' in init parameter mapping");

            CompilerTestTools.Blank();
        }

        // ===== 13. like 委托（�?.6）与 ext 扩展成员（�?.4�?====
        public static void TestLikeAndExtension()
        {
            CompilerTestTools.Section("like Delegation / ext Extension");

            // like 委托（规�?§9.6 示例形态）
            TestDecl(
                "pub class Apple : Fruit like pear {\n" +
                "    pub var pear: Pear = Pear()\n" +
                "}",
                "pub class Apple : Fruit like pear {pub var pear: Pear = Path(Pear(), [])}");

            // like 跟在 implements 之后 / 无基类直�?like（解析层允许，语义待查）
            TestDecl("class A implements Drawable like d {}",
                "class A implements Drawable like d");
            TestDecl("class A like x {}", "class A like x");

            // 错误：非 class 不允�?like；like 后缺字段�?
            TestError("struct S like x {}", "Only class declarations can use 'like' delegation");
            TestError("class A like {}", "Expected field name after 'like'");

            // ext 扩展函数与扩展字段（规范 §4.4 示例形态）
            TestDecl("pub ext func String.reversed(): String {}",
                "pub ext func String.reversed(): String {}");
            TestDecl(
                "pub ext var String.isEmpty: bool { get(_: _) { return (this.length == 0) } }",
                "pub ext var String.isEmpty: bool {get(_){}}");

            // 错误：限定名必须�?ext 修饰（全局函数与全局变量均拒绝）
            TestError("func String.reversed(): String {}", "Expected '(' in declaration");
            TestError("var a.b: i32", "Expected ':', '=' or line break after variable name");

            // 错误：ext 限定名的后续段必须是合法标识符（数字词拒绝）
            TestError("ext func String.123(): String {}", "Expected member name after '.'");
            TestError("pub ext var String.123: bool", "Expected member name after '.'");

            CompilerTestTools.Blank();
        }

        // ===== 14. @ 注解（wrapper 应用，SYNTAX §14.5，P5�?====
        public static void TestAnnotations()
        {
            CompilerTestTools.Section("@ Annotations (Wrapper Applications)");

            // 编译器内�?wrapper：wrapper 类型标识（�?4.2/14.3/14.4�?
            TestDecl("@WrapperTarget(.Entity)\npub wrapper Logged {}",
                "@WrapperTarget(EnumCase(.Entity)) pub wrapper Logged");
            TestDecl("@WrapperTarget(.Value)\npub wrapper Clamped {}",
                "@WrapperTarget(EnumCase(.Value)) pub wrapper Clamped");
            // 用户 wrapper 应用：类�?/ 函数 / 全局变量
            TestDecl("@Logged(\"DEBUG\")\npub class MyService {}",
                "@Logged(Str(\"DEBUG\")) pub class MyService");
            TestDecl("@Timed()\npub func heavyComputation() {}",
                "@Timed() pub func heavyComputation() {}");
            TestDecl("@Clamped(0, 100)\nvar health: i32 = 50",
                "@Clamped(Int(0,I32), Int(100,I32)) var health: i32 = Int(50,I32)");
            // 多注解叠加（§14.5 示例形态）
            TestDecl("@Logged(\"DEBUG\")\n@Serializable()\npub class MyService {}",
                "@Logged(Str(\"DEBUG\")) @Serializable() pub class MyService");
            // 注解名可为路�?
            TestDecl("@core.WrapperTarget(.Method)\npub wrapper Timed {}",
                "@core.WrapperTarget(EnumCase(.Method)) pub wrapper Timed");

            CompilerTestTools.Blank();
        }

        // ===== 14b. native 函数（SYNTAX §4.6，M43�?====
        public static void TestNativeFunction()
        {
            CompilerTestTools.Section("Native Functions");

            // 顶层 native 函数：无体声明（换行收尾�?
            TestDecl("pub native func fflush(): i32",
                "pub native func fflush(): i32");

            // 成员形态：priv static native（stdlib print 形态）
            TestDecl("class C { priv static native func print(text: String) }",
                "class C {priv static native func print(text: String)}");

            // 内建注解 + native（@NativeLibrary/@NativeSymbol 字符串实参）
            TestDecl("class C {\n@NativeLibrary(\"rigi_rt\")\n@NativeSymbol(\"print\")\npriv static native func print(text: String)\n}",
                "class C {@NativeLibrary(Str(\"rigi_rt\")) @NativeSymbol(Str(\"print\")) priv static native func print(text: String)}");

            CompilerTestTools.Blank();
        }

        // ===== 15. wrapper proxy 成员�?proxy.*，SYNTAX §14.2/§14.6，P5�?====
        public static void TestWrapperProxy()
        {
            CompilerTestTools.Section("Wrapper Proxy Members");

            // specific 方法代理（�?4.2 示例形态）
            TestDecl(
                "pub wrapper Logged {\n" +
                "    operator .proxy.doSomething(arg: i32): String {}\n" +
                "}",
                "pub wrapper Logged {operator .proxy.doSomething(arg: i32): String {}}");
            // specific 运算符代�?/ getter 代理（带泛型�?
            TestDecl(
                "wrapper W {\n" +
                "    operator .proxy.opr.plus(another: TTarget): TTarget {}\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {}\n" +
                "}",
                "wrapper W {operator .proxy.opr.plus(another: TTarget): TTarget {}, " +
                "operator .proxy.get.name\\<TField>(value: TField): TField {}}");
            // 四类 wildcard 共存（每类最多一个）
            TestDecl(
                "wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String): TReturn {}\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {}\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {}\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String): TReturn {}\n" +
                "}",
                "wrapper W {" +
                "operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String): TReturn {}, " +
                "operator .proxy.get.*\\<TValue>(symbol: String,value: TValue): TValue {}, " +
                "operator .proxy.set.*\\<TValue>(symbol: String,value: TValue) {}, " +
                "operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String): TReturn {}}");
            // value wrapper（�?4.3）与 method wrapper（�?4.4）的 proxy 形�?
            TestDecl(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init(_ -> min, _ -> max)\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {}\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {}\n" +
                "}",
                "@WrapperTarget(EnumCase(.Value)) pub wrapper Clamped {pub var min: i32, pub var max: i32, " +
                "pub init(_ -> min,_ -> max), operator .proxy.get\\<TValue>(value: TValue): TValue {}, " +
                "operator .proxy.set\\<TValue>(value: TValue) {}}");
            TestDecl(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn extends Object>(): TReturn {}\n" +
                "}",
                "@WrapperTarget(EnumCase(.Method)) pub wrapper Timed {pub init(), " +
                "operator .proxy.call\\<TReturn, TReturn extends Object>(): TReturn {}}");
            // method wrapper canonical 保留参数�?.name（�?4.4 示例形态）：前导点原样入参数名
            TestDecl(
                "wrapper W {\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}",
                "wrapper W {operator .proxy.call(.name: String,args: named Any...): Any {}}");

            // 错误：proxy 名只能出现在 wrapper 体内
            TestError("pub class A { operator .proxy.f() {} }", "Expected declaration name");
            TestError("operator .proxy.f() {}", "Expected declaration name");
            // 错误：proxy 名首段必须是 proxy
            TestError("wrapper W { operator .foo.f() {} }", "must start with '.proxy.'");
            // 错误：同�?wildcard 重复（�?4.6�?
            TestError(
                "wrapper W {\n" +
                "    operator .proxy.*\\<T>(symbol: String) {}\n" +
                "    operator .proxy.*\\<T>(symbol: String) {}\n" +
                "}",
                "Duplicate wildcard proxy of the same category");
            // 错误：wildcard �?* 必须收尾�? 后再遇点按「期�?(」拒绝）
            TestError("wrapper W { operator .proxy.*.f() {} }", "Expected '(' in declaration");

            CompilerTestTools.Blank();
        }

        // ===== 辅助 =====

        // 辅助：全管线解析并比对整�?Root �?AST 描述�?
        private static void TestDecl(string code, string expectedDesc)
        {
            try
            {
                var root = CompilerTestTools.ParseRoot(code);
                CaseAssertions.Check(Label(code), AstDescribe.Root(root), expectedDesc);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"{Label(code)} => 意外异常", false, ex.Message);
            }
        }

        // 辅助：期望解析失败（ParserException/LexerException，消息含片段�?
        private static void TestError(string code, string expectedMessagePart)
        {
            CaseAssertions.CheckParseError(Label(code), () => CompilerTestTools.ParseRoot(code), expectedMessagePart);
        }

        // 多行源码�?label 转义显示
        private static string Label(string code) => code.Replace("\n", "\\n");

        // ===== 入口 =====


        internal static TestSuiteData Spec { get; } = new("TypeDeclaration",
        [
            (nameof(TestSimpleClass), TestSimpleClass),
            (nameof(TestClassWithModifiers), TestClassWithModifiers),
            (nameof(TestInterface), TestInterface),
            (nameof(TestStruct), TestStruct),
            (nameof(TestWrapper), TestWrapper),
            (nameof(TestGlobals), TestGlobals),
            (nameof(TestMembers), TestMembers),
            (nameof(TestInheritance), TestInheritance),
            (nameof(TestNestedTypes), TestNestedTypes),
            (nameof(TestDeclarationGenericParameters), TestDeclarationGenericParameters),
            (nameof(TestEnumCases), TestEnumCases),
            (nameof(TestInitParameterMapping), TestInitParameterMapping),
            (nameof(TestLikeAndExtension), TestLikeAndExtension),
            (nameof(TestAnnotations), TestAnnotations),
            (nameof(TestNativeFunction), TestNativeFunction),
            (nameof(TestWrapperProxy), TestWrapperProxy),
        ], sectionTitle: "TypeDeclaration");
    }
}
