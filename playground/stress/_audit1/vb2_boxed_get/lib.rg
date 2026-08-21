// V-B probe c4 配套库：HBox : Box\<Hidden> 具化，get() 返回 Hidden
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub open class Box\<T> {
    pub var v: T
    pub init(_ -> v)
    pub func get(): T { return v }
}
pub class HBox : Box\<Hidden> {
    pub init() { super(new Hidden()) }
}
