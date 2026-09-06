// expect-output: mq-segment-cursor-ok
// 慢 reader 钉住前段、快 reader 停在段边界、积压期间从尾部 branch。
import core.messaging.*
import core.serialization.Serializable
import core.io.Console
@Serializable
pub shared class Message {
    pub var value: i32
    pub init(_ -> value)
}
func expect(reader: QueueHandle\<Message>, value: i32) {
    const item = await MessageQueue.next(reader)
    if (item.isEos or ((item.item as Message).value != value)) {
        throw new core.RuntimeException("跨段消息序号错误")
    }
}
pub func main(): i32 {
    const owner = MessageQueue.create_queue\<Message>()
    const sender = MessageQueue.add_queue_handle(owner, QueueHandleType.Sender)
    const fast = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    const slow = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    var i: i32 = 0
    while (i < 65) { await MessageQueue.post(sender, new Message(i))
        i = i + 1 }
    i = 0
    while (i < 64) { expect(fast, i)
        i = i + 1 }
    const branch = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    i = 65
    while (i < 129) { await MessageQueue.post(sender, new Message(i))
        i = i + 1 }
    MessageQueue.release_queue_handle(slow)
    expect(fast, 64)
    i = 65
    while (i < 129) { expect(fast, i)
        expect(branch, i)
        i = i + 1 }
    // 全部 drain 后继续发布，读者缓存必须切到新段，不能保留断开的旧尾。
    while (i < 194) { await MessageQueue.post(sender, new Message(i))
        expect(fast, i)
        expect(branch, i)
        i = i + 1 }
    MessageQueue.release_queue_handle(owner)
    MessageQueue.release_queue_handle(sender)
    if (not (await MessageQueue.next(fast)).isEos) { throw new core.RuntimeException("缺少EOS") }
    if (not (await MessageQueue.next(branch)).isEos) { throw new core.RuntimeException("分支缺少EOS") }
    MessageQueue.release_queue_handle(fast)
    MessageQueue.release_queue_handle(branch)
    Console.println("mq-segment-cursor-ok")
    return 0
}
