// bug g2（正例）：整数字面量后可直接跟成员访问 7.twice() ≡ (7).twice()
//（SYNTAX §3.3，'.' 后继是标识符按路径表达式解析，含扩展成员）。
// 修复前 '.' 已被字面量层消费无法重放，7.twice() 解析失败。
// expect-output: 14
// expect-exit: 0
import core.io.Console
pub ext func i32.twice(): i32 { return (this * 2) }
pub func main(): i32 {
    Console.println("${7.twice()}")
    return 0
}
