// e2e-slow-gate: 文件流五模式、追加与扩容的核心慢例；默认 VM 跳过，按名仍可执行。
// fs_stream_core.rg —— 手动文件流核心对拍；完整能力组合见手动慢例 fs_filestream.rg。
// expect-output: fs-stream-core-ok
// expect-exit: 0
import core.fs.*
import core.io.*
import core.math.*
import core.collections.*

func check(fails: List\<i32>, name: String, ok: bool) {
    if (not ok) {
        Console.println("check-FAIL ${name}")
        fails.add(1)
    }
}

func byteAt(bytes: Span\<u8>, index: i32): u8 {
    return (bytes[index] if? (0 as u8))
}

// 五种模式：已有文件不覆盖，新建冲突后按字节验证原内容。
func modes(root: String, fails: List\<i32>) {
    const a = "${root}/a"
    var out = File.openWrite(Path.of(a), .CreateOrTruncate)
    out.write("ABCD".toUtf8Span())
    out.dispose()
    out = File.openWrite(Path.of(a), .OpenExisting)
    out.write("XY".toUtf8Span())
    out.dispose()
    const wantAE: FileSystemErrorKind = .AlreadyExists
    var conflict = false
    try {
        const rejected = File.openWrite(Path.of(a), .CreateNew)
        rejected.dispose()
    } catch (e: FileSystemException) { conflict = e.kind == wantAE }
    check(fails, "createnew-alreadyexists", conflict)
    const input = File.openRead(Path.of(a))
    const original = core.collections.spanOf\<u8>(4)
    input.readExactly(original)
    input.dispose()
    check(fails, "openexisting-and-createnew-bytes",
        (byteAt(original, 0) == (88 as u8)) and
        ((byteAt(original, 1) == (89 as u8)) and
        ((byteAt(original, 2) == (67 as u8)) and (byteAt(original, 3) == (68 as u8)))))
    const b = "${root}/b"
    out = File.openWrite(Path.of(b), .CreateNew)
    out.write("12".toUtf8Span())
    out.dispose()
    out = File.openWrite(Path.of(b), .CreateOrTruncate)
    out.write("9".toUtf8Span())
    out.dispose()
    check(fails, "createtruncate", fsStat(b).length == 1L)
    out = File.openWrite(Path.of(b), .OpenOrCreate)
    check(fails, "openorcreate-keep", (out as FileOutputStream).getLength() == 1L)
    out.dispose()
    out = File.openWrite(Path.of("${root}/c"), .OpenOrCreate)
    out.write("T".toUtf8Span())
    out.dispose()
    check(fails, "openorcreate-new", fsStat("${root}/c").length == 1L)
}

// 追加句柄旧位置为 2，另一写入者增长到 4 后必须写成 ABZZCD。
func appendAfterWriter(root: String, fails: List\<i32>) {
    const path = "${root}/d"
    const append = File.openWrite(Path.of(path), .Append)
    append.write("AB".toUtf8Span())
    const other = (File.openWrite(Path.of(path), .OpenExisting) as FileOutputStream)
    other.seek(other.getLength())
    other.write("ZZ".toUtf8Span())
    other.dispose()
    append.write("CD".toUtf8Span())
    append.dispose()
    const input = File.openRead(Path.of(path))
    const bytes = core.collections.spanOf\<u8>(6)
    input.readExactly(bytes)
    input.dispose()
    check(fails, "append-at-new-end",
        (byteAt(bytes, 0) == (65 as u8)) and
        ((byteAt(bytes, 1) == (66 as u8)) and
        ((byteAt(bytes, 2) == (90 as u8)) and
        ((byteAt(bytes, 3) == (90 as u8)) and
        ((byteAt(bytes, 4) == (67 as u8)) and (byteAt(bytes, 5) == (68 as u8)))))))
}

func readAfterEof(root: String, fails: List\<i32>) {
    const input = (File.openRead(Path.of("${root}/a")) as FileInputStream)
    const bytes = core.collections.spanOf\<u8>(5)
    input.readExactly(bytes, 0, 4)
    check(fails, "read-position", input.getPosition() == 4L)
    check(fails, "eof-zero", input.read(bytes, 0, 1) == 0)
    const append = File.openWrite(Path.of("${root}/a"), .Append)
    append.write("!".toUtf8Span())
    append.dispose()
    check(fails, "eof-not-sticky", input.read(bytes, 0, 1) == 1)
    check(fails, "eof-byte", byteAt(bytes, 0) == (33 as u8))
    check(fails, "length-live", input.getLength() == 5L)
    input.seek(7L)
    check(fails, "seek-past-eof", input.read(bytes, 0, 1) == 0)
    input.seek(0L)
    check(fails, "seek-position", input.getPosition() == 0L)
    input.dispose()
}

// 截短、增长和越末写后读回所有新增字节，确保不是只改长度。
func resize(path: String, fails: List\<i32>) {
    const out = (File.openWrite(Path.of(path), .CreateOrTruncate) as FileOutputStream)
    out.write("HELLO".toUtf8Span())
    out.seek(0L)
    out.setLength(3L)
    check(fails, "shrink", out.getLength() == 3L)
    out.setLength(8L)
    check(fails, "grow-keeps-cursor",
        (out.getLength() == 8L) and (out.getPosition() == 0L))
    out.seek(10L)
    check(fails, "seek-does-not-grow", out.getLength() == 8L)
    out.write("Q".toUtf8Span())
    check(fails, "gap-write", out.getLength() == 11L)
    out.seek(4L)
    out.setLength(2L)
    check(fails, "shrink-keeps-beyond-cursor",
        (out.getLength() == 2L) and (out.getPosition() == 4L))
    out.write("R".toUtf8Span())
    out.dispose()
    const input = File.openRead(Path.of(path))
    const bytes = core.collections.spanOf\<u8>(5)
    input.readExactly(bytes)
    input.dispose()
    check(fails, "gap-bytes",
        (byteAt(bytes, 0) == (72 as u8)) and
        ((byteAt(bytes, 1) == (69 as u8)) and
        ((byteAt(bytes, 2) == (0 as u8)) and
        ((byteAt(bytes, 3) == (0 as u8)) and (byteAt(bytes, 4) == (82 as u8))))))
    // 单独增长至 11 后读回 3..9 的零，以及越末写 Q，避免后续截短掩盖错误。
    const zeroPath = "${path}.gap"
    const gap = (File.openWrite(Path.of(zeroPath), .CreateNew) as FileOutputStream)
    gap.write("X".toUtf8Span())
    gap.setLength(8L)
    gap.seek(10L)
    gap.write("Q".toUtf8Span())
    gap.dispose()
    const zeroInput = File.openRead(Path.of(zeroPath))
    const grown = core.collections.spanOf\<u8>(11)
    zeroInput.readExactly(grown)
    zeroInput.dispose()
    var allZero = true
    for (i in 1 to 10) {
        if (byteAt(grown, i) != (0 as u8)) { allZero = false }
    }
    check(fails, "grow-gap-zero-bytes", allZero)
    check(fails, "gap-tail", byteAt(grown, 10) == (81 as u8))
    fsUnlink(zeroPath)
}

func errors(root: String, fails: List\<i32>) {
    const wantNF: FileSystemErrorKind = .NotFound
    var noParent = false
    try {
        const rejected = File.openWrite(Path.of("${root}/nodir/x"), .CreateNew)
        rejected.dispose()
    } catch (e: FileSystemException) { noParent = e.kind == wantNF }
    check(fails, "missing-parent-notfound", noParent)
    var noFile = false
    try {
        const rejected = File.openRead(Path.of("${root}/missing"))
        rejected.dispose()
    } catch (e: FileSystemException) { noFile = e.kind == wantNF }
    check(fails, "missing-read-notfound", noFile)
}

func main(): i32 {
    const fails = new List\<i32>()
    const root = "rigi_fstream_core_${new Random().nextU64()}"
    fsMkdir(root, FS_MODE_DIRECTORY)
    modes(root, fails)
    appendAfterWriter(root, fails)
    readAfterEof(root, fails)
    resize("${root}/e", fails)
    errors(root, fails)
    fsUnlink("${root}/a")
    fsUnlink("${root}/b")
    fsUnlink("${root}/c")
    fsUnlink("${root}/d")
    fsUnlink("${root}/e")
    fsRmdir(root)
    if (fails.length == 0L) {
        Console.println("fs-stream-core-ok")
        return 0
    }
    Console.println("fs-stream-core-fails: ${fails.length}")
    return (fails.length as i32)
}
