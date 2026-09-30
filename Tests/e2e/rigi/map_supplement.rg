// STDLIB §4.2.2（施工块 1-2）：Map.clear/keys/values 行为语料，及既有
// 覆盖写/删除重添加契约断言。keys()/values() 返回独立新 List——修改
// Map 或 clear Map 不影响已返回结果，修改返回的 List 也不影响 Map；
// 覆盖已有键只更新值、保留原键对象和位置；删除后重新添加排在末尾。
// expect-output: 3
// expect-output: a
// expect-output: b
// expect-output: c
// expect-output: 1
// expect-output: 2
// expect-output: 3
// expect-output: 3
// expect-output: true
// expect-output: 0
// expect-output: 3
// expect-output: 9
// expect-output: 1
// expect-output: 2
// expect-output: ONE
// expect-output: 100
// expect-output: 1
// expect-output: 2
// expect-output: 2
// expect-output: 1
// expect-output: again
// expect-exit: 0
import core.collections.*

// 自定义 equals 键：同 tag 视为相等（== 经运行期最派生 operator equals）；
// id 不参与判等，用于确认覆盖写保留的是**原键对象**（id 100 而非 300）
class KeyBox {
    pub var tag: i32
    pub var id: i32
    pub init(t: i32, d: i32) {
        tag = t
        id = d
    }
    pub operator equals(other: KeyBox): bool { return (tag == other.tag) }
}

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("Map 补充方法断言失败 " + step.toString()) }
}

// i64/i32 可空读未命中的回退哨兵（-1）
var missNum: i32 = (0 - 1)

pub func main(): i32 {
    // KeyBox 可空读的回退哨兵（不参与判等的兜底对象；对象非 shared-safe，
    // 不能做全局字段，放局部）
    const missKey: KeyBox = new KeyBox((0 - 1), (0 - 1))

    // ── keys()/values()：按插入顺序返回全部键与值 ──
    const m = new Map\<String, i32>()
    m.set("a", 1)
    m.set("b", 2)
    m.set("c", 3)
    require((m.count == (3 as i64)))
    core.io.Console.println(m.count.toString())
    const ks = m.keys()
    const vs = m.values()
    require((ks.length == (3 as i64)))
    require((vs.length == (3 as i64)))
    require(((ks.getAtIndex((0 as i64)) if? "") == "a"))
    require(((ks.getAtIndex((1 as i64)) if? "") == "b"))
    require(((ks.getAtIndex((2 as i64)) if? "") == "c"))
    require(((vs.getAtIndex((0 as i64)) if? missNum) == 1))
    require(((vs.getAtIndex((1 as i64)) if? missNum) == 2))
    require(((vs.getAtIndex((2 as i64)) if? missNum) == 3))
    core.io.Console.println(ks.getAtIndex((0 as i64)) if? "")
    core.io.Console.println(ks.getAtIndex((1 as i64)) if? "")
    core.io.Console.println(ks.getAtIndex((2 as i64)) if? "")
    core.io.Console.println((vs.getAtIndex((0 as i64)) if? missNum).toString())
    core.io.Console.println((vs.getAtIndex((1 as i64)) if? missNum).toString())
    core.io.Console.println((vs.getAtIndex((2 as i64)) if? missNum).toString())

    // ── 独立性其一：修改 Map 不影响已返回的 keys/values ──
    m.set("d", 4)
    m.remove("a")
    require((m.count == (3 as i64)))
    require((ks.length == (3 as i64)))
    require((vs.length == (3 as i64)))
    require(((ks.getAtIndex((0 as i64)) if? "") == "a"))
    require(((vs.getAtIndex((0 as i64)) if? missNum) == 1))
    core.io.Console.println(ks.length.toString())
    core.io.Console.println(((ks.getAtIndex((0 as i64)) if? "") == "a").toString())

    // ── clear：清空后 count 为 0，已返回 List 不受影响，容器可继续用 ──
    require((vs.length == (3 as i64)))
    m.clear()
    require((m.count == (0 as i64)))
    require((ks.length == (3 as i64)))
    require((vs.length == (3 as i64)))
    require((m.containsKey("a")) == false)
    core.io.Console.println(m.count.toString())
    core.io.Console.println(vs.length.toString())
    m.set("x", 9)
    require((m.count == (1 as i64)))
    require(((m.tryGet("x") if? missNum) == 9))
    core.io.Console.println((m.tryGet("x") if? missNum).toString())

    // ── 独立性其二：修改返回的 List 不影响 Map ──
    const ks2 = m.keys()
    ks2.add("ghost")
    const vs2 = m.values()
    vs2.clear()
    require((m.count == (1 as i64)))
    require((m.containsKey("ghost")) == false)
    require(((m.tryGet("x") if? missNum) == 9))
    core.io.Console.println(m.count.toString())

    // ── 覆盖写：只更新值，保留原键对象和位置 ──
    const m2 = new Map\<KeyBox, String>()
    const k1 = new KeyBox(1, 100)
    const k2 = new KeyBox(2, 200)
    m2.set(k1, "one")
    m2.set(k2, "two")
    const k1b = new KeyBox(1, 300)
    m2.set(k1b, "ONE")
    require((m2.count == (2 as i64)))
    require(((m2.tryGet(new KeyBox(1, 0)) if? "") == "ONE"))
    require(((m2.tryGet(new KeyBox(2, 0)) if? "") == "two"))
    core.io.Console.println(m2.count.toString())
    core.io.Console.println(m2.tryGet(new KeyBox(1, 0)) if? "")
    require(((m2.keyAtIndex((0 as i64)) if? missKey).id == 100))
    core.io.Console.println((m2.keyAtIndex((0 as i64)) if? missKey).id.toString())
    require(((m2.keyAtIndex((0 as i64)) if? missKey).tag == 1))
    require(((m2.keyAtIndex((1 as i64)) if? missKey).tag == 2))
    core.io.Console.println((m2.keyAtIndex((0 as i64)) if? missKey).tag.toString())
    core.io.Console.println((m2.keyAtIndex((1 as i64)) if? missKey).tag.toString())

    // ── 删除后重新添加排在末尾 ──
    require(m2.remove(new KeyBox(1, 0)))
    require((m2.count == (1 as i64)))
    m2.set(new KeyBox(1, 400), "again")
    require((m2.count == (2 as i64)))
    require(((m2.keyAtIndex((0 as i64)) if? missKey).tag == 2))
    require(((m2.keyAtIndex((1 as i64)) if? missKey).tag == 1))
    require(((m2.valueAtIndex((1 as i64)) if? "") == "again"))
    core.io.Console.println((m2.keyAtIndex((0 as i64)) if? missKey).tag.toString())
    core.io.Console.println((m2.keyAtIndex((1 as i64)) if? missKey).tag.toString())
    core.io.Console.println(m2.valueAtIndex((1 as i64)) if? "")
    return 0
}
