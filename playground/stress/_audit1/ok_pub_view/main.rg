// 合法对照（c10）：b.n() 经 pub Base 视图动态派发——视图类型 pub，不得误报
import core.io.Console
pub func main(): i32 {
    const b = mkView()
    const r = b.n()
    Console.println("r=${r}")
    return r
}
