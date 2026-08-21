// F1/V-A e2e 配套库（负例·多文件同组）：priv Hidden 经 pub 字段以
// Box\<Hidden\> 具化泄出。
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub open class Box\<T> {
    pub var v: T
    pub init(_ -> v)
}
pub class Holder {
    pub init()
    pub var bx: Box\<Hidden> = new Box\<Hidden>(new Hidden())
}
