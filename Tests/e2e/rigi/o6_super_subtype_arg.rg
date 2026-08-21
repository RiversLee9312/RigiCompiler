// bug O6（正例）：super(...) 实参是形参声明类型的子类——前端生成到
// 形参类型的 cast（§9.2.2），VM 按可赋值性匹配，不再中止。
// expect-exit: 6
pub open class Node {
    pub var tag: i32 = 0
    pub init(n: Node) { tag = (n.tag + 1) }
    pub init(seed: i32) { tag = seed }
}
pub class Leaf : Node {
    pub init(prev: Leaf) { super(prev) }
    pub init(seed: i32) { super(seed) }
}
pub func main(): i32 {
    const b = new Leaf(new Leaf(5))
    return b.tag
}
