import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: json-nested-map-wire-extended-ok
// expect-exit: 0
// e2e-slow-gate: 多闭合容器类型的写侧组合边界，手动定向执行。
// §4.7：Map 的 List/Array/可空元素/嵌套值、空 Map 和非 String 键。
// 核心单字段 Map 对拍见 json_nested_map_wire.rg；本例不并入默认 native 对拍。

@Serializable()
class JmwHolder {
    pub var byName: Map\<String, i32>
    pub var maps: List\<Map\<String, i32>>
    pub var arrayMaps: Array\<Map\<String, i32>>
    pub var optionalMaps: List\<Map\<String, i32>?>
    pub var nestedMap: Map\<String, Map\<String, i32>>
    pub var numbers: Array\<i32>
    pub var empty: Map\<String, i32>
    pub init(_ -> byName, _ -> maps, _ -> arrayMaps, _ -> optionalMaps,
        _ -> nestedMap, _ -> numbers, _ -> empty)
}

@Serializable()
class JmwNonString {
    pub var byNumber: Map\<i32, String>
    pub init(_ -> byNumber)
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
    const maps = new List\<Map\<String, i32>>()
    maps.add(byName)
    maps.add(new Map\<String, i32>())
    const arrayMaps = core.collections.arrayOfElements\<Map\<String, i32>>(byName)
    const optionalMaps = new List\<Map\<String, i32>?>()
    optionalMaps.add(null)
    optionalMaps.add(byName)
    const nestedMap = new Map\<String, Map\<String, i32>>()
    nestedMap.set("inner", byName)
    const nums = core.collections.arrayOfElements\<i32>((2 as i32), (4 as i32))
    const holder = new JmwHolder(byName, maps, arrayMaps, optionalMaps,
        nestedMap, nums, new Map\<String, i32>())
    const wire = holder:Serializable.toParcel()
    const typed = jmwWrite(wire, true)
    const mapId = "core.collections::Map<.string, .i32>"
    const mapTyped = "{\".rigi.type-identifier\":\"${mapId}\",\"content\":[{\"key\":\"a\",\"value\":7}]}"
    const emptyTyped = "{\".rigi.type-identifier\":\"${mapId}\",\"content\":[]}"
    const nestedTyped = "{\".rigi.type-identifier\":\"core.collections::Map<.string, core.collections::Map<.string, .i32>>\",\"content\":[{\"key\":\"inner\",\"value\":${mapTyped}}]}"
    const expectedTyped = "{\".rigi.type-identifier\":\"JmwHolder\",\"byName\":${mapTyped},\"maps\":[${mapTyped},${emptyTyped}],\"arrayMaps\":[${mapTyped}],\"optionalMaps\":[null,${mapTyped}],\"nestedMap\":${nestedTyped},\"numbers\":[2,4],\"empty\":${emptyTyped}}"
    if (typed != expectedTyped) {
        Console.println("FAIL typed: " + typed)
        return 1
    }
    const plain = jmwWrite(wire, false)
    const expectedPlain = "{\"byName\":{\"a\":7},\"maps\":[{\"a\":7},{}],\"arrayMaps\":[{\"a\":7}],\"optionalMaps\":[null,{\"a\":7}],\"nestedMap\":{\"inner\":{\"a\":7}},\"numbers\":[2,4],\"empty\":{}}"
    if (plain != expectedPlain) {
        Console.println("FAIL plain: " + plain)
        return 2
    }
    const nonString = new JmwNonString(new Map\<i32, String>())
    const intTyped = jmwWrite(nonString:Serializable.toParcel(), true)
    if (intTyped != "{\".rigi.type-identifier\":\"JmwNonString\",\"byNumber\":{\".rigi.type-identifier\":\"core.collections::Map<.i32, .string>\",\"content\":[]}}") {
        Console.println("FAIL non-string typed: " + intTyped)
        return 3
    }
    var rejected = false
    try {
        const ignored = jmwWrite(nonString:Serializable.toParcel(), false)
    } catch (e: JsonException) {
        rejected = true
    }
    if (not rejected) { return 4 }
    Console.println("json-nested-map-wire-extended-ok")
    return 0
}
