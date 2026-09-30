// e2e-slow-gate: 并发消息压力用例（多读者并发 send/await 深复制往返，
// 解释执行下净 VM 约 73 秒）。裁剪会失去并发交错判别力；
// RIGI_E2E_SLOW=1 时并入默认跑。
// expect-output: mq-concurrent-ok
import core.messaging.*
import core.coroutine.*
import core.collections.*
import core.serialization.Serializable
import core.io.Console

@Serializable
pub shared class Message {
    pub var producer: i32
    pub var sequence: i32
    pub init(_ -> producer, _ -> sequence)
}

async func produce(queue: Messenger\<Message>, id: i32) {
    const message = new Message(id, 0)
    var i = 0
    while (i < 96) {
        message.sequence = i
        await queue.send(message)
        // send 的接受点之后修改源；读者必须看到入队快照。
        message.sequence = -1
        yield
        if ((i & 15) == 0) { yield sleep(1) }
        i = i + 1
    }
}

async func consume(reader: Reader\<Message>, slow: bool): i32 {
    const sequences = arrayOf\<i32>(4)
    var i = 0
    while (i < 4) { sequences[i] = -1
        i = i + 1 }
    var count = 0
    var checksum = 0
    while (true) {
        const item = await reader.next()
        if (item.isEos) { break }
        const message = (item.item as Message)
        if ((message.producer < 0) or (message.producer >= 4)) {
            throw new core.RuntimeException("非法生产者")
        }
        const previous = (sequences[message.producer] as i32)
        if (message.sequence != (previous + 1)) {
            throw new core.RuntimeException("广播乱序、丢失、重复或快照别名")
        }
        sequences[message.producer] = message.sequence
        checksum = checksum + ((message.producer * 1000) + message.sequence)
        count = count + 1
        if (slow and ((count & 15) == 0)) { yield sleep(1) }
    }
    if (count != 384) { throw new core.RuntimeException("EOS 丢失积压") }
    i = 0
    while (i < 4) {
        if ((sequences[i] as i32) != 95) { throw new core.RuntimeException("流不完整") }
        i = i + 1
    }
    reader.dispose()
    return checksum
}

pub func main(): i32 {
    const queue = new Messenger\<Message>()
    const fast = queue.createReader()
    const slow = fast.branch()
    const first = new Task\<i32>(func{async (): i32 -> await consume(fast, false)})
    const second = new Task\<i32>(func{async (): i32 -> await consume(slow, true)})
    first.run(new ComputeExecutor())
    second.run(new IOExecutor())
    const producers = arrayOf\<Task>(4)
    var i = 0
    while (i < 4) {
        const id = i
        const producer = new Task(func{async () -> { await produce(queue, id) }})
        producer.run(new ComputeExecutor())
        producers[i] = producer
        i = i + 1
    }
    i = 0
    while (i < 4) { await (producers[i] as Task)
        i = i + 1 }
    queue.dispose()
    if (((await first) != 594240) or ((await second) != 594240)) {
        throw new core.RuntimeException("广播校验和不一致")
    }
    Console.println("mq-concurrent-ok")
    return 0
}
