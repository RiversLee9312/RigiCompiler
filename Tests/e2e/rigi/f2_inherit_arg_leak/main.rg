// F2 复审 fx_inh_mono2（负例）：继承单调性递归构造基类实参——pub 类
// 的基类 Box\<Hidden\> 实参 Hidden 为文件级私有，泄漏点是 Hidden。
// expect-error: Inconsistent accessibility: base class 'Hidden' is less accessible than class 'HBox'
pub open class Box\<T> {
    pub var v: T
    pub init(_ -> v)
}
class Hidden {
    pub init()
}
pub class HBox : Box\<Hidden> {
    pub init() { super(new Hidden()) }
}
pub func main(): i32 { return 0 }
