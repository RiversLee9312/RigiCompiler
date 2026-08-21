// W4（正例）：init(Any) 声明在 init(Node) 之前，super(Leaf) 仍命中更具体的
// init(Node)——编译期 ranking + cast 到 Node，VM TypesEqual 不按声明序漂移。
// expect-exit: 2
pub open class Node { pub init() {} }
pub class Leaf : Node { pub init() {} }
pub open class Box {
    pub var tag: i32 = 0
    pub init(a: Any) { tag = 1 }
    pub init(n: Node) { tag = 2 }
}
pub class Child : Box {
    pub init(x: Leaf) { super(x) }
}
pub func main(): i32 {
    const c = new Child(new Leaf())
    return c.tag
}
