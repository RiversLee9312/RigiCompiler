// 固定wake gate可重复等待；释放挂起reader必须唤醒错误出口。
// expect-output: 190
// expect-output: MessageQueue: Reader 已释放
// expect-output: Receiver.addListener：Receiver 已 dispose
// expect-output: getter released
// expect-output: getter released
import core.messaging.*
import core.serialization.Serializable
import core.coroutine.*
import core.io.Console
@Serializable
pub shared class Msg {
    pub var n: i32
    pub init(_ -> n)
}
pub func main(): i32 {
    const owner = new Messenger\<Msg>()
    const sender = owner
    const reader = owner.createReader()
    var i: i32 = 0
    var sum: i32 = 0
    while (i < 20) {
        const pending = reader.next()
        yield sleep(1)
        await sender.send(new Msg(i))
        const item = await pending
        sum = sum + (item.item as Msg).n
        i = i + 1
    }
    Console.println(sum.toString())
    const pending = reader.next()
    yield sleep(1)
    reader.dispose()
    try { await pending }
    catch (e: core.IllegalStateException) { Console.println(e.getMessage()) }
    sender.dispose()
    const messenger = new Messenger\<Msg>()
    const receiver = messenger.receiver
    receiver.dispose()
    const listener = func{async (m: Msg) -> {}}
    try { receiver.addListener(listener) }
    catch (e: core.IllegalStateException) { Console.println(e.getMessage()) }
    messenger.dispose()
    const disposed = new Messenger\<Msg>()
    disposed.dispose()
    i = 0
    while (i < 2) {
        try { const view = disposed.receiver }
        catch (e: core.IllegalStateException) { Console.println("getter released") }
        i = i + 1
    }
    return 0
}
