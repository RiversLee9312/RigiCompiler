// 不显式写 typeOf(Probe)，确保元数据由实际调用的数据流收集。
// expect-output: dynamic-type-ok
// expect-exit: 0
class Probe { pub var n: i32 = 7 }
rich struct Packet { pub var value: Probe
    pub init(_ -> value) }
class StoredProbe { pub var n: i32 = 9 }
rich struct StoredPacket { pub var value: StoredProbe
    pub init(_ -> value) }
func describe(value: Any): String { return typeOf(value).toString() }
func relay(value: Any): String { return describe(value) }
pub func main(): i32 {
    var i = 0
    while (i < 16) {
        if (relay(new Probe()) != "Probe") { return 1 }
        if (relay(i) != ".i32") { return 2 }
        if (relay("value") != ".string") { return 3 }
        if (relay(new Packet(new Probe())) != "Packet") { return 4 }
        // 这两个类型只通过 Any 容器读取，不能被前面的直接传参预先登记。
        const values = core.collections.arrayOf\<Any>(2)
        values[0] = new StoredProbe()
        values[1] = new StoredPacket(new StoredProbe())
        if (relay(values[0] as Any) != "StoredProbe") { return 5 }
        if (relay(values[1] as Any) != "StoredPacket") { return 6 }
        i += 1
    }
    core.io.Console.println("dynamic-type-ok")
    return 0
}
