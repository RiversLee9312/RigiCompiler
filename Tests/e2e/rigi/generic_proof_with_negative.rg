// 声明处就必须证明转发约束，即使该泛型函数尚未调用。
// expect-error: does not satisfy the 'With Serializable'
namespace application
@WrapperTarget(.Entity)
wrapper SerializationBase { pub init() }
func consume\<T with core.serialization.Serializable>(value: T) {}
func forward\<T with application.SerializationBase>(value: T) { consume\<T>(value) }
pub func main(): i32 { return 0 }
