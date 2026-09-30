// DateTime 大跨度与边界回归：运算不得借完整 i64 总纳秒限制合法公历差值。
// expect-output: wide-datetime-ok
// expect-exit: 0
import core.time.*
import core.io.Console

func check(name: String, ok: bool): i32 {
    if (ok) { return 0 }
    Console.println("FAIL ${name}")
    return 1
}

pub func main(): i32 {
    var fails = 0
    const first = new DateTime(new TimeStamp(-62135596800000L, 0))
    const epoch = new DateTime(new TimeStamp(0L, 0))
    const last = new DateTime(new TimeStamp(253402300799999L, 999999))
    const days = TimeSpan.fromDays(719162L)
    const minSpan = TimeSpan.fromMilliseconds((-9223372036854775807L - 1L))
    const maxSpan = TimeSpan.fromMilliseconds(9223372036854775807L)

    fails += check("first-to-epoch", (first + days) == epoch)
    fails += check("epoch-back", (epoch - days) == first)
    fails += check("epoch-negative", (epoch + TimeSpan.fromDays(-719162L)) == first)
    fails += check("first-negative-minus", (first - TimeSpan.fromDays(-719162L)) == epoch)
    fails += check("wide-difference", (epoch - first) == days)
    fails += check("wide-reverse", (first - epoch) == TimeSpan.fromDays(-719162L))
    fails += check("wide-ns-borrow", ((epoch - first) - TimeSpan.fromNanoseconds(1L))
        == (TimeSpan.fromDays(719162L) - TimeSpan.fromNanoseconds(1L)))
    fails += check("wide-upper-diff", (last - first)
        == (TimeSpan.fromMilliseconds(315537897599999L) + TimeSpan.fromNanoseconds(999999L)))
    fails += check("upper-carry", (new DateTime(new TimeStamp(253402300799998L, 999999))
        + TimeSpan.fromNanoseconds(1L)) == new DateTime(new TimeStamp(253402300799999L, 0)))
    fails += check("lower-borrow", (new DateTime(new TimeStamp(-62135596799999L, 0))
        - TimeSpan.fromNanoseconds(1L)) == new DateTime(new TimeStamp(-62135596800000L, 999999)))
    fails += check("upper-final", (new DateTime(new TimeStamp(253402300799999L, 999998))
        + TimeSpan.fromNanoseconds(1L)) == last)
    fails += check("lower-final", (new DateTime(new TimeStamp(-62135596800000L, 1))
        - TimeSpan.fromNanoseconds(1L)) == first)

    var threw = false
    try { const bad = last + TimeSpan.fromNanoseconds(1L) }
    catch (e: core.OutOfBoundException) { threw = true }
    fails += check("upper-one-ns-over", threw)
    threw = false
    try { const bad = first - TimeSpan.fromNanoseconds(1L) }
    catch (e: core.OutOfBoundException) { threw = true }
    fails += check("lower-one-ns-over", threw)
    threw = false
    try { const bad = epoch - minSpan }
    catch (e: core.OutOfBoundException) { threw = true }
    fails += check("min-span-minus", threw)
    threw = false
    try { const bad = epoch + minSpan }
    catch (e: core.OutOfBoundException) { threw = true }
    fails += check("min-span-plus", threw)
    threw = false
    try { const bad = epoch - maxSpan }
    catch (e: core.OutOfBoundException) { threw = true }
    fails += check("max-span-minus", threw)
    threw = false
    try { const bad = epoch + maxSpan }
    catch (e: core.OutOfBoundException) { threw = true }
    fails += check("max-span-plus", threw)

    if (fails != 0) { return 1 }
    Console.println("wide-datetime-ok")
    return 0
}
