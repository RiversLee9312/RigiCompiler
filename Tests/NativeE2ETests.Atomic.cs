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

        // review-20260910 Phase 2.5：AtomicStruct.mutate（一步式安全 RMW）。
        // aurora 踩坑场景回归：store(load()+1) 的读-改-写跨 AtomicStruct
        // 异步 Mutex 挂起点会丢更新；mutate 持锁完成整个 RMW，多协程并发
        // 自增的终值必须精确等于 任务数×次数（确定性断言，不是概率对拍）。
        private const string AtomicStructMutateConcurrentSource = """
            import core.collections.*
            import core.coroutine.*

            pub func main(): i32 {
                const counter = new AtomicStruct\<i64>(0L)
                const rounds = 50
                const tasks = arrayOf\<Task>(4)
                var i = 0
                while (i < 4) {
                    const t = new Task(func{async () -> {
                        var r = 0
                        while (r < rounds) {
                            counter.mutate{(v: i64): i64 -> (v + 1L)}
                            r += 1
                        }
                    }})
                    t.run(new ComputeExecutor())
                    tasks[i] = t
                    i += 1
                }
                i = 0
                while (i < 4) {
                    await (tasks[i] as Task)
                    i += 1
                }
                core.io.Console.println(counter.load().toString())
                // 退出码绑定精确值：对拍一致不能排除两宿主同样丢更新
                if (counter.load() == 200L) {
                    return 0
                }
                return 1
            }
            """;

        // trailing lambda 形态（§5.3）：mutate{...} 紧贴调用；与显式
        // func{...} 实参混用，确认两种书写走同一私有 Atomic.mutate 转发。
        private const string AtomicStructMutateTrailingLambdaSource = """
            pub func main(): i32 {
                const c = new AtomicStruct\<i64>(10L)
                c.mutate{(v: i64): i64 -> (v + 1L)}
                core.io.Console.println(c.load().toString())
                c.mutate{(v: i64): i64 -> (v * 2L)}
                core.io.Console.println(c.load().toString())
                c.mutate(func{ (old: i64): i64 -> old + 5L })
                core.io.Console.println(c.load().toString())
                return 0
            }
            """;

        // 回调抛错：私有 Atomic.mutate 仅在回调成功返回后替换 Handle，
        // 异常经 finally 释放锁并保留旧值——门面只放行，不新造规则；
        // 抛错后再 mutate 确认锁可重取、计数继续有效。
        private const string AtomicStructMutateThrowKeepsOldValueSource = """
            pub func main(): i32 {
                const s = new AtomicStruct\<i64>(7L)
                try {
                    s.mutate(func{ (old: i64): i64 -> {
                        throw new core.RuntimeException("callback failed")
                    }})
                } catch(e: core.RuntimeException) {
                    core.io.Console.println("caught")
                }
                core.io.Console.println(s.load().toString())
                s.mutate{(v: i64): i64 -> (v + 1L)}
                core.io.Console.println(s.load().toString())
                return 0
            }
            """;
    }
}
