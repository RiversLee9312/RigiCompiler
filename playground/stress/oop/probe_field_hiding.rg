import core.io.Console

pub open class Base {
    pub var hp: i32 = 10
    pub init()
}

pub class Hero : Base {
    pub var hp: i32 = 20
    pub init()
}

pub func main(): i32 {
    const h = new Hero()
    Console.println(h.hp.toString())
    return 0
}
