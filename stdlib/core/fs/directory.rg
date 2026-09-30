// ============================================================================
// Rigi 标准库：core.fs 目录读取 + 创建/删除（施工块 7-5，STDLIB §4.5.4 +
// §4.5.5，维护契约 D5）。
//
// 在 7-2 原语层（fsDirOpen/fsDirRead/FsDirEntry/DirHandle/fsMkdir/fsRmdir/
// fsUnlink/fsLstat/fsRealpath，internal 面）之上组装公共目录入口（§4.5.1
// 「File/Directory 提供文件和目录操作」的 Directory 侧；File 静态面见
// 7-3 file.rg，删除文件/链接条目的 File.delete 族也归 file.rg——目录
// 静态面只收目录专属入口）：
//
//   - DirectoryReader（§4.5.4 逐条）：local 类，实现 IDisposable；
//     read(): DirectoryEntry? 每次取得一个条目，null 表示结束——结束后
//     持续返回 null，重新扫描须重新打开。创建者经 using/dispose 管理
//     （结束和失败不免除关闭责任）；同实例不支持并发或重入（local
//     设计契约，§4.4）。不把目录句柄隐含在普通 IEnumerable 中：现有
//     for-each 协议只规定 iterate/moveNext/current，不自动关闭枚举
//     资源（SYNTAX §6 控制流）——DirectoryReader 的生命周期显式呈现，
//     不能依赖 GC 或循环正常结束来清理句柄；
//   - DirectoryEntry（§4.5.4 不可变值记录）：name（条目名称）+ path
//     （基于本次打开基准得到的绝对路径）+ kindHint（可空 FileKind 提示
//     ——可能不可用或过时，完整信息由 getInfo 另行查询）；
//   - 语义逐条（§4.5.4，注释即契约）：返回顺序不保证排序、不递归、
//     不返回 `.`/`..`、包含隐藏项（由 7-2 原语双端保证）；目录读取不是
//     原子快照，外部修改期间不保证条目集合完整性；读取或名称解码失败
//     后进入故障状态，只允许 dispose；关闭/故障后 read 抛
//     IllegalStateException；关闭失败后仍进入关闭状态，重复 dispose
//     不重试（幂等支架形态参照 core.io 流基类）；名称解码失败
//     （Linux 非法 UTF-8 名称/Windows 无法解码的名称）报
//     InvalidNameEncoding 类错误（不替换不跳过，§4.5.2——归一码映射在
//     7-2 fsErrKind）；
//   - Directory 静态入口：open 打开目录 reader；list 整体收集（上限
//     超出报错不截断，中途失败不返回部分列表）；create/createAll/delete/
//     deleteIfExists 创建与删除（§4.5.5）。
//
// 条目绝对路径的组装（「基于本次打开基准得到的绝对路径」）：open 时把
// 打开基准固定为绝对形态——相对路径以本次调用开始时的进程当前目录为
// 基准（§4.5.2；内部取得该基准不新增当前目录查询 API，经 getRealPath
// 的原语面 fsRealpath(".") 取 CWD 绝对文本，"." 恒存在），再用
// toAbsolute 词法组合、不解析链接——条目路径保持调用者给出的打开基准
// 拼写（经链接打开时条目路径仍在链接路径之下，不落到目标真实路径；
// realpath 的链接解析面归 getRealPath）。之后每个条目 path =
// 基准.join(名称)（名称恒为相对单段——目录条目不含分隔符）。
//
// 宿主差异在公共层统一（本块实测：VM 侧 mkdir 原语 Directory.
// CreateDirectory 对已存在目录静默成功、自动补建缺失父级；native 侧
// CreateDirectoryW 报 AlreadyExists/NotFound——§4.5.5「只创建末级，父级
// 须存在，目标已存在时报错」的语义由本层显式判别保证，不依赖宿主原语
// 的错误报告；VM 侧 rmdir 原语对非空目录的归一类别与 native 不同
// [Other vs DirectoryNotEmpty]——「非空目录报 DirectoryNotEmpty」由本层
// 判空保证）。判别与系统调用之间的 TOCTOU 竞态窗口首版接受并注释
//（info.rg removeLink 同口径）。
//
// 不做（§4.5.4/§4.5.5 明文钉死）：递归删除、回收站、自动移除只读属性
// 再重试不提供；删除语义不因平台对目录链接使用不同系统调用而变成递归
// 操作；首版不新增 chmod/ACL/所有者/可执行位管理，普通创建使用宿主
// 权限规则（Linux 目录 0777 受 umask，Windows 正常继承安全描述符——
// 7-2 FS_MODE_DIRECTORY）；复制/移动属 7-6（file.rg copy、info.rg
// move），临时目录已由 7-6 追加（Directory.createTemporary）。
// ============================================================================
namespace core.fs

// ===== 目录条目（§4.5.4 不可变值记录，§4.5.9 可序列化）=====

// 一次目录读取所得的条目记录：不持有系统资源（句柄在 DirectoryReader）。
// name 为条目名称（不含路径部分）；path 为基于本次打开基准得到的绝对
// 路径（组装方式见文件头）；kindHint 为可空种类提示——可能不可用或
// 过时（打开后条目可被外部改动），完整信息由 getInfo 另行查询。
@core.serialization.Serializable()
pub rich struct DirectoryEntry {
    // 条目名称（不含 `.`/`..`——读取端不产出）
    pub var name: String {
        pub get
        priv set(value: _) { }
    }

    // 基于本次打开基准的绝对路径
    pub var path: Path {
        pub get
        priv set(value: _) { }
    }

    // 可空种类提示：宿主本次不可得时为 null（不伪造）；提示可能过时，
    // 完整信息由 getInfo 另行查询（§4.5.4）
    pub var kindHint: FileKind? {
        pub get
        priv set(value: _) { }
    }

    pub init(_ -> name, _ -> path, _ -> kindHint) { }
}

// ===== 目录读取器（§4.5.4）=====

// local 类；同一实例不支持并发或重入（§4.4 设计契约）。生命周期显式
// 呈现：创建者（Directory.open 的调用方）经 using/dispose 管理关闭
// 责任——读到结束、故障或提前离开都不免除关闭责任；for-each 协议
// （iterate/moveNext/current）不自动关闭枚举资源，本类型不提供
// IEnumerable 形态（§4.5.4）。
//
// 三态状态机（i32 投影，core.io 流基类同款形态）：0 正常 / 1 故障 /
// 2 关闭。read 在关闭或故障态抛 IllegalStateException（§4.5.4）；实际
// 读取或名称解码失败先 markFaulted 再抛（FileSystemException 经 7-2
// 归一码映射携带 kind——InvalidNameEncoding 属其中，不替换不跳过）；
// 故障后只允许 dispose。到达结束后持续返回 null（固定结束状态，与
// 文件流 EOF 不粘滞语义不同，§4.5.6），状态仍为正常、仍须 dispose。
// dispose 幂等：先置关闭态再释放句柄，重复 dispose 直接返回不重试；
// 关闭失败后仍进入关闭状态（§4.5.4；句柄释放经 7-2 NativeRc，close
// 错误不上报——7-2 口径）。
pub class DirectoryReader implements core.IDisposable {
    // 所持目录句柄（fsDirOpen 所建，本 reader 唯一拥有）
    priv const handle: DirHandle
    // 打开基准的绝对形态（open 时固定；条目 path 由此 join 名称）
    priv const basePath: Path
    // 已到达结束（此后 read 持续 null，重扫须重新打开，§4.5.4）
    priv var exhausted: bool = false
    // 状态三态 i32 投影：0 正常 / 1 故障 / 2 关闭
    priv var state_: i32 = 0

    internal init(_ -> handle, _ -> basePath) { }

    // 取得下一个条目：null 表示结束（结束后持续 null）；顺序不保证
    // 排序、不递归、不返回 `.`/`..`、包含隐藏项（7-2 原语双端保证）。
    // 目录读取不是原子快照：外部修改期间不保证条目集合完整性（§4.5.4）。
    // 关闭/故障后抛 IllegalStateException；实际读取或名称解码失败置
    // 故障后原样抛
    pub func read(): DirectoryEntry? {
        if (state_ == 2) {
            throw new core.IllegalStateException("目录读取器已关闭")
        }
        if (state_ == 1) {
            throw new core.IllegalStateException(
                "目录读取器已进入故障状态，只允许清理")
        }
        if (exhausted) { return null }
        var raw: FsDirEntry? = null
        try {
            raw = fsDirRead(handle)
        } catch (e: core.Exception) {
            // 读取或名称解码失败（InvalidNameEncoding 等）→ 故障态，
            // 只允许 dispose；不替换不跳过（§4.5.2/§4.5.4）
            state_ = 1
            throw e
        }
        if (raw == null) {
            exhausted = true
            return null
        }
        const entry: FsDirEntry = raw
        // 可空种类提示：宿主本次不可得 → null（不伪造）；FS_KIND_*
        // 归一值 → FileKind（契约顺序，info.rg 同款映射）
        var hint: FileKind? = null
        if (entry.kindAvailable) {
            var k: FileKind = .Other
            if (entry.kindHint == FS_KIND_FILE) {
                const f: FileKind = .File
                k = f
            } else {
                if (entry.kindHint == FS_KIND_DIRECTORY) {
                    const d: FileKind = .Directory
                    k = d
                } else {
                    if (entry.kindHint == FS_KIND_LINK) {
                        const l: FileKind = .Link
                        k = l
                    }
                }
            }
            const boxedHint: FileKind? = k
            hint = boxedHint
        }
        const made = new DirectoryEntry(entry.name,
            basePath.join(Path.of(entry.name)), hint)
        const boxed: DirectoryEntry? = made
        return boxed
    }

    // 幂等关闭（core.io 流基类支架形态）：先置关闭态再释放句柄；重复
    // dispose 直接返回，不重试（§4.5.4「关闭失败后仍进入关闭状态」）。
    // 关闭责任在创建者：读到结束、故障或提前离开都必须 dispose
    pub override func dispose() {
        if (state_ == 2) {
            return
        }
        state_ = 2
        handle.dispose()
    }
}

// ===== Directory 静态入口（§4.5.1：文件系统入口接收 Path；与 File
// 同风格的静态入口类，只收目录专属入口）=====

pub class Directory {
    // 打开目录读取器（契约 §4.5.4 拼写 Directory.open；「open」是语言
    // 保留修饰符关键字，声明名位被 M31 拦截无法编译——过渡拼写
    // openReader 与 File.openRead/openWrite 的 open 前缀先例一致，待
    // 裁决后定稿，见块报告「待裁决问题」）。diropen 打开即验证：不存在
    // 报 NotFound 类、末段非目录报 NotDirectory 类 FileSystemException
    //（§4.5.9 经 7-2 错误映射）。条目 path 的打开基准在本次调用固定为
    // 绝对形态（相对路径以本次调用开始时的进程当前目录为基准，§4.5.2
    // ——组装方式见文件头；不新增当前目录查询 API）。返回的 reader 由
    // 调用方 using/dispose 管理（§3.2：创建/打开资源的一方负责关闭）
    pub static func openReader(path: Path): DirectoryReader {
        var baseAbs: Path = path
        if (not path.isAbsolute()) {
            baseAbs = path.toAbsolute(Path.of(fsRealpath(".")))
        }
        const handle = fsDirOpen(path.text)
        return new DirectoryReader(handle, baseAbs)
    }

    // 整体目录列表（§4.5.4）：内部打开 reader 收集为独立 List 并关闭。
    // maxEntries 为条目数上限——超出时报错，不截断返回；-1（默认）
    // 表示不设上限，仍受容器容量约束，大目录使用 DirectoryReader；
    // 其他负值非法（OutOfBoundException）。中途失败不返回部分列表
    // （先关闭 reader 再原样抛）。list 不因整体读取而新增快照或排序
    // 承诺（收集语义逐条同 DirectoryReader.read：非原子、不排序、
    // 不递归、含隐藏项）
    pub static func list(path: Path,
            maxEntries: i64 = -1L): core.collections.List\<DirectoryEntry> {
        if (maxEntries < -1L) {
            throw new core.OutOfBoundException(
                "list 上限非法：${maxEntries}（仅 -1 表示不设上限）")
        }
        const reader = openReader(path)
        const result = new core.collections.List\<DirectoryEntry>()
        try {
            var done = false
            while (not done) {
                const got = reader.read()
                if (got == null) {
                    done = true
                } else {
                    const entry: DirectoryEntry = got
                    if ((maxEntries >= 0L)
                            and (result.length == maxEntries)) {
                        // 再收一个就会超出：报错，不截断返回（§4.5.4）
                        throw new core.OutOfBoundException(
                            "list 条目数超出上限：${maxEntries}")
                    }
                    result.add(entry)
                }
            }
        } catch (e: core.Exception) {
            // 中途失败（含超限）不返回部分列表：先关闭再抛（关闭责任
            // 不因失败免除，§4.5.4）
            reader.dispose()
            throw e
        }
        reader.dispose()
        return result
    }

    // 创建末级目录（§4.5.5）：只创建末级，父级须存在，目标已存在时
    // 报错。宿主 mkdir 原语两端语义不同（VM 静默已存在/自动补建父级，
    // native 报 AlreadyExists/NotFound——见文件头），公共语义由本层
    // 显式判别保证：
    //   - 父级缺失 → NotFound、父级存在但非目录 → NotDirectory（stat
    //     前置判别；父级为目录链接按正常路径规则解析——stat 跟随）；
    //   - 目标已存在（末段条目本身存在，含断链）→ AlreadyExists
    //    （lstat 不跟随末段判别——创建不替换不跟随末段条目）；
    //   - 判别与创建之间的 TOCTOU 竞态窗口首版接受（判别后条目被并发
    //     创建：VM 静默成功/native 报 AlreadyExists——info.rg removeLink
    //     同口径）。
    // 权限使用宿主规则：Linux 目录 0777 受 umask，Windows 正常继承
    // 安全描述符（7-2 FS_MODE_DIRECTORY；首版不新增 chmod/ACL/所有者/
    // 可执行位管理，§4.5.5）
    pub static func create(path: Path) {
        const parentOpt: Path? = path.parent()
        if (parentOpt != null) {
            const parent: Path = parentOpt
            const ps = fsStat(parent.text)
            if (ps.kind != FS_KIND_DIRECTORY) {
                const nd: FileSystemErrorKind = .NotDirectory
                throw new FileSystemException(nd, "Directory.create",
                    parent.text)
            }
        }
        var existsEntry = false
        try {
            fsLstat(path.text)
            existsEntry = true
        } catch (e: FileSystemException) {
            const nf: FileSystemErrorKind = .NotFound
            if (e.kind != nf) { throw e }
        }
        if (existsEntry) {
            const ae: FileSystemErrorKind = .AlreadyExists
            throw new FileSystemException(ae, "Directory.create", path.text)
        }
        fsMkdir(path.text, FS_MODE_DIRECTORY)
    }

    // 逐级创建（§4.5.5）：允许已有目录并创建缺失父级；路径中遇到普通
    // 文件报错（AlreadyExists——与 create 的「目标已存在」同口径；判别
    // 经 stat 跟随末段，已有目录链接按正常路径规则解析为目录）；中途
    // 失败保留已创建的目录，不自动回滚（递归自顶向下判别、自底向上
    // 创建，失败原样抛）。TOCTOU 竞态窗口首版接受（判别后被并发创建：
    // VM mkdir 静默成功按已有目录语义成立/native 报 AlreadyExists）
    pub static func createAll(path: Path) {
        var kind: i32 = 0 - 1
        var found = false
        try {
            kind = fsStat(path.text).kind
            found = true
        } catch (e: FileSystemException) {
            const nf: FileSystemErrorKind = .NotFound
            if (e.kind != nf) { throw e }
        }
        if (found) {
            if (kind == FS_KIND_DIRECTORY) {
                // 已有目录（含指向目录的链接——跟随解析）→ 允许
                return
            }
            // 已有非目录条目（普通文件/指向文件的链接/断链等）挡路
            const ae: FileSystemErrorKind = .AlreadyExists
            throw new FileSystemException(ae, "Directory.createAll",
                path.text)
        }
        // 缺失：先造父级（"." 恒为已存在目录——stat 命中，递归止于根/
        // 当前目录；根没有父路径时不再向上）
        const parentOpt: Path? = path.parent()
        if (parentOpt != null) {
            const parent: Path = parentOpt
            createAll(parent)
        }
        fsMkdir(path.text, FS_MODE_DIRECTORY)
    }

    // 删除真实空目录（§4.5.5）。末段是链接时报 NotDirectory：只删除
    // 真实空目录，不跟随末段链接删除其目标，也不在本入口删除链接条目
    // 本身（那是 removeLink/File.delete 的职责）——Windows
    // RemoveDirectoryW 与 POSIX rmdir 对链接的系统行为不同（删链接
    // 本身 / ENOTDIR），lstat 前置判别统一为 NotDirectory，删除语义不
    // 因平台对目录链接使用不同系统调用而改变、更不因此变成递归操作
    //（§4.5.5）。非空目录报 DirectoryNotEmpty：VM/native 对 rmdir 非空
    // 的归一类别不同（实测 VM Other / native DirectoryNotEmpty——见
    // 文件头），由本层判空统一（即开即读即关，不长期持有判别句柄）。
    // 判别与删除之间的 TOCTOU 竞态窗口首版接受（并发塞入条目后宿主
    // 类别随宿主不同；并发删除则 NotFound）
    pub static func delete(path: Path) {
        const st = fsLstat(path.text)
        if (st.kind == FS_KIND_LINK) {
            const nd: FileSystemErrorKind = .NotDirectory
            throw new FileSystemException(nd, "Directory.delete", path.text)
        }
        const dh = fsDirOpen(path.text)
        var empty = true
        try {
            const first = fsDirRead(dh)
            if (first != null) { empty = false }
        } catch (e: core.Exception) {
            // 读取失败（含名称解码 InvalidNameEncoding）是独立失败：
            // 照常关闭句柄后原样抛，不伪装成空/非空（§4.5.9）
            dh.dispose()
            throw e
        }
        dh.dispose()
        if (not empty) {
            const dne: FileSystemErrorKind = .DirectoryNotEmpty
            throw new FileSystemException(dne, "Directory.delete", path.text)
        }
        fsRmdir(path.text)
    }

    // 删除的容错形态（§4.5.5）：返回是否实际删除，只把不存在转为
    // false；链接（NotDirectory）、非空（DirectoryNotEmpty）、权限等
    // 其他失败原样抛，不吞掉。对断链的删除按链接条目存在处理（lstat
    // 判别不跟随——断链是已存在条目，走链接分支报 NotDirectory）
    pub static func deleteIfExists(path: Path): bool {
        var removed = true
        try {
            delete(path)
        } catch (e: FileSystemException) {
            const nf: FileSystemErrorKind = .NotFound
            if (e.kind != nf) { throw e }
            removed = false
        }
        return removed
    }

    // 原子创建临时目录（§4.5.8，施工块 7-6）：显式接收所在目录，创建
    // 新目录并返回路径。prefix 校验与名称组装同 File.createTemporary
    // （fsValidateTempPrefix/fsTempName，file.rg——分隔符/NUL 报
    // InvalidPath，最终名称过 Path 规则；随机部分防撞选名）。「已有
    // 同名项不能被覆盖」：VM 侧 mkdir 原语对已存在目录静默成功（文件
    // 头实测，Directory.create 同款差异），预检 + mkdir 的
    // AlreadyExists 重试双重保证（fsTryCreateTempDir：冲突返回假，
    // 上层重选名重试；上限 100 次防死循环，耗尽报 Other——随机名称
    // 正常不可达）。判别与创建之间的 TOCTOU 窗口首版接受（info.rg
    // removeLink 同口径）。调用者负责删除临时目录；关闭/回收不自动
    // 删除（§4.5.8）
    pub static func createTemporary(directory: Path, prefix: String): Path {
        fsValidateTempPrefix(prefix)
        const rnd = new core.math.Random()
        var attempt: i32 = 0
        var made: Path? = null
        while (made == null) {
            if (attempt >= 100) {
                const oc: FileSystemErrorKind = .Other
                throw new FileSystemException(oc,
                    "Directory.createTemporary", directory.text)
            }
            const candidate = directory.join(
                Path.of(fsTempName(prefix, rnd)))
            if (fsTryCreateTempDir(candidate.text)) {
                made = candidate
            }
            attempt = (attempt + 1)
        }
        // 守卫收窄（循环退出蕴含非空；guard-throw 建立收窄，file.rg
        // File.createTemporary 同款形态）
        if (made == null) {
            const oc2: FileSystemErrorKind = .Other
            throw new FileSystemException(oc2,
                "Directory.createTemporary", directory.text)
        }
        const target: Path = made
        return target
    }
}

// 临时目录原子创建单次尝试：预检已存在条目（含断链——lstat 不跟随
// 末段）返回假；mkdir 报 AlreadyExists 也返回假（native 侧/竞态窗口
// 内被并发创建），其他错误原样抛。真 = 创建成功
internal func fsTryCreateTempDir(pathText: String): bool {
    var existsEntry = false
    try {
        fsLstat(pathText)
        existsEntry = true
    } catch (e: FileSystemException) {
        const nf: FileSystemErrorKind = .NotFound
        if (e.kind != nf) { throw e }
    }
    if (existsEntry) { return false }
    try {
        fsMkdir(pathText, FS_MODE_DIRECTORY)
        return true
    } catch (e: FileSystemException) {
        const ae: FileSystemErrorKind = .AlreadyExists
        if (e.kind != ae) { throw e }
        return false
    }
}
