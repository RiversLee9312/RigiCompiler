// 字段 override（正例，§9.2.1 字段覆写）：open 字段 + 子类 override
// 不同初值——存储仍是基类槽，虚派发选中最高派生 ..init.field。
// base=10、hero=99、villain=-7（经基类槽读回 -7）。
// expect-exit: 10996993
pub open class Base {
    pub open var hp: i32 = 10
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
    const viaBase: Base = v
    return ((((b.hp * 1000) + (h.hp * 10)) + -v.hp) * 1000) + viaBase.hp
}
