// bug g6（正例·标量界例外）：T extends i32 的零参 T() 按约束界编译期
// 判定放行，运行期产出标量零值（§3.7）。
// expect-output: 0
// expect-exit: 0
import core.io.Console
pub func make\<T extends i32>(): T { return T() }
pub func main(): i32 {
    const x = make\<i32>()
    Console.println("${x}")
    return (x + 0)
}
