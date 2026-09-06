// 慢读者阻挡水位；释放后批量compact不改变绝对cursor，branch从tail。
// expect-output: 179700
// expect-output: 600
// expect-output: true
// expect-output: true
import core.messaging.*
import core.serialization.Serializable
import core.io.Console
@Serializable
pub shared class Msg {
    pub var n: i32
    pub init(_ -> n)
}
pub func main(): i32 {
    const owner = MessageQueue.create_queue\<Msg>()
    const sender = MessageQueue.add_queue_handle(owner, QueueHandleType.Sender)
    const fast = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    const slow = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    var i: i32 = 0
    while (i < 600) {
        await MessageQueue.post(sender, new Msg(i))
        i = i + 1
    }
    var sum: i32 = 0
    i = 0
    while (i < 600) {
        const item = await MessageQueue.next(fast)
        sum = sum + (item.item as Msg).n
        i = i + 1
        // 部分compact保留400..599；继续drain又覆盖全部清空。
        if (i == 400) { MessageQueue.release_queue_handle(slow) }
    }
    const branch = MessageQueue.add_queue_handle(fast, QueueHandleType.Reader)
    await MessageQueue.post(sender, new Msg(600))
    const a = await MessageQueue.next(fast)
    const b = await MessageQueue.next(branch)
    (a.item as Msg).n = -1
    Console.println(sum.toString())
    Console.println((b.item as Msg).n.toString())
    MessageQueue.release_queue_handle(owner)
    MessageQueue.release_queue_handle(sender)
    Console.println((await MessageQueue.next(fast)).isEos.toString())
    Console.println((await MessageQueue.next(branch)).isEos.toString())
    MessageQueue.release_queue_handle(fast)
    MessageQueue.release_queue_handle(branch)
    return 0
}
