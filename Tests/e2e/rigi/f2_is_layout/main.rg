// F2/V3（负例）：is 试探命中布局违规构造 Wrap\<User>（非 rich struct
// 持 Object）——试探命中即填入点，补只读 CheckConstructedType
// （修复前静默通过）。
// expect-error: Non-rich struct 'Wrap' cannot hold object field 'v'
pub class User {
    pub init()
}
pub struct Wrap\<T> {
    pub var v: T
    pub init(_ -> v)
}
pub func check(a: Object): i32 {
    if (a is Wrap\<User>) {
        return 1
    }
    return 0
}
pub func main(): i32 { return 0 }
