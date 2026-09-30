import core.collections.*
import core.io.*
import core.serialization.json.*
import core.text.*
// expect-output: json-map-reserved-key-smoke-ok
// expect-exit: 0
// §4.7.1/4：目标为 String 键 Map 时，保留形名字与普通字符串值均为业务数据。

pub func main(): i32 {
    const text = "{\".rigi.type-identifier\":\"ordinary\",\"content\":\"x\"}"
    const stream = new MemoryInputStream((new Utf8Encoder()).encode(text))
    var map = new Map\<String, String>()
    try {
        map = (new JsonSerializer()).readAs\<Map\<String, String>>(stream)
    } finally(e) {
        stream.dispose()
    }
    if (map.keys().length != (2 as i64)) { return 1 }
    if ((map.tryGet(".rigi.type-identifier") if? "") != "ordinary") { return 2 }
    if ((map.tryGet("content") if? "") != "x") { return 3 }
    Console.println("json-map-reserved-key-smoke-ok")
    return 0
}
