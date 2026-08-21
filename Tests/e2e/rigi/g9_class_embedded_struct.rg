// bug g9（正例·class 内嵌 struct）：b.item.v = 2 经 class 字段 item
// 写回（引用/值混合边界）。
// expect-output: 2
// expect-exit: 0
pub struct Num {
    pub var v: i32
    pub init(_ -> v)
}
pub class BoxNum {
    pub var item: Num
    pub init(_ -> item)
}
pub func main(): i32 {
    var b = new BoxNum(new Num(1))
    b.item.v = 2
    core.io.Console.println("${b.item.v}")
    return 0
}
