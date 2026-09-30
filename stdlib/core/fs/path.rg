// ============================================================================
// Rigi 标准库：core.fs Path 与错误骨架（施工块 7-1，STDLIB §4.5.1 /
// §4.5.2 / §4.5.9，维护契约 D5）。
//
//   - FileSystemErrorKind（§4.5.9 错误分类）：逐字契约清单 17 类，错误
//     类别经 kind 字段携带，不靠解析错误消息判断；
//   - FileSystemException : core.IOException（§4.5.9）：kind + 操作名
//     称 + 相关路径（双路径操作的 path2）+ 可取得的原生错误码；
//     消息风格参照 exceptions.rg（动态部分字符串插值）；
//   - Path（§4.5.2）：不可变 struct，保存原始路径文本。构造不访问磁盘、
//     不要求目标存在；拒绝空文本与内嵌 NUL；当前目录显式使用 "."；不
//     裁剪空白、不展开 ~/环境变量/通配符/URI。平台语法经私有原生原语
//     rigi_host_is_windows() 判定（§4.5.9「OS 操作通过私有 native 原语
//     与 VM hook 接入」；公共平台信息 API 按 D5 继续后置——原语不是对
//     外平台查询入口）。VM（宿主进程判定）/native（_WIN32 编译期判定）
//     同一套 Rigi 实现，两形态同语义。
//
// Windows 规则（本机开发平台，全部实测；Linux 分支按契约实现但本机
// 无法实测——逐处注释标注）：
//   - 接受 '/' 与 '\' 分隔符；支持完整盘符绝对路径（C:\... / C:/...）
//     与 UNC 路径（\\server\share...，前导两个分隔符任意混用）；
//   - 拒绝依赖隐含盘符状态的形式：C:foo、C:、\foo、/foo（驱动器相对/
//     根相对路径）；
//   - 首版只接受普通文件路径：拒绝设备命名空间（\\.\、\\?\）、备用数
//     据流（名称中任何冒号）、保留设备名称（CON/PRN/AUX/NUL/COM1-9/
//     LPT1-9，按首点前的基名不区分大小写判定，如 con.txt 亦拒绝）；
//   - 拒绝会被普通路径规则改写的末尾空格/点名称（任意名称分量以空格
//     或点结尾，"." / ".." 点段本身除外）——不静默改名，报 InvalidPath；
//   - 不在库内统一施加 260 字符限制（§4.5.2），实际长度限制由后续
//     I/O 层按系统错误报告。
//
// Linux 规则（本机无法实测）：分隔符 '/'，反斜杠是普通名称字符；只
// 拒绝空文本与内嵌 NUL。名称编码限制（Linux 非法 UTF-8 名称、Windows
// 无法解码名称）不在 Path 层——Path 只保存 Rigi String 文本，编码校验
// 在 I/O 层报 InvalidNameEncoding（§4.5.2/§4.5.9，本块仅注释指明）。
//
// Path 相等与哈希（§4.5.2）：按保存的文本精确比较（String 内容语义，
// 大小写敏感、不做大小写折叠）；大小写/拼写不同的路径可指向同一文件，
// Path 相等不承担文件身份判断。hash 为 UTF-8 字节上的 FNV-1a（64 位，
// 整数运算按语言语义回绕），与 equals 同口径，VM/native 逐位一致。
//
// 词法操作（§4.5.2 逐条）：
//   - normalizeLexically()：整理分隔符（统一为平台规范分隔符）、消解
//     "." 与可配对的 "名称/.."；相对路径开头无法消解的 ".." 保留；绝对
//     路径不越过根。词法整理不访问磁盘、不保证原路径与结果访问同一
//     对象（符号链接会影响 ".." 的实际解析）；文件操作不能先隐式调用
//     它（后续 I/O 块遵守，本块注释指明）；
//   - join(other)：后续参数必须相对，绝对参数报 InvalidPath 而不丢弃
//     已有前缀；结果不隐式消解 "." / ".."；
//   - toAbsolute(base)：base 必须绝对；已绝对的输入保持自身；
//   - relativeTo(base)：只词法计算；根不同报错（根按规范化文本精确
//     比较——盘符大小写不同视为不同根，本库不做大小写折叠）；不通过
//     磁盘证明文件间关系；
//   - root()/parent()/name()/extension()/nameWithoutExtension()/
//     isAbsolute()：根没有父路径；单段相对路径的父路径为 "."；尾部
//     分隔符不产生额外空名称，实际 I/O 前也不静默删除尾部分隔符以
//     放宽原路径的目录要求（后续 I/O 块遵守）；扩展名含点，".gitignore"
//     无扩展名，"a.tar.gz" 的扩展名为 ".gz"；
//   - 不做：自动 Unicode 规范化/大小写折叠/分隔符重写（normalize 显式
//     调用除外）/".." 消解；相对路径的当前目录基准（文件操作块的内部
//     事项，§4.5.2，本块注释指明）。
//
// 序列化（§4.5.9）：Path 按普通 Serializable 值保存——表示路径文本，
// 不代表资源句柄或跨机器可用的文件身份；恢复遵守当前平台的文本规则
// （构造校验在恢复路径生效，块 6-1 TimeStamp setter 校验先例：校验放
// 在 text 的 setter，init 参数洞与序列化恢复都经 setter 通道）。
// ============================================================================
namespace core.fs

// ===== 错误分类（§4.5.9 逐字清单，顺序即契约顺序）=====

pub enum struct FileSystemErrorKind {}[
    InvalidPath,
    InvalidNameEncoding,
    NotFound,
    AlreadyExists,
    PermissionDenied,
    NotDirectory,
    IsDirectory,
    WrongType,
    DirectoryNotEmpty,
    ReadOnlyFileSystem,
    NoSpace,
    CrossDevice,
    TooManyLinks,
    PathTooLong,
    SharingViolation,
    Unsupported,
    Other
]

// ===== 文件系统异常（§4.5.9）=====

// 消息模板：动态部分（操作名/路径/原生错误码）字符串插值，风格参照
// exceptions.rg；错误类别经 kind 字段携带，消息不内嵌类别文本——
// 「不靠解析错误消息判断类别」。
priv func fsBuildMessage(op: String, p1: String?, p2: String?,
        nativeCode: i64?): String {
    var m = "文件系统操作失败：${op}"
    if (p1 != null) {
        const s: String = p1
        m = "${m}（路径：${s}）"
    }
    if (p2 != null) {
        const s: String = p2
        m = "${m}（路径 2：${s}）"
    }
    if (nativeCode != null) {
        const c: i64 = nativeCode
        m = "${m}（原生错误码：${c}）"
    }
    return m
}

pub open class FileSystemException : core.IOException {
    // 错误类别（§4.5.9 分类清单）
    pub var kind: FileSystemErrorKind
    // 操作名称（如 "Path.join"）
    pub var operation: String
    // 相关路径（无可奉告为 null）
    pub var path: String?
    // 双路径操作的第二路径（复制/移动等后续块使用）
    pub var path2: String?
    // 可取得的原生错误码（无则 null）
    pub var nativeError: i64?

    // 单路径入口（path2/nativeError 置 null）
    pub init(errorKind: FileSystemErrorKind, op: String, p1: String?) {
        message = fsBuildMessage(op, p1, null, null)
        kind = errorKind
        operation = op
        path = p1
        const none2: String? = null
        path2 = none2
        const noneN: i64? = null
        nativeError = noneN
    }

    // 全量入口（双路径操作 + 原生错误码）
    pub init(errorKind: FileSystemErrorKind, op: String, p1: String?,
            p2: String?, nativeCode: i64?) {
        message = fsBuildMessage(op, p1, p2, nativeCode)
        kind = errorKind
        operation = op
        path = p1
        path2 = p2
        nativeError = nativeCode
    }

    pub override func getMessage(): String { return message }
}

// ===== 宿主平台判定原语（§4.5.9 私有原语接入；见文件头注释）=====

@NativeLibrary("rigi_rt")
@NativeSymbol("host_is_windows")
priv native func rigi_host_is_windows(): bool

// internal：本 NS 跨文件共用的平台判定（7-6 file.rg 临时前缀校验等；
// native 声明保持 priv——原语面不跨文件泄漏）
internal func fsIsWindows(): bool { return rigi_host_is_windows() }

// ===== 内部词法助手（文件级 priv；全部不访问磁盘）=====

// 平台规范分隔符（Windows '\'，Linux '/'）
priv func fsSep(win: bool): String {
    if (win) { return "\\" }
    return "/"
}

// 单字节读取（Span 索引返回可空，界内前提由调用方保证；time.rg
// tmByteAt 同款形态）
priv func fsByteAt(bytes: Span\<u8>, offset: i64): u8 {
    return (bytes[(offset as i32)] if? (0 as u8))
}

// 分隔符判定：'/' 双平台都是分隔符；'\' 仅 Windows（Linux 反斜杠是
// 普通名称字符，§4.5.2）
priv func fsIsSepByte(b: u8, win: bool): bool {
    if (b == (47 as u8)) { return true }
    if (win and (b == (92 as u8))) { return true }
    return false
}

// 解析结果：root = 规范化根文本（null = 相对路径；否则恒以平台规范
// 分隔符结尾）；segments = 非空名称分量序列（连续/尾随分隔符不产生
// 空名称分量）
priv class FsParsedPath {
    pub var root: String?
    pub var segments: core.collections.List\<String>

    pub init(r: String?, segs: core.collections.List\<String>) {
        root = r
        segments = segs
    }
}

// 切分名称分量（[from, n) 区间；空段跳过）
priv func fsSplitSegments(rawText: String, bytes: Span\<u8>, from: i64,
        n: i64, win: bool): core.collections.List\<String> {
    const segs = new core.collections.List\<String>()
    var i = from
    while (i < n) {
        while ((i < n) and fsIsSepByte(fsByteAt(bytes, i), win)) {
            i = i + 1L
        }
        const start = i
        while ((i < n) and (not fsIsSepByte(fsByteAt(bytes, i), win))) {
            i = i + 1L
        }
        if (i > start) {
            segs.add(rawText.slice(start, i - start))
        }
    }
    return segs
}

// 文本解析（词法；前提：文本已过 fsValidate 平台规则校验）
priv func fsParse(rawText: String): FsParsedPath {
    const win = fsIsWindows()
    const bytes = rawText.toUtf8Span()
    const n = rawText.length
    if (win) {
        if ((n >= 2L) and (fsIsSepByte(fsByteAt(bytes, 0L), true)
                and fsIsSepByte(fsByteAt(bytes, 1L), true))) {
            // UNC（\\server\share...）：validate 已拒设备命名空间（\\.、
            // \\?）并保证 server/share 非空；根规范化统一为 '\' 分隔
            var i = 2L
            const serverStart = i
            while ((i < n) and (not fsIsSepByte(fsByteAt(bytes, i), true))) {
                i = i + 1L
            }
            const server = rawText.slice(serverStart, i - serverStart)
            if (i < n) { i = i + 1L }
            const shareStart = i
            while ((i < n) and (not fsIsSepByte(fsByteAt(bytes, i), true))) {
                i = i + 1L
            }
            const share = rawText.slice(shareStart, i - shareStart)
            const root = ((("\\\\" + server) + "\\") + share) + "\\"
            return new FsParsedPath(root,
                fsSplitSegments(rawText, bytes, i, n, true))
        }
        if ((n >= 2L) and (fsByteAt(bytes, 1L) == (58 as u8))) {
            // 盘符绝对路径（validate 已保证字母盘符与第三字符为分隔符；
            // 盘符大小写原样保留——不做大小写折叠）
            const drive = rawText.slice(0L, 1L)
            const root = (drive + ":") + "\\"
            return new FsParsedPath(root,
                fsSplitSegments(rawText, bytes, 2L + 1L, n, true))
        }
        // 普通相对路径（validate 已拒 \foo、C:foo 等隐含盘符形态）
        return new FsParsedPath(null,
            fsSplitSegments(rawText, bytes, 0L, n, true))
    }
    // Linux 分支（本机无法实测）：根 = "/"，反斜杠为普通名称字符
    if (fsByteAt(bytes, 0L) == (47 as u8)) {
        return new FsParsedPath("/",
            fsSplitSegments(rawText, bytes, 1L, n, false))
    }
    return new FsParsedPath(null,
        fsSplitSegments(rawText, bytes, 0L, n, false))
}

// 重组文本：root（若非 null，恒以规范分隔符结尾）+ 前 count 个分量
priv func fsBuildPrefix(root: String?, segs: core.collections.List\<String>,
        count: i64, win: bool): String {
    const sb = new core.text.StringBuilder()
    var needSep = false
    if (root != null) {
        const r: String = root
        sb.append(r)
        // 根已以分隔符结尾，首个分量前不补
    }
    var i = 0L
    while (i < count) {
        const segOpt: String? = segs.getAtIndex(i)
        if (segOpt != null) {
            const seg: String = segOpt
            if (needSep) { sb.append(fsSep(win)) }
            sb.append(seg)
            needSep = true
        }
        i = i + 1L
    }
    return sb.toString()
}

// 重组全部：空结果回退为根文本（纯根路径）或 "."（空相对路径）
priv func fsBuildAll(root: String?, segs: core.collections.List\<String>,
        win: bool): String {
    const built = fsBuildPrefix(root, segs, segs.length, win)
    if (built.length > 0L) { return built }
    if (root != null) {
        const r: String = root
        return r
    }
    return "."
}

// ===== 构造校验（§4.5.2）=====

// Windows 单名称分量校验：checkReserved 区分 UNC server/share（保留
// 设备名判定不适用）与普通段
priv func fsCheckNameWindows(seg: String, checkReserved: bool) {
    // 备用数据流/冒号语法：名称中任何冒号都拒绝（盘符冒号在段结构层
    // 已单独处理，不会到达这里）
    if (seg.contains(":")) {
        throw new FileSystemException(.InvalidPath, "Path 构造", seg)
    }
    // 末尾空格/点：会被 Windows 普通路径规则改写——不静默改名，报
    // InvalidPath（"." / ".." 点段本身除外）
    if ((seg != ".") and (seg != "..")) {
        const lastOpt: char? = seg.characterAt(seg.length - 1L)
        if (lastOpt != null) {
            const last: char = lastOpt
            if ((last == (32 as char)) or (last == (46 as char))) {
                throw new FileSystemException(.InvalidPath, "Path 构造", seg)
            }
        }
    }
    if (checkReserved and fsIsReservedWindows(seg)) {
        throw new FileSystemException(.InvalidPath, "Path 构造", seg)
    }
}

// 保留设备名称判定：首点前的基名，不区分大小写（CON/PRN/AUX/NUL/
// COM1-9/LPT1-9；如 con.txt、NUL 均拒绝）
priv func fsIsReservedWindows(seg: String): bool {
    var base = seg
    const dotOpt: i64? = seg.indexOf(".")
    if (dotOpt != null) {
        const dotIdx: i64 = dotOpt
        base = seg.slice(0L, dotIdx)
    }
    const up = base.toUpper()
    if ((up == "CON") or (up == "PRN")) { return true }
    if ((up == "AUX") or (up == "NUL")) { return true }
    if ((up.length == 4L)
            and (up.startsWith("COM") or up.startsWith("LPT"))) {
        const d = up.slice(3L)
        const db = d.toUtf8Span()
        if (((d.length == 1L) and (fsByteAt(db, 0L) >= (49 as u8)))
                and (fsByteAt(db, 0L) <= (57 as u8))) {
            return true
        }
    }
    return false
}

// Windows 全文本结构校验（前提：无内嵌 NUL、非空）
priv func fsValidateWindows(value: String, bytes: Span\<u8>, n: i64) {
    // 设备命名空间：\\.\ 与 \\?\（前导双分隔符后紧跟 '.' 或 '?'）
    if ((n >= 4L)
            and ((fsIsSepByte(fsByteAt(bytes, 0L), true)
                and fsIsSepByte(fsByteAt(bytes, 1L), true))
                and ((fsByteAt(bytes, 2L) == (46 as u8))
                    or (fsByteAt(bytes, 2L) == (63 as u8))))) {
        throw new FileSystemException(.InvalidPath,
            "Path 构造：拒绝设备命名空间路径", value)
    }
    if ((n >= 2L) and (fsIsSepByte(fsByteAt(bytes, 0L), true)
            and fsIsSepByte(fsByteAt(bytes, 1L), true))) {
        // UNC：server 与 share 必须非空（\\server\share）
        var i = 2L
        while ((i < n) and (not fsIsSepByte(fsByteAt(bytes, i), true))) {
            i = i + 1L
        }
        if (i == 2L) {
            throw new FileSystemException(.InvalidPath,
                "Path 构造：UNC 缺少服务器名", value)
        }
        const server = value.slice(2L, i - 2L)
        fsCheckNameWindows(server, false)
        if (i >= n) {
            throw new FileSystemException(.InvalidPath,
                "Path 构造：UNC 缺少共享名", value)
        }
        i = i + 1L
        const shareStart = i
        while ((i < n) and (not fsIsSepByte(fsByteAt(bytes, i), true))) {
            i = i + 1L
        }
        const share = value.slice(shareStart, i - shareStart)
        if (i == shareStart) {
            throw new FileSystemException(.InvalidPath,
                "Path 构造：UNC 共享名为空", value)
        }
        fsCheckNameWindows(share, false)
        // 其余分量按普通段校验
        while (i < n) {
            while ((i < n) and fsIsSepByte(fsByteAt(bytes, i), true)) {
                i = i + 1L
            }
            const start = i
            while ((i < n) and (not fsIsSepByte(fsByteAt(bytes, i), true))) {
                i = i + 1L
            }
            if (i > start) {
                fsCheckNameWindows(value.slice(start, i - start), true)
            }
        }
        return
    }
    if (fsIsSepByte(fsByteAt(bytes, 0L), true)) {
        // 单前导分隔符 = 根相对路径（\foo、/foo），依赖隐含盘符状态
        throw new FileSystemException(.InvalidPath,
            "Path 构造：拒绝根相对路径（\\foo 形态）", value)
    }
    if ((n >= 2L) and (fsByteAt(bytes, 1L) == (58 as u8))) {
        // 盘符形态：必须 A-Z 字母 + 绝对（第三字符为分隔符）——拒绝
        // C:foo / C: 驱动器相对路径
        const c0 = fsByteAt(bytes, 0L)
        const isAlpha = (((c0 >= (65 as u8)) and (c0 <= (90 as u8)))
            or ((c0 >= (97 as u8)) and (c0 <= (122 as u8))))
        if (not isAlpha) {
            throw new FileSystemException(.InvalidPath,
                "Path 构造：盘符必须是 A-Z 字母", value)
        }
        if (n == 2L) {
            throw new FileSystemException(.InvalidPath,
                "Path 构造：拒绝驱动器相对路径（C: 形态）", value)
        }
        if (not fsIsSepByte(fsByteAt(bytes, 2L), true)) {
            throw new FileSystemException(.InvalidPath,
                "Path 构造：拒绝驱动器相对路径（C:foo 形态）", value)
        }
        var i = 3L
        while (i < n) {
            while ((i < n) and fsIsSepByte(fsByteAt(bytes, i), true)) {
                i = i + 1L
            }
            const start = i
            while ((i < n) and (not fsIsSepByte(fsByteAt(bytes, i), true))) {
                i = i + 1L
            }
            if (i > start) {
                fsCheckNameWindows(value.slice(start, i - start), true)
            }
        }
        return
    }
    // 普通相对路径：逐段校验（冒号 = 备用数据流语法，在段校验内拒绝）
    var i = 0L
    while (i < n) {
        while ((i < n) and fsIsSepByte(fsByteAt(bytes, i), true)) {
            i = i + 1L
        }
        const start = i
        while ((i < n) and (not fsIsSepByte(fsByteAt(bytes, i), true))) {
            i = i + 1L
        }
        if (i > start) {
            fsCheckNameWindows(value.slice(start, i - start), true)
        }
    }
}

// 构造校验全入口：拒绝空文本与内嵌 NUL（双平台），再走平台结构规则。
// Path 只保存文本，名称编码（UTF-8 可解码性）限制在 I/O 层报
// InvalidNameEncoding，本层不涉及（§4.5.2，见文件头注释）。
priv func fsValidate(value: String) {
    const n = value.length
    if (n == 0L) {
        throw new FileSystemException(.InvalidPath, "Path 构造", value)
    }
    const bytes = value.toUtf8Span()
    var i = 0L
    while (i < n) {
        if (fsByteAt(bytes, i) == (0 as u8)) {
            throw new FileSystemException(.InvalidPath,
                "Path 构造：文本含内嵌 NUL", value)
        }
        i = i + 1L
    }
    if (fsIsWindows()) {
        fsValidateWindows(value, bytes, n)
    }
    // Linux 分支（本机无法实测）：除空文本/NUL 外无其他 Path 层限制
}

// ===== Path（§4.5.2 不可变 struct）=====

@core.serialization.Serializable()
pub struct Path {
    // 原始路径文本（只读对外；pub get + priv set——写通道只服务构造
    // 与序列化恢复）。setter 全路径校验：init 参数洞与 Serializable
    // 恢复都经此通道，构造校验在恢复路径同样生效（块 6-1 TimeStamp
    // setter 校验先例）。
    pub var text: String {
        pub get
        priv set(value: _) {
            fsValidate(value)
        }
    }

    pub init(_ -> text) { }

    // 由 String 显式构造（§4.5.1：文件系统入口接收 Path，不隐式把
    // 普通文本解释为路径）。不访问磁盘、不要求目标存在。
    pub static func of(text: String): Path {
        return new Path(text)
    }

    // ---- 判等与哈希（§4.5.2：按保存文本精确比较，String 内容语义；
    // 大小写/拼写不同的路径可指向同一文件，Path 相等不承担文件身份
    // 判断）----

    pub operator equals(other: Path): bool {
        return text == other.text
    }

    // FNV-1a（64 位）over UTF-8 字节：整数运算按语言语义回绕（恒等定
    // 义，VM/native 逐位一致），与 equals 同口径（内容语义）
    pub override func hash(): i64 {
        const bytes = text.toUtf8Span()
        // 14695981039346656037（= 0xcbf29ce484222325）的 i64 回绕形态
        var h: i64 = (-3750763034362895579L)
        var i = 0L
        while (i < (bytes.length as i64)) {
            const b: i64 = (fsByteAt(bytes, i)) as i64
            h = h ^ b
            h = h * (1099511628211L)
            i = i + 1L
        }
        return h
    }

    pub override func toString(): String { return text }

    // ---- 词法操作（§4.5.2）----

    pub func isAbsolute(): bool {
        return fsParse(text).root != null
    }

    // 词法整理：统一规范分隔符、消解 "." 与可配对 "名称/.."；相对开头
    // 无法消解的 ".." 保留；绝对不越根。不访问磁盘、不保证与原路径
    // 访问同一对象；文件操作不得先隐式调用它（§4.5.2，后续 I/O 块
    // 遵守）。
    pub func normalizeLexically(): Path {
        const parsed = fsParse(text)
        const win = fsIsWindows()
        const stack = new core.collections.List\<String>()
        var i = 0L
        while (i < parsed.segments.length) {
            const segOpt: String? = parsed.segments.getAtIndex(i)
            if (segOpt != null) {
                const seg: String = segOpt
                if (seg == ".") {
                    // 消解当前目录段
                } else if (seg == "..") {
                    var paired = false
                    if (stack.length > 0L) {
                        const topOpt: String? = stack.getAtIndex(
                            stack.length - 1L)
                        if (topOpt != null) {
                            const top: String = topOpt
                            if (top != "..") {
                                stack.removeAt(stack.length - 1L)
                                paired = true
                            }
                        }
                    }
                    if (not paired) {
                        if (parsed.root != null) {
                            // 绝对路径不越过根：丢弃
                        } else {
                            stack.add(seg)
                        }
                    }
                } else {
                    stack.add(seg)
                }
            }
            i = i + 1L
        }
        return new Path(fsBuildAll(parsed.root, stack, win))
    }

    // 拼接：other 必须相对，绝对参数报 InvalidPath（携带两条路径），
    // 不丢弃已有前缀；不隐式消解 "." / ".."。结果分隔符统一为平台
    // 规范形。
    pub func join(other: Path): Path {
        const parsed = fsParse(text)
        const otherParsed = fsParse(other.text)
        if (otherParsed.root != null) {
            throw new FileSystemException(.InvalidPath, "Path.join",
                text, other.text, null)
        }
        const merged = new core.collections.List\<String>()
        fsAppendAll(merged, parsed.segments)
        fsAppendAll(merged, otherParsed.segments)
        return new Path(fsBuildAll(parsed.root, merged, fsIsWindows()))
    }

    // 多变参替代形态：按序拼接（首个绝对参数即报错，同 join 语义）
    pub func joinAll(parts: Array\<Path>): Path {
        var result = this
        var i = 0L
        while (i < (parts.length as i64)) {
            const pOpt: Path? = parts[(i as i32)]
            if (pOpt != null) {
                const p: Path = pOpt
                result = result.join(p)
            }
            i = i + 1L
        }
        return result
    }

    // 以绝对 base 组合相对路径；base 非绝对报 InvalidPath；已绝对的
    // 输入保持自身。相对路径的当前目录基准属文件操作块内部事项，本
    // 层不引入进程当前目录查询（§4.5.2/D5）。
    pub func toAbsolute(base: Path): Path {
        if (isAbsolute()) { return this }
        if (not base.isAbsolute()) {
            throw new FileSystemException(.InvalidPath,
                "Path.toAbsolute：base 必须是绝对路径", base.text)
        }
        return base.join(this)
    }

    // 相对化推导（只词法）：根不同报错（规范化根文本精确比较——不
    // 做大小写折叠）；不通过磁盘证明文件间关系。
    pub func relativeTo(base: Path): Path {
        const parsed = fsParse(text)
        const baseParsed = fsParse(base.text)
        const thisHasRoot = parsed.root != null
        const baseHasRoot = baseParsed.root != null
        if ((thisHasRoot and (not baseHasRoot))
                or ((not thisHasRoot) and baseHasRoot)) {
            throw new FileSystemException(.InvalidPath,
                "Path.relativeTo：两路径根不可比（一绝对一相对）",
                text, base.text, null)
        }
        if (thisHasRoot) {
            // 结构前提：双侧根非空（thisHasRoot 已判定）；显式收窄取值
            const r1Opt: String? = parsed.root
            const r2Opt: String? = baseParsed.root
            if ((r1Opt == null) or (r2Opt == null)) {
                throw new FileSystemException(.Other,
                    "Path.relativeTo：内部解析失败", text)
            }
            const r1: String = r1Opt
            const r2: String = r2Opt
            if (r1 != r2) {
                throw new FileSystemException(.InvalidPath,
                    "Path.relativeTo：根不同", text, base.text, null)
            }
        }
        var common = 0L
        while ((common < parsed.segments.length)
                and (common < baseParsed.segments.length)) {
            const aOpt: String? = parsed.segments.getAtIndex(common)
            const bOpt: String? = baseParsed.segments.getAtIndex(common)
            // 构造保证分量非空；收窄只在判空正分支成立（guard-break 不
            // 建立收窄），故用正分支嵌套
            if (aOpt != null) {
                const a: String = aOpt
                if (bOpt != null) {
                    const b: String = bOpt
                    if (a != b) { break }
                    common = common + 1L
                } else {
                    break
                }
            } else {
                break
            }
        }
        const ups = baseParsed.segments.length - common
        if ((ups == 0L) and (common == parsed.segments.length)) {
            return new Path(".")
        }
        const sb = new core.text.StringBuilder()
        var k = 0L
        while (k < ups) {
            if (sb.length > 0L) { sb.append(fsSep(fsIsWindows())) }
            sb.append("..")
            k = k + 1L
        }
        var j = common
        while (j < parsed.segments.length) {
            const sOpt: String? = parsed.segments.getAtIndex(j)
            if (sOpt != null) {
                const s: String = sOpt
                if (sb.length > 0L) { sb.append(fsSep(fsIsWindows())) }
                sb.append(s)
            }
            j = j + 1L
        }
        return new Path(sb.toString())
    }

    // ---- 组成部分（§4.5.2）----

    // 根（规范化文本，恒以规范分隔符结尾）；相对路径无根。
    pub func root(): Path? {
        const parsed = fsParse(text)
        const rOpt: String? = parsed.root
        if (rOpt == null) { return null }
        const r: Path = Path.of(rOpt)
        const boxed: Path? = r
        return boxed
    }

    // 父路径：根没有父路径；单段相对路径的父路径为 "."；尾部分隔符
    // 不产生额外空名称（"a/b/" 的父为 "a"）。
    pub func parent(): Path? {
        const parsed = fsParse(text)
        const cnt = parsed.segments.length
        if (cnt == 0L) { return null }
        if (cnt == 1L) {
            const rootOpt: String? = parsed.root
            if (rootOpt != null) {
                const rootText: String = rootOpt
                const r: Path = Path.of(rootText)
                const boxed: Path? = r
                return boxed
            }
            const dot: Path = Path.of(".")
            const boxed2: Path? = dot
            return boxed2
        }
        const built = fsBuildPrefix(parsed.root, parsed.segments,
            cnt - 1L, fsIsWindows())
        const p: Path = Path.of(built)
        const boxed3: Path? = p
        return boxed3
    }

    // 末段名称（无末段名称——纯根路径或 "."——返回空串）
    pub func name(): String {
        const parsed = fsParse(text)
        const cnt = parsed.segments.length
        if (cnt == 0L) { return "" }
        const lastOpt: String? = parsed.segments.getAtIndex(cnt - 1L)
        if (lastOpt == null) { return "" }
        const last: String = lastOpt
        return last
    }

    // 扩展名（含点）：".gitignore" 无扩展名；"a.tar.gz" 的扩展名为
    // ".gz"；末尾点（"name."）无扩展名（Linux 合法名；Windows 已在
    // 构造拒绝）。
    pub func extension(): String? {
        const nm = name()
        const n = nm.length
        if (n == 0L) { return null }
        const dotOpt: i64? = nm.lastIndexOf(".")
        if (dotOpt == null) { return null }
        const d: i64 = dotOpt
        if (d == 0L) { return null }
        if (d == (n - 1L)) { return null }
        const extText = nm.slice(d)
        const boxed: String? = extText
        return boxed
    }

    pub func nameWithoutExtension(): String {
        const nm = name()
        const extOpt: String? = extension()
        if (extOpt == null) { return nm }
        const extText: String = extOpt
        return nm.slice(0L, nm.length - extText.length)
    }
}

// 列表拼接助手（join 用；避免暴露可变内部状态，逐元素拷贝）
priv func fsAppendAll(dst: core.collections.List\<String>,
        src: core.collections.List\<String>) {
    var i = 0L
    while (i < src.length) {
        const sOpt: String? = src.getAtIndex(i)
        if (sOpt != null) {
            const s: String = sOpt
            dst.add(s)
        }
        i = i + 1L
    }
}
