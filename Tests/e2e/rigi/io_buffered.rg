// STDLIB §4.4（施工块 2-3）：core.io 缓冲流包装器（BufferedInputStream/
// BufferedOutputStream）语料。文件内自带探测用假流（记录 write/flush/
// dispose 调用次数与字节内容、可编程故障点），参照 io_stream_base.rg 的
// ScriptedInputStream/CollectingOutputStream 模式。
// 覆盖：缓冲写多次小 write 只累积一次 inner 大块写；跨容量自动提交；
// 大块直传路径；flush 提交并逐层刷新、flush 后可继续写；缓冲读预读
//（inner 已消费位置 > 调用者已读位置）与多次小读到 EOF；所有权默认
// Borrowed（dispose 后 inner 未关）与显式 Owned（dispose 后 inner 关闭）；
// 构造失败不接管；dispose 收尾（未 flush 数据提交 + flush 被调 + 二次
// dispose 无重复动作）；关闭态 write/flush 抛 IllegalStateException；
// 故障注入（write/flush/read 失败 → 故障态，后续操作抛
// IllegalStateException，故障态 dispose 不重写数据但仍清理、Owned 时
// 关闭 inner）；缓冲输入 dispose 不回退预读、不再读 inner；与
// MemoryInputStream/MemoryOutputStream 组合端到端；read/write 入口
// 参数校验（范围错误抛 core.OutOfBoundException、count==0 不消费）。
// expect-output: io-bufwrite-accumulate-ok
// expect-output: io-bufwrite-autosubmit-ok
// expect-output: io-bufwrite-bigpass-ok
// expect-output: io-bufread-prefetch-ok
// expect-output: io-ownership-borrowed-ok
// expect-output: io-ownership-owned-ok
// expect-output: io-ownership-ctor-fail-ok
// expect-output: io-dispose-finalize-ok
// expect-output: io-dispose-closed-ok
// expect-output: io-fault-write-ok
// expect-output: io-fault-flush-ok
// expect-output: io-fault-read-ok
// expect-output: io-bufindispose-ok
// expect-output: io-mem-combo-ok
// expect-output: io-range-ok
// expect-exit: 0
import core.collections.*
import core.io.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("io_buffered 断言失败 " + step.toString()) }
}

// 构造 0..n-1 的字节串（脚本化数据）
func makeBytes(n: i32): Array\<u8> {
    const result = arrayOf\<u8>(n)
    var i: i32 = 0
    while (i < n) {
        result[i] = (i as u8)
        i = (i + 1)
    }
    return result
}

// 探测用假输入流：预置字节串 + 单次上限；记录 read 调用次数与已交付
// 总字节数（inner 已消费位置，供预读断言）；failAtRead 指定第 N 次
// read 抛 core.IOException（1 基，0 表不注入）
class ProbeInputStream : InputStream {
    priv const data: Array\<u8>
    priv const maxChunk: i32
    priv var pos: i32
    pub var readCalls: i32 = 0
    pub var failAtRead: i32 = 0
    // disposeCore 执行计数（所有权/幂等断言用）
    pub var coreRuns: i32 = 0

    pub init(bytes: Array\<u8>, chunkLimit: i32) {
        data = bytes
        maxChunk = chunkLimit
        pos = 0
    }

    // inner 已消费位置（预读领先断言、dispose 未回退断言用）
    pub func delivered(): i32 {
        return pos
    }

    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        readCalls = (readCalls + 1)
        if ((failAtRead > 0) and (readCalls == failAtRead)) {
            throw new core.IOException("探测故障：第 ${readCalls} 次 read")
        }
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) { return 0 }
        var want: i32 = count
        if (want > maxChunk) { want = maxChunk }
        if (want > (data.length - pos)) { want = (data.length - pos) }
        if (want <= 0) { return 0 }
        var i: i32 = 0
        while (i < want) {
            buffer[offset + i] = (data[pos + i] as u8)
            i = (i + 1)
        }
        pos = (pos + want)
        return want
    }

    // 收尾钩子：仅计数
    protected override func disposeCore() {
        coreRuns = (coreRuns + 1)
    }
}

// 探测用假输出流：字节收进 List\<u8>；write/flush/disposeCore 计数；
// failAtWrite/failAtFlush 指定第 N 次 write/flush 抛 core.IOException
//（1 基，0 表不注入——故障在真正写/刷新前抛出，数据不落入 sink）
class ProbeOutputStream : OutputStream {
    pub const sink: List\<u8>
    pub var writeCalls: i32 = 0
    pub var flushCalls: i32 = 0
    pub var failAtWrite: i32 = 0
    pub var failAtFlush: i32 = 0
    pub var coreRuns: i32 = 0

    pub init() {
        sink = new List\<u8>()
    }

    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        writeCalls = (writeCalls + 1)
        if ((failAtWrite > 0) and (writeCalls == failAtWrite)) {
            throw new core.IOException("探测故障：第 ${writeCalls} 次 write")
        }
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
        if ((failAtFlush > 0) and (flushCalls == failAtFlush)) {
            throw new core.IOException("探测故障：第 ${flushCalls} 次 flush")
        }
        ensureOpen()
    }

    protected override func disposeCore() {
        coreRuns = (coreRuns + 1)
    }
}

pub func main(): i32 {
    // ── A. 缓冲写：多次小 write 只累积一次 inner 大块写；flush 提交 ──
    // ── 并逐层刷新；flush 后可继续写 ──
    const sinkA = new ProbeOutputStream()
    const outA = new BufferedOutputStream(sinkA, 16)
    outA.writeByte((65 as u8))
    outA.writeByte((66 as u8))
    outA.writeByte((67 as u8))
    outA.writeByte((68 as u8))
    outA.writeByte((69 as u8))
    // 尚未提交：inner 未被触碰
    require((sinkA.writeCalls == 0))
    require((sinkA.sink.length == (0 as i64)))
    require((sinkA.flushCalls == 0))
    outA.flush()
    // 一次大块写收齐全部字节，flush 计数增加（逐层刷新完成）
    require((sinkA.writeCalls == 1))
    require((sinkA.sink.length == (5 as i64)))
    require((sinkA.flushCalls == 1))
    require(((sinkA.sink.getAtIndex((0 as i64)) as u8) == (65 as u8)))
    require(((sinkA.sink.getAtIndex((4 as i64)) as u8) == (69 as u8)))
    // flush 成功后仍可继续写入（不结束流、不关闭流）
    outA.writeByte((70 as u8))
    require((sinkA.sink.length == (5 as i64)))
    outA.flush()
    require((sinkA.sink.length == (6 as i64)))
    require(((sinkA.sink.getAtIndex((5 as i64)) as u8) == (70 as u8)))
    require((sinkA.flushCalls == 2))
    outA.dispose()
    // 正常态 dispose 收尾：空缓冲直接逐层 flush
    require((sinkA.flushCalls == 3))
    require((sinkA.writeCalls == 2))
    core.io.Console.println("io-bufwrite-accumulate-ok")

    // ── A2. 跨容量自动提交：缓冲满时自动把已有内容写给 inner ──
    const sinkA2 = new ProbeOutputStream()
    const outA2 = new BufferedOutputStream(sinkA2, 4)
    outA2.writeByte((1 as u8))
    outA2.writeByte((2 as u8))
    outA2.writeByte((3 as u8))
    outA2.writeByte((4 as u8))
    require((sinkA2.writeCalls == 0))
    // 第 5 字节放不下：先自动提交已有 4 字节
    outA2.writeByte((5 as u8))
    require((sinkA2.writeCalls == 1))
    require((sinkA2.sink.length == (4 as i64)))
    outA2.flush()
    require((sinkA2.sink.length == (5 as i64)))
    var iA2: i32 = 0
    while (iA2 < 5) {
        // 写入值为 1..5
        require(((sinkA2.sink.getAtIndex((iA2 as i64)) as u8) == ((iA2 + 1) as u8)))
        iA2 = (iA2 + 1)
    }
    outA2.dispose()
    core.io.Console.println("io-bufwrite-autosubmit-ok")

    // ── A3. 大块直传：请求量 >= 容量时先提交缓冲、再绕过缓冲直写 ──
    const sinkA3 = new ProbeOutputStream()
    const outA3 = new BufferedOutputStream(sinkA3, 4)
    outA3.writeByte((7 as u8))
    outA3.writeByte((8 as u8))
    const bigA3 = spanOf\<u8>(5)
    bigA3[0] = (9 as u8)
    bigA3[1] = (10 as u8)
    bigA3[2] = (11 as u8)
    bigA3[3] = (12 as u8)
    bigA3[4] = (13 as u8)
    outA3.write(bigA3)
    // 两次 inner 写：先提交 2 字节缓冲，再直传 5 字节；顺序保持
    require((sinkA3.writeCalls == 2))
    require((sinkA3.sink.length == (7 as i64)))
    require(((sinkA3.sink.getAtIndex((0 as i64)) as u8) == (7 as u8)))
    require(((sinkA3.sink.getAtIndex((1 as i64)) as u8) == (8 as u8)))
    require(((sinkA3.sink.getAtIndex((6 as i64)) as u8) == (13 as u8)))
    outA3.dispose()
    core.io.Console.println("io-bufwrite-bigpass-ok")

    // ── B. 缓冲读：inner 预读（inner 已消费位置 > 调用者已读位置）；──
    // ── 多次小 read 内容正确到 EOF ──
    const dataB = makeBytes(20)
    const pinB = new ProbeInputStream(dataB, 64)
    const binB = new BufferedInputStream(pinB, 8)
    const bufB = spanOf\<u8>(4)
    // 首次读 1 字节：预读使 inner 已消费 8 字节（领先调用者 1 字节）
    require((binB.read(bufB, 0, 1) == 1))
    require(((bufB[0] as u8) == (0 as u8)))
    require((pinB.delivered() == 8))
    require((pinB.readCalls == 1))
    // 多次小读消费剩余 19 字节，内容逐字节正确
    var consumedB: i32 = 1
    var guardB: i32 = 0
    while ((consumedB < 20) and (guardB < 100)) {
        const nB = binB.read(bufB, 0, 4)
        if (nB == 0) { break }
        var jB: i32 = 0
        while (jB < nB) {
            require(((bufB[jB] as u8) == (dataB[consumedB + jB] as u8)))
            jB = (jB + 1)
        }
        consumedB = (consumedB + nB)
        guardB = (guardB + 1)
    }
    require((consumedB == 20))
    require((pinB.delivered() == 20))
    // 全部消费后：3 次预读补读已耗尽 inner（8+8+4）
    require((pinB.readCalls == 3))
    // EOF：缓冲空 + inner 返回 0 → 返回 0（EOF 探测用掉第 4 次 inner.read）
    require((binB.read(bufB, 0, 1) == 0))
    require((pinB.readCalls == 4))
    require((binB.read(bufB, 0, 1) == 0))
    binB.dispose()
    pinB.dispose()
    core.io.Console.println("io-bufread-prefetch-ok")

    // ── C. 所有权：默认 Borrowed——dispose 后 inner 未关闭 ──
    const pinC1 = new ProbeInputStream(makeBytes(4), 8)
    const binC1 = new BufferedInputStream(pinC1, 8)
    binC1.dispose()
    require((pinC1.coreRuns == 0))
    const pC1 = new ProbeOutputStream()
    const outC1 = new BufferedOutputStream(pC1, 8)
    outC1.dispose()
    require((pC1.coreRuns == 0))
    require((pC1.flushCalls == 1))
    core.io.Console.println("io-ownership-borrowed-ok")

    // ── C2. 所有权：显式 .Owned——dispose 后 inner 被关闭 ──
    const pinC2 = new ProbeInputStream(makeBytes(4), 8)
    const binC2 = new BufferedInputStream(pinC2, 8, .Owned)
    binC2.dispose()
    require((pinC2.coreRuns == 1))
    const pC2 = new ProbeOutputStream()
    const outC2 = new BufferedOutputStream(pC2, 8, .Owned)
    outC2.dispose()
    require((pC2.coreRuns == 1))
    core.io.Console.println("io-ownership-owned-ok")

    // ── C3. 构造失败不接管：capacity 非法抛错后 inner 仍由调用者负责 ──
    const pC3 = new ProbeOutputStream()
    try {
        new BufferedOutputStream(pC3, 0)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    try {
        new BufferedOutputStream(pC3, (0 - 4), .Owned)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    try {
        new BufferedInputStream(new ProbeInputStream(makeBytes(1), 8), 0)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    // 构造失败未接管：inner 未被关闭、仍可正常使用，由调用者自行关闭
    require((pC3.coreRuns == 0))
    pC3.writeByte((9 as u8))
    require((pC3.sink.length == (1 as i64)))
    pC3.dispose()
    require((pC3.coreRuns == 1))
    core.io.Console.println("io-ownership-ctor-fail-ok")

    // ── D. dispose 收尾：未 flush 的缓冲数据在 dispose 中全部提交 ──
    // ── 且 flush 被调；二次 dispose 无重复动作 ──
    const sinkD = new ProbeOutputStream()
    const outD = new BufferedOutputStream(sinkD, 8)
    outD.writeByte((1 as u8))
    outD.writeByte((2 as u8))
    outD.writeByte((3 as u8))
    outD.writeByte((4 as u8))
    outD.writeByte((5 as u8))
    require((sinkD.writeCalls == 0))
    outD.dispose()
    require((sinkD.sink.length == (5 as i64)))
    require(((sinkD.sink.getAtIndex((0 as i64)) as u8) == (1 as u8)))
    require(((sinkD.sink.getAtIndex((4 as i64)) as u8) == (5 as u8)))
    require((sinkD.flushCalls == 1))
    require((sinkD.writeCalls == 1))
    require((sinkD.coreRuns == 0))
    // 二次 dispose 无操作：无重复写出、无重复刷新、无重复关闭
    outD.dispose()
    require((sinkD.writeCalls == 1))
    require((sinkD.flushCalls == 1))
    require((sinkD.coreRuns == 0))
    core.io.Console.println("io-dispose-finalize-ok")

    // ── D2. 关闭态：dispose 后 write/flush 抛 IllegalStateException ──
    const bufD2 = spanOf\<u8>(2)
    try {
        outD.write(bufD2, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        outD.writeByte((1 as u8))
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        outD.flush()
        require(false)
    } catch (e: core.IllegalStateException) { }
    require((sinkD.flushCalls == 1))
    core.io.Console.println("io-dispose-closed-ok")

    // ── E. 故障注入：inner 在第 N 次 write 抛错 → 包装器故障态 ──
    const pE = new ProbeOutputStream()
    pE.failAtWrite = 1
    const outE = new BufferedOutputStream(pE, 4, .Owned)
    outE.writeByte((1 as u8))
    outE.writeByte((2 as u8))
    outE.writeByte((3 as u8))
    outE.writeByte((4 as u8))
    require((pE.writeCalls == 0))
    // 第 5 字节触发自动提交 → inner 第 1 次 write 抛 IOException
    try {
        outE.writeByte((5 as u8))
        require(false)
    } catch (e: core.IOException) { }
    require((pE.sink.length == (0 as i64)))
    // 故障态：后续 write/flush 只允许清理——抛 IllegalStateException
    try {
        outE.writeByte((9 as u8))
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        outE.flush()
        require(false)
    } catch (e: core.IllegalStateException) { }
    require((pE.flushCalls == 0))
    // 故障态 dispose：不重写可能已部分提交的数据（sink 仍空），
    // 但仍清理自身并关闭 Owned 的 inner；二次 dispose 无重复
    outE.dispose()
    require((pE.sink.length == (0 as i64)))
    require((pE.writeCalls == 1))
    require((pE.coreRuns == 1))
    outE.dispose()
    require((pE.coreRuns == 1))
    core.io.Console.println("io-fault-write-ok")

    // ── E2. 故障注入：inner.flush 抛错 → 故障态；故障 dispose 不再 ──
    // ── 重写也不再刷新 ──
    const pE2 = new ProbeOutputStream()
    pE2.failAtFlush = 1
    const outE2 = new BufferedOutputStream(pE2, 8)
    outE2.writeByte((42 as u8))
    try {
        outE2.flush()
        require(false)
    } catch (e: core.IOException) { }
    // 本层缓冲已提交成功（write 完成），flush 失败置故障
    require((pE2.sink.length == (1 as i64)))
    require((pE2.flushCalls == 1))
    try {
        outE2.flush()
        require(false)
    } catch (e: core.IllegalStateException) { }
    // 故障态 dispose：不重写数据、不再刷新，仍清理自身；Borrowed 不关 inner
    outE2.dispose()
    require((pE2.sink.length == (1 as i64)))
    require((pE2.writeCalls == 1))
    require((pE2.flushCalls == 1))
    require((pE2.coreRuns == 0))
    core.io.Console.println("io-fault-flush-ok")

    // ── E3. 故障注入：inner.read 抛错 → 包装器故障态；dispose 仍执行 ──
    const pinE3 = new ProbeInputStream(makeBytes(20), 64)
    pinE3.failAtRead = 2
    const binE3 = new BufferedInputStream(pinE3, 8, .Owned)
    const bufE3 = spanOf\<u8>(8)
    // 第 1 次 inner.read 正常（预读 8 字节并整段消费）
    require((binE3.read(bufE3, 0, 8) == 8))
    // 缓冲空，补读触发第 2 次 inner.read → IOException → 故障态
    try {
        binE3.read(bufE3, 0, 1)
        require(false)
    } catch (e: core.IOException) { }
    try {
        binE3.read(bufE3, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    // 故障态 dispose 仍清理（Owned 关闭 inner）
    binE3.dispose()
    require((pinE3.coreRuns == 1))
    core.io.Console.println("io-fault-read-ok")

    // ── F. 缓冲输入 dispose：不回退预读位置、不再读 inner ──
    const pinF = new ProbeInputStream(makeBytes(10), 64)
    const binF = new BufferedInputStream(pinF, 8)
    const bufF = spanOf\<u8>(2)
    require((binF.read(bufF, 0, 2) == 2))
    require((pinF.readCalls == 1))
    require((pinF.delivered() == 8))
    binF.dispose()
    // dispose 后：inner 的 read 调用次数不变（未再读）、位置未回退、
    // 未关闭（Borrowed）
    require((pinF.readCalls == 1))
    require((pinF.delivered() == 8))
    require((pinF.coreRuns == 0))
    core.io.Console.println("io-bufindispose-ok")

    // ── G. 与 MemoryInputStream/MemoryOutputStream 组合端到端 ──
    const memG = new MemoryOutputStream()
    const boutG = new BufferedOutputStream(memG, 4)
    var kG: i32 = 0
    while (kG < 10) {
        boutG.writeByte((kG as u8))
        kG = (kG + 1)
    }
    // dispose 收尾：剩余缓冲提交 + 逐层 flush
    boutG.dispose()
    const snapG = memG.toSpan()
    require((snapG.length == 10))
    const minG = new MemoryInputStream(snapG)
    const binG = new BufferedInputStream(minG, 3)
    const dstG = spanOf\<u8>(10)
    binG.readExactly(dstG, 0, 10)
    var jG: i32 = 0
    while (jG < 10) {
        require(((dstG[jG] as u8) == (jG as u8)))
        jG = (jG + 1)
    }
    require((binG.readByte() == null))
    binG.dispose()
    minG.dispose()
    memG.dispose()
    core.io.Console.println("io-mem-combo-ok")

    // ── H. 入口参数校验：范围错误抛 OutOfBoundException 且不消费；──
    // ── count == 0 校验后直接成功、不触碰 inner ──
    const pinH = new ProbeInputStream(makeBytes(4), 64)
    const binH = new BufferedInputStream(pinH, 8)
    const bufH = spanOf\<u8>(4)
    try {
        binH.read(bufH, (0 - 1), 1)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    try {
        binH.read(bufH, 0, (0 - 1))
        require(false)
    } catch (e: core.OutOfBoundException) { }
    try {
        binH.read(bufH, 2, 3)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    require((pinH.readCalls == 0))
    require((binH.read(bufH, 0, 0) == 0))
    require((pinH.readCalls == 0))
    binH.dispose()
    const poutH = new ProbeOutputStream()
    const boutH = new BufferedOutputStream(poutH, 8)
    try {
        boutH.write(bufH, (0 - 1), 1)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    try {
        boutH.write(bufH, 2, 3)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    require((poutH.writeCalls == 0))
    boutH.write(bufH, 0, 0)
    require((poutH.writeCalls == 0))
    boutH.dispose()
    core.io.Console.println("io-range-ok")
    return 0
}
