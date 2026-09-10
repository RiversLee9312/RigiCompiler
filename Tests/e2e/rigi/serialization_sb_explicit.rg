import core.serialization.*
// expect-output: sb-explicit-ok
// expect-exit: 0
@Serializable
rich struct Payload\<T with Serializable> {
    pub var value: T
    pub init(_ -> value)
}
func copy\<T with Serializable>(value: T, graph: bool): T {
    if (not (value with Serializable)) { throw new core.RuntimeException("缺少实际 Serializable 应用") }
    return deepCopy(value, graph)
}
pub func main(): i32 {
    var i: i32 = 0
    while (i < 64) {
        const graph = (i < 32)
        if (copy(i, graph) != i) { return 1 }
        if (copy(i as i64, graph) != (i as i64)) { return 2 }
        if (copy(i as u64, graph) != (i as u64)) { return 3 }
        if (copy(i as i16, graph) != (i as i16)) { return 4 }
        if (copy(i as u16, graph) != (i as u16)) { return 5 }
        if (copy(i as i8, graph) != (i as i8)) { return 6 }
        if (copy(i as u8, graph) != (i as u8)) { return 7 }
        if (copy(i as u32, graph) != (i as u32)) { return 8 }
        if (copy(i as float, graph) != (i as float)) { return 9 }
        if (copy(i as double, graph) != (i as double)) { return 10 }
        if (copy(graph, graph) != graph) { return 11 }
        if (copy('中', graph) != '中') { return 12 }
        const text = "序列化-" + i.toString()
        if (copy(text, graph) != text) { return 13 }
        const wrapped = copy(new Payload\<i32>(i), graph)
        if (wrapped.value != i) { return 14 }
        const p = i:Serializable.toParcel(graph)
        if (fromParcel\<i32>(p, graph) != i) { return 15 }
        var rejected = false
        try { const wrong = fromParcel\<String>(p, graph) }
        catch (e: core.CastException) { rejected = true }
        if (not rejected) { return 16 }
        i = i + 1
    }
    core.io.Console.println("sb-explicit-ok")
    return 0
}
