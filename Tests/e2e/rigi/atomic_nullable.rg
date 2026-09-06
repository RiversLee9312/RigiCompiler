// nullable 作为泛型 T 保持稳定存储，null 往返不解引用。
// expect-output: true
// expect-output: 5
// expect-output: true
pub func main(): i32 {
    unsafe seq {
        const a = new Atomic\<i32?>(null)
        core.io.Console.println((a.load() == null).toString())
        a.mutate(func{ (old: i32?): i32? -> 5 })
        core.io.Console.println((a.load() as i32).toString())
        a.mutate(func{ (old: i32?): i32? -> null })
        core.io.Console.println((a.load() == null).toString())
    }
    return 0
}
