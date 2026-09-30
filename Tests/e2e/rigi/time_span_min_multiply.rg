// TimeSpan 乘法边界：负号允许最小毫秒，正号只允许最大毫秒；
// 纳秒余量参与最终范围判定，不能仅凭无符号毫秒幅值拒绝。
// expect-output: time-span-min-multiply-ok
// expect-exit: 0
import core.time.*
import core.io.Console

func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

pub func main(): i32 {
    var fails = 0
    const minMs = (-9223372036854775807L - 1L)
    const halfMin = TimeSpan.fromMilliseconds(-4611686018427387904L)
    const minSpan = TimeSpan.fromMilliseconds(minMs)
    const oneNs = TimeSpan.fromNanoseconds(1L)

    // 等于负界合法；仅多一纳秒越界，少一纳秒则借位后仍可表示。
    fails = fails + check("negative-exact-min", (halfMin * 2L) == minSpan)
    fails = fails + check("negative-inside-ns", ((halfMin + oneNs) * 2L)
        == (minSpan + TimeSpan.fromNanoseconds(2L)))
    fails = fails + check("negative-factor-min", (TimeSpan.fromMilliseconds(4611686018427387904L) * (-2L))
        == minSpan)
    var threw = false
    try {
        const x = (halfMin - oneNs) * 2L
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("negative-beyond-one-ns", threw)

    // 正界不因负向扩张而放宽；正界以内的亚毫秒余量仍正常保留。
    fails = fails + check("positive-near-max", ((TimeSpan.fromMilliseconds(4611686018427387903L)
        + TimeSpan.fromNanoseconds(999999L)) * 2L)
        == (TimeSpan.fromMilliseconds(9223372036854775807L)
            + TimeSpan.fromNanoseconds(999998L)))
    threw = false
    try {
        const x = TimeSpan.fromMilliseconds(4611686018427387904L) * 2L
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("positive-beyond-max", threw)
    threw = false
    try {
        const x = (TimeSpan.fromMilliseconds(4611686018427387904L) + oneNs) * 2L
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("positive-beyond-max-ns", threw)
    // -1 倍快速路径必须既不误拒，也不能从 MIN 毫秒取负回绕。
    fails = fails + check("negative-opposite-inside", ((minSpan + oneNs) * (-1L))
        == (TimeSpan.fromMilliseconds(9223372036854775807L)
            + TimeSpan.fromNanoseconds(999999L)))
    threw = false
    try {
        const x = minSpan / (-1L)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("negative-divisor-min", threw)
    fails = fails + check("negative-divisor-inside", ((minSpan + oneNs) / (-2L))
        == (TimeSpan.fromMilliseconds(4611686018427387903L)
            + TimeSpan.fromNanoseconds(999999L)))
    fails = fails + check("positive-divisor-min", (minSpan / 2L) == halfMin)
    // 因子的绝对值也可能是 i64_MIN，不能在取幅值时回绕。
    fails = fails + check("min-factor-positive-ns", (oneNs * minMs)
        == TimeSpan.fromNanoseconds(minMs))
    fails = fails + check("min-factor-negative-ns", ((-oneNs) * minMs)
        == (TimeSpan.fromNanoseconds(9223372036854775807L) + oneNs))

    if (fails > 0) { return 1 }
    Console.println("time-span-min-multiply-ok")
    return 0
}
