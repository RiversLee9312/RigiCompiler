// W2 负例：静态字段类型不得使用所属类型上的类型参数。
// expect-error: static members cannot use type parameter 'T' of enclosing type 'Box'
pub class Box\<T> {
    pub var v: T
    pub init(_ -> v)
    pub static var zero: T
}
pub func main(): i32 { return 0 }
