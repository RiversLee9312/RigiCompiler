import core.io.Console

pub open class Base {
    pub open var hp: i32 = 10
    pub var mp: i32 = 1
    pub init()
}

pub open class Hero : Base {
    pub override var hp: i32 = 99
    pub init()
}

pub class Villain : Hero {
    pub override var hp: i32 = -7
    pub init()
}

pub func main(): i32 {
    const b = new Base()
    const h = new Hero()
    const v = new Villain()
    Console.println("base=${b.hp} hero=${h.hp} villain=${v.hp}")
    Console.println("viaBase=${(h as Base).hp} mp=${h.mp}")
    return 0
}
