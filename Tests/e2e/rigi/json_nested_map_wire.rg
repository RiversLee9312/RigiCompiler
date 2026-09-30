import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: json-nested-map-wire-ok
// expect-exit: 0
// §4.7.3/4：Serializable 对象的 Map 字段经 toParcel 的内部交替键值
// wire，写出时必须恢复成 Map 外观；根 Map 已由 json_write 覆盖。

@Serializable()
class JmwHolder {
    pub var byName: Map\<String, i32>
    pub init(_ -> byName)
}

func jmwWrite(value: Any?, typed: bool): String {
    const stream = new MemoryOutputStream()
    try {
        (new JsonSerializer()).write(value, stream, typed)
        return (new Utf8Decoder()).decode(stream.toSpan(), true)
    } finally(e) {
        stream.dispose()
    }
}

pub func main(): i32 {
    const byName = new Map\<String, i32>()
    byName.set("a", (7 as i32))
    const wire = (new JmwHolder(byName)):Serializable.toParcel()
    const typed = jmwWrite(wire, true)
    const expectedTyped = "{\".rigi.type-identifier\":\"JmwHolder\",\"byName\":{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[{\"key\":\"a\",\"value\":7}]}}"
    if (typed != expectedTyped) {
        Console.println("FAIL typed: " + typed)
        return 1
    }
    const plain = jmwWrite(wire, false)
    const expectedPlain = "{\"byName\":{\"a\":7}}"
    if (plain != expectedPlain) {
        Console.println("FAIL plain: " + plain)
        return 2
    }
    Console.println("json-nested-map-wire-ok")
    return 0
}
