// V-A probe c6b 配套库：priv Hidden 经 pub 字段以 Box\<Hidden> 具化泄出
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub open class Box\<T> {
    pub var v: T
    pub init(_ -> v)
}
pub class Holder2 {
    pub init()
    pub var bx: Box\<Hidden> = new Box\<Hidden>(new Hidden())
}
