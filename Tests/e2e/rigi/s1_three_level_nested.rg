// bug g9/S1（正例·三层嵌套 + 复合赋值 + 混合边界）：三层 struct 链写穿；
// class 中间环节处写回停止（w.box.m.leaf.v = 5 只写回 box.m 一层）。
// expect-output: 42
// expect-output: 50
// expect-output: 5
// expect-exit: 0
pub struct Deep {
    pub var v: i32
    pub init(_ -> v)
}
pub struct Mid {
    pub var leaf: Deep
    pub init(_ -> leaf)
}
pub class Outer {
    pub var mid: Mid
    pub init(_ -> mid)
}
pub class BoxMid {
    pub var m: Mid
    pub init(_ -> m)
}
pub class WrapBox {
    pub var box: BoxMid
    pub init(_ -> box)
}
pub func main(): i32 {
    var o = new Outer(new Mid(new Deep(1)))
    o.mid.leaf.v = 42
    core.io.Console.println("${o.mid.leaf.v}")
    o.mid.leaf.v += 8
    core.io.Console.println("${o.mid.leaf.v}")
    var w = new WrapBox(new BoxMid(new Mid(new Deep(1))))
    w.box.m.leaf.v = 5
    core.io.Console.println("${w.box.m.leaf.v}")
    return 0
}
