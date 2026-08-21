// F2/V-C 配套库：泛型基类带 extends 约束
pub open class Animal {
    pub init()
}
pub open class Cage\<T extends Animal> {
    pub init()
}
