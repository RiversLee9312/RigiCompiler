// STDLIB §4.9.4：DateTime.now 同一次 UTC 采样保留毫秒余纳秒。
// 不断言任意一次必非零（平台分辨率可使单次恰在毫秒边界）。
// expect-output: time-now-precision-ok
// expect-exit: 0
import core.time.*
import core.io.Console

pub func main(): i32 {
    const before = DateTime.now()
    const now = DateTime.now()
    const after = DateTime.now()
    if ((now.stamp.nanoseconds < 0) or (now.stamp.nanoseconds > 999999)) { return 1 }
    // Rigi 无运算符优先级：比较与算术连用必须括号化
    if (now.stamp.milliseconds < (before.stamp.milliseconds - 2000L)) { return 2 }
    if (now.stamp.milliseconds > (after.stamp.milliseconds + 2000L)) { return 3 }
    if ((now.year < 2020) or (now.year > 2100)) { return 4 }
    Console.println("time-now-precision-ok")
    return 0
}
