// 固定wake gate可重复等待；释放挂起reader必须唤醒错误出口。
// expect-output: 190
// expect-output: MessageQueue: 句柄已释放或不存在
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
    const owner = MessageQueue.create_queue\<Msg>()
    const sender = MessageQueue.add_queue_handle(owner, QueueHandleType.Sender)
    const reader = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    var i: i32 = 0
    var sum: i32 = 0
    while (i < 20) {
        const pending = MessageQueue.next(reader)
        yield sleep(1)
        await MessageQueue.post(sender, new Msg(i))
        const item = await pending
        sum = sum + (item.item as Msg).n
        i = i + 1
    }
    Console.println(sum.toString())
    const pending = MessageQueue.next(reader)
    yield sleep(1)
    MessageQueue.release_queue_handle(reader)
    try { await pending }
    catch (e: core.IllegalStateException) { Console.println(e.getMessage()) }
    MessageQueue.release_queue_handle(sender)
    MessageQueue.release_queue_handle(owner)
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
