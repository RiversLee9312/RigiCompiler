// ============================================================================
// accept_config_transform.rg —— 施工块 8-1：MVP 应用验收（STDLIB §8）场景 1
// 「配置转换」：内存流中的 JSON → JsonSerializer → Serializable 对象 →
// 修改 → 内存流写回（keepTypeInfo=false 互操作形态）。NativeE2E
// 「验收场景1 配置转换对拍」Case 复用本语料（VM/native 双宿主一致）。
//
//   ① 配置读入：readAs\<AppConfig> 类型引导恢复（嵌套对象 / List\<String> /
//      i32 / String / bool 字段按目标声明直接构造）；输入经借用探测流
//      （读取确实发生、Serializer 不关闭调用者流）。
//   ② 修改两个字段后写回：互操作形态（write 第三参数 keepTypeInfo=false）
//      递归去除类型标识与 Map 包装，输出业务字段文本逐字断言。
//   ③ 坏 JSON：解析错误（截断文档、第二根值）以 JsonException 原样传播
//      （不吞不转译，携带零基字节 offset）。
//   ④ 类型不匹配：字段类型与目标声明不符（数字进 String、bool 进 i32）
//      由读取面/严格恢复报错（JsonException 或 SerializationException）。
//   ⑤ 正确关闭：各层资源由创建方 using/dispose 收尾——Serializer 借用
//      语义的探测（读/写两路：不关闭、不主动 flush），调用方 dispose 后
//      探测计数 +1；seq using 组合 TextReader/内存流两层读回校验。
//   ⑥ 保留全部数据的步骤声明：本场景的 JSON 读取面按 §4.7.6「逐块读取
//      字节后建立完整解析树」整体持有输入快照（readAll 性质），写出面
//      完整构建文本后一次性交给调用者流；两者均为整体内存形态，无流式
//      中间态。
//   ⑦ 不依赖路径或命令行参数 API（全部经内存流，§8 场景 1 约束）。
// expect-output: accept-config values name=rigi-svc version=2 port=5433 tags=2
// expect-output: accept-config close-ok
// expect-output: accept-config bad-json-ok
// expect-output: accept-config type-mismatch-ok
// expect-output: accept-config-ok
// expect-exit: 0
// ============================================================================
import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*

// ── 业务配置类型（嵌套对象 / List / i32 / String / bool）──

@Serializable()
class AcDbConfig {
    pub var host: String
    pub var port: i32
    pub var pooled: bool
    pub init(_ -> host, _ -> port, _ -> pooled)
}

@Serializable()
class AcAppConfig {
    pub var name: String
    pub var version: i32
    pub var debug: bool
    pub var tags: List\<String>
    pub var db: AcDbConfig
    pub init(_ -> name, _ -> version, _ -> debug, _ -> tags, _ -> db)
}

// ── 借用探测流：读侧（serializer_base/json_read 同款计数先例）──

class AcProbeInput : InputStream {
    pub var readCalls: i32 = 0
    pub var disposeRuns: i32 = 0
    priv const data: Array\<u8>
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

// ── 借用探测流：写侧（io_text_streams CollectingOutputStream 同款）──

class AcProbeOutput : OutputStream {
    pub const sink: List\<u8>
    pub var flushCount: i32 = 0
    pub var disposeRuns: i32 = 0

    pub init() {
        sink = new List\<u8>()
    }

    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        var i: i32 = 0
        while (i < count) {
            sink.add((buffer[offset + i] as u8))
            i = (i + 1)
        }
    }

    pub override func flush() {
        ensureOpen()
        flushCount = (flushCount + 1)
    }

    protected override func disposeCore() {
        disposeRuns = (disposeRuns + 1)
    }
}

// ── 助手 ──

// String → UTF-8 字节数组（io_text_streams bytesOf 同款）。
func acBytes(text: String): Array\<u8> {
    const span = text.toUtf8Span()
    const result = arrayOf\<u8>(span.length)
    var i: i32 = 0
    while (i < span.length) {
        result[i] = (span[i] as u8)
        i = (i + 1)
    }
    return result
}

// 写侧探测流收集的字节 → 文本（Utf8Decoder 严格回读；Array → Span 经
// spanOf 拷贝，json_read rdReadBytes 同款）。
func acCollectedText(probe: AcProbeOutput): String {
    const total = (probe.sink.length as i32)
    const span = spanOf\<u8>(total)
    var i: i32 = 0
    while ((i as i64) < probe.sink.length) {
        span[i] = (probe.sink.getAtIndex((i as i64)) as u8)
        i = (i + 1)
    }
    const decoder = new Utf8Decoder()
    return decoder.decode(span, true)
}

// String → 输入流（String 直取 UTF-8 Span，io_text_streams 先例）。
func acStream(text: String): MemoryInputStream {
    return new MemoryInputStream(text.toUtf8Span())
}

// 首败即返。
func acFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

func acExpect(cond: bool, code: i32, msg: String): i32 {
    if (cond) { return 0 }
    return acFail(code, msg)
}

// 坏 JSON：解析错误原样传播（JsonException 携带非负字节 offset）。
func acExpectBadJson(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = acStream(text)
    var threw = false
    var off: i64 = (0 as i64) - (1 as i64)
    try {
        const ignored = ser.readAs\<AcAppConfig>(s)
    } catch (e: JsonException) {
        threw = true
        off = e.offset
    } finally(closer) {
        s.dispose()
    }
    if (not threw) { return acFail(code, "坏 JSON 未抛 JsonException：[${text}]") }
    if (off < (0 as i64)) { return acFail((code + 100), "坏 JSON offset 异常：${off}") }
    return 0
}

// 类型不匹配：目标声明不符 → 读取面 JsonException 或严格恢复
// SerializationException（json_read rdExpectNestError 同口径）。
func acExpectTypeMismatch(ser: JsonSerializer, text: String, code: i32): i32 {
    const s = acStream(text)
    var threw = false
    try {
        const ignored = ser.readAs\<AcAppConfig>(s)
    } catch (e: SerializationException) {
        threw = true
    } catch (e2: JsonException) {
        threw = true
    } finally(closer) {
        s.dispose()
    }
    if (not threw) { return acFail(code, "类型不匹配未报错：[${text}]") }
    return 0
}

pub func main(): i32 {
    const ser = new JsonSerializer()

    // ── ① 配置读入（内存流 → Serializable 对象）──
    const inputJson = (("{\"name\":\"rigi-svc\",\"version\":1,\"debug\":false," +
        "\"tags\":[\"alpha\",\"beta\"],\"db\":{\"host\":\"127.0.0.1\",") +
        "\"port\":5432,\"pooled\":true}}")
    const probe = new AcProbeInput(acBytes(inputJson))
    const config = ser.readAs\<AcAppConfig>(probe)
    // 字段按目标声明构造（嵌套对象与容器强类型触达成功即证）。
    var rc = acExpect(config.name == "rigi-svc", 11, "name")
    if (rc != 0) { return rc }
    rc = acExpect(config.version == 1, 12, "version")
    if (rc != 0) { return rc }
    rc = acExpect(config.debug == false, 13, "debug")
    if (rc != 0) { return rc }
    rc = acExpect(config.tags.length == (2 as i64), 14, "tags.length")
    if (rc != 0) { return rc }
    rc = acExpect((config.tags.getAtIndex((0 as i64)) if? "?") == "alpha", 15, "tags[0]")
    if (rc != 0) { return rc }
    rc = acExpect((config.tags.getAtIndex((1 as i64)) if? "?") == "beta", 16, "tags[1]")
    if (rc != 0) { return rc }
    rc = acExpect(config.db.host == "127.0.0.1", 17, "db.host")
    if (rc != 0) { return rc }
    rc = acExpect(config.db.port == 5432, 18, "db.port")
    if (rc != 0) { return rc }
    rc = acExpect(config.db.pooled == true, 19, "db.pooled")
    if (rc != 0) { return rc }
    // 借用语义（读路）：读取确实发生；Serializer 不关闭调用者流。
    rc = acExpect(probe.readCalls > 0, 110, "probe 未被读取")
    if (rc != 0) { return rc }
    rc = acExpect(probe.disposeRuns == 0, 111, "读路借用流被关闭")
    if (rc != 0) { return rc }
    probe.dispose()
    rc = acExpect(probe.disposeRuns == 1, 112, "调用方 dispose 未生效")
    if (rc != 0) { return rc }

    // ── ② 修改两个字段 → 写回（keepTypeInfo=false 互操作形态）──
    config.version = 2
    config.db.port = 5433
    const outProbe = new AcProbeOutput()
    ser.write(config:Serializable.toParcel(), outProbe, false)
    // 借用语义（写路）：不主动 flush、不关闭调用者流。
    rc = acExpect(outProbe.flushCount == 0, 121, "写路主动 flush")
    if (rc != 0) { return rc }
    rc = acExpect(outProbe.disposeRuns == 0, 122, "写路借用流被关闭")
    if (rc != 0) { return rc }
    const written = acCollectedText(outProbe)
    outProbe.dispose()
    rc = acExpect(outProbe.disposeRuns == 1, 123, "写侧调用方 dispose 未生效")
    if (rc != 0) { return rc }
    // 互操作形态逐字期望：无 .rigi.type-identifier，业务字段按声明序。
    rc = acExpect(written == (("{\"name\":\"rigi-svc\",\"version\":2,\"debug\":false," +
        "\"tags\":[\"alpha\",\"beta\"],\"db\":{\"host\":\"127.0.0.1\",") +
        "\"port\":5433,\"pooled\":true}}"), 124, "写回文本不符：[${written}]")
    if (rc != 0) { return rc }
    Console.println("accept-config values name=${config.name} version=${config.version} port=${config.db.port} tags=${config.tags.length}")

    // ── ⑤ 正确关闭（seq using 各层组合读回校验）──
    seq using(const view = acStream(written)) {
        seq using(const reader = new TextReader(view)) {
            const back = reader.readToEnd()
            if (back != written) {
                rc = acFail(131, "TextReader 读回不符")
            }
        }
    }
    if (rc != 0) { return rc }
    Console.println("accept-config close-ok")

    // ── ③ 坏 JSON：解析错误原样传播 ──
    rc = acExpectBadJson(ser, "{\"name\":\"x\",", 141)
    if (rc != 0) { return rc }
    rc = acExpectBadJson(ser, "{\"name\":\"x\"} {\"name\":\"y\"}", 143)
    if (rc != 0) { return rc }
    rc = acExpectBadJson(ser, "", 145)
    if (rc != 0) { return rc }
    Console.println("accept-config bad-json-ok")

    // ── ④ 类型不匹配：严格恢复报错 ──
    rc = acExpectTypeMismatch(ser,
        "{\"name\":1,\"version\":1,\"debug\":false,\"tags\":[],\"db\":{\"host\":\"h\",\"port\":1,\"pooled\":false}}",
        151)
    if (rc != 0) { return rc }
    rc = acExpectTypeMismatch(ser,
        "{\"name\":\"svc\",\"version\":true,\"debug\":false,\"tags\":[],\"db\":{\"host\":\"h\",\"port\":1,\"pooled\":false}}",
        152)
    if (rc != 0) { return rc }
    rc = acExpectTypeMismatch(ser,
        "{\"name\":\"svc\",\"version\":1,\"debug\":false,\"tags\":[],\"db\":{\"host\":\"h\",\"port\":\"5432\",\"pooled\":false}}",
        153)
    if (rc != 0) { return rc }
    Console.println("accept-config type-mismatch-ok")

    Console.println("accept-config-ok")
    return 0
}
