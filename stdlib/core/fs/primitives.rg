// ============================================================================
// Rigi 标准库：core.fs native 原语层（施工块 7-2，STDLIB §4.5.6/§4.5.9
// + §3.2/§3.3，维护契约 D5）。
//
// 本文件是文件系统能力的原语地基（完整文件流 API 属 7-3，信息查询
// FileInfo 组装属 7-5，目录 reader 属 7-4，创建/删除/移动/复制/临时
// 文件的公共入口属 7-6——后续块在本层之上组装，本层不做公共 API
// 承诺，全部 internal：模块内（编译单元内）可见，7-3/7-4/7-5/7-6 与
// e2e 语料使用，不对最终用户暴露）。
//
//   - FileHandle / FileCarriage：文件句柄的 NativeRcHandle 模型落地
//     （core.native 契约，CoroutineHandle 先例）——local FileHandle
//     持强引用，seq using 管理；shared FileCarriage 是弱搬运票据，
//     跨协程 retain() 判空后接管。dispose = release → C 侧析构回调
//     关闭 fd/HANDLE（close 错误不上报：持久化错误归 flush 面，
//     §4.5.6；与 .NET FileStream.Dispose 同步 close 同口径）。
//   - 错误映射：C/VM 侧只产归一错误码（类 errno 钉死值，Windows
//     GetLastError / .NET 异常经各自映射表归一到同一套码），本文件
//     fsErrKind 把归一码映射为 FileSystemErrorKind 并抛
//     FileSystemException（kind + 操作名 + 路径 + 原生码，§4.5.9——
//     错误分类不靠解析错误消息）。
//   - 挂起读写/flush：start/take 两段式（stdin 挂起读先例直复刻）——
//     start 把阻塞系统调用卸载到宿主后台线程（不在 Compute Worker
//     上同步阻塞，§3.3/§4.5.6），调用方 yield 一次性 EventAlarm 挂起，
//     完成后经事件唤醒 take 取结果。挂起期间句柄由 FileHandle 强引用
//     保活、缓冲区借用（§3.2）。
//   - 路径编码：路径以 String（UTF-8）传入，C 侧 Windows 转 UTF-16
//     （严格拒绝非法序列 → InvalidNameEncoding，不替换不跳过，
//     §4.5.2）；Linux 直接用 UTF-8 字节。长路径内部前缀是 C 侧实现
//     细节（§4.5.2「内部使用的原生路径前缀不作为用户路径语法扩展」）。
//
// 同步/挂起分界（报告口径）：read/write/flush 等待数据传输或持久化
// （可能任意久）→ 挂起两段式；文件 open 须防 FIFO 等非普通文件
// 同步等待另一端，再按已打开句柄确认普通文件（目录 IsDirectory，
// 其余 WrongType）。seek/tell/getLength/setLength/stat/realpath/mkdir/
// rmdir/unlink/rename/readdir 族通常快速返回 → 同步直调（setLength
// 的零填充与游标保持在原语层兑现，§4.5.6）。
// ============================================================================
namespace core.fs

// ===== open 标志位（与 rigi_rt fs.c RIGI_FS_F_* / VM 侧常量逐位一致；
// FileWriteMode 五模式的映射在 7-3 文件流层）=====

internal const FS_F_READ: i32 = 1
internal const FS_F_WRITE: i32 = 2
internal const FS_F_APPEND: i32 = 4
internal const FS_F_CREATE: i32 = 8
internal const FS_F_TRUNCATE: i32 = 16
internal const FS_F_CREATE_NEW: i32 = 32

// ===== 归一错误码 → FileSystemErrorKind（§4.5.9 分类的唯一映射落点；
// 码表本体钉在 rigi_rt fs.c（native）与 VmDispatch.MapFsError（VM），
// 三处同值）=====

priv func fsErrKind(code: i32): FileSystemErrorKind {
    if (code == 2) { return .NotFound }
    if (code == 13) { return .PermissionDenied }
    if (code == 17) { return .AlreadyExists }
    if (code == 18) { return .CrossDevice }
    if (code == 20) { return .NotDirectory }
    if (code == 21) { return .IsDirectory }
    if (code == 22) { return .InvalidPath }
    if (code == 26) { return .SharingViolation }
    if (code == 28) { return .NoSpace }
    if (code == 30) { return .ReadOnlyFileSystem }
    if (code == 31) { return .TooManyLinks }
    if (code == 36) { return .PathTooLong }
    if (code == 38) { return .Unsupported }
    if (code == 39) { return .DirectoryNotEmpty }
    if (code == 1001) { return .InvalidNameEncoding }
    if (code == 1002) { return .WrongType }
    // 5/9/12/40/1000 及其余未识别码一律 Other（链接循环不伪装
    // NotFound，§4.5.3——40 落 Other 是契约归类，非吞并）
    return .Other
}

// 构造文件系统异常（归一码为正数形态传入；nativeError 携带该码）
priv func fsMakeException(op: String, p1: String?, p2: String?,
        code: i32): FileSystemException {
    return new FileSystemException(fsErrKind(code), op, p1, p2,
        (code as i64))
}

// ===== 内部二进制助手（出参 Span 小端读取；Span 索引返回可空，界内
// 前提由调用方保证——path.rg fsByteAt 同款形态）=====

priv func fsByteAt(bytes: Span\<u8>, offset: i32): u8 {
    return (bytes[offset] if? (0 as u8))
}

priv func fsReadI64Le(bytes: Span\<u8>, offset: i32): i64 {
    var v: i64 = 0L
    var i = 0
    while (i < 8) {
        const b: u8 = fsByteAt(bytes, offset + i)
        v = v | ((b as i64) << ((8 * i) as i64))
        i = i + 1
    }
    return v
}

// ===== 句柄模型（core.native.NativeRcHandle 契约，CoroutineHandle
// 先例：token 留私有/内部字段，retain/release 经 rigi_rt NativeRc
// 注册表，用户代码不能凭整数获得资源 authority）=====

// 文件句柄（local）：每个实例只属于当前 Coroutine，由 IDisposable/
// seq using 确定性释放。token 字段 internal 供本文件原语包装与 7-3
// 流层触达（internal 类不对外，authority 不泄漏到模块外）。
internal class FileHandle : core.native.NativeRcHandle\<FileCarriage> {
    internal var token: i64
    // 打开路径文本（错误诊断的 path 携带；Carriage 恢复路径无来源，
    // 为 null）
    internal var pathText: String?

    internal init(_ -> token, _ -> pathText) { }

    pub override func carry(): FileCarriage {
        if (token == (0 as i64)) {
            throw new core.IllegalStateException("FileHandle 已释放")
        }
        return new FileCarriage(token)
    }

    pub override func dispose() {
        if (token != (0 as i64)) {
            const held = token
            token = (0 as i64)
            rigi_native_rc_release(held)
        }
    }
}

// 弱搬运票据（shared）：不拥有资源、不提供操作能力；接收方 retain()
// 判空后按具体类型转换，seq using 接管（§3.2/native_rc.rg 契约）
internal shared class FileCarriage implements core.native.ICarriage {
    priv const token: i64
    internal init(_ -> token)

    pub override func retain(): Any? {
        if (rigi_native_rc_retain(token) == 0) { return null }
        const none: String? = null
        return new FileHandle(token, none) as Any
    }
}

// 唯一的 Carriage→Handle 恢复点（调用方判空 + seq using，禁止显式
// retain/release 配对散落业务逻辑）
internal func retainFile(carriage: FileCarriage): FileHandle? {
    const retained = carriage.retain()
    if (retained == null) { return null }
    return retained as FileHandle
}

// ===== 挂起等待事件（stdin StdinWake 先例：一次性粘滞事件，每轮操作
// 新建一枚——signal 即终态不可复位，复用会让下一轮 yield 立即通过）=====

internal shared class FsWake : core.coroutine.EventAlarm {
}

// ===== native 原语面（C 符号 = rigi_ + @NativeSymbol 短名直拼，
// RuntimeFaces 映射规则；VM hook 同键注册，双宿主同语义）=====

@NativeLibrary("rigi_rt")
@NativeSymbol("native_rc_retain")
priv native func rigi_native_rc_retain(token: i64): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("native_rc_release")
priv native func rigi_native_rc_release(token: i64)

// 打开文件（同步）：flags 见 FS_F_*（组合语义见 rigi_fs_open 注释）；
// mode 为 POSIX 创建权限位（Windows 忽略）。成功 out[0..8) 写句柄
// token；返回 0，< 0 = -归一码
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_open")
priv native func rigi_fs_open(path: String, flags: i32, mode: i32,
    out: Span\<u8>): i32

// 启动一次挂起读：buffer[offset..offset+count) 的读请求卸载到宿主
// 后台线程，完成后触发 wakeHandle。返回 0 = 已卸载（挂起等唤醒后
// fs_read_take 取结果）；< 0 = 立即失败（不挂起）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_read_start")
priv native func rigi_fs_read_start(handle: i64, buffer: Span\<u8>,
    offset: i32, count: i32, wake: i64): i32

// 取挂起读结果（唤醒后调用）：≥ 0 = 实际读取字节数（0 = 当前 EOF，
// 不粘滞——之后再次读取可以看到新增内容，§4.5.6）；< 0 = -归一码
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_read_take")
priv native func rigi_fs_read_take(handle: i64): i32

// ===== 原语包装（internal：7-3/7-4/7-5/7-6 与 e2e 语料的触达面；
// 错误码在此映射 FileSystemException）=====

// 打开文件：flags/mode 见上；返回的 FileHandle 由调用方 seq using
// 管理（§3.2：创建/打开资源的一方负责关闭）
internal func fsOpen(path: String, flags: i32, mode: i32): FileHandle {
    const out = core.collections.spanOf\<u8>(8)
    const rc = rigi_fs_open(path, flags, mode, out)
    if (rc != 0) {
        throw fsMakeException("fs.open", path, null, 0 - rc)
    }
    return new FileHandle(fsReadI64Le(out, 0), path)
}

// 挂起读（普通 func 内挂起，§3.1：不因内部等待 I/O 另建 Task/协程）：
// count == 0 直接返回 0，不消费输入也不挂起；I/O 失败抛
// FileSystemException（§4.5.9——错误分类经 kind 携带）
internal func fsRead(handle: FileHandle, buffer: Span\<u8>, offset: i32,
        count: i32): i32 {
    if (count == 0) { return 0 }
    const wake = new FsWake()
    const h = wake.ensureHandle()
    const rc = rigi_fs_read_start(handle.token, buffer, offset, count, h)
    if (rc != 0) {
        throw fsMakeException("fs.read", handle.pathText, null, 0 - rc)
    }
    // 挂起等待：不占 Worker；读完成由后台线程 signal 唤醒（粘滞兜底
    // 先 signal 后 yield 的窗口，stdin 先例）
    yield (wake as core.coroutine.EventAlarm)
    const n = rigi_fs_read_take(handle.token)
    if (n < 0) {
        throw fsMakeException("fs.read", handle.pathText, null, 0 - n)
    }
    return n
}

// ============================================================================
// 原语族（阶段 2）：挂起写/flush + 同步定位/长度/信息查询/创建删除/
// 移动/目录枚举。同步/挂起分界见文件头：write/flush 等待数据传输或
// 持久化（可能任意久）挂起两段式；其余元数据操作同步直调。
// ============================================================================

// ===== kind 与 seek 基准常量（C 侧 rigi_fs_stat 结构 / rigi_fs_seek
// whence 双端同值）=====

internal const FS_KIND_FILE: i32 = 0
internal const FS_KIND_DIRECTORY: i32 = 1
internal const FS_KIND_LINK: i32 = 2
internal const FS_KIND_OTHER: i32 = 3

internal const FS_SEEK_SET: i32 = 0
internal const FS_SEEK_CUR: i32 = 1
internal const FS_SEEK_END: i32 = 2

// 宿主权限规则基础位（§4.5.5：Linux 文件 0666 / 目录 0777 受 umask，
// Windows 忽略；open/diropen 的 mode 形参供 7-3/7-6 使用）
internal const FS_MODE_FILE: i32 = 438      // 0666
internal const FS_MODE_DIRECTORY: i32 = 511 // 0777

// stat 结构 48 字节（C 侧 RIGI_FS_STAT_SIZE）；时间毫秒位哨兵 =
// i64 最小值（宿主不可得 → Rigi 层组装 null，不伪造时钟精度）
internal const FS_STAT_SIZE: i32 = 48
internal const FS_TIME_UNAVAILABLE: i64 = ((0L - 9223372036854775807L) - 1L)

// ===== native 原语面（C 符号 rigi_fs_*，映射规则同上）=====

// 启动一次挂起写：buffer[offset..offset+count) 卸载到宿主后台线程。
// 返回 0 = 已卸载；< 0 = 立即失败
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_write_start")
priv native func rigi_fs_write_start(handle: i64, buffer: Span\<u8>,
    offset: i32, count: i32, wake: i64): i32

// 取挂起写结果（唤醒后调用）：≥ 0 = 实际写出字节数（允许少于 count
// 的部分写，7-3 流层循环补齐）；< 0 = -归一码
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_write_take")
priv native func rigi_fs_write_take(handle: i64): i32

// 启动一次挂起 flush（系统持久化刷新：FlushFileBuffers / fsync，
// §4.5.6——不是库缓冲提交）。返回 0 = 已卸载；< 0 = 立即失败
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_flush_start")
priv native func rigi_fs_flush_start(handle: i64, wake: i64): i32

// 取挂起 flush 结果（唤醒后调用）：0 = 持久化完成；< 0 = -归一码
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_flush_take")
priv native func rigi_fs_flush_take(handle: i64): i32

// 定位：whence FS_SEEK_*；成功 out[0..8) 写新绝对位置；结果位置为负
// 归 -22（Rigi 层按范围错误抛 OutOfBoundException，§4.5.9）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_seek")
priv native func rigi_fs_seek(handle: i64, offset: i64, whence: i32,
    out: Span\<u8>): i32

// 当前游标绝对位置（out[0..8)）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_tell")
priv native func rigi_fs_tell(handle: i64, out: Span\<u8>): i32

// 当前文件长度（out[0..8)）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_get_length")
priv native func rigi_fs_get_length(handle: i64, out: Span\<u8>): i32

// 设置文件长度：缩短截断/增长补零，成功后游标保持不变——即使已在新
// 末尾之后（§4.5.6 明文契约，Windows 零填充由 C 侧实现补齐）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_set_length")
priv native func rigi_fs_set_length(handle: i64, length: i64): i32

// 信息查询：out[0..48) 写 stat 结构（kind i32 / length i64 / 三组时间
// (毫秒 i64 + 纳秒 i32)，布局见 rigi_rt fs.c）。stat 跟随末段链接，
// lstat 只查询末段链接本身（断链仍可被识别，§4.5.3）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_stat")
priv native func rigi_fs_stat(path: String, out: Span\<u8>): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("fs_lstat")
priv native func rigi_fs_lstat(path: String, out: Span\<u8>): i32

// realpath：要求目标存在，解析链接返回绝对路径（§4.5.3）。out 写
// UTF-8 字节、meta[0..4) 写字节数；返回 2 = out 不足（meta 回所需，
// 放大重试一次）；< 0 = -归一码
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_realpath")
priv native func rigi_fs_realpath(path: String, out: Span\<u8>,
    meta: Span\<u8>): i32

// 创建/删除（§4.5.5 宿主权限规则；目标不存在默认报错）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_mkdir")
priv native func rigi_fs_mkdir(path: String, mode: i32): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("fs_rmdir")
priv native func rigi_fs_rmdir(path: String): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("fs_unlink")
priv native func rigi_fs_unlink(path: String): i32

// 移动/重命名：replace = 0 → NoReplace（系统不替换保证，遇任何已有
// 目标报错，§4.5.7「不能用 exists + 覆盖 rename 模拟」；宿主/文件系
// 统不能提供保证时报 Unsupported）；1 → Replace（覆盖仅限文件/链接
// 条目，不允许目录覆盖或合并）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_rename")
priv native func rigi_fs_rename(src: String, dst: String,
    replace: i32): i32

// 目录枚举（§4.5.4 地基，DirectoryReader 组装在 7-4）：diropen 打开
// 即验证；dirread 每次一个条目，返回 0 = 条目（out 名称 UTF-8，
// meta[0..4) 字节数 / [4..8) kind 提示 / [8..12) 提示有效标志）、
// 1 = 结束、2 = out 不足（meta 回所需）、< 0 = -归一码（含名称无法
// 无损表达 → -1001，§4.5.2）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_diropen")
priv native func rigi_fs_diropen(path: String, out: Span\<u8>): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("fs_dirread")
priv native func rigi_fs_dirread(handle: i64, out: Span\<u8>,
    meta: Span\<u8>): i32

// ===== 内部二进制助手（stat 结构/变长输出解析）=====

priv func fsReadI32Le(bytes: Span\<u8>, offset: i32): i32 {
    var v: i32 = 0
    var i = 0
    while (i < 4) {
        const b: i32 = (fsByteAt(bytes, offset + i) as i32)
        v = v | (b << (8 * i))
        i = i + 1
    }
    return v
}

// 变长输出的字节段 → String（Utf8Decoder 严格模式；非法名称已被
// C/VM 侧校验先拒（-1001），此处严格解码是不可达的纵深防御）
priv func fsDecodeName(bytes: Span\<u8>, count: i32): String {
    const tmp = core.collections.spanOf\<u8>(count)
    var i = 0
    while (i < count) {
        tmp[i] = fsByteAt(bytes, i)
        i = i + 1
    }
    return new core.text.Utf8Decoder().decode(tmp, true)
}

// ===== 信息查询记录（一次查询所得，不持句柄不自动更新，§4.5.3；
// FileInfo 公共组装属 7-5，本层给原始记录。时间以 (epoch 毫秒 i64,
// 纳秒余量 i32) 原始对携带；毫秒位 = FS_TIME_UNAVAILABLE 表示宿主不
// 可得，7-5 组装 TimeStamp? 时据此置 null——不伪造时钟精度也不用元
// 数据变更时间冒充创建时间。普通 struct：初版曾以 rich struct 携
// TimeStamp? 字段，native 侧返回值交付 UAF（块报告；根因已修——
// richretrfix 返回值交付即移动契约，回归语料 rich_return_nullable），
// 本层保留 (ms, ns) 原始对形态：值语义更薄、7-5 组装时再装箱，无
// 行为影响=====

internal struct FsStatInfo {
    internal var kind: i32
    // 普通文件字节长度；非普通文件 -1（「非普通文件不伪造长度」→
    // 7-5 组装 FileInfo 时置 null，§4.5.3）
    internal var length: i64
    internal var mtimeMs: i64
    internal var mtimeNs: i32
    internal var atimeMs: i64
    internal var atimeNs: i32
    internal var birthMs: i64
    internal var birthNs: i32

    internal init(_ -> kind, _ -> length, _ -> mtimeMs, _ -> mtimeNs,
            _ -> atimeMs, _ -> atimeNs, _ -> birthMs, _ -> birthNs) { }
}

priv func fsParseStat(bytes: Span\<u8>): FsStatInfo {
    return new FsStatInfo(fsReadI32Le(bytes, 0), fsReadI64Le(bytes, 4),
        fsReadI64Le(bytes, 12), fsReadI32Le(bytes, 20),
        fsReadI64Le(bytes, 24), fsReadI32Le(bytes, 32),
        fsReadI64Le(bytes, 36), fsReadI32Le(bytes, 44))
}

// ===== 目录句柄（FileHandle 同款 NativeRcHandle 模型）=====

internal class DirHandle : core.native.NativeRcHandle\<DirCarriage> {
    internal var token: i64
    internal var pathText: String?

    internal init(_ -> token, _ -> pathText) { }

    pub override func carry(): DirCarriage {
        if (token == (0 as i64)) {
            throw new core.IllegalStateException("DirHandle 已释放")
        }
        return new DirCarriage(token)
    }

    pub override func dispose() {
        if (token != (0 as i64)) {
            const held = token
            token = (0 as i64)
            rigi_native_rc_release(held)
        }
    }
}

internal shared class DirCarriage implements core.native.ICarriage {
    priv const token: i64
    internal init(_ -> token)

    pub override func retain(): Any? {
        if (rigi_native_rc_retain(token) == 0) { return null }
        const none: String? = null
        return new DirHandle(token, none) as Any
    }
}

internal func retainDir(carriage: DirCarriage): DirHandle? {
    const retained = carriage.retain()
    if (retained == null) { return null }
    return retained as DirHandle
}

// 目录条目（不排序不递归，含隐藏项；kind 提示可能不可用或过时，
// 完整信息由 getInfo 另行查询，§4.5.4；绝对路径组装属 7-4）
internal struct FsDirEntry {
    internal var name: String
    internal var kindHint: i32
    internal var kindAvailable: bool

    internal init(_ -> name, _ -> kindHint, _ -> kindAvailable) { }
}

// ===== 原语包装（internal：7-3/7-4/7-5/7-6 与 e2e 语料的触达面）=====

// 挂起写（同 fsRead 挂起形态）：返回实际写出字节数
internal func fsWrite(handle: FileHandle, buffer: Span\<u8>, offset: i32,
        count: i32): i32 {
    if (count == 0) { return 0 }
    const wake = new FsWake()
    const h = wake.ensureHandle()
    const rc = rigi_fs_write_start(handle.token, buffer, offset, count, h)
    if (rc != 0) {
        throw fsMakeException("fs.write", handle.pathText, null, 0 - rc)
    }
    yield (wake as core.coroutine.EventAlarm)
    const n = rigi_fs_write_take(handle.token)
    if (n < 0) {
        throw fsMakeException("fs.write", handle.pathText, null, 0 - n)
    }
    return n
}

// 挂起 flush：等待系统持久化刷新成功（§4.5.6）
internal func fsFlush(handle: FileHandle) {
    const wake = new FsWake()
    const h = wake.ensureHandle()
    const rc = rigi_fs_flush_start(handle.token, h)
    if (rc != 0) {
        throw fsMakeException("fs.flush", handle.pathText, null, 0 - rc)
    }
    yield (wake as core.coroutine.EventAlarm)
    const r = rigi_fs_flush_take(handle.token)
    if (r != 0) {
        throw fsMakeException("fs.flush", handle.pathText, null, 0 - r)
    }
}

// 定位（结果位置为负属范围错误 → OutOfBoundException，§4.5.9）
internal func fsSeek(handle: FileHandle, offset: i64, whence: i32): i64 {
    const out = core.collections.spanOf\<u8>(8)
    const rc = rigi_fs_seek(handle.token, offset, whence, out)
    if (rc != 0) {
        if ((0 - rc) == 22) {
            throw new core.OutOfBoundException("seek 结果位置越界")
        }
        throw fsMakeException("fs.seek", handle.pathText, null, 0 - rc)
    }
    return fsReadI64Le(out, 0)
}

internal func fsTell(handle: FileHandle): i64 {
    const out = core.collections.spanOf\<u8>(8)
    const rc = rigi_fs_tell(handle.token, out)
    if (rc != 0) {
        throw fsMakeException("fs.tell", handle.pathText, null, 0 - rc)
    }
    return fsReadI64Le(out, 0)
}

internal func fsGetLength(handle: FileHandle): i64 {
    const out = core.collections.spanOf\<u8>(8)
    const rc = rigi_fs_get_length(handle.token, out)
    if (rc != 0) {
        throw fsMakeException("fs.getLength", handle.pathText, null, 0 - rc)
    }
    return fsReadI64Le(out, 0)
}

// 设置长度（负长度报范围错误；缩短截断/增长补零，游标保持不变）
internal func fsSetLength(handle: FileHandle, length: i64) {
    if (length < 0L) {
        throw new core.OutOfBoundException("setLength 负长度")
    }
    const rc = rigi_fs_set_length(handle.token, length)
    if (rc != 0) {
        throw fsMakeException("fs.setLength", handle.pathText, null, 0 - rc)
    }
}

internal func fsStat(path: String): FsStatInfo {
    const out = core.collections.spanOf\<u8>(FS_STAT_SIZE)
    const rc = rigi_fs_stat(path, out)
    if (rc != 0) {
        throw fsMakeException("fs.stat", path, null, 0 - rc)
    }
    return fsParseStat(out)
}

internal func fsLstat(path: String): FsStatInfo {
    const out = core.collections.spanOf\<u8>(FS_STAT_SIZE)
    const rc = rigi_fs_lstat(path, out)
    if (rc != 0) {
        throw fsMakeException("fs.lstat", path, null, 0 - rc)
    }
    return fsParseStat(out)
}

// realpath：缓冲不足放大重试一次（C 侧协议正数哨兵 2）
internal func fsRealpath(path: String): String {
    var out = core.collections.spanOf\<u8>(512)
    const meta = core.collections.spanOf\<u8>(4)
    var rc = rigi_fs_realpath(path, out, meta)
    if (rc == 2) {
        out = core.collections.spanOf\<u8>(fsReadI32Le(meta, 0))
        rc = rigi_fs_realpath(path, out, meta)
    }
    if (rc != 0) {
        throw fsMakeException("fs.realpath", path, null, 0 - rc)
    }
    return fsDecodeName(out, fsReadI32Le(meta, 0))
}

internal func fsMkdir(path: String, mode: i32) {
    const rc = rigi_fs_mkdir(path, mode)
    if (rc != 0) {
        throw fsMakeException("fs.mkdir", path, null, 0 - rc)
    }
}

internal func fsRmdir(path: String) {
    const rc = rigi_fs_rmdir(path)
    if (rc != 0) {
        throw fsMakeException("fs.rmdir", path, null, 0 - rc)
    }
}

internal func fsUnlink(path: String) {
    const rc = rigi_fs_unlink(path)
    if (rc != 0) {
        throw fsMakeException("fs.unlink", path, null, 0 - rc)
    }
}

// 移动/重命名（NoReplace 默认语义对齐 MoveMode 默认，§4.5.7）
internal func fsRename(src: String, dst: String, replace: bool) {
    const rc = rigi_fs_rename(src, dst, if (replace) { 1 } else { 0 })
    if (rc != 0) {
        throw fsMakeException("fs.rename", src, dst, 0 - rc)
    }
}

internal func fsDirOpen(path: String): DirHandle {
    const out = core.collections.spanOf\<u8>(8)
    const rc = rigi_fs_diropen(path, out)
    if (rc != 0) {
        throw fsMakeException("fs.dirOpen", path, null, 0 - rc)
    }
    return new DirHandle(fsReadI64Le(out, 0), path)
}

// 读一个条目：null = 结束（此后持续 null，重扫须重新打开，§4.5.4）；
// 缓冲不足放大重试一次（正数哨兵 2）
internal func fsDirRead(handle: DirHandle): FsDirEntry? {
    var out = core.collections.spanOf\<u8>(256)
    const meta = core.collections.spanOf\<u8>(12)
    var rc = rigi_fs_dirread(handle.token, out, meta)
    if (rc == 2) {
        out = core.collections.spanOf\<u8>(fsReadI32Le(meta, 0))
        rc = rigi_fs_dirread(handle.token, out, meta)
    }
    if (rc == 1) { return null }
    if (rc < 0) {
        throw fsMakeException("fs.dirRead", handle.pathText, null, 0 - rc)
    }
    const n = fsReadI32Le(meta, 0)
    return new FsDirEntry(fsDecodeName(out, n), fsReadI32Le(meta, 4),
        (fsReadI32Le(meta, 8) != 0))
}

// ============================================================================
// 原语族（7-6）：系统文件身份比较（§4.5.7「截断前按实际打开的源/目标
// 系统文件身份拒绝自复制」的地基）。身份判定在系统层：Windows 卷序列
// 号 + 64 位文件索引，Linux st_dev + st_ino（C/VM 双端同口径）——硬链
// 接、不同路径访问同一文件即同一身份，不比较路径文本。
// ============================================================================

// 文件身份比较（同步元数据操作，stat 同分界）：两个已打开文件句柄是
// 否指向同一系统文件。out[0] 写 1（同一）/ 0（不同）；返回 0；< 0 =
// -归一码（句柄已关闭 -9 防御；身份查询失败按宿主错误映射）
@NativeLibrary("rigi_rt")
@NativeSymbol("fs_same_file")
priv native func rigi_fs_same_file(a: i64, b: i64, out: Span\<u8>): i32

// 身份比较包装：真 = 两句柄是同一系统文件（自复制的判定面，7-6
// File.copy 在截断前调用——不能只比较路径文本，也不能只先查名称再
// 打开截断，§4.5.7）。失败映射 FileSystemException（双路径携带两侧
// 打开路径文本）
internal func fsSameIdentity(a: FileHandle, b: FileHandle): bool {
    const out = core.collections.spanOf\<u8>(1)
    const rc = rigi_fs_same_file(a.token, b.token, out)
    if (rc != 0) {
        throw fsMakeException("fs.sameFile", a.pathText, b.pathText, 0 - rc)
    }
    return fsByteAt(out, 0) != (0 as u8)
}
