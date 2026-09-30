// TimeSpan 加减先完成纳秒进借位，按最终规范化毫秒段判断范围。
// expect-output: time-span-carry-boundaries-ok
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
    const minSpan = TimeSpan.fromMilliseconds(minMs)
    const maxSpan = TimeSpan.fromMilliseconds(9223372036854775807L)
    const oneNs = TimeSpan.fromNanoseconds(1L)
    const negativeFraction = TimeSpan.fromNanoseconds(-999999L)
    var valid = false

    // 毫秒之和暂态为 MIN-1，纳秒进位后恰好 MIN。
    try {
        valid = ((minSpan + TimeSpan.fromNanoseconds(999999L)) + negativeFraction) == minSpan
    } catch (e: core.OutOfBoundException) {
        valid = false
    }
    fails = fails + check("plus-temporary-underflow", valid)

    // 毫秒之差暂态为 MAX+1，纳秒借位后仍在正界内。
    valid = false
    try {
        valid = ((TimeSpan.fromMilliseconds(0L) - (minSpan + oneNs))
            == (maxSpan + TimeSpan.fromNanoseconds(999999L)))
    } catch (e: core.OutOfBoundException) {
        valid = false
    }
    fails = fails + check("minus-temporary-overflow", valid)

    // 恰好越过最终负界与正界必须报错，不能利用暂态回绕伪装合法。
    var threw = false
    try {
        const x = (minSpan + TimeSpan.fromNanoseconds(999998L)) + negativeFraction
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("plus-final-underflow", threw)
    threw = false
    try {
        const x = oneNs - minSpan
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("minus-final-overflow", threw)
    threw = false
    try {
        const x = maxSpan + oneNs
        const y = x + TimeSpan.fromNanoseconds(999999L)
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("plus-final-overflow-carry", threw)
    threw = false
    try {
        const x = minSpan - oneNs
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("minus-final-underflow-borrow", threw)

    // 非边界进借位及相反数交互保留原有精度与符号。
    fails = fails + check("plus-ordinary-carry", (TimeSpan.fromNanoseconds(999999L)
        + oneNs) == TimeSpan.fromMilliseconds(1L))
    fails = fails + check("minus-ordinary-borrow", (TimeSpan.fromMilliseconds(1L)
        - oneNs) == TimeSpan.fromNanoseconds(999999L))
    fails = fails + check("opposite-near-min", (-(minSpan + oneNs))
        == (maxSpan + TimeSpan.fromNanoseconds(999999L)))

    if (fails > 0) { return 1 }
    Console.println("time-span-carry-boundaries-ok")
    return 0
}
