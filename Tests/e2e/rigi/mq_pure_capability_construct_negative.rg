// MessageQueue 是私有实现，不能由用户直接构造。
// expect-error: inaccessible due to its accessibility level
import core.messaging.*
import core.serialization.Serializable
@Serializable
pub shared class Msg { pub init() }
pub func main(): i32 {
    const forged = new MessageQueue\<Msg>()
    return 0
}
