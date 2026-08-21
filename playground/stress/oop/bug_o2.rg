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
}

pub func main(): i32 {
    const c = new Child()
    Console.println("asChild=${c.work()}")
    return 0
}
