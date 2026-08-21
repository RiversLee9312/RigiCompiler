import core.io.Console

@WrapperTarget(.Method)
pub wrapper Trace {
    pub init()
    operator .proxy.call\<TReturn>(): TReturn {
        Console.println("trace")
        return inner()
    }
}

pub open class Base {
    pub init()
    @Trace()
    pub open func work(): i32 { return 1 }
}

pub class Child : Base {
    pub init()
    @Trace()
    pub override func work(): i32 { return 2 }
}

pub interface Work {
    func work(): i32
}

pub class Job implements Work {
    pub init()
    @Trace()
    pub override func work(): i32 { return 3 }
}

pub func main(): i32 {
    const c = new Child()
    Console.println("asChild=${c.work()}")
    const b: Base = c
    Console.println("asBase=${b.work()}")
    const w: Work = new Job()
    Console.println("asIface=${w.work()}")
    return 0
}
