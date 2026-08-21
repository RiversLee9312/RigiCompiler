// W5（负例，§9.3）：全局字段初值不得直接引用其它全局字段（后向引用）。
// expect-error: cannot reference global/static field 'b'
var a: i32 = (b + 1)
var b: i32 = 1
pub func main(): i32 {
    return 0
}
