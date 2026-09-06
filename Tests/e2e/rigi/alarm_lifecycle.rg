// expect-output: alarm-lifecycle-ok
import core.coroutine.*
import core.io.Console
pub shared class Gate : EventAlarm {
    pub var next: Gate?
    pub func fire() { signal() }
}
pub shared class Retained {
    pub static var alarm: EventAlarm?
}
func exercise() {
    const late = sleep(1)
    yield sleep(3)
    yield late
    yield late
    const finite = new Timer(1L, .Repeat(2))
    yield finite
    yield finite
    yield finite
    const infinite = new Timer(1L, .InfiniteRepeat)
    yield infinite
    yield infinite
    const gate = new Gate()
    gate.fire()
    yield gate
    gate.fire()
    yield gate
    // 环使内部析构也经过 macroGC；不能在该回调中重入 GC fence。
    gate.next = gate
}
func crossOwner() {
    const task = new Task\<EventAlarm>(func{async (): EventAlarm -> sleep(1)})
    task.run(new ComputeExecutor())
    const alarm = await task
    yield alarm
    // 返回后由主 Worker 释放结果，底座必须交回创建它的 Compute Worker。
}
pub func main(): i32 {
    var i: i32 = 0
    while (i < 40) { exercise()
        crossOwner()
        i = i + 1 }
    // shutdown 先清扫底座，随后静态槽析构必须安全忽略失效身份。
    Retained.alarm = sleep(1)
    Console.println("alarm-lifecycle-ok")
    return 0
}
