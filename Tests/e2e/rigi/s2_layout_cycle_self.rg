// bug S2（负例·自含）：值类型布局环——struct 直接值字段回指自身，
// 布局无法有限落地，P2 拒绝（§10）。
// expect-error: Value-type layout cycle: Box -> Box
pub struct Box {
    pub var next: Box
    pub init(_ -> next)
}
pub func main(): i32 {
    return 0
}
