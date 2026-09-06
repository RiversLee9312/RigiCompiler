import core.io.Console
// expect-output: direct
// expect-output: 2
// expect-output: 7
// expect-output: 3
// expect-output: 22
// expect-output: masked
// expect-output: 2
pub func main(): i32 {
    const fail = func{ ():i32 -> { throw new core.RuntimeException("direct") }}
    try { fail() } catch (_:core.RuntimeException) { Console.println("direct") }
    var finalized: i32 = 0
    const choose = func{ (n:i32):i32 -> {
        try {
            if (n > 0) { return@_ n + 1 }
            throw new core.RuntimeException("mixed")
        } finally (_) { finalized = finalized + 1 }
    }}
    Console.println(choose(1).toString())
    try { choose(0) } catch (_:core.RuntimeException) {}
    Console.println(choose(6).toString())
    Console.println(finalized.toString())
    // finally 自己的完成覆盖原 return；循环跳转穿过 finally 后再生效。
    const overrideReturn = func{ ():i32 -> {
        try { return@_ 11 } finally (e) {
            if (e != null) { throw new core.RuntimeException("unexpected") }
            return@_ 22
        }
    }}
    Console.println(overrideReturn().toString())
    const overrideThrow = func{ ():i32 -> {
        try { return@_ 11 } finally (_) { throw new core.RuntimeException("masked") }
    }}
    try { overrideThrow() } catch (_:core.RuntimeException) { Console.println("masked") }
    var i = 0
    var cleaned = 0
    while (i < 3) {
        i = i + 1
        try {
            if (i == 1) { continue }
            break
        } finally (_) { cleaned = cleaned + 1 }
    }
    Console.println(cleaned.toString())
    return 0
}