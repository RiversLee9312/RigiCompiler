using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    // init 运行期匹配：编译期 cast 到声明类型后按静态类型 TypesEqual
    // 验证目标（不是按运行期 typeid 可赋值性再 ranking）；无关类型拒绝。
    public static partial class BilVmDispatchTests
    {
        // 4699a58 / W4：子类实参静态 new——编译器 cast 到声明类型后严格匹配
        private static void TestInitOverloadAssignability()
        {
            var result = Run(
                "pub open class Animal { pub var n: i32 = 0 }\n" +
                "pub class Dog : Animal { }\n" +
                "pub class Cat : Animal { }\n" +
                "pub class Box {\n" +
                "    pub var tag: i32\n" +
                "    pub init(a: Animal) { tag = 1 }\n" +
                "    pub init() { tag = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const d = new Dog()\n" +
                "    const b = new Box(d)\n" +
                "    const empty = new Box()\n" +
                "    return ((b.tag * 10) + empty.tag)\n" +
                "}\n");
            CheckOk("init 重载：子类实参命中基类形参，零参另一重载仍在", result);
            CheckI32("tag 1 与 0", result, 10);
        }

        private static void TestInitRejectsUnrelatedType()
        {
            var result = Run(
                "pub class Account { pub var id: i32 = 0 }\n" +
                "pub class Other { pub var id: i32 = 0 }\n" +
                "pub class Node { pub init(a: Account) { } }\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(new Node(new Account()))\n" +
                "    var o = new Other()\n" +
                "    var n = new t(o)\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("动态 new 无关类型不得命中 Account init",
                result.Exception != null,
                result.Exception?.ToString() ?? "未抛异常");
        }

        // 新 init 原则（§9.3/§9.7 修订）：子类 init 不调 super() 时基类
        // 用户 init 体不跑，但基类字段初始值已由实际类型的
        // ..init.wrapper（..init.field.*）缝合——先于任何 init 体
        private static void TestNoSuperBaseFieldInitializersRun()
        {
            var result = Run(
                "pub open class A {\n" +
                "    pub var a1: i32 = 11\n" +
                "    pub var a2: i32 = 22\n" +
                "    pub init() { a2 = (a2 + 100) }\n" +
                "}\n" +
                "pub class B : A {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new B()\n" +
                "    return ((b.a1 * 1000) + b.a2)\n" +
                "}\n");
            CheckOk("不调 super 的子类构造", result);
            CheckI32("基类字段初值已跑（a1=11 a2=22，A.init 体未跑）",
                result, 11022);
        }

        // 字段初值早于基类 init 体：基类 init 读到本类声明初值
        private static void TestBaseInitSeesFieldInitializers()
        {
            var result = Run(
                "pub open class A {\n" +
                "    pub var a1: i32 = 11\n" +
                "    pub var seen: i32 = 0\n" +
                "    pub init() { seen = a1 }\n" +
                "}\n" +
                "pub class B : A {\n" +
                "    pub init() { super() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new B()\n" +
                "    return b.seen\n" +
                "}\n");
            CheckOk("super 链构造", result);
            CheckI32("基类 init 读到字段初值 11（不再是 0）", result, 11);
        }

        // bug O6 / W4：super(...) 实参是形参声明类型的子类——前端生成到
        // 形参类型的 cast（§9.2.2），VM 按 cast 后静态类型 TypesEqual 验证
        private static void TestSuperInitSubtypeArgument()
        {
            var result = Run(
                "pub open class Node {\n" +
                "    pub var tag: i32 = 0\n" +
                "    pub init(n: Node) { tag = (n.tag + 1) }\n" +
                "    pub init(seed: i32) { tag = seed }\n" +
                "}\n" +
                "pub class Leaf : Node {\n" +
                "    pub init(prev: Leaf) { super(prev) }\n" +
                "    pub init(seed: i32) { super(seed) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new Leaf(new Leaf(5))\n" +
                "    return b.tag\n" +
                "}\n");
            CheckOk("super 子类实参不再中止", result);
            CheckI32("Node.init(n) 跑：tag=5+1=6", result, 6);
        }

        // W4：init(Any) 声明在 init(Node) 之前——编译期选更具体的 Node，
        // 实参 cast 到 Node 后 VM TypesEqual 只命中 Node，不再按声明序
        // 漂移到 Any。
        private static void TestSuperInitStrictMatchIgnoresDeclarationOrder()
        {
            var result = Run(
                "pub open class Node { pub init() {} }\n" +
                "pub class Leaf : Node { pub init() {} }\n" +
                "pub open class Box {\n" +
                "    pub var tag: i32 = 0\n" +
                "    pub init(a: Any) { tag = 1 }\n" +
                "    pub init(n: Node) { tag = 2 }\n" +
                "}\n" +
                "pub class Child : Box {\n" +
                "    pub init(x: Leaf) { super(x) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const c = new Child(new Leaf())\n" +
                "    return c.tag\n" +
                "}\n");
            CheckOk("super 多可赋值重载不按声明序漂移", result);
            CheckI32("编译期选 init(Node)，tag=2", result, 2);
        }

        private static void TestNewInitStrictMatchIgnoresDeclarationOrder()
        {
            var result = Run(
                "pub open class Node { pub init() {} }\n" +
                "pub class Leaf : Node { pub init() {} }\n" +
                "pub class Box {\n" +
                "    pub var tag: i32 = 0\n" +
                "    pub init(a: Any) { tag = 1 }\n" +
                "    pub init(n: Node) { tag = 2 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new Box(new Leaf())\n" +
                "    return b.tag\n" +
                "}\n");
            CheckOk("new 多可赋值重载不按声明序漂移", result);
            CheckI32("编译期选 init(Node)，tag=2", result, 2);
        }

        // 显式 cast 到 Any：编译期选 init(Any)，VM 严格匹配 Any 而非 Node
        private static void TestSuperInitExplicitCastToAny()
        {
            var result = Run(
                "pub open class Node { pub init() {} }\n" +
                "pub class Leaf : Node { pub init() {} }\n" +
                "pub open class Box {\n" +
                "    pub var tag: i32 = 0\n" +
                "    pub init(a: Any) { tag = 1 }\n" +
                "    pub init(n: Node) { tag = 2 }\n" +
                "}\n" +
                "pub class Child : Box {\n" +
                "    pub init(x: Leaf) { super(x as Any) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const c = new Child(new Leaf())\n" +
                "    return c.tag\n" +
                "}\n");
            CheckOk("super 显式 cast 到 Any 命中 init(Any)", result);
            CheckI32("编译期选 init(Any)，tag=1", result, 1);
        }

        // 字段 override（§9.2.1 字段覆写）：open 字段 + 子类 override
        // 不同初值——存储仍是基类槽，虚派发选中最高派生 ..init.field
        private static void TestFieldOverrideInitialValue()
        {
            var result = Run(
                "pub open class Base {\n" +
                "    pub open var hp: i32 = 10\n" +
                "    pub init()\n" +
                "}\n" +
                "pub open class Hero : Base {\n" +
                "    pub override var hp: i32 = 99\n" +
                "    pub init()\n" +
                "}\n" +
                "pub class Villain : Hero {\n" +
                "    pub override var hp: i32 = -7\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new Base()\n" +
                "    const h = new Hero()\n" +
                "    const v = new Villain()\n" +
                "    const viaBase: Base = v\n" +
                "    return ((((b.hp * 1000) + (h.hp * 10)) + -v.hp) * 1000) + viaBase.hp\n" +
                "}\n");
            CheckOk("字段 override 链构造", result);
            CheckI32("base=10 hero=99 villain=-7（基类槽读回 -7）",
                result, ((10000 + 990 + 7) * 1000) - 7);
        }

        // 属性初值走 setter（§9.3 新原则 6）：..init.field.* 内 set.field
        // 经 setter 应用；init(_ -> x) 参数映射覆盖初值（原则 7）
        private static void TestPropertyInitializerGoesThroughSetter()
        {
            var result = Run(
                "pub class Meter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { value = (value * 2) }\n" +
                "    } = 21\n" +
                "    pub init()\n" +
                "}\n" +
                "pub class Box {\n" +
                "    pub var n: i32 = 5\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m = new Meter()\n" +
                "    const b = new Box(9)\n" +
                "    return ((m.value * 100) + b.n)\n" +
                "}\n");
            CheckOk("属性 setter 初值 + 映射覆盖", result);
            CheckI32("21*2=42（setter 生效）；映射 9 覆盖初值 5", result, 4209);
        }

        // N1：顶层全局字段（var/const）与静态字段的声明初始值在 main
        // 前执行（..globals.init）
        private static void TestGlobalFieldInitializersRun()
        {
            var result = Run(
                "var g: i32 = 42\n" +
                "const cg: i32 = 1\n" +
                "pub class Holder {\n" +
                "    pub static var s: i32 = 100\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return (((g * 100) + (cg * 10)) + (Holder.s - 100))\n" +
                "}\n");
            CheckOk("全局/静态字段初值执行", result);
            CheckI32("g=42 cg=1 s=100", result, 4210);
        }
    }
}
