import core.io.Console

pub open class Node {
    pub var tag: i32 = 0
    pub init(n: Node) {
        tag = n.tag + 1
        Console.println("Node.init ${tag}")
    }
    pub init(seed: i32) {
        tag = seed
    }
}

pub class Leaf : Node {
    pub init(prev: Leaf) {
        super(prev)
        Console.println("Leaf.init ${tag}")
    }
    pub init(seed: i32) {
        super(seed)
    }
}

pub func main(): i32 {
    const b = new Leaf(new Leaf(5))
    Console.println("ok ${b.tag}")
    return 0
}
