// STDLIB §4.2.2（施工块 1-1）：List.clear/contains/indexOf 行为语料。
// 判等一律走既有 ==（equals-or-hash 链）；indexOf 未命中返回 null；
// clear 后 length 回零且容器可继续使用；可空元素（List\<String?>）的
// null 槽参与判等。
// expect-output: 3
// expect-output: 0
// expect-output: 3
// expect-output: true
// expect-output: false
// expect-output: 1
// expect-output: -1
// expect-output: true
// expect-output: 1
// expect-output: true
// expect-output: 0
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
    if (value == false) { throw new core.RuntimeException("List 补充方法断言失败 " + step.toString()) }
}

// indexOf 未命中的回退哨兵（i64 的 -1）
var missI64: i64 = ((0 as i64) - (1 as i64))

pub func main(): i32 {
    // clear：清空后 length 为 0，且容器可继续使用
    const l = new List\<String>()
    l.add("a")
    l.add("b")
    l.add("c")
    require((l.length == (3 as i64)))
    core.io.Console.println(l.length.toString())
    l.clear()
    require((l.length == (0 as i64)))
    core.io.Console.println(l.length.toString())
    require((l.contains("a")) == false)
    l.add("a")
    l.add("b")
    l.add("c")
    require((l.length == (3 as i64)))
    core.io.Console.println(l.length.toString())

    // contains/indexOf：命中与未命中（String 内容判等）
    require(l.contains("b"))
    core.io.Console.println(l.contains("b").toString())
    require((l.contains("z")) == false)
    core.io.Console.println(l.contains("z").toString())
    require(((l.indexOf("b") if? missI64) == (1 as i64)))
    core.io.Console.println((l.indexOf("b") if? missI64).toString())
    require((l.indexOf("z") == null))
    core.io.Console.println((l.indexOf("z") if? missI64).toString())
    require(l.contains("a"))

    // 自定义 equals 对象：== 走 operator equals，与实例身份无关
    const boxes = new List\<Box>()
    boxes.add(new Box(7))
    boxes.add(new Box(8))
    require(boxes.contains(new Box(7)))
    core.io.Console.println(boxes.contains(new Box(7)).toString())
    require(((boxes.indexOf(new Box(8)) if? missI64) == (1 as i64)))
    core.io.Console.println((boxes.indexOf(new Box(8)) if? missI64).toString())
    require((boxes.contains(new Box(9))) == false)
    require((boxes.indexOf(new Box(9)) == null))

    // 可空元素：null 槽与 null 实参按 == 判等，非 null 元素不受干扰
    const ln = new List\<String?>()
    ln.add(null)
    ln.add("x")
    ln.add(null)
    require((ln.length == (3 as i64)))
    require(ln.contains(null))
    core.io.Console.println(ln.contains(null).toString())
    require(ln.contains("x"))
    require((ln.indexOf("q") == null))
    require(((ln.indexOf(null) if? missI64) == (0 as i64)))
    core.io.Console.println((ln.indexOf(null) if? missI64).toString())
    return 0
}
