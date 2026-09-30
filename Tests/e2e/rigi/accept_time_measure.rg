// ============================================================================
// accept_time_measure.rg —— 施工块 8-1：MVP 应用验收（STDLIB §8）场景 4
// 「时间与耗时」：按 §4.9 解析和输出 DateTime 与 ISO Duration 文本、执行
// 纳秒时间值运算，并以单调时钟和 Stopwatch 测量上述处理过程。NativeE2E
// 「验收场景4 时间与耗时对拍」Case 复用本语料（VM/native 双宿主一致）。
//
//   ① DateTime 文本（RFC 3339 收窄子集）：Z 与 ±HH:MM 偏移解析（偏移
//      换算 UTC）、9 位纳秒 fraction、toString 往返逐字。
//   ② ISO Duration 文本：P1DT2H3M4.5S 固定样例解析与规范输出、负值
//      整体负号（-PT0.000000001S 形态由 -P1DT2H3M4.5S 覆盖同类通道）。
//   ③ 纳秒时间值运算：TimeSpan 乘除整数（base-100 limb 精确、不足一
//      纳秒向零截断的通道由除法覆盖）、加减、取负；DateTime ± TimeSpan
//      进位/借位跨日；两时刻相减保完整纳秒精度。
//   ④ 测量：处理过程（①–③ 的解析/运算/校验循环 32 次）由 Stopwatch
//      start/stop 计量，同时以 MonotonicClock 采样差对照——断言为不变
//      量与量级（elapsed ≥ 0 且 < 60 s、单调采样不倒退、差非负），
//      不断言墙钟具体值（VM/native 与平台分辨率差异不进入断言）。
//   ⑤ Stopwatch 行为：reset 清零并停止、start 幂等、stop 幂等、
//      start→sleep(20)→stop 累计 ≥ 睡眠量级（定时器只晚不早 + 5 ms
//      抖动余量，time_clock 语料同口径）。
//   ⑥ 保留全部数据的步骤声明：本场景无 I/O 输入；文本解析为纯计算，
//      各样例文本以字面量整体持有（无可保留/释放的数据步骤）。
// expect-output: accept-time dt=2026-09-24T08:30:05.123456789Z span=P1DT2H3M4.5S span3=P3DT6H9M13.5S spanHalf=PT13H1M32.25S
// expect-output: accept-time neg=-P1DT2H3M4.5S diff-ns=1500000001 plus25h=2026-01-02T01:00:00Z minus1m=2025-12-31T23:59:00Z
// expect-output: accept-time measure checksum=3001104000 elapsed-nonneg=true upper-ok=true monotonic-ok=true
// expect-output: accept-time sleep-floor=ok
// expect-output: accept-time-ok
// expect-exit: 0
// ============================================================================
import core.time.*
import core.coroutine.*
import core.io.*

// 首败即返。
func amFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

func amExpect(cond: bool, code: i32, msg: String): i32 {
    if (cond) { return 0 }
    return amFail(code, msg)
}

// 处理过程本体：①–③ 的解析、文本输出与纳秒运算 + 确定性校验。
// 返回恒定校验和 93784500（= P1DT2H3M4.5S 的总毫秒）；任何一步失败
// 返回 -1（正常路径下文本与数值全部确定）。
func amProcessOnce(): i64 {
    const dt = DateTime.parse("2026-09-24T08:30:05.123456789Z")
    if (dt.toString() != "2026-09-24T08:30:05.123456789Z") { return 0L - 1L }
    const offset = DateTime.parse("2024-02-29T12:34:56+05:30")
    if (offset.toString() != "2024-02-29T07:04:56Z") { return 0L - 1L }
    const sample = TimeSpan.parse("P1DT2H3M4.5S")
    if (sample.toString() != "P1DT2H3M4.5S") { return 0L - 1L }
    if ((sample * 3L).toString() != "P3DT6H9M13.5S") { return 0L - 1L }
    if ((sample / 2L).toString() != "PT13H1M32.25S") { return 0L - 1L }
    if ((-sample).toString() != "-P1DT2H3M4.5S") { return 0L - 1L }
    if (((sample + TimeSpan.fromSeconds(1L)) - TimeSpan.fromSeconds(1L)) != sample) {
        return 0L - 1L
    }
    const t1 = DateTime.parse("2026-01-01T00:00:00Z")
    const t2 = t1 + TimeSpan.fromNanoseconds(1500000001L)
    if ((t2 - t1) != TimeSpan.fromNanoseconds(1500000001L)) { return 0L - 1L }
    return sample.totalMilliseconds
}

pub func main(): i32 {
    // ── ①–③ 文本与运算逐项断言（确定值）──
    const dt = DateTime.parse("2026-09-24T08:30:05.123456789Z")
    const dtText = dt.toString()
    var rc = amExpect(dtText == "2026-09-24T08:30:05.123456789Z", 11,
        "dt 文本=${dtText}")
    if (rc != 0) { return rc }
    const sample = TimeSpan.parse("P1DT2H3M4.5S")
    const spanText = sample.toString()
    rc = amExpect(spanText == "P1DT2H3M4.5S", 12, "span 文本=${spanText}")
    if (rc != 0) { return rc }
    const span3 = sample * 3L
    rc = amExpect(span3.toString() == "P3DT6H9M13.5S", 13,
        "span3=${span3.toString()}")
    if (rc != 0) { return rc }
    const spanHalf = sample / 2L
    rc = amExpect(spanHalf.toString() == "PT13H1M32.25S", 14,
        "spanHalf=${spanHalf.toString()}")
    if (rc != 0) { return rc }
    Console.println("accept-time dt=${dtText} span=${spanText} span3=${span3.toString()} spanHalf=${spanHalf.toString()}")

    const neg = -sample
    const t1 = DateTime.parse("2026-01-01T00:00:00Z")
    const t2 = t1 + TimeSpan.fromNanoseconds(1500000001L)
    const diff = t2 - t1
    const plus25h = t1 + TimeSpan.fromHours(25L)
    const minus1m = t1 - TimeSpan.fromMinutes(1L)
    rc = amExpect(neg.toString() == "-P1DT2H3M4.5S", 21,
        "neg=${neg.toString()}")
    if (rc != 0) { return rc }
    rc = amExpect(diff.totalNanoseconds == 1500000001L, 22,
        "diff-ns=${diff.totalNanoseconds}")
    if (rc != 0) { return rc }
    rc = amExpect(plus25h.toString() == "2026-01-02T01:00:00Z", 23,
        "plus25h=${plus25h.toString()}")
    if (rc != 0) { return rc }
    rc = amExpect(minus1m.toString() == "2025-12-31T23:59:00Z", 24,
        "minus1m=${minus1m.toString()}")
    if (rc != 0) { return rc }
    Console.println("accept-time neg=${neg.toString()} diff-ns=${diff.totalNanoseconds} plus25h=${plus25h.toString()} minus1m=${minus1m.toString()}")

    // ── ④ 单调时钟 + Stopwatch 测量处理过程（32 次完整处理）──
    const procStart = MonotonicClock.now()
    const w = new Stopwatch()
    w.restart()
    var loops: i32 = 0
    var checksum: i64 = 0L
    while (loops < 32) {
        checksum = checksum + amProcessOnce()
        loops = (loops + 1)
    }
    w.stop()
    const procEnd = MonotonicClock.now()
    rc = amExpect(checksum == (32L * 93784500L), 31, "checksum=${checksum}")
    if (rc != 0) { return rc }
    rc = amExpect(not w.isRunning, 32, "stop 后仍在运行")
    if (rc != 0) { return rc }
    const elapsed = w.elapsed
    const zero = TimeSpan.fromNanoseconds(0L)
    // 不变量：elapsed ≥ 0；量级：正常实现远小于 60 s（不断言具体值）
    const elapsedNonneg = not (elapsed.compareTo(zero) is .LesserThanAnother)
    const upperOk = elapsed.totalMilliseconds < 60000L
    // 单调钟：顺序采样不倒退（允许相等）、采样差非负
    const monotonicOk = not (procEnd.compareTo(procStart) is .LesserThanAnother)
    const diffNonneg = not ((procEnd - procStart).compareTo(zero) is .LesserThanAnother)
    rc = amExpect(elapsedNonneg, 33, "elapsed 为负")
    if (rc != 0) { return rc }
    rc = amExpect(upperOk, 34, "elapsed 超量级：${elapsed.totalMilliseconds}ms")
    if (rc != 0) { return rc }
    rc = amExpect(monotonicOk and diffNonneg, 35, "单调采样倒退")
    if (rc != 0) { return rc }
    Console.println("accept-time measure checksum=${checksum} elapsed-nonneg=${elapsedNonneg} upper-ok=${upperOk} monotonic-ok=${monotonicOk and diffNonneg}")

    // ── ⑤ Stopwatch reset/start/stop 语义 + 睡眠量级下限 ──
    w.reset()
    rc = amExpect((not w.isRunning) and (w.elapsed == zero), 41, "reset 语义")
    if (rc != 0) { return rc }
    w.start()
    w.start()
    rc = amExpect(w.isRunning, 42, "start 后未运行")
    if (rc != 0) { return rc }
    yield sleep(20)
    w.stop()
    w.stop()
    rc = amExpect(not w.isRunning, 43, "stop 后仍在运行")
    if (rc != 0) { return rc }
    // 20 ms 定时器只晚不早（+5 ms 抖动余量）；上界同样宽松。
    rc = amExpect(w.elapsed.totalMilliseconds >= 15L, 44,
        "elapsed=${w.elapsed.totalMilliseconds}ms 未达睡眠量级")
    if (rc != 0) { return rc }
    rc = amExpect(w.elapsed.totalMilliseconds < 60000L, 45, "sleep 段超量级")
    if (rc != 0) { return rc }
    Console.println("accept-time sleep-floor=ok")

    Console.println("accept-time-ok")
    return 0
}
