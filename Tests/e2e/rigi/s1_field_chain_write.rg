// bug g9/S1（正例·字段链）：嵌套 struct 字段链写穿——VM get.field 对值
// 类型 .Copy() 后，正向 get 物化中间值 + 叶写 + 值类型中间反向 set 写回
//（§10/§13.2）。覆盖：字段链写 / 整字段替换 / 方法内 this 链写 / 顶层平写。
// expect-output: 7
// expect-output: 8
// expect-output: 9
// expect-output: 9
// expect-exit: 0
pub struct Vec2 {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
}
pub struct Rect {
    pub var origin: Vec2
    pub var size: Vec2
    pub init(_ -> origin, _ -> size)
    pub func shiftThis() { origin.x = (origin.x + 1) }
}
pub func main(): i32 {
    var r = new Rect(new Vec2(1, 2), new Vec2(3, 4))
    r.origin.x = 7
    core.io.Console.println("${r.origin.x}")
    r.origin = new Vec2(8, 2)
    core.io.Console.println("${r.origin.x}")
    r.shiftThis()
    core.io.Console.println("${r.origin.x}")
    var v = new Vec2(1, 2)
    v.x = 9
    core.io.Console.println("${v.x}")
    return 0
}
