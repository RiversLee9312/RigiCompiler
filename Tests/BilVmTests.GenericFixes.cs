using System.Linq;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    // VM / 具化泛型修复：泛型类索引、类级 typeid 帧、T() 零值、
    // 内部类可空 init、init 经编译期 cast 后按静态类型严格匹配。
    // S1/g9：嵌套 struct place 写穿（字段链/复合赋值/值类型 receiver
    // 方法调用写回/class 内嵌 struct/三层混合边界/静态字段根）。
    public static partial class BilVmTests
    {
        private static void TestGenericIndexOperator()
        {
            var result = Run(
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub operator getAtIndex(index: i32): T? { return item }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>(7)\n" +
                "    var nested = new Box\\<Box\\<i32>>(b)\n" +
                "    const row = nested[0]\n" +
                "    if (row != null) {\n" +
                "        return (((row[0] if? 0) + (b[0] if? 0)))\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("泛型类 getAtIndex（含 Box<Box<i32>>）", result);
            CheckI32("7+7", result, 14);
        }

        private static void TestClassGenericParamInMethodFrame()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub class Repo\\<TItem> {\n" +
                "    pub func make(cap: i32): Array\\<TItem> {\n" +
                "        return arrayOf\\<TItem>(cap)\n" +
                "    }\n" +
                "    pub func mix\\<U>(x: TItem, y: U): U { return y }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const r = new Repo\\<i32>()\n" +
                "    const a = r.make(3)\n" +
                "    a[0] = 7\n" +
                "    return (((a[0] if? 0) + r.mix\\<i32>(1, 35)))\n" +
                "}\n");
            CheckOk("类泛型参数在方法体可用（arrayOf<TItem> + 方法自有 U）", result);
            CheckI32("7+35", result, 42);
        }

        private static void TestReifiedConstructZeroValue()
        {
            // g6（§3.7）：T() 零参按约束界编译期判定——标量界例外得零值
            var i32 = Run(
                "pub func make\\<T extends i32>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    const x = make\\<i32>()\n" +
                "    core.io.Console.println(\"${x}\")\n" +
                "    return (x + 0)\n" +
                "}\n");
            CheckOk("T() 对 i32 产出零值", i32);
            TestHarness.Check("i32 零值打印 0", i32.Stdout, "0\n");
            CheckI32("0+0", i32, 0);

            var flag = Run(
                "pub func make\\<T extends bool>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    const x = make\\<bool>()\n" +
                "    if (x) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("T() 对 bool 产出 false", flag);
            CheckI32("false 走 else", flag, 0);

            var text = Run(
                "pub func make\\<T extends String>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    const s = make\\<String>()\n" +
                "    core.io.Console.println(\"${s}\")\n" +
                "    if ((s == \"\")) { return 0 }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("T() 对 String 产出空串", text);
            TestHarness.Check("空串打印空行", text.Stdout, "\n");
            CheckI32("空串 length=0", text, 0);

            // g6：class 界有可访问零参 init → 放行，运行期按 typeid 跑 init
            var point = Run(
                "pub class Point {\n" +
                "    pub var x: i32 = 1\n" +
                "    pub var y: i32 = 2\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func make\\<T extends Point>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    const p = make\\<Point>()\n" +
                "    return (p.x + p.y)\n" +
                "}\n");
            CheckOk("T() 对有零参 init 的 class 界放行", point);
            CheckI32("Point 初值 1+2", point, 3);
        }

        // MW8c-3 E：动态 new 无匹配 init（含零参）必须抛 NoSuchMethodException
        private static void TestDynamicNewMustThrowNoMatchingInit()
        {
            var zeroArg = Run(
                "pub class OnlyI32 {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(OnlyI32)\n" +
                "    var o = new t()\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("零参无匹配 init 抛异常",
                zeroArg.Exception != null, zeroArg.Exception?.ToString() ?? "未抛");
            TestHarness.CheckTrue("零参无匹配 init 消息",
                zeroArg.Exception != null
                && zeroArg.Exception.Message.Contains("不匹配任何 init"),
                zeroArg.Exception?.Message ?? "");

            var structDyn = Run(
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Vec)\n" +
                "    var s = new t(7)\n" +
                "    return s.x\n" +
                "}\n");
            CheckOk("动态 new struct 目标", structDyn);
            CheckI32("struct 字段落位", structDyn, 7);
        }

        private static void TestNestedClassNullableInit()
        {
            var empty = Run(
                "pub class List\\<T> {\n" +
                "    pub class Node {\n" +
                "        pub var value: T\n" +
                "        pub var next: Node?\n" +
                "        pub init(_ -> value) { next = null }\n" +
                "    }\n" +
                "    pub class Iter {\n" +
                "        pub var n: Node?\n" +
                "        pub init(head: Node?) { n = head }\n" +
                "    }\n" +
                "    pub var head: Node?\n" +
                "    pub init() { head = null }\n" +
                "    pub func make(): Iter { return new Iter(head) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new List\\<i32>()\n" +
                "    var it = b.make()\n" +
                "    if (it.n == null) { return 0 }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("内部类可空实参构造（空链表）", empty);
            CheckI32("head 为 null 时 Iter init 命中", empty, 0);

            var linked = Run(
                "pub class List\\<T> {\n" +
                "    pub class Node {\n" +
                "        pub var value: T\n" +
                "        pub var next: Node?\n" +
                "        pub init(_ -> value) { next = null }\n" +
                "    }\n" +
                "    pub class Iter {\n" +
                "        pub var n: Node?\n" +
                "        pub init(head: Node?) { n = head }\n" +
                "    }\n" +
                "    pub var head: Node?\n" +
                "    pub init() { head = null }\n" +
                "    pub func push(v: T) { head = new Node(v) }\n" +
                "    pub func make(): Iter { return new Iter(head) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new List\\<i32>()\n" +
                "    b.push(9)\n" +
                "    var it = b.make()\n" +
                "    var cur = it.n\n" +
                "    if (cur == null) { return 0 }\n" +
                "    return 9\n" +
                "}\n");
            CheckOk("内部类非空 Node? 实参构造", linked);
            CheckI32("非空 head 透传 value", linked, 9);
        }

        // 4699a58 / W4：Savings → Account、null → Account? 由编译期 cast 达成严格匹配
        private static void TestInitMatchByAssignability()
        {
            var result = Run(
                "pub open class Account { pub var id: i32 = 0 }\n" +
                "pub class Savings : Account { }\n" +
                "pub class Node { pub init(a: Account) { } }\n" +
                "pub func main(): i32 {\n" +
                "    const s = new Savings()\n" +
                "    const n = new Node(s)\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("init 严格匹配（Savings 经 cast → Account）", result);
            CheckI32("构造成功", result, 0);

            var nullable = Run(
                "pub open class Account { pub var id: i32 = 0 }\n" +
                "pub class Savings : Account { }\n" +
                "pub class Holder {\n" +
                "    pub var a: Account?\n" +
                "    pub init(x: Account?) { a = x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const h1 = new Holder(null)\n" +
                "    const h2 = new Holder(new Savings())\n" +
                "    if (h1.a == null) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("init 可空实参 + 子类实参", nullable);
            CheckI32("null 命中 Account? 且 Savings 也可赋", nullable, 1);
        }

        // W2：方法级泛型工厂（BoxFactory.wrap/zeroOf）替代构造类型静态成员
        private static void TestConstructedTypeStaticMembers()
        {
            var result = Run(
                "import core.io.Console\n" +
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub static func count(): i32 { return 7 }\n" +
                "}\n" +
                "pub class BoxFactory {\n" +
                "    pub static func wrap\\<T>(x: T): Box\\<T> { return new Box\\<T>(x) }\n" +
                "    pub static func zeroOf\\<T extends i32>(): Box\\<T> { return new Box\\<T>(T()) }\n" +
                "    pub static func pick\\<U>(u: U): U { return u }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = BoxFactory.wrap\\<i32>(8)\n" +
                "    const z = BoxFactory.zeroOf\\<i32>()\n" +
                "    const p = BoxFactory.pick\\<String>(\"hi\")\n" +
                "    Console.println(\"${Box.count()}:${b.v}:${z.v}:${p}\")\n" +
                "    return b.v\n" +
                "}\n");
            CheckOk("W2：BoxFactory 方法级泛型 + 裸名 count", result);
            TestHarness.Check("W2 stdout 为 7:8:0:hi", result.Stdout, "7:8:0:hi\n");
            CheckI32("W2 返回 wrap 的 v", result, 8);
        }

        // Holder\<i32?> 必须同解为 Holder<Nullable<i32>>（此前类型注解路径
        // 把 ? 误挂到外层 Holder 上，报 Expected '=' or line break）
        private static void TestNullableGenericTypeArgument()
        {
            var bugG1 = Run(
                "import core.io.Console\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h: Holder\\<i32?> = new Holder\\<i32?>(null)\n" +
                "    Console.println(\"${(h.v if? -1)}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("bug_g1：Holder\\<i32?> 内嵌可空实参", bugG1);
            TestHarness.Check("bug_g1 stdout 为 -1", bugG1.Stdout, "-1\n");
            CheckI32("bug_g1 main 返回 0", bugG1, 0);

            // 内层可空 + 外层可空：Holder\<i32?>?（可空后缀各归其主）
            var outerNullable = Run(
                "pub class Holder\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h: Holder\\<i32?>? = new Holder\\<i32?>(7)\n" +
                "    if (h == null) { return 0 }\n" +
                "    return ((h.v if? -1))\n" +
                "}\n");
            CheckOk("Holder\\<i32?>? 内外双可空", outerNullable);
            CheckI32("非空外层取内层值 7", outerNullable, 7);

            // 三层嵌套混合可空：Box\<Box\<i32?>?>——内层实参可空、
            // 中层整体作实参再带可空，各层后缀互不串扰
            var nested = Run(
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var innerBox: Box\\<i32?> = new Box\\<i32?>(null)\n" +
                "    var outerBox: Box\\<Box\\<i32?>?> = new Box\\<Box\\<i32?>?>(innerBox)\n" +
                "    var mid = outerBox.v\n" +
                "    if (mid == null) { return 0 }\n" +
                "    return ((mid.v if? -1))\n" +
                "}\n");
            CheckOk("Box\\<Box\\<i32?>?> 三层混合可空", nested);
            CheckI32("mid 非空、mid.v 为 null 取 -1", nested, -1);

            // 多实参混合可空：Pair\<i32?, String?>（g1 前第二个 ? 同样被吞）
            var pair = Run(
                "pub class Pair\\<A, B> {\n" +
                "    pub var first: A\n" +
                "    pub var second: B\n" +
                "    pub init(a: A, b: B) { first = a\n" +
                "        second = b }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p: Pair\\<i32?, String?> = new Pair\\<i32?, String?>(null, \"x\")\n" +
                "    if (p.second == null) { return 0 }\n" +
                "    return ((p.first if? -1))\n" +
                "}\n");
            CheckOk("Pair\\<i32?, String?> 多实参混合可空", pair);
            CheckI32("first 为 null 取 -1", pair, -1);
        }

        // g8/g10：泛型参数可空端到端——T → T? 装箱视图返回（g8）；
        // if?/?. 认 Nullable<T>（T 为型参，g10），unwrap\<i32>(null, -1) 走
        // .nullable<.generic<$.generic.T>> 的 null 比较与 unwrap cast
        private static void TestGenericNullableEndToEnd()
        {
            var g8 = Run(
                "import core.io.Console\n" +
                "pub func wrapNull\\<T>(x: T): T? { return x }\n" +
                "pub func main(): i32 {\n" +
                "    const v = wrapNull\\<i32>(3)\n" +
                "    Console.println(\"${(v if? 0)}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("bug_g8：T → T? return 装箱视图", g8);
            TestHarness.Check("bug_g8 stdout 为 3", g8.Stdout, "3\n");
            CheckI32("bug_g8 main 返回 0", g8, 0);

            var g10 = Run(
                "import core.io.Console\n" +
                "pub func unwrap\\<T>(x: T?, fallback: T): T { return (x if? fallback) }\n" +
                "pub func nameOf\\<T>(x: T?): String? { return x?.toString() }\n" +
                "pub func main(): i32 {\n" +
                "    const a = unwrap\\<i32>(null, -1)\n" +
                "    Console.println(\"a=${a}\")\n" +
                "    Console.println((nameOf\\<i32>(7) if? \"null\"))\n" +
                "    Console.println((nameOf\\<i32>(null) if? \"null\"))\n" +
                "    return a\n" +
                "}\n");
            CheckOk("bug_g10：if?/?. 于 GP 可空", g10);
            TestHarness.Check("bug_g10 stdout 为 a=-1 / 7 / null",
                g10.Stdout, "a=-1\n7\nnull\n");
            CheckI32("bug_g10 main 返回 a=-1", g10, -1);
        }

        // W8：T extends B → B? 装箱视图端到端；反例无约束 T → U? 全管线报错
        private static void TestBoundNullableBoxingEndToEnd()
        {
            var ok = Run(
                "pub open class Animal {\n" +
                "    pub const id: i32\n" +
                "    pub init(_ -> id)\n" +
                "}\n" +
                "pub class Dog : Animal {\n" +
                "    pub init(_ -> id)\n" +
                "}\n" +
                "pub func wrapBound\\<T extends Animal>(x: T): Animal? { return x }\n" +
                "pub func main(): i32 {\n" +
                "    const v = wrapBound\\<Dog>(new Dog(7))\n" +
                "    return (v if? new Animal(0)).id\n" +
                "}\n");
            CheckOk("W8：T extends Animal → Animal? return", ok);
            CheckI32("W8 main 返回 7", ok, 7);

            var (bad, _, _) = BilTestHarness.EmitBilUnit(
                "pub func bad\\<T, U>(x: T): U? { return x }\n" +
                "pub func main(): i32 { return 0 }\n");
            TestHarness.CheckTrue("W8 反例：无约束 T → U? 全管线报错",
                bad.Diagnostics.HasErrors
                && bad.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("Cannot return 'T' from function returning 'Nullable<U>'")),
                string.Join("; ", bad.Diagnostics.Diagnostics.Select(d => d.Message)));
        }

        // ===== bug S1/g9：嵌套 struct place 写入丢失（§10/§13.2）=====
        // VM get.field 对值类型 .Copy()——修复 = 正向 get 物化中间值 +
        // 叶写 + 值类型中间反向 set 写回（普通字段链/复合赋值/值类型
        // receiver 方法调用三方向）

        // bug_s1 场景：字段链写 / 整字段替换 / 方法内 this 链写 / 顶层平写
        private static void TestNestedStructFieldChainWrite()
        {
            var result = Run(
                "pub struct Vec2 {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n" +
                "pub struct Rect {\n" +
                "    pub var origin: Vec2\n" +
                "    pub var size: Vec2\n" +
                "    pub init(_ -> origin, _ -> size)\n" +
                "    pub func shiftThis() { origin.x = (origin.x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = new Rect(new Vec2(1, 2), new Vec2(3, 4))\n" +
                "    r.origin.x = 7\n" +
                "    core.io.Console.println(\"${r.origin.x}\")\n" +
                "    r.origin = new Vec2(8, 2)\n" +
                "    core.io.Console.println(\"${r.origin.x}\")\n" +
                "    r.shiftThis()\n" +
                "    core.io.Console.println(\"${r.origin.x}\")\n" +
                "    var v = new Vec2(1, 2)\n" +
                "    v.x = 9\n" +
                "    core.io.Console.println(\"${v.x}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("bug_s1：嵌套 struct 字段链写穿", result);
            TestHarness.Check("chain=7 whole=8 this=9 flat=9",
                result.Stdout, "7\n8\n9\n9\n");
        }

        // bug_s1b 场景：place 上 receiver 调用 / 方法内 this 链 receiver
        // 调用 / 方法内复合赋值 / 整字段替换（全写回）
        private static void TestStructReceiverCallWriteback()
        {
            var result = Run(
                "pub struct Vec2 {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "    pub func bumpX() { x = (x + 1) }\n" +
                "}\n" +
                "pub struct Rect {\n" +
                "    pub var origin: Vec2\n" +
                "    pub var size: Vec2\n" +
                "    pub init(_ -> origin, _ -> size)\n" +
                "    pub func bumpOrigin() { origin.bumpX() }\n" +
                "    pub func addOrigin() { origin.x += 1 }\n" +
                "    pub func replaceOrigin() {\n" +
                "        var o = origin\n" +
                "        o.x = (o.x + 1)\n" +
                "        origin = o\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r1 = new Rect(new Vec2(1, 0), new Vec2(0, 0))\n" +
                "    r1.origin.bumpX()\n" +
                "    core.io.Console.println(\"${r1.origin.x}\")\n" +
                "    var r2 = new Rect(new Vec2(1, 0), new Vec2(0, 0))\n" +
                "    r2.bumpOrigin()\n" +
                "    core.io.Console.println(\"${r2.origin.x}\")\n" +
                "    var r3 = new Rect(new Vec2(1, 0), new Vec2(0, 0))\n" +
                "    r3.addOrigin()\n" +
                "    core.io.Console.println(\"${r3.origin.x}\")\n" +
                "    var r4 = new Rect(new Vec2(1, 0), new Vec2(0, 0))\n" +
                "    r4.replaceOrigin()\n" +
                "    core.io.Console.println(\"${r4.origin.x}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("bug_s1b：值类型 receiver 方法调用写回", result);
            TestHarness.Check("place-call/this-call/compound/replace 全 = 2",
                result.Stdout, "2\n2\n2\n2\n");
        }

        // bug_g9 场景：class 内嵌 struct——b.item.v = 2 经 item 写回
        private static void TestClassEmbeddedStructFieldWrite()
        {
            var result = Run(
                "pub struct Num {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub class BoxNum {\n" +
                "    pub var item: Num\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new BoxNum(new Num(1))\n" +
                "    b.item.v = 2\n" +
                "    core.io.Console.println(\"${b.item.v}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("bug_g9：class 内嵌 struct 字段写", result);
            TestHarness.Check("box=2", result.Stdout, "2\n");
        }

        // 三层嵌套 + 复合赋值 + 引用/值混合边界（写回在 class 中间停止）
        private static void TestThreeLevelNestedStructWrite()
        {
            var result = Run(
                "pub struct Deep {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub struct Mid {\n" +
                "    pub var leaf: Deep\n" +
                "    pub init(_ -> leaf)\n" +
                "}\n" +
                "pub class Outer {\n" +
                "    pub var mid: Mid\n" +
                "    pub init(_ -> mid)\n" +
                "}\n" +
                "pub class BoxMid {\n" +
                "    pub var m: Mid\n" +
                "    pub init(_ -> m)\n" +
                "}\n" +
                "pub class WrapBox {\n" +
                "    pub var box: BoxMid\n" +
                "    pub init(_ -> box)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var o = new Outer(new Mid(new Deep(1)))\n" +
                "    o.mid.leaf.v = 42\n" +
                "    core.io.Console.println(\"${o.mid.leaf.v}\")\n" +
                "    o.mid.leaf.v += 8\n" +
                "    core.io.Console.println(\"${o.mid.leaf.v}\")\n" +
                "    var w = new WrapBox(new BoxMid(new Mid(new Deep(1))))\n" +
                "    w.box.m.leaf.v = 5\n" +
                "    core.io.Console.println(\"${w.box.m.leaf.v}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("三层嵌套 + 混合边界字段写穿", result);
            TestHarness.Check("三层 42/50；class 中间停止写回 5",
                result.Stdout, "42\n50\n5\n");
        }

        // W6：静态字段根嵌套 struct 链写穿——赋值 / 整字段替换 / 复合
        // 赋值 / receiver 方法调用 / 全局字段根 全部写回槽位
        private static void TestStaticFieldRootChainWrite()
        {
            var result = Run(
                "pub struct Vec2 {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "    pub func bumpX() { x = (x + 1) }\n" +
                "}\n" +
                "pub struct Rect {\n" +
                "    pub var origin: Vec2\n" +
                "    pub var size: Vec2\n" +
                "    pub init(_ -> origin, _ -> size)\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    pub static var current: Rect = new Rect(new Vec2(1, 2), new Vec2(3, 4))\n" +
                "}\n" +
                "var g: Rect = new Rect(new Vec2(1, 2), new Vec2(3, 4))\n" +
                "pub func main(): i32 {\n" +
                "    Holder.current.origin.x = 7\n" +
                "    core.io.Console.println(\"${Holder.current.origin.x}\")\n" +
                "    Holder.current.origin = new Vec2(8, 2)\n" +
                "    core.io.Console.println(\"${Holder.current.origin.x}\")\n" +
                "    Holder.current.origin.x += 1\n" +
                "    core.io.Console.println(\"${Holder.current.origin.x}\")\n" +
                "    Holder.current.origin.bumpX()\n" +
                "    core.io.Console.println(\"${Holder.current.origin.x}\")\n" +
                "    g.origin.x = 11\n" +
                "    core.io.Console.println(\"${g.origin.x}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("W6：静态/全局字段根嵌套 struct 链写穿", result);
            TestHarness.Check("static 7/8/9/10；global 11",
                result.Stdout, "7\n8\n9\n10\n11\n");
        }

        // W6：wrapped 静态字段根（cell getValue 拷贝 → 链写 → setValue）
        private static void TestWrappedStaticFieldRootChainWrite()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper IdW {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub struct Vec2 {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n" +
                "pub struct Rect {\n" +
                "    pub var origin: Vec2\n" +
                "    pub var size: Vec2\n" +
                "    pub init(_ -> origin, _ -> size)\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @IdW\n" +
                "    pub static var current: Rect = new Rect(new Vec2(1, 2), new Vec2(3, 4))\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Holder.current.origin.x = 7\n" +
                "    core.io.Console.println(\"${Holder.current.origin.x}\")\n" +
                "    Holder.current.origin.x += 1\n" +
                "    core.io.Console.println(\"${Holder.current.origin.x}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("W6：wrapped 静态字段根链写穿", result);
            TestHarness.Check("wrapped static 7/8", result.Stdout, "7\n8\n");
        }
    }
}
