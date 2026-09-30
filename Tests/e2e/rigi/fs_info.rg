// ============================================================================
// fs_info.rg —— 施工块 7-4（STDLIB §4.5.3 + §4.5.9 序列化条目 + §4.5.5
// removeLink，D5）：core.fs 信息查询与链接端到端语料。
//   ① getInfo 普通文件：kind=.File、length 正确、path 保留查询时文本、
//      modifiedAt/accessedAt 非 null 且量级合理、createdAt 非 null 时
//      量级合理（Linux 无 btime → null，不冒充不伪造）；followLinks=
//      false 对普通文件与跟随形态一致
//   ② getInfo 目录：kind=.Directory、length 为 null（非普通文件不伪造
//      长度）、modifiedAt 非 null
//   ③ 不存在：getInfo 抛 NotFound、tryGetInfo → null、exists → false
//   ④ 非 NotFound 失败不伪装成不存在（§4.5.3/§4.5.9）：库层转换口径 =
//      宿主归一类别——先经原语探测宿主对超长末段名（300 字符分量）的
//      类别（归一码表 7-2 双端同值，但 Windows 宿主对该形态的原始错误
//      经 VM/native 各自映射可能不同：native 侧 \\?\ 长路径折叠进
//      NotFound，VM 侧 .NET 归非 NotFound 类别），再断言 tryGetInfo/
//      exists 与宿主类别一致——非 NotFound 原样抛（绝不转 null/false）；
//      NotFound 才转 null/false（宿主层面即「不存在」）。中间分量是
//      文件（f.txt\child）原样失败（双端均折叠 NotFound，只钉不吞面）
//   ⑤ 既有链接识别（环境限制：Windows 创建符号链接需权限，链接创建
//      入口按 §4.5.3 后置——用系统既有 junction「C:\Users\All Users」
//      只读探测；探测不到时静默跳过该子组）：followLinks=false 查链接
//      本身 kind=.Link、length null。跟随面不对拍：系统既有 junction
//      属系统过滤保护的特殊条目，VM 侧解析返回 NotFound、native 侧可
//      正常跟随，链接跟随由普通链接承担（无创建入口，报告说明）
//   ⑥ getRealPath：要求目标存在、解析为绝对路径且 exists 为真；不存在
//      抛 NotFound
//   ⑦ removeLink：普通文件报 WrongType（条目保留）；不存在报 NotFound
//     （删链接不删目标的删除面：无创建链接入口无法安全构造——系统既有
//      junction 不可删除，由 fsUnlink 系统语义与 7-2 原语对拍承担，
//      报告说明）
//   ⑧ FileInfo 序列化（§4.5.9）：deepCopy 往返字段保持；记录不查盘不
//      刷新（记录后追加文件，记录 length 不变、实时查询得新值）；恢复
//      得到「当时的信息记录」；恢复校验生效（非空字段收 null 拒绝）
// 目录名取系统随机源（Random 无参构造）保证 e2e 并行与 NativeE2E 双宿
// 主各自唯一；产物全在唯一目录内、末尾自清理。
// expect-output: fs-info-ok
// expect-exit: 0
// ============================================================================
import core.fs.*
import core.serialization.*
import core.io.Console
import core.math.*
import core.collections.*
import core.text.*

func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

// 时间量级合理性：epoch 毫秒落在 2017-07 .. 2096 窗口内
func plausibleMs(stamp: core.time.TimeStamp): bool {
    return ((stamp.milliseconds > 1500000000000L)
        and (stamp.milliseconds < 4000000000000L))
}

func main(): i32 {
    var fails = 0
    const rnd = new Random()
    const root = "rigi_fsinfo_probe_${rnd.nextU64()}"
    const f = "${root}\\f.txt"
    const d = "${root}\\d"
    const missing = "${root}\\nope.txt"
    fsMkdir(root, FS_MODE_DIRECTORY)

    // 准备：21 字节普通文件 + 空目录（open 不自动创建父目录）
    var h = fsOpen(f, ((FS_F_WRITE | FS_F_CREATE) | FS_F_TRUNCATE),
        FS_MODE_FILE)
    const data = "Hello fs info query!!".toUtf8Span()
    fsWrite(h, data, 0, (data.length as i32))
    h.dispose()
    fsMkdir(d, FS_MODE_DIRECTORY)

    // ===== ① getInfo 普通文件 =====
    const kFile: FileKind = .File
    const info = getInfo(Path.of(f))
    fails = fails + check("file-kind", info.kind == kFile)
    fails = fails + check("file-length",
        (info.length if? (0L - 1L)) == 21L)
    fails = fails + check("file-path-kept", info.path.text == f)
    const mmOpt: core.time.TimeStamp? = info.modifiedAt
    if (mmOpt != null) {
        const mm: core.time.TimeStamp = mmOpt
        fails = fails + check("file-mtime-plausible", plausibleMs(mm))
    } else {
        fails = fails + check("file-mtime-nonnull", false)
    }
    const atOpt: core.time.TimeStamp? = info.accessedAt
    if (atOpt != null) {
        const at: core.time.TimeStamp = atOpt
        fails = fails + check("file-atime-plausible", plausibleMs(at))
    } else {
        fails = fails + check("file-atime-nonnull", false)
    }
    // createdAt：非 null 时量级合理且 ≤ modifiedAt（Linux 无 btime →
    // null，语料不钉平台可得性，只钉「不冒充」面）
    const crOpt: core.time.TimeStamp? = info.createdAt
    if (crOpt != null) {
        const cr: core.time.TimeStamp = crOpt
        var birthOk = plausibleMs(cr)
        if (mmOpt != null) {
            const mm2: core.time.TimeStamp = mmOpt
            birthOk = birthOk and (cr.milliseconds <= mm2.milliseconds)
        }
        fails = fails + check("file-ctime-plausible", birthOk)
    }
    // followLinks=false 对普通文件与跟随形态一致（末段非链接）
    const infoNoFollow = getInfo(Path.of(f), false)
    fails = fails + check("file-nofollow-kind", infoNoFollow.kind == kFile)
    fails = fails + check("file-nofollow-length",
        (infoNoFollow.length if? (0L - 1L)) == 21L)

    // ===== ② getInfo 目录 =====
    const kDir: FileKind = .Directory
    const dInfo = getInfo(Path.of(d))
    fails = fails + check("dir-kind", dInfo.kind == kDir)
    fails = fails + check("dir-length-null", dInfo.length == null)
    fails = fails + check("dir-mtime-nonnull", dInfo.modifiedAt != null)

    // ===== ③ 不存在：NotFound / null / false 一致 =====
    const kNF: FileSystemErrorKind = .NotFound
    var nfKind = false
    try {
        const gone = getInfo(Path.of(missing))
    } catch (e: FileSystemException) {
        const k: FileSystemErrorKind = e.kind
        nfKind = (k == kNF)
    }
    fails = fails + check("missing-getinfo-notfound", nfKind)
    fails = fails + check("missing-trygetinfo-null",
        tryGetInfo(Path.of(missing)) == null)
    fails = fails + check("missing-exists-false",
        exists(Path.of(missing)) == false)

    // ===== ④ 非 NotFound 失败不伪装成不存在（宿主类别口径一致）=====
    // 稳定可构造形态：300 字符末段名（Windows 分量上限 255）。先探测
    // 宿主对该形态的归一类别，再断言 tryGetInfo/exists 与之一致：
    // 非 NotFound 原样抛（绝不伪装成不存在）；NotFound 才转 null/false
    const sbL = new StringBuilder()
    var li = 0
    while (li < 300) {
        sbL.append("a")
        li = li + 1
    }
    const pLong = Path.of("${root}\\${sbL.toString()}.txt")
    var probeKind: FileSystemErrorKind? = null
    try {
        fsStat(pLong.text)
    } catch (e: FileSystemException) {
        probeKind = e.kind
    }
    var tryThrew = false
    try {
        const r1 = tryGetInfo(pLong)
    } catch (e: FileSystemException) {
        tryThrew = true
    }
    var exThrew = false
    try {
        exists(pLong)
    } catch (e: FileSystemException) {
        exThrew = true
    }
    const kNF3: FileSystemErrorKind = .NotFound
    if ((probeKind if? kNF3) == kNF3) {
        // 宿主报告 NotFound（native 侧把该形态折叠进 NOT_FOUND）：转
        // null/false 是宿主口径，非库层伪装
        fails = fails + check("notdir-consistent-null", tryThrew == false)
        fails = fails + check("notdir-consistent-false", exThrew == false)
    } else {
        // 宿主类别非 NotFound（本机 VM 侧 InvalidPath）：必须原样抛
        fails = fails + check("notdir-not-disguised-try", tryThrew)
        fails = fails + check("notdir-not-disguised-exists", exThrew)
    }
    // 中间分量是文件：原样失败（双端宿主均折叠 NotFound，只钉不吞面）
    var midThrew = false
    try {
        const r2 = getInfo(Path.of("${f}\\child"))
    } catch (e: FileSystemException) {
        midThrew = true
    }
    fails = fails + check("notdir-midfile-throws", midThrew)

    // ===== ⑤ 既有链接识别（只读探测，见文件头环境限制说明）=====
    // 只探测 followLinks=false 的末段链接识别面：系统既有 junction 属
    // 特殊兼容条目（系统过滤保护），VM 侧 ResolveLinkTarget(final) 对
    // 其返回 null（→ NotFound）而 native 侧可正常跟随——链接「跟随到
    // 目标」面由普通链接承担，本机无创建链接入口，语料不触达（报告
    // 说明）
    const jSelf = tryGetInfo(Path.of("C:\\Users\\All Users"), false)
    if (jSelf != null) {
        const j: FileInfo = jSelf
        const kLink: FileKind = .Link
        fails = fails + check("junction-self-link", j.kind == kLink)
        fails = fails + check("junction-self-len-null", j.length == null)
        fails = fails + check("junction-self-path-kept", j.path.text
            == "C:\\Users\\All Users")
    }

    // ===== ⑥ getRealPath =====
    const rp = getRealPath(Path.of(f))
    fails = fails + check("realpath-abs", rp.isAbsolute())
    fails = fails + check("realpath-exists", exists(rp, true))
    var rpNf = false
    try {
        const r3 = getRealPath(Path.of(missing))
    } catch (e: FileSystemException) {
        const k3: FileSystemErrorKind = e.kind
        rpNf = (k3 == kNF)
    }
    fails = fails + check("realpath-missing-notfound", rpNf)

    // ===== ⑦ removeLink =====
    var wt = false
    try {
        removeLink(Path.of(f))
    } catch (e: FileSystemException) {
        const k4: FileSystemErrorKind = e.kind
        const kWt: FileSystemErrorKind = .WrongType
        wt = (k4 == kWt)
    }
    fails = fails + check("rmlink-file-wrongtype", wt)
    fails = fails + check("rmlink-file-kept", exists(Path.of(f), false))
    var rmNf = false
    try {
        removeLink(Path.of(missing))
    } catch (e: FileSystemException) {
        const k5: FileSystemErrorKind = e.kind
        rmNf = (k5 == kNF)
    }
    fails = fails + check("rmlink-missing-notfound", rmNf)

    // ===== ⑧ 序列化（§4.5.9：当时的记录，不查盘不刷新）=====
    // 记录（info）取得后追加 5 字节：实时查询得 26，记录保持 21
    var ah = fsOpen(f, ((FS_F_WRITE | FS_F_APPEND) | FS_F_CREATE),
        FS_MODE_FILE)
    const extra = "12345".toUtf8Span()
    fsWrite(ah, extra, 0, 5)
    ah.dispose()
    fails = fails + check("live-length-refreshed",
        (getInfo(Path.of(f)).length if? (0L - 1L)) == 26L)
    const back = deepCopy\<FileInfo>(info)
    fails = fails + check("ser-len-not-refreshed",
        (back.length if? (0L - 1L)) == 21L)
    fails = fails + check("ser-path-kept", back.path.text == f)
    fails = fails + check("ser-kind-kept", back.kind == kFile)
    const bm: core.time.TimeStamp? = back.modifiedAt
    const rm2: core.time.TimeStamp? = info.modifiedAt
    if ((bm != null) and (rm2 != null)) {
        const b1: core.time.TimeStamp = bm
        const r1s: core.time.TimeStamp = rm2
        fails = fails + check("ser-mtime-kept",
            ((b1.milliseconds == r1s.milliseconds)
                and (b1.nanoseconds == r1s.nanoseconds)))
    } else {
        fails = fails + check("ser-mtime-kept", false)
    }
    // 正向恢复：得到「当时的信息记录」（不查询磁盘、不自动刷新）
    const wire = info:Serializable.toParcel()
    const restored = fromParcel\<FileInfo>(wire)
    fails = fails + check("ser-restore-len",
        (restored.length if? (0L - 1L)) == 21L)
    // 恢复校验：非空字段收 null 拒绝（字段集合完全匹配）
    var rejected = false
    try {
        const wire2 = info:Serializable.toParcel()
        wire2.setDynamic("path", null)
        const bad = fromParcel\<FileInfo>(wire2)
    } catch (e: SerializationException) {
        rejected = true
    }
    fails = fails + check("ser-restore-validates", rejected)

    // ===== ⑨ 清理（唯一目录名保证不残留共享名）=====
    fsUnlink(f)
    fsRmdir(d)
    fsRmdir(root)
    if (fails == 0) {
        Console.println("fs-info-ok")
    }
    return fails
}
