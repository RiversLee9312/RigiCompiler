import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: json-root-object-containers-ok
// expect-exit: 0
// wb-5-2d：readAs 顶层自定义对象容器恢复（§4.7.1「按已知目标声明递归
// 构造并恢复对象/Array/List/Map」——顶层对象容器无免责）语料。
// 覆盖：
//   - 手写普通 JSON 固定样例（非仅自编自解）：Array<RcPoint> /
//     List<RcPoint> / Map<String, RcPoint> 根，i32/String 字段精确
//     恢复（宽度经 toParcel 声明宽度槽二次确认）；
//   - 空容器依声明恢复（List/Array/Map）；
//   - 可空对象元素/值（List<RcPoint?> / Map<String, RcPoint?>）：
//     显式 null 与存在值混排；
//   - 嵌套容器 List<List<RcPoint>>（内层子 envelope 递归）；
//   - 合法派生类型（.rigi.type-identifier=RcDerived 恢复进
//     List<RcBase>）与不兼容标识（元素标识为基类 RcBase → 严格恢复
//     cast 到 RcPoint 拒绝）、未登记标识（wire 类型未登记拒绝）；
//   - Type<T> 值重载入口（typeOf(实例) 实参走同一路径）；
//   - 负例：元素字段缺失/多余/null 进非可空元素/元素标量类型不匹配
//     （严格恢复 SerializationException 或读取面 JsonException）；
//   - 往返补充：write 两模式产物 readAs 恢复等值（浅/深独立性按既定
//     恢复契约，往返只作为补充断言）。
// 实现注记：顶层容器恢复 = SB envelope wire（typeName=目标名原文 +
// 受控元数据 ..value=元素记录载荷；元素一律记录形态：对象/枚举业务
// 记录、标量 SB 值 envelope、嵌套容器子 envelope、声明可空 null 为
// wire null 哨兵）交已知静态 T 的严格 fromParcel<T>——与字段通道
// （DecodeBody 直入）互不干扰，buildWire/字段构造路径保持原状。

// ── 业务语料类型 ──

@Serializable()
class RcPoint {
    pub var x: i32
    pub var name: String
    pub init(_ -> x, _ -> name)
}

@Serializable()
pub enum struct RcMark {} [Off -> 0, On -> 1]

// 派生类型：基类 open，元素以基类声明、经类型标识恢复派生实例。
@Serializable()
pub open class RcBase {
    pub var tag: i32
    pub init(_ -> tag)
}

@Serializable()
class RcDerived : RcBase {
    pub var extra: String
    pub init(_ -> tag, _ -> extra) {
        super(tag)
    }
}

// ── 断言与输入助手 ──

func rcFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

// String → 内存输入流（UTF-8 编码快照）。
func rcStream(text: String): MemoryInputStream {
    const enc = new Utf8Encoder()
    return new MemoryInputStream(enc.encode(text))
}

// readAs 即用即 Dispose 包装（两个公共入口共用）。
func rcAs\<T with Serializable>(text: String): T {
    const s = rcStream(text)
    try {
        return (new JsonSerializer()).readAs\<T>(s)
    } finally(e) {
        s.dispose()
    }
}

// Type\<T> 值形态入口（typeOf(实例) 实参走同一恢复路径）。
func rcAsValue\<T with Serializable>(tv: Type\<T>, text: String): T {
    const s = rcStream(text)
    try {
        return (new JsonSerializer()).readAs\<T>(tv, s)
    } finally(e) {
        s.dispose()
    }
}

// 负例：期望 JsonException（读取面）或严格恢复 SerializationException
// （缺失/多余/null 进非可空），未知派生类型为恢复端既有的
// IllegalStateException（wire 类型未登记，与 strict_fromparcel 顶层
// 同口径）。
func rcExpectError\<T with Serializable>(text: String, code: i32): i32 {
    const s = rcStream(text)
    var threw = false
    try {
        const ignored = (new JsonSerializer()).readAs\<T>(s)
    } catch (e: JsonException) {
        threw = true
    } catch (e2: SerializationException) {
        threw = true
    } catch (e3: core.IllegalStateException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rcFail(code, "未抛异常：[${text}]")
}

// 写出为文本（MemoryOutputStream 收字节，Utf8Decoder 严格回读）。
func rcWriteMode(ser: JsonSerializer, value: Any?, keepTypeInfo: bool):
        String {
    const out = new MemoryOutputStream()
    try {
        ser.write(value, out, keepTypeInfo)
        const decoder = new Utf8Decoder()
        return decoder.decode(out.toSpan(), true)
    } finally(e) {
        out.dispose()
    }
}

// ── C1/C2：Array/List 根 + 宽度保真 + 空容器 ──

func sectionC1(): i32 {
    // 手写普通 JSON：i32 值为整数词法、String 含转义；宽度经 toParcel
    // 声明宽度槽二次确认（i64 中间形态会被严格恢复拒绝，成功即证）。
    const list = rcAs\<Array\<RcPoint>>(
        "[{\"x\":1,\"name\":\"alpha\"},{\"x\":-2,\"name\":\"be\\\"ta\"}]")
    if (list.length != (2 as i32)) { return rcFail(11, "C1 len") }
    const p0 = list[(0 as i32)] if? (new RcPoint((0 as i32), ""))
    if (p0.x != (1 as i32)) { return rcFail(12, "C1 x0") }
    if (p0.name != "alpha") { return rcFail(13, "C1 n0") }
    const p1 = list[(1 as i32)] if? (new RcPoint((0 as i32), ""))
    if (p1.x != ((0 as i32) - (2 as i32))) { return rcFail(14, "C1 x1") }
    if (p1.name != "be\"ta") { return rcFail(15, "C1 n1") }
    const xWire = (p1:Serializable.toParcel()).getElement\<i32>("x") if? (0 as i32)
    if (xWire != ((0 as i32) - (2 as i32))) { return rcFail(16, "C1 x wire") }
    // List 根与空容器依声明恢复。
    const l = rcAs\<List\<RcPoint>>(
        "[{\"x\":3,\"name\":\"g\"}]")
    if (l.length != (1 as i64)) { return rcFail(17, "C1 list len") }
    if ((l.getAtIndex((0 as i64)) if? (new RcPoint((0 as i32), ""))).x
            != (3 as i32)) {
        return rcFail(18, "C1 list x")
    }
    const emptyArr = rcAs\<Array\<RcPoint>>("[]")
    if (emptyArr.length != (0 as i32)) { return rcFail(19, "C1 empty arr") }
    const emptyList = rcAs\<List\<RcPoint>>("[]")
    if (emptyList.length != (0 as i64)) { return rcFail(110, "C1 empty list") }
    return 0
}

// ── C3：Map<String, RcPoint> 根（普通字典 + content 包装两形态）──

func sectionC3(): i32 {
    // 普通对象字典：成员名即业务键。
    const m = rcAs\<Map\<String, RcPoint>>(
        "{\"p1\":{\"x\":7,\"name\":\"n1\"},\"p2\":{\"x\":8,\"name\":\"n2\"}}")
    if (m.count != (2 as i64)) { return rcFail(31, "C3 count") }
    const v1 = m.tryGet("p1") if? (new RcPoint((0 as i32), ""))
    if (v1.x != (7 as i32)) { return rcFail(32, "C3 p1.x") }
    if (v1.name != "n1") { return rcFail(33, "C3 p1.name") }
    const v2 = m.tryGet("p2") if? (new RcPoint((0 as i32), ""))
    if (v2.x != (8 as i32)) { return rcFail(34, "C3 p2.x") }
    // 空字典。
    const empty = rcAs\<Map\<String, RcPoint>>("{}")
    if (empty.count != (0 as i64)) { return rcFail(35, "C3 empty") }
    // content 包装形态（§4.7.3 保留类型信息写出的手写等价样例；成员
    // 顺序宽容）。
    const c = rcAs\<Map\<String, RcPoint>>(
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, RcPoint>\"," +
        "\"content\":[{\"value\":{\"x\":9,\"name\":\"n3\"},\"key\":\"p3\"}]}")
    if (c.count != (1 as i64)) { return rcFail(36, "C3 content count") }
    const v3 = c.tryGet("p3") if? (new RcPoint((0 as i32), ""))
    if ((v3.x != (9 as i32)) or (v3.name != "n3")) {
        return rcFail(37, "C3 content p3")
    }
    return 0
}

// ── C4/C5：可空对象元素/值 ──

func sectionC4(): i32 {
    const l = rcAs\<List\<RcPoint?>>(
        "[{\"x\":4,\"name\":\"d\"},null,{\"x\":5,\"name\":\"e\"}]")
    if (l.length != (3 as i64)) { return rcFail(41, "C4 len") }
    if (l.getAtIndex((1 as i64)) != null) { return rcFail(42, "C4 null") }
    const p0 = l.getAtIndex((0 as i64)) if? (new RcPoint((0 as i32), ""))
    if ((p0.x != (4 as i32)) or (p0.name != "d")) {
        return rcFail(43, "C4 p0")
    }
    // 全 null 与空。
    const allNull = rcAs\<List\<RcPoint?>>("[null,null]")
    if (allNull.length != (2 as i64)) { return rcFail(44, "C4 all null") }
    if (allNull.getAtIndex((0 as i64)) != null) { return rcFail(45, "C4 n0") }
    // Map 可空值：null 与非 null 混排。
    const m = rcAs\<Map\<String, RcPoint?>>(
        "{\"a\":null,\"b\":{\"x\":6,\"name\":\"n\"}}")
    if (m.count != (2 as i64)) { return rcFail(46, "C4 map count") }
    if (m.tryGet("a") != null) { return rcFail(47, "C4 map null") }
    const b = m.tryGet("b") if? (new RcPoint((0 as i32), ""))
    if (b.x != (6 as i32)) { return rcFail(48, "C4 map b") }
    return 0
}

// ── C6：嵌套容器 List<List<RcPoint>> ──

func sectionC6(): i32 {
    const n = rcAs\<List\<List\<RcPoint>>>(
        "[[{\"x\":1,\"name\":\"a\"}],[{\"x\":2,\"name\":\"b\"}," +
        "{\"x\":3,\"name\":\"c\"}],[]]")
    if (n.length != (3 as i64)) { return rcFail(61, "C6 outer") }
    const row0 = n.getAtIndex((0 as i64)) if? (new List\<RcPoint>())
    if (row0.length != (1 as i64)) { return rcFail(62, "C6 row0") }
    const p00 = row0.getAtIndex((0 as i64)) if? (new RcPoint((0 as i32), ""))
    if ((p00.x != (1 as i32)) or (p00.name != "a")) {
        return rcFail(63, "C6 p00")
    }
    const row2 = n.getAtIndex((2 as i64)) if? (new List\<RcPoint>())
    if (row2.length != (0 as i64)) { return rcFail(64, "C6 row2 empty") }
    return 0
}

// ── C7：合法派生类型 + 不兼容标识 ──

func sectionC7(): i32 {
    // 合法派生：元素声明为基类 RcBase，.rigi.type-identifier=RcDerived
    // （实际类型在声明边界内的多态恢复，§4.7.2）。
    const list = rcAs\<List\<RcBase>>(
        "[{\"tag\":1},{\".rigi.type-identifier\":\"RcDerived\"," +
        "\"tag\":2,\"extra\":\"x\"}]")
    if (list.length != (2 as i64)) { return rcFail(71, "C7 len") }
    const b0 = list.getAtIndex((0 as i64)) if? (new RcDerived((0 as i32), ""))
    if (b0.tag != (1 as i32)) { return rcFail(72, "C7 tag0") }
    // 派生实例：toParcel 虚派发到实际类型，wire 含派生字段 extra 与 tag。
    const d1 = list.getAtIndex((1 as i64)) if? (new RcDerived((0 as i32), ""))
    if (d1.tag != (2 as i32)) { return rcFail(73, "C7 tag1") }
    // 派生实例经 SB 表示（toParcel）写出：互操作模式输出完整派生字段。
    const d1Parcel = d1:Serializable.toParcel()
    const d1Wire = rcWriteMode(new JsonSerializer(), d1Parcel, false)
    if (d1Wire != "{\"extra\":\"x\",\"tag\":2}") {
        return rcFail(74, "C7 extra: ${d1Wire}")
    }
    // 不兼容标识：元素标识为基类 RcBase——恢复实例不在 RcPoint 边界内，
    // 严格恢复拒绝。
    var rc = rcExpectError\<List\<RcPoint>>(
        "[{\".rigi.type-identifier\":\"RcBase\",\"tag\":1}]", (75 as i32))
    if (rc != 0) { return rc }
    // 未登记标识：wire 类型未登记（恢复端既有 IllegalStateException 口
    // 径，与顶层对象目标一致）。
    rc = rcExpectError\<List\<RcPoint>>(
        "[{\".rigi.type-identifier\":\"NoSuchType\",\"x\":1,\"name\":\"a\"}]",
        (76 as i32))
    if (rc != 0) { return rc }
    return 0
}

// ── C8：Type\<T> 值重载入口（与无参形态同一路径）──

func sectionC8(): i32 {
    const l = rcAsValue\<List\<RcPoint>>(typeOf(rcPointListSeed()),
        "[{\"x\":5,\"name\":\"v\"}]")
    if (l.length != (1 as i64)) { return rcFail(81, "C8 len") }
    const p = l.getAtIndex((0 as i64)) if? (new RcPoint((0 as i32), ""))
    if ((p.x != (5 as i32)) or (p.name != "v")) { return rcFail(82, "C8 p") }
    // Map 目标的值重载。
    const m = rcAsValue\<Map\<String, RcPoint>>(typeOf(rcPointMapSeed()),
        "{\"k\":{\"x\":6,\"name\":\"w\"}}")
    const v = m.tryGet("k") if? (new RcPoint((0 as i32), ""))
    if ((v.x != (6 as i32)) or (v.name != "w")) { return rcFail(83, "C8 m") }
    return 0
}

func rcPointListSeed(): List\<RcPoint> {
    const l = new List\<RcPoint>()
    l.add(new RcPoint((0 as i32), ""))
    return l
}

func rcPointMapSeed(): Map\<String, RcPoint> {
    const m = new Map\<String, RcPoint>()
    m.set("k", new RcPoint((0 as i32), ""))
    return m
}

// ── C9：负例组 ──

func sectionC9(): i32 {
    var rc = rcExpectError\<List\<RcPoint>>(
        "[{\"x\":1}]", (91 as i32))
    if (rc != 0) { return rc }
    rc = rcExpectError\<List\<RcPoint>>(
        "[{\"x\":1,\"name\":\"a\",\"z\":9}]", (92 as i32))
    if (rc != 0) { return rc }
    rc = rcExpectError\<List\<RcPoint>>(
        "[{\"x\":1,\"name\":\"a\"},{\"x\":2,\"name\":\"b\"},null]", (93 as i32))
    if (rc != 0) { return rc }
    rc = rcExpectError\<List\<RcPoint>>(
        "[{\"x\":\"s\",\"name\":\"a\"}]", (94 as i32))
    if (rc != 0) { return rc }
    rc = rcExpectError\<Map\<String, RcPoint>>(
        "{\"a\":{\"x\":1,\"name\":\"n\"},\"a\":{\"x\":2,\"name\":\"m\"}}",
        (95 as i32))
    if (rc != 0) { return rc }
    // 期望数组遇对象（读取面形状错误）。
    rc = rcExpectError\<Array\<RcPoint>>(
        "{\"x\":1,\"name\":\"a\"}", (96 as i32))
    if (rc != 0) { return rc }
    return 0
}

// ── C10：往返补充（两模式写出产物 readAs 恢复等值）──

func sectionC10(ser: JsonSerializer): i32 {
    const seed = new List\<RcPoint>()
    seed.add(new RcPoint((11 as i32), "r1"))
    seed.add(new RcPoint((22 as i32), "r2"))
    // 默认（保留类型信息）模式产物（SB 表示经 toParcel 的 envelope，
    // writeParcel 解包 ..value 输出元素记录）经 readAs 恢复。
    const typed = rcWriteMode(ser, seed:Serializable.toParcel(), true)
    const back = rcAs\<List\<RcPoint>>(typed)
    if (back.length != (2 as i64)) { return rcFail(101, "C10 typed len") }
    const p1 = back.getAtIndex((1 as i64)) if? (new RcPoint((0 as i32), ""))
    if ((p1.x != (22 as i32)) or (p1.name != "r2")) {
        return rcFail(102, "C10 typed p1")
    }
    // 互操作模式产物（无类型标识普通数组）经 readAs 恢复。
    const plain = rcWriteMode(ser, seed:Serializable.toParcel(), false)
    if (plain != "[{\"x\":11,\"name\":\"r1\"},{\"x\":22,\"name\":\"r2\"}]") {
        return rcFail(103, "C10 plain text: ${plain}")
    }
    const back2 = rcAs\<List\<RcPoint>>(plain)
    if (back2.length != (2 as i64)) { return rcFail(104, "C10 plain len") }
    // Map envelope 的交替键值 wire 只供严格恢复；JSON 必须转换为
    // §4.7.3/4.7.4 外观，两模式均能由 readAs 恢复。
    const seedMap = new Map\<String, RcPoint>()
    seedMap.set("k", new RcPoint((33 as i32), "m"))
    const mapTyped = rcWriteMode(ser, seedMap:Serializable.toParcel(), true)
    if (mapTyped != "{\".rigi.type-identifier\":\"core.collections::Map<.string, RcPoint>\",\"content\":[{\"key\":\"k\",\"value\":{\".rigi.type-identifier\":\"RcPoint\",\"x\":33,\"name\":\"m\"}}]}") {
        return rcFail(105, "C10 typed Map: ${mapTyped}")
    }
    const mapPlain = rcWriteMode(ser, seedMap:Serializable.toParcel(), false)
    if (mapPlain != "{\"k\":{\"x\":33,\"name\":\"m\"}}") {
        return rcFail(106, "C10 plain Map: ${mapPlain}")
    }
    const mappedTyped = rcAs\<Map\<String, RcPoint>>(mapTyped)
    const mappedPlain = rcAs\<Map\<String, RcPoint>>(mapPlain)
    if ((mappedTyped.tryGet("k") if? (new RcPoint((0 as i32), ""))).x != (33 as i32)) {
        return rcFail(107, "C10 typed Map 恢复")
    }
    if ((mappedPlain.tryGet("k") if? (new RcPoint((0 as i32), ""))).name != "m") {
        return rcFail(108, "C10 plain Map 恢复")
    }
    return 0
}

pub func main(): i32 {
    var rc: i32 = 0
    rc = sectionC1()
    if (rc != 0) { return rc }
    rc = sectionC3()
    if (rc != 0) { return rc }
    rc = sectionC4()
    if (rc != 0) { return rc }
    rc = sectionC6()
    if (rc != 0) { return rc }
    rc = sectionC7()
    if (rc != 0) { return rc }
    rc = sectionC8()
    if (rc != 0) { return rc }
    rc = sectionC9()
    if (rc != 0) { return rc }
    rc = sectionC10(new JsonSerializer())
    if (rc != 0) { return rc }
    Console.println("json-root-object-containers-ok")
    return 0
}
