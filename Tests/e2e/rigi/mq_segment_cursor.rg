// e2e-slow-gate: 跨段游标压力用例（段容量 64 的边界向量 65/129/194，
// 解释执行下净 VM 约 25 秒）。判别力在段边界计数，不可裁剪；
// RIGI_E2E_SLOW=1 时并入默认跑。
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
func expect(reader: Reader\<Message>, value: i32) {
    const item = await reader.next()
    if (item.isEos or ((item.item as Message).value != value)) {
        throw new core.RuntimeException("跨段消息序号错误")
    }
}
pub func main(): i32 {
    const owner = new Messenger\<Message>()
    const sender = owner
    const fast = owner.createReader()
    const slow = owner.createReader()
    var i: i32 = 0
    while (i < 65) { await sender.send(new Message(i))
        i = i + 1 }
    i = 0
    while (i < 64) { expect(fast, i)
        i = i + 1 }
    const branch = owner.createReader()
    i = 65
    while (i < 129) { await sender.send(new Message(i))
        i = i + 1 }
    slow.dispose()
    expect(fast, 64)
    i = 65
    while (i < 129) { expect(fast, i)
        expect(branch, i)
        i = i + 1 }
    // 全部 drain 后继续发布，读者缓存必须切到新段，不能保留断开的旧尾。
    while (i < 194) { await sender.send(new Message(i))
        expect(fast, i)
        expect(branch, i)
        i = i + 1 }
    sender.dispose()
    if (not (await fast.next()).isEos) { throw new core.RuntimeException("缺少EOS") }
    if (not (await branch.next()).isEos) { throw new core.RuntimeException("分支缺少EOS") }
    fast.dispose()
    branch.dispose()
    Console.println("mq-segment-cursor-ok")
    return 0
}
