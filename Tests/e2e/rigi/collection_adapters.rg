// STDLIB §4.2.1 第二段 / §4.2.4 末段（施工块 1-6）：Array/Span →
// IEnumerable 借用适配器语料。
// 适配器借用原存储：枚举器直读底层 Array/Span 当前内容，不是快照——
// 遍历中途经别名写入后 current 反映新值是**既定借用语义**（§4.2.4 末段：
// 经任意别名在遍历期间写入属不支持的用法，不加修改计数、不承诺检测）。
// 每次 iterate() 产生独立枚举器（嵌套交替迭代互不影响）；Span 元素满足
// ValueType 约束，枚举范围恰为 length（越界读不到）；SpanEnumerator 状态
// 契约对齐 §4.2.4：未开始/正常结束后 current 抛
// core.NoSuchElementException，结束后 moveNext 持续 false（无失效检测）。
// 适配结果与 List 同走 IEnumerable<T> 形参（协议一致）。
// expect-output: array-sum-ok
// expect-output: array-cursor-ok
// expect-output: array-nested-ok
// expect-output: array-borrow-ok
// expect-output: array-empty-ok
// expect-output: span-sum-ok
// expect-output: span-cursor-ok
// expect-output: span-borrow-ok
// expect-output: span-noelem-fresh
// expect-output: span-noelem-end
// expect-output: span-empty-ok
// expect-output: interop-count-ok
// expect-exit: 0
import core.collections.*

// 以 IEnumerable<T> 为形参的通用计数函数：适配结果与 List 均可传入，
// 验证适配器与既有容器协议一致（§4.2.1 适配器接入 IEnumerable）
func countOf\<T>(source: IEnumerable\<T>): i32 {
    var n: i32 = 0
    const it = source.iterate()
    while (it.moveNext()) {
        n = (n + 1)
    }
    return n
}

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("adapters 断言失败 " + step.toString()) }
}

pub func main(): i32 {
    // ── Array 适配器：for-each 遍历求和 ──
    const arr = arrayOfElements\<i32>(1, 2, 3, 4)
    var sum: i32 = 0
    for (v in asEnumerable(arr)) {
        sum = (sum + v)
    }
    require((sum == 10))
    core.io.Console.println("array-sum-ok")

    // ── Array 适配器：两次 iterate 独立游标（交替推进互不影响）──
    const e1 = asEnumerable(arr)
    const itA = e1.iterate()
    const itB = e1.iterate()
    require(itA.moveNext())
    require(itA.moveNext())
    require(itB.moveNext())
    require((itA.current() == 2))
    require((itB.current() == 1))
    require((countOf\<i32>(e1) == 4))
    core.io.Console.println("array-cursor-ok")

    // ── Array 适配器：嵌套双重 for-each（外层推进中内层重新 iterate、
    // ── 完整遍历，验证内外枚举器完全独立）──
    var pairSum: i32 = 0
    for (x in asEnumerable(arr)) {
        for (y in asEnumerable(arr)) {
            pairSum = (pairSum + ((x * 10) + y))
        }
    }
    // 外层 x∈[1,4] 各配内层 y∈[1,4] 全和：50 + 90 + 130 + 170 = 440
    require((pairSum == 440))
    core.io.Console.println("array-nested-ok")

    // ── Array 适配器：借用语义——遍历中途经数组别名写入，后续 current
    // ── 反映新值。这是借用而非快照的**既定语义**（§4.2.4 末段：遍历
    // ── 期间经任意别名写入属不支持的用法，不加修改计数、不承诺检测）
    const alias = arr
    const itBorrow = asEnumerable(arr).iterate()
    require(itBorrow.moveNext())
    require((itBorrow.current() == 1))
    alias[0] = 100
    // 借用：current 直读底层当前内容（若是快照这里仍是 1）
    require((itBorrow.current() == 100))
    // 写入不使枚举器失效（适配器不加修改计数）：照常推进到尾
    require(itBorrow.moveNext())
    require(itBorrow.moveNext())
    require(itBorrow.moveNext())
    require((itBorrow.moveNext()) == false)
    core.io.Console.println("array-borrow-ok")

    // ── Array 适配器：空 Array——for-each 零次、计数 0 ──
    const empty = arrayOf\<i32>(0)
    var seen: i32 = 0
    for (v in asEnumerable(empty)) {
        seen = (seen + 1)
    }
    require((seen == 0))
    require((countOf\<i32>(asEnumerable(empty)) == 0))
    core.io.Console.println("array-empty-ok")

    // ── Span 适配器：spanOf 填充后 for-each 求和 ──
    const span = spanOf\<i32>(3)
    span[0] = 5
    span[1] = 6
    span[2] = 7
    var ssum: i32 = 0
    for (v in asEnumerable(span)) {
        ssum = (ssum + v)
    }
    require((ssum == 18))
    core.io.Console.println("span-sum-ok")

    // ── Span 适配器：枚举范围恰为 length（越界读不到——计数恰 3、
    // ── 正常结束后 moveNext 持续 false）；两次 iterate 独立游标 ──
    const itS1 = asEnumerable(span).iterate()
    const itS2 = asEnumerable(span).iterate()
    require(itS1.moveNext())
    require(itS2.moveNext())
    require(itS1.moveNext())
    require((itS1.current() == 6))
    require((itS2.current() == 5))
    require((countOf\<i32>(asEnumerable(span)) == 3))
    core.io.Console.println("span-cursor-ok")

    // ── Span 适配器：借用语义（同 Array，既定语义注释见上）──
    const salias = span
    const itSBorrow = asEnumerable(span).iterate()
    require(itSBorrow.moveNext())
    require((itSBorrow.current() == 5))
    salias[0] = 50
    // 借用：直读底层当前内容
    require((itSBorrow.current() == 50))
    core.io.Console.println("span-borrow-ok")

    // ── Span 枚举器状态契约（§4.2.4 状态机，无失效检测）：未开始 current
    // ── 抛 NoSuchElementException；抛错不改游标，moveNext 照常开始 ──
    const itFresh = asEnumerable(span).iterate()
    try {
        itFresh.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("span-noelem-fresh")
    }
    require(itFresh.moveNext())
    require((itFresh.current() == 50))
    require(itFresh.moveNext())
    require(itFresh.moveNext())
    require((itFresh.moveNext()) == false)
    // 正常结束后 current 抛、moveNext 持续 false
    try {
        itFresh.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("span-noelem-end")
    }
    require((itFresh.moveNext()) == false)

    // ── Span 适配器：空 Span——for-each 零次、计数 0 ──
    const es = spanOf\<i32>(0)
    var sseen: i32 = 0
    for (v in asEnumerable(es)) {
        sseen = (sseen + 1)
    }
    require((sseen == 0))
    require((countOf\<i32>(asEnumerable(es)) == 0))
    core.io.Console.println("span-empty-ok")

    // ── 适配结果与 List 互操作：同一 IEnumerable<T> 形参既收 List
    // ──（原生容器）也收 asEnumerable 适配结果（协议一致，§4.2.1）──
    const lst = new List\<i32>()
    lst.add(7)
    lst.add(8)
    require((countOf\<i32>(lst) == 2))
    require((countOf\<i32>(asEnumerable(arrayOfElements\<i32>(7, 8))) == 2))
    var both: i32 = 0
    for (v in lst) {
        both = (both + v)
    }
    require((both == 15))
    core.io.Console.println("interop-count-ok")

    return 0
}
