// bug O2（正例）：子类未 override 的基类方法，其 Method wrapper 由
// 实际类型的 ..init.wrapper 经继承闭包缝合安装（§9.7 修订）——
// new Child().work() 也有 trace。
// expect-output: trace
// expect-output: 1
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
}
pub func main(): i32 {
    core.io.Console.println("${new Child().work()}")
    return 0
}
