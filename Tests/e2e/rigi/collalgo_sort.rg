// STDLIB §4.2.3 / §7.2「集合算法」排序段（施工块 1-8）：core.collections
// sorted/sortInPlace 与 compare 比较辅助语料。覆盖：sorted 对 i32 序列
// 升序正确、返回新 List 且源不被修改（容器独立）；稳定性（Pair<i32,
// String> 按键排序，相等键的原始相对顺序保持）；浮点辅助规则（NaN 排在
// 全部非 NaN 之后、NaN 之间排序相等、±0 排序相等、其余数值序——打印排序
// 后序列逐元素 toString 观察格式）；compare float 重载同规则；自定义逆
// 序 comparer；Array 借用适配器输入；sortInPlace 正常排序修改目标；比较
// 器中途抛错时目标保持原内容（先快照后写回的事故保证）；sortInPlace 后
// 排序前创建的枚举器 moveNext 抛 IllegalStateException（§4.2.4 原地排序
// 失效）。String 排序语料见 collalgo_string_compare.rg（块 3-3b 落地
// compare(String, String) 标量字典序重载后的专门覆盖），本文件保持
// 原有跳过行不动。
// expect-output: sort-i32-ok
// expect-output: sort-string-skipped
// expect-output: sort-stable-ok
// expect-output: sort-double: -2.25 -1 0 -0 3.5 NaN NaN
// expect-output: sort-double-checked-ok
// expect-output: sort-float-ok
// expect-output: sort-desc-ok
// expect-output: sort-adapter-ok
// expect-output: sortinplace-ok
// expect-output: sortinplace-error-ok
// expect-output: sortinplace-ise-ok
// expect-output: sortinplace-empty-ise-move-ok
// expect-output: sortinplace-empty-ise-current-ok
// expect-output: sortinplace-error-enumerator-ok
// expect-exit: 0
import core.collections.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("collalgo_sort 断言失败 " + step.toString()) }
}

// i32/i64 解包回退哨兵（if? 未命中侧）
var missI32: i32 = (0 - 1)
var missI64: i64 = ((0 as i64) - (1 as i64))

pub func main(): i32 {
    // ── sorted（i32）：升序正确、相等元素全保留、返回新 List、源不变 ──
    const src = new List\<i32>()
    src.add(5)
    src.add(1)
    src.add(4)
    src.add(1)
    src.add(3)
    // compare 直接作实参传入：按 Func<ComparisonResult, i32, i32> 目标
    // 类型决议到 compare(i32, i32) 重载。语言无函数名作值的形态，经
    // lambda 转发（匿名函数即一等 callable 值的构造方式）
    const ascCmp = func{(a: i32, b: i32): core.ComparisonResult -> compare(a, b)}
    const asc = sorted\<i32>(src, ascCmp)
    require((asc.length == (5 as i64)))
    require(((asc.getAtIndex((0 as i64)) if? missI32) == 1))
    require(((asc.getAtIndex((1 as i64)) if? missI32) == 1))
    require(((asc.getAtIndex((2 as i64)) if? missI32) == 3))
    require(((asc.getAtIndex((3 as i64)) if? missI32) == 4))
    require(((asc.getAtIndex((4 as i64)) if? missI32) == 5))
    // 源保持原序原内容（sorted 不修改输入）
    require(((src.getAtIndex((0 as i64)) if? missI32) == 5))
    require(((src.getAtIndex((1 as i64)) if? missI32) == 1))
    require(((src.getAtIndex((4 as i64)) if? missI32) == 3))
    // 结果与源是独立容器：写结果不回灌源
    asc.setAtIndex((0 as i64), 99)
    require(((src.getAtIndex((1 as i64)) if? missI32) == 1))
    core.io.Console.println("sort-i32-ok")

    // ── String 排序：本文件保持历史跳过行；String compare/sorted 语料
    // 已由 collalgo_string_compare.rg 专门覆盖（块 3-3b）──
    core.io.Console.println("sort-string-skipped")

    // ── 稳定性：按键排序的 Pair<i32,String>，相等键保持原始相对顺序 ──
    const pairs = new List\<core.Pair\<i32, String>>()
    pairs.add(new core.Pair\<i32, String>(2, "a"))
    pairs.add(new core.Pair\<i32, String>(1, "b"))
    pairs.add(new core.Pair\<i32, String>(2, "c"))
    pairs.add(new core.Pair\<i32, String>(1, "d"))
    pairs.add(new core.Pair\<i32, String>(2, "e"))
    // comparer 只看键：compare(i32, i32) 决议；键 2 的 a/c/e 与键 1 的
    // b/d 各自的原始相对顺序必须在结果中保持（稳定排序契约）
    const byKey = sorted\<core.Pair\<i32, String>>(pairs, func{(x: core.Pair\<i32, String>, y: core.Pair\<i32, String>): core.ComparisonResult -> compare(x.key, y.key)})
    require(((byKey.getAtIndex((0 as i64)) as core.Pair\<i32, String>).value == "b"))
    require(((byKey.getAtIndex((1 as i64)) as core.Pair\<i32, String>).value == "d"))
    require(((byKey.getAtIndex((2 as i64)) as core.Pair\<i32, String>).value == "a"))
    require(((byKey.getAtIndex((3 as i64)) as core.Pair\<i32, String>).value == "c"))
    require(((byKey.getAtIndex((4 as i64)) as core.Pair\<i32, String>).value == "e"))
    core.io.Console.println("sort-stable-ok")

    // ── 浮点辅助规则（compare double）：NaN 排尾、NaN 互等、±0 相等 ──
    const nan: double = (0.0 / 0.0)
    const nums = new List\<double>()
    nums.add(3.5)
    nums.add(nan)
    nums.add(-(2.25))
    nums.add(0.0)
    nums.add(-(0.0))
    nums.add(-1.0)
    nums.add((0.0 / 0.0))
    const dblCmp = func{(a: double, b: double): core.ComparisonResult -> compare(a, b)}
    const ordered = sorted\<double>(nums, dblCmp)
    // 逐元素打印排序后序列（double.toString 不变文化格式：NaN 字面
    // "NaN"、-0 带符号）。期望：-2.25 -1 0 -0 3.5 NaN NaN——两个 NaN 全部
    // 排在非 NaN 之后（排序相等自然保持相邻），±0 排序相等且保持原相对
    // 顺序（+0 先于 -0）
    var line = "sort-double:"
    var idx: i64 = (0 as i64)
    while (idx < ordered.length) {
        line = (line + (" " + ((ordered.getAtIndex(idx) as double).toString())))
        idx = (idx + (1 as i64))
    }
    core.io.Console.println(line)
    // 结构断言：首元素 -2.25、末两位是 NaN、NaN 之前是 3.5
    require(((ordered.getAtIndex((0 as i64)) as double).toString() == "-2.25"))
    require(((ordered.getAtIndex((6 as i64)) as double).toString() == "NaN"))
    require(((ordered.getAtIndex((5 as i64)) as double).toString() == "NaN"))
    require(((ordered.getAtIndex((4 as i64)) as double).toString() == "3.5"))
    core.io.Console.println("sort-double-checked-ok")

    // ── compare float 重载同规则（NaN 排尾、数值序）──
    const fone: float = (1.0 as float)
    const fzero: float = (0.0 as float)
    // float NaN 构造（0.0/0.0 形态）：NaN 是唯一不等于自身的值
    const fnan: float = (fzero / fzero)
    require(not (fnan == fnan))
    // NaN 排在非 NaN 之后（GreaterThanAnother）；NaN 之间排序相等
    require(compare(fnan, fone) is .GreaterThanAnother)
    require(compare(fnan, fnan) is .Equal)
    require(compare(fone, fzero) is .GreaterThanAnother)
    require(compare(fzero, fzero) is .Equal)
    core.io.Console.println("sort-float-ok")

    // ── 自定义 comparer：逆序排序 ──
    const desc = sorted\<i32>(src, func{(a: i32, b: i32): core.ComparisonResult -> {
        if (a < b) { return@_ core.ComparisonResult.GreaterThanAnother }
        if (a > b) { return@_ core.ComparisonResult.LesserThanAnother }
        return@_ core.ComparisonResult.Equal
    }})
    require(((desc.getAtIndex((0 as i64)) if? missI32) == 5))
    require(((desc.getAtIndex((1 as i64)) if? missI32) == 4))
    require(((desc.getAtIndex((2 as i64)) if? missI32) == 3))
    require(((desc.getAtIndex((3 as i64)) if? missI32) == 1))
    require(((desc.getAtIndex((4 as i64)) if? missI32) == 1))
    core.io.Console.println("sort-desc-ok")

    // ── Array 借用适配器输入：sorted 对任意 IEnumerable<T> 工作 ──
    const raw = arrayOfElements\<i32>(9, 7, 8)
    const fromArr = sorted\<i32>(asEnumerable(raw), ascCmp)
    require(((fromArr.getAtIndex((0 as i64)) if? missI32) == 7))
    require(((fromArr.getAtIndex((1 as i64)) if? missI32) == 8))
    require(((fromArr.getAtIndex((2 as i64)) if? missI32) == 9))
    core.io.Console.println("sort-adapter-ok")

    // ── sortInPlace：正常排序修改目标（length 不变、逐元素有序）──
    const target = new List\<i32>()
    target.add(5)
    target.add(3)
    target.add(1)
    target.add(4)
    target.add(2)
    sortInPlace\<i32>(target, ascCmp)
    require((target.length == (5 as i64)))
    require(((target.getAtIndex((0 as i64)) if? missI32) == 1))
    require(((target.getAtIndex((1 as i64)) if? missI32) == 2))
    require(((target.getAtIndex((2 as i64)) if? missI32) == 3))
    require(((target.getAtIndex((3 as i64)) if? missI32) == 4))
    require(((target.getAtIndex((4 as i64)) if? missI32) == 5))
    core.io.Console.println("sortinplace-ok")

    // ── 比较器中途抛错：目标保持原内容（先快照后写回的事故保证）──
    const fragile = new List\<i32>()
    fragile.add(3)
    fragile.add(1)
    fragile.add(2)
    var calls: i32 = 0
    var caught = false
    try {
        sortInPlace\<i32>(fragile, func{(a: i32, b: i32): core.ComparisonResult -> {
            calls = (calls + 1)
            if (calls == 1) { throw new core.RuntimeException("comparer 爆炸") }
            return@_ compare(a, b)
        }})
        require(false)
    } catch (e: core.RuntimeException) {
        caught = true
    }
    require(caught)
    // 全部比较发生在临时快照上：异常传播时目标一次都未被写，逐元素原样
    require((calls == 1))
    require(((fragile.getAtIndex((0 as i64)) if? missI32) == 3))
    require(((fragile.getAtIndex((1 as i64)) if? missI32) == 1))
    require(((fragile.getAtIndex((2 as i64)) if? missI32) == 2))
    core.io.Console.println("sortinplace-error-ok")

    // ── 迭代失效：sortInPlace 后，排序前创建的枚举器 moveNext 抛 ISE ──
    const sp = new List\<i32>()
    sp.add(2)
    sp.add(1)
    const stale = sp.iterate()
    require(stale.moveNext())
    sortInPlace\<i32>(sp, ascCmp)
    try {
        stale.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("sortinplace-ise-ok")
    }
    // 目标本身排序成功（失效不影响本次原地排序结果）
    require(((sp.getAtIndex((0 as i64)) if? missI32) == 1))
    require(((sp.getAtIndex((1 as i64)) if? missI32) == 2))

    // 空表排序没有可写回的槽，成功排序仍须使先前的枚举器失效。
    // 分别检查 moveNext 与尚未开始的 current：后者必须优先报告失效。
    const empty = new List\<i32>()
    const emptyMove = empty.iterate()
    const emptyCurrent = empty.iterate()
    sortInPlace\<i32>(empty, ascCmp)
    require((empty.length == (0 as i64)))
    try {
        emptyMove.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("sortinplace-empty-ise-move-ok")
    }
    try {
        emptyCurrent.current()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("sortinplace-empty-ise-current-ok")
    }

    // 比较器在快照排序期间抛错，原 List 的既有枚举器不能被排序无端失效。
    const beforeFailure = fragile.iterate()
    var failedAgain = false
    try {
        sortInPlace\<i32>(fragile, func{(a: i32, b: i32): core.ComparisonResult -> {
            throw new core.RuntimeException("比较器失败")
        }})
        require(false)
    } catch (e: core.RuntimeException) {
        failedAgain = true
    }
    require(failedAgain)
    require(beforeFailure.moveNext())
    require((beforeFailure.current() == 3))
    core.io.Console.println("sortinplace-error-enumerator-ok")
    return 0
}
