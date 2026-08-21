import core.io.Console

pub open class A {
    pub var a1: i32 = 11
    pub var a2: i32 = 22
    pub init() {
        Console.println("A.init")
    }
}

pub class B : A {
    pub init() {
        Console.println("B.init")
    }
}

pub func main(): i32 {
    const b = new B()
    Console.println("B a1=${b.a1} a2=${b.a2}")
    return 0
}
