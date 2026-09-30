// e2e-slow-gate: 完整文件流多能力组合慢例；默认 VM 语料集跳过，按名显式运行或设 RIGI_E2E_SLOW=1 并入。
// ============================================================================
// fs_filestream.rg —— 施工块 7-3（STDLIB §4.5.6 + §4.4，D5）：core.fs
// 文件流端到端语料。FileWriteMode 五模式全表 + File.openRead/openWrite
// + FileInputStream/FileOutputStream/FileAppendStream 双宿主往返。
//   ① 五模式：OpenExisting 不截断（"XY" 覆盖头两字节，尾部保留）/
//      CreateNew 仅新建（新名成功、已有条目 AlreadyExists 且内容不动）/
//      CreateOrTruncate 截断 / OpenOrCreate 不截断与新名创建 / Append
//      不存在则创建、每次写入到达当时末尾（含另一写入者并发增长后仍
//      接其尾部——系统追加机制，非打开时定位一次）；父目录不自动创建
//   ② 读流：readExactly 往返；EOF 返回 0 不粘滞（另一写入者追加后再次
//      读取可见新增，§4.5.6——与目录 reader 固定结束状态不同）；seek
//      末尾之后读即 EOF；负位置 OutOfBoundException；getLength 实时性
//      （外部追加后再查得新值）
//   ③ 输出流：OpenExisting 保留内容；setLength 缩短截断/增长补零/
//      游标保持（含游标已在新末尾之后）；seek 末尾之后写入空隙补零；
//      负长度 OutOfBoundException
//   ④ 追加流类型层：as ISeekableStream 被拒（接口能力由具体返回类型
//      体现，§4.5.6）；getLength/flush 面可用
//   ⑤ flush 持久化（写-flush-close 后重开读回；Windows FlushFileBuffers
//      / Linux fsync 的系统持久化语义由 7-2 原语层保证，本语料按内容
//      为王做间接验证）
//   ⑥ dispose：关闭后 read/write/flush/seek/getPosition/getLength/
//      setLength 抛 IllegalStateException；重复 dispose 无操作；seq
//      using 组合
//   ⑦ §4.4 通用能力组合：pipe → MemoryOutputStream / AutoBuffer 比对；
//      readAll 比对；TextReader/TextWriter 组合读写文本行（UTF-8 行往返）
// 具体类型触达面：openRead/openWrite 的静态类型是 InputStream/
// OutputStream（§4.4 通用面），seek/getPosition/getLength/setLength 经
// 具体类型 as 转换触达；追加流转 ISeekableStream 失败即契约的类型层
// 体现（④）。
// 目录名取系统随机源（Random 无参构造）保证 e2e 并行与 NativeE2E 双宿
// 主各自唯一；产物全在唯一目录内、末尾自清理（根目录先行 mkdir——open
// 不自动创建父目录）。
// expect-output: fs-filestream-ok
// expect-exit: 0
// ============================================================================
import core.fs.*
import core.io.*
import core.math.*
import core.collections.*

func check(fails: List\<i32>, name: String, cond: bool) {
    if (not cond) {
        Console.println("check-FAIL ${name}")
        fails.add(1)
    }
}

func byteAt(bytes: Span\<u8>, offset: i32): u8 {
    return (bytes[offset] if? (0 as u8))
}

func spansEq(a: Span\<u8>, b: Span\<u8>): bool {
    if (a.length != b.length) { return false }
    var i: i32 = 0
    while (i < a.length) {
        if (byteAt(a, i) != byteAt(b, i)) { return false }
        i = (i + 1)
    }
    return true
}

func main(): i32 {
    const fails = new List\<i32>()
    const rnd = new Random()
    const root = "rigi_fstream_probe_${rnd.nextU64()}"
    fsMkdir(root, FS_MODE_DIRECTORY)

    // ===== ① 五模式全表 =====
    // OpenExisting：先 CreateOrTruncate 建立 "ABCD"，再 OpenExisting 从
    // 0 覆盖 "XY"——不截断（长度保持 4，尾部 "CD" 保留）
    const fA = "${root}/a.txt"
    var out = File.openWrite(Path.of(fA), .CreateOrTruncate)
    out.write("ABCD".toUtf8Span())
    out.dispose()
    out = File.openWrite(Path.of(fA), .OpenExisting)
    var co = (out as FileOutputStream)
    check(fails, "openexisting-keep", co.getLength() == 4L)
    check(fails, "openexisting-pos0", co.getPosition() == 0L)
    co.write("XY".toUtf8Span())
    co.dispose()
    check(fails, "openexisting-notrunc", fsStat(fA).length == 4L)

    // CreateNew：新名成功；对已有条目 AlreadyExists 且内容不被覆盖
    const fB = "${root}/b.txt"
    out = File.openWrite(Path.of(fB), .CreateNew)
    out.write("12".toUtf8Span())
    out.dispose()
    var got: FileSystemErrorKind? = null
    try {
        const t = File.openWrite(Path.of(fA), .CreateNew)
        t.dispose()
    } catch (e: FileSystemException) {
        got = e.kind
    }
    const wantAE: FileSystemErrorKind = .AlreadyExists
    check(fails, "createnew-conflict", (got if? wantAE) == wantAE)

    // CreateOrTruncate：已有文件写短内容 → 截断
    out = File.openWrite(Path.of(fB), .CreateOrTruncate)
    out.write("9".toUtf8Span())
    out.dispose()
    check(fails, "createtrunc", fsStat(fB).length == 1L)

    // OpenOrCreate：已有文件不截断（长度 1 保留，头部覆盖写）；新名创建
    out = File.openWrite(Path.of(fB), .OpenOrCreate)
    out.write("8".toUtf8Span())
    out.dispose()
    check(fails, "openorcreate-notrunc", fsStat(fB).length == 1L)
    const fC = "${root}/c.txt"
    out = File.openWrite(Path.of(fC), .OpenOrCreate)
    out.write("T".toUtf8Span())
    out.dispose()
    check(fails, "openorcreate-new", fsStat(fC).length == 1L)

    // Append：不存在则创建；两次写入各接当时末尾；另一写入者并发增长
    // 后仍接其尾部（系统追加机制，§4.5.6——非打开时定位一次）
    const fD = "${root}/d.txt"
    var ap = File.openWrite(Path.of(fD), .Append)
    ap.write("AB".toUtf8Span())
    ap.write("CD".toUtf8Span())
    ap.dispose()
    check(fails, "append-create-len", fsStat(fD).length == 4L)
    const fE = "${root}/e.txt"
    var ap1 = File.openWrite(Path.of(fE), .Append)
    ap1.write("AB".toUtf8Span())
    // 另一写入者：非追加流定位到末尾写 "ZZ"（文件增至 4 字节）
    var other = File.openWrite(Path.of(fE), .OpenExisting)
    const skOther = (other as ISeekableStream)
    const otherLen = (other as FileOutputStream)
    skOther.seek(otherLen.getLength())
    other.write("ZZ".toUtf8Span())
    other.dispose()
    // ap1 的下一次写入必须到达「当时末尾」（4）而非其旧位置（2）
    ap1.write("CD".toUtf8Span())
    ap1.dispose()
    check(fails, "append-at-then-end", fsStat(fE).length == 6L)

    // 父目录不存在不自动创建（§4.5.6）
    got = null
    try {
        const t = File.openWrite(Path.of("${root}/nodir/x.txt"), .CreateNew)
        t.dispose()
    } catch (e: FileSystemException) {
        got = e.kind
    }
    const wantNF: FileSystemErrorKind = .NotFound
    check(fails, "parent-not-created", (got if? wantNF) == wantNF)
    got = null
    try {
        const t = File.openRead(Path.of("${root}/missing.txt"))
        t.dispose()
    } catch (e: FileSystemException) {
        got = e.kind
    }
    check(fails, "openread-missing", (got if? wantNF) == wantNF)

    // ===== ② 读流：往返 / EOF 不粘滞 / 定位 / 实时长度 =====
    // fA 当前内容 "XYCD"
    var inp = (File.openRead(Path.of(fA)) as FileInputStream)
    const buf = core.collections.spanOf\<u8>(16)
    inp.readExactly(buf, 0, 4)
    check(fails, "read-exactly", ((byteAt(buf, 0) == (88 as u8))
        and (byteAt(buf, 3) == (68 as u8))))
    check(fails, "read-pos", inp.getPosition() == 4L)
    check(fails, "read-eof-0", inp.read(buf, 0, 4) == 0)
    // 另一写入者追加 "!" 后，同一读流再次读取可见新增（EOF 不缓存）
    var ap2 = File.openWrite(Path.of(fA), .Append)
    ap2.write("!".toUtf8Span())
    ap2.dispose()
    check(fails, "eof-not-sticky", inp.read(buf, 0, 4) == 1)
    check(fails, "eof-new-data", byteAt(buf, 0) == (33 as u8))
    check(fails, "eof-again-0", inp.read(buf, 0, 4) == 0)
    // getLength 实时性（外部改文件后再查，不缓存）
    check(fails, "getlength-live", inp.getLength() == 5L)
    // seek 末尾之后读即 EOF；负位置范围错误
    inp.seek(7L)
    check(fails, "seek-past-eof-read", inp.read(buf, 0, 4) == 0)
    var oob = false
    try { inp.seek((0L - 1L)) } catch (e: core.OutOfBoundException) {
        oob = true
    }
    check(fails, "seek-negative", oob)
    inp.dispose()

    // ===== ③ 输出流：保留 / setLength / 补零 / 游标保持 =====
    const fG = "${root}/g.txt"
    out = File.openWrite(Path.of(fG), .CreateOrTruncate)
    out.write("HELLO".toUtf8Span())
    out.dispose()
    var o = (File.openWrite(Path.of(fG), .OpenExisting) as FileOutputStream)
    check(fails, "out-keep", o.getLength() == 5L)
    // 缩短截断，游标保持
    o.setLength(3L)
    check(fails, "setlen-shrink", o.getLength() == 3L)
    check(fails, "cursor-kept-shrink", o.getPosition() == 0L)
    // 增长补零，游标保持
    o.setLength(8L)
    check(fails, "setlen-grow", o.getLength() == 8L)
    check(fails, "cursor-kept-grow", o.getPosition() == 0L)
    // 定位到末尾之后（定位不扩容），写入空隙补零
    o.seek(10L)
    check(fails, "seek-past-len", o.getPosition() == 10L)
    check(fails, "seek-no-grow", o.getLength() == 8L)
    o.write("Q".toUtf8Span())
    check(fails, "gap-write-len", o.getLength() == 11L)
    check(fails, "gap-write-pos", o.getPosition() == 11L)
    // 游标已在新末尾之后时 setLength：游标保持不变（§4.5.6 明文）
    o.seek(4L)
    o.setLength(2L)
    check(fails, "setlen-shrink2", o.getLength() == 2L)
    check(fails, "cursor-kept-beyond", o.getPosition() == 4L)
    // 在游标位置（4）写入：空隙 [2,4) 补零
    o.write("R".toUtf8Span())
    check(fails, "write-after-beyond", o.getLength() == 5L)
    // 负长度范围错误（参数校验失败不置故障——流仍可用）
    oob = false
    try { o.setLength((0L - 1L)) } catch (e: core.OutOfBoundException) {
        oob = true
    }
    check(fails, "setlen-negative", oob)
    o.dispose()
    // 重开读回：H E \0 \0 R
    inp = (File.openRead(Path.of(fG)) as FileInputStream)
    check(fails, "gap-readback-len", inp.getLength() == 5L)
    const fin = core.collections.spanOf\<u8>(5)
    inp.readExactly(fin)
    inp.dispose()
    var gapOk = (byteAt(fin, 0) == (72 as u8))
    gapOk = gapOk and (byteAt(fin, 1) == (69 as u8))
    gapOk = gapOk and (byteAt(fin, 2) == (0 as u8))
    gapOk = gapOk and (byteAt(fin, 3) == (0 as u8))
    gapOk = gapOk and (byteAt(fin, 4) == (82 as u8))
    check(fails, "gap-readback", gapOk)

    // ===== ④ 追加流类型层：as ISeekableStream 被拒 =====
    var ap3 = (File.openWrite(Path.of(fD), .Append) as FileAppendStream)
    var castRejected = false
    try {
        const rejected = ap3 as ISeekableStream
        rejected.seek(0L)
    } catch (e: core.CastException) {
        castRejected = true
    }
    check(fails, "append-no-seekable", castRejected)
    // getLength/flush 面可用（§4.5.6：具体文件流提供 getLength）
    check(fails, "append-getlength", ap3.getLength() == 4L)
    ap3.write("!".toUtf8Span())
    ap3.flush()
    ap3.dispose()
    check(fails, "append-after-ext", fsStat(fD).length == 5L)

    // ===== ⑤ flush 持久化（间接验证：写-flush-close 后重开读回；
    // 系统持久化语义由 7-2 原语层 FlushFileBuffers/fsync 保证）=====
    const fH = "${root}/h.txt"
    out = File.openWrite(Path.of(fH), .CreateOrTruncate)
    out.write("PERSIST".toUtf8Span())
    out.flush()
    out.dispose()
    inp = (File.openRead(Path.of(fH)) as FileInputStream)
    check(fails, "flush-persist", inp.readAll().length == 7)
    inp.dispose()

    // ===== ⑥ dispose：关闭后各操作 IllegalStateException + 幂等 =====
    inp = (File.openRead(Path.of(fA)) as FileInputStream)
    inp.dispose()
    inp.dispose()
    var closed = false
    try { inp.read(buf, 0, 4) } catch (e: core.IllegalStateException) {
        closed = true
    }
    check(fails, "closed-read", closed)
    closed = false
    try { inp.seek(0L) } catch (e: core.IllegalStateException) {
        closed = true
    }
    check(fails, "closed-seek", closed)
    closed = false
    try { inp.getPosition() } catch (e: core.IllegalStateException) {
        closed = true
    }
    check(fails, "closed-getpos", closed)
    closed = false
    try { inp.getLength() } catch (e: core.IllegalStateException) {
        closed = true
    }
    check(fails, "closed-getlen", closed)

    out = File.openWrite(Path.of(fB), .OpenExisting)
    co = (out as FileOutputStream)
    co.dispose()
    co.dispose()
    closed = false
    try { co.write(buf, 0, 4) } catch (e: core.IllegalStateException) {
        closed = true
    }
    check(fails, "closed-write", closed)
    closed = false
    try { co.flush() } catch (e: core.IllegalStateException) {
        closed = true
    }
    check(fails, "closed-flush", closed)
    closed = false
    try { co.setLength(0L) } catch (e: core.IllegalStateException) {
        closed = true
    }
    check(fails, "closed-setlen", closed)

    // seq using 组合（正常收尾含持久化刷新）
    seq using(const u = File.openWrite(Path.of(fB), .CreateOrTruncate)) {
        u.write("USING".toUtf8Span())
    }
    check(fails, "using-write", fsStat(fB).length == 5L)

    // ===== ⑦ §4.4 通用能力组合 =====
    // pipe → MemoryOutputStream；readAll；pipe → AutoBuffer
    //（多条消费路径互不隐式回绕——每条前显式 seek(0)）
    inp = (File.openRead(Path.of(fB)) as FileInputStream)
    const mem = new MemoryOutputStream()
    const piped = inp.pipe(mem)
    check(fails, "pipe-count", piped == 5L)
    check(fails, "pipe-content",
        spansEq(mem.toSpan(), "USING".toUtf8Span()))
    mem.dispose()
    inp.seek(0L)
    check(fails, "readall-content",
        spansEq(inp.readAll(), "USING".toUtf8Span()))
    const ab = new AutoBuffer()
    const absink = ab.getOutputStream()
    inp.seek(0L)
    check(fails, "pipe-autobuffer", inp.pipe(absink) == 5L)
    absink.dispose()
    inp.dispose()
    const abview = ab.getInputStream()
    const abbuf = core.collections.spanOf\<u8>(5)
    abview.readExactly(abbuf)
    abview.dispose()
    check(fails, "autobuffer-content",
        spansEq(abbuf, "USING".toUtf8Span()))

    // TextWriter/TextReader 组合读写文本行（UTF-8 行往返；LF 默认）
    const fT = "${root}/t.txt"
    seq using(const tw = File.openWrite(Path.of(fT), .CreateOrTruncate)) {
        seq using(const w = new TextWriter(tw)) {
            w.writeLine("第一行")
            w.writeLine("second")
        }
    }
    var line1: String? = null
    var line2: String? = null
    var line3: String? = "sentinel"
    seq using(const tr = File.openRead(Path.of(fT))) {
        seq using(const r = new TextReader(tr)) {
            line1 = r.readLine()
            line2 = r.readLine()
            line3 = r.readLine()
        }
    }
    check(fails, "text-line1", (line1 if? "") == "第一行")
    check(fails, "text-line2", (line2 if? "") == "second")
    check(fails, "text-line3-null", line3 == null)

    // ===== 清理（唯一目录名保证不残留共享名）=====
    fsUnlink(fA)
    fsUnlink(fB)
    fsUnlink(fC)
    fsUnlink(fD)
    fsUnlink(fE)
    fsUnlink(fG)
    fsUnlink(fH)
    fsUnlink(fT)
    fsRmdir(root)
    if (fails.length == 0L) {
        Console.println("fs-filestream-ok")
        return 0
    }
    Console.println("fs-filestream-fails: ${fails.length}")
    return (fails.length as i32)
}
