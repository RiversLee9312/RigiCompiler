// bug O7（正例）：..init.wrapper 先于一切 init 体执行且覆盖继承闭包
//（§9.7/§14.2 修订）——基类 init 体读字段时，（重申到子类的）Entity
// wrapper 已安装，读命中 .proxy.get.* 链；字段初值 10 先于 init 体。
// expect-output: audit
// expect-output: audit
// expect-exit: 11
@WrapperTarget(.Entity)
pub wrapper Audit {
    pub init()
    operator .proxy.get.*\<TValue>(symbol: String, value: TValue): TValue {
        core.io.Console.println("audit")
        return value
    }
}
@Audit()
pub open class Base {
    pub var hp: i32 = 10
    pub init() { hp = (this.hp + 1) }
}
@Audit()
pub class Hero : Base {
    pub init() { super() }
}
pub func main(): i32 {
    return new Hero().hp
}
