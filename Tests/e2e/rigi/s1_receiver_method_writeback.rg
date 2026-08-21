// bug g9/S1（正例·receiver 方法/复合赋值）：值类型 receiver 方法调用
// 写回——place 上 receiver 调用 / 方法内 this 链 receiver 调用 / 方法内
// 复合赋值 / 整字段替换四形态全写回（§10/§13.2）。
// expect-output: 2
// expect-output: 2
// expect-output: 2
// expect-output: 2
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
    pub func bumpOrigin() { origin.bumpX() }
    pub func addOrigin() { origin.x += 1 }
    pub func replaceOrigin() {
        var o = origin
        o.x = (o.x + 1)
        origin = o
    }
}
pub func main(): i32 {
    var r1 = new Rect(new Vec2(1, 0), new Vec2(0, 0))
    r1.origin.bumpX()
    core.io.Console.println("${r1.origin.x}")
    var r2 = new Rect(new Vec2(1, 0), new Vec2(0, 0))
    r2.bumpOrigin()
    core.io.Console.println("${r2.origin.x}")
    var r3 = new Rect(new Vec2(1, 0), new Vec2(0, 0))
    r3.addOrigin()
    core.io.Console.println("${r3.origin.x}")
    var r4 = new Rect(new Vec2(1, 0), new Vec2(0, 0))
    r4.replaceOrigin()
    core.io.Console.println("${r4.origin.x}")
    return 0
}
