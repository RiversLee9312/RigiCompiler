// MW7b 负例：SharedSpan<含 local class 引用的 rich struct> 违反元素约束
// expect-error: 不满足 SharedSpan 的元素约束
class LocalUser {
    pub const name: String
    pub init(_ -> name)
}
rich struct Rec {
    pub var owner: LocalUser
    pub init(_ -> owner)
}
pub func take(s: SharedSpan\<Rec>): i32 {
    return 0
}
pub func main(): i32 {
    return 0
}
