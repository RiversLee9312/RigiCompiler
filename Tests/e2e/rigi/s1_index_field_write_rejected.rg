// bug g9/S1（负例·arr[i].f=）：P14 起 getAtIndex 恒返回 T?，索引结果
// 不是可写 place——b[0].x = 9 在 P3 拒绝（可空类型上不得直接访问成员）。
// expect-error: cannot be accessed on nullable type
pub struct Vec2 {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
}
class Bag {
    pub var held: Vec2
    pub init(v: Vec2) { held = v }
    pub operator getAtIndex(index: i32): Vec2? { return held }
}
func f(b: Bag) {
    b[0].x = 9
}
pub func main(): i32 {
    return 0
}
