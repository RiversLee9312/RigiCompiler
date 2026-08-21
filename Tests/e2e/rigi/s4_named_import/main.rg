// bug S4（正例·多文件同组）：具名 import 顶层函数（重载全导入按签名
// 消歧）与全局字段/const 进值/调用查找序（§15.2）。
// expect-output: vec
// expect-output: xy
// expect-output: 10
// expect-exit: 0
import core.io.Console
import scene.geom.pickAxis
import scene.geom.Vec2
import scene.geom.axisBoost
pub func main(): i32 {
    const v = new Vec2(3, 4)
    Console.println(pickAxis(v))
    Console.println(pickAxis(1, 2))
    Console.println("${axisBoost}")
    return 0
}
