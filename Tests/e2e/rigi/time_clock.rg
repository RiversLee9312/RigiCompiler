// ============================================================================
// time_clock.rg —— 施工块 6-3（STDLIB §4.9.4 / §4.9.5，D6）
// core.time 单调时钟 / MonotonicInstant / Stopwatch：VM 与 native 双宿主
// 对拍（NativeE2E「单调时钟对拍」Case 复用本语料）。
//   ① MonotonicClock.now() 顺序采样不倒退（允许相等；实际分辨率平台
//      相关，契约不要求每次读取增加一纳秒）；两次采样差为非负 TimeSpan；
//      反向求差为非正；仅确实前进时为负 TimeSpan（TimeSpan 有符号表示）。
//   ② MonotonicInstant 按读数 compareTo/equals；不提供 Serializable
//      （§4.9.5 注释级声明，不做运行时断言）；不能转日期/不当 Unix
//      时间戳（类型面即约束，无对应 API）。
//   ③ Stopwatch：初始停止且为零；start 对运行中、stop 对已停止均幂等；
//      start→sleep→stop 累计 ≥ 睡眠量级；再次 start 继续累计不丢前段；
//      reset 清零并停止；restart 清零并开始；运行中 elapsed 包含当前
//      区间且随等待增长。
//   ④ 跨 Worker：ComputeExecutor 上经 sleep 挂起/恢复后再读单调时钟，
//      与挂起前比较不倒退（同一进程时钟域，§4.9.4）。
// expect-output: hop-ok
// expect-output: clock-ok
// expect-exit: 0
// ============================================================================
import core.time.*
import core.coroutine.*
import core.io.Console

// 失败计数（0 = 通过）；失败时打印名字定位（expect-output 只钉通过形态）
func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

// ④ 跨 Worker：sleep 挂起（不占 Worker）后恢复再读；same-process 时钟
// 域内不得倒退
async func readAfterHop(before: MonotonicInstant): i32 {
    yield sleep(20)
    const after = MonotonicClock.now()
    if (after.compareTo(before) is .LesserThanAnother) { return 1 }
    return 0
}

pub func main(): i32 {
    var fails = 0

    // ---- ① 顺序采样不倒退 + 非负差 ----
    var prev = MonotonicClock.now()
    var i = 0
    while (i < 512) {
        const cur = MonotonicClock.now()
        if (cur.compareTo(prev) is .LesserThanAnother) {
            fails = fails + check("mono-backward", false)
        }
        prev = cur
        i += 1
    }
    const s1 = MonotonicClock.now()
    const s2 = MonotonicClock.now()
    const zero = TimeSpan.fromNanoseconds(0L)
    const order = s2.compareTo(s1)
    const forward = (s2 - s1).compareTo(zero)
    const reverse = (s1 - s2).compareTo(zero)
    fails = fails + check("mono-diff-nonneg", not (forward is .LesserThanAnother))
    // 为什么不强制每次严格增长：时钟分辨率有限，顺序读数可相等；
    // 相等时双向差必须精确为零，确实前进时才要求正差/负差。
    fails = fails + check("mono-diff-nonpos", not (reverse is .GreaterThanAnother))
    if (order is .Equal) {
        fails = fails + check("mono-diff-equal-forward", forward is .Equal)
        fails = fails + check("mono-diff-equal-reverse", reverse is .Equal)
    } else {
        fails = fails + check("mono-diff-forward-positive", forward is .GreaterThanAnother)
        fails = fails + check("mono-diff-reverse-negative", reverse is .LesserThanAnother)
    }
    // 同一读数构造必现的相等形态，避免平台高分辨率下仅覆盖前进分支。
    fails = fails + check("mono-diff-self-zero", (s1 - s1) == zero)

    // ---- ② MonotonicInstant 比较与判等 ----
    fails = fails + check("mi-eq-self", s1 == s1)
    fails = fails + check("mi-eq-reflex", s1.compareTo(s1) is .Equal)
    fails = fails + check("mi-order", not (order is .LesserThanAnother))

    // ---- ③ Stopwatch 语义 ----
    const w = new Stopwatch()
    fails = fails + check("sw-init-stopped", not w.isRunning)
    fails = fails + check("sw-init-zero",
        w.elapsed == TimeSpan.fromNanoseconds(0L))
    // start 幂等（运行中再 start 不改变状态/不丢区间）
    w.start()
    w.start()
    fails = fails + check("sw-start-running", w.isRunning)
    yield sleep(20)
    w.stop()
    w.stop()
    fails = fails + check("sw-stop-idempotent", not w.isRunning)
    // 累计 ≥ 睡眠量级（20ms 定时器只晚不早；留 5ms 抖动余量）
    fails = fails + check("sw-elapsed-ge-slept",
        w.elapsed.totalMilliseconds >= 15L)
    const e1 = w.elapsed
    // 再次 start 继续累计（不丢前段）
    w.start()
    yield sleep(10)
    w.stop()
    fails = fails + check("sw-accumulate",
        w.elapsed.totalMilliseconds >= (e1.totalMilliseconds + 5L))
    // reset 清零并停止
    w.reset()
    fails = fails + check("sw-reset-stopped", not w.isRunning)
    fails = fails + check("sw-reset-zero",
        w.elapsed == TimeSpan.fromNanoseconds(0L))
    // restart 清零并开始；运行中 elapsed 包含当前区间且随等待增长
    w.restart()
    fails = fails + check("sw-restart-running", w.isRunning)
    const r1 = w.elapsed
    yield sleep(10)
    const r2 = w.elapsed
    fails = fails + check("sw-running-grows",
        r2.totalMilliseconds >= (r1.totalMilliseconds + 5L))

    // ---- ④ 跨 Worker（ComputeExecutor 上挂起/恢复） ----
    const hopBefore = MonotonicClock.now()
    const hopTask = new Task\<i32>(func{async (): i32 -> await readAfterHop(hopBefore)})
    hopTask.run(new ComputeExecutor())
    const hopFails = await (hopTask as Task\<i32>)
    if (hopFails == 0) {
        Console.println("hop-ok")
    } else {
        fails = fails + check("mono-hop-backward", false)
    }

    if (fails > 0) {
        Console.println("time-clock-FAIL count=${fails}")
        return 1
    }
    Console.println("clock-ok")
    return 0
}
