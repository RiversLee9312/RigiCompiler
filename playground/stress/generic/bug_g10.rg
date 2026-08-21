import core.io.Console

// bug g10：if? / ?. 不认 Nullable<T>（T 为型参）
pub func unwrap\<T>(x: T?, fallback: T): T { return (x if? fallback) }

pub func main(): i32 {
    const a = unwrap\<i32>(null, -1)
    Console.println("a=" + a.toString())
    return 0
}
