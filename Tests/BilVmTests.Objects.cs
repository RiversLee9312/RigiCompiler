using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // Objects 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestClassInstanceFields()
        {
            var result = Run(
                "pub class Box {\n" +
                "    pub var n: i32\n" +
                "    pub init(v: i32) { n = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box(7)\n" +
                "    b.n = b.n + 1\n" +
                "    return b.n\n" +
                "}\n");
            CheckOk("实例字段", result);
            CheckI32("7+1", result, 8);
        }

        private static void TestFieldZeroDefault()
        {
            // P18/S2（§9.3 DA）：非空字段必须有声明初始值或 init 赋值——
            // 声明初始值即落地值（VM 零填充仅为 BIL 层分配细节，前端不再
            // 产生「无初始值裸字段」形态）
            var result = Run(
                "pub class Box { pub var n: i32 = 0 }\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    return b.n\n" +
                "}\n");
            CheckOk("字段零值", result);
            CheckI32("声明初始值 0 落地", result, 0);
        }

        // SYNTAX §9.3 回归：默认构造应用声明处字段初始化器（含泛型类）
        //（历史 bug：初始化器被丢弃，字段读出零值）
        private static void TestDefaultConstructorFieldInitializer()
        {
            var result = Run(
                "pub class Plain {\n" +
                "    pub var count: i32 = 41\n" +
                "    pub var other: i32 = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const p = new Plain()\n" +
                "    return (p.count * 100) + p.other\n" +
                "}\n");
            CheckOk("默认构造字段初始化器", result);
            CheckI32("带初始化器取初始化器", result, 4100);
            var generic = Run(
                "class Box\\<T> {\n" +
                "    pub var item: i32 = 5\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>()\n" +
                "    return b.item\n" +
                "}\n");
            CheckOk("泛型类默认构造字段初始化器", generic);
            CheckI32("new Box<i32>().item = 5", generic, 5);
        }

        // SYNTAX §9.3 回归：显式 init（无体 / 空体）同样应用声明处
        // 实例字段初始化器（历史 bug：仅默认构造合成路径拼初始化器）
        private static void TestExplicitInitFieldInitializer()
        {
            var plain = Run(
                "pub class Plain {\n" +
                "    pub var n: i32 = 100\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Plain().n\n" +
                "}\n");
            CheckOk("无体显式 init 字段初始化器", plain);
            CheckI32("new Plain().n = 100", plain, 100);

            var withProp = Run(
                "pub class WithProp {\n" +
                "    pub var n: i32 {\n" +
                "        pub get\n" +
                "        pub set\n" +
                "    } = 100\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new WithProp().n\n" +
                "}\n");
            CheckOk("自动访问器 + 显式 init 初始化器", withProp);
            CheckI32("new WithProp().n = 100", withProp, 100);

            var withBacking = Run(
                "pub class WithBackingProp {\n" +
                "    pub var n: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { }\n" +
                "    } = 100\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new WithBackingProp().n\n" +
                "}\n");
            CheckOk("backing 访问器 + 显式 init 初始化器", withBacking);
            CheckI32("new WithBackingProp().n = 100", withBacking, 100);

            var multi = Run(
                "pub class Multi {\n" +
                "    pub var a: i32 = 1\n" +
                "    pub var b: i32 = 2\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m = new Multi()\n" +
                "    return m.a + m.b\n" +
                "}\n");
            CheckOk("空体显式 init 多字段初始化器", multi);
            CheckI32("a+b=3", multi, 3);

            var clamped = Run(
                "pub class Meter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { if (value > 100) { value = 100 } }\n" +
                "    } = 150\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Meter().value\n" +
                "}\n");
            CheckOk("显式 init 初始化器经 setter 钳制", clamped);
            CheckI32("setter 钳制 150→100", clamped, 100);

            var mapped = Run(
                "pub class M {\n" +
                "    pub var x: i32 = 10\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> y)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m = new M(7)\n" +
                "    return (m.x * 100) + m.y\n" +
                "}\n");
            CheckOk("显式 init 初始化器 + 参数映射", mapped);
            CheckI32("x=10 y=7", mapped, 1007);
        }

        // SYNTAX §9.4.1 回归：带自定义访问器的实例字段，声明处初始化器
        // 经 setter 应用（BIL set.field 本就强制走 setter——backing 钳制
        // 自初始化起生效；计算形态同经 setter）
        private static void TestAccessorFieldInitializerViaSetter()
        {
            var backing = Run(
                "pub class Meter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { if (value > 100) { value = 100 } }\n" +
                "    } = 150\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Meter().value\n" +
                "}\n");
            CheckOk("backing 访问器初始化器经 setter", backing);
            CheckI32("setter 钳制 150→100", backing, 100);

            var computed = Run(
                "pub class Therm {\n" +
                // P18/S2（§9.3 DA）：底层字段给哨兵初始值（声明序先于
                // display 的 setter 写入，终值不变）
                "    pub var celsius: i32 = 0\n" +
                "    pub var display: i32 {\n" +
                "        pub get(_: _) { return celsius }\n" +
                "        pub set(_: _) { celsius = value }\n" +
                "    } = 33\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Therm().celsius\n" +
                "}\n");
            CheckOk("计算访问器初始化器经 setter", computed);
            CheckI32("计算形态 setter 写底层字段", computed, 33);
        }

        // SYNTAX §9.3 回归：默认构造链式——隐式/合成零参构造先完成基类
        // 初始化再应用本类字段初始化器（A→B→C 逐环；无本类初始化器的
        // 派生类同样合成；泛型基类 super 照 extends 代入转发）
        private static void TestDefaultConstructorChaining()
        {
            var chain = Run(
                "pub open class A { pub var x: i32 = 41 }\n" +
                "pub open class B : A { pub var y: i32 = 7 }\n" +
                "pub class C : B { pub var z: i32 = 9 }\n" +
                "pub func main(): i32 {\n" +
                "    const c = new C()\n" +
                "    return ((c.x * 100) + (c.y * 10)) + c.z\n" +
                "}\n");
            CheckOk("A→B→C 链式默认构造", chain);
            CheckI32("x=41 y=7 z=9 全部生效", chain, 4179);

            var derivedOnly = Run(
                "pub open class B { pub var y: i32 = 7 }\n" +
                "pub class C : B { }\n" +
                "pub func main(): i32 {\n" +
                "    return new C().y\n" +
                "}\n");
            CheckOk("无本类初始化器的派生合成", derivedOnly);
            CheckI32("基类初始化经合成 super 生效", derivedOnly, 7);

            var generic = Run(
                "pub open class B\\<T> { pub var v: i32 = 41 }\n" +
                "pub class C\\<T> : B\\<T> { pub var w: i32 = 7 }\n" +
                "pub func main(): i32 {\n" +
                "    const c = new C\\<i32>()\n" +
                "    return (c.v * 100) + c.w\n" +
                "}\n");
            CheckOk("泛型基类链式默认构造", generic);
            CheckI32("泛型基类 super 照 extends 代入", generic, 4107);
        }

        // 返回保证分析透视语句位置 seq 块（含嵌套；裸 return 直达外层
        // 函数，BIL §9.4 call blk 终止判定同口径透视）
        private static void TestSeqStatementReturnTransparency()
        {
            var single = Run(
                "pub func main(): i32 {\n" +
                "    seq { return 7 }\n" +
                "}\n");
            CheckOk("seq 末位 return 透视", single);
            CheckI32("seq { return 7 }", single, 7);

            var nested = Run(
                "pub func main(): i32 {\n" +
                "    seq { seq { return 7 } }\n" +
                "}\n");
            CheckOk("嵌套 seq return 透视", nested);
            CheckI32("seq { seq { return 7 } }", nested, 7);
        }

        private static void TestStaticFields()
        {
            var result = Run(
                "pub class Counter { pub static var value: i32 }\n" +
                "pub func main(): i32 {\n" +
                "    Counter.value = 42\n" +
                "    return Counter.value\n" +
                "}\n");
            CheckOk("静态字段", result);
            CheckI32("静态字段 42", result, 42);
        }

        // 回归（历史 bug：根命名空间全局字段 P4 报 "has no owner to
        // project"）：无 namespace 声明文件的顶层 var 读写端到端——宿主
        // 投影为空形态 type()，验证器归一放行，VM 按字段符号寻址
        private static void TestRootNamespaceGlobalField()
        {
            var result = Run(
                "var counter: i32 = 0\n" +
                "pub func main(): i32 {\n" +
                "    counter = (counter + 1)\n" +
                "    counter = (counter + 1)\n" +
                "    return counter\n" +
                "}\n");
            CheckOk("根命名空间全局字段", result);
            CheckI32("两次自增后读出 2", result, 2);
        }

        // like 委托（SYNTAX §9.6）端到端：委托字段类型提供同签名实现的
        // 接口成员由合成转发方法承担；显式实现优先于委托；接口类型接收者
        // 虚调用同样命中（含显式与转发两路）
        private static void TestLikeDelegationForwarding()
        {
            var result = Run(
                "import core.io.Console\n" +
                "pub interface Fruit {\n" +
                "    func taste(): String\n" +
                "    func color(): String\n" +
                "}\n" +
                "pub class Pear implements Fruit {\n" +
                "    pub override func taste(): String { return \"pear-ish\" }\n" +
                "    pub override func color(): String { return \"green\" }\n" +
                "}\n" +
                "pub class Apple implements Fruit like pear {\n" +
                "    pub var pear: Pear = new Pear()\n" +
                "    pub override func taste(): String { return \"apple-ish\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const a = new Apple()\n" +
                "    Console.println(a.taste())\n" +
                "    Console.println(a.color())\n" +
                "    const f: Fruit = a\n" +
                "    Console.println(f.taste())\n" +
                "    Console.println(f.color())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("like 委托转发", result);
            CaseAssertions.Check("显式优先 + 委托转发 + 接口虚调用",
                result.Stdout, "apple-ish\ngreen\napple-ish\ngreen\n");
        }

        // bug O3：like 目标字段为接口类型——转发体调接口方法，运行时对
        // 字段值虚派发。含：基本委托运行正确、显式 override 优先于转发、
        // 字段接口默认方法（HasBody）作委托目标
        private static void TestLikeDelegationInterfaceField()
        {
            var result = Run(
                "import core.io.Console\n" +
                "pub interface Work { func run(x: i32): i32\n }\n" +
                "pub class Impl implements Work {\n" +
                "    pub override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub class ViaIface implements Work like sink {\n" +
                "    pub var sink: Work = new Impl()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new ViaIface()\n" +
                "    Console.println(b.run(3).toString())\n" +
                "    const w: Work = b\n" +
                "    Console.println(w.run(4).toString())\n" +
                "    return b.run(3)\n" +
                "}\n");
            CheckOk("接口字段 like 委托转发", result);
            CaseAssertions.Check("接口字段委托运行输出",
                result.Stdout, "4\n5\n");
            CheckI32("接口字段委托返回值", result, 4);

            // 显式 override 优先于接口字段 like 转发
            var explicitFirst = Run(
                "import core.io.Console\n" +
                "pub interface Work {\n" +
                "    func run(x: i32): i32\n" +
                "    func tag(): String\n" +
                "}\n" +
                "pub class Impl implements Work {\n" +
                "    pub override func run(x: i32): i32 { return (x + 1) }\n" +
                "    pub override func tag(): String { return \"impl\" }\n" +
                "}\n" +
                "pub class ViaIface implements Work like sink {\n" +
                "    pub var sink: Work = new Impl()\n" +
                "    pub override func run(x: i32): i32 { return (x - 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new ViaIface()\n" +
                "    Console.println(b.run(10).toString())\n" +
                "    Console.println(b.tag())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("显式 override 优先于接口字段转发", explicitFirst);
            CaseAssertions.Check("显式优先输出",
                explicitFirst.Stdout, "9\nimpl\n");

            // 字段接口的默认方法（HasBody）作委托目标：虚派发到默认实现
            var defaultMethod = Run(
                "import core.io.Console\n" +
                "pub interface Sink {\n" +
                "    func greet(): String { return \"hi\" }\n" +
                "}\n" +
                "pub interface Greeter { func greet(): String\n }\n" +
                "pub class Impl implements Sink { }\n" +
                "pub class ViaDefault implements Greeter like sink {\n" +
                "    pub var sink: Sink = new Impl()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const v = new ViaDefault()\n" +
                "    Console.println(v.greet())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("接口默认方法作委托目标", defaultMethod);
            CaseAssertions.Check("默认方法转发输出", defaultMethod.Stdout, "hi\n");
        }

    }
}
