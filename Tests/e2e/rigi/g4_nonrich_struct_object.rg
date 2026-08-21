// bug g4（负例）：非 rich 泛型 struct 以 Object 实参填入——「最悲观假设」
// 下 Wrap\<LocalUser\> 的字段 v 实为 Object 持有，编译报错（§3.1.1/§3.6）。
// expect-error: Non-rich struct 'Wrap' cannot hold object field 'v'
class LocalUser {
    pub const name: String
    pub init(_ -> name)
}
struct Wrap\<T> {
    pub var v: T
    pub init(_ -> v)
}
pub func main(): i32 {
    const w = new Wrap\<LocalUser>(new LocalUser("x"))
    return 0
}
