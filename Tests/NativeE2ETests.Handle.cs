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
    }
}
