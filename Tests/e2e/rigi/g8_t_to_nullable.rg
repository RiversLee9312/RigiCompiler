// bug g8（正例）：泛型参数 T → T? 返回按装箱视图构造
//（.generic 占位的 Nullable 包装）。
// expect-output: 3
// expect-exit: 0
import core.io.Console
pub func wrapNull\<T>(x: T): T? { return x }
pub func main(): i32 {
    const v = wrapNull\<i32>(3)
    Console.println("${(v if? 0)}")
    return 0
}
