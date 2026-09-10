// extends 保证不能用来冒充 supers 保证。
// expect-error: does not satisfy the 'Supers Object'
func consume\<T supers Object>(value: T) {}
func forward\<T extends Object>(value: T) { consume\<T>(value) }
pub func main(): i32 { return 0 }
