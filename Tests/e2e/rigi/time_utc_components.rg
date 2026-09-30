// DateTime UTC 只读分量（STDLIB §4.9.1）：纪元前 floor days、闰日、
// 偏移跨午夜、纳秒不改秒字段、0001/9999 边界；与 toString 共用日历拆分。
// expect-output: utc-components-ok
// expect-exit: 0
import core.time.*
import core.io.Console

func fieldsMatch(dt: DateTime, y: i32, mo: i32, d: i32,
        h: i32, mi: i32, s: i32): bool {
    return (((((dt.year == y) and (dt.month == mo))
        and (dt.day == d)) and (dt.hour == h))
        and (dt.minute == mi)) and (dt.second == s)
}

pub func main(): i32 {
    var ok = true
    const min = DateTime.parse("0001-01-01T00:00:00Z")
    if (not fieldsMatch(min, 1, 1, 1, 0, 0, 0)) { ok = false }
    const before = DateTime.parse("1969-12-31T23:59:59.999999999Z")
    if (not fieldsMatch(before, 1969, 12, 31, 23, 59, 59)) { ok = false }
    const epoch = DateTime.parse("1970-01-01T00:00:00Z")
    if (not fieldsMatch(epoch, 1970, 1, 1, 0, 0, 0)) { ok = false }
    const leap = DateTime.parse("2024-02-29T12:34:56.123456789Z")
    if (not fieldsMatch(leap, 2024, 2, 29, 12, 34, 56)) { ok = false }
    const midnight = DateTime.parse("2024-03-01T00:00:00Z")
    if (not fieldsMatch(midnight - TimeSpan.fromNanoseconds(1L),
            2024, 2, 29, 23, 59, 59)) { ok = false }
    const offset = DateTime.parse("2024-03-01T00:30:00+01:00")
    if (not fieldsMatch(offset, 2024, 2, 29, 23, 30, 0)) { ok = false }
    const max = DateTime.parse("9999-12-31T23:59:59.999999999Z")
    if (not fieldsMatch(max, 9999, 12, 31, 23, 59, 59)) { ok = false }
    // 抽取后的文本路径必须仍和属性一致：负纪元、闰日、跨午夜、
    // 显示偏移不改 UTC 字段，纳秒余量与两端格式不丢失。
    if (min.toString() != "0001-01-01T00:00:00Z") { ok = false }
    if (before.toString() != "1969-12-31T23:59:59.999999999Z") { ok = false }
    if (leap.toString() != "2024-02-29T12:34:56.123456789Z") { ok = false }
    if (offset.toString() != "2024-02-29T23:30:00Z") { ok = false }
    if (offset.toString(60) != "2024-03-01T00:30:00+01:00") { ok = false }
    if (max.toString() != "9999-12-31T23:59:59.999999999Z") { ok = false }
    if (ok) {
        Console.println("utc-components-ok")
        return 0
    }
    Console.println("utc-components-FAIL")
    return 1
}
