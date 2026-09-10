// 循环产出临时槽在第二次调用抛异常时，旧值只能释放一次。
// 覆盖对象、动态字符串、rich 值及嵌套 catch 后继续使用原引用。
// expect-output: arc-exception-ok
// expect-exit: 0
class Item {
    pub var n: i32
    pub init(_ -> n)
}
rich struct Packet {
    pub var item: Item
    pub var text: String
    pub init(_ -> item, _ -> text)
}
func objectResult(i: i32, item: Item): Item {
    if (i == 1) { throw new IllegalStateException("object") }
    return item
}
func stringResult(i: i32): String {
    if (i == 1) { throw new IllegalStateException("string") }
    return "allocated-" + i.toString()
}
func richResult(i: i32, item: Item): Packet {
    if (i == 1) { throw new IllegalStateException("rich") }
    return new Packet(item, "packet-" + i.toString())
}
pub func main(): i32 {
    var round = 0
    while (round < 16) {
        const item = new Item(round)
        var caught = 0
        var i = 0
        try {
            while (i < 2) {
                const value = objectResult(i, item)
                if (value.n != round) { return 1 }
                i += 1
            }
        } catch (e: IllegalStateException) { caught += 1 }
        i = 0
        try {
            while (i < 2) {
                const value = stringResult(i)
                if (value != "allocated-0") { return 2 }
                i += 1
            }
        } catch (e: IllegalStateException) { caught += 1 }
        i = 0
        try {
            while (i < 2) {
                const value = richResult(i, item)
                if ((value.item.n != round) or (value.text != "packet-0")) { return 3 }
                i += 1
            }
        } catch (e: IllegalStateException) { caught += 1 }
        if ((caught != 3) or (item.n != round)) { return 4 }
        round += 1
    }
    core.io.Console.println("arc-exception-ok")
    return 0
}
