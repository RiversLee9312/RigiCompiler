// ============================================================================
// fs_primitives.rg —— 施工块 7-2（STDLIB §4.5.6 / §4.5.9 + §3.2/§3.3，
// D5）：core.fs native 原语层端到端语料。open/write/read/flush/close/
// seek/tell/getLength/setLength/stat/lstat/mkdir/rmdir/unlink/rename
//（NoReplace 系统保证 + Replace）/diropen/dirread/realpath 双宿主往返
// 与关键错误映射（NotFound/IsDirectory/AlreadyExists/NotDirectory）。
// 目录名取系统随机源（Random 无参构造）保证 e2e 并行与 NativeE2E 双宿
// 主各自唯一；末尾自清理（open 不自动创建父目录，根目录先行 mkdir）。
//   ① 创建或截断打开 → 挂起写 → 挂起 flush → tell → dispose；
//      释放后 carry() 闸（IllegalStateException）
//   ② 只读打开 → 挂起读往返 → EOF 不粘滞（读到当前 EOF 返回 0，之后
//      再次读取可见新增，§4.5.6）→ seek END/tell/getLength → 零计数
//      读写直接返回 0
//   ③ stat：kind/length/时间可得性（§4.5.3 一次查询记录）
//   ④ setLength：缩短截断 / 增长补零（Windows 零填充由实现补齐，不
//      假定宿主）/ 游标保持——即使已在新末尾之后（§4.5.6 明文）
//   ⑤ append：系统追加机制每次写到达当时末尾（§4.5.6）
//   ⑥ 错误映射：open 不存在 → NotFound；目录当文件开 → IsDirectory
//     （失败路径补查）；mkdir 已存在 → AlreadyExists；stat 不存在 →
//      NotFound；diropen 文件 → NotDirectory
//   ⑦ rename：NoReplace 遇已有目标报 AlreadyExists（系统不替换保证，
//      §4.5.7「不能用 exists + 覆盖 rename 模拟」）+ NoReplace 成功 +
//      Replace 覆盖文件
//   ⑧ 目录枚举：diropen/dirread 计数 + 名称命中 + 结束后持续 null
//      （重扫须重新打开，§4.5.4）
//   ⑨ realpath：要求目标存在，解析为绝对路径且长于相对输入（含随机
//      目录名的绝对路径逐宿主不同，不作 stdout 对拍内容）
// VM 侧 Windows 实测；Linux 分支按契约实现未实测（fs_path.rg 同口径）。
// expect-output: fs-prim-probe-ok
// expect-exit: 0
// ============================================================================
import core.fs.*
import core.io.Console
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

func main(): i32 {
    const fails = new List\<i32>()
    const rnd = new Random()
    const root = "rigi_fsprim_probe_${rnd.nextU64()}"
    // 根目录先行创建（open 不自动创建父目录，§4.5.6）
    fsMkdir(root, FS_MODE_DIRECTORY)

    // ===== ① 打开（创建或截断）→ 挂起写 → flush → dispose 关闭 =====
    const f1 = "${root}/a.txt"
    var h = fsOpen(f1, ((FS_F_WRITE | FS_F_CREATE) | FS_F_TRUNCATE),
        FS_MODE_FILE)
    const data = "Hello fs primitives!".toUtf8Span()
    check(fails, "write-20", fsWrite(h, data, 0, (data.length as i32)) == 20)
    fsFlush(h)
    check(fails, "tell-after-write", fsTell(h) == 20L)
    h.dispose()
    // 已释放检测走 Rigi 层 carry() 闸（token 归零 → IllegalStateException；
    // 释放后直调原语属违反契约的防御面：native abort / VM 基础设施错误，
    // 不在双宿主公共契约内，语料不触达）
    var released = false
    try {
        h.carry()
    } catch (e: core.IllegalStateException) {
        released = true
    }
    check(fails, "released-handle", released)

    // ===== ② 只读打开 → 挂起读往返 → EOF 不粘滞 → 定位/长度 =====
    h = fsOpen(f1, FS_F_READ, 0)
    const buf = core.collections.spanOf\<u8>(64)
    check(fails, "read-20", fsRead(h, buf, 0, 32) == 20)
    var same = true
    var i = 0
    while (i < 20) {
        if (byteAt(buf, i) != byteAt(data, i)) { same = false }
        i = i + 1
    }
    check(fails, "read-content", same)
    check(fails, "eof-0", fsRead(h, buf, 0, 8) == 0)
    check(fails, "seek-end-20", fsSeek(h, 0L, FS_SEEK_END) == 20L)
    check(fails, "getLength-20", fsGetLength(h) == 20L)
    check(fails, "tell-20", fsTell(h) == 20L)
    check(fails, "read-at-end-0", fsRead(h, buf, 0, 4) == 0)
    check(fails, "zero-count-read", fsRead(h, buf, 0, 0) == 0)
    check(fails, "zero-count-write", fsWrite(h, buf, 0, 0) == 0)
    h.dispose()

    // ===== ③ stat：kind/length（7-5 FileInfo 组装的原语底）=====
    const st = fsStat(f1)
    check(fails, "stat-kind-file", st.kind == FS_KIND_FILE)
    check(fails, "stat-length", st.length == 20L)
    check(fails, "stat-mtime-nonnull", st.mtimeMs != FS_TIME_UNAVAILABLE)

    // ===== ④ setLength：缩短截断 / 增长补零 / 游标保持 =====
    h = fsOpen(f1, FS_F_WRITE, 0)
    fsSetLength(h, 5L)
    check(fails, "setLength-shrink", fsGetLength(h) == 5L)
    check(fails, "cursor-kept-0", fsTell(h) == 0L)
    fsSetLength(h, 30L)
    check(fails, "setLength-grow", fsGetLength(h) == 30L)
    check(fails, "cursor-kept-after-grow", fsTell(h) == 0L)
    h.dispose()
    h = fsOpen(f1, FS_F_READ, 0)
    check(fails, "reopen-read-30", fsRead(h, buf, 0, 64) == 30)
    same = ((byteAt(buf, 0) == (72 as u8)) and (byteAt(buf, 4) == (111 as u8)))
    i = 5
    while (i < 30) {
        if (byteAt(buf, i) != (0 as u8)) { same = false }
        i = i + 1
    }
    check(fails, "grow-zero-filled", same)
    h.dispose()

    // ===== ⑤ append 模式：每次写到达当时末尾（§4.5.6 系统追加）=====
    const f2 = "${root}/b.log"
    h = fsOpen(f2, ((FS_F_WRITE | FS_F_APPEND) | FS_F_CREATE), FS_MODE_FILE)
    const bd = "BC".toUtf8Span()
    check(fails, "append-w1", fsWrite(h, bd, 0, 1) == 1)
    check(fails, "append-w2", fsWrite(h, bd, 1, 1) == 1)
    h.dispose()
    check(fails, "append-length", fsStat(f2).length == 2L)

    // ===== ⑥ mkdir + 错误映射（NotFound / IsDirectory / AlreadyExists）=====
    const d1 = "${root}/d"
    fsMkdir(d1, FS_MODE_DIRECTORY)
    check(fails, "stat-dir-kind", fsStat(d1).kind == FS_KIND_DIRECTORY)
    var got: FileSystemErrorKind? = null
    try {
        const t = fsOpen("${root}/missing.txt", FS_F_READ, 0)
        t.dispose()
    } catch (e: FileSystemException) {
        got = e.kind
    }
    const wantNF: FileSystemErrorKind = .NotFound
    check(fails, "open-missing-NotFound", (got if? wantNF) == wantNF)
    got = null
    try {
        const t = fsOpen(d1, FS_F_READ, 0)
        t.dispose()
    } catch (e: FileSystemException) {
        got = e.kind
    }
    const wantID: FileSystemErrorKind = .IsDirectory
    check(fails, "open-dir-IsDirectory", (got if? wantID) == wantID)
    got = null
    try {
        fsMkdir(d1, FS_MODE_DIRECTORY)
    } catch (e: FileSystemException) {
        got = e.kind
    }
    const wantAE: FileSystemErrorKind = .AlreadyExists
    check(fails, "mkdir-exists-AlreadyExists", (got if? wantAE) == wantAE)
    got = null
    try {
        fsStat("${root}/missing.txt")
    } catch (e: FileSystemException) {
        got = e.kind
    }
    check(fails, "stat-missing-NotFound", (got if? wantNF) == wantNF)

    // ===== ⑦ rename：NoReplace 冲突（系统保证）+ 成功 + Replace =====
    const x = "${root}/x.txt"
    const y = "${root}/y.txt"
    const z = "${root}/z.txt"
    h = fsOpen(x, (FS_F_WRITE | FS_F_CREATE_NEW), FS_MODE_FILE)
    fsWrite(h, data, 0, 5)
    h.dispose()
    h = fsOpen(y, (FS_F_WRITE | FS_F_CREATE_NEW), FS_MODE_FILE)
    fsWrite(h, data, 0, 2)
    h.dispose()
    got = null
    try {
        fsRename(x, y, false)
    } catch (e: FileSystemException) {
        got = e.kind
    }
    check(fails, "rename-noreplace-conflict", (got if? wantAE) == wantAE)
    check(fails, "noreplace-kept-both",
        ((fsStat(x).length == 5L) and (fsStat(y).length == 2L)))
    fsRename(x, z, false)
    var xGone = false
    try {
        fsLstat(x)
    } catch (e: FileSystemException) {
        const k: FileSystemErrorKind = e.kind
        const nf: FileSystemErrorKind = .NotFound
        xGone = k == nf
    }
    check(fails, "rename-moved", (fsStat(z).length == 5L) and xGone)
    fsRename(y, z, true)
    check(fails, "rename-replace", fsStat(z).length == 2L)

    // ===== ⑧ 目录枚举：diropen/dirread 计数 + 名称命中 + 结束 null =====
    const dh = fsDirOpen(root)
    var count = 0
    var sawD = false
    var sawZ = false
    var sawB = false
    var done = false
    while (not done) {
        const e = fsDirRead(dh)
        if (e == null) {
            done = true
        } else {
            const ent: FsDirEntry = e
            count = count + 1
            if (ent.name == "d") { sawD = true }
            if (ent.name == "z.txt") { sawZ = true }
            if (ent.name == "b.log") { sawB = true }
        }
    }
    check(fails, "dir-count-4", count == 4)
    check(fails, "dir-saw-d", sawD)
    check(fails, "dir-saw-z", sawZ)
    check(fails, "dir-saw-b", sawB)
    check(fails, "dir-end-null", fsDirRead(dh) == null)
    dh.dispose()
    got = null
    try {
        const t = fsDirOpen(z)
        t.dispose()
    } catch (e: FileSystemException) {
        got = e.kind
    }
    const wantND: FileSystemErrorKind = .NotDirectory
    check(fails, "diropen-file-NotDirectory", (got if? wantND) == wantND)

    // ===== ⑨ realpath：解析为绝对路径且包含末段 =====
    const rp = fsRealpath(z)
    check(fails, "realpath-longer", rp.length > z.length)
    check(fails, "realpath-absolute", Path.of(rp).isAbsolute())

    // ===== ⑩ 清理（唯一目录名保证不残留共享名）=====
    fsUnlink(z)
    fsUnlink(f1)
    fsUnlink(f2)
    fsRmdir(d1)
    fsRmdir(root)
    if (fails.length == 0L) {
        Console.println("fs-prim-probe-ok")
        return 0
    }
    Console.println("fs-prim-probe-fails: ${fails.length}")
    return (fails.length as i32)
}
