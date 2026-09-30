// STDLIB §4.4（施工块 3-6）：core.io 文本流适配器（TextReader/TextWriter）
// 语料。底层用 MemoryInputStream/MemoryOutputStream/AutoBuffer + 脚本化
// 分段流组合。
// 覆盖：readChar 逐标量（ASCII/中文/补充平面）与 EOF null；readLine 的
// LF/CRLF/单独 CR、CRLF 跨底层读块切分（chunkLimit=1）、空行、末尾无
// 换行的最后一行、空输入 null；BOM 跳过/无 BOM 正常/仅 BOM 得空；
// readToEnd 全部剩余文本与 maxBytes 超限抛 OutOfBoundException（按结果
// UTF-8 字节数计、不静默截断、负上限非法）；TextWriter 写出字节正确、
// writeLine 默认 LF 与 CRLF 配置、写后不自动 flush（探测流计数）、显式
// flush 计数 +1、dispose 时 Borrowed 不关 host 但 flush、Owned 关 host；
// 严格模式非法 UTF-8 readChar/readLine 抛 TextFormatException（故障化
// 后只允许清理）与替换模式 U+FFFD；Console.println 行输出。
// expect-output: txt-readchar-ok
// expect-output: txt-readline-lf-ok
// expect-output: txt-readline-crlf-ok
// expect-output: txt-readline-cr-ok
// expect-output: txt-readline-chunk-ok
// expect-output: txt-bom-ok
// expect-output: txt-readtoend-ok
// expect-output: txt-readtoend-limit-ok
// expect-output: txt-writer-bytes-ok
// expect-output: txt-writer-nolf-autoflush-ok
// expect-output: txt-writer-flush-ok
// expect-output: txt-writer-crlf-ok
// expect-output: txt-writer-dispose-borrowed-ok
// expect-output: txt-writer-dispose-owned-ok
// expect-output: txt-writer-memory-ok
// expect-output: txt-writer-autobuffer-ok
// expect-output: txt-strict-ok
// expect-output: txt-replacement-ok
// expect-output: txt-reader-closed-ok
// expect-output: console-line-ok
// expect-exit: 0
import core.collections.*
import core.io.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("io_text_streams 断言失败 " + step.toString()) }
}

// 脚本化假输入流：预置字节串，每次最多读 maxChunk 字节（模拟底层短读
// ——CRLF/多字节序列跨底层读块切分的构造手段）。实现契约同
// io_stream_base.rg 同款（状态闸门 → 范围校验 → count==0 语义）
class ChunkedInputStream : InputStream {
    priv const data: Array\<u8>
    priv const maxChunk: i32
    priv var pos: i32

    pub init(bytes: Array\<u8>, chunkLimit: i32) {
        data = bytes
        maxChunk = chunkLimit
        pos = 0
    }

    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) { return 0 }
        var want: i32 = count
        if (want > maxChunk) { want = maxChunk }
        if (want > (data.length - pos)) { want = (data.length - pos) }
        if (want == 0) { return 0 }
        var i: i32 = 0
        while (i < want) {
            buffer[offset + i] = (data[pos + i] as u8)
            i = (i + 1)
        }
        pos = (pos + want)
        return want
    }

    protected override func disposeCore() { }
}

// 收集型假输出流：全部字节收进 List\<u8>；flush/disposeCore 计数
// （write 后不自动 flush 与 dispose 收尾刷新的探测面）
class CollectingOutputStream : OutputStream {
    pub const sink: List\<u8>
    pub var flushCount: i32 = 0
    pub var coreRuns: i32 = 0

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
        coreRuns = (coreRuns + 1)
    }
}

// String 的 UTF-8 字节 → 数组（构造任意输入含非法序列用）
func bytesOf(text: String): Array\<u8> {
    const span = text.toUtf8Span()
    const result = arrayOf\<u8>(span.length)
    var i: i32 = 0
    while (i < span.length) {
        result[i] = (span[i] as u8)
        i = (i + 1)
    }
    return result
}

// 校验收集流内容恰为 text 的 UTF-8 字节
func sinkIsText(co: CollectingOutputStream, text: String): bool {
    const expected = bytesOf(text)
    if (co.sink.length != (expected.length as i64)) { return false }
    var i: i32 = 0
    while (i < expected.length) {
        if (((co.sink.getAtIndex((i as i64)) as u8) == (expected[i] as u8)) == false) { return false }
        i = (i + 1)
    }
    return true
}

// Span 与数组逐字节一致
func spanEquals(src: Span\<u8>, expected: Array\<u8>): bool {
    if (src.length != expected.length) { return false }
    var i: i32 = 0
    while (i < expected.length) {
        if (((src[i] as u8) == (expected[i] as u8)) == false) { return false }
        i = (i + 1)
    }
    return true
}

// ── A. readChar：ASCII/中文/补充平面逐标量，EOF null ──
// "A中😀" = 41 / E4 B8 AD / F0 9F 98 80（1+3+4 字节）
func secA() {
    const mem = new MemoryInputStream("A中😀".toUtf8Span())
    const r = new TextReader(mem)
    const c1 = r.readChar()
    const c2 = r.readChar()
    const c3 = r.readChar()
    require((c1 != null))
    require((c2 != null))
    require((c3 != null))
    require(((c1 if? ' ') == 'A'))
    require(((c2 if? ' ') == '中'))
    require(((c3 if? ' ') == '😀'))
    require((r.readChar() == null))
    r.dispose()
    core.io.Console.println("txt-readchar-ok")
}

// ── B. readLine：LF 行尾 ──
func secB() {
    const r = new TextReader(new MemoryInputStream("ab\ncd\n".toUtf8Span()))
    const l1 = (r.readLine() if? "<null>")
    const l2 = (r.readLine() if? "<null>")
    require((l1 == "ab"))
    require((l2 == "cd"))
    require((r.readLine() == null))
    r.dispose()
    core.io.Console.println("txt-readline-lf-ok")
}

// ── C. readLine：CRLF 行尾 ──
func secC() {
    const r = new TextReader(new MemoryInputStream("ab\r\ncd\r\n".toUtf8Span()))
    require(((r.readLine() if? "?") == "ab"))
    require(((r.readLine() if? "?") == "cd"))
    require((r.readLine() == null))
    r.dispose()
    core.io.Console.println("txt-readline-crlf-ok")
}

// ── D. readLine：单独 CR 行尾（CR 后预读 'c' 非 LF → 推回）──
func secD() {
    const r = new TextReader(new MemoryInputStream("ab\rcd".toUtf8Span()))
    require(((r.readLine() if? "?") == "ab"))
    require(((r.readLine() if? "?") == "cd"))
    require((r.readLine() == null))
    r.dispose()
    core.io.Console.println("txt-readline-cr-ok")
}

// ── E. readLine：CRLF 跨底层读块切分（chunkLimit=1 逐字节交付，
// ── 多字节序列也跨块）+ 空行 + 末尾无换行最后一行 + 空输入 null ──
func secE() {
    const r = new TextReader(new ChunkedInputStream(bytesOf("a中\r\n\nb"), 1))
    require(((r.readLine() if? "?") == "a中"))
    require(((r.readLine() if? "?") == ""))
    require(((r.readLine() if? "?") == "b"))
    require((r.readLine() == null))
    r.dispose()
    // 空输入：没有任何剩余内容 → null
    const rEmpty = new TextReader(new MemoryInputStream(spanOf\<u8>(0)))
    require((rEmpty.readLine() == null))
    rEmpty.dispose()
    core.io.Console.println("txt-readline-chunk-ok")
}

// ── F. BOM：有 BOM 跳过 / 无 BOM 正常 / 仅 BOM 得空 ──
func secF() {
    // EF BB BF + "hi"
    const bom = arrayOf\<u8>(5)
    bom[0] = 239UB
    bom[1] = 187UB
    bom[2] = 191UB
    bom[3] = 104UB  // 'h'
    bom[4] = 105UB  // 'i'
    const r1 = new TextReader(new ChunkedInputStream(bom, 99))
    require((r1.readToEnd() == "hi"))
    r1.dispose()
    const r2 = new TextReader(new MemoryInputStream("hi".toUtf8Span()))
    require((r2.readToEnd() == "hi"))
    r2.dispose()
    const onlyBom = arrayOf\<u8>(3)
    onlyBom[0] = 239UB
    onlyBom[1] = 187UB
    onlyBom[2] = 191UB
    const r3 = new TextReader(new ChunkedInputStream(onlyBom, 99))
    require((r3.readToEnd() == ""))
    require((r3.readLine() == null))
    r3.dispose()
    // BOM 后 readLine 同样跳过
    const r4 = new TextReader(new ChunkedInputStream(bom, 99))
    require(((r4.readLine() if? "?") == "hi"))
    r4.dispose()
    core.io.Console.println("txt-bom-ok")
}

// ── G. readToEnd：全部剩余文本 + maxBytes 超限抛 OutOfBoundException ──
// "ab中" = 1+1+3 = 5 字节
func secG() {
    const r = new TextReader(new MemoryInputStream("ab中".toUtf8Span()))
    require((r.readChar() != null))
    require((r.readToEnd() == "b中"))
    r.dispose()
    // 恰等上限：精确读全（maxBytes 不是截断长度）
    const r2 = new TextReader(new MemoryInputStream("ab中".toUtf8Span()))
    require((r2.readToEnd((5 as i64)) == "ab中"))
    r2.dispose()
    // 超限 1 字节：抛 OutOfBoundException，不静默截断
    const r3 = new TextReader(new MemoryInputStream("ab中".toUtf8Span()))
    var threw = false
    try {
        r3.readToEnd((4 as i64))
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    require(threw)
    // 负上限：上限声明本身非法
    const r4 = new TextReader(new MemoryInputStream("x".toUtf8Span()))
    threw = false
    try {
        r4.readToEnd((0 as i64) - (1 as i64))
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    require(threw)
    core.io.Console.println("txt-readtoend-ok")
    core.io.Console.println("txt-readtoend-limit-ok")
}

// ── H. TextWriter：write 字节正确（AutoBuffer 导出比对）+ 默认无 BOM ──
func secH() {
    const buf = new AutoBuffer()
    const w = new TextWriter(buf.getOutputStream())
    w.write("A中")
    w.flush()
    const view = buf.getInputStream()
    const got = view.readAll()
    require(spanEquals(got, bytesOf("A中")))
    // 默认不生成 BOM：首字节即 'A'
    require(((got[0] as u8) == 65UB))
    view.dispose()
    w.dispose()
    core.io.Console.println("txt-writer-bytes-ok")
}

// ── I. writeLine 默认 LF；写后不因一行自动 flush（探测流计数 0）；
// ── 显式 flush：计数 +1 ──
func secI() {
    const co = new CollectingOutputStream()
    const w = new TextWriter(co)
    w.writeLine("x")
    require((co.flushCount == 0))
    require(sinkIsText(co, "x\n"))
    w.flush()
    require((co.flushCount == 1))
    w.dispose()
    core.io.Console.println("txt-writer-nolf-autoflush-ok")
    core.io.Console.println("txt-writer-flush-ok")
}

// ── J. writeLine CRLF 配置 ──
func secJ() {
    const co = new CollectingOutputStream()
    const w = new TextWriter(co, null, true)
    w.writeLine("y")
    require((co.flushCount == 0))
    require(sinkIsText(co, "y\r\n"))
    w.dispose()
    core.io.Console.println("txt-writer-crlf-ok")
}

// ── K. dispose：Borrowed 不关闭 host 但收尾 flush；Owned 关闭 host ──
func secK() {
    // Borrowed：dispose 后 host 可继续写（未被关闭），且收尾已 flush
    const co = new CollectingOutputStream()
    const w = new TextWriter(co)
    w.write("z")
    require((co.flushCount == 0))
    w.dispose()
    require((co.flushCount == 1))
    require((co.coreRuns == 0))
    co.writeByte(33UB)
    co.dispose()
    // 幂等：二次 dispose 无操作
    w.dispose()
    // Owned：dispose 收尾 flush 并关闭 host
    const co2 = new CollectingOutputStream()
    const w2 = new TextWriter(co2, null, false, .Owned)
    w2.write("z")
    w2.dispose()
    require((co2.flushCount == 1))
    require((co2.coreRuns == 1))
    var rejected = false
    try {
        co2.writeByte(65UB)
    } catch (e: core.IllegalStateException) {
        rejected = true
    }
    require(rejected)
    core.io.Console.println("txt-writer-dispose-borrowed-ok")
    core.io.Console.println("txt-writer-dispose-owned-ok")
}

// ── L. TextWriter → MemoryOutputStream 导出独立副本比对 ──
func secL() {
    const mem = new MemoryOutputStream()
    const w = new TextWriter(mem)
    w.write("hello")
    w.writeLine("中")
    require((mem.toSpan().length == (8 + 1)))
    const copy = mem.toSpan()
    require(spanEquals(copy, bytesOf("hello中\n")))
    w.dispose()
    mem.dispose()
    core.io.Console.println("txt-writer-memory-ok")
}

// ── M. TextReader/TextWriter 经 AutoBuffer 双向 roundtrip ──
func secM() {
    const buf = new AutoBuffer()
    const w = new TextWriter(buf.getOutputStream())
    w.writeLine("行一")
    w.write("tail")
    w.dispose()
    const r = new TextReader(buf.getInputStream())
    require(((r.readLine() if? "?") == "行一"))
    require((r.readToEnd() == "tail"))
    r.dispose()
    core.io.Console.println("txt-writer-autobuffer-ok")
}

// ── N. 严格模式：非法 UTF-8 readChar/readLine 抛 TextFormatException，
// ── 故障化后只允许清理（再读抛 IllegalStateException）──
// 注：读取器按块热切解码——非法字节随其所在块的解码抛出（不必等
// 「读到」该字节）；构造上让首块干净、非法字节落在第二块。
func secN() {
    // readChar 路径：'A' 后紧跟非法首字节（同一首块）→ 首次读取即抛
    const bad = arrayOf\<u8>(2)
    bad[0] = 65UB    // 'A'
    bad[1] = 255UB   // 非法首字节
    const r1 = new TextReader(new ChunkedInputStream(bad, 1))
    var threw = false
    try {
        r1.readChar()
    } catch (e: core.text.TextFormatException) {
        threw = true
    }
    require(threw)
    threw = false
    try {
        r1.readChar()
    } catch (e: core.IllegalStateException) {
        threw = true
    }
    require(threw)
    r1.dispose()
    // readLine 路径：首块 "ab\nC" 干净（首行 "ab" 正常返回），非法
    // 字节 255 落在第二块 "D\xff" → 第二行读取抛出
    const bad2 = arrayOf\<u8>(6)
    bad2[0] = 97UB   // 'a'
    bad2[1] = 98UB   // 'b'
    bad2[2] = 10UB   // '\n'
    bad2[3] = 67UB   // 'C'
    bad2[4] = 68UB   // 'D'
    bad2[5] = 255UB  // 非法首字节
    const r2 = new TextReader(new ChunkedInputStream(bad2, 1))
    require(((r2.readLine() if? "?") == "ab"))
    threw = false
    try {
        r2.readLine()
    } catch (e: core.text.TextFormatException) {
        threw = true
    }
    require(threw)
    r2.dispose()
    core.io.Console.println("txt-strict-ok")
}

// ── O. 替换模式：非法 UTF-8 得 U+FFFD 继续 ──
func secO() {
    const bad = arrayOf\<u8>(2)
    bad[0] = 255UB
    bad[1] = 65UB   // 'A'
    const r = new TextReader(new ChunkedInputStream(bad, 99),
        new core.text.Utf8Decoder(true))
    const c1 = r.readChar()
    const c2 = r.readChar()
    require((c1 != null))
    require((c2 != null))
    require(((c1 if? ' ') == (65533 as char)))
    require(((c2 if? ' ') == 'A'))
    require((r.readChar() == null))
    r.dispose()
    core.io.Console.println("txt-replacement-ok")
}

// ── P. TextReader 关闭后操作抛 IllegalStateException；dispose 幂等 ──
func secP() {
    const r = new TextReader(new MemoryInputStream("q".toUtf8Span()))
    r.dispose()
    r.dispose()
    var threw = false
    try {
        r.readChar()
    } catch (e: core.IllegalStateException) {
        threw = true
    }
    require(threw)
    threw = false
    try {
        r.readLine()
    } catch (e: core.IllegalStateException) {
        threw = true
    }
    require(threw)
    core.io.Console.println("txt-reader-closed-ok")
}

pub func main(): i32 {
    secA()
    secB()
    secC()
    secD()
    secE()
    secF()
    secG()
    secH()
    secI()
    secJ()
    secK()
    secL()
    secM()
    secN()
    secO()
    secP()
    // Console 行输出（内容 + 换行经 expect-output 断言）
    core.io.Console.println("console-line-ok")
    return 0
}
