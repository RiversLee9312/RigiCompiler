// unsafe seq 的返回值、嵌套分支与方法体在 VM 上保持原行为。
// expect-output: 7
unsafe func danger(): i32 { return 7 }
pub func main(): i32 {
    const value = unsafe volatile seq {
        if (true) { return@_ danger() }
        return@_ 0
    }
    core.io.Console.println(value.toString())
    return 0
}
