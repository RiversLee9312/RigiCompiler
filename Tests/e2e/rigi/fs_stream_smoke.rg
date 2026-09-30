// fs_stream_smoke.rg —— 默认文件流烟测：仅创建、字节往返与已存在错误。
// expect-output: fs-stream-smoke-ok
// expect-exit: 0
import core.fs.*
import core.io.*
import core.math.*
import core.collections.*

func main(): i32 {
    const root = "rigi_fstream_smoke_${new Random().nextU64()}"
    const path = "${root}/data"
    fsMkdir(root, FS_MODE_DIRECTORY)
    const out = File.openWrite(Path.of(path), .CreateNew)
    out.write("AZ".toUtf8Span())
    out.dispose()
    const input = File.openRead(Path.of(path))
    const bytes = core.collections.spanOf\<u8>(2)
    input.readExactly(bytes)
    input.dispose()
    const byteOk = (((bytes[0] if? (0 as u8)) == (65 as u8))
        and ((bytes[1] if? (0 as u8)) == (90 as u8)))
    const wantAE: FileSystemErrorKind = .AlreadyExists
    var conflict = false
    try {
        const rejected = File.openWrite(Path.of(path), .CreateNew)
        rejected.dispose()
    } catch (e: FileSystemException) { conflict = e.kind == wantAE }
    fsUnlink(path)
    fsRmdir(root)
    if (byteOk and conflict) {
        Console.println("fs-stream-smoke-ok")
        return 0
    }
    Console.println("fs-stream-smoke-FAIL")
    return 1
}
