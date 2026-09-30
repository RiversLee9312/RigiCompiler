import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: json-read-ok
// expect-exit: 0
// 施工块 5-2b：JsonSerializer 读取面（§4.7 读侧：§4.7.1 读取入口与
// 目标类型、§4.7.2 读侧类型标识解读、§4.7.3 Map 恢复与重复键、
// §4.7.4 互操作形态 readAs、§4.7.5 数字/字符/容器读侧、§4.7.6 完整
// 文档/重复成员名/编码/限制/错误偏移）语料。
// 覆盖：
//   - 动态读全值域：对象→Map<String, Any?>（键序保留、业务键原样）、
//     数组→Array<Any?>、空对象/空数组区分、null/bool/数字（i64 边界、
//     i64→u64 升级、超 u64 报错、小数/指数→double、整数不经浮点）；
//   - 语法拒绝：空文档、第二根值、注释、尾随逗号、重复成员名（转义
//     还原后 "a" 与 "\u0061"）、非法转义、未配对代理（高/低/高后非
//     低代理）、控制字符未转义、数字非法形态（前导零 01、1.、.5、
//     1e、+1、-、1e+、0x 尾字符）；
//   - 限制：深度 255 过/256 过/257 拒（默认 256，根容器深度 1）、
//     可配置更小上限、总输入字节/字符串 token/数字 token 上限；
//   - BOM：UTF-8 BOM 接受并跳过（偏移含 BOM 计数）；错误偏移精确
//     断言（含 BOM 与不含 BOM 两例）；
//   - 借用：传入流不关闭不 flush（成功与失败两路探测计数）；
//   - 写读往返：json_write 输出 read 回来再断言——Map content 形态
//     恢复为活 Map（值按声明键值类型构造）、非 String 键 Map、枚举
//     两形态（带载荷 rich / 无载荷 enum struct）恢复为 Parcel（meta
//     ..case 通道）并可原样再写出、嵌套对象类型标识恢复为 Parcel；
//   - Map content 负例：缺 content、content 非数组、条目非对象、
//     条目成员缺失/多余、重复键报错（不经 Map.set 静默覆盖）；
//   - readAs 类型引导：无标识普通 JSON 恢复自定义对象（i32 不经
//     i64—— deserialize 动态路径对照负例、char 恰一标量含补充平面、
//     List<i32>/Map<String, i32>/嵌套对象/可空字段显式存在、成员序
//     任意）；字段缺失/多余/类型不匹配/null 进非可空均由严格恢复
//     报错；枚举两形态（裸 case 名/带载荷 case 对象/保留类型信息
//     形态、未知 case、带载荷 case 的裸字符串形状错误）；标量与
//     集合顶层目标（含空容器依声明恢复）；Map 目标业务键原样（
//     .rigi.type-identifier 作业务键，§4.7.4 固定示例）；Type\<T>
//     值形态入口；
//   - deserialize\<T> 走通（基类便利层：read→验 Parcel→严格
//     fromParcel）：宽度兼容形状（i64/String/bool/可空/嵌套对象）
//     往返相等；宽度不兼容形状（i32 字段）按契约报
//     SerializationException（§4.7.5 不承诺无损往返）；
//   - 读侧编码构造选项与写侧共享（utf-8/utf8 并收、其他报错），
//     轻量复用写侧 G5 覆盖，本文件不重开。
//
// wire 形状注记（与 serializer_base/json_write 实测口径一致）：Parcel
// 业务字段里的集合载荷统一为 .array<.any>（List 元素序、Map 摊平
// 键值交替序）；JSON 读取面对无目标数组统一得到 Array<Any?>，对
// 类型引导的集合字段产出 Array<Any> 载荷（严格恢复的形状前提）。

// ── 业务语料类型 ──

@Serializable()
class RdLeaf {
    pub var flag: bool
    pub var label: String
    pub init(_ -> flag, _ -> label)
}

@Serializable()
class RdNest {
    pub var num: i32
    pub var ch: char
    pub var scores: List\<i32>
    pub var lookup: Map\<String, i32>
    pub var child: RdLeaf
    pub var note: String?
    pub init(_ -> num, _ -> ch, _ -> scores, _ -> lookup, _ -> child,
        _ -> note)
}

// deserialize 走通用例：字段形状全部宽度兼容动态规则（i64/String/
// bool/可空/嵌套 bool+String 对象），serialize→deserialize 往返相等。
@Serializable()
class RdWide {
    pub var v: i64
    pub var s: String
    pub var b: bool
    pub var note: String?
    pub var child: RdLeaf
    pub init(_ -> v, _ -> s, _ -> b, _ -> note, _ -> child)
}

// 无载荷枚举（非 rich）：互操作形态输出 case 名字符串。
@Serializable()
pub enum struct RdMark {} [Low -> 1, High -> 2]

// 契约固定示例用枚举（rich）：Failed 带 errorCode: i32 载荷；Ok 与
// Failed 同枚举（rich enum 的 case 按既有绑定先例全 case 绑定载荷
// 形参）。
@Serializable()
pub rich enum struct RdResult {
    pub var errorCode: i32
    pub init(_ -> errorCode)
} [Ok(errorCode = _) -> 0, Failed(errorCode = _) -> 1]

// ── 探测流：借用语义计数（serializer_base.rg 同款）──

class RdProbeInput : InputStream {
    pub var readCalls: i32 = 0
    pub var disposeRuns: i32 = 0
    priv var data: Array\<u8>
    priv var pos: i32

    pub init(source: Array\<u8>) {
        data = source
        pos = 0
    }

    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        readCalls = (readCalls + 1)
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) {
            return 0
        }
        if (pos >= data.length) {
            return 0
        }
        var want: i32 = count
        if (want > (data.length - pos)) {
            want = (data.length - pos)
        }
        var i: i32 = 0
        while (i < want) {
            buffer[offset + i] = (data[pos + i] as u8)
            i = (i + 1)
        }
        pos = (pos + want)
        return want
    }

    protected override func disposeCore() {
        disposeRuns = (disposeRuns + 1)
    }
}

// ── 断言与输入助手 ──

func rdFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

func rdExpect(actual: bool, code: i32): i32 {
    if (actual) { return 0 }
    return rdFail(code, "断言失败")
}

// String → 内存输入流（UTF-8 编码快照）。
func rdStream(text: String): MemoryInputStream {
    const enc = new Utf8Encoder()
    return new MemoryInputStream(enc.encode(text))
}

func rdRead(ser: JsonSerializer, text: String): Any? {
    const s = rdStream(text)
    try {
        return ser.read(s)
    } finally(e) {
        s.dispose()
    }
}

func rdReadBytes(ser: JsonSerializer, bytes: Array\<u8>): Any? {
    const span = core.collections.spanOf\<u8>((bytes.length as i32))
    var i: i32 = 0
    while (i < bytes.length) {
        span[i] = (bytes[i] as u8)
        i = (i + 1)
    }
    const s = new MemoryInputStream(span)
    try {
        return ser.read(s)
    } finally(e) {
        s.dispose()
    }
}

// 写出为文本（MemoryOutputStream 收字节，Utf8Decoder 严格回读）。
func rdWrite(ser: JsonSerializer, value: Any?): String {
    const out = new MemoryOutputStream()
    try {
        ser.write(value, out)
        const decoder = new Utf8Decoder()
        return decoder.decode(out.toSpan(), true)
    } finally(e) {
        out.dispose()
    }
}

// 期望 JsonException（语法/表示/范围/限制族）的通用负例助手。
func rdExpectError(ser: JsonSerializer, text: String, code: i32): i32 {
    try {
        const ignored = rdRead(ser, text)
    } catch (e: JsonException) {
        return 0
    }
    return rdFail(code, "未抛 JsonException：[${text}]")
}

// 期望 JsonException 且 offset 精确（§4.7.6：零基字节偏移，含 BOM）。
func rdExpectOffset(ser: JsonSerializer, text: String, want: i64,
        code: i32): i32 {
    try {
        const ignored = rdRead(ser, text)
    } catch (e: JsonException) {
        if (e.offset == want) { return 0 }
        return rdFail(code, "offset ${e.offset} != ${want}")
    }
    return rdFail(code, "未抛 JsonException：[${text}]")
}

// 字节形态输入的偏移断言（BOM 等无法经 String 插值构造的输入）。
func rdExpectOffsetBytes(ser: JsonSerializer, bytes: Array\<u8>, want: i64,
        code: i32): i32 {
    try {
        const ignored = rdReadBytes(ser, bytes)
    } catch (e: JsonException) {
        if (e.offset == want) { return 0 }
        return rdFail(code, "offset ${e.offset} != ${want}")
    }
    return rdFail(code, "未抛 JsonException（字节输入）")
}

// 期望任意异常（严格恢复 SerializationException 等）的负例助手。
func rdExpectAnyError(ser: JsonSerializer, text: String, code: i32): i32 {
    try {
        const ignored = rdRead(ser, text)
    } catch (e: JsonException) {
        return 0
    } catch (e2: core.RuntimeException) {
        return 0
    }
    return rdFail(code, "未抛异常：[${text}]")
}

// readAs\<T> 包装（文本 → 目标类型恢复）。
func rdReadAsNest(ser: JsonSerializer, text: String): RdNest {
    const s = rdStream(text)
    try {
        return ser.readAs\<RdNest>(s)
    } finally(e) {
        s.dispose()
    }
}

// readAs 的即用即 Dispose 包装（正例单行断言用：传入流借用——
// Serializer 不关闭调用者流，dispose 由创建方的本助手负责）。
func rdAsDisposed\<T with Serializable>(ser: JsonSerializer, text: String): T {
    const s = rdStream(text)
    try {
        return ser.readAs\<T>(s)
    } finally(e) {
        s.dispose()
    }
}

// 值的类型名（双宿主拼写判别助手：VM BIL 别名形 / native canonical）。
func rdTypeName(v: Any): String {
    return typeOf(v).toString()
}

func rdIsType(name: String, alias: String, canonical: String): bool {
    return (name == alias) or (name == canonical)
}

// 非可空 Any 收窄与类型化解包助手：native 后端对「可空 Any 槽」的
// cast/typeof 有收窄缺陷（b4-2 实测抛 CastException，VM cast 宽松），
// 先判 null 收窄为非可空 Any 再取值则双宿主一致——同 Parcel
// .setDynamic 的 putDynamicChecked 分拆先例。
func rdNN(v: Any?): Any {
    if (v == null) { throw new core.RuntimeException("rdNN：意外的 null") }
    return v
}

func rdI64(v: Any): i64 { return v as i64 }
func rdU64(v: Any): u64 { return v as u64 }
func rdF64(v: Any): double { return v as double }
func rdB(v: Any): bool { return v as bool }
func rdS(v: Any): String { return v as String }
func rdAO(v: Any): Array\<Any?> { return v as Array\<Any?> }
func rdMSA(v: Any): core.collections.Map\<String, Any?> {
    return v as core.collections.Map\<String, Any?>
}
func rdMSI(v: Any): Map\<String, i32> { return v as Map\<String, i32> }
func rdMII(v: Any): Map\<i32, i32> { return v as Map\<i32, i32> }
func rdP(v: Any): Parcel { return v as Parcel }

// ── A. 动态读全值域 ──

func sectionA(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    // A1：对象 → Map<String, Any?>，键序保留、值域完整。
    const v1 = rdRead(ser, "{\"a\":1,\"b\":true,\"c\":null,\"d\":\"x\",\"e\":[1,2],\"f\":{}}")
    if (v1 == null) { return rdFail(11, "A1 null") }
    if (not (v1 is core.collections.Map\<String, Any?>)) { return rdFail(12, "A1 not Map") }
    const m1 = rdMSA(v1)
    if (m1.count != (6 as i64)) { return rdFail(13, "A1 count") }
    if ((m1.keyAtIndex((0 as i64)) as String) != "a") { return rdFail(14, "A1 key0") }
    if ((m1.keyAtIndex((5 as i64)) as String) != "f") { return rdFail(15, "A1 key5") }
    const aVal = m1.tryGet("a")
    if (aVal == null) { return rdFail(16, "A1 a null") }
    if (not rdIsType(rdTypeName(aVal), ".i64", "core::i64")) { return rdFail(17, "A1 a not i64") }
    if (rdI64(aVal) != (1 as i64)) { return rdFail(18, "A1 a value") }
    const cVal = m1.tryGet("c")
    var cIsNull = false
    if (cVal == null) { cIsNull = true }
    if (not cIsNull) { return rdFail(19, "A1 c not null") }
    if (not m1.containsKey("c")) { return rdFail(110, "A1 c absent") }
    const bRaw = m1.tryGet("b")
    if (bRaw == null) { return rdFail(1110, "A1 b null") }
    if (not rdB(bRaw)) { return rdFail(111, "A1 b") }
    const dRaw = m1.tryGet("d")
    if (dRaw == null) { return rdFail(1111, "A1 d null") }
    if (rdS(dRaw) != "x") { return rdFail(112, "A1 d") }
    const eVal = m1.tryGet("e")
    if (eVal == null) { return rdFail(113, "A1 e null") }
    if (not (eVal is Array\<Any?>)) { return rdFail(114, "A1 e not Array") }
    const eArr = rdAO(eVal)
    if (eArr.length != (2 as i32)) { return rdFail(115, "A1 e len") }
    const fVal = m1.tryGet("f")
    if (fVal == null) { return rdFail(116, "A1 f null") }
    if (not (fVal is core.collections.Map\<String, Any?>)) { return rdFail(117, "A1 f not Map") }
    if (rdMSA(fVal).count != (0 as i64)) { return rdFail(118, "A1 f count") }
    // A2：空对象与空数组保持不同。
    const v2 = rdRead(ser, "[]")
    if (v2 == null) { return rdFail(121, "A2 null") }
    if (not (v2 is Array\<Any?>)) { return rdFail(122, "A2 not Array") }
    if (rdAO(v2).length != (0 as i32)) { return rdFail(123, "A2 len") }
    const v3 = rdRead(ser, "{}")
    if (not (v3 is core.collections.Map\<String, Any?>)) { return rdFail(124, "A3 not Map") }
    // A3：根标量（null 实际 null / bool / String / 数字）。
    if (rdRead(ser, "null") != null) { return rdFail(125, "A3 null root") }
    const tVal = rdRead(ser, "true")
    if (tVal == null) { return rdFail(126, "A3 true null") }
    if (not (tVal is bool)) { return rdFail(127, "A3 true kind") }
    const sVal = rdRead(ser, "\"hi\"")
    if (sVal == null) { return rdFail(128, "A3 str null") }
    if (not (sVal is String)) { return rdFail(129, "A3 str kind") }
    if (rdS(sVal) != "hi") { return rdFail(130, "A3 str value") }
    // A4：数字边界——i64 最大/最小、u64 升级、超 u64 报错、小数/指数
    // → double、整数不经浮点。
    const i64max = rdRead(ser, "9223372036854775807")
    if (rdI64(rdNN(i64max)) != (9223372036854775807L as i64)) { return rdFail(131, "A4 i64max") }
    const i64min = rdRead(ser, "-9223372036854775808")
    if (rdI64(rdNN(i64min)) != ((-9223372036854775807L - (1 as i64)))) { return rdFail(132, "A4 i64min") }
    const u64v = rdRead(ser, "9223372036854775808")
    if (not rdIsType(rdTypeName(rdNN(u64v)), ".u64", "core::u64")) { return rdFail(133, "A4 u64 kind") }
    if (rdU64(rdNN(u64v)) != (9223372036854775808UL as u64)) { return rdFail(134, "A4 u64 value") }
    const u64max = rdRead(ser, "18446744073709551615")
    if (rdU64(rdNN(u64max)) != (18446744073709551615UL as u64)) { return rdFail(135, "A4 u64max") }
    rc = rdExpectError(ser, "18446744073709551616", (136 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "-9223372036854775809", (137 as i32))
    if (rc != 0) { return rc }
    const dbl = rdRead(ser, "1.5")
    if (not rdIsType(rdTypeName(rdNN(dbl)), ".f64", "core::double")) { return rdFail(138, "A4 1.5 kind") }
    if (rdF64(rdNN(dbl)) != (1.5 as double)) { return rdFail(139, "A4 1.5") }
    const dbl2 = rdRead(ser, "1e3")
    if (rdF64(rdNN(dbl2)) != (1000.0 as double)) { return rdFail(140, "A4 1e3") }
    const negz = rdRead(ser, "-0.0")
    if (rdF64(rdNN(negz)) != ((-0.0) as double)) { return rdFail(141, "A4 -0.0") }
    const intV = rdRead(ser, "3")
    if (not rdIsType(rdTypeName(rdNN(intV)), ".i64", "core::i64")) { return rdFail(142, "A4 3 not i64") }
    // A5：键序保留（插入序即文本序）。
    const v5 = rdRead(ser, "{\"z\":1,\"a\":2,\"m\":3}")
    const m5 = rdMSA(rdNN(v5))
    if ((m5.keyAtIndex((0 as i64)) as String) != "z") { return rdFail(143, "A5 k0") }
    if ((m5.keyAtIndex((1 as i64)) as String) != "a") { return rdFail(144, "A5 k1") }
    if ((m5.keyAtIndex((2 as i64)) as String) != "m") { return rdFail(145, "A5 k2") }
    return 0
}

// ── B. 语法拒绝 / 限制 / BOM / 偏移 / 借用 ──

// 嵌套数组文本：openCount 层 "[" + "1" + openCount 层 "]"。
func rdNest(openCount: i32): String {
    const sb = new StringBuilder()
    var i: i32 = 0
    while (i < openCount) {
        sb.append("[")
        i = (i + 1)
    }
    sb.append("1")
    i = 0
    while (i < openCount) {
        sb.append("]")
        i = (i + 1)
    }
    return sb.toString()
}

func sectionB(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    // B1：空文档（空串 / 纯空白）。
    rc = rdExpectError(ser, "", (21 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "   ", (22 as i32))
    if (rc != 0) { return rc }
    // B2：第二根值。
    rc = rdExpectError(ser, "1 2", (23 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "[] {}", (24 as i32))
    if (rc != 0) { return rc }
    // B3：注释（JSON 无注释语法）。
    rc = rdExpectError(ser, "// x\n1", (25 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "/* */ 1", (26 as i32))
    if (rc != 0) { return rc }
    // B4：尾随逗号。
    rc = rdExpectError(ser, "[1,]", (27 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "{\"a\":1,}", (28 as i32))
    if (rc != 0) { return rc }
    // B5：重复成员名（含转义还原后 "a" 与 "\u0061"）。
    rc = rdExpectError(ser, "{\"a\":1,\"a\":2}", (29 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "{\"a\":1,\"\\u0061\":2}", (210 as i32))
    if (rc != 0) { return rc }
    // B6：非法转义。
    rc = rdExpectError(ser, "\"\\x\"", (211 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "\"\\u12\"", (212 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "\"\\u12zz\"", (213 as i32))
    if (rc != 0) { return rc }
    // B7：未配对代理（孤立高/孤立低/高后非低代理）。
    rc = rdExpectError(ser, "\"\\uD83D\"", (214 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "\"\\uDE00\"", (215 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "\"\\uD83Dx\"", (216 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "\"\\uD83D\\u0041\"", (217 as i32))
    if (rc != 0) { return rc }
    // B8：控制字符未转义（原始 0x01 字节入串）。
    rc = rdExpectError(ser, "\"a${(1 as char)}b\"", (218 as i32))
    if (rc != 0) { return rc }
    // B9：数字非法形态（JSON 自有数字语法，RFC 8259）。
    rc = rdExpectError(ser, "01", (219 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "-01", (220 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "1.", (221 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, ".5", (222 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "1e", (223 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "+1", (224 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "-", (225 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "1e+", (226 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser, "0x1", (227 as i32))
    if (rc != 0) { return rc }
    // B10：深度边界——默认上限 256（根容器深度 1）：255 过 / 256 过 /
    // 257 拒。
    if (rdRead(ser, rdNest((255 as i32))) == null) { return rdFail(228, "B10 d255") }
    if (rdRead(ser, rdNest((256 as i32))) == null) { return rdFail(229, "B10 d256") }
    rc = rdExpectError(ser, rdNest((257 as i32)), (230 as i32))
    if (rc != 0) { return rc }
    // B11：可配置更小上限（maxDepth=2：[[1]] 过、[[[1]]] 拒）。
    const serD2 = new JsonSerializer("", (2 as i32))
    if (rdRead(serD2, "[[1]]") == null) { return rdFail(231, "B11 d2 ok") }
    rc = rdExpectError(serD2, "[[[1]]]", (232 as i32))
    if (rc != 0) { return rc }
    // B12：输入/token 上限（总字节 / 字符串 / 数字）。
    const serL = new JsonSerializer("", (256 as i32), "utf-8",
        (5 as i64), (0 as i64), (0 as i64))
    rc = rdExpectError(serL, "[1,2,3]", (233 as i32))
    if (rc != 0) { return rc }
    if (rdRead(serL, "[1,2]") == null) { return rdFail(234, "B12 total ok") }
    const serS = new JsonSerializer("", (256 as i32), "utf-8",
        (0 as i64), (3 as i64), (0 as i64))
    rc = rdExpectError(serS, "\"abcdef\"", (235 as i32))
    if (rc != 0) { return rc }
    if (rdRead(serS, "\"abc\"") == null) { return rdFail(236, "B12 str ok") }
    const serN = new JsonSerializer("", (256 as i32), "utf-8",
        (0 as i64), (0 as i64), (3 as i64))
    rc = rdExpectError(serN, "12345", (237 as i32))
    if (rc != 0) { return rc }
    if (rdRead(serN, "123") == null) { return rdFail(238, "B12 num ok") }
    // B13：BOM 接受并跳过；偏移含已消费 BOM。
    const bomBytes = core.collections.arrayOfElements\<u8>(
        (239 as u8), (187 as u8), (191 as u8), (52 as u8), (50 as u8))
    const bomVal = rdReadBytes(ser, bomBytes)
    if (rdI64(rdNN(bomVal)) != (42 as i64)) { return rdFail(239, "B13 bom") }
    // B14：错误偏移精确（不含 BOM："[1,]" 的 ']' 在偏移 3；含 BOM：
    // 偏移 6；非法字面量位置）。
    rc = rdExpectOffset(ser, "[1,]", (3 as i64), (240 as i32))
    if (rc != 0) { return rc }
    const bomErr = core.collections.arrayOfElements\<u8>(
        (239 as u8), (187 as u8), (191 as u8), (91 as u8), (49 as u8),
        (44 as u8), (93 as u8))
    rc = rdExpectOffsetBytes(ser, bomErr, (6 as i64), (241 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectOffset(ser, "tru", (3 as i64), (242 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectOffset(ser, "{\"a\":1,\"a\":2}", (7 as i64), (243 as i32))
    if (rc != 0) { return rc }
    // B14b：标量根不占容器深度（maxDepth=1 下标量与根容器照常成功，
    // 二层嵌套拒绝）。
    const serD1 = new JsonSerializer("", (1 as i32))
    if (rdRead(serD1, "5") == null) { return rdFail(244, "B14b scalar root") }
    if (rdRead(serD1, "[]") == null) { return rdFail(246, "B14b root array") }
    rc = rdExpectError(serD1, "[[]]", (245 as i32))
    if (rc != 0) { return rc }
    return 0
}

// ── B15：借用语义（成功与失败两路：不关闭、不 flush 语义计数）──

func sectionBorrow(ser: JsonSerializer): i32 {
    const okBytes = core.collections.arrayOfElements\<u8>(
        (123 as u8), (125 as u8))
    const probe = new RdProbeInput(okBytes)
    const v = ser.read(probe)
    if (v == null) { return rdFail(251, "borrow read null") }
    if (probe.disposeRuns != 0) { return rdFail(252, "borrow disposed") }
    if (probe.readCalls == 0) { return rdFail(253, "borrow no read") }
    probe.dispose()
    if (probe.disposeRuns != 1) { return rdFail(254, "borrow self dispose") }
    const badBytes = core.collections.arrayOfElements\<u8>(
        (91 as u8), (49 as u8), (44 as u8), (93 as u8))
    const probe2 = new RdProbeInput(badBytes)
    var threw = false
    try {
        const ignored = ser.read(probe2)
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return rdFail(255, "borrow error not thrown") }
    if (probe2.disposeRuns != 0) { return rdFail(256, "borrow error disposed") }
    // 探测流由测试方创建，最终由测试方处置（借用语义只约束 Serializer）。
    probe2.dispose()
    return 0
}

// ── C. 写读往返（json_write 输出 read 回来再断言）──

func sectionC(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    // C1：§4.7.3 Map<String, i32> content 形态 → 活 Map（值按声明键值
    // 类型构造，String 键原样——含 .rigi.type-identifier/content/a.b/
    // 空串业务键）。
    const table = new Map\<String, i32>()
    table.set(".rigi.type-identifier", (1 as i32))
    table.set("content", (42 as i32))
    table.set("a.b", (3 as i32))
    table.set("", (0 as i32))
    const tableText = rdWrite(ser, table)
    const back1 = rdRead(ser, tableText)
    if (back1 == null) { return rdFail(31, "C1 null") }
    if (not (back1 is Map\<String, i32>)) { return rdFail(32, "C1 not typed Map") }
    const backMap = rdMSI(back1)
    if (backMap.count != (4 as i64)) { return rdFail(33, "C1 count") }
    if ((backMap.keyAtIndex((0 as i64)) as String) != ".rigi.type-identifier") { return rdFail(34, "C1 k0") }
    if ((backMap.tryGet("content") if? (0 as i32)) != (42 as i32)) { return rdFail(35, "C1 v") }
    if ((backMap.tryGet("") if? (1 as i32)) != (0 as i32)) { return rdFail(36, "C1 empty key") }
    // C2：非 String 键 Map（Map<i32, i32>）content 形态 → 活 Map。
    const byInt = new Map\<i32, i32>()
    byInt.set((7 as i32), (8 as i32))
    byInt.set((9 as i32), (10 as i32))
    const back2 = rdRead(ser, rdWrite(ser, byInt))
    if (not (back2 is Map\<i32, i32>)) { return rdFail(37, "C2 kind") }
    const back2m = rdMII(back2)
    if ((back2m.tryGet((7 as i32)) if? (0 as i32)) != (8 as i32)) { return rdFail(38, "C2 v") }
    if ((back2m.tryGet((9 as i32)) if? (0 as i32)) != (10 as i32)) { return rdFail(39, "C2 v2") }
    // C3：Map content 负例——缺 content / content 非数组 / 条目非对象 /
    // 条目成员多余 / 条目成员缺失 / 重复键（不经 Map.set 静默覆盖）。
    rc = rdExpectError(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\"}",
        (310 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":{}}",
        (311 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[1]}",
        (312 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[{\"key\":\"a\",\"value\":1,\"extra\":2}]}",
        (313 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[{\"key\":\"a\"}]}",
        (314 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectError(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[{\"key\":\"a\",\"value\":1},{\"key\":\"a\",\"value\":2}]}",
        (315 as i32))
    if (rc != 0) { return rc }
    // 条目成员顺序宽容（value 在前同样接受）。
    const back3 = rdRead(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[{\"value\":1,\"key\":\"a\"}]}")
    if (not (back3 is Map\<String, i32>)) { return rdFail(316, "C3 order") }
    // C3b：非 String 键 content 的重复键判重（Map 既有 == 语义，不经
    // Map.set 静默覆盖）。
    rc = rdExpectError(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.i32, .i32>\",\"content\":[{\"key\":7,\"value\":1},{\"key\":7,\"value\":2}]}",
        (317 as i32))
    if (rc != 0) { return rc }
    // C3c：非 String 键 content 正常恢复为活 Map<i32, i32>。
    const back3c = rdRead(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.i32, .i32>\",\"content\":[{\"key\":7,\"value\":1},{\"key\":8,\"value\":2}]}")
    if (not (back3c is Map\<i32, i32>)) { return rdFail(332, "C3c kind") }
    if ((rdMII(back3c).tryGet((7 as i32)) if? (0 as i32)) != (1 as i32)) { return rdFail(333, "C3c v") }
    // C3d：未知但结构合法的类型标识仍保存在表示中（延迟到请求恢复
    // 对象时才由 fromParcel 报未知类型，§4.7.1）；类型元数据位于任意
    // 位置均可解析（本例标识在末尾，§4.7.6「解析不能依赖输出顺序」）。
    const back3d = rdRead(ser,
        "{\"x\":1,\".rigi.type-identifier\":\"NoSuchRegisteredType\"}")
    if (not (back3d is Parcel)) { return rdFail(334, "C3d not Parcel") }
    if (rdP(back3d).typeName != "NoSuchRegisteredType") { return rdFail(335, "C3d typeName") }
    const back3dX = rdP(back3d).getDynamic("x")
    if (back3dX == null) { return rdFail(337, "C3d x null") }
    if (rdI64(back3dX) != (1 as i64)) { return rdFail(336, "C3d field") }
    // C4：枚举保留类型信息形态 → Parcel（meta ..case 通道）并可原样
    // 再写出（read→write 文本往返）。
    const enumText = rdWrite(ser, RdResult.Failed(404):Serializable.toParcel())
    if (enumText != "{\".rigi.type-identifier\":\"RdResult\",\".rigi.enum-case\":\"Failed\",\"errorCode\":404}") {
        return rdFail(317, "C4 write text")
    }
    const enumBack = rdRead(ser, enumText)
    if (not (enumBack is Parcel)) { return rdFail(318, "C4 not Parcel") }
    const enumParcel = rdP(enumBack)
    if (enumParcel.typeName != "RdResult") { return rdFail(319, "C4 typeName") }
    if ((enumParcel.getMetaElement\<String>("..case") if? "") != "Failed") { return rdFail(320, "C4 case") }
    if (not enumParcel.contains("errorCode")) { return rdFail(321, "C4 field") }
    const enumAgain = rdWrite(ser, enumParcel)
    if (enumAgain != enumText) { return rdFail(322, "C4 roundtrip") }
    // C5：无载荷枚举（enum struct）两形态读回。
    const markText = rdWrite(ser, RdMark.High:Serializable.toParcel())
    const markBack = rdRead(ser, markText)
    if (not (markBack is Parcel)) { return rdFail(323, "C5 not Parcel") }
    const markParcel = rdP(markBack)
    if (markParcel.typeName != "RdMark") { return rdFail(324, "C5 typeName") }
    if ((markParcel.getMetaElement\<String>("..case") if? "") != "High") { return rdFail(325, "C5 case") }
    if (rdWrite(ser, markParcel) != markText) { return rdFail(326, "C5 roundtrip") }
    // C6：嵌套对象类型标识恢复为 Parcel（动态字段为 i64——§4.7.5 不
    // 承诺无损宽度；结构/类型名/case 元数据保真）。
    const nestText = rdWrite(ser, new RdNest((1 as i32), 'x', newList2(),
        newLookup2(), new RdLeaf(true, "kid"), null):Serializable.toParcel())
    const nestBack = rdRead(ser, nestText)
    if (not (nestBack is Parcel)) { return rdFail(327, "C6 not Parcel") }
    const nestParcel = rdP(nestBack)
    if (nestParcel.typeName != "RdNest") { return rdFail(328, "C6 typeName") }
    const childVal = nestParcel.getDynamic("child")
    if (not (childVal is Parcel)) { return rdFail(329, "C6 child") }
    const childLabel = rdP(rdNN(childVal)).getDynamic("label")
    if (childLabel == null) { return rdFail(331, "C6 child label null") }
    if (rdS(childLabel) != "kid") { return rdFail(330, "C6 child label") }
    return 0
}

func newList2(): List\<i32> {
    const scores = new List\<i32>()
    scores.add((5 as i32))
    scores.add((6 as i32))
    return scores
}

func newLookup2(): Map\<String, i32> {
    const lookup = new Map\<String, i32>()
    lookup.set("a", (1 as i32))
    return lookup
}

// ── D. readAs 类型引导 ──

func sectionD(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    // D1：无标识普通 JSON 恢复自定义对象——字段按声明直接构造（i32
    // 不经 i64：成功即证， deserialize 动态路径对照见 D2）；成员顺序
    // 任意（本例乱序）；可空字段显式存在（null）。
    const text1 = "{\"note\":null,\"child\":{\"label\":\"k\",\"flag\":true},\"lookup\":{\"b\":2,\"a\":1},\"ch\":\"x\",\"scores\":[1,2],\"num\":3}"
    const n1 = rdReadAsNest(ser, text1)
    if (n1.num != (3 as i32)) { return rdFail(41, "D1 num") }
    if (n1.ch != 'x') { return rdFail(42, "D1 ch") }
    if (n1.scores.length != (2 as i64)) { return rdFail(43, "D1 scores") }
    if ((n1.scores.getAtIndex((1 as i64)) if? (0 as i32)) != (2 as i32)) { return rdFail(44, "D1 scores1") }
    if ((n1.lookup.tryGet("a") if? (0 as i32)) != (1 as i32)) { return rdFail(45, "D1 lookup") }
    if (not n1.child.flag) { return rdFail(46, "D1 child flag") }
    if (n1.child.label != "k") { return rdFail(47, "D1 child label") }
    var noteNull = false
    if (n1.note == null) { noteNull = true }
    if (not noteNull) { return rdFail(48, "D1 note") }
    // D2：宽度对照——同一对象的带类型标识 serialize 输出经
    // deserialize（动态 read → i64 字段值）被严格恢复拒绝
    // （§4.7.5/§4.6.3：不承诺数值宽度无损往返；已知目标应走 readAs）。
    const out = new MemoryOutputStream()
    var serText: String = ""
    try {
        ser.serialize\<RdNest>(n1, out)
        const dec = new Utf8Decoder()
        serText = dec.decode(out.toSpan(), true)
    } finally(e) {
        out.dispose()
    }
    const inStream = rdStream(serText)
    var threw = false
    try {
        const ignored = ser.deserialize\<RdNest>(inStream)
    } catch (e2: SerializationException) {
        threw = true
    }
    inStream.dispose()
    if (not threw) { return rdFail(49, "D2 width mismatch not rejected") }
    // D3：字段缺失 / 多余 / 类型不匹配 / null 进非可空——严格恢复报错。
    rc = rdExpectNestError(ser, "{\"num\":3,\"ch\":\"x\",\"scores\":[1],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"}}",
        (410 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectNestError(ser, "{\"num\":3,\"ch\":\"x\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null,\"extra\":1}",
        (411 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectNestError(ser, "{\"num\":\"3\",\"ch\":\"x\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null}",
        (412 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectNestError(ser, "{\"num\":null,\"ch\":\"x\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null}",
        (413 as i32))
    if (rc != 0) { return rc }
    // D4：整数目标不接受小数/指数词法（3.0、3e0 不作整数输入）。
    rc = rdExpectNestError2(ser, "{\"num\":3.0,\"ch\":\"x\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null}",
        (414 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectNestError2(ser, "{\"num\":3e0,\"ch\":\"x\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null}",
        (415 as i32))
    if (rc != 0) { return rc }
    // D5：char 目标恰一个 Unicode 标量（0/2 标量报错；补充平面 1
    // 标量合法）。
    rc = rdExpectNestError(ser, "{\"num\":3,\"ch\":\"\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null}",
        (416 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectNestError(ser, "{\"num\":3,\"ch\":\"ab\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null}",
        (417 as i32))
    if (rc != 0) { return rc }
    const nEmoji = rdReadAsNest(ser, "{\"num\":3,\"ch\":\"\\uD83D\\uDE00\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null}")
    if ((nEmoji.ch as i64) != (128512 as i64)) { return rdFail(418, "D5 emoji") }
    const nEmoji2 = rdReadAsNest(ser, "{\"num\":3,\"ch\":\"${(128512 as char)}\",\"scores\":[],\"lookup\":{},\"child\":{\"flag\":true,\"label\":\"k\"},\"note\":null}")
    if ((nEmoji2.ch as i64) != (128512 as i64)) { return rdFail(419, "D5 emoji utf8") }
    // D6：保留类型信息的形态同样可由 readAs 按目标解释（类型标识
    // 与声明一致时正常恢复）。
    const typedText = rdWrite(ser, new RdNest((9 as i32), 'q', newList2(),
        newLookup2(), new RdLeaf(false, "t"), "n"):Serializable.toParcel())
    const nTyped = rdReadAsNest(ser, typedText)
    if (nTyped.num != (9 as i32)) { return rdFail(420, "D6 num") }
    const nNote = nTyped.note
    if (nNote == null) { return rdFail(422, "D6 note null") }
    if (rdS(nNote) != "n") { return rdFail(421, "D6 note") }
    return 0
}

// readAs\<RdNest> 负例：期望严格恢复 SerializationException 或读取面
// JsonException（标量种类不匹配由读取面词法/表示族报错）。
func rdExpectNestError(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<RdNest>(s)
    } catch (e: SerializationException) {
        threw = true
    } catch (e2: JsonException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "未抛异常：[${text}]")
}

// readAs\<RdNest> 负例：期望读取面 JsonException（表示/词法族）。
func rdExpectNestError2(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<RdNest>(s)
    } catch (e: JsonException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "未抛 JsonException：[${text}]")
}

func sectionD2(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    // D7：标量顶层目标。
    if (rdAsDisposed\<i32>(ser, "3") != (3 as i32)) { return rdFail(421, "D7 i32") }
    rc = rdExpectScalarError(ser, "3.0", (422 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectScalarError(ser, "3e0", (423 as i32))
    if (rc != 0) { return rc }
    if (rdAsDisposed\<u8>(ser, "255") != (255 as u8)) { return rdFail(424, "D7 u8") }
    rc = rdExpectU8Error(ser, "256", (425 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectU8Error(ser, "-1", (426 as i32))
    if (rc != 0) { return rc }
    if (rdAsDisposed\<i64>(ser, "-9223372036854775808") != ((-9223372036854775807L - (1 as i64)))) {
        return rdFail(427, "D7 i64min")
    }
    if (rdAsDisposed\<u64>(ser, "18446744073709551615") != (18446744073709551615UL as u64)) {
        return rdFail(428, "D7 u64max")
    }
    rc = rdExpectU64Error(ser, "18446744073709551616", (429 as i32))
    if (rc != 0) { return rc }
    if (rdAsDisposed\<double>(ser, "1e3") != (1000.0 as double)) { return rdFail(430, "D7 f64") }
    if (rdAsDisposed\<double>(ser, "3") != (3.0 as double)) { return rdFail(431, "D7 f64 int lexeme") }
    const f32v = rdAsDisposed\<float>(ser, "0.1")
    if (f32v != (0.1 as float)) { return rdFail(432, "D7 f32") }
    rc = rdExpectF32Error(ser, "1e39", (433 as i32))
    if (rc != 0) { return rc }
    if (not rdAsDisposed\<bool>(ser, "true")) { return rdFail(434, "D7 bool") }
    if (rdAsDisposed\<String>(ser, "\"s\"") != "s") { return rdFail(435, "D7 str") }
    rc = rdExpectBoolError(ser, "1", (436 as i32))
    if (rc != 0) { return rc }
    // D8：集合顶层目标（含空容器依声明恢复）。
    const li = rdAsDisposed\<List\<i32>>(ser, "[1,2]")
    if (li.length != (2 as i64)) { return rdFail(437, "D8 list") }
    if ((li.getAtIndex((0 as i64)) if? (0 as i32)) != (1 as i32)) { return rdFail(438, "D8 list0") }
    const ai = rdAsDisposed\<Array\<i64>>(ser, "[1,2]")
    if (ai.length != (2 as i32)) { return rdFail(439, "D8 arr") }
    if ((ai[0] if? (0 as i64)) != (1 as i64)) { return rdFail(440, "D8 arr0") }
    const le = rdAsDisposed\<List\<i32>>(ser, "[]")
    if (le.length != (0 as i64)) { return rdFail(441, "D8 empty list") }
    const ae = rdAsDisposed\<Array\<String>>(ser, "[]")
    if (ae.length != (0 as i32)) { return rdFail(442, "D8 empty arr") }
    // D9：Map 顶层目标——普通对象按字典读（业务键原样，含
    // .rigi.type-identifier 形式；§4.7.4 固定示例）。
    const mi = rdAsDisposed\<Map\<String, i32>>(ser, "{\"a\":1}")
    if ((mi.tryGet("a") if? (0 as i32)) != (1 as i32)) { return rdFail(443, "D9 map") }
    const mi2 = rdAsDisposed\<Map\<String, i32>>(ser,
        "{\".rigi.type-identifier\":1,\"content\":42}")
    if ((mi2.tryGet(".rigi.type-identifier") if? (0 as i32)) != (1 as i32)) { return rdFail(444, "D9 meta-shaped key") }
    if ((mi2.tryGet("content") if? (0 as i32)) != (42 as i32)) { return rdFail(445, "D9 content key") }
    // Map 包装形式同样可经 readAs 恢复。
    const mi3 = rdAsDisposed\<Map\<String, i32>>(ser,
        "{\".rigi.type-identifier\":\"core.collections::Map<.string, .i32>\",\"content\":[{\"key\":\"a\",\"value\":1}]}")
    if ((mi3.tryGet("a") if? (0 as i32)) != (1 as i32)) { return rdFail(446, "D9 wrapper") }
    // 非 String 键目标 Map + 普通对象 → 报错。
    rc = rdExpectIntMapError(ser, "{\"a\":1}", (447 as i32))
    if (rc != 0) { return rc }
    return 0
}

func rdExpectScalarError(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<i32>(s)
    } catch (e: JsonException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "readAs<i32> 未报错：[${text}]")
}

func rdExpectU8Error(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<u8>(s)
    } catch (e: JsonException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "readAs<u8> 未报错：[${text}]")
}

func rdExpectU64Error(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<u64>(s)
    } catch (e: JsonException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "readAs<u64> 未报错：[${text}]")
}

func rdExpectF32Error(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<float>(s)
    } catch (e: JsonException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "readAs<float> 未报错：[${text}]")
}

func rdExpectBoolError(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<bool>(s)
    } catch (e: JsonException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "readAs<bool> 未报错：[${text}]")
}

func rdExpectIntMapError(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<Map\<i32, i32>>(s)
    } catch (e: JsonException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "readAs<Map<i32,i32>> 未报错：[${text}]")
}

// ── D10：枚举目标两形态 ──

func sectionD3(ser: JsonSerializer): i32 {
    var rc: i32 = 0
    // 带载荷 case 对象（互操作形态）——rich 枚举按既有绑定先例全 case
    // 绑定载荷形参，Ok 亦有 errorCode 载荷字段（§4.7.4：是否有载荷按
    // 应序列化的实例字段判断）。
    const okVal = rdAsDisposed\<RdResult>(ser,
        "{\"case\":\"Ok\",\"errorCode\":0}")
    const okWire = okVal:Serializable.toParcel()
    if ((okWire.getMetaElement\<String>("..case") if? "") != "Ok") { return rdFail(451, "D10 Ok case") }
    if ((okWire.getElement\<i32>("errorCode") if? (1 as i32)) != (0 as i32)) { return rdFail(452, "D10 Ok payload") }
    // 带载荷 case 对象（互操作形态）。
    const failedVal = rdAsDisposed\<RdResult>(ser,
        "{\"case\":\"Failed\",\"errorCode\":404}")
    const failedWire = failedVal:Serializable.toParcel()
    if ((failedWire.getMetaElement\<String>("..case") if? "") != "Failed") { return rdFail(453, "D10 Failed case") }
    if ((failedWire.getElement\<i32>("errorCode") if? (0 as i32)) != (404 as i32)) { return rdFail(454, "D10 Failed code") }
    // 保留类型信息形态。
    const typedVal = rdAsDisposed\<RdResult>(ser,
        "{\".rigi.type-identifier\":\"RdResult\",\".rigi.enum-case\":\"Failed\",\"errorCode\":404}")
    const typedWire = typedVal:Serializable.toParcel()
    if ((typedWire.getMetaElement\<String>("..case") if? "") != "Failed") { return rdFail(455, "D10 typed case") }
    if ((typedWire.getElement\<i32>("errorCode") if? (0 as i32)) != (404 as i32)) { return rdFail(456, "D10 typed code") }
    // 未知 case / 缺失 case / 形状错误 / 带载荷 case 的裸字符串。
    rc = rdExpectEnumError(ser, "\"Nope\"", (457 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectEnumError(ser, "{\"case\":\"Nope\",\"errorCode\":1}", (458 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectEnumError(ser, "{\"errorCode\":404}", (459 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectEnumError(ser, "123", (460 as i32))
    if (rc != 0) { return rc }
    rc = rdExpectEnumError(ser, "\"Failed\"", (461 as i32))
    if (rc != 0) { return rc }
    // 无载荷枚举（enum struct）。
    const highVal = rdAsDisposed\<RdMark>(ser, "\"High\"")
    const highWire = highVal:Serializable.toParcel()
    if ((highWire.getMetaElement\<String>("..case") if? "") != "High") { return rdFail(462, "D10 High") }
    const highTyped = rdAsDisposed\<RdMark>(ser,
        "{\".rigi.type-identifier\":\"RdMark\",\".rigi.enum-case\":\"Low\"}")
    const lowWire = highTyped:Serializable.toParcel()
    if ((lowWire.getMetaElement\<String>("..case") if? "") != "Low") { return rdFail(463, "D10 Low typed") }
    return 0
}

func rdExpectEnumError(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = rdStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<RdResult>(s)
    } catch (e: JsonException) {
        threw = true
    } catch (e2: SerializationException) {
        threw = true
    }
    s.dispose()
    if (threw) { return 0 }
    return rdFail(code, "readAs<RdResult> 未报错：[${text}]")
}

// ── D11：Type\<T> 值形态入口 ──
// typefix 启用（协程帧编组 ABI 同构修复）：早前 native 对 getid.var 得
// 来的闭合 Type 值视图存在胖引用 ABI 缺陷——typeOf(x) 经协程切分的泛型
// 方法形参（readAs 的 typeValue）转发后，帧打包把 Type<X> 误装箱成 16B
// 胖值并截断第 0 字段（视图 sheet core::Type<X>），反射双拼写分发不命中
// 即抛「未登记的类型：core::Type<RdLeaf>」；VM 正常。修复
// （CoroutineSplitPass 帧编组 .typeid 源恒等拷贝 + BoxEmitter/
// FieldEmitter 防御守卫）后 native 对拍转绿，本组用例锁定回归。
func rdAsValue\<T with Serializable>(ser: JsonSerializer, tv: Type\<T>,
        text: String): T {
    const s = rdStream(text)
    try {
        return ser.readAs\<T>(tv, s)
    } finally(e) {
        s.dispose()
    }
}

func sectionD4(ser: JsonSerializer): i32 {
    // D11-1：对象目标——typeOf(实例) 实参经协程帧打包（修复点路径），
    // 字段按声明构造。
    const leaf = rdAsValue\<RdLeaf>(ser, typeOf(new RdLeaf(true, "seed")),
        "{\"flag\":false,\"label\":\"vv\"}")
    if (leaf.flag) { return rdFail(471, "D11 leaf flag") }
    if (leaf.label != "vv") { return rdFail(472, "D11 leaf label") }
    // D11-2：嵌套对象（宽度兼容形状 + 内层对象字段）。
    const wide = rdAsValue\<RdWide>(ser,
        typeOf(new RdWide((0 as i64), "", false, null,
            new RdLeaf(false, ""))),
        "{\"v\":8,\"s\":\"wd\",\"b\":false,\"note\":null," +
        "\"child\":{\"flag\":true,\"label\":\"cc\"}}")
    if (wide.v != (8 as i64)) { return rdFail(473, "D11 wide v") }
    if (wide.s != "wd") { return rdFail(474, "D11 wide s") }
    if (wide.b) { return rdFail(475, "D11 wide b") }
    if (wide.note != null) { return rdFail(476, "D11 wide note") }
    if (not wide.child.flag) { return rdFail(477, "D11 wide child flag") }
    if (wide.child.label != "cc") { return rdFail(478, "D11 wide child label") }
    // D11-3：集合目标（元素按声明构造）。
    const li = rdAsValue\<List\<i32>>(ser, typeOf(newList2()), "[1,2,3]")
    if (li.length != (3 as i64)) { return rdFail(479, "D11 list len") }
    // D11-4：枚举目标（保留类型信息形态，§4.7.4 读侧）。
    const failed = rdAsValue\<RdResult>(ser, typeOf(RdResult.Ok((0 as i32))),
        "{\".rigi.type-identifier\":\"RdResult\"," +
        "\".rigi.enum-case\":\"Failed\",\"errorCode\":404}")
    const failedWire = failed:Serializable.toParcel()
    if ((failedWire.getMetaElement\<String>("..case") if? "") != "Failed") {
        return rdFail(480, "D11 enum case")
    }
    // D11-5：负例——字段类型不匹配仍由严格恢复报错（值形态与无参形态
    // 同口径）。
    const sBad = rdStream("{\"flag\":\"notbool\",\"label\":\"x\"}")
    var threw = false
    try {
        const ignored = ser.readAs\<RdLeaf>(typeOf(new RdLeaf(false, "")),
            sBad)
    } catch (e: JsonException) {
        threw = true
    } catch (e2: SerializationException) {
        threw = true
    } finally(x) {
        sBad.dispose()
    }
    if (not threw) { return rdFail(470, "D11 负例未报错") }
    return 0
}

// ── E. deserialize\<T> 走通（基类便利层全链路）──

func sectionE(ser: JsonSerializer): i32 {
    // 宽度兼容形状全字段往返相等。
    const wide = new RdWide((7 as i64), "w", true, null,
        new RdLeaf(false, "c"))
    const out = new MemoryOutputStream()
    ser.serialize\<RdWide>(wide, out)
    const dec = new Utf8Decoder()
    const text = dec.decode(out.toSpan(), true)
    out.dispose()
    const back = ser.deserializeFromString\<RdWide>(text)
    if (back.v != (7 as i64)) { return rdFail(481, "E v") }
    if (back.s != "w") { return rdFail(482, "E s") }
    if (not back.b) { return rdFail(483, "E b") }
    var noteNull = false
    if (back.note == null) { noteNull = true }
    if (not noteNull) { return rdFail(484, "E note") }
    if (back.child.label != "c") { return rdFail(485, "E child") }
    // deserialize 主入口（InputStream 形态）同样走通。
    const sE = rdStream(text)
    var back2: RdWide = back
    try {
        back2 = ser.deserialize\<RdWide>(sE)
    } finally(e) {
        sE.dispose()
    }
    if (back2.v != (7 as i64)) { return rdFail(486, "E v2") }
    return 0
}

pub func main(): i32 {
    const ser = new JsonSerializer()
    var rc: i32 = 0
    rc = sectionA(ser)
    if (rc != 0) { return rc }
    rc = sectionB(ser)
    if (rc != 0) { return rc }
    rc = sectionBorrow(ser)
    if (rc != 0) { return rc }
    rc = sectionC(ser)
    if (rc != 0) { return rc }
    rc = sectionD(ser)
    if (rc != 0) { return rc }
    rc = sectionD2(ser)
    if (rc != 0) { return rc }
    rc = sectionD3(ser)
    if (rc != 0) { return rc }
    rc = sectionD4(ser)
    if (rc != 0) { return rc }
    rc = sectionE(ser)
    if (rc != 0) { return rc }
    Console.println("json-read-ok")
    return 0
}
