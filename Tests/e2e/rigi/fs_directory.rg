// ============================================================================
// fs_directory.rg —— 施工块 7-5（STDLIB §4.5.4 + §4.5.5，D5）：core.fs
// 目录读取 + 创建/删除端到端语料。
//   ① create/createAll：末级创建（父级须存在）、父级缺失 NotFound、
//      目标已存在 AlreadyExists、createAll 允许已有目录并逐级补建、
//      中途遇普通文件报错且不回滚既有子树、失败目标未创建（错误类别
//      平台分支：Windows AlreadyExists / Linux NotDirectory——中间
//      分量不是目录，宿主系统差异，§4.5.3/§4.5.9 平台语义注记）
//   ② DirectoryReader 流式读取：恰好 5 条目（3 文件 + 子目录 + 隐藏项）
//      逐一读出（不排序不递归含隐藏项，双端原语保证）、无 `.`/`..`、
//      条目 path 为基于打开基准的绝对路径（以打开基准文本为前缀、末段
//      = 条目名称；经链接打开不解析——本机无创建链接入口，拼写保持面
//      由词法组合保证）、kindHint 正确或 null 容忍（Linux readdir
//      d_type=DT_UNKNOWN 时提示不可得 → null，语料不钉平台可得性）
//   ③ 生命周期：结束后持续返回 null（重扫须重新打开）、读一半提前离开
//     （seq using 收尾——for-each 协议不自动关闭枚举资源，§4.5.4）、
//      重复 dispose 幂等、关闭后 read 抛 IllegalStateException；故障态
//      只允许清理的 I/O 失败面无法在合法名称环境内构造（Windows 无法
//      创建非法 UTF-8 名称；名称解码失败报 InvalidNameEncoding 不替换
//      不跳过由 7-2 原语对拍承担——native Linux 侧 utf8 校验与双端归
//      一码表，语料注释说明）
//   ④ Directory.list：整体收集（数量与名称命中）、maxEntries 超限报错
//      不截断（异常形态即「中途失败不返回部分列表」——并发修改窗口
//      无法在单线程语料内构造）、恰好等于上限成功、-1 不设上限、其他
//      负值非法、空目录 → 空列表
//   ⑤ 删除族：File.delete 文件/Directory.delete 真实空目录/非空目录
//      DirectoryNotEmpty（VM 侧 rmdir 原语非空归一类别与 native 不同，
//      由公共层判空统一）/不存在默认 NotFound/deleteIfExists 只把不存
//      在转 false（真路径 true、再删 false、非空与目录目标不吞）/
//      File.delete 目录 IsDirectory/Directory.delete 普通文件
//      NotDirectory（条目保留）。断链与指向目录链接的删除形态：本机
//      无创建链接入口（Windows 需权限，§4.5.3 创建后置）无法构造，
//      链接末段 NotDirectory 判别与 unlink 只落条目本身的语义由 7-2
//      原语系统调用语义承担（语料注释说明）
// 目录名取系统随机源（Random 无参构造）保证 e2e 并行与 NativeE2E 双宿
// 主各自唯一；产物全在唯一目录内、末尾自清理。
// 契约拼写注：§4.5.4 Directory.open 的「open」是语言保留修饰符关键字
// （M31 声明名位拦截），实现用 openReader 过渡拼写（File.openRead/
// openWrite 前缀先例），见块报告待裁决问题。
// expect-output: fs-dir-ok
// expect-exit: 0
// ============================================================================
import core.fs.*
import core.io.Console
import core.math.*
import core.collections.*
import core.text.*

// 失败计数（多宿主输出只钉最终 ok 行，不打印条目顺序——读取顺序不
// 保证排序，§4.5.4）
var fails: i32 = 0

func check(name: String, cond: bool) {
    if (not cond) {
        Console.println("check-FAIL ${name}")
        fails = fails + 1
    }
}

func main(): i32 {
    const rnd = new Random()
    const root = "rigi_fsdir_probe_${rnd.nextU64()}"
    const rootP = Path.of(root)
    // 结构目录（①创建语义）与条目目录（②③④恰好 5 条目）分离
    const topP = Path.of("${root}/top")
    const entP = Path.of("${root}/entries")

    // ===== ① create/createAll =====
    // 末级创建：父级须存在（root 尚不存在 → NotFound）
    var got: FileSystemErrorKind? = null
    try {
        Directory.create(Path.of("${root}/top"))
    } catch (e: FileSystemException) {
        got = e.kind
    }
    const kNF: FileSystemErrorKind = .NotFound
    check("create-missing-root-NF", (got if? kNF) == kNF)
    // createAll 逐级补建（root/top 均缺失）
    Directory.createAll(topP)
    check("createAll-nested", exists(topP, true))
    const topInfo = getInfo(topP)
    const kDirC: FileKind = .Directory
    check("createAll-top-isdir", topInfo.kind == kDirC)
    // create 末级成功（父级已存在）
    Directory.create(Path.of("${root}/top/emptydir"))
    check("create-leaf", exists(Path.of("${root}/top/emptydir"), false))
    // create 父级缺失 → NotFound；目标已存在 → AlreadyExists（目录形态）
    got = null
    try {
        Directory.create(Path.of("${root}/no_such/child"))
    } catch (e: FileSystemException) {
        got = e.kind
    }
    check("create-missing-parent-NF", (got if? kNF) == kNF)
    const kAE: FileSystemErrorKind = .AlreadyExists
    got = null
    try {
        Directory.create(Path.of("${root}/top/emptydir"))
    } catch (e: FileSystemException) {
        got = e.kind
    }
    check("create-exists-dir-AE", (got if? kAE) == kAE)
    // createAll 允许已有目录（整链已存在）
    Directory.createAll(topP)
    check("createAll-existing-ok", exists(topP, true))
    // 中途遇普通文件：平台系统差异（§4.5.3/§4.5.9 平台语义注记）——
    // Linux 的 stat/lstat 对「文件/子路径」报 ENOTDIR → NotDirectory，
    // createAll 前置判别原样抛、exists 同形态同类别抛（中间分量不是
    // 目录不伪装成不存在）；Windows 路径属性族对该形态报
    // ERROR_PATH_NOT_FOUND → NotFound，createAll 走「缺失」分支对父级
    // 文件判型报 AlreadyExists、exists 返回 false。两种结果都不回滚
    // 既有子树、失败目标未创建
    const fBlock = "${root}/top/plain.txt"
    var h = File.openWrite(Path.of(fBlock), .CreateOrTruncate)
    h.dispose()
    const kNDBlk: FileSystemErrorKind = .NotDirectory
    if (fsIsWindows()) {
        got = null
        try {
            Directory.createAll(Path.of("${fBlock}/child"))
        } catch (e: FileSystemException) {
            got = e.kind
        }
        check("createAll-file-block-AE", (got if? kAE) == kAE)
        check("createAll-keep-existing", exists(topP, true))
        check("createAll-failed-target-absent",
            not exists(Path.of("${fBlock}/child"), false))
    } else {
        // Linux：createAll 报 NotDirectory（不落 AlreadyExists）；
        // exists 对「文件/子路径」抛 NotDirectory 而非返回 false
        got = null
        try {
            Directory.createAll(Path.of("${fBlock}/child"))
        } catch (e: FileSystemException) {
            got = e.kind
        }
        check("createAll-file-block-ND", (got if? kNDBlk) == kNDBlk)
        check("createAll-keep-existing", exists(topP, true))
        got = null
        try {
            exists(Path.of("${fBlock}/child"), false)
        } catch (e: FileSystemException) {
            got = e.kind
        }
        check("exists-file-child-ND", (got if? kNDBlk) == kNDBlk)
    }

    // 条目目录：恰好 5 条目（3 普通文件 + 1 子目录 + 1 隐藏项）
    Directory.create(entP)
    h = File.openWrite(Path.of("${root}/entries/a.txt"), .CreateOrTruncate)
    h.dispose()
    h = File.openWrite(Path.of("${root}/entries/b.log"), .CreateOrTruncate)
    h.dispose()
    h = File.openWrite(Path.of("${root}/entries/c.dat"), .CreateOrTruncate)
    h.dispose()
    Directory.create(Path.of("${root}/entries/sub"))
    h = File.openWrite(Path.of("${root}/entries/.hidden"), .CreateOrTruncate)
    h.dispose()

    // ===== ② DirectoryReader 流式读取 =====
    var count = 0
    var sawDot = false
    var sawSub = false
    var sawHidden = false
    var hitsA = 0
    var hitsB = 0
    var allAbs = true
    var hintsOk = true
    seq using(const r = Directory.openReader(entP)) {
        var done = false
        while (not done) {
            const e = r.read()
            if (e == null) {
                done = true
            } else {
                const ent: DirectoryEntry = e
                count = count + 1
                if ((ent.name == ".") or (ent.name == "..")) { sawDot = true }
                if (ent.name == "sub") { sawSub = true }
                if (ent.name == ".hidden") { sawHidden = true }
                if (ent.name == "a.txt") { hitsA = hitsA + 1 }
                if (ent.name == "b.log") { hitsB = hitsB + 1 }
                // 访问器结果直调方法（chainfix 回归：rich struct 访问器
                // pub get 结果上直调方法曾被 P4 写穿接管误报 'path' is
                // inaccessible——接管判定现与写回构造共用 setter 使用点
                // 可见性口径，只读 place 不接管不写回；此处曾以先绑定
                // 局部绕行，语义相同，还原直调形态钉住回归）
                if (not ent.path.isAbsolute()) { allAbs = false }
                // 条目绝对路径以打开基准为前缀（末段 = 条目名称）
                const et: String = ent.path.text
                if (et.endsWith(ent.name) == false) {
                    allAbs = false
                }
                // kindHint：正确或 null 容忍（Linux DT_UNKNOWN → null，
                // 不钉平台可得性）；非 null 时文件/目录提示不得颠倒
                const hint: FileKind? = ent.kindHint
                if (hint != null) {
                    const hk: FileKind = hint
                    const kF: FileKind = .File
                    const kD: FileKind = .Directory
                    if (ent.name == "sub") {
                        if (hk != kD) { hintsOk = false }
                    } else {
                        if (hk != kF) { hintsOk = false }
                    }
                }
            }
        }
    }
    check("reader-count-5", count == 5)
    check("reader-no-dotdot", not sawDot)
    check("reader-saw-sub", sawSub)
    check("reader-saw-hidden", sawHidden)
    check("reader-names-once", ((hitsA == 1) and (hitsB == 1)))
    check("reader-abs-paths", allAbs)
    check("reader-hints-tolerant", hintsOk)

    // ===== ③ 生命周期：结束后持续 null / 提前离开 / 关闭态 =====
    // 结束后持续返回 null（固定结束状态，重扫须重新打开）
    const r1 = Directory.openReader(entP)
    var drain = 0
    var againNull = false
    var done3 = false
    while (not done3) {
        const e = r1.read()
        if (e != null) {
            drain = drain + 1
        } else {
            // 首个 null 之后立刻再读：仍 null（此后持续 null）
            againNull = (r1.read() == null)
            done3 = true
        }
    }
    check("reader-end-count-5", drain == 5)
    check("reader-end-persists", againNull)
    r1.dispose()
    r1.dispose() // 重复 dispose 幂等
    var closedRead = false
    try {
        const gone = r1.read()
    } catch (e: core.IllegalStateException) {
        closedRead = true
    }
    check("closed-read-ISE", closedRead)
    // 重扫须重新打开：重新 open 后仍能读全 5 条
    seq using(const r2 = Directory.openReader(entP)) {
        var n2 = 0
        var done2 = false
        while (not done2) {
            const e2 = r2.read()
            if (e2 != null) {
                n2 = n2 + 1
            } else {
                done2 = true
            }
        }
        check("rescan-reopen-5", n2 == 5)
    }
    // 提前离开：读一半后 seq using 收尾（关闭责任不因提前离开免除，
    // for-each 协议不自动关闭枚举资源，§4.5.4）——断言无泄漏无崩溃且
    // 后续操作正常
    seq using(const r3 = Directory.openReader(entP)) {
        const first = r3.read()
        const second = r3.read()
        check("early-leave-got-two",
            (first != null) and (second != null))
    }
    check("after-early-leave-alive", exists(entP, true))

    // ===== ④ Directory.list =====
    const all = Directory.list(entP)
    check("list-count-5", all.length == 5L)
    var listSub = false
    var listHidden = false
    var li = 0L
    while (li < all.length) {
        const eo: DirectoryEntry? = all.getAtIndex(li)
        if (eo != null) {
            const ee: DirectoryEntry = eo
            if (ee.name == "sub") { listSub = true }
            if (ee.name == ".hidden") { listHidden = true }
        }
        li = li + 1L
    }
    check("list-saw-sub", listSub)
    check("list-saw-hidden", listHidden)
    // 上限超出报错不截断（4 < 5 → OutOfBoundException；异常即「中途
    // 失败不返回部分列表」形态——并发修改窗口单线程语料无法构造）
    var threw4 = false
    try {
        const few = Directory.list(entP, 4L)
    } catch (e: core.OutOfBoundException) {
        threw4 = true
    }
    check("list-limit4-throws", threw4)
    var threw1 = false
    try {
        const one = Directory.list(entP, 1L)
    } catch (e2: core.OutOfBoundException) {
        threw1 = true
    }
    check("list-limit1-throws", threw1)
    // 恰好等于上限 → 全量收集成功
    const exact5 = Directory.list(entP, 5L)
    check("list-limit5-exact", exact5.length == 5L)
    // -1 不设上限（默认形态）；其他负值非法
    const unbounded = Directory.list(entP)
    check("list-default-unbounded", unbounded.length == 5L)
    const unbounded2 = Directory.list(entP, -1L)
    check("list-minus1-unbounded", unbounded2.length == 5L)
    var threwNeg = false
    try {
        const bad = Directory.list(entP, -2L)
    } catch (e3: core.OutOfBoundException) {
        threwNeg = true
    }
    check("list-neg2-throws", threwNeg)
    // list 空目录 → 空列表
    const emptyList = Directory.list(Path.of("${root}/top/emptydir"))
    check("list-empty-dir", emptyList.length == 0L)

    // ===== ⑤ 删除族 =====
    // File.delete / deleteIfExists（真路径 true、再删 false）
    check("file-deleteIfExists-true",
        File.deleteIfExists(Path.of("${root}/entries/a.txt")) == true)
    check("file-gone", not exists(Path.of("${root}/entries/a.txt"), false))
    check("file-deleteIfExists-false",
        File.deleteIfExists(Path.of("${root}/entries/a.txt")) == false)
    var fileMissingNf = false
    try {
        File.delete(Path.of("${root}/entries/a.txt"))
    } catch (e: FileSystemException) {
        const k: FileSystemErrorKind = e.kind
        fileMissingNf = (k == kNF)
    }
    check("file-delete-missing-NF", fileMissingNf)
    // Directory.delete 真实空目录
    Directory.delete(Path.of("${root}/top/emptydir"))
    check("dir-delete-empty-ok",
        not exists(Path.of("${root}/top/emptydir"), false))
    // Directory.delete 不存在 → NotFound；deleteIfExists → false
    var dirMissingNf = false
    try {
        Directory.delete(Path.of("${root}/top/emptydir"))
    } catch (e: FileSystemException) {
        const k2: FileSystemErrorKind = e.kind
        dirMissingNf = (k2 == kNF)
    }
    check("dir-delete-missing-NF", dirMissingNf)
    check("dir-deleteIfExists-missing-false",
        Directory.deleteIfExists(Path.of("${root}/top/emptydir")) == false)
    // 非空目录 → DirectoryNotEmpty（条目保留；公共层判空统一双端类别）
    const kDNE: FileSystemErrorKind = .DirectoryNotEmpty
    got = null
    try {
        Directory.delete(entP)
    } catch (e: FileSystemException) {
        got = e.kind
    }
    check("dir-delete-nonempty-DNE", (got if? kDNE) == kDNE)
    check("dir-delete-nonempty-kept", exists(entP, true))
    // deleteIfExists 非空不吞：不转 false，原样抛
    var ifExNonEmptyThrew = false
    try {
        Directory.deleteIfExists(entP)
    } catch (e: FileSystemException) {
        const k3: FileSystemErrorKind = e.kind
        ifExNonEmptyThrew = (k3 == kDNE)
    }
    check("dir-deleteIfExists-nonempty-throws", ifExNonEmptyThrew)
    // Directory.delete 普通文件 → NotDirectory（条目保留）
    const kND: FileSystemErrorKind = .NotDirectory
    got = null
    try {
        Directory.delete(Path.of("${root}/entries/b.log"))
    } catch (e: FileSystemException) {
        got = e.kind
    }
    check("dir-delete-file-ND", (got if? kND) == kND)
    check("dir-delete-file-kept",
        exists(Path.of("${root}/entries/b.log"), false))
    // File.delete 目录 → IsDirectory（目录保留）
    const kID: FileSystemErrorKind = .IsDirectory
    got = null
    try {
        File.delete(Path.of("${root}/entries/sub"))
    } catch (e: FileSystemException) {
        got = e.kind
    }
    check("file-delete-dir-ID", (got if? kID) == kID)
    check("file-delete-dir-kept",
        exists(Path.of("${root}/entries/sub"), true))
    // File.deleteIfExists 非文件目标不吞（目录 → IsDirectory 原样抛）
    var ifExDirThrew = false
    try {
        File.deleteIfExists(Path.of("${root}/entries/sub"))
    } catch (e: FileSystemException) {
        const k4: FileSystemErrorKind = e.kind
        ifExDirThrew = (k4 == kID)
    }
    check("file-deleteIfExists-dir-throws", ifExDirThrew)
    // 断链删除形态（删除链接本身不删目标 / Directory.delete 末段链接
    // NotDirectory）：本机无创建链接入口（Windows 需权限，创建入口
    // §4.5.3 后置）无法构造——由 7-2 unlink/rmdir 系统调用语义与
    // Directory.delete 的 lstat 判别承担，语料注释说明（fs_info.rg
    // 既有 junction 只读探测同口径）

    // ===== ⑥ 清理（唯一目录名保证不残留共享名）=====
    File.delete(Path.of("${root}/entries/b.log"))
    File.delete(Path.of("${root}/entries/c.dat"))
    File.delete(Path.of("${root}/entries/.hidden"))
    Directory.delete(Path.of("${root}/entries/sub"))
    Directory.delete(entP)
    File.delete(Path.of(fBlock))
    Directory.delete(topP)
    Directory.delete(rootP)
    check("cleanup-root-gone", not exists(rootP, false))
    if (fails == 0) {
        Console.println("fs-dir-ok")
    }
    return fails
}
