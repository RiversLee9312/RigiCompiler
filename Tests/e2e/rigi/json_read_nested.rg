import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: json-read-nested-ok
// expect-exit: 0
// 施工块 5-2c：readAs 嵌套对象类型引导读取（§4.7.1「递归到每个成员时
// 重复这一判断」）+ 按类型名反射重载（fieldsOf/casesOf/isSerializable
// 的 String 形态）语料。
// 覆盖：
//   - 嵌套自定义对象（无标识普通 JSON）：嵌套 i32/i16 宽度保真（成功即
//     证——i64 中间形态会被严格恢复拒绝，并经 toParcel 取回 i32/i16
//     槽二次确认）、嵌套嵌套（NdRect{tl,br: NdPoint}）；
//   - 嵌套枚举：无载荷 enum struct 互操作裸 case 名与对象形态、带载荷
//     rich enum 互操作 case 对象、保留类型信息形态（.rigi.enum-case）；
//     未知 case / 形状错误负例；
//   - 容器成员声明中的自定义对象：List\<NdPoint>（元素宽度保真）、
//     Map\<String, NdPoint>（值对象引导）；顶层 List/Map 目标的
//     自定义对象元素/值（buildValue 通道）；
//   - 可空嵌套对象：显式 null 与存在值两形态；
//   - 嵌套负例：嵌套字段缺失/多余/null 进非可空（严格恢复
//     SerializationException）、嵌套标量类型不匹配/小数词法进整数
//     （JsonException）；
//   - 按名反射重载直接断言：fieldsOf 字段闭包（名称/规范类型名/可空
//     标志，含容器键值/元素类型文本）、casesOf case 清单（含载荷）、
//     isSerializable（已登记 true / 未登记 false）、未登记名查询抛
//     IllegalArgumentException；
//   - 深度安全：自引用类型 NdChain（next: NdChain?）按输入节点驱动
//     展开，256 层链正常恢复、257 层被 JSON 深度上限拒绝（类型级
//     递归引导天然终止）。

// ── 业务语料类型 ──

@Serializable()
class NdPoint {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
}

// 嵌套嵌套：矩形的两角各是一个嵌套对象。
@Serializable()
class NdRect {
    pub var tl: NdPoint
    pub var br: NdPoint
    pub init(_ -> tl, _ -> br)
}

// 无载荷枚举（非 rich）：互操作形态输出 case 名字符串。
@Serializable()
pub enum struct NdMark {} [Off -> 0, On -> 1]

// 带载荷枚举（rich）：case 按既有绑定先例全 case 绑定载荷形参。
@Serializable()
pub rich enum struct NdResult {
    pub var code: i32
    pub init(_ -> code)
} [Good(code = _) -> 0, Bad(code = _) -> 1]

@Serializable()
class NdNode {
    pub var width: i16
    pub var pt: NdPoint
    pub var rect: NdRect
    pub var children: List\<NdPoint>
    pub var byName: Map\<String, NdPoint>
    pub var mark: NdMark
    pub var result: NdResult
    pub var tag: String?
    pub var maybe: NdPoint?
    pub init(_ -> width, _ -> pt, _ -> rect, _ -> children, _ -> byName,
        _ -> mark, _ -> result, _ -> tag, _ -> maybe)
}

// 自引用类型：类型级递归引导按输入节点驱动（深度上限内正常、超上限
// 由 JSON 解析器拒绝）。
@Serializable()
class NdChain {
    pub var v: i32
    pub var next: NdChain?
    pub init(_ -> v, _ -> next)
}

// ── 断言与输入助手 ──

func nxFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

// String → 内存输入流（UTF-8 编码快照）。
func nxStream(text: String): MemoryInputStream {
    const enc = new Utf8Encoder()
    return new MemoryInputStream(enc.encode(text))
}

func nxReadAsNode(ser: JsonSerializer, text: String): NdNode {
    const s = nxStream(text)
    try {
        return ser.readAs\<NdNode>(s)
    } finally(e) {
        s.dispose()
    }
}

// readAs 即用即 Dispose 包装。
func nxAsDisposed\<T with Serializable>(ser: JsonSerializer, text: String): T {
    const s = nxStream(text)
    try {
        return ser.readAs\<T>(s)
    } finally(e) {
        s.dispose()
    }
}

// 负例：readAs\<NdNode> 期望 JsonException 或严格恢复
// SerializationException（与 json_read.rg 负例助手同口径）。
func nxExpectNodeError(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = nxStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<NdNode>(s)
    } catch (e: JsonException) {
        threw = true
    } catch (e2: SerializationException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return nxFail(code, "未抛异常：[${text}]")
}

// NdNode 全字段正例 JSON 的公共前缀（给定 pt/rect/children/byName/
// mark/result/tag/maybe 成员的拼装由调用方完成）：本助手生成
// width=7 + 传入各成员文本的完整对象。
func nxNodeJson(pt: String, rect: String, children: String,
        byName: String, mark: String, result: String, tag: String,
        maybe: String): String {
    return "{\"width\":7,\"pt\":${pt},\"rect\":${rect},\"children\":${children},\"byName\":${byName},\"mark\":${mark},\"result\":${result},\"tag\":${tag},\"maybe\":${maybe}}"
}

func nxGoodTail(): String {
    return "{\"x\":1,\"y\":2}"
}

// ── N1：嵌套对象/枚举/容器成员类型引导 ──

func sectionN1(ser: JsonSerializer): i32 {
    const text = nxNodeJson(nxGoodTail(),
        "{\"tl\":{\"x\":3,\"y\":4},\"br\":{\"x\":5,\"y\":6}}",
        "[{\"x\":7,\"y\":8},{\"x\":9,\"y\":10}]",
        "{\"a\":{\"x\":11,\"y\":12}}",
        "\"On\"",
        "{\"case\":\"Bad\",\"code\":13}",
        "null",
        "{\"x\":14,\"y\":15}")
    const n = nxReadAsNode(ser, text)
    // 嵌套 i16/i32 宽度保真：字段直读成功即证（i64 中间形态被严格恢复
    // 拒绝），再经 toParcel 取回声明宽度槽二次确认。
    if (n.width != (7 as i16)) { return nxFail(11, "N1 width") }
    const widthWire = (n:Serializable.toParcel()).getElement\<i16>("width") if? (0 as i16)
    if (widthWire != (7 as i16)) { return nxFail(12, "N1 width wire") }
    if (n.pt.x != (1 as i32)) { return nxFail(13, "N1 pt.x") }
    if (n.pt.y != (2 as i32)) { return nxFail(14, "N1 pt.y") }
    const ptXWire = (n.pt:Serializable.toParcel()).getElement\<i32>("x") if? (0 as i32)
    if (ptXWire != (1 as i32)) { return nxFail(15, "N1 pt.x wire") }
    // 嵌套嵌套。
    if (n.rect.tl.x != (3 as i32)) { return nxFail(16, "N1 rect.tl.x") }
    if (n.rect.br.y != (6 as i32)) { return nxFail(17, "N1 rect.br.y") }
    // 容器成员元素/值对象引导（宽度保真）。
    if (n.children.length != (2 as i64)) { return nxFail(18, "N1 children len") }
    const child1 = n.children.getAtIndex((1 as i64)) if? (new NdPoint((0 as i32), (0 as i32)))
    if (child1.y != (10 as i32)) { return nxFail(19, "N1 children[1].y") }
    if (n.byName.count != (1 as i64)) { return nxFail(110, "N1 byName count") }
    const byNameA = n.byName.tryGet("a") if? (new NdPoint((0 as i32), (0 as i32)))
    if (byNameA.x != (11 as i32)) { return nxFail(111, "N1 byName a.x") }
    // 嵌套枚举：无载荷裸 case 名 / 带载荷 case 对象（经 toParcel 的
    // meta ..case 通道确认 case 与载荷）。
    const markWire = n.mark:Serializable.toParcel()
    if ((markWire.getMetaElement\<String>("..case") if? "") != "On") {
        return nxFail(112, "N1 mark case")
    }
    const resultWire = n.result:Serializable.toParcel()
    if ((resultWire.getMetaElement\<String>("..case") if? "") != "Bad") {
        return nxFail(113, "N1 result case")
    }
    if ((resultWire.getElement\<i32>("code") if? (0 as i32)) != (13 as i32)) {
        return nxFail(114, "N1 result code")
    }
    // 可空字段：显式 null 与存在值。
    var tagNull = false
    if (n.tag == null) { tagNull = true }
    if (not tagNull) { return nxFail(115, "N1 tag null") }
    var maybeNull = false
    if (n.maybe == null) { maybeNull = true }
    if (maybeNull) { return nxFail(116, "N1 maybe null") }
    if ((n.maybe if? (new NdPoint((0 as i32), (0 as i32)))).x != (14 as i32)) {
        return nxFail(117, "N1 maybe.x")
    }
    return 0
}

// ── N2：嵌套枚举其余形态 + 顶层容器目标 ──

func sectionN2(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    // 无载荷枚举的对象互操作形态（{"case":"On"}）。
    const textObj = nxNodeJson(nxGoodTail(),
        "{\"tl\":{\"x\":3,\"y\":4},\"br\":{\"x\":5,\"y\":6}}",
        "[]", "{}",
        "{\"case\":\"On\"}",
        "{\"case\":\"Good\",\"code\":0}",
        "\"t\"", "null")
    const nObj = nxReadAsNode(ser, textObj)
    const markObjWire = nObj.mark:Serializable.toParcel()
    if ((markObjWire.getMetaElement\<String>("..case") if? "") != "On") {
        return nxFail(21, "N2 mark obj form")
    }
    if ((nObj.tag if? "") != "t") { return nxFail(22, "N2 tag") }
    // 保留类型信息形态（嵌套枚举两形态）。
    const textTyped = nxNodeJson(nxGoodTail(),
        "{\"tl\":{\"x\":3,\"y\":4},\"br\":{\"x\":5,\"y\":6}}", "[]", "{}",
        "{\".rigi.type-identifier\":\"NdMark\",\".rigi.enum-case\":\"Off\"}",
        "{\".rigi.type-identifier\":\"NdResult\",\".rigi.enum-case\":\"Good\",\"code\":2}",
        "null", "null")
    const nTyped = nxReadAsNode(ser, textTyped)
    const markTypedWire = nTyped.mark:Serializable.toParcel()
    if ((markTypedWire.getMetaElement\<String>("..case") if? "") != "Off") {
        return nxFail(23, "N2 mark typed")
    }
    const typedWire = nTyped.result:Serializable.toParcel()
    const typedCode = typedWire.getElement\<i32>("code") if? (0 as i32)
    if (typedCode != (2 as i32)) { return nxFail(24, "N2 result typed code") }
    // 顶层 List/Map 目标的自定义对象元素：wb-5-2d 起走 envelope 通道
    // （SB envelope wire + 严格 fromParcel<T>，元素为记录形态）——原
    // 「类型擦除下无从取得静态 T 走 fromParcel」限制已解除（已知静态 T
    // 的入口承担最终恢复）；定向语料见 json_root_object_containers.rg。
    // 本文件的 N1 children/byName 成员槽通道（字段闭包 + buildWire 裸
    // 载荷 + DecodeBody 直入）保持原状，两通道互不干扰。
    // 自引用类型：256 层链正常恢复（类型级递归按输入节点驱动）。
    const chainText = nxChainText((256 as i32))
    const chain = nxAsDisposed\<NdChain>(ser, chainText)
    var depth: i32 = 0
    var cur: NdChain? = chain
    while (cur != null) {
        depth = (depth + 1)
        cur = cur.next
    }
    if (depth != (256 as i32)) { return nxFail(28, "N2 chain depth") }
    // 257 层被 JSON 深度上限拒绝（默认 256：根容器深度 1）。
    rc = nxExpectChainError(ser, nxChainText((257 as i32)), (29 as i32))
    if (rc != 0) { return rc }
    return 0
}

// 自引用链 JSON：depth 层嵌套对象，最内层 next=null。
func nxChainText(depth: i32): String {
    const sb = new StringBuilder()
    var i: i32 = 0
    while (i < depth) {
        sb.append("{\"v\":1,\"next\":")
        i = (i + 1)
    }
    sb.append("null")
    i = 0
    while (i < depth) {
        sb.append("}")
        i = (i + 1)
    }
    return sb.toString()
}

func nxExpectChainError(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = nxStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<NdChain>(s)
    } catch (e: JsonException) {
        threw = true
    } catch (e2: SerializationException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return nxFail(code, "链深 257 未报错")
}

// ── N3：嵌套负例（严格恢复 + 读取面报错）──

func sectionN3(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    const goodRect = "{\"tl\":{\"x\":3,\"y\":4},\"br\":{\"x\":5,\"y\":6}}"
    const goodRest = "\"On\",\"result\":{\"case\":\"Good\",\"code\":0},\"tag\":null,\"maybe\":null"
    // 嵌套字段缺失（pt 无 y）——严格恢复。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":{\"x\":1},\"rect\":${goodRect},\"children\":[],\"byName\":{},\"mark\":${goodRest}}",
        (31 as i32))
    if (rc != 0) { return rc }
    // 嵌套字段多余（pt 多 z）——严格恢复。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":{\"x\":1,\"y\":2,\"z\":3},\"rect\":${goodRect},\"children\":[],\"byName\":{},\"mark\":${goodRest}}",
        (32 as i32))
    if (rc != 0) { return rc }
    // 嵌套标量类型不匹配（pt.x 为字符串）——读取面。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":{\"x\":\"1\",\"y\":2},\"rect\":${goodRect},\"children\":[],\"byName\":{},\"mark\":${goodRest}}",
        (33 as i32))
    if (rc != 0) { return rc }
    // 小数词法进整数槽（pt.x = 1.0）——读取面。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":{\"x\":1.0,\"y\":2},\"rect\":${goodRect},\"children\":[],\"byName\":{},\"mark\":${goodRest}}",
        (34 as i32))
    if (rc != 0) { return rc }
    // null 进非可空嵌套对象（pt=null）——严格恢复。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":null,\"rect\":${goodRect},\"children\":[],\"byName\":{},\"mark\":${goodRest}}",
        (35 as i32))
    if (rc != 0) { return rc }
    // 嵌套枚举未知 case——读取面。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":{\"x\":1,\"y\":2},\"rect\":${goodRect},\"children\":[],\"byName\":{},\"mark\":\"Blink\",\"result\":{\"case\":\"Good\",\"code\":0},\"tag\":null,\"maybe\":null}",
        (36 as i32))
    if (rc != 0) { return rc }
    // 嵌套枚举形状错误（mark=5）——读取面。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":{\"x\":1,\"y\":2},\"rect\":${goodRect},\"children\":[],\"byName\":{},\"mark\":5,\"result\":{\"case\":\"Good\",\"code\":0},\"tag\":null,\"maybe\":null}",
        (37 as i32))
    if (rc != 0) { return rc }
    // 容器元素对象字段缺失（children[0] 无 y）——严格恢复。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":{\"x\":1,\"y\":2},\"rect\":${goodRect},\"children\":[{\"x\":1}],\"byName\":{},\"mark\":${goodRest}}",
        (38 as i32))
    if (rc != 0) { return rc }
    // Map 值对象宽度不匹配（byName.a.x 为字符串）——读取面。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":{\"x\":1,\"y\":2},\"rect\":${goodRect},\"children\":[],\"byName\":{\"a\":{\"x\":\"1\",\"y\":2}},\"mark\":${goodRest}}",
        (39 as i32))
    if (rc != 0) { return rc }
    // 嵌套对象遇非对象节点（pt=5）——读取面形状错误。
    rc = nxExpectNodeError(ser,
        "{\"width\":7,\"pt\":5,\"rect\":${goodRect},\"children\":[],\"byName\":{},\"mark\":${goodRest}}",
        (310 as i32))
    if (rc != 0) { return rc }
    return 0
}

// ── N4：按名反射重载直接断言 ──

func sectionN4(ser: JsonSerializer): i32 {
    // fieldsOf("NdPoint")：字段闭包（声明序、规范类型名、可空标志）。
    const ptFields = fieldsOf("NdPoint")
    if (ptFields.length != (2 as i32)) { return nxFail(41, "N4 pt fields len") }
    const f0 = ptFields[(0 as i32)] if? (new FieldInfo("?", ".any", true))
    if (f0.name != "x") { return nxFail(42, "N4 pt f0 name") }
    if (f0.typeName != ".i32") { return nxFail(43, "N4 pt f0 type") }
    if (f0.nullable) { return nxFail(44, "N4 pt f0 nullable") }
    const f1 = ptFields[(1 as i32)] if? (new FieldInfo("?", ".any", true))
    if (f1.name != "y") { return nxFail(45, "N4 pt f1 name") }
    // fieldsOf("NdNode")：容器键值/元素类型文本 + 可空结构化载体。
    const nodeFields = fieldsOf("NdNode")
    var childrenType = ""
    var byNameType = ""
    var maybeType = ""
    var maybeNullable = false
    var widthType = ""
    var i: i64 = (0 as i64)
    while (i < (nodeFields.length as i64)) {
        const fi = (nodeFields[(i as i32)] if? (new FieldInfo("?", ".any", true)))
        if (fi.name == "children") { childrenType = fi.typeName }
        if (fi.name == "byName") { byNameType = fi.typeName }
        if (fi.name == "maybe") {
            maybeType = fi.typeName
            maybeNullable = fi.nullable
        }
        if (fi.name == "width") { widthType = fi.typeName }
        i = (i + (1 as i64))
    }
    if (childrenType != "core.collections::List<NdPoint>") {
        return nxFail(46, "N4 children type: ${childrenType}")
    }
    if (byNameType != "core.collections::Map<.string, NdPoint>") {
        return nxFail(47, "N4 byName type: ${byNameType}")
    }
    if ((maybeType != "NdPoint") or (not maybeNullable)) {
        return nxFail(48, "N4 maybe: ${maybeType} nullable=${maybeNullable}")
    }
    if (widthType != ".i16") { return nxFail(49, "N4 width type: ${widthType}") }
    // casesOf("NdMark")：固定 case 的 fields 为空数组。
    const markCases = casesOf("NdMark")
    if (markCases.length != (2 as i32)) { return nxFail(410, "N4 mark cases len") }
    const c0 = markCases[(0 as i32)] if? (new EnumCaseInfo("?", arrayOf\<FieldInfo>(0)))
    if (c0.name != "Off") { return nxFail(411, "N4 mark case0") }
    if (c0.fields.length != (0 as i32)) { return nxFail(412, "N4 mark case0 fields") }
    // casesOf("NdResult")：载荷洞（名称 + 声明类型）。
    const resultCases = casesOf("NdResult")
    var badCodeType = ""
    var j: i64 = (0 as i64)
    while (j < (resultCases.length as i64)) {
        const ci = (resultCases[(j as i32)] if?
            (new EnumCaseInfo("?", arrayOf\<FieldInfo>(0))))
        if (ci.name == "Bad") {
            if (ci.fields.length == (1 as i32)) {
                badCodeType = (ci.fields[(0 as i32)] if?
                    (new FieldInfo("?", ".any", true))).typeName
            }
        }
        j = (j + (1 as i64))
    }
    if (badCodeType != ".i32") { return nxFail(413, "N4 Bad code type") }
    // isSerializable：已登记（用户类型 + 内建标量）true；未登记 false。
    if (not isSerializable("NdPoint")) { return nxFail(414, "N4 isSer NdPoint") }
    if (not isSerializable(".i32")) { return nxFail(415, "N4 isSer .i32") }
    if (isSerializable("NoSuchRegisteredType")) {
        return nxFail(416, "N4 isSer unknown")
    }
    // 未登记名查询抛 IllegalArgumentException（字段与 case 通道同口径）。
    try {
        const ignored = fieldsOf("NoSuchRegisteredType")
    } catch (e: core.IllegalArgumentException) {
        try {
            const ignored2 = casesOf("NoSuchRegisteredType")
        } catch (e2: core.IllegalArgumentException) {
            return 0
        }
        return nxFail(417, "N4 casesOf unknown 未抛")
    }
    return nxFail(418, "N4 fieldsOf unknown 未抛")
}

pub func main(): i32 {
    const ser = new JsonSerializer()
    var rc: i32 = 0
    rc = sectionN1(ser)
    if (rc != 0) { return rc }
    rc = sectionN2(ser)
    if (rc != 0) { return rc }
    rc = sectionN3(ser)
    if (rc != 0) { return rc }
    rc = sectionN4(ser)
    if (rc != 0) { return rc }
    Console.println("json-read-nested-ok")
    return 0
}
