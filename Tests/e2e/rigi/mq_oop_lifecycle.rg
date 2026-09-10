// expect-output: mq-lifecycle-ok
import core.messaging.*
import core.coroutine.*
import core.serialization.Serializable
import core.io.Console

@Serializable
pub shared class Message {
    pub var value: i32
    pub var text: String
    pub init(_ -> value, _ -> text)
}

func expect(reader: Reader\<Message>, value: i32) {
    const item = await reader.next()
    if (item.isEos) { throw new core.RuntimeException("提前 EOS") }
    const message = (item.item as Message)
    if ((message.value != value) or (message.text != "快照🙂")) {
        throw new core.RuntimeException("消息顺序或深复制损坏")
    }
}

func expectEos(reader: Reader\<Message>) {
    const item = await reader.next()
    if ((not item.isEos) or (item.item != null)) {
        throw new core.RuntimeException("EOS 载荷错误")
    }
}

pub func main(): i32 {
    const messenger = new Messenger\<Message>()
    // 无读者的消息不回放；快慢读者跨多个段独立推进。
    await messenger.send(new Message(-1, "丢弃"))
    const fast = messenger.createReader()
    const slow = messenger.createReader()
    const source = new Message(0, "快照🙂")
    var i = 0
    while (i < 257) {
        source.value = i
        await messenger.send(source)
        i = i + 1
    }
    source.value = -10
    source.text = "已修改"
    i = 0
    while (i < 129) { expect(fast, i)
        i = i + 1 }
    i = 0
    while (i < 65) { expect(slow, i)
        i = i + 1 }
    const branch = fast.branch()
    // 移除最慢读者触发前缀回收；新订阅只能看到 257 以后的消息。
    slow.dispose()
    slow.dispose()
    var rejected = false
    try { const invalid = slow.branch() }
    catch (e: core.IllegalStateException) { rejected = true }
    if (not rejected) { throw new core.RuntimeException("已释放 Reader 可派生") }
    i = 257
    while (i < 386) {
        await messenger.send(new Message(i, "快照🙂"))
        i = i + 1
    }
    i = 129
    while (i < 386) { expect(fast, i)
        i = i + 1 }
    i = 257
    while (i < 386) { expect(branch, i)
        i = i + 1 }
    // 日志完全排空后复用，反复交错 signal-before-await 与挂起唤醒。
    i = 386
    while (i < 418) {
        const pending = fast.next()
        yield
        await messenger.send(new Message(i, "快照🙂"))
        const result = await pending
        if (result.isEos or ((result.item as Message).value != i)) {
            throw new core.RuntimeException("唤醒丢失或消息损坏")
        }
        expect(branch, i)
        i = i + 1
    }
    const cancelled = branch.next()
    yield
    branch.dispose()
    rejected = false
    try { const invalid = await cancelled }
    catch (e: core.IllegalStateException) { rejected = true }
    if (not rejected) { throw new core.RuntimeException("释放没有终止挂起 next") }
    // 保留一个 outstanding next，拒绝第二个，再用封存唤醒第一个。
    const pending = fast.next()
    yield
    rejected = false
    try { const invalid = await fast.next() }
    catch (e: core.IllegalStateException) { rejected = true }
    if (not rejected) { throw new core.RuntimeException("重复 next 未拒绝") }
    messenger.dispose()
    messenger.dispose()
    if (not (await pending).isEos) { throw new core.RuntimeException("封存没有唤醒") }
    expectEos(fast)
    const late = fast.branch()
    expectEos(late)
    late.dispose()
    rejected = false
    try { await messenger.send(new Message(419, "禁止")) }
    catch (e: core.IllegalStateException) { rejected = true }
    if (not rejected) { throw new core.RuntimeException("封存后可发送") }
    fast.dispose()
    // 已接受的积压在 Messenger.dispose 后仍可排空。
    const draining = new Messenger\<Message>()
    const reader = draining.createReader()
    i = 0
    while (i < 130) { await draining.send(new Message(i, "快照🙂"))
        i = i + 1 }
    draining.dispose()
    i = 0
    while (i < 130) { expect(reader, i)
        i = i + 1 }
    expectEos(reader)
    reader.dispose()
    Console.println("mq-lifecycle-ok")
    return 0
}
