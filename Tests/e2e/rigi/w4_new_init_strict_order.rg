// W4（正例）：静态 new 同模型——init(Any) 在前，new Box(Leaf) 仍命中 init(Node)。
// expect-exit: 2
pub open class Node { pub init() {} }
pub class Leaf : Node { pub init() {} }
pub class Box {
    pub var tag: i32 = 0
    pub init(a: Any) { tag = 1 }
    pub init(n: Node) { tag = 2 }
}
pub func main(): i32 {
    const b = new Box(new Leaf())
    return b.tag
}
