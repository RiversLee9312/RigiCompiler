// P14 行为变化（正例）：内建数组读取恒为 T?——越界/负下标读不 trap
// 按「读取失败」得 null（§13.2），界内写入与读回正常。
// expect-output: -1
// expect-output: null
// expect-exit: 7
import core.collections.*
pub func main(): i32 {
    var a = arrayOf\<i32>(2)
    a[0] = 7
    core.io.Console.println((a[5] if? -1).toString())
    const neg = a[(0 - 1)]
    if (neg == null) {
        core.io.Console.println("null")
    }
    return a[0] if? 0
}
