import core.io.Console

pub open class Meter {
    pub var value: i32 {
        pub get(value: _) { return value }
        pub set(value: _) {
            value = value * 2
            Console.println("set ok")
        }
    } = 21
    pub init()
}

pub class Box {
    pub var n: i32 = 5
    pub init(_ -> n)
}

pub func main(): i32 {
    const m = new Meter()
    Console.println("meter=${m.value}")
    const b = new Box(9)
    Console.println("box=${b.n}")
    const b2 = new Box(0)
    Console.println("box2=${b2.n}")
    return 0
}
