// W5（负例，§9.3）：静态字段初值不得直接引用其它静态字段。
// expect-error: cannot reference global/static field 'Holder.s'
pub class Holder {
    pub static var s: i32 = 1
    pub static var t: i32 = (s + 1)
}
pub func main(): i32 {
    return 0
}
