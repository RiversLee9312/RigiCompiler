namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        private const string HandleBoundarySource = """
            class Item { pub var n: i32 = 1 }
            class ActualCell: Cell\<Any> {
                pub override func getValue(): Any { return 73 }
                pub override func setValue(v: Any) {}
            }
            unsafe func check\<T>(v: T) {
                seq using(const p = placeOf v) {
                    const h = p.expose()
                    core.io.Console.println(h.load().toString())
                    try { h.asMutable() }
                    catch(e: core.ImmutablePlaceException) { core.io.Console.println("immutable") }
                }
            }
            pub func main(): i32 {
                const n: i32 = 5
                const item = new Item()
                unsafe seq using(const p = placeOf n) using(const q = placeOf item) {
                    const h = p.expose()
                    const o = q.expose()
                    core.io.Console.println(h.load().toString())
                    try { h.asMutable() }
                    catch(e: core.ImmutablePlaceException) { core.io.Console.println("readonly") }
                    o.load().n = 8
                    core.io.Console.println(item.n.toString())
                    try { o.asMutable() }
                    catch(e: core.ImmutablePlaceException) { core.io.Console.println("object") }
                    check\<Any>(7)
                    const cell: Any = new ActualCell()
                    seq using(const cp = placeOf cell) {
                        const ch = cp.expose()
                        core.io.Console.println((ch.load() is ActualCell).toString())
                    }
                }
                return 0
            }
            """;

        private const string CellSlotSource = """
            interface Read { func getValue(): i32 }
            class UserCell: Cell\<i32> implements Read {
                var value: i32 = 2
                pub override func getValue(): i32 { return value }
                pub override func setValue(v: i32) {
                    if (v == 99) { throw new core.RuntimeException("setter") }
                    value = v
                }
                pub func getValue(extra: i32): i32 { return value + extra }
                pub func setValue(a: i32, b: i32) { value = a + b }
            }
            func cycle\<T>(cell: Cell\<T>, value: T): T {
                cell.setValue(value)
                return cell.getValue()
            }
            pub func main(): i32 {
                const c = new UserCell()
                c.setValue(4)
                core.io.Console.println(c.getValue().toString())
                core.io.Console.println(cycle\<i32>(c, 8).toString())
                const i: Read = c
                core.io.Console.println(i.getValue().toString())
                core.io.Console.println(c.getValue(3).toString())
                c.setValue(4, 5)
                core.io.Console.println(c.getValue().toString())
                try { cycle\<i32>(c, 99) }
                catch(e: core.RuntimeException) { core.io.Console.println(e.getMessage()) }
                return 0
            }
            """;

        private const string HandleRichSource = """
            class Ref { pub var n: i32 = 6 }
            rich struct Value {
                pub var object: Ref
                pub var text: String
                pub init(o: Ref, s: String) { object = o
                    text = s }
            }
            pub func main(): i32 {
                var v = new Value(new Ref(), "first")
                unsafe seq using(const p = placeOf v) {
                    const h = p.expose()
                    const m = h.asMutable()
                    const copy = h.load()
                    m.store(new Value(new Ref(), "second"))
                    core.io.Console.println(copy.text)
                    core.io.Console.println(v.text)
                    core.io.Console.println(h.load().object.n.toString())
                    try {
                        m.store(new Value(new Ref(), "third"))
                        throw new core.RuntimeException("after-store")
                    }
                    catch(e: core.RuntimeException) { core.io.Console.println(v.text) }
                    core.io.Console.println(copy.text)
                }
                const frozen = new Value(new Ref(), "readonly")
                unsafe seq using(const p = placeOf frozen) {
                    core.io.Console.println(p.expose().load().text)
                    try { p.expose().asMutable() }
                    catch(e: core.ImmutablePlaceException) { core.io.Console.println("immutable") }
                }
                return 0
            }
            """;

        private const string HandleCycleSource = """
            unsafe shared class Node {
                pub var next: Handle\<Node>?
            }
            unsafe func cycle() {
                const n = new Node()
                seq using(const p = placeOf n) { n.next = p.expose() }
            }
            pub func main(): i32 {
                unsafe seq { var i: i32 = 0
                    while(i < 500) { cycle()
                        i += 1 } }
                core.io.Console.println("collected")
                return 0
            }
            """;

        // 3b-δ1 借用化对拍①：load() 消费链借用槽多跳传播（call→copy→
        // cast→传参→出口返回）。handle_target 已去 acquire（返回裸引用，
        // RcInjection 豁免借用槽全部义务），本用例锁定行为不变 + memtrack
        // 零泄漏口径自动检测 ARC 配平（借用槽漏 release 成 owned 或反向
        // 都会在双宿主对拍/泄漏退出码暴露）
        private const string HandleBorrowChainSource = """
            class Item { pub var n: i32 = 1 }
            unsafe func echo(v: i32): i32 { return v }
            unsafe func probe(item: Item): i32 {
                seq using(const q = placeOf item) {
                    const h = q.expose()
                    const a = h.load()
                    const b = h.load()
                    const c = h.load()
                    core.io.Console.println(a.n.toString())
                    core.io.Console.println(b.n.toString())
                    core.io.Console.println(c.n.toString())
                    return (echo(a.n) + echo(b.n)) + echo(c.n)
                }
                return 0
            }
            pub func main(): i32 {
                const item = new Item()
                item.n = 7
                var result: i32 = 0
                unsafe seq using(const w = placeOf item) {
                    result = probe(item)
                }
                return result
            }
            """;

        // 3b-δ1 借用化对拍②：跨协程借用 load——expose/load 发生在 async
        // 帮手体内（CoroutineSplit 后的 resume 状态机 fn 中间），借用返回
        // 推导与借用槽传播必须覆盖 split 后的全部函数；Handle/借用值随
        // frame move 跨挂起点不破坏配平
        private const string HandleBorrowCoroutineSource = """
            shared class Item { pub var n: i32 = 1 }
            async func probe(item: Item): i32 {
                unsafe seq using(const q = placeOf item) {
                    const h = q.expose()
                    return h.load().n
                }
                return 0
            }
            async func run(): i32 {
                const item = new Item()
                item.n = 42
                return await probe(item)
            }
            pub func main(): i32 {
                const t = run()
                return await t
            }
            """;
    }
}
