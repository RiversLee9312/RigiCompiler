// expect-output: parked-drain-ok
// 3b-δ2 对拍：属主协程 park（EventAlarm 停靠，无自重排 carriage）期间，
// 他协程（Compute Worker）释放最后一份 capability → 归零转移把 shellID
// 压入属主挂起栈（发布者线程只减量/压栈，不清壳——3b-β 的发布者线程
// 同步清理已随 δ2 封口）；属主被 sleep 定时器门控唤醒后的恢复槽段前
// 消化（延迟有界 = 定时器间隔），随后同段内高频 expose→归零（200 轮
// 同段压栈、段尾一次消化），壳创建/归零/清理全周期无观测差异。
// memtrack 零泄漏口径由 native 台账验收；VM 无壳计数通道，行为同语料
// 对拍。
import core.collections.*
import core.coroutine.*
import core.io.Console
shared class ParkBox {
    pub var v: i32 = 31
}
async func parkedOwner(sink: Array\<Handle\<ParkBox>>): i32 {
    const box = new ParkBox()
    box.v = 31
    unsafe seq using(const place = placeOf box) {
        // 唯一 capability 存入 sink；属主自身零持有（归零转移可由
        // 他线程单独触发）
        sink[0] = place.expose()
    }
    // park 在 EventAlarm 通道：挂起不自带 carriage，恢复被定时器门控
    yield sleep(60)
    // 恢复段：唤醒点即属主执行槽（挂起栈在此消化）；本段再压 200 轮
    // 段内归零，段尾一次排空
    var i: i32 = 0
    while (i < 200) {
        unsafe seq using(const p = placeOf box) {
            const h = p.expose()
            if (h.load().v != 31) { throw new core.RuntimeException("段内 load 值错误") }
        }
        i = i + 1
    }
    return 0
}
async func lateReleaser(sink: Array\<Handle\<ParkBox>>): i32 {
    const h = sink[0] as Handle\<ParkBox>
    unsafe seq {
        if (h.load().v != 31) { throw new core.RuntimeException("park 期间 load 值错误") }
    }
    // 返回即最后一份 capability 在非属主协程释放 → 归零转移入属主
    // 挂起栈（属主此刻 park 中）
    return 0
}
pub func main(): i32 {
    const sink = arrayOf\<Handle\<ParkBox>>(1)
    const owner = new Task\<i32>(func{async (): i32 -> await parkedOwner(sink)})
    owner.run(new ComputeExecutor())
    // 让属主先 expose 并 park（60ms 睡眠内完成 releaser 全程）
    yield sleep(20)
    const releaser = new Task\<i32>(func{async (): i32 -> await lateReleaser(sink)})
    releaser.run(new ComputeExecutor())
    if ((await releaser) != 0) { throw new core.RuntimeException("releaser 失败") }
    if ((await owner) != 0) { throw new core.RuntimeException("owner 失败") }
    Console.println("parked-drain-ok")
    return 0
}
