// fx_inh_mono2 配套库：pub 泛型基类 + 文件级私有实参类型
pub open class Box\<T> {
    pub var v: T
    pub init(_ -> v)
}
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
