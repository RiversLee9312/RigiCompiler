// 私有队列状态不能由用户读取、写回或复活。
// expect-error: inaccessible due to its accessibility level
import core.messaging.*
import core.serialization.Serializable
@Serializable
pub shared class Msg { pub init() }
pub func main(): i32 {
    const owner = new Messenger\<Msg>()
    const other = new Messenger\<Msg>()
    owner.dispose()
    owner.queue = other.queue
    return 0
}
