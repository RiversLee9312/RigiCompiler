// capability 没有用户可调用的构造入口。
// expect-error: init
import core.messaging.*
import core.serialization.Serializable
@Serializable
pub shared class Msg { pub init() }
pub func main(): i32 {
    const forged = new QueueHandle\<Msg>()
    return 0
}
