// expect-output: cross-release-ok
// 3b-β 对拍：Handle 跨协程传递且最后一次 capability 释放在非属主协程
// ——capability 析构经壳指针 fetch_sub（old==1 归零转移）投递释放消息，
// Dispatcher publishNative 分支同步归零清理（属主通道）；目标由壳锚
// 保活，memtrack 零泄漏口径由 native 台账验收。
import core.coroutine.*
import core.io.Console
shared class ShellBox {
    pub var v: i32 = 0
}
async func shellConsume(h: Handle\<ShellBox>): i32 {
    yield
    unsafe seq {
        if (h.load().v != 41) { throw new core.RuntimeException("跨协程 load 值错误") }
    }
    // 返回即 h 消亡：最后一份 capability 在非属主协程（Compute Worker）
    // 释放 → 归零转移走属主通道
    return 0
}
pub func main(): i32 {
    const box = new ShellBox()
    box.v = 41
    unsafe seq using(const place = placeOf box) {
        const handle = place.expose()
        const task = new Task\<i32>(func{async (): i32 -> await shellConsume(handle)})
        task.run(new ComputeExecutor())
        if ((await task) != 0) { throw new core.RuntimeException("Task 失败") }
        // 闭包持引用随 Task 终态消亡；此处壳已归零清理，壳锚释放后
        // box 仍由 main 局部变量持活——load 保活语义二次验收
        if (handle.load().v != 41) { throw new core.RuntimeException("归零后 load 值错误") }
    }
    Console.println("cross-release-ok")
    return 0
}
