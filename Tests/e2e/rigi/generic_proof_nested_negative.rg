// 不变泛型构造不得因某个叶子尚未具化而跳过精确检查。
// expect-error: does not satisfy the 'Extends Container<Object>'
class Container\<T> { pub init() }
func consume\<T extends Container\<Object>>(value: T) {}
func forward\<T>(value: Container\<T>) { consume(value) }
pub func main(): i32 { return 0 }
