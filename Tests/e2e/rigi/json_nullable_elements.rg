import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: json-nullable-elements-ok
// expect-exit: 0
// 可空集合元素的裸 null（§4.7.1 按目标声明可空性读取 / §4.7.5 null 对应
// 空值）：readAs 对象内 Array<i32?> / List<String?> / Map<String, i32?>
// 字段接受显式 JSON null 元素/值（含 null/非 null 混排、空容器、嵌套
// 容器）；String "null" 与 null 区分；非可空元素遇 null 仍拒绝（读取面
// JsonException）；两种写出模式（默认保留类型信息 / keepTypeInfo=false）
// 的 null 元素均输出 JSON null，不出现内部哨兵记录文本
// （NullSentinel wire 名）或空对象残留；读回值逐项断言（固定外部 JSON
// 与预期对象值，非仅自身往返）。
//
// 表示通道注记：类型引导读取的对象字段经 wire 载荷（.array<.any>），
// 可空元素的 JSON null 造形为与 EncodeOptionalElement 同形态的 wire
// null 哨兵记录（Parcel 受控面 newNullSentinelWire），严格 fromParcel
// 的 DecodeOptionalElement 按同名判回真实 null；非可空元素遇哨兵记录
// 仍被严格恢复拒绝。写出侧 writeNonNull 按同名把哨兵记录还原为 JSON
// null——格式边界不泄漏内部哨兵。

// ── 业务语料类型 ──

@Serializable()
class NeRec {
    pub var nums: Array\<i32?>
    pub var names: List\<String?>
    pub var byKey: Map\<String, i32?>
    pub var emptyNums: Array\<i32?>
    pub var nested: Array\<List\<String?>>
    pub init(_ -> nums, _ -> names, _ -> byKey, _ -> emptyNums,
        _ -> nested)
}

// 非可空元素对照（拒绝面用）。
@Serializable()
class NeStrict {
    pub var nums: Array\<i32>
    pub var names: List\<String>
    pub init(_ -> nums, _ -> names)
}

// ── 断言与输入助手 ──

func neFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

// String → 内存输入流（UTF-8 编码快照）。
func neStream(text: String): MemoryInputStream {
    const enc = new Utf8Encoder()
    return new MemoryInputStream(enc.encode(text))
}

// readAs 即用即 Dispose 包装。
func neAs\<T with Serializable>(ser: JsonSerializer, text: String): T {
    const s = neStream(text)
    try {
        return ser.readAs\<T>(s)
    } finally(e) {
        s.dispose()
    }
}

// 负例：readAs\<NeStrict> 期望 JsonException（读取面非可空拒绝）或严格
// 恢复 SerializationException。
func neExpectError(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = neStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<NeStrict>(s)
    } catch (e: JsonException) {
        threw = true
    } catch (e2: SerializationException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return neFail(code, "未抛异常：[${text}]")
}

// 两种模式写出（value 为 SB 表示，通常为 toParcel 产物）。
func neWriteMode(ser: JsonSerializer, value: Any?,
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

// ── P1：混合 null/非 null 元素恢复 + String "null" 区分 ──

func sectionP1(ser: JsonSerializer): i32 {
    // 固定外部 JSON：null 夹在非 null 元素中；byName 值含显式 null；
    // names 元素区分 String "null" 文本与 null 空值。
    const text = "{\"nums\":[1,null,3],\"names\":[\"null\",null,\"a\"],\"byKey\":{\"x\":1,\"y\":null,\"z\":7},\"emptyNums\":[],\"nested\":[[\"p\",null],[null],[\"q\"]]}"
    const r = neAs\<NeRec>(ser, text)
    // Array<i32?>：逐项断言（宽度保真由声明通道保证）。
    if (r.nums.length != (3 as i32)) { return neFail(11, "nums len") }
    if ((r.nums.getAtIndex((0 as i32)) if? (0 as i32)) != (1 as i32)) {
        return neFail(12, "nums[0]")
    }
    if (r.nums.getAtIndex((1 as i32)) != null) { return neFail(13, "nums[1]") }
    if ((r.nums.getAtIndex((2 as i32)) if? (0 as i32)) != (3 as i32)) {
        return neFail(14, "nums[2]")
    }
    // List<String?>：首元素是字符串文本 "null"，不是 null 空值。
    if (r.names.length != (3 as i64)) { return neFail(15, "names len") }
    if ((r.names.getAtIndex((0 as i64)) if? "") != "null") {
        return neFail(16, "names[0] 文本 null")
    }
    if (r.names.getAtIndex((1 as i64)) != null) { return neFail(17, "names[1]") }
    if ((r.names.getAtIndex((2 as i64)) if? "") != "a") {
        return neFail(18, "names[2]")
    }
    // Map<String, i32?>：值含 null 与非 null 混排（插入序）。
    if (r.byKey.count != (3 as i64)) { return neFail(19, "byKey count") }
    if ((r.byKey.tryGet("x") if? (0 as i32)) != (1 as i32)) {
        return neFail(110, "byKey x")
    }
    if (r.byKey.tryGet("y") != null) { return neFail(111, "byKey y") }
    if ((r.byKey.tryGet("z") if? (0 as i32)) != (7 as i32)) {
        return neFail(112, "byKey z")
    }
    // 空容器依声明恢复。
    if (r.emptyNums.length != (0 as i32)) { return neFail(113, "emptyNums") }
    // 嵌套容器：Array<List<String?>>，内层 null 元素。
    if (r.nested.length != (3 as i32)) { return neFail(114, "nested len") }
    const inner0 = r.nested.getAtIndex((0 as i32))
    if ((inner0 if? (new List\<String?>())).length != (2 as i64)) {
        return neFail(115, "nested[0] len")
    }
    if ((inner0 if? (new List\<String?>())).getAtIndex((1 as i64)) != null) {
        return neFail(116, "nested[0][1]")
    }
    const inner1 = r.nested.getAtIndex((1 as i32))
    if ((inner1 if? (new List\<String?>())).getAtIndex((0 as i64)) != null) {
        return neFail(117, "nested[1][0]")
    }
    const inner2 = (r.nested.getAtIndex((2 as i32)) if? (new List\<String?>()))
    if ((inner2.getAtIndex((0 as i64)) if? "") != "q") {
        return neFail(118, "nested[2][0]")
    }
    return 0
}

// ── P2：写出两模式不泄漏哨兵 + 写读一致 ──

func sectionP2(ser: JsonSerializer): i32 {
    // 全 null/混合值对象 → toParcel → 两种模式写出。
    const nums = core.collections.arrayOf\<i32?>(2)
    nums[(0 as i32)] = null
    nums[(1 as i32)] = (5 as i32)
    const names = new List\<String?>()
    names.add(null)
    const byKey = new Map\<String, i32?>()
    byKey.set("k", null)
    const emptyNums = core.collections.arrayOf\<i32?>(0)
    const nested = core.collections.arrayOf\<List\<String?>>(1)
    nested[(0 as i32)] = names
    const r = new NeRec(nums, names, byKey, emptyNums, nested)
    const wire = r:Serializable.toParcel()
    const typed = neWriteMode(ser, wire, true)
    const plain = neWriteMode(ser, wire, false)
    // 两种模式均不出现内部哨兵记录文本（NullSentinel wire 名），也不把
    // 哨兵记录写成对象残留（"{}"）——格式边界不泄漏内部表示。
    if (typed.contains("NullSentinel")) {
        return neFail(21, "默认写出泄漏哨兵：${typed}")
    }
    if (typed.contains("{}")) {
        return neFail(22, "默认写出哨兵对象残留：${typed}")
    }
    if (plain.contains("NullSentinel")) {
        return neFail(23, "互操作写出泄漏哨兵：${plain}")
    }
    if (plain.contains("{}")) {
        return neFail(24, "互操作写出哨兵对象残留：${plain}")
    }
    // null 元素按 §4.7.5 输出 JSON null；Map 字段按 §4.7.3/4.7.4
    // 输出业务 Map 形态，不把交替键值内部载荷当作 JSON 数组。
    const expectTyped = "{\".rigi.type-identifier\":\"NeRec\",\"nums\":[null,5],\"names\":[null],\"byKey\":{\".rigi.type-identifier\":\"core.collections::Map<.string, .nullable<.i32>>\",\"content\":[{\"key\":\"k\",\"value\":null}]},\"emptyNums\":[],\"nested\":[[null]]}"
    if (typed != expectTyped) {
        return neFail(25, "默认写出不符：${typed}")
    }
    const expectPlain = "{\"nums\":[null,5],\"names\":[null],\"byKey\":{\"k\":null},\"emptyNums\":[],\"nested\":[[null]]}"
    if (plain != expectPlain) {
        return neFail(26, "互操作写出不符：${plain}")
    }
    // 写读一致：两种模式产物分别经 readAs 恢复（互操作产物按普通 JSON
    // 目标读取），null 元素原样往返。
    const back = neAs\<NeRec>(ser, typed)
    if (back.nums.getAtIndex((0 as i32)) != null) { return neFail(27, "typed 回读 nums[0]") }
    if ((back.nums.getAtIndex((1 as i32)) if? (0 as i32)) != (5 as i32)) {
        return neFail(28, "typed 回读 nums[1]")
    }
    if (back.byKey.tryGet("k") != null) { return neFail(29, "typed 回读 byKey.k") }
    const innerBack = (back.nested.getAtIndex((0 as i32)) if? (new List\<String?>()))
    if (innerBack.getAtIndex((0 as i64)) != null) {
        return neFail(210, "typed 回读 nested[0][0]")
    }
    const backPlain = neAs\<NeRec>(ser, plain)
    if (backPlain.nums.getAtIndex((0 as i32)) != null) {
        return neFail(211, "plain 回读 nums[0]")
    }
    if ((backPlain.nums.getAtIndex((1 as i32)) if? (0 as i32)) != (5 as i32)) {
        return neFail(212, "plain 回读 nums[1]")
    }
    return 0
}

// ── P3：非可空元素遇 null 仍拒绝 ──

func sectionP3(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    // Array<i32> 元素 null（夹在非 null 中）——读取面拒绝。
    rc = neExpectError(ser,
        "{\"nums\":[1,null,3],\"names\":[\"a\"]}", (31 as i32))
    if (rc != 0) { return rc }
    // List<String> 元素 null——读取面拒绝。
    rc = neExpectError(ser,
        "{\"nums\":[1],\"names\":[\"a\",null]}", (32 as i32))
    if (rc != 0) { return rc }
    // 非可空字段整体 null（严格恢复既有语义，不因本块放宽）。
    rc = neExpectError(ser,
        "{\"nums\":null,\"names\":[\"a\"]}", (33 as i32))
    if (rc != 0) { return rc }
    // 非可空元素类型不匹配仍拒绝（形状前提不受 null 桥影响）。
    rc = neExpectError(ser,
        "{\"nums\":[\"1\"],\"names\":[\"a\"]}", (34 as i32))
    if (rc != 0) { return rc }
    return 0
}

pub func main(): i32 {
    const ser = new JsonSerializer()
    var rc: i32 = 0
    rc = sectionP1(ser)
    if (rc != 0) { return rc }
    rc = sectionP2(ser)
    if (rc != 0) { return rc }
    rc = sectionP3(ser)
    if (rc != 0) { return rc }
    Console.println("json-nullable-elements-ok")
    return 0
}
