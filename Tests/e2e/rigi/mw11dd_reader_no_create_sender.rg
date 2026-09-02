// MW11d-D（负例，交接 §16.1）：单向 capability——Reader 无任何派生
// Sender 的 API（结构性保证：无相应方法，编译期拒绝）。
// expect-error: createSender
import core.serialization.Serializable
import core.messaging.Messenger

@Serializable
pub shared class Msg {
    pub var n: i32
    pub init(_ -> n)
}

pub func main(): i32 {
    const msgr = new Messenger\<Msg>()
    const reader = msgr.createReader()
    const bad = reader.createSender()
    msgr.dispose()
    reader.dispose()
    return 0
}
