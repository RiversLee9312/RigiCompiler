// 无约束型参不能凭尚未具化而获得子类型保证。
// expect-error: does not satisfy the 'Extends Object'
func consume\<T extends Object>(value: T) {}
func forward\<T>(value: T) { consume(value) }
pub func main(): i32 { return 0 }
