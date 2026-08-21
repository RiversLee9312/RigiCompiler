// F1 合法对照（c10，正例·多文件同组）：b.n() 经 pub Base 视图动态
// 派发——视图类型本身 pub，使用点不提及不可见类型，F1 收口不得误报。
// expect-output: r=7
// expect-exit: 7
import core.io.Console
pub func main(): i32 {
    const b = mkView()
    const r = b.n()
    Console.println("r=${r}")
    return r
}
