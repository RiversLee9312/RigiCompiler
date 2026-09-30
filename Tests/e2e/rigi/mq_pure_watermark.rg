// e2e-slow-gate: 水位/compact 压力用例（600 条积压 + 慢读者钉住前段的
// compact 往返，解释执行下净 VM 约 57 秒）。600 条积压是水位回收的
// 判别规模，不可裁剪；RIGI_E2E_SLOW=1 时并入默认跑。
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
    const owner = new Messenger\<Msg>()
    const sender = owner
    const fast = owner.createReader()
    const slow = owner.createReader()
    var i: i32 = 0
    while (i < 600) {
        await sender.send(new Msg(i))
        i = i + 1
    }
    var sum: i32 = 0
    i = 0
    while (i < 600) {
        const item = await fast.next()
        sum = sum + (item.item as Msg).n
        i = i + 1
        // 部分compact保留400..599；继续drain又覆盖全部清空。
        if (i == 400) { slow.dispose() }
    }
    const branch = fast.branch()
    await sender.send(new Msg(600))
    const a = await fast.next()
    const b = await branch.next()
    (a.item as Msg).n = -1
    Console.println(sum.toString())
    Console.println((b.item as Msg).n.toString())
    sender.dispose()
    Console.println((await fast.next()).isEos.toString())
    Console.println((await branch.next()).isEos.toString())
    fast.dispose()
    branch.dispose()
    return 0
}
