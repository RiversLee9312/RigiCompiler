// MW7b：spanOf<i32> 写入 / 读回 / 求和
// expect-exit: 42
import core.collections.*
pub func main(): i32 {
    var a = spanOf\<i32>(3)
    a[0] = 10
    a[1] = 20
    a[2] = 12
    return (((a[0] if? 0) + (a[1] if? 0)) + (a[2] if? 0))
}
