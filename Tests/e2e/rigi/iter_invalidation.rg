// STDLIB §4.2.4（施工块 1-3）：List/Map 遍历修改检测与枚举器状态错误语料。
// iterate() 创建的枚举器记下创建时修改计数：期间 add/insert/setAtIndex/
// remove/removeAt 命中/clear（含空表 clear）及 Map 的 set（新增键、覆盖
// 已有键——写相同值也算）均使既有枚举器失效，之后的 moveNext/current 抛
// core.IllegalStateException（失效优先于正常结束）；无效果操作（remove
// 未命中、越界抛错）不失效。首次 moveNext 前或正常返回 false 后 current
// 抛 core.NoSuchElementException，结束后 moveNext 持续 false。裸 Array
// 构造的公开 ListEnumerator（AtomicSnapshot 借用适配器路径）不做失效
// 检测，行为保持现状。
// expect-output: list-sweep-ok
// expect-output: list-ise-add
// expect-output: list-ise-current
// expect-output: list-ise-remove
// expect-output: list-ise-removeat
// expect-output: list-ise-set
// expect-output: list-ise-set-same
// expect-output: list-ise-insert
// expect-output: list-ise-clear-empty
// expect-output: list-ise-clear
// expect-output: list-noelem-after-end
// expect-output: list-oob-set
// expect-output: list-oob-removeat
// expect-output: list-noelem-fresh
// expect-output: list-ise-after-end
// expect-output: list-ise-current-after-end
// expect-output: map-sweep-ok
// expect-output: map-ise-add
// expect-output: map-ise-set-same
// expect-output: map-ise-set
// expect-output: map-ise-remove
// expect-output: map-ise-clear
// expect-output: map-noelem-fresh
// expect-output: map-pair-ok
// expect-output: map-noelem-end
// expect-output: map-ise-after-end
// expect-output: bare-array-ok
// expect-exit: 0
import core.collections.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("遍历失效断言失败 " + step.toString()) }
}

pub func main(): i32 {
    // ── 未修改集合：整趟遍历不受影响，current 逐元素正确 ──
    const l1 = new List\<i32>()
    l1.add(1)
    l1.add(2)
    l1.add(3)
    const it1 = l1.iterate()
    var sum: i32 = 0
    while (it1.moveNext()) { sum = (sum + it1.current()) }
    require((sum == 6))
    core.io.Console.println("list-sweep-ok")

    // ── 迭代中 add：下一次 moveNext 抛 IllegalStateException，current 亦然 ──
    const l2 = new List\<String>()
    l2.add("a")
    l2.add("b")
    l2.add("c")
    const it2 = l2.iterate()
    require(it2.moveNext())
    l2.add("d")
    try {
        it2.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-add")
    }
    try {
        it2.current()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-current")
    }

    // ── 迭代中 remove 命中：失效 ──
    const l3 = new List\<String>()
    l3.add("a")
    l3.add("b")
    const it3 = l3.iterate()
    require(it3.moveNext())
    require(l3.remove("b"))
    try {
        it3.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-remove")
    }

    // ── 迭代中 removeAt：失效 ──
    const l4 = new List\<String>()
    l4.add("a")
    l4.add("b")
    const it4 = l4.iterate()
    require(it4.moveNext())
    l4.removeAt((1 as i64))
    try {
        it4.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-removeat")
    }

    // ── 迭代中 setAtIndex 赋值：失效 ──
    const l5 = new List\<String>()
    l5.add("a")
    l5.add("b")
    const it5 = l5.iterate()
    require(it5.moveNext())
    l5.setAtIndex((1 as i64), "B")
    try {
        it5.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-set")
    }

    // ── 成功赋值写入相同值同样计修改、同样失效 ──
    const l6 = new List\<String>()
    l6.add("a")
    const it6 = l6.iterate()
    require(it6.moveNext())
    l6.setAtIndex((0 as i64), "a")
    try {
        it6.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-set-same")
    }

    // ── 迭代中 insert：失效 ──
    const l7 = new List\<String>()
    l7.add("a")
    const it7 = l7.iterate()
    require(it7.moveNext())
    l7.insert((0 as i64), "head")
    try {
        it7.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-insert")
    }

    // ── 空表成功 clear 同样失效（§4.2.4：成功 clear 即计修改） ──
    const l8 = new List\<String>()
    const it8 = l8.iterate()
    l8.clear()
    try {
        it8.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-clear-empty")
    }

    // ── 非空表 clear：失效 ──
    const l8b = new List\<String>()
    l8b.add("a")
    const it8b = l8b.iterate()
    require(it8b.moveNext())
    l8b.clear()
    try {
        it8b.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-clear")
    }

    // ── 无效果操作不计修改：remove 未命中后枚举器照常走到正常结束 ──
    const l9 = new List\<String>()
    l9.add("x")
    const it9 = l9.iterate()
    require(it9.moveNext())
    require((l9.remove("ghost") == false))
    require((it9.moveNext() == false))
    try {
        it9.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("list-noelem-after-end")
    }
    require((it9.moveNext() == false))

    // ── 越界抛错不计修改：setAtIndex/removeAt 越界后枚举器仍可用 ──
    const l10 = new List\<String>()
    l10.add("x")
    const it10 = l10.iterate()
    require(it10.moveNext())
    try {
        l10.setAtIndex((5 as i64), "y")
        require(false)
    } catch (e: core.OutOfBoundException) {
        core.io.Console.println("list-oob-set")
    }
    try {
        l10.removeAt((9 as i64))
        require(false)
    } catch (e: core.OutOfBoundException) {
        core.io.Console.println("list-oob-removeat")
    }
    require((it10.moveNext() == false))

    // ── 首次 moveNext 前 current 抛 NoSuchElementException；遍历中 current ──
    // ── 返回当前元素；正常结束后 current 抛、moveNext 持续 false ──
    const l11 = new List\<String>()
    l11.add("a")
    const it11 = l11.iterate()
    try {
        it11.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("list-noelem-fresh")
    }
    require(it11.moveNext())
    require((it11.current() == "a"))
    require((it11.moveNext() == false))
    l11.add("b")
    try {
        it11.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-after-end")
    }
    try {
        it11.current()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("list-ise-current-after-end")
    }

    // ── Map：未修改集合整趟遍历，Pair 键值求和正确 ──
    const m = new Map\<String, i32>()
    m.set("a", 1)
    m.set("b", 2)
    const itSweep = m.iterate()
    var msum: i32 = 0
    while (itSweep.moveNext()) { msum = (msum + itSweep.current().value) }
    require((msum == 3))
    core.io.Console.println("map-sweep-ok")

    // ── Map：迭代中 set 新增键 → 失效 ──
    const itAdd = m.iterate()
    require(itAdd.moveNext())
    m.set("c", 3)
    try {
        itAdd.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("map-ise-add")
    }

    // ── Map：覆盖已有键写入相同值也计修改 → 失效 ──
    const itSame = m.iterate()
    require(itSame.moveNext())
    m.set("a", 1)
    try {
        itSame.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("map-ise-set-same")
    }

    // ── Map：覆盖已有键写入不同值 → 失效 ──
    const itOver = m.iterate()
    require(itOver.moveNext())
    m.set("a", 100)
    try {
        itOver.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("map-ise-set")
    }

    // ── Map：remove 未命中不失效（可继续），命中删除后失效 ──
    const itMiss = m.iterate()
    require(itMiss.moveNext())
    require((m.remove("zzz") == false))
    require(itMiss.moveNext())
    require(m.remove("a"))
    try {
        itMiss.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("map-ise-remove")
    }

    // ── Map：clear 后失效 ──
    const itClear = m.iterate()
    require(itClear.moveNext())
    m.clear()
    try {
        itClear.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("map-ise-clear")
    }

    // ── Map：首次 moveNext 前 current 抛 NoSuchElementException；current ──
    // ── 返回的 Pair 键值正确；结束后 current 抛、moveNext 持续 false； ──
    // ── 结束后修改集合 → 失效优先于正常结束 ──
    const m2 = new Map\<String, i32>()
    m2.set("k", 7)
    const itK = m2.iterate()
    try {
        itK.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("map-noelem-fresh")
    }
    require(itK.moveNext())
    require((itK.current().key == "k"))
    require((itK.current().value == 7))
    core.io.Console.println("map-pair-ok")
    require((itK.moveNext() == false))
    try {
        itK.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("map-noelem-end")
    }
    require((itK.moveNext() == false))
    m2.set("z", 9)
    try {
        itK.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("map-ise-after-end")
    }

    // ── 回归：裸 Array 构造的公开 ListEnumerator（AtomicSnapshot 借用
    // ── 适配器路径）不做失效检测，遍历行为保持现状 ──
    const arr = arrayOfElements\<String>("p", "q")
    const bare = new ListEnumerator\<String>(arr, 2)
    require(bare.moveNext())
    require((bare.current() == "p"))
    require(bare.moveNext())
    require((bare.current() == "q"))
    require((bare.moveNext() == false))
    core.io.Console.println("bare-array-ok")
    return 0
}
