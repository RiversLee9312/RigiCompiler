// g4 反例矩阵（应编译通过并运行）：值类型实参 / rich 持有者 / 嵌套值类型
pub struct Wrap\<T> {
    pub var v: T
    pub init(_ -> v)
}
pub rich struct RichWrap\<T> {
    pub var v: T
    pub init(_ -> v)
}
pub class User {
    pub const name: String
    pub init(_ -> name)
}
func main() {
    const a = new Wrap\<i32>(8)
    const b = new RichWrap\<User>(new User("x"))
    const c = new Wrap\<Wrap\<i32>>(new Wrap\<i32>(1))
}
