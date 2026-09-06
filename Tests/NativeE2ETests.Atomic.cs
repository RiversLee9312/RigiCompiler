namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        private const string AtomicNullableSource = """
            class Item { pub var n:i32 = 8 }
            pub func main(): i32 {
                unsafe seq {
                    const a = new Atomic\<i32?>(null)
                    core.io.Console.println((a.load() == null).toString())
                    a.mutate(func{ (old:i32?):i32? -> 5 })
                    core.io.Console.println((a.load() as i32).toString())
                    a.mutate(func{ (old:i32?):i32? -> null })
                    core.io.Console.println((a.load() == null).toString())
                    const o = new Atomic\<Item?>(null)
                    o.mutate(func{ (old:Item?):Item? -> new Item() })
                    const loaded = o.load()
                    core.io.Console.println((loaded == null).toString())
                    core.io.Console.println((loaded as Item).n.toString())
                    o.mutate(func{ (old:Item?):Item? -> null })
                    core.io.Console.println((o.load() == null).toString())
                }
                return 0
            }
            """;

        private const string CallableSlotSource = """
            class Item {}
            func apply\<T>(body:Func\<T,T,T>, a:T, b:T):T { return body(a,b) }
            pub func main():i32 {
                const sum = func{ (a:i32,b:i32):i32 -> a + b }
                core.io.Console.println(sum(2,3).toString())
                core.io.Console.println(apply\<i32>(sum,4,5).toString())
                const text = func{ (a:String,b:String):String -> a + b }
                core.io.Console.println(apply\<String>(text,"a","b"))
                const action = func{ (a:i32) -> core.io.Console.println(a.toString()) }
                action(6)
                const create = func{ ():Item -> new Item() }
                create()
                const stringify = func{ ():String -> "owned" + "-result" }
                stringify()
                return 0
            }
            """;

        private const string NullableCastSource = """
            class Item {}
            class Other {}
            func convert\<T>(value:Any?):T { return value as T }
            func check\<T>(value:Any?):bool { return value is T }
            pub func main():i32 {
                const good:Any? = new Item()
                const bad:Any? = new Other()
                core.io.Console.println(check\<Item?>(good).toString())
                core.io.Console.println(check\<Item?>(bad).toString())
                core.io.Console.println(check\<Item?>(null).toString())
                core.io.Console.println((convert\<Item?>(good) != null).toString())
                core.io.Console.println((convert\<Item?>(null) == null).toString())
                try { convert\<Item?>(bad) }
                catch(e:core.CastException) { core.io.Console.println("rejected") }
                return 0
            }
            """;

        private const string AtomicSource = """
            class Item {
                pub var n: i32
                pub init(n: i32) { this.n = n }
            }
            pub func main(): i32 {
                unsafe seq {
                    const a = new Atomic\<i32>(3)
                    a.mutate(func{ (old: i32): i32 -> old + 4 })
                    core.io.Console.println(a.load().toString())
                    try { a.mutate(func{ (old: i32): i32 -> { throw new core.RuntimeException("callback") }}) }
                    catch(e: core.RuntimeException) {}
                    core.io.Console.println(a.load().toString())
                    a.mutate(func{ (old: i32): i32 -> old + 2 })
                    core.io.Console.println(a.load().toString())
                    const o = new Atomic\<Item>(new Item(1))
                    o.mutate(func{ (old: Item): Item -> new Item(12) })
                    core.io.Console.println(o.load().n.toString())
                }
                const s = new AtomicStruct\<i32>(2)
                s.store(4)
                core.io.Console.println(s.load().toString())
                return 0
            }
            """;

        private const string AtomicContentionSource = """
            unsafe async func update(a:Atomic\<i32>) {
                a.mutate(func{ (old:i32):i32 -> {
                    yield
                    return@_ old + 1
                }})
            }
            pub func main():i32 {
                unsafe seq {
                    const a = new Atomic\<i32>(0)
                    const one = update(a)
                    const two = update(a)
                    await one
                    await two
                    core.io.Console.println(a.load().toString())
                }
                return 0
            }
            """;
    }
}
