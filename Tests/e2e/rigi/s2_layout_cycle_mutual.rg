// bug S2（负例·互含）：值类型布局环——A↔B 互含，环路径规范化去重后
// 只报一条（§10）。
// expect-error: Value-type layout cycle: MutA -> MutB -> MutA
pub struct MutA {
    pub var b: MutB
    pub init(_ -> b)
}
pub struct MutB {
    pub var a: MutA
    pub init(_ -> a)
}
pub func main(): i32 {
    return 0
}
