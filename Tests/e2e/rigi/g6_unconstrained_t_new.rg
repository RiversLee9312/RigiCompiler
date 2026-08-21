// bug g6（负例）：无约束零参 T() —— 最大基类 Any 无零参 init，
// 编译期判定拒绝（§3.7）。
// expect-error: 'T()' has no such method: unconstrained type parameter resolves to Any
func make\<T>(): T { return T() }
pub func main(): i32 {
    return 0
}
