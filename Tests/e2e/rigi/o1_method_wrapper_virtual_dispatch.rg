// bug O1（正例）：虚/接口派发不丢 Method wrapper——经基类/接口/中间层
// 静态类型调用时 invoke 带静态符号，收集 wrapper 链前先按 receiver
// 实际类型虚派发取实现槽符号（RUNTIME §7 + §14.9）。
// expect-output: trace
// expect-output: 2
// expect-output: trace
// expect-output: 3
// expect-output: trace
// expect-output: 3
// expect-exit: 0
@WrapperTarget(.Method)
pub wrapper Trace {
    pub init()
    operator .proxy.call\<TReturn>(): TReturn {
        core.io.Console.println("trace")
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
pub open class Mid : Base {
    pub init()
    @Trace()
    pub override func work(): i32 { return 2 }
}
pub class Leaf : Mid {
    pub init()
    @Trace()
    pub override func work(): i32 { return 3 }
}
pub func main(): i32 {
    const b: Base = new Child()
    core.io.Console.println("${b.work()}")
    const w: Work = new Job()
    core.io.Console.println("${w.work()}")
    const m: Mid = new Leaf()
    core.io.Console.println("${m.work()}")
    return 0
}
