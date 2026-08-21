// bug g10（正例）：if?/?. 认 Nullable\<T\>（T 为型参）——
// unwrap\<i32\>(null, -1) 走 null 比较与 unwrap cast，?. 调方法同认。
// expect-output: a=-1
// expect-output: 7
// expect-output: null
// expect-exit: -1
import core.io.Console
pub func unwrap\<T>(x: T?, fallback: T): T { return (x if? fallback) }
pub func nameOf\<T>(x: T?): String? { return x?.toString() }
pub func main(): i32 {
    const a = unwrap\<i32>(null, -1)
    Console.println("a=${a}")
    Console.println((nameOf\<i32>(7) if? "null"))
    Console.println((nameOf\<i32>(null) if? "null"))
    return a
}
