// ============================================================================
// accept_file_roundtrip.rg —— 施工块 8-2：MVP 应用验收（STDLIB §8）场景 5
// 「文件保存恢复」：用程序中给定的 Path 显式选择模式打开文件流
//（File.openWrite(.CreateOrTruncate) / File.openRead）→ 交给
// JsonSerializer 写入 → 按 using 关闭（持久化 flush + 资源关闭）→ 再打
// 开读回恢复对象（readAs）。NativeE2E「验收场景5 文件保存恢复对拍」
// Case 复用本语料（VM/native 双宿主一致）。
//
//   ① 显式模式打开 + JSON 往返：AfDoc（嵌套 AfMetrics / i32 / String）
//      写出（默认 keepTypeInfo=true 保留类型标识）→ 关闭 → 重开 →
//      readAs 类型引导严格恢复出强类型对象；恢复字段逐一断言。修改对
//      象后同路径 CreateOrTruncate 重写（更短内容，截断旧文本无残留）→
//      再关闭再重开恢复新值——两代往返均逐字精确断言。
//   ② 持久化 flush + 资源关闭（using 逆序清理）：写出经借用探测流
//      （AfProbeOut 委托 FileOutputStream）：内层 probe 按 using 先收尾
//      （disposeRuns==1）、序列化器借用边界不 flush（flushCount==0）、
//      外层文件流此刻仍打开（getPosition 探活）——内层先于外层清理的
//      直接证据；外层 dispose 即持久化刷新点（FileOutputStream 收尾
//      fsFlush = FlushFileBuffers/fsync，§4.5.6「等待全部写入与系统持
//      久化刷新」）：关闭后重开读到逐字完整内容即刷新完成的证据；重复
//      dispose 幂等；dispose 后 write/flush/read 抛 IllegalStateException。
//      文本文件用双层嵌套 using（TextWriter 内层 Borrowed + 文件流外
//      层）：内层收尾后、外层收尾前，getLength 实时查询已见全部字节
//     （writer.flush 已直达 host）——第二处逆序清理证据；外层收尾后才
//      重开读取。
//   ③ 不存在：File.openRead 对缺失文件抛 NotFound 类 FileSystemException。
//   ④ 坏 JSON：写坏内容（截断文档、第二根值）→ 关闭 → 重开 readAs 报
//      JsonException（解析错误原样传播，携非负零基 offset）。
//   ⑤ 类型不匹配：数字进 String 字段、按另一目标类型严格恢复（字段
//      缺失）→ 读取面 JsonException 或严格恢复 SerializationException。
//   ⑥ 文本文件同样由流组合 TextReader/TextWriter 处理：写 4 行（空行、
//      emoji、末行无换行）→ 逐行 readLine 断言 + EOF null + readToEnd
//      整体对照逐字一致。
//   ⑦ 不依赖路径内容便利 API、命令行参数或环境变量 API（打开/读写全
//      经显式模式的文件流；目录名取系统随机源唯一化，结束时整体删除
//      自清理——「文件输出不自动构成原子替换或目录项恢复事务」末段
//      契约由注释声明：本场景只验证单文件内容持久化，不做替换事务）。
// expect-output: accept-file restored title=验收文档 rev=1 count=3 caption=主指标
// expect-output: accept-file reworked title=验收文档 rev=2 count=7
// expect-output: accept-file exact-bytes-ok
// expect-output: accept-file missing-notfound-ok
// expect-output: accept-file bad-json-ok
// expect-output: accept-file type-mismatch-ok
// expect-output: accept-file closed-ops-ok
// expect-output: accept-file text-lines-ok
// expect-output: accept-file-ok
// expect-exit: 0
// ============================================================================
import core.collections.*
import core.fs.*
import core.io.*
import core.math.*
import core.serialization.*
import core.serialization.json.*
import core.text.*

// ── 业务文档类型（嵌套对象 / i32 / String）──

@Serializable()
class AfMetrics {
    pub var count: i32
    pub var caption: String
    pub init(_ -> count, _ -> caption) { }
}

@Serializable()
class AfDoc {
    pub var title: String
    pub var revision: i32
    pub var metrics: AfMetrics
    pub init(_ -> title, _ -> revision, _ -> metrics) { }
}

// ── 借用探测输出流：委托真实文件流并计数（accept_config_transform
//    AcProbeOutput 同款形态；借用语义——disposeCore 不关闭 host）──

class AfProbeOut : OutputStream {
    pub const host: OutputStream
    pub var flushCount: i32 = 0
    pub var disposeRuns: i32 = 0

    pub init(target: OutputStream) {
        host = target
    }

    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        host.write(buffer, offset, count)
    }

    pub override func flush() {
        ensureOpen()
        flushCount = (flushCount + 1)
        host.flush()
    }

    protected override func disposeCore() {
        disposeRuns = (disposeRuns + 1)
    }
}

// ── 助手 ──

// 首败即返（accept_state_roundtrip awFail 同款）。
func afFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

func afExpect(cond: bool, code: i32, msg: String): i32 {
    if (cond) { return 0 }
    return afFail(code, msg)
}

// 显式模式写全部字节（CreateOrTruncate；写毕即关）。
func afWriteBytes(path: Path, data: Span\<u8>): i32 {
    const out = File.openWrite(path, .CreateOrTruncate)
    out.write(data, 0, data.length)
    out.dispose()
    return 0
}

// 期望 FileSystemException kind 命中（fs_copymove expectKind 同款，
// 首败即返形态）。
func afExpectFsKind(want: FileSystemErrorKind, code: i32, msg: String,
        body: core.Action): i32 {
    var ok = false
    try {
        body()
    } catch (e: FileSystemException) {
        if (e.kind == want) { ok = true }
    }
    if (ok) { return 0 }
    return afFail(code, msg)
}

// 坏 JSON（文件内容）：解析错误原样传播，JsonException 携非负 offset；
// 流由本助手关闭（finally 收尾，异常不吞）。
func afExpectBadJson(ser: JsonSerializer, path: Path, code: i32): i32 {
    const inp = File.openRead(path)
    var threw = false
    var off: i64 = (0 as i64) - (1 as i64)
    try {
        const ignored = ser.readAs\<AfDoc>(inp)
    } catch (e: JsonException) {
        threw = true
        off = e.offset
    } finally(closer) {
        inp.dispose()
    }
    if (not threw) { return afFail(code, "坏 JSON 未抛 JsonException") }
    if (off < (0 as i64)) { return afFail((code + 100), "offset=${off} 应非负") }
    return 0
}

// 类型不匹配：严格恢复报错（读取面 JsonException 或 SerializationException，
// accept_config_transform acExpectTypeMismatch 同口径）；流由本助手关闭。
func afExpectTypeMismatch(ser: JsonSerializer, path: Path, code: i32): i32 {
    const inp = File.openRead(path)
    var threw = false
    try {
        const ignored = ser.readAs\<AfMetrics>(inp)
    } catch (e: SerializationException) {
        threw = true
    } catch (e2: JsonException) {
        threw = true
    } finally(closer) {
        inp.dispose()
    }
    if (not threw) { return afFail(code, "类型不匹配未报错") }
    return 0
}

// 打开读回全部字节并严格解码（持久化内容的精确对照面）。
func afReadText(path: Path): String {
    const inp = File.openRead(path)
    const bytes = inp.readAll()
    inp.dispose()
    return new Utf8Decoder().decode(bytes, true)
}

pub func main(): i32 {
    const ser = new JsonSerializer()

    // 临时目录自清理：目录名取系统随机源（fs_copymove 先例），产物全在
    // 唯一目录内，末尾整体删除。
    const rnd = new Random()
    const rootP = Path.of("rigi_acceptfile_${rnd.nextU64()}")
    Directory.createAll(rootP)
    const dataP = rootP.join(Path.of("doc.json"))

    // ── ① 第一代：显式模式打开 → JSON 写入 → using 关闭 → 重开恢复 ──
    const doc = new AfDoc("验收文档", 1, new AfMetrics(3, "主指标"))
    // 逐字期望：类型标识保留（keepTypeInfo 默认）、字段按声明序。
    const text1 = (("{\".rigi.type-identifier\":\"AfDoc\",\"title\":\"验收文档\"," +
        "\"revision\":1,\"metrics\":{\".rigi.type-identifier\":\"AfMetrics\",") +
        "\"count\":3,\"caption\":\"主指标\"}}")
    const len1: i64 = (text1.toUtf8Span().length as i64)
    var rc: i32 = 0
    // File.openWrite 静态返回 OutputStream（§4.4 通用面）；定位面经具体
    // 类型转换触达（file.rg 契约），seq using 绑定声明内完成转换。
    seq using(const fout = (File.openWrite(dataP, .CreateOrTruncate)) as FileOutputStream) {
        const probe = new AfProbeOut(fout)
        ser.write(doc:Serializable.toParcel(), probe)
        // 内层借用层先收尾（外层文件流仍由 using 持有）——using 逆序
        // 清理：probe 收尾后、fout 收尾前，探测计数与定位面可见。
        probe.dispose()
        rc = afExpect(probe.disposeRuns == 1, 11, "内层探测流未先收尾")
        if (rc != 0) { return rc }
        rc = afExpect(probe.flushCount == 0, 12, "写路借用流被主动 flush")
        if (rc != 0) { return rc }
        rc = afExpect(fout.getPosition() == len1, 13,
            "外层文件流位置不符（应仍打开且已收全字节）")
        if (rc != 0) { return rc }
    }
    // 外层 using 收尾 = 持久化刷新点（disposeCore 正常态 fsFlush）。
    // 关闭后重开读到逐字完整内容：持久化刷新完成的证据（flush 落盘 +
    // 资源关闭后内容可见）。
    const readBack1 = afReadText(dataP)
    rc = afExpect(readBack1 == text1, 14, "第一代内容不符：[${readBack1}]")
    if (rc != 0) { return rc }
    const view1 = new MemoryInputStream(readBack1.toUtf8Span())
    const restored = ser.readAs\<AfDoc>(view1)
    view1.dispose()
    rc = afExpect(restored.title == "验收文档", 15, "title")
    if (rc != 0) { return rc }
    rc = afExpect(restored.revision == 1, 16, "revision=${restored.revision}")
    if (rc != 0) { return rc }
    rc = afExpect(restored.metrics.count == 3, 17, "metrics.count")
    if (rc != 0) { return rc }
    rc = afExpect(restored.metrics.caption == "主指标", 18, "metrics.caption")
    if (rc != 0) { return rc }
    Console.println("accept-file restored title=${restored.title} rev=${restored.revision} count=${restored.metrics.count} caption=${restored.metrics.caption}")

    // ── ① 第二代：修改对象 → 同路径 CreateOrTruncate 重写（更短内容，
    //    截断旧文本无残留）→ 关闭 → 重开恢复 ──
    doc.revision = 2
    doc.metrics.count = 7
    const text2 = (("{\".rigi.type-identifier\":\"AfDoc\",\"title\":\"验收文档\"," +
        "\"revision\":2,\"metrics\":{\".rigi.type-identifier\":\"AfMetrics\",") +
        "\"count\":7,\"caption\":\"主指标\"}}")
    seq using(const fout2 = (File.openWrite(dataP, .CreateOrTruncate)) as FileOutputStream) {
        const probe2 = new AfProbeOut(fout2)
        ser.write(doc:Serializable.toParcel(), probe2)
        probe2.dispose()
        rc = afExpect(probe2.disposeRuns == 1, 21, "第二代内层未先收尾")
        if (rc != 0) { return rc }
        rc = afExpect(fout2.getPosition() == (text2.toUtf8Span().length as i64), 22,
            "第二代外层位置不符")
        if (rc != 0) { return rc }
    }
    rc = afExpect(afReadText(dataP) == text2, 23, "第二代内容不符（截断残留？）")
    if (rc != 0) { return rc }
    const in2 = File.openRead(dataP)
    const reworked = ser.readAs\<AfDoc>(in2)
    in2.dispose()
    rc = afExpect(((reworked.revision == 2) and (reworked.metrics.count == 7)) and
        (reworked.metrics.caption == "主指标"), 24, "第二代恢复字段不符")
    if (rc != 0) { return rc }
    Console.println("accept-file reworked title=${reworked.title} rev=${reworked.revision} count=${reworked.metrics.count}")
    // 两代写出内容均与期望逐字一致（精确字节对照）。
    Console.println("accept-file exact-bytes-ok")

    // ── ③ 不存在：openRead 对缺失文件抛 NotFound 类 ──
    const kNF: FileSystemErrorKind = .NotFound
    rc = afExpectFsKind(kNF, 31, "缺失文件未报 NotFound",
        func{() -> File.openRead(rootP.join(Path.of("missing.json")))})
    if (rc != 0) { return rc }
    Console.println("accept-file missing-notfound-ok")

    // ── ④ 坏 JSON：写坏内容 → 关闭 → 重开 readAs 报 JsonException ──
    const badP = rootP.join(Path.of("bad.json"))
    rc = afWriteBytes(badP, "{\"title\":\"残页\",".toUtf8Span())
    if (rc != 0) { return rc }
    rc = afExpectBadJson(ser, badP, 41)
    if (rc != 0) { return rc }
    rc = afWriteBytes(badP, "{\"title\":\"残页\"} {\"title\":\"又一页\"}".toUtf8Span())
    if (rc != 0) { return rc }
    rc = afExpectBadJson(ser, badP, 43)
    if (rc != 0) { return rc }
    Console.println("accept-file bad-json-ok")

    // ── ⑤ 类型不匹配：严格恢复报错 ──
    // 数字进 String 字段（读面/严格恢复拒绝）。
    rc = afWriteBytes(badP, (("{\".rigi.type-identifier\":\"AfDoc\",\"title\":3," +
        "\"revision\":1,\"metrics\":{\".rigi.type-identifier\":\"AfMetrics\",") +
        "\"count\":3,\"caption\":\"主指标\"}}").toUtf8Span())
    if (rc != 0) { return rc }
    rc = afExpectTypeMismatch(ser, badP, 51)
    if (rc != 0) { return rc }
    // 按另一目标类型严格恢复：AfDoc 形状的内容缺 AfMetrics 声明字段。
    rc = afWriteBytes(badP, text2.toUtf8Span())
    if (rc != 0) { return rc }
    rc = afExpectTypeMismatch(ser, badP, 53)
    if (rc != 0) { return rc }
    Console.println("accept-file type-mismatch-ok")

    // ── ② 资源关闭：dispose 幂等 + dispose 后操作抛 IllegalStateException ──
    const closedP = rootP.join(Path.of("closed.bin"))
    const w = File.openWrite(closedP, .CreateOrTruncate)
    const buf = spanOf\<u8>(4)
    w.write(buf, 0, 4)
    w.dispose()
    w.dispose()
    var closedOps = true
    try {
        w.write(buf, 0, 4)
    } catch (e: core.IllegalStateException) {
        // 关闭后写出拒绝
    } catch (other: core.Exception) {
        closedOps = false
    }
    try {
        w.flush()
    } catch (e2: core.IllegalStateException) {
        // 关闭后刷新拒绝
    } catch (other2: core.Exception) {
        closedOps = false
    }
    const r2 = File.openRead(closedP)
    r2.dispose()
    try {
        const ignored = r2.read(buf, 0, 4)
        closedOps = false
    } catch (e3: core.IllegalStateException) {
        // 关闭后读取拒绝
    } catch (other3: core.Exception) {
        closedOps = false
    }
    rc = afExpect(closedOps, 61, "dispose 后操作未全部抛 IllegalStateException")
    if (rc != 0) { return rc }
    Console.println("accept-file closed-ops-ok")

    // ── ⑥ 文本文件：双层嵌套 using 组合 TextWriter/TextReader ──
    const textP = rootP.join(Path.of("lines.txt"))
    const textWhole = "第一行\n\nthird-😀\n末行无换行"
    const textLen: i64 = (textWhole.toUtf8Span().length as i64)
    seq using(const tfout = (File.openWrite(textP, .CreateOrTruncate)) as FileOutputStream) {
        seq using(const tw = new TextWriter(tfout)) {
            tw.writeLine("第一行")
            tw.writeLine("")
            tw.writeLine("third-😀")
            tw.write("末行无换行")
        }
        // 内层（TextWriter，Borrowed）已先收尾：writeLine 不自动刷新，
        // 收尾提交直达 host——此刻外层文件流仍打开且 getLength 实时查询
        // 已见全部字节（第二处逆序清理证据；写入默认不生成 BOM）。
        rc = afExpect(tfout.getLength() == textLen, 71,
            "内层收尾后长度不符（应=${textLen}）")
    }
    if (rc != 0) { return rc }
    seq using(const fr = File.openRead(textP)) {
        seq using(const tr = new TextReader(fr)) {
            // readLine 返回 String?：与文本比较前 if? 收窄（null 时取
            // 默认——4 行均非 null，收窄不影响断言）；EOF 用 null 直比。
            const l1 = (tr.readLine() if? "?")
            const l2 = (tr.readLine() if? "?")
            const l3 = (tr.readLine() if? "?")
            const l4 = (tr.readLine() if? "?")
            const l5 = tr.readLine()
            if ((not ((((l1 == "第一行") and (l2 == "")) and
                    ((l3 == "third-😀") and (l4 == "末行无换行"))))) or
                    (l5 != null)) {
                rc = afFail(72, "逐行内容不符")
            }
            if (rc == 0) {
                const whole = tr.readToEnd()
                // readToEnd 续读剩余（已消费行不再出现）→ EOF 后为空串。
                if (whole != "") {
                    rc = afFail(73, "续读应空：[${whole}]")
                }
            }
        }
    }
    if (rc != 0) { return rc }
    rc = afExpect(afReadText(textP) == textWhole, 74, "整体对照不符")
    if (rc != 0) { return rc }
    Console.println("accept-file text-lines-ok")

    // ── ⑦ 清理（末段契约声明：单文件内容持久化不构成替换事务，目录
    //    项层面按已定 §4.5 契约验收；本场景整体删除自清理）──
    File.delete(dataP)
    File.delete(badP)
    File.delete(closedP)
    File.delete(textP)
    Directory.delete(rootP)
    rc = afExpect(not exists(rootP, false), 81, "临时目录未清理干净")
    if (rc != 0) { return rc }

    Console.println("accept-file-ok")
    return 0
}
