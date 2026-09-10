// 用户同名 wrapper 不能冒充 core.SerializationBase 获得序列化能力。
// expect-error: does not satisfy the 'With Serializable'
namespace application
@WrapperTarget(.Entity)
wrapper SerializationBase { pub init() }
func consume\<T with core.serialization.Serializable>(value: T) {}
@application.SerializationBase
class Forged { pub init() }
pub func main(): i32 {
    consume(new Forged())
    return 0
}
