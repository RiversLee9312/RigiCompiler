// SB 在 core 可供公开签名引用；用户只能消费约束，不能应用能力。
// expect-exit: 0
func identity\<T with SerializationBase>(value: T): T { return value }
pub func main(): i32 {
    if (identity(17) != 17) { return 1 }
    if (identity("core") != "core") { return 2 }
    return 0
}
