// STDLIB §4.2.2（施工块 1-1）：List.insert/remove 行为语料。
// insert 接受 0..length（length 处即追加），非法位置（负、大于 length）
// 抛 core.OutOfBoundException；remove 删除首个相等元素，重复元素只删
// 首个，无匹配不修改返回 false。
// expect-output: 4
// expect-output: a
// expect-output: m
// expect-output: b
// expect-output: c
// expect-output: 数组下标越界：-1（长度 4）
// expect-output: 数组下标越界：5（长度 4）
// expect-output: true
// expect-output: false
// expect-output: true
// expect-output: 1
// expect-output: true
// expect-output: 1
// expect-output: 1
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
    // insert：头部 / 中间 / 末尾（length 处即追加）
    const l = new List\<String>()
    l.add("b")
    l.insert((0 as i64), "a")
    l.insert((1 as i64), "m")
    l.insert(l.length, "c")
    require((l.length == (4 as i64)))
    core.io.Console.println(l.length.toString())
    var i: i64 = (0 as i64)
    while (i < l.length) {
        core.io.Console.println((l.getAtIndex(i) as String))
        i = (i + (1 as i64))
    }

    // 非法位置：负 index 与大于 length 抛 core.OutOfBoundException
    //（消息模板与 setAtIndex/removeAt 同款）
    var caught: i32 = 0
    try {
        l.insert(((0 as i64) - (1 as i64)), "v")
        caught = (caught + 100)
    }
    catch(e: core.OutOfBoundException) {
        caught = (caught + 1)
        core.io.Console.println(e.getMessage())
    }
    try {
        l.insert((5 as i64), "v")
        caught = (caught + 100)
    }
    catch(e: core.OutOfBoundException) {
        caught = (caught + 1)
        core.io.Console.println(e.getMessage())
    }
    require((caught == 2))
    require((l.length == (4 as i64)))
    require(((l.getAtIndex((0 as i64)) as String) == "a"))

    // 空表 insert(0) 合法
    const empty = new List\<i32>()
    empty.insert((0 as i64), 9)
    require((empty.length == (1 as i64)))
    require(((empty.getAtIndex((0 as i64)) if? (0 - 1)) == 9))

    // remove：命中删除首个相等元素；重复元素只删首个；未命中返回 false
    // 且不修改
    const r = new List\<String>()
    r.add("p")
    r.add("q")
    r.add("p")
    const firstGone = r.remove("p")
    require(firstGone)
    core.io.Console.println(firstGone.toString())
    require((r.length == (2 as i64)))
    require(((r.getAtIndex((0 as i64)) as String) == "q"))
    require(((r.getAtIndex((1 as i64)) as String) == "p"))
    const noHit = r.remove("z")
    require((noHit == false))
    core.io.Console.println(noHit.toString())
    require((r.length == (2 as i64)))
    const secondGone = r.remove("p")
    require(secondGone)
    core.io.Console.println(secondGone.toString())
    require((r.length == (1 as i64)))
    core.io.Console.println(r.length.toString())
    require((r.contains("p")) == false)
    require(r.contains("q"))

    // 自定义 equals 对象的重复元素同样只删首个
    const boxes = new List\<Box>()
    boxes.add(new Box(7))
    boxes.add(new Box(8))
    boxes.add(new Box(7))
    const goneBox = boxes.remove(new Box(7))
    require(goneBox)
    core.io.Console.println(goneBox.toString())
    require((boxes.length == (2 as i64)))
    require(boxes.contains(new Box(7)))
    require(((boxes.indexOf(new Box(7)) if? missI64) == (1 as i64)))
    core.io.Console.println((boxes.indexOf(new Box(7)) if? missI64).toString())

    // 可空元素的 remove：null 元素命中删除，剩余 null 位置右移
    const ln = new List\<String?>()
    ln.add(null)
    ln.add("x")
    ln.add(null)
    const goneNull = ln.remove(null)
    require(goneNull)
    require((ln.length == (2 as i64)))
    require(((ln.indexOf(null) if? missI64) == (1 as i64)))
    core.io.Console.println((ln.indexOf(null) if? missI64).toString())
    return 0
}
