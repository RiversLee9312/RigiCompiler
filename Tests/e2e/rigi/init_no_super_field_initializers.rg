// init 陷阱（正例）：子类 init 不调 super() 时基类用户 init 体不跑，
// 但基类字段初始值已由实际类型的 ..init.wrapper（..init.field.*）
// 缝合——先于任何 init 体（§9.3/§9.7 修订）。a1=11、a2=22（A.init
// 体未跑，若跑则 a2=122，退出码会变成 11122）。
// expect-exit: 11022
pub open class A {
    pub var a1: i32 = 11
    pub var a2: i32 = 22
    pub init() { a2 = (a2 + 100) }
}
pub class B : A {
    pub init() { }
}
pub func main(): i32 {
    const b = new B()
    return ((b.a1 * 1000) + b.a2)
}
