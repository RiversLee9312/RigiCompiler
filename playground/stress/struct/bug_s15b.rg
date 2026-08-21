// bug s15b：自包含非 rich struct —— 布局无限大，编译期应拒绝（§10 布局环）
pub struct Box {
    pub var next: Box
    pub init(_ -> next)
}

pub func main(): i32 {
    return 0
}
