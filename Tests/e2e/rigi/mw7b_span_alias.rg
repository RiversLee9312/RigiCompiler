// MW7b：Span 引用语义——b=a 后经 b 写入、经 a 读可见
// expect-exit: 42
import core.collections.*
pub func main(): i32 {
    var a = spanOf\<i32>(1)
    a[0] = 1
    var b = a
    b[0] = 42
    return a[0] if? 0
}
