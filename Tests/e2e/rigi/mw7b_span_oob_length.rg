// MW7b：越界读得 null；length 特权字段；越界写 MW9b 起抛可捕获 core.OutOfBoundException（本用例只断言读）
// expect-output: -1
// expect-output: null
// expect-exit: 3
import core.collections.*
pub func main(): i32 {
    var a = spanOf\<i32>(3)
    a[0] = 7
    core.io.Console.println((a[5] if? -1).toString())
    const neg = a[(0 - 1)]
    if (neg == null) {
        core.io.Console.println("null")
    }
    return a.length
}
