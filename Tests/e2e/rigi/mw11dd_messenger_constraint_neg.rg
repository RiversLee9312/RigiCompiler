// MW11d-D（负例，交接 §3/§28）：Messenger 的 TMessage 必须
// with Serializable——未标 @Serializable 的类型编译期拒绝。
// expect-error: does not satisfy the 'With Serializable'
import core.messaging.Messenger

pub shared class Plain {
    pub var n: i32
    pub init(_ -> n)
}

pub func main(): i32 {
    const msgr = new Messenger\<Plain>()
    msgr.dispose()
    return 0
}
