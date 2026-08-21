// F2 合法对照（正例）：同文件私有基类 + 私有派生、pub 基类 + pub 派生、
// 构造基类实参满足约束、同文件私有类型 is/typeOf、pub 函数字段全 pub
// 签名——新闸门均不得误报。
// expect-output: 7
// expect-exit: 7
import core.io.Console
open class PrivBase {
    pub init()
    pub open func n(): i32 { return 1 }
}
class PrivDerived : PrivBase {
    pub init() { super() }
    pub override func n(): i32 { return 7 }
}
pub open class PubBase {
    pub init()
}
pub class PubDerived : PubBase {
    pub init()
}
pub open class Animal {
    pub init()
}
pub class Dog : Animal {
    pub init()
}
pub open class Cage\<T extends Animal> {
    pub init()
}
pub class GoodCage : Cage\<Dog> {
    pub init()
}
pub func main(): i32 {
    const d: PrivBase = new PrivDerived()
    const o: Object = new PrivDerived()
    if (o is PrivDerived) {
        Console.println(d.n().toString())
        return d.n()
    }
    const t = typeOf(PrivDerived)
    return 0
}
