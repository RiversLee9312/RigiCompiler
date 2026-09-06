// native 专用：台账不是 BIL VM 的语言接口。
import core.coroutine.*
import core.io.Console
@NativeLibrary("rigi_rt")
@NativeSymbol("timer_live_bytes")
priv native func timerBytes(): i64
pub shared class Gate : EventAlarm {
    pub func fire() { signal() }
}
func once() {
    const gate = new Gate()
    gate.fire()
    yield gate
    yield sleep(1)
    const task = new Task\<EventAlarm>(func{async (): EventAlarm -> sleep(1)})
    task.run(new ComputeExecutor())
    yield (await task)
}
pub func main(): i32 {
    const baseline = timerBytes()
    var round: i32 = 0
    while (round < 4) {
        var i: i32 = 0
        while (i < 100) { once()
            i = i + 1 }
        yield sleep(10)
        // 固定余量容纳当前挂起帧和异步 close，不能随轮次放宽。
        if (timerBytes() > (baseline + (8192 as i64))) {
            throw new core.RuntimeException("alarm底座持续积累")
        }
        round = round + 1
    }
    Console.println("alarm-resources-ok")
    return 0
}
