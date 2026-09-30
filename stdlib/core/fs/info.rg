// ============================================================================
// Rigi 标准库：core.fs 信息查询与链接（施工块 7-4，STDLIB §4.5.3 +
// §4.5.9 序列化条目 + §4.5.5 removeLink，维护契约 D5）。
//
// 在 7-2 原语层（fsStat/fsLstat/fsRealpath/fsUnlink/fsRename，internal
// 面）之上组装公共信息查询与链接删除入口（§4.5.1「通用 getInfo/
// tryGetInfo/exists/getRealPath/move/removeLink 为本 NS 的普通函数」
// ——7-4 实现其中信息查询与 removeLink 五个；move 已由 7-6 追加）：
//
//   - FileKind（§4.5.3 逐字四成员）：File/Directory/Link/Other。
//     Windows junction（MOUNT_POINT）归 Link，未知 reparse 类型不认定
//     为普通文件（归 Other，§4.5.3）——判定在 7-2 C/VM 侧完成（FS_KIND_*
//     归一值），本层只做值映射；
//   - FileInfo（§4.5.3 不可变值记录）：path（查询时的路径）+ kind +
//     length（普通文件字节长度，i64?——非普通文件不伪造长度，为 null）
//     + modifiedAt/accessedAt/createdAt（core.time.TimeStamp?，保留可
//     取得的亚毫秒部分）。字段访问器 pub get + priv set：写通道只服务
//     构造与序列化恢复（Path.text/TimeStamp 同款形态），对外只读；
//   - getInfo/tryGetInfo/exists（§4.5.3）：followLinks 默认 true；
//     tryGetInfo 仅将不存在转为 null、exists 仅将不存在转为 false——
//     权限不足、中间分量不是目录、链接循环及其他错误不伪装成不存在
//     （原样抛，错误类别由 7-2 归一码映射携带，不解析错误消息）；
//   - getRealPath（§4.5.3）：访问文件系统、要求目标存在（断链 →
//     NotFound）、解析链接、返回本库可表达的绝对 Path。与
//     normalizeLexically 分开（词法整理不访问磁盘、不保证访问同一
//     对象；本入口是真解析）；返回路径不是文件身份——同一文件的多个
//     硬链接仍可能得到不同路径；
//   - removeLink（§4.5.3/§4.5.5）：专门删除末段链接（不删链接目标）；
//     非链接时报类型错误（WrongType）。经 fsLstat 判别 + fsUnlink 组合；
//   - （7-6 追加）MoveMode + move（§4.5.7，本 NS 普通函数——§4.5.1 与
//     getInfo 族同列；归位见 move 注释）。
//
// 时间组装（FsStatInfo (epoch 毫秒 i64, 纳秒余量 i32) 原始对 →
// TimeStamp?，本块完成）：
//   - 毫秒位 = FS_TIME_UNAVAILABLE（i64 最小值，7-2 哨兵）= 宿主不可得
//     → null（「缺乏支持的字段为 null」；不用元数据变更时间冒充创建
//     时间、不伪造时钟精度——Linux 传统 stat 不携带 btime，C 侧按哨兵
//     不可得处理，本层照 null 组装）；
//   - 纳秒余量为负（Windows 1970 前时刻经 FILETIME 截断除法产生）时
//     规范化为 TimeStamp 形态（毫秒位恒整数毫秒 + 纳秒位 0..999999），
//     总纳秒量不变——core.time 范围定义：TimeStamp.milliseconds 任意
//     i64，nanoseconds 恒 0..999999（负时刻由负毫秒 + 正余量表达）；
//   - 规范化后仍超出 TimeStamp 纳秒位表示域的原始值：属「信息范围不
//     可表示」——与「不存在」是不同失败（§4.5.3），按范围错误抛
//     FileSystemException（kind=.Other），不窄化数值或替换为零掩盖。
//     实际不可达（7-2 C 侧已把超范围秒值归不可得哨兵），防御性保留。
//
// 语义钉死（§4.5.3，注释即契约）：信息是一次查询所得的记录——不持有
// 打开句柄、不自动更新、不承诺各字段组成文件系统的原子快照；重新查询
// 才获得新记录。followLinks=false 只查询末段链接本身（断链仍可被识别
// ——kind=.Link、length 为 null），不禁止中间路径经过链接，不构成整条
// 路径的无链接保证。
//
// TOCTOU（removeLink，首版接受并注释）：fsLstat 判别与 fsUnlink 删除
// 之间目标条目可被并发替换（判别时是链接、删除时已是普通文件，或反向
// ——后者 unlink 直接删除替换后的条目）。判别只为把「非链接」归类为
// 类型错误（§4.5.5），首版接受该竞态窗口；unlink 系统语义本身只删除
// 末段条目、不删除链接目标，删除断链按链接条目存在处理。
//
// 序列化（§4.5.9）：FileInfo 按普通 Serializable 值保存——表示「当时
// 的信息记录」，不代表资源句柄、不查询磁盘、不自动刷新；恢复得到恢复
// 表示当时的记录，字段集合完全匹配校验在恢复路径生效（含 Path 字段经
// 其 setter 通道的构造校验）。
//
// 不做（§4.5.3 明文后置）：创建符号链接/硬链接及读取链接原始目标文本
// 的入口留待后续（本块只识别、访问、删除已有链接）；目录读取属 7-5。
// 相对路径以本次调用开始时的进程当前目录为基准（原语/宿主行为，§4.5.2；
// 不新增当前目录查询 API）。
// ============================================================================
namespace core.fs

// ===== 文件类别（§4.5.3 逐字四成员，顺序即契约顺序）=====

@core.serialization.Serializable()
pub enum struct FileKind {}[
    // 普通文件
    File,
    // 目录
    Directory,
    // 符号链接与 Windows junction（MOUNT_POINT）；未知 reparse 类型不
    // 归此类（归 Other，§4.5.3「不把所有未知 reparse 类型一律认定为
    // 普通文件」——C 侧按 reparse tag 判定）
    Link,
    // 其余类别（设备、管道、socket 及未知 reparse 类型等）
    Other
]

// ===== 信息记录（§4.5.3 不可变值记录）=====

// 一次查询所得的记录：不持有打开句柄、不自动更新、不承诺原子快照；
// 重新查询才获得新记录。字段只读（priv set 写通道服务构造与序列化
// 恢复，§4.5.9）。
@core.serialization.Serializable()
pub rich struct FileInfo {
    // 查询时的路径（原样保留调用者给出的 Path 文本，不解析不规范化）
    pub var path: Path {
        pub get
        priv set(value: _) { }
    }

    // 类别（followLinks=false 时末段为链接即 .Link，断链亦然）
    pub var kind: FileKind {
        pub get
        priv set(value: _) { }
    }

    // 普通文件字节长度；非普通文件不伪造长度（目录/链接/其他为 null，
    // §4.5.3）
    pub var length: i64? {
        pub get
        priv set(value: _) { }
    }

    // 修改/访问/创建时间（保留可取得的亚毫秒部分；宿主不支持的字段为
    // null——不用元数据变更时间冒充创建时间、不伪造时钟精度）
    pub var modifiedAt: core.time.TimeStamp? {
        pub get
        priv set(value: _) { }
    }

    pub var accessedAt: core.time.TimeStamp? {
        pub get
        priv set(value: _) { }
    }

    pub var createdAt: core.time.TimeStamp? {
        pub get
        priv set(value: _) { }
    }

    pub init(_ -> path, _ -> kind, _ -> length, _ -> modifiedAt,
            _ -> accessedAt, _ -> createdAt) { }
}

// ===== 时间组装（原始对 → TimeStamp?；见文件头「时间组装」）=====

priv func fsMakeStamp(ms: i64, ns: i32, op: String,
        pathText: String?): core.time.TimeStamp? {
    // 不可得哨兵（宿主不支持该字段）：null——不伪造、不冒充（§4.5.3）
    if (ms == FS_TIME_UNAVAILABLE) { return null }
    var m = ms
    var n = ns
    if (n < 0) {
        // 原始余量为负（Windows 1970 前时刻截断除法）：规范化进位到
        // 毫秒位，总量不变（TimeStamp 纳秒位恒 0..999999，core.time
        // 范围定义）
        if (m == ((0L - 9223372036854775807L) - 1L)) {
            const wt: FileSystemErrorKind = .Other
            throw new FileSystemException(wt, op, pathText)
        }
        m = m - 1L
        n = n + 1000000
    }
    if ((n < 0) or (n > 999999)) {
        // 范围不可表示是不同于不存在的失败（§4.5.3）：不窄化数值或
        // 替换为零掩盖（实际不可达，防御性保留，见文件头）
        const wt2: FileSystemErrorKind = .Other
        throw new FileSystemException(wt2, op, pathText)
    }
    const stamp = new core.time.TimeStamp(m, n)
    const boxed: core.time.TimeStamp? = stamp
    return boxed
}

// ===== 查询核心（stat/lstat 二选一 + FileInfo 组装）=====

priv func fsQueryInfo(pathText: String, follow: bool, op: String): FileInfo {
    const st = (if (follow) { fsStat(pathText) } else { fsLstat(pathText) })
    // FS_KIND_*（C 侧钉死值，7-2）→ FileKind（契约顺序）
    var kind: FileKind = .Other
    if (st.kind == FS_KIND_FILE) {
        const k: FileKind = .File
        kind = k
    } else {
        if (st.kind == FS_KIND_DIRECTORY) {
            const k2: FileKind = .Directory
            kind = k2
        } else {
            if (st.kind == FS_KIND_LINK) {
                const k3: FileKind = .Link
                kind = k3
            }
        }
    }
    // 长度：普通文件才携带（C 侧非普通文件已置 -1，本层组装 null——
    // 不伪造长度，§4.5.3）
    var lenOpt: i64? = null
    if (st.kind == FS_KIND_FILE) {
        const v: i64 = st.length
        lenOpt = v
    }
    const m = fsMakeStamp(st.mtimeMs, st.mtimeNs, op, pathText)
    const a = fsMakeStamp(st.atimeMs, st.atimeNs, op, pathText)
    const b = fsMakeStamp(st.birthMs, st.birthNs, op, pathText)
    return new FileInfo(Path.of(pathText), kind, lenOpt, m, a, b)
}

// ===== 查询入口（§4.5.1「本 NS 的普通函数」：顶层函数而非 File/
// Directory 静态面——getInfo 族跨文件与目录两类对象，挂任一类下都会
// 误导另一半使用者；File 静态面只收文件专属入口，7-3 openRead/openWrite
// 先例）=====

// 信息查询：不存在时抛错（NotFound 类 FileSystemException）。
// followLinks=false 只查询末段链接本身（断链仍可被识别），不禁止中间
// 路径经过链接（不构成整条路径的无链接保证，§4.5.3）。信息是一次查询
// 所得的记录（见 FileInfo 契约注释）。
pub func getInfo(path: Path, followLinks: bool = true): FileInfo {
    return fsQueryInfo(path.text, followLinks, "getInfo")
}

// getInfo 的容错形态：仅将不存在转为 null；权限不足、中间分量不是
// 目录、链接循环及其他错误不伪装成不存在（原样抛，§4.5.3/§4.5.9）。
pub func tryGetInfo(path: Path, followLinks: bool = true): FileInfo? {
    var info: FileInfo? = null
    var missing = false
    try {
        info = fsQueryInfo(path.text, followLinks, "tryGetInfo")
    } catch (e: FileSystemException) {
        const nf: FileSystemErrorKind = .NotFound
        if (e.kind != nf) { throw e }
        missing = true
    }
    if (missing) { return null }
    return info
}

// 存在性查询：仅将不存在转为 false，同样不吞其他错误（§4.5.3）。
// 使用与 getInfo 相同的链接跟随选项（末段为断链时 followLinks=true
// 报 NotFound → false，followLinks=false → true）。
pub func exists(path: Path, followLinks: bool = true): bool {
    var found = true
    try {
        fsQueryInfo(path.text, followLinks, "exists")
    } catch (e: FileSystemException) {
        const nf: FileSystemErrorKind = .NotFound
        if (e.kind != nf) { throw e }
        found = false
    }
    return found
}

// ===== 真实路径（§4.5.3）=====

// 访问文件系统、要求目标存在（含断链 → NotFound）、解析链接、返回本库
// 可表达的绝对 Path。与 normalizeLexically 分开：词法整理不访问磁盘、
// 不保证原路径与结果访问同一对象；本入口是文件系统层面的真解析。返回
// 路径不是文件身份——同一文件的多个硬链接仍可能得到不同路径；Path
// 相等也不承担文件身份判断（§4.5.2）。
pub func getRealPath(path: Path): Path {
    return Path.of(fsRealpath(path.text))
}

// ===== removeLink（§4.5.3/§4.5.5）=====

// 专门删除末段链接（不删除链接目标）；非链接时报类型错误
// （FileSystemException kind=WrongType）；不存在报 NotFound。判别经
// fsLstat（不跟随末段）；删除断链按链接条目存在处理。TOCTOU 窗口
// （判别与删除之间条目被并发替换）首版接受，见文件头注释。
pub func removeLink(path: Path) {
    const st = fsLstat(path.text)
    if (st.kind != FS_KIND_LINK) {
        const wt: FileSystemErrorKind = .WrongType
        throw new FileSystemException(wt, "removeLink", path.text)
    }
    fsUnlink(path.text)
}

// ===== move（§4.5.7，施工块 7-6）=====

// 移动模式（§4.5.7 两成员，顺序即契约顺序）
pub enum struct MoveMode {}[
    // 默认：系统不替换保证——遇任何已有目标条目（含断链）报错；
    // 保证来自系统操作本身（renameat2 RENAME_NOREPLACE / MoveFileExW
    // 不带 REPLACE_EXISTING），不能用 exists + 覆盖 rename 模拟
    //（§4.5.7）；宿主或文件系统不能提供保证时报 Unsupported
    NoReplace,
    // 覆盖已有目标——仅限文件/链接条目，不允许目录覆盖或目录合并
    Replace
]

// 移动/重命名（§4.5.7 逐条，§4.5.1 的本 NS 普通函数——与 getInfo 族
// 同列，跨文件/目录/链接三类对象，挂任一静态面都会误导另一半使用者，
// info.rg 先例）：用系统移动/重命名能力（fsRename 原语，7-2），支持
// 文件、目录和链接条目；源或目标末段为链接时操作条目本身，不跟随它
// 替换目标内容（系统 rename 语义）。跨文件系统报 CrossDevice，不自动
// 退化成复制再删除。移动目录要求目标不存在（两种模式均可；Replace
// 对目录目标的覆盖被系统/公共层拒绝——POSIX rename 会静默替换空目
// 录，由 7-2 预检拦截）。复制与移动不是跨进程事务，失败不普遍承诺
// 所有路径和内容完全未变（§4.5.7 注释钉死）。
pub func move(source: Path, destination: Path,
        mode: MoveMode = .NoReplace) {
    const mReplace: MoveMode = .Replace
    fsRename(source.text, destination.text, (mode == mReplace))
}
