// V3（负例）：is 试探命中布局违规构造 Wrap\<User>（非 rich struct
// 持 Object）——修复前零检查静默通过，修复后补只读填入点检查
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
