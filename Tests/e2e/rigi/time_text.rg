// ============================================================================
// time_text.rg —— 施工块 6-2（STDLIB §4.9.2 / §4.9.3 / §4.9.5，D6）
// core.time 文本格式：VM 与 native 双宿主对拍（NativeE2E「时间文本对拍」
// Case 复用本语料）。
//   DateTime（RFC 3339 收窄子集）：
//   ① 合法全形态：Z / ±HH:MM 偏移 / fraction 1 位与 9 位（直接形成纳秒，
//      不经浮点）；严格消费全文。
//   ② 拒绝形态全表：缺失偏移 / 非法日期（2 月 30 日、非闰年 2 月 29 日）
//      / 秒值 60 / 24:00 / 未知偏移 -00:00 / 超九位小数 / 尾随垃圾 /
//      空白 / 小写 t/z / 非 ASCII 数字。
//   ③ 偏移换算正确性：+08:00 等整分钟偏移换算 UTC（DateTime 不保存
//      原始输入偏移）；当地与换算后 UTC 都须界内（0000 年、UTC 上溢
//      /下溢拒绝，超范围类可区分）。
//   ④ 往返恒等：parse(toString(d)) == d（含纳秒）；显式零偏移输出 Z；
//      非零偏移输出 ±HH:MM；显示偏移使日期越界报 OutOfBoundException。
//   ⑤ tryParse 一致性：合法得同值、非法得 null；TimeParseException
//      非法文本与不可表示范围两类可区分（isOutOfRange + position）。
//   TimeSpan（ISO 8601 Duration 日时分秒子集）：
//   ⑥ 固定样例：1 天 2 时 3 分 4.5 秒 → P1DT2H3M4.5S；-1ns →
//      -PT0.000000001S；PT90M 规范输出 PT1H30M；P1M / P1W /
//      PT0.0000000001S 拒绝。
//   ⑦ 拒绝形态：P / PT / P1DT / 混合符号 / 前导加号 / 秒外小数 /
//      空小数 / 单位乱序重复 / 尾随垃圾 / 空白 / 小写。
//   ⑧ 零统一 PT0S（含负零输入归一）；非零负值整体负号；范围溢出不
//      截断不回绕（超范围类）；与向零截断运算衔接；往返恒等。
//   D6：日历计算与文本行为纯 Rigi 计算 + ASCII 扫描，不读系统时区，
//   不因 OS/系统语言/进程 locale 改变；旧的 `天数.hh:mm:ss` 形态不是
//   默认解析或输出格式。
// expect-output: span-samples P1DT2H3M4.5S -PT0.000000001S PT1H30M PT0S
// expect-output: dt-forms 2024-02-29T12:34:56Z 2024-06-15T04:50:30Z
// expect-output: time-text-ok
// expect-exit: 0
// ============================================================================
import core.time.*
import core.io.Console

// 失败计数（0 = 通过）；失败时打印名字定位（expect-output 只钉通过形态）
func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

// DateTime 拒绝断言：必须抛 TimeParseException（其他异常照常传播）
func dtRejects(name: String, text: String): i32 {
    try {
        const v = DateTime.parse(text)
        Console.println("dt-accept-FAIL ${name}")
        return 1
    } catch (e: TimeParseException) {
        return 0
    }
}

// TimeSpan 拒绝断言（经 tryParse 一致性通道：null = 拒绝）
func tsRejects(name: String, text: String): i32 {
    const r = TimeSpan.tryParse(text)
    if (r == null) { return 0 }
    Console.println("ts-accept-FAIL ${name}")
    return 1
}

pub func main(): i32 {
    var fails = 0

    // ---- ⑥ TimeSpan 固定样例 ----
    const sample = ((((TimeSpan.fromDays(1L) + TimeSpan.fromHours(2L))
        + TimeSpan.fromMinutes(3L)) + TimeSpan.fromSeconds(4L))
        + TimeSpan.fromNanoseconds(500000000L))
    const s1 = sample.toString()
    const s2 = TimeSpan.fromNanoseconds(-1L).toString()
    const s3 = TimeSpan.parse("PT90M").toString()
    const s4 = TimeSpan.fromMilliseconds(0L).toString()
    Console.println("span-samples ${s1} ${s2} ${s3} ${s4}")
    fails = fails + check("span-fix-1", s1 == "P1DT2H3M4.5S")
    fails = fails + check("span-fix-2", s2 == "-PT0.000000001S")
    fails = fails + check("span-fix-3", s3 == "PT1H30M")
    fails = fails + check("span-fix-4", s4 == "PT0S")
    // 固定样例拒绝组（契约明文）
    fails = fails + tsRejects("span-fix-rej-M", "P1M")
    fails = fails + tsRejects("span-fix-rej-W", "P1W")
    fails = fails + tsRejects("span-fix-rej-frac10", "PT0.0000000001S")

    // ---- ⑦ TimeSpan 拒绝形态 ----
    fails = fails + tsRejects("span-rej-P", "P")
    fails = fails + tsRejects("span-rej-PT", "PT")
    fails = fails + tsRejects("span-rej-P1DT", "P1DT")
    fails = fails + tsRejects("span-rej-plus", "+PT1S")
    fails = fails + tsRejects("span-rej-inner-sign", "-PT1H-2M")
    fails = fails + tsRejects("span-rej-frac-H", "PT1.5H")
    fails = fails + tsRejects("span-rej-frac-empty", "PT1.S")
    fails = fails + tsRejects("span-rej-order", "PT1M1H")
    fails = fails + tsRejects("span-rej-dup", "PT1H1H")
    fails = fails + tsRejects("span-rej-trailing", "PT1Sx")
    fails = fails + tsRejects("span-rej-ws-front", " PT1S")
    fails = fails + tsRejects("span-rej-ws-back", "PT1S ")
    fails = fails + tsRejects("span-rej-lower", "pt1s")
    fails = fails + tsRejects("span-rej-y", "P1Y")
    fails = fails + tsRejects("span-rej-empty", "")

    // ---- TimeSpan 解析与规范化 ----
    fails = fails + check("span-unnorm", TimeSpan.parse("PT90M")
        == TimeSpan.fromMinutes(90L))
    fails = fails + check("span-days", TimeSpan.parse("P1DT2H")
        == (TimeSpan.fromDays(1L) + TimeSpan.fromHours(2L)))
    fails = fails + check("span-neg", TimeSpan.parse("-PT1H30M")
        == -TimeSpan.fromMinutes(90L))
    fails = fails + check("span-frac", TimeSpan.parse("PT4.5S")
        == (TimeSpan.fromSeconds(4L) + TimeSpan.fromNanoseconds(500000000L)))
    fails = fails + check("span-frac-9", TimeSpan.parse("PT0.000000001S")
        == TimeSpan.fromNanoseconds(1L))
    fails = fails + check("span-neg-zero", TimeSpan.parse("-PT0S")
        == TimeSpan.fromMilliseconds(0L))
    fails = fails + check("span-neg-frac", TimeSpan.parse("-PT0.001S")
        == TimeSpan.fromNanoseconds(-1000000L))
    // 旧 `天数.hh:mm:ss` 形态不是默认解析格式
    fails = fails + tsRejects("span-rej-legacy", "1.00:00:00")
    // 范围溢出：不截断、不回绕，超范围类
    var threw = false
    var rangeClass = false
    try {
        const v = TimeSpan.parse("PT9223372036854775808S")
    } catch (e: TimeParseException) {
        threw = true
        rangeClass = e.isOutOfRange
    }
    fails = fails + check("span-ovf-throw", threw)
    fails = fails + check("span-ovf-class", rangeClass)
    // 向零截断衔接：亚纳秒向零截断
    fails = fails + check("span-div-trunc",
        (TimeSpan.fromNanoseconds(1L) / 2L) == TimeSpan.fromMilliseconds(0L))
    // 往返恒等（含纳秒与负值；逐个具名展开——struct 变参数组在 native
    // ABI 下有既有损坏问题，语料不依赖该形态）
    const rs0 = TimeSpan.fromMilliseconds(0L)
    const rs1 = TimeSpan.fromNanoseconds(-1L)
    const rs2 = TimeSpan.fromNanoseconds(1L)
    const rs3 = sample
    const rs4 = -sample
    const rs5 = TimeSpan.fromDays(400L) + TimeSpan.fromNanoseconds(999999L)
    const rs6 = TimeSpan.fromHours(-25L)
    const rs7 = TimeSpan.fromMilliseconds(-1500L)
    const rs8 = TimeSpan.parse("PT0.001S")
    fails = fails + check("span-round-0", TimeSpan.parse(rs0.toString()) == rs0)
    fails = fails + check("span-round-1", TimeSpan.parse(rs1.toString()) == rs1)
    fails = fails + check("span-round-2", TimeSpan.parse(rs2.toString()) == rs2)
    fails = fails + check("span-round-3", TimeSpan.parse(rs3.toString()) == rs3)
    fails = fails + check("span-round-4", TimeSpan.parse(rs4.toString()) == rs4)
    fails = fails + check("span-round-5", TimeSpan.parse(rs5.toString()) == rs5)
    fails = fails + check("span-round-6", TimeSpan.parse(rs6.toString()) == rs6)
    fails = fails + check("span-round-7", TimeSpan.parse(rs7.toString()) == rs7)
    fails = fails + check("span-round-8", TimeSpan.parse(rs8.toString()) == rs8)

    // ---- ① DateTime 合法全形态 ----
    const dLeap = DateTime.parse("2024-02-29T12:34:56Z")
    fails = fails + check("dt-leap", dLeap.toString() == "2024-02-29T12:34:56Z")
    const dFrac1 = DateTime.parse("2024-01-01T00:00:00.5Z")
    fails = fails + check("dt-frac1", dFrac1.toString() == "2024-01-01T00:00:00.5Z")
    const dFrac9 = DateTime.parse("2024-01-01T00:00:00.123456789Z")
    fails = fails + check("dt-frac9", dFrac9.toString() == "2024-01-01T00:00:00.123456789Z")
    const dFracTrim = DateTime.parse("2024-01-01T00:00:00.100Z")
    fails = fails + check("dt-frac-trim", dFracTrim.toString() == "2024-01-01T00:00:00.1Z")
    const dOff = DateTime.parse("2024-06-15T10:20:30+05:30")
    fails = fails + check("dt-off", dOff.toString() == "2024-06-15T04:50:30Z")
    const dOffNeg = DateTime.parse("2024-06-15T10:20:30-05:30")
    fails = fails + check("dt-off-neg", dOffNeg.toString() == "2024-06-15T15:50:30Z")
    const dMin = DateTime.parse("0001-01-01T00:00:00Z")
    fails = fails + check("dt-min", dMin.toString() == "0001-01-01T00:00:00Z")
    const dMax = DateTime.parse("9999-12-31T23:59:59.999999999Z")
    fails = fails + check("dt-max", dMax.toString() == "9999-12-31T23:59:59.999999999Z")
    // 显式零偏移仍输出 Z；非零偏移输出 ±HH:MM
    fails = fails + check("dt-zero-off", dFrac1.toString(0) == "2024-01-01T00:00:00.5Z")
    fails = fails + check("dt-show-off", dFrac1.toString(480) == "2024-01-01T08:00:00.5+08:00")
    fails = fails + check("dt-show-off-neg",
        dFrac1.toString(-90) == "2023-12-31T22:30:00.5-01:30")
    Console.println("dt-forms ${dLeap.toString()} ${dOff.toString()}")

    // ---- ② DateTime 拒绝形态 ----
    fails = fails + dtRejects("dt-rej-no-offset", "2024-01-01T00:00:00")
    fails = fails + dtRejects("dt-rej-bad-date", "2023-02-29T00:00:00Z")
    fails = fails + dtRejects("dt-rej-feb30", "2024-02-30T00:00:00Z")
    fails = fails + dtRejects("dt-rej-apr31", "2024-04-31T00:00:00Z")
    fails = fails + dtRejects("dt-rej-mon13", "2024-13-01T00:00:00Z")
    fails = fails + dtRejects("dt-rej-mon00", "2024-00-01T00:00:00Z")
    fails = fails + dtRejects("dt-rej-day00", "2024-01-00T00:00:00Z")
    fails = fails + dtRejects("dt-rej-sec60", "2024-01-01T00:00:60Z")
    fails = fails + dtRejects("dt-rej-2400", "2024-01-01T24:00:00Z")
    fails = fails + dtRejects("dt-rej-min60", "2024-01-01T00:60:00Z")
    fails = fails + dtRejects("dt-rej-minus-zero", "2024-01-01T00:00:00-00:00")
    fails = fails + dtRejects("dt-rej-frac10", "2024-01-01T00:00:00.1234567890Z")
    fails = fails + dtRejects("dt-rej-frac-empty", "2024-01-01T00:00:00.Z")
    fails = fails + dtRejects("dt-rej-trailing", "2024-01-01T00:00:00Zx")
    fails = fails + dtRejects("dt-rej-ws-front", " 2024-01-01T00:00:00Z")
    fails = fails + dtRejects("dt-rej-ws-back", "2024-01-01T00:00:00Z ")
    fails = fails + dtRejects("dt-rej-ws-inner", "2024-01-01T00:00:00 Z")
    fails = fails + dtRejects("dt-rej-lower-t", "2024-01-01t00:00:00Z")
    fails = fails + dtRejects("dt-rej-lower-z", "2024-01-01T00:00:00z")
    fails = fails + dtRejects("dt-rej-short", "2024-01-01T00:00Z")
    fails = fails + dtRejects("dt-rej-off-hour", "2024-01-01T00:00:00+24:00")
    fails = fails + dtRejects("dt-rej-off-min", "2024-01-01T00:00:00+00:60")
    fails = fails + dtRejects("dt-rej-no-colon", "2024-01-01T00:00:00+0800")

    // ---- ③ 偏移换算与范围 ----
    fails = fails + check("dt-conv",
        DateTime.parse("2024-01-01T08:00:00+08:00")
            == DateTime.parse("2024-01-01T00:00:00Z"))
    fails = fails + check("dt-conv-neg",
        DateTime.parse("2024-01-01T00:00:00-23:59")
            == DateTime.parse("2024-01-01T23:59:00Z"))
    fails = fails + dtRejects("dt-rej-year0", "0000-01-01T00:00:00Z")
    fails = fails + dtRejects("dt-rej-utc-under", "0001-01-01T00:00:00+00:01")
    fails = fails + dtRejects("dt-rej-utc-over", "9999-12-31T23:59:59-23:59")

    // ---- ④ 往返恒等（含纳秒；逐个具名展开——struct 变参数组在 native
    // ABI 下有既有损坏问题，语料不依赖该形态） ----
    const rd0 = DateTime.parse("1970-01-01T00:00:00Z")
    const rd1 = DateTime.parse("1969-12-31T23:59:59.999999999Z")
    fails = fails + check("dt-round-1", DateTime.parse(dLeap.toString()) == dLeap)
    fails = fails + check("dt-round-2", DateTime.parse(dFrac1.toString()) == dFrac1)
    fails = fails + check("dt-round-3", DateTime.parse(dFrac9.toString()) == dFrac9)
    fails = fails + check("dt-round-4", DateTime.parse(dFracTrim.toString()) == dFracTrim)
    fails = fails + check("dt-round-5", DateTime.parse(dOff.toString()) == dOff)
    fails = fails + check("dt-round-6", DateTime.parse(dOffNeg.toString()) == dOffNeg)
    fails = fails + check("dt-round-7", DateTime.parse(dMin.toString()) == dMin)
    fails = fails + check("dt-round-8", DateTime.parse(dMax.toString()) == dMax)
    fails = fails + check("dt-round-9", DateTime.parse(rd0.toString()) == rd0)
    fails = fails + check("dt-round-10", DateTime.parse(rd1.toString()) == rd1)
    // 显示偏移超范围：参数越界与边界日期平移越界
    threw = false
    try {
        const v = dFrac1.toString(1440)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("dt-show-off-arg-ovf", threw)
    threw = false
    try {
        const v = dMax.toString(1)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("dt-show-off-dst-ovf", threw)
    threw = false
    try {
        const v = dMin.toString(-1)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("dt-show-off-dst-ovf-neg", threw)

    // ---- ⑤ tryParse 一致性与异常两类 ----
    const tp = DateTime.tryParse("2024-02-29T12:34:56Z")
    if (tp == null) {
        Console.println("check-FAIL dt-tryparse-null")
        fails = fails + 1
    } else {
        if (not (tp == dLeap)) {
            Console.println("check-FAIL dt-tryparse-eq")
            fails = fails + 1
        }
    }
    if (not (DateTime.tryParse("2024-01-01T00:00:00") == null)) {
        Console.println("check-FAIL dt-tryparse-rej")
        fails = fails + 1
    }
    const tsp = TimeSpan.tryParse("P1DT2H3M4.5S")
    if (tsp == null) {
        Console.println("check-FAIL span-tryparse-null")
        fails = fails + 1
    } else {
        if (not (tsp == sample)) {
            Console.println("check-FAIL span-tryparse-eq")
            fails = fails + 1
        }
    }
    if (not (TimeSpan.tryParse("PT") == null)) {
        Console.println("check-FAIL span-tryparse-rej")
        fails = fails + 1
    }
    // 两类可区分：非法文本（月份越界）vs 不可表示的范围（年份 0000），
    // 均带字节位置
    var fmtClassOk = false
    try {
        const v = DateTime.parse("2024-13-01T00:00:00Z")
    } catch (e: TimeParseException) {
        fmtClassOk = ((not e.isOutOfRange) and (not (e.position == null)))
    }
    fails = fails + check("dt-exc-fmt-class", fmtClassOk)
    var rngClassOk = false
    try {
        const v = DateTime.parse("0000-06-01T00:00:00Z")
    } catch (e: TimeParseException) {
        rngClassOk = (e.isOutOfRange and (not (e.position == null)))
    }
    fails = fails + check("dt-exc-rng-class", rngClassOk)

    if (fails > 0) {
        Console.println("time-text-FAIL count=${fails}")
        return 1
    }
    Console.println("time-text-ok")
    return 0
}
