import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: json-map-reserved-key-ok
// expect-exit: 0
// §4.7.1/4：String 值的保留形业务键不是对象类型元数据。

@Serializable()
class KeyValue {
    pub var name: String
    pub init(_ -> name)
}

@Serializable()
class KeyHolder {
    pub var item: Map\<String, String>
    pub var items: Array\<Map\<String, String>>
    pub init(_ -> item, _ -> items)
}

func readKey\<T with Serializable>(ser: JsonSerializer, text: String): T {
    const stream = new MemoryInputStream((new Utf8Encoder()).encode(text))
    try {
        return ser.readAs\<T>(stream)
    } finally(e) {
        stream.dispose()
    }
}

func hasKeys(map: Map\<String, String>): bool {
    return (((map.keys().length == (2 as i64)) and
        ((map.tryGet(".rigi.type-identifier") if? "") == "ordinary")) and
        ((map.tryGet("content") if? "") == "x"))
}

func rejectsBadWrapper(ser: JsonSerializer, text: String): bool {
    const stream = new MemoryInputStream((new Utf8Encoder()).encode(text))
    var rejected = false
    try {
        const ignored = ser.readAs\<Map\<String, String>>(stream)
    } catch (e: JsonException) {
        rejected = true
    } finally(e) {
        stream.dispose()
    }
    return rejected
}

pub func main(): i32 {
    const ser = new JsonSerializer()
    const plain = "{\".rigi.type-identifier\":\"ordinary\",\"content\":\"x\"}"
    if (not hasKeys(readKey\<Map\<String, String>>(ser, plain))) { return 1 }
    const holder = readKey\<KeyHolder>(ser,
        "{\"item\":${plain},\"items\":[${plain}]}")
    if (not hasKeys(holder.item)) { return 2 }
    if ((holder.items.length != (1 as i32)) or
            (not hasKeys((holder.items[0] as Map\<String, String>)))) { return 3 }

    const id = "core.collections::Map<.string, .string>"
    const wrapped = "{\".rigi.type-identifier\":\"${id}\",\"content\":[{\"key\":\"content\",\"value\":\"x\"}]}"
    const actual = readKey\<Map\<String, String>>(ser, wrapped)
    if ((actual.keys().length != (1 as i64)) or
            ((actual.tryGet("content") if? "") != "x")) { return 4 }
    if (not rejectsBadWrapper(ser,
            "{\".rigi.type-identifier\":\"${id}\"}")) { return 5 }
    if (not rejectsBadWrapper(ser,
            "{\".rigi.type-identifier\":\"${id}\",\"content\":\"x\"}")) { return 6 }
    // 对象值目标的顶层 envelope 与普通标量 Map 不同，仍应保留业务键。
    const objectMap = readKey\<Map\<String, KeyValue>>(ser,
        "{\".rigi.type-identifier\":{\"name\":\"ordinary\"},\"content\":{\"name\":\"x\"}}")
    if ((objectMap.keys().length != (2 as i64)) or
            ((objectMap.tryGet(".rigi.type-identifier") if? (new KeyValue(""))).name != "ordinary")) { return 8 }
    if ((objectMap.tryGet("content") if? (new KeyValue(""))).name != "x") { return 9 }
    // 非 String 键不允许将普通对象的成员名转换成键。
    const stream = new MemoryInputStream((new Utf8Encoder()).encode(plain))
    var rejected = false
    try {
        const ignored = ser.readAs\<Map\<i32, String>>(stream)
    } catch (e: JsonException) {
        rejected = true
    } finally(e) {
        stream.dispose()
    }
    if (not rejected) { return 7 }
    // 无目标 read 仍将明确的非 Map 类型标识留在 Parcel 中。
    const dynamicStream = new MemoryInputStream((new Utf8Encoder()).encode(plain))
    var dynamic: Any? = null
    try {
        dynamic = ser.read(dynamicStream)
    } finally(e) {
        dynamicStream.dispose()
    }
    if (not (dynamic is Parcel)) { return 10 }
    if ((dynamic as Parcel).typeName != "ordinary") { return 11 }
    Console.println("json-map-reserved-key-ok")
    return 0
}
