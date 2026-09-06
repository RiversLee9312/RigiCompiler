// Atomic 对值和 local 对象均可替换；异常不提交并释放锁。
// expect-output: 7
// expect-output: 7
// expect-output: 9
// expect-output: 12
// expect-output: 4
class AtomicItem {
    pub var n: i32
    pub init(value: i32) { n = value }
}
func atomicFail(old: i32): i32 { throw new core.RuntimeException("callback") }
pub func main(): i32 {
    unsafe seq {
        const a = new Atomic\<i32>(3)
        a.mutate(func{ (old: i32): i32 -> old + 4 })
        core.io.Console.println(a.load().toString())
        try {
            a.mutate(func{ (old: i32): i32 -> atomicFail(old) })
        } catch(e: core.RuntimeException) {}
        core.io.Console.println(a.load().toString())
        a.mutate(func{ (old: i32): i32 -> old + 2 })
        core.io.Console.println(a.load().toString())
        const o = new Atomic\<AtomicItem>(new AtomicItem(1))
        o.mutate(func{ (old: AtomicItem): AtomicItem -> new AtomicItem(12) })
        core.io.Console.println(o.load().n.toString())
    }
    const s = new AtomicStruct\<i32>(2)
    s.store(4)
    core.io.Console.println(s.load().toString())
    return 0
}
