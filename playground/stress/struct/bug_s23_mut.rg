// bug s23_mut：互递归非 rich struct —— A 内嵌 B、B 内嵌 A，布局环应编译期拒绝（§10）
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
