import core.io.Console

pub interface Work {
    func run(x: i32): i32
}

pub class Impl implements Work {
    pub init()
    pub override func run(x: i32): i32 { return (x + 1) }
}

pub class ViaIface implements Work like sink {
    pub var sink: Work = new Impl()
}

pub func main(): i32 {
    const b = new ViaIface()
    Console.println(b.run(3).toString())
    return 0
}
