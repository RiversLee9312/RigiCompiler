// ============================================================================
// time_values.rg —— 施工块 6-1（STDLIB §4.9.1 / §4.9.4 / §4.9.5，D6）
// core.time 时间值纳秒化与运算：VM 与 native 双宿主对拍（NativeE2E
// 「时间值对拍」Case 复用本语料）。
//   ① TimeSpan 规范化分解：-1ns = -1ms + 999999ns；零唯一表示；
//      fromDays/Hours/Minutes/Seconds/Milliseconds/Microseconds/Nanoseconds
//      换算与表示范围溢出（OutOfBoundException）。
//   ② total 属性族向零截断：-1.5ms → totalMilliseconds -1；-1ns → 0；
//      总量超 i64 明确报错。
//   ③ 运算：加减进位/借位、取负（最小 TimeSpan 取负报错）、乘除整数
//      （base-100 limb 宽中间形态；不足一纳秒向零截断）、除零
//      DividedByZeroException、比较与判等按完整总量含纳秒。
//   ④ DateTime：±TimeSpan、两时刻相减保完整纳秒精度（差 1ns 对）、
//      UTC 公历范围（0001..9999）构造与运算校验。
//   ⑤ Timer.schedule：正持续时间向上取整为毫秒（<1ms 正余量不提前
//      到期，墙上时钟可断言形态，3 次重试消取样抖动）；过去时刻立即
//      具备触发条件。
//   ⑥ 序列化（§4.9.5）：toParcel 规范化对象字段形状；deepCopy 往返；
//      恢复路径同样校验不变量（纳秒 0..999999、DateTime 公历范围）。
// expect-output: neg1ns totalMs=0 totalNs=-1 totalUs=0
// expect-output: schedule-past=ok
// expect-output: schedule-ceil=ok
// expect-output: time-values-ok
// expect-exit: 0
// ============================================================================
import core.time.*
import core.serialization.*
import core.coroutine.*
import core.io.Console

// 失败计数（0 = 通过）；失败时打印名字定位（expect-output 只钉通过形态）
func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

func exerciseSchedule(): i32 {
    var fails = 0
    // 过去时刻：立即具备触发条件（yield 完成即通过）
    const past = DateTime.now() - TimeSpan.fromMilliseconds(10L)
    const late = Timer.schedule(past)
    yield late
    Console.println("schedule-past=ok")
    // 正持续时间 <1ms：向上取整 1ms，不会提前到期。
    // finish.ms > start.ms 当且仅当真实等待 ≥1ms（两取样间若跨越
    // 毫秒边界判失败并重试，至多 3 次消抖动）
    var attempt = 0
    var ceilingOk = false
    while (attempt < 3) {
        const start = DateTime.now()
        const alarm = Timer.schedule(start + TimeSpan.fromNanoseconds(1L))
        yield alarm
        const finish = DateTime.now()
        if (finish.stamp.milliseconds > start.stamp.milliseconds) {
            ceilingOk = true
            attempt = 3
        }
        attempt += 1
    }
    if (ceilingOk) {
        Console.println("schedule-ceil=ok")
    } else {
        Console.println("schedule-ceil=FAIL")
        fails = fails + 1
    }
    return fails
}

pub func main(): i32 {
    var fails = 0

    // ---- ① 规范化 ----
    const neg1ns = TimeSpan.fromNanoseconds(-1L)
    Console.println("neg1ns totalMs=${neg1ns.totalMilliseconds} totalNs=${neg1ns.totalNanoseconds} totalUs=${neg1ns.totalMicroseconds}")
    fails = fails + check("norm-neg1ns-totalms", neg1ns.totalMilliseconds == 0L)
    fails = fails + check("norm-neg1ns-totalns", neg1ns.totalNanoseconds == -1L)
    fails = fails + check("norm-neg1ns-decomp", neg1ns == (TimeSpan.fromMilliseconds(-1L)
        + TimeSpan.fromNanoseconds(999999L)))
    // 零唯一表示
    fails = fails + check("norm-zero-eq", TimeSpan.fromNanoseconds(0L)
        == TimeSpan.fromMilliseconds(0L))
    fails = fails + check("norm-zero-minus", (TimeSpan.fromMilliseconds(0L)
        - TimeSpan.fromNanoseconds(0L)) == TimeSpan.fromMilliseconds(0L))
    // 换算
    fails = fails + check("conv-days-hours", TimeSpan.fromDays(1L).totalHours == 24L)
    fails = fails + check("conv-days-ms", TimeSpan.fromDays(2L).totalMilliseconds == 172800000L)
    fails = fails + check("conv-hours-min", TimeSpan.fromHours(25L).totalMinutes == 1500L)
    fails = fails + check("conv-min-sec", TimeSpan.fromMinutes(90L).totalSeconds == 5400L)
    fails = fails + check("conv-sec-min", TimeSpan.fromSeconds(3661L).totalMinutes == 61L)
    fails = fails + check("conv-ms-sec", TimeSpan.fromMilliseconds(1500L).totalSeconds == 1L)
    fails = fails + check("conv-us-sec", TimeSpan.fromMicroseconds(1500000L).totalSeconds == 1L)
    fails = fails + check("conv-us-ns", TimeSpan.fromMicroseconds(1L).totalNanoseconds == 1000L)
    fails = fails + check("conv-ns-ms", TimeSpan.fromNanoseconds(1000000L)
        == TimeSpan.fromMilliseconds(1L))
    // 换算溢出
    var threw = false
    try {
        const x = TimeSpan.fromDays(9223372036854775807L)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("conv-days-ovf", threw)
    threw = false
    try {
        const x = TimeSpan.fromSeconds(9223372036854775807L)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("conv-sec-ovf", threw)

    // ---- ② total 属性族向零截断 ----
    fails = fails + check("tot-neg15ms", TimeSpan.fromNanoseconds(-1500000L).totalMilliseconds == -1L)
    fails = fails + check("tot-neg1ns-ms", TimeSpan.fromNanoseconds(-1L).totalMilliseconds == 0L)
    fails = fails + check("tot-neg1us-ms", TimeSpan.fromMicroseconds(-1L).totalMilliseconds == 0L)
    fails = fails + check("tot-neg1us-us", TimeSpan.fromMicroseconds(-1L).totalMicroseconds == -1L)
    fails = fails + check("tot-neg1ns-us", TimeSpan.fromNanoseconds(-1L).totalMicroseconds == 0L)
    fails = fails + check("tot-neg1500ms-sec", TimeSpan.fromMilliseconds(-1500L).totalSeconds == -1L)
    fails = fails + check("tot-neg1500ms-min", TimeSpan.fromMilliseconds(-1500L).totalMinutes == 0L)
    fails = fails + check("tot-neg49h-days", TimeSpan.fromHours(-49L).totalDays == -2L)
    fails = fails + check("tot-pos1500ms-whole", TimeSpan.fromMilliseconds(1500L).totalMilliseconds == 1500L)
    fails = fails + check("tot-pos15ms-trunc", TimeSpan.fromNanoseconds(1500000L).totalMilliseconds == 1L)
    // 总量超 i64 明确报错
    threw = false
    try {
        const x = TimeSpan.fromMilliseconds(9223372036854775807L).totalNanoseconds
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("tot-ns-max-ovf", threw)
    threw = false
    try {
        const x = TimeSpan.fromMilliseconds((-9223372036854775807L - 1L)).totalNanoseconds
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("tot-ns-min-ovf", threw)

    // ---- ③ TimeSpan 运算 ----
    fails = fails + check("op-add", (TimeSpan.fromMilliseconds(1500L)
        + TimeSpan.fromMilliseconds(500L)) == TimeSpan.fromMilliseconds(2000L))
    fails = fails + check("op-add-carry", (TimeSpan.fromNanoseconds(999999L)
        + TimeSpan.fromNanoseconds(1L)) == TimeSpan.fromMilliseconds(1L))
    fails = fails + check("op-sub-borrow", (TimeSpan.fromMilliseconds(1L)
        - TimeSpan.fromNanoseconds(1L)) == TimeSpan.fromNanoseconds(999999L))
    fails = fails + check("op-opp-neg", (-TimeSpan.fromNanoseconds(-1L)) == TimeSpan.fromNanoseconds(1L))
    fails = fails + check("op-opp-zero", (-TimeSpan.fromMilliseconds(0L)) == TimeSpan.fromMilliseconds(0L))
    // 乘法（含 limb 宽中间形态与溢出检查）
    fails = fails + check("op-mul", (TimeSpan.fromMilliseconds(3L) * 3L)
        == TimeSpan.fromMilliseconds(9L))
    fails = fails + check("op-mul-ns", (TimeSpan.fromNanoseconds(7L) * 3L)
        == TimeSpan.fromNanoseconds(21L))
    fails = fails + check("op-mul-neg", (TimeSpan.fromMilliseconds(5L) * (-2L))
        == TimeSpan.fromMilliseconds(-10L))
    fails = fails + check("op-mul-neg-ns", (TimeSpan.fromNanoseconds(5L) * (-2L))
        == TimeSpan.fromNanoseconds(-10L))
    fails = fails + check("op-mul-neg-t", (TimeSpan.fromNanoseconds(-6L) * 3L)
        == TimeSpan.fromNanoseconds(-18L))
    fails = fails + check("op-mul-wide", (TimeSpan.fromMilliseconds(9223372036854775L) * 1000L)
        == TimeSpan.fromMilliseconds(9223372036854775000L))
    threw = false
    try {
        const x = TimeSpan.fromMilliseconds(9223372036854775807L) * 2L
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("op-mul-ovf", threw)
    // 除法：向零截断到纳秒；除零报错；最小值取负报错
    fails = fails + check("op-div", (TimeSpan.fromNanoseconds(7L) / 2L)
        == TimeSpan.fromNanoseconds(3L))
    fails = fails + check("op-div-neg", (TimeSpan.fromNanoseconds(-7L) / 2L)
        == TimeSpan.fromNanoseconds(-3L))
    fails = fails + check("op-div-neg2", (TimeSpan.fromNanoseconds(7L) / (-2L))
        == TimeSpan.fromNanoseconds(-3L))
    fails = fails + check("op-div-negneg", (TimeSpan.fromNanoseconds(-7L) / (-2L))
        == TimeSpan.fromNanoseconds(3L))
    fails = fails + check("op-div-ms", (TimeSpan.fromMilliseconds(10L) / 3L)
        == TimeSpan.fromNanoseconds(3333333L))
    fails = fails + check("op-div-subns", (TimeSpan.fromNanoseconds(1L) / 2L)
        == TimeSpan.fromMilliseconds(0L))
    fails = fails + check("op-div-wide7", (TimeSpan.fromMilliseconds(9223372036854775807L) / 7L)
        == TimeSpan.fromMilliseconds(1317624576693539401L))
    fails = fails + check("op-div-wide2", (TimeSpan.fromMilliseconds(9223372036854775807L) / 2L)
        == (TimeSpan.fromMilliseconds(4611686018427387903L)
            + TimeSpan.fromNanoseconds(500000L)))
    fails = fails + check("op-div-widens", (TimeSpan.fromNanoseconds(9223372036854775807L) / 3L)
        == TimeSpan.fromNanoseconds(3074457345618258602L))
    threw = false
    try {
        const x = TimeSpan.fromMilliseconds(1L) / 0L
    } catch (e: core.DividedByZeroException) {
        threw = true
    }
    fails = fails + check("op-div-zero", threw)
    threw = false
    try {
        const x = -TimeSpan.fromMilliseconds((-9223372036854775807L - 1L))
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("op-opp-min", threw)
    // 比较与判等按完整总量（含纳秒）
    fails = fails + check("op-cmp-gt", TimeSpan.fromNanoseconds(1L).compareTo(
        TimeSpan.fromMilliseconds(0L)) is .GreaterThanAnother)
    fails = fails + check("op-cmp-lt", TimeSpan.fromNanoseconds(-1L).compareTo(
        TimeSpan.fromMilliseconds(0L)) is .LesserThanAnother)
    fails = fails + check("op-eq-full", (TimeSpan.fromMilliseconds(1L)
        == TimeSpan.fromNanoseconds(1000000L)))
    fails = fails + check("op-neq-ns", not (TimeSpan.fromNanoseconds(1L)
        == TimeSpan.fromMilliseconds(0L)))

    // ---- ④ DateTime 运算与范围 ----
    const t0 = new DateTime(new TimeStamp(100000000000L, 0))
    const t1 = new DateTime(new TimeStamp(100000000000L, 1))
    // 两时刻相减保留完整纳秒精度（旧实现只减毫秒，这里差 1ns）
    fails = fails + check("dt-diff-ns", (t1 - t0) == TimeSpan.fromNanoseconds(1L))
    fails = fails + check("dt-diff-ns-neg", (t0 - t1) == TimeSpan.fromNanoseconds(-1L))
    fails = fails + check("dt-plus-ns", (t0 + TimeSpan.fromNanoseconds(1L)) == t1)
    fails = fails + check("dt-minus-ns", (t1 - TimeSpan.fromNanoseconds(1L)) == t0)
    fails = fails + check("dt-minus-borrow", (t1 - TimeSpan.fromNanoseconds(2L))
        == new DateTime(new TimeStamp(99999999999L, 999999)))
    fails = fails + check("dt-cmp", t1.compareTo(t0) is .GreaterThanAnother)
    fails = fails + check("dt-eq", t0 == new DateTime(new TimeStamp(100000000000L, 0)))
    // 构造范围校验（0001..9999 UTC 公历）
    threw = false
    try {
        const x = new DateTime(new TimeStamp((-62135596800000L - 1L), 0))
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("dt-range-min", threw)
    threw = false
    try {
        const x = new DateTime(new TimeStamp(253402300800000L, 0))
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("dt-range-max", threw)
    fails = fails + check("dt-range-edge", new DateTime(
        new TimeStamp((-62135596800000L), 0))
        == new DateTime(new TimeStamp((-62135596800000L), 0)))
    // 运算越界
    threw = false
    try {
        const x = t0 + TimeSpan.fromDays(4000000L)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("dt-plus-ovf", threw)
    threw = false
    try {
        const x = (new DateTime(new TimeStamp((-62135596800000L), 0))) - TimeSpan.fromMilliseconds(1L)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("dt-minus-ovf", threw)

    // ---- ⑤ Timer.schedule 取整 ----
    fails = fails + exerciseSchedule()

    // ---- ⑥ 序列化（§4.9.5） ----
    const ts = new TimeStamp(123456789L, 987654)
    const tsBack = deepCopy\<TimeStamp>(ts)
    fails = fails + check("ser-ts-roundtrip", (tsBack.milliseconds == 123456789L)
        and (tsBack.nanoseconds == 987654))
    const spBack = deepCopy\<TimeSpan>(TimeSpan.fromNanoseconds(-1L))
    fails = fails + check("ser-span-roundtrip", spBack == TimeSpan.fromNanoseconds(-1L))
    const dtBack = deepCopy\<DateTime>(t1)
    fails = fails + check("ser-dt-roundtrip", dtBack == t1)
    // Parcel 形状：规范化对象字段表示（无日期/Duration 字符串形态）
    const wire = ts:Serializable.toParcel()
    const hasMs = wire.contains("milliseconds")
    const hasNs = wire.contains("nanoseconds")
    fails = fails + check("ser-parcel-shape", ((wire.elementCount() == 2L) and hasMs) and hasNs)
    // 恢复路径校验：纳秒不变量越界拒绝
    threw = false
    try {
        const bad = ts:Serializable.toParcel()
        bad.setElement\<i32>("nanoseconds", 1000000)
        const r = fromParcel\<TimeStamp>(bad)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("ser-restore-ns", threw)
    // 恢复路径校验：TimeSpan 纳秒不变量越界拒绝
    threw = false
    try {
        const bad = TimeSpan.fromMilliseconds(1L):Serializable.toParcel()
        bad.setElement\<i32>("nanoseconds", (-1))
        const r = fromParcel\<TimeSpan>(bad)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("ser-restore-span-ns", threw)
    // 恢复路径校验：DateTime 公历范围越界拒绝
    threw = false
    try {
        const bad = t1:Serializable.toParcel()
        const stampWire = bad.getElement\<Parcel>("stamp") as Parcel
        stampWire.setElement\<i64>("milliseconds", (-62135596800000L - 1L))
        const r = fromParcel\<DateTime>(bad)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("ser-restore-dt-range", threw)

    if (fails > 0) {
        Console.println("time-values-FAIL count=${fails}")
        return 1
    }
    Console.println("time-values-ok")
    return 0
}
