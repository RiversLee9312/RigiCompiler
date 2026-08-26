// MW7b：sharedSpanOf<i32> 基本读写
// expect-exit: 42
import core.collections.*
pub func main(): i32 {
    var a = sharedSpanOf\<i32>(2)
    a[0] = 40
    a[1] = 2
    return ((a[0] if? 0) + (a[1] if? 0))
}
