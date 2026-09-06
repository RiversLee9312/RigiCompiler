// 普通 dispose 状态机在恢复 .this 后仍须登记进入；零/一元回调布局共存。
// expect-output: disposed
import core.messaging.*
import core.coroutine.*
import core.serialization.Serializable
import core.io.Console
@Serializable
pub shared class Msg {
    pub var id: i32 = 0
}
pub shared class Listener : core.AsyncAction\<Msg> {
    pub override async operator call(message: Msg) {}
}
pub shared class Disposer implements core.IDisposable {
    pub override func dispose() { yield sleep(1) }
}
pub func main(): i32 {
    const messenger = new Messenger\<Msg>()
    const receiver = messenger.receiver
    const reader = messenger.createReader()
    receiver.addListener(new Listener())
    receiver.dispose()
    reader.dispose()
    messenger.dispose()
    const disposer = new Disposer()
    disposer.dispose()
    Console.println("disposed")
    return 0
}
