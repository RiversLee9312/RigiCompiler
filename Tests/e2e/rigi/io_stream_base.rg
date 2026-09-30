// STDLIB §4.4（施工块 2-1）：core.io 流核心抽象语料。用户代码继承
// InputStream/OutputStream 抽象类——「可由用户继承实现」正是验收点。
// 覆盖：readExactly 跨多次短读完成；提前 EOF 抛 EndOfStreamException
//（异常携带请求/实际字节数，已读入数据保留，EOF 不置故障流仍可用）；
// count==0 不消费输入、不证明 EOF；offset/count 非法（负、越界、加法
// 溢出）抛 core.OutOfBoundException 且不消费；readByte 到 EOF 返回
// null；writeByte/整 Span write 重载；pipe 计数正确、不关闭任何一端、
// 不自动 flush、目标收到全部字节；dispose 幂等（disposeCore 只跑一次）；
// 故障态与关闭态的 read/write/flush 抛 core.IllegalStateException。
// expect-output: io-readexactly-ok
// expect-output: io-eos-req5-act3
// expect-output: io-eos-partial-ok
// expect-output: io-eos-reusable-ok
// expect-output: io-zero-count-ok
// expect-output: io-range-ok
// expect-output: io-readbyte-ok
// expect-output: io-write-ok
// expect-output: io-pipe-count-7
// expect-output: io-pipe-no-close-flush-ok
// expect-output: io-dispose-idempotent-ok
// expect-output: io-fault-state-ok
// expect-output: io-closed-state-ok
// expect-exit: 0
import core.collections.*
import core.io.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("io_stream_base 断言失败 " + step.toString()) }
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

// 校验 Span 前 n 字节与 expected 相同
func bytesEqual(buf: Span\<u8>, n: i32, expected: Array\<u8>): bool {
    if (expected.length != n) { return false }
    var i: i32 = 0
    while (i < n) {
        if (((buf[i] as u8) == (expected[i] as u8)) == false) { return false }
        i = (i + 1)
    }
    return true
}

// 脚本化假输入流：预置字节串，每次最多读 maxChunk 字节（模拟底层短读）。
// 实现契约：先 ensureOpen() 状态闸门，再复用基类 checkRange 范围校验；
// count == 0 校验通过后返回 0（不消费输入、不证明 EOF）
class ScriptedInputStream : InputStream {
    priv const data: Array\<u8>
    priv const maxChunk: i32
    priv var pos: i32
    // disposeCore 执行计数（验证幂等支架：dispose 多次只跑一次）
    pub var coreRuns: i32 = 0

    pub init(bytes: Array\<u8>, chunkLimit: i32) {
        data = bytes
        maxChunk = chunkLimit
        pos = 0
    }

    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) { return 0 }
        // 实际交付量受三重上限约束：请求数、脚本单次上限、剩余数据量
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

    // 演示故障态入口：实现经基类 protected markFaulted() 进入故障态
    pub func fail() {
        markFaulted()
    }

    // 收尾钩子：仅计数；幂等由基类 dispose 支架保证
    protected override func disposeCore() {
        coreRuns = (coreRuns + 1)
    }
}

// 收集型假输出流：全部字节收进 List\<u8>；flush 与 disposeCore 计数
class CollectingOutputStream : OutputStream {
    // 收集槽公开只读引用（测试校验内容用）
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

pub func main(): i32 {
    // ── A. readExactly 跨多次短读完成：10 字节、单次上限 3 → 4 次 read ──
    const a = new ScriptedInputStream(makeBytes(10), 3)
    const bufA = spanOf\<u8>(10)
    a.readExactly(bufA, 0, 10)
    require(bytesEqual(bufA, 10, makeBytes(10)))
    // 填满后源已耗尽：下一次读取即 EOF（readByte → null）
    require((a.readByte() == null))
    core.io.Console.println("io-readexactly-ok")

    // ── B. 提前 EOF：数据 3 字节、单次上限 2，精确读 5 字节 ──
    const b = new ScriptedInputStream(makeBytes(3), 2)
    const bufB = spanOf\<u8>(5)
    try {
        b.readExactly(bufB, 0, 5)
        require(false)
    } catch (e: EndOfStreamException) {
        // 异常携带本次请求字节数与实际已读字节数（均 i32）
        require((e.requested == 5))
        require((e.actual == 3))
        core.io.Console.println("io-eos-req5-act3")
    }
    // 已读入缓冲区的数据保留（前 3 槽 = 0,1,2），部分结果不算成功
    require(((bufB[0] as u8) == (0 as u8)))
    require(((bufB[1] as u8) == (1 as u8)))
    require(((bufB[2] as u8) == (2 as u8)))
    core.io.Console.println("io-eos-partial-ok")
    // 提前 EOF 不置故障、不回退流位置：继续读立即 EOF（null）
    require((b.readByte() == null))
    core.io.Console.println("io-eos-reusable-ok")

    // ── C. count == 0：read 校验后返回 0；readExactly 校验后直接成功；──
    // ── 都不消费输入、不证明 EOF（下一字节仍是首字节）──
    const c = new ScriptedInputStream(makeBytes(2), 5)
    const bufC = spanOf\<u8>(4)
    require((c.read(bufC, 0, 0) == 0))
    c.readExactly(bufC, 0, 0)
    const first = c.readByte()
    require((first != null))
    require(((first as u8) == (0 as u8)))
    core.io.Console.println("io-zero-count-ok")

    // ── D. 范围校验：负 offset/count、越界、加法溢出 → OutOfBoundException ──
    const d = new ScriptedInputStream(makeBytes(4), 5)
    const bufD = spanOf\<u8>(4)
    try {
        d.readExactly(bufD, (0 - 1), 1)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    try {
        d.readExactly(bufD, 0, (0 - 1))
        require(false)
    } catch (e: core.OutOfBoundException) { }
    // 零长度不绕过范围验证：offset 越界即使 count == 0 也抛
    try {
        d.readExactly(bufD, 5, 0)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    try {
        d.readExactly(bufD, 2, 3)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    // 加法溢出：offset + count = 2 + 2147483647 回绕为负——实现用减法
    // 形式检查，仍须抛 OutOfBoundException
    try {
        d.readExactly(bufD, 2, 2147483647)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    // 假实现三参 read 同样复用基类校验助手
    try {
        d.read(bufD, (0 - 1), 0)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    core.io.Console.println("io-range-ok")
    // 校验失败不消费输入、不置故障：流仍正常完成一次精确读取
    d.readExactly(bufD, 0, 4)
    require(bytesEqual(bufD, 4, makeBytes(4)))

    // ── E. readByte：逐字节读到 EOF 返回 null ──
    const eb = new ScriptedInputStream(makeBytes(2), 5)
    const b1 = eb.readByte()
    require((b1 != null))
    require(((b1 as u8) == (0 as u8)))
    const b2 = eb.readByte()
    require((b2 != null))
    require(((b2 as u8) == (1 as u8)))
    require((eb.readByte() == null))
    core.io.Console.println("io-readbyte-ok")

    // ── F. writeByte 与整 Span write 重载；普通 write 不自动 flush ──
    const f = new CollectingOutputStream()
    f.writeByte((72 as u8))
    f.writeByte((105 as u8))
    const bufF = spanOf\<u8>(3)
    bufF[0] = (1 as u8)
    bufF[1] = (2 as u8)
    bufF[2] = (3 as u8)
    f.write(bufF)
    require((f.sink.length == (5 as i64)))
    require(((f.sink.getAtIndex((0 as i64)) as u8) == (72 as u8)))
    require(((f.sink.getAtIndex((1 as i64)) as u8) == (105 as u8)))
    require(((f.sink.getAtIndex((2 as i64)) as u8) == (1 as u8)))
    require(((f.sink.getAtIndex((3 as i64)) as u8) == (2 as u8)))
    require(((f.sink.getAtIndex((4 as i64)) as u8) == (3 as u8)))
    require((f.flushCount == 0))
    core.io.Console.println("io-write-ok")

    // ── G. pipe：有界缓冲传输到 EOF，计数正确；不关闭任何一端、不 ──
    // ── 自动 flush；目标收到全部字节 ──
    const src = new ScriptedInputStream(makeBytes(7), 2)
    const dst = new CollectingOutputStream()
    const moved = src.pipe(dst)
    require((moved == (7 as i64)))
    require((dst.sink.length == (7 as i64)))
    var i: i32 = 0
    while (i < 7) {
        require(((dst.sink.getAtIndex((i as i64)) as u8) == (i as u8)))
        i = (i + 1)
    }
    // 两端 disposeCore 均未执行（pipe 不关闭），目标未被 flush
    require((src.coreRuns == 0))
    require((dst.coreRuns == 0))
    require((dst.flushCount == 0))
    core.io.Console.println("io-pipe-count-7")
    core.io.Console.println("io-pipe-no-close-flush-ok")

    // ── H. dispose 幂等：二次 dispose 无操作，disposeCore 只跑一次 ──
    const h = new CollectingOutputStream()
    h.dispose()
    require((h.coreRuns == 1))
    h.dispose()
    require((h.coreRuns == 1))
    core.io.Console.println("io-dispose-idempotent-ok")

    // ── I. 故障态：markFaulted 后只允许清理——read/readExactly 拒绝，──
    // ── dispose 仍照常收尾（故障流 dispose 清理自身）──
    const fl = new ScriptedInputStream(makeBytes(4), 5)
    const bufI = spanOf\<u8>(2)
    fl.fail()
    try {
        fl.read(bufI, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        fl.readExactly(bufI, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        fl.readByte()
        require(false)
    } catch (e: core.IllegalStateException) { }
    require((fl.coreRuns == 0))
    fl.dispose()
    require((fl.coreRuns == 1))
    core.io.Console.println("io-fault-state-ok")

    // ── J. 关闭态：read/write/flush 一律抛 IllegalStateException ──
    const cl = new ScriptedInputStream(makeBytes(2), 5)
    const co = new CollectingOutputStream()
    cl.dispose()
    co.dispose()
    const bufJ = spanOf\<u8>(2)
    try {
        cl.read(bufJ, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        cl.read(bufJ)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        cl.readByte()
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        cl.readExactly(bufJ, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        co.write(bufJ, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        co.write(bufJ)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        co.writeByte((1 as u8))
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        co.flush()
        require(false)
    } catch (e: core.IllegalStateException) { }
    // 关闭态的失败操作不产生副作用：flush 计数不变
    require((co.flushCount == 0))
    core.io.Console.println("io-closed-state-ok")
    return 0
}
