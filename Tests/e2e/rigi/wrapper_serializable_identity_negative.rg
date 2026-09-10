// 用户同名 Entity wrapper 不能借 SB 满足其 with 约束。
// expect-error: does not satisfy the 'With Serializable'
@WrapperTarget(.Entity)
wrapper Serializable { pub init() }
func take\<T with Serializable>(value: T) { }
pub func main(): i32 {
    take\<i32>(7)
    return 0
}
