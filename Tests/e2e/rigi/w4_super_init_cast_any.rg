// W4（正例）：显式 `as Any` 让编译期选 init(Any)，VM 严格匹配 Any 而非 Node。
// expect-exit: 1
pub open class Node { pub init() {} }
pub class Leaf : Node { pub init() {} }
pub open class Box {
    pub var tag: i32 = 0
    pub init(a: Any) { tag = 1 }
    pub init(n: Node) { tag = 2 }
}
pub class Child : Box {
    pub init(x: Leaf) { super(x as Any) }
}
pub func main(): i32 {
    const c = new Child(new Leaf())
    return c.tag
}
