// STDLIB §4.2.2/§4.2.4（施工块 1-4）：Set 基本行为语料。
// add/remove/contains/clear/count 返回值语义（重复 add false、删除不存在
// false 且不修改）；插入序遍历、删除后重新添加排在末尾；判等严格走既有
// ==（equals-or-hash 链）：自定义 equals 对象按 tag 判等，可空元素
// Set\<String?> 含 null 槽，NaN 不等于自身（可出现多个、按 NaN 查找不
// 匹配），±0 按既有数值相等。
// 修改检测（§4.2.4）：成功的 add（新增）/remove（命中）/clear（含空表
// clear）计修改，使既有枚举器失效——之后的 moveNext/current 抛
// core.IllegalStateException（失效优先于正常结束）；重复 add、未命中
// remove 不计修改，枚举器照常推进。元素对象内部字段变化不属于容器修改。
// 首次 moveNext 前或正常返回 false 后 current 抛
// core.NoSuchElementException；结束后 moveNext 持续 false。
// expect-output: set-basic-ok
// expect-output: 1
// expect-output: a;b;c;
// expect-output: a;c;b;
// expect-output: a;c;b;
// expect-output: set-equals-ok
// expect-output: 1
// expect-output: set-null-ok
// expect-output: set-nan-ok
// expect-output: 2
// expect-output: set-zero-ok
// expect-output: set-ise-add
// expect-output: set-ise-current
// expect-output: set-ise-remove
// expect-output: set-ise-clear
// expect-output: set-ise-clear-empty
// expect-output: set-noeffect-ok
// expect-output: set-noelem-fresh
// expect-output: set-noelem-end
// expect-output: set-ise-after-end
// expect-exit: 0
import core.collections.*

// 自定义 equals 对象：同 tag 视为相等（== 经运行期最派生 operator equals）
class Box {
    pub var tag: i32
    pub init(t: i32) { tag = t }
    pub operator equals(other: Box): bool { return (tag == other.tag) }
}

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("Set 断言失败 " + step.toString()) }
}

pub func main(): i32 {
    // ── add/count/contains/remove 返回值语义 ──
    const s = new Set\<String>()
    require(s.add("a"))
    require(s.add("b"))
    // 重复 add：返回 false，不修改集合
    require((s.add("a")) == false)
    require((s.count == (2 as i64)))
    core.io.Console.println("set-basic-ok")
    require(s.contains("a"))
    require((s.contains("z")) == false)
    require(s.remove("a"))
    // 删除不存在的元素：返回 false，不修改集合
    require((s.remove("a")) == false)
    require((s.remove("ghost")) == false)
    require((s.count == (1 as i64)))
    core.io.Console.println(s.count.toString())

    // ── 插入顺序遍历；删除后重新添加排在末尾；重复 add 不扰动顺序 ──
    const s2 = new Set\<String>()
    s2.add("a")
    s2.add("b")
    s2.add("c")
    const it1 = s2.iterate()
    var acc: String = ""
    while (it1.moveNext()) {
        acc = (acc + (it1.current() + ";"))
    }
    core.io.Console.println(acc)
    require(s2.remove("b"))
    require(s2.add("b"))
    const it2 = s2.iterate()
    var acc2: String = ""
    while (it2.moveNext()) {
        acc2 = (acc2 + (it2.current() + ";"))
    }
    core.io.Console.println(acc2)
    // 重复 add（false）不改变插入顺序
    require((s2.add("a")) == false)
    const it3 = s2.iterate()
    var acc3: String = ""
    while (it3.moveNext()) {
        acc3 = (acc3 + (it3.current() + ";"))
    }
    core.io.Console.println(acc3)

    // ── 自定义 equals 对象：== 走 operator equals，与实例身份无关 ──
    const boxes = new Set\<Box>()
    require(boxes.add(new Box(7)))
    // 同 tag 的新实例视为相等 → 重复添加返回 false，不修改
    require((boxes.add(new Box(7))) == false)
    require(boxes.contains(new Box(7)))
    require((boxes.contains(new Box(9))) == false)
    require(boxes.remove(new Box(7)))
    require((boxes.count == (0 as i64)))
    core.io.Console.println("set-equals-ok")
    require(boxes.add(new Box(1)))
    require((boxes.count == (1 as i64)))
    core.io.Console.println(boxes.count.toString())

    // ── 可空元素：null 是合法元素并参与判等，非 null 元素不受干扰 ──
    const sn = new Set\<String?>()
    require(sn.add(null))
    require(sn.add("x"))
    // 重复添加 null：返回 false
    require((sn.add(null)) == false)
    require((sn.count == (2 as i64)))
    require(sn.contains(null))
    require(sn.contains("x"))
    require(sn.remove(null))
    require((sn.contains(null)) == false)
    core.io.Console.println("set-null-ok")

    // ── NaN 不等于自身：可出现多个 NaN，按 NaN 查找不匹配（§4.2.2）──
    const nan: double = (0.0 / 0.0)
    const sd = new Set\<double>()
    require(sd.add(nan))
    // 首个 NaN 查不中 → 第二次 add 仍是"新增"，返回 true
    require(sd.add(nan))
    require((sd.count == (2 as i64)))
    require((sd.contains(nan)) == false)
    core.io.Console.println("set-nan-ok")
    core.io.Console.println(sd.count.toString())

    // ── ±0 按既有数值相等：+0 与 -0 判等，不重复入集 ──
    const negZero: double = -(0.0)
    const sz = new Set\<double>()
    require(sz.add((0.0 as double)))
    require((sz.add(negZero)) == false)
    require((sz.count == (1 as i64)))
    core.io.Console.println("set-zero-ok")

    // ── 修改检测：迭代中 add 新元素 → 下一次 moveNext 抛 IllegalStateException，
    // ── current 亦然（失效优先）──
    const sm = new Set\<String>()
    sm.add("a")
    sm.add("b")
    const itAdd = sm.iterate()
    require(itAdd.moveNext())
    require(sm.add("c"))
    try {
        itAdd.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("set-ise-add")
    }
    try {
        itAdd.current()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("set-ise-current")
    }

    // ── 迭代中 remove 命中 → 失效 ──
    const sr = new Set\<String>()
    sr.add("a")
    sr.add("b")
    const itRemove = sr.iterate()
    require(itRemove.moveNext())
    require(sr.remove("b"))
    try {
        itRemove.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("set-ise-remove")
    }

    // ── 迭代中 clear（非空）→ 失效；空表 clear 同样计修改（§4.2.4）──
    const sc = new Set\<String>()
    sc.add("a")
    const itClear = sc.iterate()
    require(itClear.moveNext())
    sc.clear()
    try {
        itClear.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("set-ise-clear")
    }
    const se = new Set\<String>()
    const itEmpty = se.iterate()
    se.clear()
    try {
        itEmpty.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("set-ise-clear-empty")
    }

    // ── 无效果操作不计修改：重复 add（false）与未命中 remove（false）后
    // ── 枚举器照常推进到正常结束，current 可用 ──
    const sn2 = new Set\<String>()
    sn2.add("x")
    const itNoFx = sn2.iterate()
    require(itNoFx.moveNext())
    require((sn2.add("x")) == false)
    require((sn2.remove("ghost")) == false)
    require((itNoFx.current() == "x"))
    require((itNoFx.moveNext()) == false)
    core.io.Console.println("set-noeffect-ok")

    // ── 首次 moveNext 前 current 抛 NoSuchElementException；正常结束后
    // ── current 抛、moveNext 持续 false；结束后修改集合 → 失效优先于结束 ──
    const sf = new Set\<String>()
    sf.add("k")
    const itFresh = sf.iterate()
    try {
        itFresh.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("set-noelem-fresh")
    }
    require(itFresh.moveNext())
    require((itFresh.current() == "k"))
    require((itFresh.moveNext()) == false)
    try {
        itFresh.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("set-noelem-end")
    }
    require((itFresh.moveNext()) == false)
    sf.add("z")
    try {
        itFresh.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("set-ise-after-end")
    }
    return 0
}
