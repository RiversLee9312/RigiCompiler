// ============================================================================
// Rigi 标准库：core.fs 文件流（施工块 7-3，STDLIB §4.5.6 + §4.4，D5）。
//
// 在 7-2 原语层（primitives.rg internal 面）之上组装公共文件流 API：
//
//   - FileWriteMode（§4.5.6 五模式逐字）：OpenExisting/CreateNew/
//     CreateOrTruncate/OpenOrCreate/Append。openWrite 必须显式传入；
//     CreateNew 经原语 FS_F_CREATE_NEW 落到系统仅创建机制
//     （Windows CREATE_NEW / Linux O_CREAT|O_EXCL）——已有条目不被
//     覆盖的保证来自打开调用本身，不是「先查询再用覆盖模式打开」；
//   - File 静态入口类（§4.5.1「File/Directory 提供文件和目录操作」的
//     File 侧；Directory 属 7-4）：openRead 只读打开已有文件；openWrite
//     五模式打开。不自动创建父目录（§4.5.6）。首版分别返回输入流或
//     输出流，不提供同时读写的双向文件流；
//   - （7-6 追加）FileCopyMode + File.copy 复制（§4.5.7：文件流+pipe
//     组合，截断前按系统文件身份拒绝自复制——原语 fsSameIdentity，
//     primitives.rg 7-6 段；归位 File 静态面：复制属文件侧操作）；
//   - 具体文件流三形态（§4.4 公开面 + §4.5.6 文件专用能力由具体类型
//     体现，不给通用流或 ISeekableStream 增加文件成员）：
//       FileInputStream   —— 只读，实现 ISeekableStream，实时 getLength；
//       FileOutputStream  —— 非追加写，实现 ISeekableStream +
//                            getLength/setLength；
//       FileAppendStream  —— 追加写（.Append 唯一返回形态），不实现
//                            ISeekableStream（无任意写入定位/setLength），
//                            仅 getLength。追加与非追加取两个具体类而非
//                            「一类 + 构造参数」：接口能力由具体返回类型
//                            体现（§4.5.6 明文），追加形态在类型层面就
//                            没有定位面，而不是运行时抛错的面。
//     openRead/openWrite 的静态返回类型是 InputStream/OutputStream
//     （§4.4 通用面），ISeekableStream/setLength 等能力经具体类型转换
//     触达；追加流转 ISeekableStream 失败（CastException）即契约的
//     类型层体现。
//
// 契约要点（§4.5.6，实现注释逐条锚定）：
//   - 非追加模式初始位置 0，除明确截断外保留已有内容；定位允许到末尾
//     之后（定位不扩容，后续写入空隙补零——Windows SetEndOfFile 不定义
//     扩展区内容，零填充由 7-2 C 侧实现补齐，本层只依赖原语契约）；
//   - setLength：缩短截断、增长补零、成功后游标保持不变（即使已在新
//     末尾之后）；不承诺预分配物理空间，也不隐式替代 flush；
//   - EOF 语义：读到当前末尾返回 0，不永久关闭流、不缓存 EOF——之后
//     再次读取可以看到新增内容（与目录 reader 的固定结束状态不同）；
//   - Append：不存在则创建；系统追加机制（FILE_APPEND_DATA / O_APPEND）
//     使每次写入始终到达当时末尾，不是打开时定位一次；不承诺跨进程
//     一次大 write 或整行的原子性；
//   - flush()：等待全部写入与系统持久化刷新（FlushFileBuffers / fsync，
//     7-2 挂起两段式，不占 Compute Worker）；仅库缓冲提交或系统缓存
//     写入不满足契约。文件内容的持久化刷新不构成文件替换事务，也不
//     自动刷新目录项；
//   - 共享与身份：打开按所持句柄操作，不因路径条目改变而自动重新打开
//     其他文件；默认允许其他进程读写及平台支持的重命名/删除（7-2 C 侧
//     FILE_SHARE_READ|WRITE|DELETE / 无独享强制），不隐式提供独占锁或
//     内容快照；默认跟随链接打开；
//   - 路径基准：相对路径以本次调用开始时的进程当前目录为基准（原语/
//     宿主按调用时进程当前目录解析）；同一次多路径操作固定同一基准
//     ——多路径操作本身属 7-6，本块全是单路径操作；内部取得该基准不
//     意味着新增当前目录查询 API（§4.5.2）。
//
// 三态与收尾（§4.4）：状态闸门/范围校验/幂等 dispose 支架在抽象基类，
// 本层只实现 read/write/flush/seek 面与 disposeCore 钩子。实际 I/O 失败
// 先 markFaulted() 再抛（FileSystemException 经 7-2 错误映射携带 kind）；
// 参数校验失败（负位置/负长度/越界范围）抛 OutOfBoundException 且不置
// 故障。输出流故障投影 broken 记录「发生过实际 I/O 失败」：故障收尾不
// 重写（不再 flush）可能已部分提交的数据，但句柄照常释放。local 类型
// 不支持并发/重入（§4.4 设计契约）。
// ============================================================================
namespace core.fs

// ===== 写模式（§4.5.6 五模式，顺序即契约顺序）=====

pub enum struct FileWriteMode {}[
    // 只写已有文件，不截断
    OpenExisting,
    // 仅创建新文件（系统仅创建机制，已有条目报 AlreadyExists 不覆盖）
    CreateNew,
    // 创建或截断
    CreateOrTruncate,
    // 打开或创建，不截断
    OpenOrCreate,
    // 追加（不存在则创建；系统追加机制，每次写入到达当时末尾）
    Append
]

// 模式 → 原语 open 标志位（FS_F_* 见 primitives.rg，与 rigi_rt fs.c
// RIGI_FS_F_* 逐位一致；组合语义见 rigi_fs_open 头注释——映射落点按
// 7-2 约定在本层）
priv func fsModeFlags(mode: FileWriteMode): i32 {
    const mOpenExisting: FileWriteMode = .OpenExisting
    const mCreateNew: FileWriteMode = .CreateNew
    const mCreateOrTruncate: FileWriteMode = .CreateOrTruncate
    const mOpenOrCreate: FileWriteMode = .OpenOrCreate
    if (mode == mOpenExisting) { return FS_F_WRITE }
    if (mode == mCreateNew) { return (FS_F_WRITE | FS_F_CREATE_NEW) }
    if (mode == mCreateOrTruncate) {
        return ((FS_F_WRITE | FS_F_CREATE) | FS_F_TRUNCATE)
    }
    if (mode == mOpenOrCreate) { return (FS_F_WRITE | FS_F_CREATE) }
    // Append：写 + 系统追加 + 不存在则创建（§4.5.6「追加模式不存在则
    // 创建」）；access 映射上 APPEND 优先（rigi_fs_open：带 APPEND 即
    // FILE_APPEND_DATA/O_APPEND）
    return ((FS_F_WRITE | FS_F_APPEND) | FS_F_CREATE)
}

// ===== 复制模式（§4.5.7 两成员，顺序即契约顺序）=====

pub enum struct FileCopyMode {}[
    // 默认：只创建新目标——已有条目（含断链）报 AlreadyExists，
    // 保证来自系统仅创建机制（FS_F_CREATE_NEW，§4.5.6 CreateNew 同源）
    CreateNew,
    // 打开或创建目标，已有文件写入前截断（跟随目标链接并写入其目标
    // 文件，不替换链接条目）
    Overwrite
]

// ===== File 静态入口（§4.5.1：文件系统入口接收 Path；不自动创建父
// 目录；失败经 FileSystemException 按 kind 分类，§4.5.9）=====

pub class File {
    // 只读打开已有文件（不存在抛 NotFound 类 FileSystemException）。
    // 返回具体 FileInputStream（静态类型 InputStream——§4.4 通用面；
    // 定位/长度能力经具体类型触达）
    pub static func openRead(path: Path): core.io.InputStream {
        const handle = fsOpen(path.text, FS_F_READ, 0)
        return new FileInputStream(handle)
    }

    // 按模式打开写流（mode 必须显式传入——五模式语义差异大，无默认值）。
    // Append 返回 FileAppendStream，其余返回 FileOutputStream：接口能力
    // 由具体返回类型体现（§4.5.6）
    pub static func openWrite(path: Path, mode: FileWriteMode):
            core.io.OutputStream {
        const handle = fsOpen(path.text, fsModeFlags(mode), FS_MODE_FILE)
        const appendCase: FileWriteMode = .Append
        if (mode == appendCase) {
            return new FileAppendStream(handle)
        }
        return new FileOutputStream(handle)
    }

    // 删除文件或链接条目（§4.5.5）：包括指向目录的链接——删除链接
    // 本身，不删除链接目标（经 7-2 fsUnlink 原语语义；链接创建入口
    // §4.5.3 后置，识别/访问/删除已有链接由本入口与 removeLink 承担）。
    // 删除真实目录报 IsDirectory（原语口径：VM/native 失败路径补查 +
    // POSIX EISDIR）；不存在默认报错（NotFound）。归位 File 静态面：
    // 删除文件/链接条目属文件侧操作，目录静态面只收目录专属入口
    //（Directory.delete 删真实空目录，互不重叠）
    pub static func delete(path: Path) {
        fsUnlink(path.text)
    }

    // 删除的容错形态（§4.5.5）：返回是否实际删除，只把不存在转为
    // false——权限、目录目标（IsDirectory）等其他失败原样抛，不吞掉；
    // 对断链的删除按链接条目存在处理（unlink 只落末段条目本身）
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

    // 复制普通文件内容（§4.5.7 逐条，施工块 7-6）：
    //   - 源只读打开（默认跟随链接——源链接跟随到文件）；目标按模式
    //     打开或创建；两侧均不自动创建父目录（父级缺失 NotFound）；
    //   - CreateNew 用系统仅创建机制（FS_F_CREATE_NEW），已有条目包括
    //     断链不被覆盖；Overwrite 打开或创建、写入前截断——截断发生在
    //     自复制检查之后（见下），且跟随目标链接写入其目标文件（打开
    //     默认跟随，不替换链接条目本身）；
    //   - 自复制拒绝：两侧都打开后按系统文件身份比较（fsSameIdentity
    //     ——Windows 卷序列号+文件索引 / Linux st_dev+st_ino，硬链接、
    //     不同路径访问同一文件即同一身份），不能只比较路径文本，也不
    //     能只先查名称再打开截断（§4.5.7）。Overwrite 的截断（
    //     fsSetLength(0)）在身份判定之后执行，被拒绝时目标不受影响；
    //     CreateNew 目标必为新条目（系统保证），检查照跑保持单一入口。
    //     错误类别取 Other（契约只钉「拒绝」，未指定 kind；无原生码）
    //   - 只承诺内容：不保留 ACL/所有者/创建时间/稀疏布局等元数据；
    //     源被其他进程修改时不保证一致快照。实现为文件流 + pipe 的
    //     Rigi 组合（§4.5.7 允许；首版不采用 CopyFile 类原生复制优化
    //     ——不无条件继承宿主的自动删除等失败行为）；成功返回前完成
    //     目标持久化刷新（output.flush → FlushFileBuffers/fsync）及所
    //     创建流的关闭（§4.5.7）；
    //   - 失败可能留下新建的部分文件，或已被截断/部分改写的旧目标；
    //     不自动删除目标、不恢复旧内容（§4.5.7 钉死）。失败路径只释
    //     放句柄（不重试 flush——不重写可能已部分提交的数据，7-3 故障
    //     收尾同口径），原始异常原样传播：单异常模型，不增加聚合错误。
    pub static func copy(source: Path, destination: Path,
            mode: FileCopyMode = .CreateNew) {
        const srcHandle = fsOpen(source.text, FS_F_READ, 0)
        try {
            // 目标打开按模式（§4.5.7）：CreateNew 用系统仅创建机制
            //（FS_F_CREATE_NEW——已有条目包括断链不被覆盖，AlreadyExists
            // 保证来自打开调用本身，§4.5.6 CreateNew 同源）；Overwrite
            // 先以 OpenOrCreate 打开（不截断）——截断延迟到自复制检查
            // 之后（§4.5.7「截断前……拒绝自复制」）
            const mOverwrite0: FileCopyMode = .Overwrite
            const dstFlags = (if (mode == mOverwrite0) {
                (FS_F_WRITE | FS_F_CREATE)
            } else {
                (FS_F_WRITE | FS_F_CREATE_NEW)
            })
            const dstHandle = fsOpen(destination.text, dstFlags,
                FS_MODE_FILE)
            try {
                if (fsSameIdentity(srcHandle, dstHandle)) {
                    const oc: FileSystemErrorKind = .Other
                    throw new FileSystemException(oc, "File.copy",
                        source.text, destination.text, null)
                }
                const mOverwrite: FileCopyMode = .Overwrite
                if (mode == mOverwrite) {
                    fsSetLength(dstHandle, (0 as i64))
                }
                const input = new FileInputStream(srcHandle)
                const output = new FileOutputStream(dstHandle)
                try {
                    input.pipe(output)
                    output.flush()
                } catch (e: core.Exception) {
                    srcHandle.dispose()
                    dstHandle.dispose()
                    throw e
                }
                input.dispose()
                output.dispose()
            } catch (e: core.Exception) {
                dstHandle.dispose()
                throw e
            }
        } catch (e: core.Exception) {
            srcHandle.dispose()
            throw e
        }
    }

    // 原子创建临时文件（§4.5.8 逐条，实现契约见文件尾「临时文件」段）：
    // 显式接收所在目录，返回路径与已打开输出流。错误面：prefix 含
    // 分隔符/NUL 或最终名称不合规 → InvalidPath；目录缺失 → NotFound
    //（不自动创建父目录）；重试耗尽 → Other（防御，正常不可达）
    pub static func createTemporary(directory: Path,
            prefix: String): TemporaryFile {
        fsValidateTempPrefix(prefix)
        const rnd = new core.math.Random()
        var handle: FileHandle? = null
        var chosen: Path? = null
        var attempt: i32 = 0
        while (handle == null) {
            if (attempt >= 100) {
                const oc: FileSystemErrorKind = .Other
                throw new FileSystemException(oc, "File.createTemporary",
                    directory.text)
            }
            const candidate = directory.join(
                Path.of(fsTempName(prefix, rnd)))
            const made = fsTryCreateTempFile(candidate.text)
            if (made != null) {
                handle = made
                chosen = candidate
            }
            attempt = (attempt + 1)
        }
        // 守卫收窄（循环退出蕴含非空；guard-throw 建立收窄，path.rg
        // relativeTo 同款形态）
        if ((handle == null) or (chosen == null)) {
            const oc2: FileSystemErrorKind = .Other
            throw new FileSystemException(oc2, "File.createTemporary",
                directory.text)
        }
        const made2: FileHandle = handle
        const target: Path = chosen
        return new TemporaryFile(target, new FileOutputStream(made2))
    }
}

// ===== 只读文件输入流（§4.5.6 + §4.4）=====

// local 类型；同一实例不支持并发或重入（§4.4）。初始位置 0（新开句柄
// 的系统位置）。EOF 不粘滞：读到当前末尾返回 0 即返回，之后再次读取
// 可以看到新增内容（另一写入者追加场景，§4.5.6——与目录 reader 的
// 固定结束状态不同，DirectoryReader 属 7-4）。
pub class FileInputStream : core.io.InputStream implements
        core.io.ISeekableStream {
    // 所持文件句柄（fsOpen 所建，本流唯一拥有：disposeCore 释放；
    // NativeRc 生命周期由 7-2 FileHandle 管理）
    priv const handle: FileHandle

    internal init(_ -> handle) { }

    // 单次读取（§4.4 基类契约）：状态闸门 → 范围校验 → count == 0 校验
    // 后返回 0（不消费输入、不证明 EOF）；实际读取走原语挂起读（等待
    // 不占 Worker，恢复后继续同一次调用）。返回 0 即本次读取时已无数据
    // （当前 EOF，不粘滞不缓存）；I/O 失败置故障后原样抛
    // FileSystemException
    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) { return 0 }
        var n: i32 = 0
        try {
            n = fsRead(handle, buffer, offset, count)
        } catch (e: core.Exception) {
            markFaulted()
            throw e
        }
        return n
    }

    // 定位（§4.4 ISeekableStream）：允许定位到末尾之后（读即 EOF）；
    // 负位置抛 OutOfBoundException（参数校验失败不置故障）。实际 I/O
    // 失败置故障后抛
    pub override func seek(position: i64) {
        ensureOpen()
        if (position < 0L) {
            throw new core.OutOfBoundException(
                "文件输入流位置非法：${position} 为负")
        }
        try {
            fsSeek(handle, position, FS_SEEK_SET)
        } catch (e: core.Exception) {
            markFaulted()
            throw e
        }
    }

    // 当前字节位置（i64，§4.4：流位置与单次缓冲区索引 i32 分开）
    pub override func getPosition(): i64 {
        ensureOpen()
        var pos: i64 = 0L
        try {
            pos = fsTell(handle)
        } catch (e: core.Exception) {
            markFaulted()
            throw e
        }
        return pos
    }

    // 当前文件长度（§4.5.6：每次调用经原语实时查询，不缓存——外部/
    // 其他进程改文件后再查即得新值）
    pub func getLength(): i64 {
        ensureOpen()
        var len: i64 = 0L
        try {
            len = fsGetLength(handle)
        } catch (e: core.Exception) {
            markFaulted()
            throw e
        }
        return len
    }

    // 收尾钩子（基类 dispose 支架保证幂等）：释放句柄（FileHandle.
    // dispose 归零 token → NativeRc release → C 侧析构关闭；close 错误
    // 不上报——持久化错误归 flush 面，§4.5.6/7-2 口径）。关闭后
    // read/seek/getPosition/getLength 由 ensureOpen 抛
    // IllegalStateException
    protected override func disposeCore() {
        handle.dispose()
    }
}

// ===== 非追加文件输出流（§4.5.6 + §4.4）=====

// OpenExisting/CreateNew/CreateOrTruncate/OpenOrCreate 四模式的返回
// 形态。初始位置 0，除明确选择截断外保留已有内容。实现 ISeekableStream
// （定位本身不扩容，后续写入空隙补零）与 getLength/setLength。
// local 类型；同一实例不支持并发或重入。
pub class FileOutputStream : core.io.OutputStream implements
        core.io.ISeekableStream {
    // 所持文件句柄（同 FileInputStream）
    priv const handle: FileHandle
    // 故障投影：基类 dispose 支架先置 state_ = 2 再调 disposeCore，钩子
    // 内无法经 state_ 区分「正常关闭」与「故障态收尾」——用本标记记录
    // 是否发生过实际 I/O 失败（buffered.rg 同款）。故障收尾不 flush
    // （不重写可能已部分提交的数据），但句柄照常释放
    priv var broken: bool = false

    internal init(_ -> handle) { }

    // 单次写出（§4.4 基类契约）：状态闸门 → 范围校验 → count == 0 校验
    // 后直接成功（不写出任何字节）。原语写允许少于请求数的部分写——
    // 内部循环补齐（不得部分返回）；失败不保证回滚已写出的字节。
    // 常规文件对 count > 0 返回 0 属宿主异常形态（防御：按 I/O 失败
    // 处理并置故障，不无限自旋）
    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        try {
            var done: i32 = 0
            while (done < count) {
                const n = fsWrite(handle, buffer, offset + done,
                    count - done)
                if (n == 0) {
                    const otherKind: FileSystemErrorKind = .Other
                    throw new FileSystemException(otherKind, "File.write",
                        handle.pathText)
                }
                done = (done + n)
            }
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
    }

    // 刷新（§4.4/§4.5.6）：等待全部写入与系统持久化刷新成功
    // （FlushFileBuffers / fsync——7-2 挂起两段式，底层等待不占
    // Compute Worker）；仅库缓冲提交或系统缓存写入不满足契约。成功后
    // 仍可继续写入，不结束流也不关闭流。持久化刷新不构成文件替换事务，
    // 也不自动刷新目录项（§4.5.6）
    pub override func flush() {
        ensureOpen()
        try {
            fsFlush(handle)
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
    }

    // 定位（§4.4 ISeekableStream）：允许定位到末尾之后（定位本身不
    // 扩容，后续写入空隙补零——零填充由 7-2 C 侧实现补齐）；负位置抛
    // OutOfBoundException（不置故障）
    pub override func seek(position: i64) {
        ensureOpen()
        if (position < 0L) {
            throw new core.OutOfBoundException(
                "文件输出流位置非法：${position} 为负")
        }
        try {
            fsSeek(handle, position, FS_SEEK_SET)
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
    }

    // 当前字节位置（i64）
    pub override func getPosition(): i64 {
        ensureOpen()
        var pos: i64 = 0L
        try {
            pos = fsTell(handle)
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
        return pos
    }

    // 当前文件长度（实时查询，不缓存；§4.5.6）
    pub func getLength(): i64 {
        ensureOpen()
        var len: i64 = 0L
        try {
            len = fsGetLength(handle)
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
        return len
    }

    // 设置文件长度（§4.5.6）：负长度抛 OutOfBoundException（参数校验
    // 失败不置故障）；缩短截断、增长部分读取为零，成功后游标保持不变
    // ——即使游标已在新末尾之后（原语层兑现，§4.5.6 明文契约）。不
    // 承诺预先分配全部物理空间，也不隐式替代 flush
    pub func setLength(length: i64) {
        ensureOpen()
        if (length < 0L) {
            throw new core.OutOfBoundException(
                "setLength 长度非法：${length} 为负")
        }
        try {
            fsSetLength(handle, length)
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
    }

    // 收尾钩子（基类 dispose 支架保证幂等）：正常收尾完成持久化刷新
    // 后释放句柄；故障收尾不 flush（不重写可能已部分提交的数据）只
    // 释放句柄（§4.4：首次 dispose 的写出或刷新失败不得跳过其余必要
    // 清理——句柄照常关闭，失败向调用者报告；对象已在关闭态，不改写
    // state_）。关闭后 write/flush/seek/getPosition/getLength/setLength
    // 由 ensureOpen 抛 IllegalStateException
    protected override func disposeCore() {
        if (broken == false) {
            try {
                fsFlush(handle)
            } catch (e: core.Exception) {
                handle.dispose()
                throw e
            }
        }
        handle.dispose()
    }
}

// ===== 追加文件输出流（§4.5.6）=====

// .Append 模式的唯一返回形态。不存在则创建；系统追加机制
// （FILE_APPEND_DATA / O_APPEND，7-2 open 映射）使每次写入始终到达
// 当时末尾——不是只在打开时定位一次末尾（外部/其他进程并发增长后，
// 本流下一次写入仍接在其后）。不提供任意写入定位或 setLength：不实现
// ISeekableStream，转 ISeekableStream 失败（接口能力由具体返回类型
// 体现，§4.5.6）。不承诺跨进程一次大 write 或一整行的原子性。
// local 类型；同一实例不支持并发或重入。
pub class FileAppendStream : core.io.OutputStream {
    // 所持文件句柄（同前两形态）
    priv const handle: FileHandle
    // 故障投影（同 FileOutputStream 口径）
    priv var broken: bool = false

    internal init(_ -> handle) { }

    // 单次写出：循环补齐部分写（契约同 FileOutputStream.write）；
    // 每次底层写都由系统追加机制落位当时末尾
    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        try {
            var done: i32 = 0
            while (done < count) {
                const n = fsWrite(handle, buffer, offset + done,
                    count - done)
                if (n == 0) {
                    const otherKind: FileSystemErrorKind = .Other
                    throw new FileSystemException(otherKind, "File.write",
                        handle.pathText)
                }
                done = (done + n)
            }
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
    }

    // 刷新（§4.4/§4.5.6 持久化契约，同 FileOutputStream.flush）
    pub override func flush() {
        ensureOpen()
        try {
            fsFlush(handle)
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
    }

    // 当前文件长度（实时查询；长度查询不是写入定位，追加形态保留
    // §4.5.6「具体文件流提供 getLength」的通用面）
    pub func getLength(): i64 {
        ensureOpen()
        var len: i64 = 0L
        try {
            len = fsGetLength(handle)
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
        return len
    }

    // 收尾钩子（同 FileOutputStream.disposeCore：正常收尾持久化刷新，
    // 故障收尾只释放句柄）
    protected override func disposeCore() {
        if (broken == false) {
            try {
                fsFlush(handle)
            } catch (e: core.Exception) {
                handle.dispose()
                throw e
            }
        }
        handle.dispose()
    }
}

// ============================================================================
// 临时文件（施工块 7-6，§4.5.8）：
//   - File.createTemporary(directory, prefix) 必须显式接收所在目录；
//     原子创建新文件并返回路径与「已打开」输出流（TemporaryFile）——
//     不先仅生成名称再让调用者另行打开（§4.5.8）。已有同名项不能被
//     覆盖：经系统仅创建机制（FS_F_CREATE_NEW）创建，名称冲突（
//     AlreadyExists）重新选名重试原子创建（重试上限 100 次防死循环，
//     耗尽报 Other——随机名称使冲突概率可忽略，正常不可达）；
//   - prefix 只作名称前缀：不含路径分隔符（'/' 双平台、'\' 仅
//     Windows——Linux 反斜杠是普通名称字符，§4.5.2）、不含 NUL；最终
//     名称仍遵守 Path 规则（组装后过 Path 构造校验——Windows 保留名/
//     冒号/尾随点空格等由其拒绝），违例抛 InvalidPath；
//   - 调用者负责关闭返回的流以及删除临时文件；关闭不自动删除，不因
//     名称含临时含义而由 GC 清理（§4.5.8）；首版不新增系统临时目录
//     查询或环境变量访问 API（§4.5.8 明文后置）。
// ============================================================================

// 前缀校验（§4.5.8：分隔符/NUL 由本函数拒绝；「形成当前平台不支持的
// 名称」由最终名称的 Path 构造校验兜底，两处都归 InvalidPath）
internal func fsValidateTempPrefix(prefix: String) {
    const win = fsIsWindows()
    const bytes = prefix.toUtf8Span()
    var i: i32 = 0
    while (i < bytes.length) {
        const b: u8 = (bytes[i] if? (0 as u8))
        if ((b == (0 as u8)) or (b == (47 as u8))) {
            throw new FileSystemException(.InvalidPath,
                "临时资源名称前缀", prefix)
        }
        if (win and (b == (92 as u8))) {
            throw new FileSystemException(.InvalidPath,
                "临时资源名称前缀", prefix)
        }
        i = (i + 1)
    }
}

// 临时名称组装：prefix + '_' + 随机 u64 十进制文本（core.math.Random
// 无参构造系统种子，D7——随机部分只用于防撞选名，不承诺不可预测性）
internal func fsTempName(prefix: String, rnd: core.math.Random): String {
    return "${prefix}_${rnd.nextU64()}"
}

// 临时文件原子创建单次尝试：F_CREATE_NEW 系统仅创建机制（已有条目不
// 被覆盖，§4.5.6 CreateNew 同源）。名称冲突（AlreadyExists）返回 null
// 供上层重选名重试；其他错误原样抛
internal func fsTryCreateTempFile(pathText: String): FileHandle? {
    var handle: FileHandle? = null
    var conflict = false
    try {
        handle = fsOpen(pathText, (FS_F_WRITE | FS_F_CREATE_NEW),
            FS_MODE_FILE)
    } catch (e: FileSystemException) {
        const ae: FileSystemErrorKind = .AlreadyExists
        if (e.kind == ae) {
            conflict = true
        } else {
            throw e
        }
    }
    if (conflict) { return null }
    return handle
}

// createTemporary 的返回形态（自定义小结构而非 Pair：命名字段自说明，
// Pair 的 first/second 无法表达「path 无资源、stream 承担关闭责任」
// 的分工，§4.5.1/§4.5.8）。调用者负责 stream 的关闭与文件的删除；
// 关闭不自动删除（§4.5.8）。
pub rich struct TemporaryFile {
    // 新建临时文件的路径（目录 + 组装名称）
    pub var path: Path {
        pub get
        priv set(value: _) { }
    }

    // 已打开的输出流（原子创建所得，初始位置 0、长度 0；关闭责任在
    // 调用者——创建/打开资源的一方负责关闭，§3.2）
    pub var stream: core.io.OutputStream {
        pub get
        priv set(value: _) { }
    }

    pub init(_ -> path, _ -> stream) { }
}
