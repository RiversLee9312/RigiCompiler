// bug S4 复现：具名导入命名空间顶层函数与全局 const（类型具名导入
// 此前已正常；import * 与 FQN 调用此前已正常）。
// 运行输出 x / y / y / 0
// 注：axisBoost 仅验证具名导入全局 const 可解析绑定（全局字段初始值
// 的 VM 执行是另一独立义务，此处输出与初始值无关）
import core.io.Console
import scene.geom.pickAxis
import scene.geom.axisBoost
import scene.geom.Vec2

pub func main(): i32 {
    const v = new Vec2(3, 1)
    Console.println(pickAxis(v))
    Console.println(pickAxis(1, 4))
    Console.println(scene.geom.pickAxis(9, 9))
    Console.println((axisBoost - axisBoost).toString())
    return 0
}
