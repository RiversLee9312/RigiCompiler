// W6/S1（正例·静态字段根）：嵌套 struct 链写穿以静态/全局字段为根——
// VM get.field.static 对值类型 .Copy() 后，正向 get 物化 + 叶写 + 反向
// set 再 set.field.static 写回槽位。覆盖：链写 / 整字段替换 / 复合赋值 /
// receiver 方法调用 / 全局字段根。
// expect-output: 7
// expect-output: 8
// expect-output: 9
// expect-output: 10
// expect-output: 11
// expect-exit: 0
pub struct Vec2 {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
    pub func bumpX() { x = (x + 1) }
}
pub struct Rect {
    pub var origin: Vec2
    pub var size: Vec2
    pub init(_ -> origin, _ -> size)
}
pub class Holder {
    pub static var current: Rect = new Rect(new Vec2(1, 2), new Vec2(3, 4))
}
var g: Rect = new Rect(new Vec2(1, 2), new Vec2(3, 4))
pub func main(): i32 {
    Holder.current.origin.x = 7
    core.io.Console.println("${Holder.current.origin.x}")
    Holder.current.origin = new Vec2(8, 2)
    core.io.Console.println("${Holder.current.origin.x}")
    Holder.current.origin.x += 1
    core.io.Console.println("${Holder.current.origin.x}")
    Holder.current.origin.bumpX()
    core.io.Console.println("${Holder.current.origin.x}")
    g.origin.x = 11
    core.io.Console.println("${g.origin.x}")
    return 0
}
