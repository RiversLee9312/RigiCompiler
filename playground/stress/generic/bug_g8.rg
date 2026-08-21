import core.io.Console

// bug g8：T 不能赋给 T?（无约束泛型参数的 Nullable 装箱视图）
pub func wrapNull\<T>(x: T): T? { return x }

pub func main(): i32 {
    const v = wrapNull\<i32>(3)
    Console.println((v if? 0).toString())
    return 0
}
