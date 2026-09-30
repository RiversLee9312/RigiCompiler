import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: json-write-ok
// expect-exit: 0
// 施工块 5-2a：JsonSerializer 写出面（§4.7 写侧）语料。
// 覆盖：
//   - 契约固定示例逐字断言（§4.7.3 Map<String, i32> content 条目数组；
//     §4.7.4 keepTypeInfo=false 的 Map 互操作输出；RequestResult.Failed
//     保留类型信息 / 互操作两形态；无载荷枚举互操作 case 名字符串）；
//   - 全值域：标量/String（转义）/char（补充平面）/bool/null/Array/List/
//     嵌套对象/可空字段；空对象 {} 与空数组 [] 区分；
//   - 类型标识：嵌套对象实际类型、多态字段实际派生类型、泛型完整类型名；
//   - 引用语义（§4.7.2）：自环 Parcel 拒绝；显式图模式 Parcel 即使无环
//     也拒绝；无环重复引用展开为独立副本（同引用两字段 → 两份独立
//     对象文本）；写出错误 offset = -1（无虚构输入偏移）；
//   - Map：非 String 键（保留类型信息正常 / 互操作报错，含空 Map）；
//     空 Map 两形式（content [] / {}）；插入序；
//   - 数字：i64/u64 最大边界、浮点最短往返、负零、NaN/±Infinity 报错；
//   - 缩进配置：紧凑默认 vs 基础缩进输出形态；容器深度上限（默认 256、
//     可配置更小）；标量根不占容器深度；
//   - 借用语义（§4.6.2）：成功/失败后调用者流未关闭未 flush（探测计
//     数）；serialize/serializeToString 便利层（toParcel → write）走通；
//   - 同一实例顺序复用；编码构造选项校验（UTF-8 拼写并收，其他报错）；
//     read 空文档拒绝（5-2b 后 read 已实现：JsonException 偏移 0，
//     读侧完整语料见 json_read.rg）。
//
// Parcel 的集合字段使用内部 .array<.any> wire；JSON 写侧按声明类型
// 还原 Map 的业务表示，绝不把交替键值序列当作 JSON 数组输出。

// ── 业务语料类型 ──

@Serializable()
class JsonLeaf {
    pub var flag: bool
    pub var label: String
    pub init(_ -> flag, _ -> label)
}

@Serializable()
open class JsonBase {
    pub var id: i32
    pub init(_ -> id)
}

@Serializable()
class JsonDerived : JsonBase {
    pub var extra: String
    pub init(newId: i32, newExtra: String) {
        super(newId)
        extra = newExtra
    }
}

@Serializable()
class JsonPolyHolder {
    pub var member: JsonBase
    pub init(_ -> member)
}

@Serializable()
class JsonTwo {
    pub var a: JsonLeaf
    pub var b: JsonLeaf
    pub init(_ -> a, _ -> b)
}

@Serializable()
class JsonBox\<T with Serializable> {
    pub var item: T
    pub init(_ -> item)
}

@Serializable()
class JsonNest {
    pub var num: i32
    pub var note: String?
    pub var child: JsonLeaf
    pub var scores: List\<i32>
    pub init(_ -> num, _ -> note, _ -> child, _ -> scores)
}

@Serializable()
class JsonEmpty {
    pub init()
}

@Serializable()
pub enum struct JsonMark {} [Low -> 1, High -> 2]

// 契约固定示例用枚举（rich）：Failed 带 errorCode: i32 载荷；Ok 与
// Failed 同枚举（rich enum 的 case 按既有绑定先例全 case 绑定载荷形参；
// 无载荷 case 的互操作形态由 JsonMark 覆盖）。
@Serializable()
pub rich enum struct RequestResult {
    pub var errorCode: i32
    pub init(_ -> errorCode)
} [Ok(errorCode = _) -> 0, Failed(errorCode = _) -> 1]

// ── 探测流：借用语义计数（serializer_base.rg 同款）──

class JsonProbeStream : OutputStream {
    pub const sink: List\<u8>
    pub var writeCalls: i32 = 0
    pub var flushCalls: i32 = 0
    pub var disposeRuns: i32 = 0

    pub init() {
        sink = new List\<u8>()
    }

    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        writeCalls = (writeCalls + 1)
        ensureOpen()
        checkRange(buffer, offset, count)
        var i: i32 = 0
        while (i < count) {
            sink.add((buffer[offset + i] as u8))
            i = (i + 1)
        }
    }

    pub override func flush() {
        flushCalls = (flushCalls + 1)
        ensureOpen()
    }

    protected override func disposeCore() {
        disposeRuns = (disposeRuns + 1)
    }
}

// ── 断言助手 ──

// 写出为文本（MemoryOutputStream 收字节，Utf8Decoder 严格回读）。
func jsonWriteMode(ser: JsonSerializer, value: Any?,
        keepTypeInfo: bool): String {
    const out = new MemoryOutputStream()
    try {
        ser.write(value, out, keepTypeInfo)
        const decoder = new Utf8Decoder()
        return decoder.decode(out.toSpan(), true)
    } finally(e) {
        out.dispose()
    }
}

func jsonWrite(ser: JsonSerializer, value: Any?): String {
    return jsonWriteMode(ser, value, true)
}

func expectJson(actual: String, want: String, code: i32): i32 {
    if (actual == want) { return 0 }
    Console.println("FAIL ${code}: got [${actual}] want [${want}]")
    return code
}

func expectJsonContains(actual: String, part: String, code: i32): i32 {
    if (actual.contains(part)) { return 0 }
    Console.println("FAIL ${code}: [${actual}] 缺少 [${part}]")
    return code
}

// ── 契约固定示例期望文本（逐字，§4.7.3 / §4.7.4）──

// §4.7.3 Map<String, i32> 保留类型信息固定表示。
func wantMapTyped(): String {
    return "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[{\"key\":\".rigi.type-identifier\",\"value\":1},{\"key\":\"content\",\"value\":42},{\"key\":\"a.b\",\"value\":3},{\"key\":\"\",\"value\":0}]}"
}

// §4.7.4 同一 Map 关闭类型信息后的互操作输出（四成员皆业务键）。
func wantMapInterop(): String {
    return "{\".rigi.type-identifier\":1,\"content\":42,\"a.b\":3,\"\":0}"
}

// §4.7.4 RequestResult.Failed 保留类型信息固定形态。
func wantFailedTyped(): String {
    return "{\".rigi.type-identifier\":\"RequestResult\",\".rigi.enum-case\":\"Failed\",\"errorCode\":404}"
}

// §4.7.4 RequestResult.Failed 互操作固定形态。
func wantFailedInterop(): String {
    return "{\"case\":\"Failed\",\"errorCode\":404}"
}

pub func main(): i32 {
    const ser = new JsonSerializer()
    var rc: i32 = 0

    // ── A. 契约固定示例逐字断言 ──
    // A1（§4.7.3）：Map<String, i32> 保留类型信息的固定表示。
    const table = new Map\<String, i32>()
    table.set(".rigi.type-identifier", (1 as i32))
    table.set("content", (42 as i32))
    table.set("a.b", (3 as i32))
    table.set("", (0 as i32))
    rc = expectJson(jsonWrite(ser, table), wantMapTyped(), (11 as i32))
    if (rc != 0) { return rc }
    // A2（§4.7.4）：同一 Map 关闭类型信息后的互操作输出（.rigi.type-
    // identifier 业务键原样保留）。
    rc = expectJson(jsonWriteMode(ser, table, false), wantMapInterop(), (12 as i32))
    if (rc != 0) { return rc }
    // A3（§4.7.4）：RequestResult.Failed 保留类型信息固定形态。
    rc = expectJson(jsonWrite(ser, RequestResult.Failed(404):Serializable.toParcel()), wantFailedTyped(), (13 as i32))
    if (rc != 0) { return rc }
    // A4（§4.7.4）：RequestResult.Failed 互操作固定形态。
    rc = expectJson(jsonWriteMode(ser, RequestResult.Failed(404):Serializable.toParcel(), false), wantFailedInterop(), (14 as i32))
    if (rc != 0) { return rc }
    // A5/A6：同枚举无载荷语义由 JsonMark 覆盖（见下）；此处断言 Ok
    // case 的两形态（rich enum case 全 case 绑定载荷字段的既有先例）。
    rc = expectJson(jsonWrite(ser, RequestResult.Ok(0):Serializable.toParcel()), "{\".rigi.type-identifier\":\"RequestResult\",\".rigi.enum-case\":\"Ok\",\"errorCode\":0}", (15 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWriteMode(ser, RequestResult.Ok(0):Serializable.toParcel(), false), "{\"case\":\"Ok\",\"errorCode\":0}", (16 as i32))
    if (rc != 0) { return rc }
    // 无载荷枚举（非 rich）：两形态。
    rc = expectJson(jsonWrite(ser, JsonMark.High:Serializable.toParcel()), "{\".rigi.type-identifier\":\"JsonMark\",\".rigi.enum-case\":\"High\"}", (17 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWriteMode(ser, JsonMark.High:Serializable.toParcel(), false), "\"High\"", (18 as i32))
    if (rc != 0) { return rc }

    // ── B. 全值域 ──
    // B1：标量根（bool/int/null）。
    rc = expectJson(jsonWrite(ser, true), "true", (21 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWrite(ser, (5 as i32)), "5", (22 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWrite(ser, null), "null", (23 as i32))
    if (rc != 0) { return rc }
    // B2：String 转义（引号/反斜杠/换行/制表）与控制字符 \u00XX 兜底。
    rc = expectJson(jsonWrite(ser, "a\"b\\c\nd\te"), "\"a\\\"b\\\\c\\nd\\te\"", (24 as i32))
    if (rc != 0) { return rc }
    const ctrl = "${(1 as char)}"
    rc = expectJson(jsonWrite(ser, ctrl), "\"\\u0001\"", (25 as i32))
    if (rc != 0) { return rc }
    // B3：char 补充平面（U+1F600 😀，UTF-8 4 字节直出：总长 6 字节）。
    const emoji = (128512 as char)
    const emojiText = jsonWrite(ser, emoji)
    if (emojiText.length != (6 as i64)) { return 26 }
    rc = expectJson(emojiText, "\"${emoji}\"", (27 as i32))
    if (rc != 0) { return rc }
    // B4：Array / List 根。
    rc = expectJson(jsonWrite(ser, arrayOfElements\<i32>((1 as i32), (2 as i32), (3 as i32))), "[1,2,3]", (28 as i32))
    if (rc != 0) { return rc }
    const nums = new List\<i32>()
    nums.add((7 as i32))
    nums.add((8 as i32))
    rc = expectJson(jsonWrite(ser, nums), "[7,8]", (29 as i32))
    if (rc != 0) { return rc }
    // B5：嵌套对象 + 可空字段（显式 null 与显式非 null）；集合字段载荷
    // 按 SB 表示输出为数组。
    const scores = new List\<i32>()
    scores.add((5 as i32))
    scores.add((6 as i32))
    const nest = new JsonNest((1 as i32), null, new JsonLeaf(true, "kid"), scores)
    rc = expectJson(jsonWrite(ser, nest:Serializable.toParcel()), "{\".rigi.type-identifier\":\"JsonNest\",\"num\":1,\"note\":null,\"child\":{\".rigi.type-identifier\":\"JsonLeaf\",\"flag\":true,\"label\":\"kid\"},\"scores\":[5,6]}", (30 as i32))
    if (rc != 0) { return rc }
    const nest2 = new JsonNest((2 as i32), "note", new JsonLeaf(false, "k2"), scores)
    rc = expectJson(jsonWrite(ser, nest2:Serializable.toParcel()), "{\".rigi.type-identifier\":\"JsonNest\",\"num\":2,\"note\":\"note\",\"child\":{\".rigi.type-identifier\":\"JsonLeaf\",\"flag\":false,\"label\":\"k2\"},\"scores\":[5,6]}", (31 as i32))
    if (rc != 0) { return rc }
    // B6：空对象与空数组区分。
    rc = expectJson(jsonWrite(ser, arrayOf\<i32>(0)), "[]", (32 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWriteMode(ser, new JsonEmpty():Serializable.toParcel(), false), "{}", (33 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWrite(ser, new JsonEmpty():Serializable.toParcel()), "{\".rigi.type-identifier\":\"JsonEmpty\"}", (34 as i32))
    if (rc != 0) { return rc }

    // ── C. 类型标识 ──
    // C1：多态字段恢复实际派生类型（嵌套对象带自身类型标识）。
    const poly = new JsonPolyHolder(new JsonDerived((3 as i32), "d"))
    const polyText = jsonWrite(ser, poly:Serializable.toParcel())
    rc = expectJsonContains(polyText, "\".rigi.type-identifier\":\"JsonDerived\"", (35 as i32))
    if (rc != 0) { return rc }
    rc = expectJsonContains(polyText, "\"id\":3", (36 as i32))
    if (rc != 0) { return rc }
    rc = expectJsonContains(polyText, "\"extra\":\"d\"", (37 as i32))
    if (rc != 0) { return rc }
    // C2：泛型完整类型名（闭合泛型实参）。
    rc = expectJson(jsonWrite(ser, new JsonBox\<i32>((5 as i32)):Serializable.toParcel()), "{\".rigi.type-identifier\":\"JsonBox<.i32>\",\"item\":5}", (38 as i32))
    if (rc != 0) { return rc }

    // ── D. 引用语义（§4.7.2）──
    // D1：自环 Parcel → JsonException（写出错误 offset = -1）。
    const cyc = new Parcel("Cyc")
    cyc.setDynamic("self", cyc)
    var threw = false
    try {
        ser.write(cyc, new MemoryOutputStream())
    } catch (e: JsonException) {
        threw = true
        if (e.offset != ((0 as i64) - (1 as i64))) { return 41 }
    }
    if (not threw) { return 42 }
    // D2：显式图模式 Parcel 即使无环也拒绝。
    threw = false
    try {
        ser.write(new JsonLeaf(true, "g"):Serializable.toParcel(true), new MemoryOutputStream())
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 43 }
    // D3：无环重复引用展开为独立副本（同引用两字段 → 两份独立对象）。
    const sharedLeaf = new JsonLeaf(true, "shared")
    rc = expectJson(jsonWrite(ser, new JsonTwo(sharedLeaf, sharedLeaf):Serializable.toParcel()), "{\".rigi.type-identifier\":\"JsonTwo\",\"a\":{\".rigi.type-identifier\":\"JsonLeaf\",\"flag\":true,\"label\":\"shared\"},\"b\":{\".rigi.type-identifier\":\"JsonLeaf\",\"flag\":true,\"label\":\"shared\"}}", (44 as i32))
    if (rc != 0) { return rc }
    // D4：同一实例顺序复用（独立写出状态）。
    rc = expectJson(jsonWrite(ser, (9 as i32)), "9", (45 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWrite(ser, (10 as i32)), "10", (46 as i32))
    if (rc != 0) { return rc }

    // ── E. Map 边界 ──
    // E1：非 String 键 Map 保留类型信息正常（键在条目 key 位置）。
    const byInt = new Map\<i32, i32>()
    byInt.set((7 as i32), (8 as i32))
    byInt.set((9 as i32), (10 as i32))
    rc = expectJson(jsonWrite(ser, byInt), "{\".rigi.type-identifier\":\"core.collections::Map<.i32, .i32>\",\"content\":[{\"key\":7,\"value\":8},{\"key\":9,\"value\":10}]}", (51 as i32))
    if (rc != 0) { return rc }
    // E2：非 String 键 Map 互操作报错（不转字符串、不回退条目数组）。
    threw = false
    try {
        ser.write(byInt, new MemoryOutputStream(), false)
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 52 }
    // E3/E4：空 Map<String, i32> 两形式。
    const emptyStrMap = new Map\<String, i32>()
    rc = expectJson(jsonWrite(ser, emptyStrMap), "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[]}", (53 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWriteMode(ser, emptyStrMap, false), "{}", (54 as i32))
    if (rc != 0) { return rc }
    // E5：空 Map 非 String 键互操作同样报错。
    const emptyIntMap = new Map\<i32, i32>()
    threw = false
    try {
        ser.write(emptyIntMap, new MemoryOutputStream(), false)
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 55 }

    // ── F. 数字（§4.7.5）──
    // F1/F2：整数边界（i64/u64 最大）。
    rc = expectJson(jsonWrite(ser, i64.parse("9223372036854775807")), "9223372036854775807", (61 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWrite(ser, u64.parse("18446744073709551615")), "18446744073709551615", (62 as i32))
    if (rc != 0) { return rc }
    // F3/F4/F5：浮点最短往返 / 负零保留。
    rc = expectJson(jsonWrite(ser, 0.1), "0.1", (63 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(jsonWrite(ser, -0.0), "-0", (64 as i32))
    if (rc != 0) { return rc }
    const third = (1.0 / 3.0)
    rc = expectJson(jsonWrite(ser, third), "0.3333333333333333", (65 as i32))
    if (rc != 0) { return rc }
    // F6/F7/F8：NaN/±Infinity 报错（不替换成 null 或字符串）。
    const nan = (0.0 / 0.0)
    threw = false
    try {
        ser.write(nan, new MemoryOutputStream())
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 66 }
    const inf = (1.0 / 0.0)
    threw = false
    try {
        ser.write(inf, new MemoryOutputStream())
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 67 }
    const ninf = (0.0 - inf)
    threw = false
    try {
        ser.write(ninf, new MemoryOutputStream())
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 68 }
    // f32 最短往返走通。
    const f32third = ((1.0 as float) / (3.0 as float))
    rc = expectJson(jsonWrite(ser, f32third), "0.33333334", (69 as i32))
    if (rc != 0) { return rc }

    // ── G. 格式与配置（§4.7.6）──
    // G1：基础缩进输出形态（元数据在前、缩进分级、无末尾换行）。
    const ser2 = new JsonSerializer("  ")
    rc = expectJson(jsonWrite(ser2, new JsonLeaf(true, "x"):Serializable.toParcel()), "{\n  \".rigi.type-identifier\": \"JsonLeaf\",\n  \"flag\": true,\n  \"label\": \"x\"\n}", (71 as i32))
    if (rc != 0) { return rc }
    // G2：空容器紧凑不分行。
    rc = expectJson(jsonWrite(ser, arrayOfElements\<Any?>()), "[]", (72 as i32))
    if (rc != 0) { return rc }
    // G3：深度上限可配置更小（根=1，三层嵌套 > 2 → 报错）。
    const ser3 = new JsonSerializer("", (2 as i32))
    const deep = arrayOfElements\<Any?>(arrayOfElements\<Any?>(arrayOfElements\<Any?>((1 as i32))))
    threw = false
    try {
        ser3.write(deep, new MemoryOutputStream())
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 73 }
    // G4：标量根不占容器深度（maxDepth=1 下标量照常成功）。
    rc = expectJson(jsonWrite(ser3, (5 as i32)), "5", (74 as i32))
    if (rc != 0) { return rc }
    // G5：编码构造选项（UTF-8 拼写并收；其他报错不静默退回）。
    const ser4 = new JsonSerializer("", (256 as i32), "UTF-8")
    rc = expectJson(jsonWrite(ser4, "ok"), "\"ok\"", (75 as i32))
    if (rc != 0) { return rc }
    threw = false
    try {
        const bad = new JsonSerializer("", (256 as i32), "latin-1")
    } catch (e: IllegalArgumentException) {
        threw = true
    }
    if (not threw) { return 76 }
    // G6：空文档拒绝（5-2b 读侧实现后，read 对空输入抛 JsonException
    // §4.7.6「拒绝空文档」，偏移 0）。
    threw = false
    try {
        const ignored = ser.read(new MemoryInputStream(spanOf\<u8>(0)))
    } catch (e: JsonException) {
        threw = true
        if (e.offset != (0 as i64)) { return 78 }
    }
    if (not threw) { return 77 }

    // ── H. 借用语义与便利层（§4.6.2）──
    // H1：成功后调用者流未关闭、未 flush，内容已交出；最终刷新由调用者定。
    const probe = new JsonProbeStream()
    ser.write((1 as i32), probe)
    if (probe.disposeRuns != 0) { return 81 }
    if (probe.flushCalls != 0) { return 82 }
    if (probe.sink.length != (1 as i64)) { return 83 }
    probe.flush()
    if (probe.flushCalls != 1) { return 84 }
    // H2：失败后（JsonException，构建期抛出）流仍未关闭、未 flush、无字节。
    const probe2 = new JsonProbeStream()
    threw = false
    try {
        ser.write(nan, probe2)
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 85 }
    if (probe2.disposeRuns != 0) { return 86 }
    if (probe2.flushCalls != 0) { return 87 }
    if (probe2.sink.length != (0 as i64)) { return 88 }
    if (probe2.writeCalls != 0) { return 89 }
    // H3：调用者自行处置（幂等支架在基类）。
    probe2.dispose()
    if (probe2.disposeRuns != 1) { return 90 }
    // H4：serialize / serializeToString 便利层（基类 toParcel → write）。
    const leafOut = new MemoryOutputStream()
    ser.serialize\<JsonLeaf>(new JsonLeaf(false, "s"), leafOut)
    rc = expectJson(jsonWriteText(leafOut), "{\".rigi.type-identifier\":\"JsonLeaf\",\"flag\":false,\"label\":\"s\"}", (91 as i32))
    if (rc != 0) { return rc }
    rc = expectJson(ser.serializeToString\<JsonLeaf>(new JsonLeaf(true, "t")), "{\".rigi.type-identifier\":\"JsonLeaf\",\"flag\":true,\"label\":\"t\"}", (93 as i32))
    if (rc != 0) { return rc }

    Console.println("json-write-ok")
    return 0
}

// MemoryOutputStream → 文本（toSpan 导出后严格 UTF-8 解码；dispose 前取）。
func jsonWriteText(out: MemoryOutputStream): String {
    const decoder = new Utf8Decoder()
    return decoder.decode(out.toSpan(), true)
}
