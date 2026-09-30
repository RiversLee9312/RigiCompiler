// ============================================================================
// fs_path.rg —— 施工块 7-1（STDLIB §4.5.1 / §4.5.2 / §4.5.9，D5）
// core.fs Path 纯词法语料：不访问磁盘（VM 与 native 双宿主对拍，
// NativeE2E「路径词法对拍」Case 复用本语料）。
//   ① 构造拒绝（§4.5.2 Windows 规则，本机 Windows 实测）：空文本、
//      内嵌 NUL、C:foo、C:、\foo、/foo、设备命名空间（\\.\、\\?\）、
//      保留设备名称（CON/con.txt/COM1/LPT9/NUL 等，COM10 合法）、
//      末尾空格/点名称（foo␣、foo.、a\b.）、备用数据流冒号（a:b）。
//   ② 构造接受：盘符绝对（含混合分隔符）、UNC（含前导混合分隔符）、
//      普通相对、'.'、前导空白保留（不裁剪、不规范化）。
//   ③ equals/hash：文本精确、大小写敏感；内容相同哈希相同。
//   ④ normalizeLexically：'.'/'..' 配对消解、相对开头 '..' 保留、
//      绝对不越根、分隔符统一、尾随分隔符处理。
//   ⑤ join/toAbsolute/relativeTo：相对组合、绝对参数报错（携带双
//      路径、不丢弃前缀）、base 非绝对报错、同根推导、根不同报错。
//   ⑥ root/parent/name/extension/nameWithoutExtension/isAbsolute：
//      根无父、单段父为 '.'、尾部分隔符、.gitignore 无扩展名、
//      a.tar.gz 得 .gz。
//   ⑦ 序列化（§4.5.9）：deepCopy 往返文本保持；恢复路径构造校验
//      生效（Parcel 塞非法文本拒绝）。
// 平台分支：①②④⑤⑥ 的平台分歧断言按宿主分支（fsIsWindows，VM 宿主
// 判定 / native _WIN32 编译期判定，双宿主同语义）——Windows 侧保留
// 本机实测原断言，Linux 侧对称覆盖（'/' 根组合推导、Windows 盘符文本
// 在 Linux 是普通相对名称：构造接受、toAbsolute/relativeTo 拒其为
// 非绝对 base/异根，§4.5.2「语法遵循当前平台」）。
// expect-output: fs-path-ok
// expect-exit: 0
// ============================================================================
import core.fs.*
import core.serialization.*
import core.io.Console

// 失败计数（0 = 通过）；失败时打印名字定位（expect-output 只钉通过形态）
func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

// 构造拒绝期待：必须抛 FileSystemException 且 kind == .InvalidPath
func expectInvalidPath(name: String, raw: String): i32 {
    try {
        const p = Path.of(raw)
        Console.println("check-FAIL ${name}（未抛错）")
        return 1
    } catch (e: FileSystemException) {
        const k: FileSystemErrorKind = e.kind
        const ip: FileSystemErrorKind = .InvalidPath
        if (k == ip) { return 0 }
        Console.println("check-FAIL ${name}（kind 非 InvalidPath）")
        return 1
    }
}

// 词法异常期待：操作名 + 双路径携带（join 绝对参数不丢弃已有前缀）
func expectJoinAbsError(name: String, a: String, b: String): i32 {
    try {
        const r = Path.of(a).join(Path.of(b))
        Console.println("check-FAIL ${name}（未抛错）")
        return 1
    } catch (e: FileSystemException) {
        const k: FileSystemErrorKind = e.kind
        const ip: FileSystemErrorKind = .InvalidPath
        const ok = ((k == ip) and optEq(e.path, a)) and optEq(e.path2, b)
        if (ok) { return 0 }
        Console.println("check-FAIL ${name}（kind/路径携带不符）")
        return 1
    }
}

// 可空 Path 的文本投影（避免 Path? == Path? 直比：struct 覆写 hash 后
// 可空判等在 VM 触发「字段访问目标不是对象」缺口——playground/b71_probe
// 最小复现 S1/S2；非空 Path 判等不受影响，见 eq-* 用例）
func optText(p: Path?): String {
    if (p != null) { return p.text }
    return "<null>"
}

// 可空 String 与文本的相等比较（native 侧 String? == String? 直比在
// 本机对拍中不成立——可空判等 lowering 缺口族；显式判空后双侧非空
// 比较，VM/native 一致）
func optEq(a: String?, b: String): bool {
    if (a == null) { return false }
    const s: String = a
    return s == b
}

// Windows 专属拒绝形态的跨平台断言（§4.5.2「语法遵循当前平台」）：
// Windows 报 InvalidPath（expectInvalidPath 语义）；Linux 把同文本当
// 普通名称（盘符/保留设备名/尾部空格点/ADS 冒号/UNC 形均不适用）
// 构造成功且文本原样保持
func expectWinOnlyReject(name: String, raw: String): i32 {
    if (fsIsWindows()) {
        return expectInvalidPath(name, raw)
    }
    const p = Path.of(raw)
    if (p.text == raw) { return 0 }
    Console.println("check-FAIL ${name}（Linux 构造改变文本）")
    return 1
}

pub func main(): i32 {
    var fails = 0
    // 平台分支判定（path.rg internal 面 fsIsWindows：私有原语
    // host_is_windows，VM 宿主判定 / native _WIN32 编译期判定，双宿主
    // 同语义）——平台词法规则分歧的断言按宿主分支，Linux 侧对称覆盖
    //（原版仅本机 Windows 实测，Linux 分支无断言）
    const isWin = fsIsWindows()

    // ---- ① 构造拒绝（Windows 专属规则按平台分支；空/NUL 双平台）----
    fails = fails + expectInvalidPath("reject-empty", "")
    const nulText = "a" + (((0 as char).toString()) + "b")
    fails = fails + expectInvalidPath("reject-nul", nulText)
    fails = fails + expectWinOnlyReject("reject-drive-rel", "C:foo")
    fails = fails + expectWinOnlyReject("reject-drive-only", "C:")
    fails = fails + expectWinOnlyReject("reject-rooted-rel", "\\foo")
    fails = fails + expectWinOnlyReject("reject-rooted-rel-slash", "/foo")
    fails = fails + expectWinOnlyReject("reject-dev-ns-dot", "\\\\.\\pipe")
    fails = fails + expectWinOnlyReject("reject-dev-ns-q", "\\\\?\\C:\\x")
    fails = fails + expectWinOnlyReject("reject-reserved-con", "CON")
    fails = fails + expectWinOnlyReject("reject-reserved-con-ext", "con.txt")
    fails = fails + expectWinOnlyReject("reject-reserved-prn", "PRN")
    fails = fails + expectWinOnlyReject("reject-reserved-aux", "a\\AUX")
    fails = fails + expectWinOnlyReject("reject-reserved-nul", "NUL")
    fails = fails + expectWinOnlyReject("reject-reserved-com1", "COM1")
    fails = fails + expectWinOnlyReject("reject-reserved-com9-ext", "com9.txt")
    fails = fails + expectWinOnlyReject("reject-reserved-lpt9", "LPT9")
    fails = fails + expectWinOnlyReject("reject-trailing-space", "foo ")
    fails = fails + expectWinOnlyReject("reject-trailing-dot", "foo.")
    fails = fails + expectWinOnlyReject("reject-trailing-dot-mid", "a\\b.")
    fails = fails + expectWinOnlyReject("reject-trailing-space-mid", "a\\b ")
    fails = fails + expectWinOnlyReject("reject-ads-colon", "a:b")
    fails = fails + expectWinOnlyReject("reject-unc-empty-server", "\\\\")
    fails = fails + expectWinOnlyReject("reject-unc-no-share", "\\\\server")
    fails = fails + expectWinOnlyReject("reject-unc-empty-share", "\\\\server\\")

    // ---- ② 构造接受（根形按平台分支）----
    if (isWin) {
        const driveAbs = Path.of("C:\\a\\b")
        fails = fails + check("accept-drive-abs", driveAbs.isAbsolute())
        fails = fails + check("accept-mixed-sep",
            Path.of("C:/a\\b").isAbsolute())
        const uncAbs = Path.of("\\\\server\\share\\dir")
        fails = fails + check("accept-unc", uncAbs.isAbsolute())
        fails = fails + check("accept-unc-mixed-lead",
            Path.of("//server\\share").isAbsolute())
    } else {
        // Linux：'/' 根即绝对；Windows 盘符文本是普通相对名称
        fails = fails + check("linux-accept-root-abs",
            Path.of("/a/b").isAbsolute())
        fails = fails + check("linux-accept-trailing-sep",
            Path.of("/a/").isAbsolute())
        fails = fails + check("linux-drive-text-rel",
            not Path.of("C:\\base").isAbsolute())
    }
    fails = fails + check("accept-rel", not Path.of("a/b").isAbsolute())
    fails = fails + check("accept-dot", not Path.of(".").isAbsolute())
    fails = fails + check("accept-com10", Path.of("COM10").name() == "COM10")
    // 不裁剪空白：前导空白原样保留
    fails = fails + check("accept-keep-space", Path.of("  a").text == "  a")

    // ---- ③ equals/hash：文本精确、大小写敏感 ----
    fails = fails + check("eq-same-text",
        Path.of("C:\\a") == Path.of("C:\\a"))
    fails = fails + check("eq-sep-sensitive",
        (Path.of("C:\\a") == Path.of("C:/a")) == false)
    fails = fails + check("eq-case-sensitive",
        (Path.of("c:\\a") == Path.of("C:\\a")) == false)
    fails = fails + check("hash-content",
        Path.of("C:\\a").hash() == Path.of("C:\\a").hash())
    fails = fails + check("hash-eq-text",
        (Path.of("x\\y").hash() == Path.of("x\\y").hash()))

    // ---- ④ normalizeLexically（期待分隔符/根形按平台分支）----
    if (isWin) {
        fails = fails + check("norm-dot",
            Path.of("a/./b").normalizeLexically().text == "a\\b")
        fails = fails + check("norm-keep-leading-dotdot",
            Path.of("../a").normalizeLexically().text == "..\\a")
        fails = fails + check("norm-abs-no-cross-root",
            Path.of("C:\\..").normalizeLexically().text == "C:\\")
        fails = fails + check("norm-abs-pair",
            Path.of("C:\\a\\..\\b").normalizeLexically().text == "C:\\b")
        fails = fails + check("norm-collapse-seps",
            Path.of("a//b/").normalizeLexically().text == "a\\b")
        fails = fails + check("norm-unify-sep",
            Path.of("C:/a\\b").normalizeLexically().text == "C:\\a\\b")
        fails = fails + check("norm-root-only",
            Path.of("C:\\").normalizeLexically().text == "C:\\")
    } else {
        fails = fails + check("norm-dot",
            Path.of("a/./b").normalizeLexically().text == "a/b")
        fails = fails + check("norm-keep-leading-dotdot",
            Path.of("../a").normalizeLexically().text == "../a")
        fails = fails + check("norm-abs-no-cross-root",
            Path.of("/..").normalizeLexically().text == "/")
        fails = fails + check("norm-abs-pair",
            Path.of("/a/../b").normalizeLexically().text == "/b")
        fails = fails + check("norm-collapse-seps",
            Path.of("a//b/").normalizeLexically().text == "a/b")
        fails = fails + check("norm-root-only",
            Path.of("/").normalizeLexically().text == "/")
    }
    fails = fails + check("norm-pair",
        Path.of("a/b/..").normalizeLexically().text == "a")
    fails = fails + check("norm-to-dot",
        Path.of("a/b/../..").normalizeLexically().text == ".")
    fails = fails + check("norm-dot-only", Path.of(".").normalizeLexically().text == ".")

    // ---- ⑤ join / toAbsolute / relativeTo（根形与期待文本按平台）----
    var threw = false
    if (isWin) {
        fails = fails + check("join-rel",
            Path.of("C:\\a").join(Path.of("b\\c")).text == "C:\\a\\b\\c")
        // join 产物分隔符随平台（Windows '\' / Linux '/'）
        fails = fails + check("join-two-rel",
            Path.of("a").join(Path.of("b")).text == "a\\b")
        fails = fails + check("join-unc",
            Path.of("\\\\s\\sh").join(Path.of("d")).text == "\\\\s\\sh\\d")
        fails = fails + expectJoinAbsError("join-abs-arg", "a", "C:\\b")
        fails = fails + check("joinall",
            Path.of("C:\\a").joinAll(core.collections.arrayOfElements\<Path>(
                Path.of("b"), Path.of("c"))).text == "C:\\a\\b\\c")
        fails = fails + check("toabs-combine",
            Path.of("a\\b").toAbsolute(Path.of("C:\\base")).text
                == "C:\\base\\a\\b")
        fails = fails + check("toabs-keep-abs",
            Path.of("C:\\x").toAbsolute(Path.of("D:\\base")).text == "C:\\x")
        fails = fails + check("relto-down",
            Path.of("C:\\a\\b").relativeTo(Path.of("C:\\a")).text == "b")
        fails = fails + check("relto-up",
            Path.of("C:\\a").relativeTo(Path.of("C:\\a\\b")).text == "..")
        fails = fails + check("relto-up-down",
            Path.of("C:\\x\\y").relativeTo(Path.of("C:\\a")).text == "..\\x\\y")
        fails = fails + check("relto-self",
            Path.of("C:\\a").relativeTo(Path.of("C:\\a")).text == ".")
        threw = false
        try {
            const r = Path.of("D:\\a").relativeTo(Path.of("C:\\"))
        } catch (e: FileSystemException) {
            const k: FileSystemErrorKind = e.kind
            const ip: FileSystemErrorKind = .InvalidPath
            threw = (k == ip)
        }
        fails = fails + check("relto-diff-root", threw)
        // 根按规范化文本精确比较：盘符大小写不同即不同根（不做大小写折叠）
        threw = false
        try {
            const r = Path.of("C:\\a").relativeTo(Path.of("c:\\"))
        } catch (e: FileSystemException) {
            threw = true
        }
        fails = fails + check("relto-case-root", threw)
    } else {
        // Linux：'/' 根的组合与推导；Windows 盘符文本是普通相对名称——
        // 作为 toAbsolute 的 base / relativeTo 的 base 按非绝对根拒绝
        //（异平台盘符路径不按其原平台语义解释，§4.5.2 平台路径注记）
        fails = fails + check("join-rel",
            Path.of("/a").join(Path.of("b/c")).text == "/a/b/c")
        // join 产物分隔符随平台（Windows '\' / Linux '/'）
        fails = fails + check("join-two-rel",
            Path.of("a").join(Path.of("b")).text == "a/b")
        fails = fails + expectJoinAbsError("join-abs-arg", "a", "/b")
        fails = fails + check("joinall",
            Path.of("/a").joinAll(core.collections.arrayOfElements\<Path>(
                Path.of("b"), Path.of("c"))).text == "/a/b/c")
        fails = fails + check("toabs-combine",
            Path.of("a/b").toAbsolute(Path.of("/base")).text == "/base/a/b")
        fails = fails + check("toabs-keep-abs",
            Path.of("/x").toAbsolute(Path.of("/base")).text == "/x")
        threw = false
        try {
            const r = Path.of("a\\b").toAbsolute(Path.of("C:\\base"))
        } catch (e: FileSystemException) {
            const k: FileSystemErrorKind = e.kind
            const ip: FileSystemErrorKind = .InvalidPath
            threw = (k == ip)
        }
        fails = fails + check("linux-toabs-reject-win-drive", threw)
        fails = fails + check("relto-down",
            Path.of("/a/b").relativeTo(Path.of("/a")).text == "b")
        fails = fails + check("relto-up",
            Path.of("/a").relativeTo(Path.of("/a/b")).text == "..")
        fails = fails + check("relto-up-down",
            Path.of("/x/y").relativeTo(Path.of("/a")).text == "../x/y")
        fails = fails + check("relto-self",
            Path.of("/a").relativeTo(Path.of("/a")).text == ".")
        threw = false
        try {
            const r = Path.of("/a").relativeTo(Path.of("C:\\base"))
        } catch (e: FileSystemException) {
            const k: FileSystemErrorKind = e.kind
            const ip: FileSystemErrorKind = .InvalidPath
            threw = (k == ip)
        }
        fails = fails + check("linux-relto-diff-root", threw)
    }
    threw = false
    try {
        const r = Path.of("a").toAbsolute(Path.of("base"))
    } catch (e: FileSystemException) {
        const k: FileSystemErrorKind = e.kind
        const ip: FileSystemErrorKind = .InvalidPath
        threw = (k == ip)
    }
    fails = fails + check("toabs-base-must-abs", threw)
    fails = fails + check("relto-both-rel",
        Path.of("a/b").relativeTo(Path.of("a")).text == "b")
    fails = fails + check("relto-both-rel-up",
        Path.of("a").relativeTo(Path.of("a/b")).text == "..")

    // ---- ⑥ 组成部分（根形按平台分支）----
    if (isWin) {
        fails = fails + check("root-drive",
            optText(Path.of("C:\\a\\b").root()) == "C:\\")
        fails = fails + check("root-unc",
            optText(Path.of("\\\\s\\sh\\d").root()) == "\\\\s\\sh\\")
        fails = fails + check("parent-root-none",
            optText(Path.of("C:\\").parent()) == "<null>")
        fails = fails + check("parent-abs",
            optText(Path.of("C:\\a").parent()) == "C:\\")
        fails = fails + check("name-root-empty", Path.of("C:\\").name() == "")
    } else {
        fails = fails + check("linux-root-slash",
            optText(Path.of("/a/b").root()) == "/")
        fails = fails + check("linux-parent-root-none",
            optText(Path.of("/").parent()) == "<null>")
        fails = fails + check("linux-parent-abs",
            optText(Path.of("/a").parent()) == "/")
        fails = fails + check("linux-name-root-empty",
            Path.of("/").name() == "")
    }
    fails = fails + check("root-rel-none",
        optText(Path.of("a/b").root()) == "<null>")
    fails = fails + check("parent-single-rel",
        optText(Path.of("a").parent()) == ".")
    fails = fails + check("parent-rel",
        optText(Path.of("a/b").parent()) == "a")
    // 尾部分隔符不产生额外空名称
    fails = fails + check("name-trailing-sep", Path.of("a/b/").name() == "b")
    fails = fails + check("ext-tar-gz", optEq(Path.of("a.tar.gz").extension(), ".gz"))
    fails = fails + check("ext-gitignore-none",
        Path.of(".gitignore").extension() == null)
    fails = fails + check("ext-none", Path.of("x").extension() == null)
    fails = fails + check("nwe-tar-gz",
        Path.of("a.tar.gz").nameWithoutExtension() == "a.tar")
    fails = fails + check("nwe-gitignore",
        Path.of(".gitignore").nameWithoutExtension() == ".gitignore")
    fails = fails + check("nwe-plain",
        Path.of("x").nameWithoutExtension() == "x")

    // ---- ⑦ 序列化（§4.5.9：Serializable 值保存路径文本）----
    const p = Path.of("C:\\a\\b")
    const pBack = deepCopy\<Path>(p)
    fails = fails + check("ser-roundtrip-text", pBack.text == "C:\\a\\b")
    fails = fails + check("ser-roundtrip-eq", pBack == p)
    // 恢复路径校验生效：Parcel 塞空文本 → fromParcel 抛 FileSystemException
    threw = false
    try {
        const wire = p:Serializable.toParcel()
        wire.setElement\<String>("text", "")
        const r = fromParcel\<Path>(wire)
    } catch (e: FileSystemException) {
        const k: FileSystemErrorKind = e.kind
        const ip: FileSystemErrorKind = .InvalidPath
        threw = (k == ip)
    }
    fails = fails + check("ser-restore-validates", threw)

    if (fails == 0) {
        Console.println("fs-path-ok")
    }
    return fails
}
