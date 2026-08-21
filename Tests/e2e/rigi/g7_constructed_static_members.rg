// W2 负例：经构造类型访问静态成员一律非法（即使该静态成员不碰 T）。
// expect-error: cannot access static member 'count' via constructed type 'Box<i32>'
pub class Box\<T> {
    pub var v: T
    pub init(_ -> v)
    pub static func count(): i32 { return 0 }
}
pub func main(): i32 {
    return Box\<i32>.count()
}
