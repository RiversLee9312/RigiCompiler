using System.Linq;

namespace RigiCompiler.Tests
{
    // P3 泛型类型系统修复组：this 自身具化、嵌套构造字段代换、
    // 字段访问路径型参代入、for-in 协议按具化接口判定、协变 init 豁免。
    public static partial class BinderTests
    {
        private static void TestGenericTypeFixes()
        {
            TestThisSelfConstructed();
            TestNestedGenericFieldIdentity();
            TestGenericFieldSubstitution();
            TestForEachConstructedInterface();
            TestCovariantInitUsage();
        }

        // ===== A7：泛型类体内 this 定型为自身具化 Box\<T\> =====
        private static void TestThisSelfConstructed()
        {
            TestHarness.Section("P3 GenericTypeFixes: this 自身具化");

            var (unit, bodies) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub func me(): Box\\<T> { return this }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>(7)\n" +
                "    return b.me().item\n" +
                "}\n");
            CheckNoErrors("无诊断（this 返回 Box<T>）", unit);
            TestHarness.CheckTrue("this 定型为 Box<T>",
                BoundDescribe.Body(BodyOf(bodies, "me")).Contains("This(Box<T>)"));

            // 嵌套泛型 Box<Box<i32>> 上调 me
            var (nested, _) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub func me(): Box\\<T> { return this }\n" +
                "}\n" +
                "pub func wrap(b: Box\\<Box\\<i32>>): Box\\<Box\\<i32>> { return b.me() }\n");
            CheckNoErrors("无诊断（嵌套 Box<Box<i32>>.me）", nested);

            // 多层继承链上 this 返回派生/基类具化
            var (inherit, inheritBodies) = BindUnit(
                "pub open class Box\\<T> {\n" +
                "    pub func me(): Box\\<T> { return this }\n" +
                "}\n" +
                "pub class Child\\<T> : Box\\<T> {\n" +
                "    pub func myself(): Child\\<T> { return this }\n" +
                "    pub func asBox(): Box\\<T> { return this }\n" +
                "}\n");
            CheckNoErrors("无诊断（继承链 this 返回）", inherit);
            TestHarness.CheckTrue("派生 this 定型为 Child<T>",
                BoundDescribe.Body(BodyOf(inheritBodies, "myself")).Contains("This(Child<T>)"));

            // 反例：返回类型与 this 具化不一致
            var (bad, _) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub func asOther(): Box\\<i32> { return this }\n" +
                "}\n");
            TestHarness.CheckSemanticError("this 不能当成无关具化返回", bad.Diagnostics,
                "Cannot return 'Box<T>' from function returning 'Box<i32>'");
        }

        // ===== A8：同形嵌套构造类型互赋（型参身份统一）=====
        private static void TestNestedGenericFieldIdentity()
        {
            TestHarness.Section("P3 GenericTypeFixes: 嵌套构造字段身份");

            var (unit, _) = BindUnit(
                "pub class Node\\<T> {\n" +
                "    pub var next: Node\\<T>?\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v) { next = null }\n" +
                "}\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var head: Node\\<T>?\n" +
                "    pub init() { head = null }\n" +
                "    pub func walk() {\n" +
                "        var n = head\n" +
                "        if (n != null) { head = n.next }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（Node<T>? 同形互赋）", unit);

            // 嵌套更深：Node<Node<T>>?
            var (deep, _) = BindUnit(
                "pub class Node\\<T> {\n" +
                "    pub var nest: Node\\<Node\\<T>>?\n" +
                "    pub init() { nest = null }\n" +
                "}\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var cur: Node\\<Node\\<T>>?\n" +
                "    pub init() { cur = null }\n" +
                "    pub func step(n: Node\\<T>) {\n" +
                "        cur = n.nest\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（Node<Node<T>>? 跨类代换）", deep);

            // 反例：不同实参的同形构造不可赋
            var (bad, _) = BindUnit(
                "pub class Node\\<T> {\n" +
                "    pub var next: Node\\<T>?\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v) { next = null }\n" +
                "}\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var head: Node\\<T>?\n" +
                "    pub init() { head = null }\n" +
                "}\n" +
                "pub func bad(h: Holder\\<i32>, n: Node\\<String>?) { h.head = n }\n");
            TestHarness.CheckSemanticError("Node<String>? 不可赋给 Node<i32>?", bad.Diagnostics,
                "Cannot assign 'Nullable<Node<String>>' to 'Nullable<Node<i32>>'");
        }

        // ===== C3：实例字段访问路径代入宿主实参 =====
        private static void TestGenericFieldSubstitution()
        {
            TestHarness.Section("P3 GenericTypeFixes: 字段访问路径代入");

            var (unit, bodies) = BindUnit(
                "pub class Repo\\<TItem> {\n" +
                "    pub var data: Array\\<TItem>\n" +
                "}\n" +
                "pub func write(r: Repo\\<i32>, a: Array\\<i32>) {\n" +
                "    r.data = a\n" +
                "    r.data[0] = 7\n" +
                "}\n");
            CheckNoErrors("无诊断（Array<TItem> 字段代入 i32）", unit);
            TestHarness.CheckTrue("字段访问定型为 Array<i32>",
                BoundDescribe.Body(BodyOf(bodies, "write")).Contains(
                    "InstField(data, Param(r,Repo<i32>), Array<i32>)"));

            // 嵌套 Array<Array<TItem>>
            var (nested, _) = BindUnit(
                "pub class Repo\\<TItem> {\n" +
                "    pub var grid: Array\\<Array\\<TItem>>\n" +
                "}\n" +
                "pub func write(r: Repo\\<i32>, g: Array\\<Array\\<i32>>) { r.grid = g }\n");
            CheckNoErrors("无诊断（嵌套 Array<Array<TItem>> 代入）", nested);

            // 方法形参路径对照（本就代入）+ 字段对照
            var (paramOk, _) = BindUnit(
                "pub class Repo\\<TItem> {\n" +
                "    pub func take(items: Array\\<TItem>) { }\n" +
                "}\n" +
                "pub func f(r: Repo\\<i32>, a: Array\\<i32>) { r.take(a) }\n");
            CheckNoErrors("无诊断（方法形参 Array<TItem> 对照）", paramOk);

            // 反例：实参类型对不上代入后的字段
            var (bad, _) = BindUnit(
                "pub class Repo\\<TItem> {\n" +
                "    pub var data: Array\\<TItem>\n" +
                "}\n" +
                "pub func bad(r: Repo\\<i32>, a: Array\\<String>) { r.data = a }\n");
            TestHarness.CheckSemanticError("Array<String> 不可赋给 Array<i32> 字段",
                bad.Diagnostics, "Cannot assign 'Array<String>' to 'Array<i32>'");
        }

        // ===== A3：for-in 按具化接口判定 =====
        private static void TestForEachConstructedInterface()
        {
            TestHarness.Section("P3 GenericTypeFixes: for-in 具化协议");

            var bagSrc =
                "import core.collections.*\n" +
                "pub class Bag\\<T> implements IEnumerable\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub class It implements IEnumerator\\<T> {\n" +
                "        priv var given: bool\n" +
                "        priv var val: T\n" +
                "        pub init(v: T) { val = v\n            given = false }\n" +
                "        pub override func moveNext(): bool {\n" +
                "            if (given) { return false }\n" +
                "            given = true\n" +
                "            return true\n" +
                "        }\n" +
                "        pub override func current(): T { return val }\n" +
                "    }\n" +
                "    pub override func iterate(): IEnumerator\\<T> { return new It(item) }\n" +
                "}\n";

            var (unit, bodies) = BindUnitWithStdlib(bagSrc +
                "pub func main(): i32 {\n" +
                "    var b = new Bag\\<i32>(7)\n" +
                "    var n = 0\n" +
                "    for (x in b) { n = x }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（Bag<i32> 直接 for-in）", unit);
            TestHarness.CheckTrue("循环变量定型为 i32",
                BoundDescribe.Body(BodyOf(bodies, "main")).Contains("For(x, Local(b,Bag<i32>)"));
            var loop = BodyOf(bodies, "main").Body.Statements
                .OfType<BoundLoop>().Single();
            TestHarness.CheckTrue("元素类型为 i32",
                loop.LoopVariable != null
                && ReferenceEquals(loop.LoopVariable.Type, unit.Symbols.Bootstrap.Int32));

            // 先赋给 IEnumerable<i32> 再遍历（对照：修复前仅此路径成功）
            var (viaIface, _) = BindUnitWithStdlib(bagSrc +
                "pub func main(): i32 {\n" +
                "    var e: IEnumerable\\<i32> = new Bag\\<i32>(7)\n" +
                "    var n = 0\n" +
                "    for (x in e) { n = x }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（经 IEnumerable<i32> 变量 for-in）", viaIface);

            // 嵌套 Bag<Bag<i32>>
            var (nested, nestedBodies) = BindUnitWithStdlib(bagSrc +
                "pub func main(): i32 {\n" +
                "    var leaf = new Bag\\<i32>(7)\n" +
                "    var outer = new Bag\\<Bag\\<i32>>(leaf)\n" +
                "    var n = 0\n" +
                "    for (b in outer) {\n" +
                "        for (x in b) { n = x }\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（Bag<Bag<i32>> 嵌套 for-in）", nested);
            TestHarness.CheckTrue("外层元素为 Bag<i32>",
                BoundDescribe.Body(BodyOf(nestedBodies, "main")).Contains("For(b, Local(outer,Bag<Bag<i32>>)"));

            // 接口继承链：IBag<T> implements IEnumerable<T>
            var (chain, _) = BindUnitWithStdlib(
                "import core.collections.*\n" +
                "pub interface IBag\\<T> implements IEnumerable\\<T> { }\n" +
                "pub class Bag\\<T> implements IBag\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub class It implements IEnumerator\\<T> {\n" +
                "        priv var given: bool\n" +
                "        priv var val: T\n" +
                "        pub init(v: T) { val = v\n            given = false }\n" +
                "        pub override func moveNext(): bool {\n" +
                "            if (given) { return false }\n" +
                "            given = true\n" +
                "            return true\n" +
                "        }\n" +
                "        pub override func current(): T { return val }\n" +
                "    }\n" +
                "    pub override func iterate(): IEnumerator\\<T> { return new It(item) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag\\<i32>(3)\n" +
                "    var n = 0\n" +
                "    for (x in b) { n = x }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（经 IBag<T> 接口链 for-in）", chain);

            // 反例：未实现 IEnumerable
            var (bad, _) = BindUnitWithStdlib(
                "pub class NotBag { }\n" +
                "pub func main(): i32 {\n" +
                "    for (x in new NotBag()) { }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("未实现 IEnumerable 仍拒绝", bad.Diagnostics,
                "does not implement core.collections.IEnumerable<T>");
        }

        // ===== A1：协变类型经 init 构造后再经协变引用读取 =====
        private static void TestCovariantInitUsage()
        {
            TestHarness.Section("P3 GenericTypeFixes: 协变 init 使用");

            var (unit, _) = BindUnit(
                "pub open class Animal { }\n" +
                "pub class Dog : Animal { }\n" +
                "pub class Box\\<out T> {\n" +
                "    pub const item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "pub func take(b: Box\\<Animal>): Animal { return b.item }\n" +
                "pub func main(): Animal {\n" +
                "    var b = new Box\\<Dog>(new Dog())\n" +
                "    return take(b)\n" +
                "}\n");
            CheckNoErrors("无诊断（Box<out T> init 后协变传递）", unit);

            // 反例：普通方法参数仍禁 out T
            var (badMethod, _) = BindUnit(
                "pub class Box\\<out T> {\n" +
                "    pub func put(item: T) { }\n" +
                "}\n");
            TestHarness.CheckSemanticError("out T 仍不可用于普通方法参数",
                badMethod.Diagnostics,
                "covariant parameter 'T' cannot be used in parameter 'item' of method 'put'");
        }
    }
}
