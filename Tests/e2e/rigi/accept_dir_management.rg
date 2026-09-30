// ============================================================================
// accept_dir_management.rg —— 施工块 8-2：MVP 应用验收（STDLIB §8）场景 7
// 「目录与文件管理」：在显式给定目录（程序内组装的相对唯一目录，产物
// 工作目录口径同 fs_copymove.rg 先例）内创建子目录（Directory.create/
// createAll）及临时文件（File.createTemporary/Directory.createTemporary）
// → 用 DirectoryReader 遍历（DirectoryEntry 的 name/path/kindHint 断言）
// → 按确定的复制/移动/删除规则管理文件。NativeE2E「验收场景7 目录与
// 文件管理对拍」Case 复用本语料（VM/native 双宿主一致）。
//
//   ① 创建：末级 create（父级须存在）、createAll 逐级补建、临时文件
//     （原子创建返回已打开输出流、名称前缀命中、写入后关闭再读回）、
//      临时目录（前缀命中、kind 为目录）。
//   ② 遍历：恰好 4 条目（3 文件 + 1 子目录）逐一读出、无 `.`/`..`、
//      name 命中集合、条目 path 绝对且末段=条目名称、kindHint 文件/
//      目录正确或 null 容忍（Linux d_type=DT_UNKNOWN 不可得不钉平台）。
//   ③ 提前停止读取：读一半（2 条）即离开 seq using——关闭责任不因提前
//      离开免除（for-each 协议不自动关闭枚举资源）；离开后目录可用、
//      重扫须重新打开且仍读全 4 条。
//   ④ 复制规则：CreateNew 默认与显式（新目标内容一致、复制后源不变）、
//      已有目标冲突 AlreadyExists 且旧内容不动、Overwrite 打开或创建 +
//      写入前截断（旧目标更长截到源长度）。
//   ⑤ 移动规则：默认 NoReplace 文件移动（旧条目消失、内容一致）、
//      NoReplace 遇已有目标 AlreadyExists 两侧不动、Replace 覆盖文件、
//      Replace 对目录目标 IsDirectory 不覆盖不合并。
//   ⑥ 删除规则：File.deleteIfExists 真/再删假、File.delete 缺失
//      NotFound、非空目录 DirectoryNotEmpty（条目保留）、File.delete
//      目录 IsDirectory（目录保留）、Directory.delete 真实空目录成功、
//      Directory.delete 普通文件 NotDirectory（条目保留）、
//      Directory.deleteIfExists 缺失 false。
//   ⑦ 失败后的部分结果：createAll 先建 keep/inner 成功 → 普通文件挡路
//      的 createAll 报错（类别平台分支：Windows AlreadyExists / Linux
//      NotDirectory，fs_directory.rg ① 同口径）——已建子树不回滚、
//      失败目标未创建。
//   ⑧ 链接条目（kindHint=Link、经链接遍历、断链删除等）：本机无创建
//      链接入口（Windows 需权限，§4.5.3 创建后置）无法在语料内构造
//     ——7-4/7-5 环境限制同口径；由 7-2 unlink/rmdir/lstat 系统调用
//      语义与既有原语对拍承担，kindHint 的 null 容忍通道已由②覆盖。
//   ⑨ 所有资源经确定性清理：全程 using（reader/输出流/写入器），文件
//      逐个删除、目录自底向上删除、结束时唯一根目录整体删除并断言
//      不存在（临时文件关闭不自动删除，调用者负责删除，§4.5.8）。
// expect-output: accept-dir created staging=4 tempdir-ok
// expect-output: accept-dir traverse count=4 abs-paths-ok hints-ok
// expect-output: accept-dir early-stop-ok
// expect-output: accept-dir copy-rules-ok
// expect-output: accept-dir move-rules-ok
// expect-output: accept-dir delete-rules-ok
// expect-output: accept-dir partial-keep-ok
// expect-output: accept-dir-ok
// expect-exit: 0
// ============================================================================
import core.collections.*
import core.fs.*
import core.io.*
import core.math.*
import core.text.*

var fails: i32 = 0

func check(name: String, cond: bool) {
    if (not cond) {
        Console.println("check-FAIL ${name}")
        fails = (fails + 1)
    }
}

// 异常 kind 断言：body 抛出且 kind 命中 → 通过；未抛/异类 → 失败
//（fs_copymove expectKind 同款）。
func expectKind(name: String, want: FileSystemErrorKind,
        body: core.Action) {
    var ok = false
    try {
        body()
    } catch (e: FileSystemException) {
        if (e.kind == want) { ok = true }
    }
    check(name, ok)
}

// 确定性周期图案（fs_copymove 同款）。
func makePattern(n: i32, modulus: i32): Span\<u8> {
    const buf = spanOf\<u8>(n)
    var i: i32 = 0
    while (i < n) {
        buf[i] = ((i % modulus) as u8)
        i = (i + 1)
    }
    return buf
}

// 整文件内容与期望一致（读毕即关；fs_copymove fileContentEq 同款）。
func fileContentEq(path: Path, expected: Span\<u8>): bool {
    const inp = File.openRead(path)
    const got = inp.readAll()
    inp.dispose()
    if (got.length != expected.length) { return false }
    var i: i32 = 0
    while (i < got.length) {
        if ((got[i] if? (0 as u8)) != (expected[i] if? (0 as u8))) {
            return false
        }
        i = (i + 1)
    }
    return true
}

func writeFile(path: Path, data: Span\<u8>) {
    const out = File.openWrite(path, .CreateOrTruncate)
    out.write(data, 0, data.length)
    out.dispose()
}

func main(): i32 {
    const rnd = new Random()
    const rootP = Path.of("rigi_acceptdir_${rnd.nextU64()}")
    const stagingP = rootP.join(Path.of("staging"))
    const archiveDeepP = rootP.join(Path.of("archive/2026"))

    // ===== ① 创建子目录与临时资源 =====
    Directory.createAll(rootP)
    Directory.create(stagingP)
    Directory.createAll(archiveDeepP)
    const pat4k = makePattern(4096, 251)
    const tf = File.createTemporary(rootP, "accptmp")
    check("tempfile-name-prefix", tf.path.name().startsWith("accptmp_"))
    check("tempfile-exists", exists(tf.path, false))
    tf.stream.write(pat4k, 0, pat4k.length)
    tf.stream.dispose()
    check("tempfile-content", fileContentEq(tf.path, pat4k))
    check("tempfile-not-deleted-on-close", exists(tf.path, false))
    const td = Directory.createTemporary(rootP, "accptdir")
    check("tempdir-name-prefix", td.name().startsWith("accptdir_"))
    const kDir: FileKind = .Directory
    check("tempdir-kind", getInfo(td).kind == kDir)
    // staging 内容：恰好 4 条目（3 文件 + 1 子目录）
    const patA = makePattern(2000, 249)
    writeFile(stagingP.join(Path.of("a.bin")), patA)
    writeFile(stagingP.join(Path.of("b.txt")), makePattern(300, 241))
    writeFile(stagingP.join(Path.of("c.dat")), makePattern(700, 239))
    Directory.create(stagingP.join(Path.of("sub")))
    Console.println("accept-dir created staging=4 tempdir-ok")

    // ===== ② DirectoryReader 遍历（name/path/kindHint）=====
    var count: i32 = 0
    var namesOk = true
    var pathsOk = true
    var hintsOk = true
    var sawDot = false
    const kFile: FileKind = .File
    seq using(const r = Directory.openReader(stagingP)) {
        var done = false
        while (not done) {
            const e = r.read()
            if (e == null) {
                done = true
            } else {
                const ent: DirectoryEntry = e
                count = (count + 1)
                if ((ent.name == ".") or (ent.name == "..")) { sawDot = true }
                const isKnown = ((ent.name == "a.bin") or (ent.name == "b.txt")) or ((ent.name == "c.dat") or (ent.name == "sub"))
                if (not isKnown) { namesOk = false }
                // 条目 path：基于打开基准的绝对路径，末段 = 条目名称
                if ((not ent.path.isAbsolute()) or ((ent.path.text.endsWith(ent.name)) == false)) {
                    pathsOk = false
                }
                // kindHint：正确或 null 容忍；非 null 不得颠倒
                const hint: FileKind? = ent.kindHint
                if (hint != null) {
                    const hk: FileKind = hint
                    if (ent.name == "sub") {
                        if (hk != kDir) { hintsOk = false }
                    } else {
                        if (hk != kFile) { hintsOk = false }
                    }
                }
            }
        }
    }
    check("traverse-count-4", count == 4)
    check("traverse-no-dotdot", not sawDot)
    check("traverse-names", namesOk)
    check("traverse-abs-paths", pathsOk)
    check("traverse-hints-tolerant", hintsOk)
    Console.println("accept-dir traverse count=4 abs-paths-ok hints-ok")

    // ===== ③ 提前停止读取：读一半即离开 using =====
    seq using(const r2 = Directory.openReader(stagingP)) {
        const first = r2.read()
        const second = r2.read()
        check("early-leave-got-two", (first != null) and (second != null))
    }
    check("early-leave-dir-alive", exists(stagingP, true))
    seq using(const r3 = Directory.openReader(stagingP)) {
        var n: i32 = 0
        var done = false
        while (not done) {
            const e3 = r3.read()
            if (e3 == null) {
                done = true
            } else {
                n = (n + 1)
            }
        }
        check("rescan-reopen-4", n == 4)
    }
    Console.println("accept-dir early-stop-ok")

    // ===== ④ 复制规则 =====
    const kAE: FileSystemErrorKind = .AlreadyExists
    const copyDst = archiveDeepP.join(Path.of("a-copy.bin"))
    File.copy(stagingP.join(Path.of("a.bin")), copyDst)
    check("copy-createnew-content", fileContentEq(copyDst, patA))
    check("copy-source-unchanged",
        fileContentEq(stagingP.join(Path.of("a.bin")), patA))
    // 已有目标冲突：CreateNew AlreadyExists 且旧内容不动
    const old = makePattern(5000, 253)
    const existing = archiveDeepP.join(Path.of("existing.bin"))
    writeFile(existing, old)
    expectKind("copy-createnew-conflict-AE", kAE, func{() ->
        File.copy(stagingP.join(Path.of("c.dat")), existing)})
    check("copy-createnew-conflict-kept", fileContentEq(existing, old))
    // Overwrite：写入前截断（5000 → 700）
    const patC = makePattern(700, 239)
    File.copy(stagingP.join(Path.of("c.dat")), existing, .Overwrite)
    check("copy-overwrite-truncated", fileContentEq(existing, patC))
    Console.println("accept-dir copy-rules-ok")

    // ===== ⑤ 移动规则（临时文件归档的应用流）=====
    const kID: FileSystemErrorKind = .IsDirectory
    const payload = archiveDeepP.join(Path.of("payload.bin"))
    move(tf.path, payload)
    check("move-noreplace-old-gone", not exists(tf.path, false))
    check("move-noreplace-content", fileContentEq(payload, pat4k))
    // NoReplace 遇已有目标：AlreadyExists，两侧不动
    expectKind("move-noreplace-conflict-AE", kAE, func{() ->
        move(copyDst, payload)})
    check("move-noreplace-src-kept", fileContentEq(copyDst, patA))
    check("move-noreplace-dst-kept", fileContentEq(payload, pat4k))
    // Replace 覆盖文件：目标被替换、源消失
    const patDup = makePattern(900, 241)
    const dup = rootP.join(Path.of("dup.bin"))
    writeFile(dup, patDup)
    move(dup, payload, .Replace)
    check("move-replace-src-gone", not exists(dup, false))
    check("move-replace-dst-content", fileContentEq(payload, patDup))
    // Replace 对目录目标：IsDirectory（目录与源均保留）
    expectKind("move-replace-dir-ID", kID, func{() ->
        move(stagingP.join(Path.of("b.txt")), stagingP.join(Path.of("sub")),
            .Replace)})
    check("move-replace-dir-kept",
        (exists(stagingP.join(Path.of("sub")), true)) and
        (exists(stagingP.join(Path.of("b.txt")), false)))
    Console.println("accept-dir move-rules-ok")

    // ===== ⑥ 删除规则 =====
    const kNF: FileSystemErrorKind = .NotFound
    const kND: FileSystemErrorKind = .NotDirectory
    const kDNE: FileSystemErrorKind = .DirectoryNotEmpty
    const aBin = stagingP.join(Path.of("a.bin"))
    check("file-deleteIfExists-true", File.deleteIfExists(aBin) == true)
    check("file-deleteIfExists-false", File.deleteIfExists(aBin) == false)
    expectKind("file-delete-missing-NF", kNF, func{() -> File.delete(aBin)})
    // 非空目录：DirectoryNotEmpty（staging 此刻仍 3 文件 + sub）
    expectKind("dir-delete-nonempty-DNE", kDNE, func{() ->
        Directory.delete(stagingP)})
    check("dir-delete-nonempty-kept", exists(stagingP, true))
    // File.delete 目录：IsDirectory（目录保留）
    expectKind("file-delete-dir-ID", kID, func{() ->
        File.delete(stagingP.join(Path.of("sub")))})
    check("file-delete-dir-kept", exists(stagingP.join(Path.of("sub")), true))
    // 真实空目录成功；普通文件 NotDirectory；deleteIfExists 缺失 false
    Directory.delete(stagingP.join(Path.of("sub")))
    check("dir-delete-empty-ok",
        not exists(stagingP.join(Path.of("sub")), false))
    const blocker = rootP.join(Path.of("blocker"))
    writeFile(blocker, makePattern(64, 7))
    expectKind("dir-delete-file-ND", kND, func{() -> Directory.delete(blocker)})
    check("dir-delete-file-kept", exists(blocker, false))
    check("dir-deleteIfExists-missing-false",
        Directory.deleteIfExists(rootP.join(Path.of("no_such_dir"))) == false)
    Console.println("accept-dir delete-rules-ok")

    // ===== ⑦ 失败后的部分结果：已建子树不回滚、失败目标未创建 =====
    // 中途遇普通文件的错误类别平台分支（fs_directory.rg ① 同口径）：
    // Windows 报 AlreadyExists、exists(文件/子路径) 返回 false；
    // Linux 的 stat/lstat 对该形态报 ENOTDIR → createAll/exists 均
    // NotDirectory（不伪装不存在，§4.5.3/§4.5.9 平台语义注记）
    Directory.createAll(rootP.join(Path.of("keep/inner")))
    check("partial-created",
        exists(rootP.join(Path.of("keep/inner")), true))
    if (fsIsWindows()) {
        expectKind("createAll-file-block-AE", kAE, func{() ->
            Directory.createAll(rootP.join(Path.of("blocker/child")))})
        check("partial-keep-existing",
            exists(rootP.join(Path.of("keep/inner")), true))
        check("partial-failed-target-absent",
            not exists(rootP.join(Path.of("blocker/child")), false))
    } else {
        expectKind("createAll-file-block-ND", kND, func{() ->
            Directory.createAll(rootP.join(Path.of("blocker/child")))})
        check("partial-keep-existing",
            exists(rootP.join(Path.of("keep/inner")), true))
        // exists 返回 bool（expectKind 的 void 委托面放不下），
        // try/catch 直取 kind（fs_directory.rg exists-file-child 同款）
        var got: FileSystemErrorKind? = null
        try {
            exists(rootP.join(Path.of("blocker/child")), false)
        } catch (e: FileSystemException) {
            got = e.kind
        }
        check("exists-file-child-ND", (got if? kND) == kND)
    }
    Console.println("accept-dir partial-keep-ok")

    // ===== ⑨ 确定性清理（链接条目见文件头⑧：无创建入口无法构造，
    //     由 7-2 原语语义与既有对拍承担）=====
    File.delete(stagingP.join(Path.of("b.txt")))
    File.delete(stagingP.join(Path.of("c.dat")))
    Directory.delete(stagingP)
    File.delete(copyDst)
    File.delete(existing)
    File.delete(payload)
    Directory.delete(archiveDeepP)
    Directory.delete(rootP.join(Path.of("archive")))
    File.delete(blocker)
    Directory.delete(rootP.join(Path.of("keep/inner")))
    Directory.delete(rootP.join(Path.of("keep")))
    Directory.delete(td)
    Directory.delete(rootP)
    check("cleanup-root-gone", not exists(rootP, false))
    if (fails == 0) {
        Console.println("accept-dir-ok")
    }
    return fails
}
