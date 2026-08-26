// MW7b：spanOf<struct Point> 元素内联读写
// expect-exit: 7
import core.collections.*
pub struct Point {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
}
pub func main(): i32 {
    var a = spanOf\<Point>(2)
    a[0] = new Point(3, 4)
    var p = a[0] if? new Point(0, 0)
    return (p.x + p.y)
}
