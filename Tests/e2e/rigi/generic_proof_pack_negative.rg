// 可变泛型实参的推断路径同样需要逐元素证明。
// expect-error: does not satisfy the 'With Serializable'
func consume\<T... with core.serialization.Serializable>(values: T...) {}
func forward\<T>(value: T) { consume(value) }
pub func main(): i32 { return 0 }
