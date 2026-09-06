// native 专用：已观察失败 Task 死亡后，节点和异常都应回收。
import core.coroutine.*
import core.io.Console
@NativeLibrary("rigi_rt")
@NativeSymbol("mem_live_bytes")
priv native func liveBytes(): i64
async func fail(): i32 { throw new core.RuntimeException("expected") }
pub class LinkedFailure : core.RuntimeException {
    pub var task: Task\<i32>?
    pub init() { message = "cycle" }
}
async func linkedFail(): i32 { throw new LinkedFailure() }
func once() {
    const task = fail()
    try { await task } catch (e: core.RuntimeException) {}
    try { await task } catch (e: core.RuntimeException) {}
    const linked = linkedFail()
    try { await linked } catch (e: LinkedFailure) { e.task = linked }
}
pub func main(): i32 {
    var warm: i32 = 0
    while (warm < 100) { once()
        warm = warm + 1 }
    yield sleep(10)
    const baseline = liveBytes()
    var round: i32 = 0
    while (round < 4) {
        var i: i32 = 0
        while (i < 100) { once()
            i = i + 1 }
        yield sleep(10)
        if (liveBytes() > (baseline + (4096 as i64))) {
            throw new core.RuntimeException("已观察失败节点积累")
        }
        round = round + 1
    }
    Console.println("failure-resources-ok")
    return 0
}
