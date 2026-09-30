// STDLIB §4.4（施工块 2-2）：普通内存流 + AutoBuffer + readAll 语料。
// 覆盖：MemoryInputStream 快照语义（构造后改源 Span 不影响读取）、
// read/readExactly、seek/getPosition、seek 到末尾与末尾后读 EOF、负
// 位置抛 core.OutOfBoundException、关闭后操作抛 core.IllegalState
// Exception；MemoryOutputStream write/writeByte、toSpan 独立副本、
// dispose 后 toSpan 抛错；AutoBuffer 输出视图追加 → 既有输入视图继续
// 读到新增数据、输入视图读到当时末尾即 EOF、输出视图 seek 覆盖写与
// 扩展写、越过末尾写补零、两个输入视图独立游标、关闭视图不清空
// AutoBuffer、count 属性；readAll 整段读出、maxBytes 超限抛
// core.OutOfBoundException、空流、配合 AutoBuffer 输入视图；pipe 到
// MemoryOutputStream 再 toSpan 验证内容。
// expect-output: mem-snapshot-ok
// expect-output: mem-seek-ok
// expect-output: mem-seek-negative-ok
// expect-output: mem-closed-ok
// expect-output: mem-out-tospan-ok
// expect-output: io-pipe-mem-ok
// expect-output: ab-append-view-ok
// expect-output: ab-two-readers-ok
// expect-output: ab-overwrite-zero-ok
// expect-output: ab-close-keep-ok
// expect-output: readall-ok
// expect-output: readall-maxbytes-ok
// expect-output: readall-empty-ok
// expect-output: readall-autobuffer-ok
// expect-exit: 0
import core.collections.*
import core.io.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("io_memory_autobuffer 断言失败 " + step.toString()) }
}

// 校验 dst 恰好 n 字节且等于 start, start+1, ...（测试数据均 < 256）
func isSeq(dst: Span\<u8>, n: i32, start: i32): bool {
    if (dst.length != n) { return false }
    var i: i32 = 0
    while (i < n) {
        if (((dst[i] as u8) == ((start + i) as u8)) == false) { return false }
        i = (i + 1)
    }
    return true
}

// 校验 dst 恰好 n 字节且逐字节等于 want[0..n)
func isBytes(dst: Span\<u8>, n: i32, want: Array\<u8>): bool {
    if (want.length != n) { return false }
    var i: i32 = 0
    while (i < n) {
        if (((dst[i] as u8) == (want[i] as u8)) == false) { return false }
        i = (i + 1)
    }
    return true
}

// 构造期望字节串
func bytesOf(values: i32...): Array\<u8> {
    const result = arrayOf\<u8>(values.length)
    var i: i32 = 0
    while (i < values.length) {
        result[i] = (values[i] as u8)
        i = (i + 1)
    }
    return result
}

// AutoBuffer 视图的静态类型是 InputStream/OutputStream，定位成员
//（ISeekableStream）经接口转换访问
func seekTo(target: InputStream, position: i64) {
    const sk = (target as ISeekableStream)
    sk.seek(position)
}

func seekTo(target: OutputStream, position: i64) {
    const sk = (target as ISeekableStream)
    sk.seek(position)
}

func posOf(target: InputStream): i64 {
    const sk = (target as ISeekableStream)
    return sk.getPosition()
}

func posOf(target: OutputStream): i64 {
    const sk = (target as ISeekableStream)
    return sk.getPosition()
}

pub func main(): i32 {
    // ── A. MemoryInputStream 快照语义：构造后改源 Span 不影响流内容 ──
    const srcA = spanOf\<u8>(4)
    srcA[0] = (10 as u8)
    srcA[1] = (20 as u8)
    srcA[2] = (30 as u8)
    srcA[3] = (40 as u8)
    const msA = new MemoryInputStream(srcA)
    srcA[0] = (99 as u8)
    const bufA = spanOf\<u8>(4)
    require((msA.read(bufA, 0, 4) == 4))
    require(isBytes(bufA, 4, bytesOf(10, 20, 30, 40)))
    // 耗尽后再 read 返回 0（EOF）
    require((msA.read(bufA, 0, 4) == 0))
    core.io.Console.println("mem-snapshot-ok")

    // ── B. seek/getPosition/readExactly：位置 i64、零基偏移；seek 到 ──
    // ── 末尾与末尾之后读即 EOF ──
    const srcB = spanOf\<u8>(5)
    var bi: i32 = 0
    while (bi < 5) {
        srcB[bi] = (bi as u8)
        bi = (bi + 1)
    }
    const msB = new MemoryInputStream(srcB)
    const two = spanOf\<u8>(2)
    msB.readExactly(two, 0, 2)
    require(isBytes(two, 2, bytesOf(0, 1)))
    require((msB.getPosition() == (2 as i64)))
    msB.seek((0 as i64))
    const bufB = spanOf\<u8>(4)
    require((msB.read(bufB, 0, 4) == 4))
    require(isBytes(bufB, 4, bytesOf(0, 1, 2, 3)))
    require((msB.getPosition() == (4 as i64)))
    // seek 允许到末尾；其后读即 EOF
    msB.seek((5 as i64))
    require((msB.read(bufB, 0, 1) == 0))
    // seek 允许越过末尾（定位本身不校验上界）；越过末尾后读即 EOF
    msB.seek((8 as i64))
    require((msB.getPosition() == (8 as i64)))
    require((msB.read(bufB, 0, 1) == 0))
    require((msB.readByte() == null))
    // seek 回退后精确读取尾段
    msB.seek((2 as i64))
    msB.readExactly(bufB, 0, 3)
    require(isBytes(bufB, 3, bytesOf(2, 3, 4)))
    core.io.Console.println("mem-seek-ok")

    // ── C. 负位置抛 core.OutOfBoundException（内存流与 AutoBuffer ──
    // ── 输入/输出视图同款）──
    try {
        msB.seek(((0 - 1) as i64))
        require(false)
    } catch (e: core.OutOfBoundException) { }
    const abC = new AutoBuffer()
    const inC = abC.getInputStream()
    const outC = abC.getOutputStream()
    try {
        seekTo(inC, ((0 - 1) as i64))
        require(false)
    } catch (e: core.OutOfBoundException) { }
    try {
        seekTo(outC, ((0 - 1) as i64))
        require(false)
    } catch (e: core.OutOfBoundException) { }
    inC.dispose()
    outC.dispose()
    core.io.Console.println("mem-seek-negative-ok")

    // ── D. 关闭后操作抛 core.IllegalStateException；dispose 幂等 ──
    msB.dispose()
    try {
        msB.read(two, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        msB.readByte()
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        msB.seek((0 as i64))
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        msB.getPosition()
        require(false)
    } catch (e: core.IllegalStateException) { }
    msB.dispose()
    const moD = new MemoryOutputStream()
    moD.dispose()
    try {
        moD.write(two, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        moD.writeByte((1 as u8))
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        moD.flush()
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        moD.toSpan()
        require(false)
    } catch (e: core.IllegalStateException) { }
    core.io.Console.println("mem-closed-ok")

    // ── E. MemoryOutputStream：write/writeByte；toSpan 独立副本（改 ──
    // ── 导出 Span 不影响流、再次 toSpan 仍正确）；flush 无操作后仍可写 ──
    const moE = new MemoryOutputStream()
    moE.writeByte((72 as u8))
    const w3 = spanOf\<u8>(3)
    w3[0] = (1 as u8)
    w3[1] = (2 as u8)
    w3[2] = (3 as u8)
    moE.write(w3)
    const out1 = moE.toSpan()
    require(isBytes(out1, 4, bytesOf(72, 1, 2, 3)))
    // 修改导出副本不影响流内容
    out1[0] = (0 as u8)
    const out2 = moE.toSpan()
    require(isBytes(out2, 4, bytesOf(72, 1, 2, 3)))
    // flush 为无操作成功返回，成功后仍可继续写入
    moE.flush()
    moE.writeByte((250 as u8))
    const out3 = moE.toSpan()
    require(isBytes(out3, 5, bytesOf(72, 1, 2, 3, 250)))
    moE.dispose()
    core.io.Console.println("mem-out-tospan-ok")

    // ── F. pipe 到 MemoryOutputStream 再 toSpan 验证内容；pipe 不 ──
    // ── 关闭任何一端 ──
    const srcF = spanOf\<u8>(6)
    var f: i32 = 0
    while (f < 6) {
        srcF[f] = (f as u8)
        f = (f + 1)
    }
    const msF = new MemoryInputStream(srcF)
    const dstF = new MemoryOutputStream()
    const moved = msF.pipe(dstF)
    require((moved == (6 as i64)))
    const piped = dstF.toSpan()
    require(isBytes(piped, 6, bytesOf(0, 1, 2, 3, 4, 5)))
    // 源到 EOF 但未关闭（readByte → null 表 EOF 而非状态错误）
    require((msF.readByte() == null))
    // 目标未被 pipe 关闭：可继续写
    dstF.writeByte((7 as u8))
    const piped2 = dstF.toSpan()
    require(isBytes(piped2, 7, bytesOf(0, 1, 2, 3, 4, 5, 7)))
    msF.dispose()
    dstF.dispose()
    core.io.Console.println("io-pipe-mem-ok")

    // ── G. AutoBuffer：输出视图追加 → 既有输入视图继续读到新增数据；──
    // ── 输入视图读到当前末尾即 EOF；count 属性 ──
    const abG = new AutoBuffer()
    require((abG.count == (0 as i64)))
    const owG = abG.getOutputStream()
    // 输出视图初始位置在创建当时的末尾（空缓冲即 0）
    require((posOf(owG) == (0 as i64)))
    const w4 = spanOf\<u8>(4)
    w4[0] = (1 as u8)
    w4[1] = (2 as u8)
    w4[2] = (3 as u8)
    w4[3] = (4 as u8)
    owG.write(w4)
    require((abG.count == (4 as i64)))
    const inG = abG.getInputStream()
    // 输入视图初始位置 0
    require((posOf(inG) == (0 as i64)))
    const rG = spanOf\<u8>(2)
    inG.readExactly(rG, 0, 2)
    require(isBytes(rG, 2, bytesOf(1, 2)))
    require((posOf(inG) == (2 as i64)))
    // 输出视图顺序追加（仍在末尾）
    const w2 = spanOf\<u8>(2)
    w2[0] = (5 as u8)
    w2[1] = (6 as u8)
    owG.write(w2)
    require((abG.count == (6 as i64)))
    // 既有输入视图直接读到新增数据（视图非快照）
    require((inG.read(rG, 0, 2) == 2))
    require(isBytes(rG, 2, bytesOf(3, 4)))
    require((inG.read(rG, 0, 2) == 2))
    require(isBytes(rG, 2, bytesOf(5, 6)))
    // 读到当前末尾即 EOF，不等待未来写入
    require((inG.read(rG, 0, 2) == 0))
    require((inG.readByte() == null))
    core.io.Console.println("ab-append-view-ok")

    // ── H. 两个输入视图独立游标 ──
    const inH1 = abG.getInputStream()
    const inH2 = abG.getInputStream()
    const rH = spanOf\<u8>(3)
    inH1.readExactly(rH, 0, 3)
    require(isBytes(rH, 3, bytesOf(1, 2, 3)))
    require((posOf(inH1) == (3 as i64)))
    const rH2 = spanOf\<u8>(2)
    inH2.readExactly(rH2, 0, 2)
    require(isBytes(rH2, 2, bytesOf(1, 2)))
    require((posOf(inH2) == (2 as i64)))
    core.io.Console.println("ab-two-readers-ok")

    // ── I. 输出视图 seek 覆盖写、扩展写、越过末尾写补零 ──
    // 当前内容 6 字节：1,2,3,4,5,6
    seekTo(owG, (1 as i64))
    require((posOf(owG) == (1 as i64)))
    const wOne = spanOf\<u8>(1)
    wOne[0] = (153 as u8)
    owG.write(wOne)
    // 覆盖位置 1，长度不变
    require((abG.count == (6 as i64)))
    // 越过末尾定位（count + 2 = 8）：定位本身不扩容
    seekTo(owG, (abG.count + (2 as i64)))
    require((posOf(owG) == (8 as i64)))
    require((abG.count == (6 as i64)))
    owG.writeByte((119 as u8))
    // 写入时空隙 [6..8) 补零，长度推进到 9
    require((abG.count == (9 as i64)))
    // 扩展边界覆盖写：seek(5) 写 3 字节覆盖 [5..8)
    seekTo(owG, (5 as i64))
    const w3x = spanOf\<u8>(3)
    w3x[0] = (7 as u8)
    w3x[1] = (8 as u8)
    w3x[2] = (9 as u8)
    owG.write(w3x)
    require((abG.count == (9 as i64)))
    // 新输入视图逐字节验证全部内容：1,153,3,4,5,7,8,9,119
    const inI = abG.getInputStream()
    const rI = spanOf\<u8>(9)
    inI.readExactly(rI, 0, 9)
    require(isBytes(rI, 9, bytesOf(1, 153, 3, 4, 5, 7, 8, 9, 119)))
    inI.dispose()
    core.io.Console.println("ab-overwrite-zero-ok")

    // ── J. 关闭视图不清空 AutoBuffer；视图 dispose 后操作抛 ──
    // ── core.IllegalStateException 且 dispose 幂等；再开新视图仍读到 ──
    // ── 全部内容 ──
    owG.dispose()
    try {
        owG.write(rG, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        seekTo(owG, (0 as i64))
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        require((posOf(owG) == (0 as i64)))
    } catch (e: core.IllegalStateException) { }
    try {
        owG.flush()
        require(false)
    } catch (e: core.IllegalStateException) { }
    owG.dispose()
    inG.dispose()
    try {
        inG.read(rG, 0, 1)
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        inG.readByte()
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        seekTo(inG, (0 as i64))
        require(false)
    } catch (e: core.IllegalStateException) { }
    try {
        require((posOf(inG) == (0 as i64)))
    } catch (e: core.IllegalStateException) { }
    inG.dispose()
    inH1.dispose()
    inH2.dispose()
    // 内容保留：count 不变，新视图读全部 9 字节一致
    require((abG.count == (9 as i64)))
    const inJ = abG.getInputStream()
    const rJ = spanOf\<u8>(9)
    inJ.readExactly(rJ, 0, 9)
    require(isBytes(rJ, 9, bytesOf(1, 153, 3, 4, 5, 7, 8, 9, 119)))
    inJ.dispose()
    core.io.Console.println("ab-close-keep-ok")

    // ── K. readAll：整段读出与已知字节一致 ──
    const srcK = spanOf\<u8>(10)
    var k: i32 = 0
    while (k < 10) {
        srcK[k] = (k as u8)
        k = (k + 1)
    }
    const rK = new MemoryInputStream(srcK)
    const all = rK.readAll()
    require(isSeq(all, 10, 0))
    rK.dispose()
    core.io.Console.println("readall-ok")

    // ── L. readAll(maxBytes)：精确读全成功；超限抛 ──
    // ── core.OutOfBoundException（不返回静默截断结果）；上限更宽也成功 ──
    const rL = new MemoryInputStream(srcK)
    const exact = rL.readAll(10)
    require(isSeq(exact, 10, 0))
    rL.dispose()
    const rL2 = new MemoryInputStream(srcK)
    try {
        rL2.readAll(5)
        require(false)
    } catch (e: core.OutOfBoundException) { }
    // 超限抛错后流仍可正常收尾（内存流 dispose 不受异常影响）
    rL2.dispose()
    const rL3 = new MemoryInputStream(srcK)
    const wide = rL3.readAll(1000)
    require(isSeq(wide, 10, 0))
    rL3.dispose()
    core.io.Console.println("readall-maxbytes-ok")

    // ── M. 空流 readAll → 0 字节 Span ──
    const rM = new MemoryInputStream(spanOf\<u8>(0))
    const empty = rM.readAll()
    require((empty.length == 0))
    rM.dispose()
    core.io.Console.println("readall-empty-ok")

    // ── N. readAll 配合 AutoBuffer 输入视图（视图同为 InputStream）──
    const abN2 = new AutoBuffer()
    const owN = abN2.getOutputStream()
    const wN = spanOf\<u8>(3)
    wN[0] = (11 as u8)
    wN[1] = (22 as u8)
    wN[2] = (33 as u8)
    owN.write(wN)
    owN.dispose()
    const viewN = abN2.getInputStream()
    const fromView = viewN.readAll()
    require(isBytes(fromView, 3, bytesOf(11, 22, 33)))
    viewN.dispose()
    core.io.Console.println("readall-autobuffer-ok")
    return 0
}
