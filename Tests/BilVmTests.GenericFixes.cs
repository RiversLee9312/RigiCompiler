using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    // VM / 具化泛型修复：泛型类索引、类级 typeid 帧、T() 零值、
    // 内部类可空 init、init 按可赋值性匹配。
    public static partial class BilVmTests
    {
        private static void TestGenericIndexOperator()
        {
            var result = Run(
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub operator getAtIndex(index: i32): T { return item }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>(7)\n" +
                "    var nested = new Box\\<Box\\<i32>>(b)\n" +
                "    return ((nested[0])[0] + b[0])\n" +
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
                "    return ((a[0] + r.mix\\<i32>(1, 35)))\n" +
                "}\n");
            CheckOk("类泛型参数在方法体可用（arrayOf<TItem> + 方法自有 U）", result);
            CheckI32("7+35", result, 42);
        }

        private static void TestReifiedConstructZeroValue()
        {
            var i32 = Run(
                "pub func make\\<T>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    const x = make\\<i32>()\n" +
                "    core.io.Console.println(\"${x}\")\n" +
                "    return (x + 0)\n" +
                "}\n");
            CheckOk("T() 对 i32 产出零值", i32);
            TestHarness.Check("i32 零值打印 0", i32.Stdout, "0\n");
            CheckI32("0+0", i32, 0);

            var flag = Run(
                "pub func make\\<T>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    const x = make\\<bool>()\n" +
                "    if (x) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("T() 对 bool 产出 false", flag);
            CheckI32("false 走 else", flag, 0);

            var text = Run(
                "pub func make\\<T>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    const s = make\\<String>()\n" +
                "    core.io.Console.println(\"${s}\")\n" +
                "    if ((s == \"\")) { return 0 }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("T() 对 String 产出空串", text);
            TestHarness.Check("空串打印空行", text.Stdout, "\n");
            CheckI32("空串 length=0", text, 0);

            var point = Run(
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "}\n" +
                "pub func make\\<T>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    const p = make\\<Point>()\n" +
                "    return (p.x + p.y)\n" +
                "}\n");
            CheckOk("T() 对 struct 产出字段零值", point);
            CheckI32("Point 零值 0+0", point, 0);
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

        private static void TestInitMatchByAssignability()
        {
            var result = Run(
                "pub open class Account { pub var id: i32 }\n" +
                "pub class Savings : Account { }\n" +
                "pub class Node { pub init(a: Account) { } }\n" +
                "pub func main(): i32 {\n" +
                "    const s = new Savings()\n" +
                "    const n = new Node(s)\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("init 按可赋值性匹配（Savings → Account）", result);
            CheckI32("构造成功", result, 0);

            var nullable = Run(
                "pub open class Account { pub var id: i32 }\n" +
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
    }
}
