// 合法对照（正例）：同文件私有基类 + 私有派生、pub 基类 + pub 派生、
// internal 派生 + internal 基类、构造基类实参满足约束——均不应报错
open class PrivBase {
    pub init()
}
class PrivDerived : PrivBase {
    pub init()
}
pub open class PubBase {
    pub init()
}
pub class PubDerived : PubBase {
    pub init()
}
internal open class IntBase {
    pub init()
}
internal class IntDerived : IntBase {
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
pub func main(): i32 { return 0 }
