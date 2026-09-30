/*
 * rigi_rt FS 原语层（施工块 7-2，STDLIB §4.5.6/§4.5.9 + §3.2/§3.3）。
 * stdlib core/fs/primitives.rg 的 priv native 原语面落地；VM 侧镜像在
 * Bil/Vm/VmDispatch.cs 的 fs 区（双宿主同语义：错误分类、EOF 语义、
 * 挂起行为）。
 *
 * 形态约定：
 *   - 同步原语返回 i32：0 = 成功；< 0 = 失败，|码| = 归一错误码（下表，
 *     类 errno 钉死值；Windows GetLastError 经 rigi_fs_map_winerr 归一，
 *     Linux errno 经 rigi_fs_map_errno 归一；VM 侧 .NET 异常映射到同一
 *     套码）。错误分类（FileSystemErrorKind）的映射表在 Rigi 层
 *     （primitives.rg），C 侧只传归一码，不解析错误消息（§4.5.9）。
 *   - 复合输出（token / i64 / 定长结构 / 变长名称）经调用方给的
 *     Span<u8> 小端缓冲写出；变长输出（realpath / 目录条目名）缓冲
 *     不足返回正数哨兵 2 并经 meta 缓冲回所需字节数，Rigi 层放大重试
 *     一次——哨兵必须为正：负数空间全归 -归一码（2 = NotFound），正
 *     数空间归「需重试」（dirread 另用 1 = 结束）。
 *   - 读写/flush 等大操作挂起化（start/take 两段式，stdin 先例直复刻）：
 *     start 把阻塞系统调用卸载到现起 detached 后台线程（绝不占
 *     Compute Worker——§3.3/§4.5.6「底层等待不得阻塞 Compute Worker」），
 *     返回 0 = 已卸载（Rigi 层 yield 一次性 EventAlarm 挂起），< 0 =
 *     立即失败（不挂起）；后台线程完成登记后 rigi_event_signal 唤醒。
 *     take ≥ 0 = 结果（read/write 为字节数，flush 为 0），< 0 = -归一码。
 *   - open/close/seek/stat 类小操作同步直调：文件 open 用 O_NONBLOCK
 *     阻断 FIFO 无对端时的无限等待，按已打开 fd 判型，只接受普通文件；
 *     本地文件系统上其余元数据通常快速返回（close 走 NativeRc 析构
 *     回调）；read/write/flush 等待传输或持久化（可能任意久）才挂起。
 *   - 挂起期间保活（§3.2）：句柄由 Rigi 层 FileHandle（NativeRcHandle
 *     强引用）持有至 take 返回；缓冲区调用期间借用（调用协程挂起持有
 *     引用，后台线程读写无回收风险——stdin Span 借用先例）。
 *   - 路径编码（§4.5.2）：String（UTF-8）→ Windows 转 UTF-16 走
 *     CreateFileW 族（严格 MB_ERR_INVALID_CHARS，非法序列拒绝）；
 *     Linux 直接用 UTF-8 字节。无法无损表达的名称报 ENCODING 归一码
 *     （Rigi 层 InvalidNameEncoding），不替换、不跳过。Windows 长路径：
 *     绝对路径超阈值时内部加 \\?\ 前缀（内部原生前缀，不扩展用户
 *     路径语法；\\?\ 形态只认反斜杠，内部一并转换，Path 文本规则不变）。
 *
 * libuv 双形态（worker.c #if RIGI_HAS_LIBUV 先例）：同步面零 uv 依赖，
 * 双形态真实现；挂起面依赖 rigi_event_signal（libuv 底座），
 * RIGI_HAS_LIBUV 未定义时 start/take 为诊断 abort 版本（与
 * rigi_alarm_wait 无 libuv 降级同纪律——无 libuv 形态下 yield 分流
 * 先达 abort，本面不另设降级）。
 *
 * 线程选型：_beginthreadex（Windows，CRT 初始化）/ pthread_detach
 * （POSIX），与 shim.c stdin 读线程同款先例；现起线程比常驻 I/O 线程
 * 少一份交接队列（文件 I/O 频率低于常规事件源，简洁优先；后续若成
 * 热点再评估线程池化）。
 *
 * 本文件静态名一律 rigi_fs_ 前缀（unity build 单编译单元防碰撞）。
 */
#include "arc.h"
#include "coroutine.h"
#include "native_rc.h"
#include "rigi_string.h"
#include "worker.h" /* rigi_event_signal（RIGI_HAS_LIBUV 形态） */

#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00 /* FILE_ATTRIBUTE_TAG_INFO 等需要 Vista+ */
#endif
#include <windows.h>
#include <errno.h>
#include <process.h>
#include <wchar.h>
#else
#include <errno.h>
#include <limits.h> /* PATH_MAX（realpath resolved 形态的缓冲上限） */
#include <unistd.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <pthread.h>
#include <dirent.h>
#include <sys/syscall.h> /* SYS_renameat2 / SYS_statx（#ifdef 守护） */
#if defined(__linux__)
/* O_PATH 为 Linux 扩展，运行时构建启用 _DEFAULT_SOURCE 而非
 * _GNU_SOURCE；自钉 Linux ABI 常量，仅 ENXIO 失败补查时使用。 */
#define RIGI_FS_O_PATH 010000000
#include <linux/stat.h> /* struct statx / STATX_BTIME：不依赖 glibc 扩展入口 */
#include <sys/sysmacros.h> /* major/minor：二次查询时核文件身份 */
#endif
#endif
#include <stdatomic.h>

/* ===== 归一错误码（钉死值，双宿主/双平台一致；不借用平台 errno 名——
 * MSVC/glibc 同名值不一，自钉一套是映射表唯一事实源；取值对齐 Linux
 * errno 常见值便于诊断，26/1000/1001/1002 为自定义段）===== */
#define RIGI_FS_E_IO         5   /* → Other */
#define RIGI_FS_E_BADF       9   /* → Other（防御：Rigi 层句柄闸门先拦） */
#define RIGI_FS_E_NOMEM      12  /* → Other */
#define RIGI_FS_E_PERMISSION 13  /* → PermissionDenied */
#define RIGI_FS_E_NOTFOUND   2   /* → NotFound */
#define RIGI_FS_E_EXISTS     17  /* → AlreadyExists */
#define RIGI_FS_E_CROSSDEV   18  /* → CrossDevice */
#define RIGI_FS_E_NOTDIR     20  /* → NotDirectory */
#define RIGI_FS_E_ISDIR      21  /* → IsDirectory */
#define RIGI_FS_E_INVAL      22  /* → InvalidPath */
#define RIGI_FS_E_SHARING    26  /* → SharingViolation（Windows 共享/锁冲突；
                                    Linux ETXTBSY 同归此类） */
#define RIGI_FS_E_NOSPACE    28  /* → NoSpace（含 EDQUOT 配额满） */
#define RIGI_FS_E_RDONLY     30  /* → ReadOnlyFileSystem */
#define RIGI_FS_E_MLINK      31  /* → TooManyLinks */
#define RIGI_FS_E_NAMETOOLONG 36 /* → PathTooLong */
#define RIGI_FS_E_NOTEMPTY   39  /* → DirectoryNotEmpty */
#define RIGI_FS_E_LOOP       40  /* → Other（链接循环，不伪装 NotFound，
                                    §4.5.3） */
#define RIGI_FS_E_NOSYS      38  /* → Unsupported */
#define RIGI_FS_E_OTHER      1000 /* → Other（自定义段） */
#define RIGI_FS_E_ENCODING   1001 /* → InvalidNameEncoding（自定义段） */
#define RIGI_FS_E_WRONGTYPE  1002 /* → WrongType（非普通文件打开） */

/* open 标志位（Rigi 层 primitives.rg 镜像常量，双端一致；组合语义见
 * rigi_fs_open——FileWriteMode 五模式的映射在 Rigi 层 7-3） */
#define RIGI_FS_F_READ         0x01 /* 读（GENERIC_READ / O_RDONLY） */
#define RIGI_FS_F_WRITE        0x02 /* 写（GENERIC_WRITE / O_WRONLY） */
#define RIGI_FS_F_APPEND       0x04 /* 系统追加（FILE_APPEND_DATA / O_APPEND：
                                     每次写到达当时末尾，§4.5.6） */
#define RIGI_FS_F_CREATE       0x08 /* 不存在则创建（OPEN_ALWAYS / O_CREAT） */
#define RIGI_FS_F_TRUNCATE     0x10 /* 存在则截断（CREATE_ALWAYS / O_TRUNC） */
#define RIGI_FS_F_CREATE_NEW   0x20 /* 仅新建（CREATE_NEW / O_CREAT|O_EXCL：
                                     系统仅创建机制保证不覆盖，§4.5.6） */

#if defined(_WIN32)
/* GetLastError → 归一码（映射表与 VM 侧 MapFsError 的 Win32 段逐条
 * 对应，双宿主一致） */
static int32_t rigi_fs_map_winerr(DWORD err)
{
    switch (err)
    {
    case ERROR_FILE_NOT_FOUND:
    case ERROR_PATH_NOT_FOUND:
    case ERROR_INVALID_DRIVE:
        return RIGI_FS_E_NOTFOUND;
    case ERROR_ACCESS_DENIED:
    case ERROR_PRIVILEGE_NOT_HELD:
    case ERROR_NOACCESS:
        return RIGI_FS_E_PERMISSION;
    case ERROR_ALREADY_EXISTS:
    case ERROR_FILE_EXISTS:
        return RIGI_FS_E_EXISTS;
    case ERROR_NOT_SAME_DEVICE:
        return RIGI_FS_E_CROSSDEV;
    case ERROR_DIRECTORY:
        return RIGI_FS_E_NOTDIR;
    case ERROR_DIR_NOT_EMPTY:
        return RIGI_FS_E_NOTEMPTY;
    case ERROR_DISK_FULL:
    case ERROR_HANDLE_DISK_FULL:
        return RIGI_FS_E_NOSPACE;
    case ERROR_SHARING_VIOLATION:
    case ERROR_LOCK_VIOLATION:
        return RIGI_FS_E_SHARING;
    case ERROR_FILENAME_EXCED_RANGE:
    case ERROR_BUFFER_OVERFLOW:
        return RIGI_FS_E_NAMETOOLONG;
    case ERROR_INVALID_NAME:
    case ERROR_INVALID_PARAMETER:
    case ERROR_NEGATIVE_SEEK:
        return RIGI_FS_E_INVAL;
    case ERROR_INVALID_HANDLE:
        return RIGI_FS_E_BADF;
    case ERROR_OUTOFMEMORY:
        return RIGI_FS_E_NOMEM;
    case ERROR_NOT_SUPPORTED:
        return RIGI_FS_E_NOSYS;
    case ERROR_NO_UNICODE_TRANSLATION:
        return RIGI_FS_E_ENCODING;
    case ERROR_CANT_ACCESS_FILE:
        /* 断链符号链接的跟随打开（stat/realpath）：目标确实不可达，
         * 归 NotFound（链接自身识别走 lstat 面） */
        return RIGI_FS_E_NOTFOUND;
    case ERROR_WRITE_FAULT:
    case ERROR_READ_FAULT:
    case ERROR_GEN_FAILURE:
        return RIGI_FS_E_IO;
    default:
        return RIGI_FS_E_OTHER;
    }
}
#else
/* errno → 归一码（值与归一码一致者同名直归；EDQUOT/ETXTBSY/EOPNOTSUPP
 * 归并，注释标注） */
static int32_t rigi_fs_map_errno(int e)
{
    switch (e)
    {
    case ENOENT: return RIGI_FS_E_NOTFOUND;
    case EPERM:
    case EACCES: return RIGI_FS_E_PERMISSION;
    case EEXIST: return RIGI_FS_E_EXISTS;
    case EXDEV: return RIGI_FS_E_CROSSDEV;
    case ENOTDIR: return RIGI_FS_E_NOTDIR;
    case EISDIR: return RIGI_FS_E_ISDIR;
    case EINVAL: return RIGI_FS_E_INVAL;
    case ETXTBSY: return RIGI_FS_E_SHARING; /* 文本文件忙 ≈ 共享冲突 */
    case ENOSPC: return RIGI_FS_E_NOSPACE;
    case EDQUOT: return RIGI_FS_E_NOSPACE; /* 配额满按 NoSpace */
    case EROFS: return RIGI_FS_E_RDONLY;
    case EMLINK: return RIGI_FS_E_MLINK;
    case ENAMETOOLONG: return RIGI_FS_E_NAMETOOLONG;
    case ENOTEMPTY: return RIGI_FS_E_NOTEMPTY;
    case ELOOP: return RIGI_FS_E_LOOP;
    case EBADF: return RIGI_FS_E_BADF;
    case ENOMEM: return RIGI_FS_E_NOMEM;
    case ENOSYS: return RIGI_FS_E_NOSYS;
    case EOPNOTSUPP: return RIGI_FS_E_NOSYS;
    case EIO: return RIGI_FS_E_IO;
    default: return RIGI_FS_E_OTHER;
    }
}
#endif

/* ===== 路径编码（§4.5.2：只支持可无损表达的名称；不替换不跳过）===== */

#if defined(_WIN32)
/* UTF-8 → UTF-16 严格转换（MB_ERR_INVALID_CHARS：非法 UTF-8 序列拒绝
 * → ERROR_NO_UNICODE_TRANSLATION → ENCODING 归一码；Rigi 层
 * InvalidNameEncoding）。返回 0 成功；< 0 = -归一码。*out 为
 * rigi_track_malloc 分配的 NUL 结尾缓冲（调用方 rigi_track_free）。 */
static int32_t rigi_fs_utf8_to_wide(const char *data, int64_t len,
    wchar_t **out)
{
    int wlen;
    wchar_t *buf;
    if (len > (int64_t)0x7FFFFFFE)
    {
        return -RIGI_FS_E_NAMETOOLONG; /* 防御（i32 上限内） */
    }
    wlen = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, (int)len,
        NULL, 0);
    if (wlen == 0)
    {
        return -rigi_fs_map_winerr(GetLastError());
    }
    buf = (wchar_t *)rigi_track_malloc(((size_t)wlen + 1) * sizeof(wchar_t));
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, (int)len,
            buf, wlen) == 0)
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        rigi_track_free(buf);
        return -e;
    }
    buf[wlen] = 0;
    *out = buf;
    return 0;
}

/* 长路径内部前缀（§4.5.2「内部使用的原生路径前缀不作为用户路径语法
 * 扩展」「正确接入 Unicode 与长路径能力，不在库内统一施加 260 字符
 * 限制」）：绝对路径（盘符/UNC）且 wchar 长度超阈值 → \\?\ 前缀；
 * \\?\ 形态只认反斜杠——内部转换 / → \（Path 文本不做分隔符重写的
 * 规则不受影响）。返回 0 成功；< 0 = -归一码。 */
static int32_t rigi_fs_wide_long_path(wchar_t **buf, int *len)
{
    int wlen = *len;
    wchar_t *src = *buf;
    int need;
    wchar_t *out;
    int i;
    int is_unc = (wlen >= 2 && src[0] == L'\\' && src[1] == L'\\');
    int is_drive = (wlen >= 3 && src[1] == L':'
        && (src[2] == L'\\' || src[2] == L'/'));
    if ((!is_unc && !is_drive) || wlen < 248)
    {
        return 0; /* 相对路径/短路径：不加前缀 */
    }
    need = wlen + 8; /* \\?\UNC\ 或 \\?\ + NUL */
    out = (wchar_t *)rigi_track_malloc((size_t)need * sizeof(wchar_t));
    if (is_unc)
    {
        /* \\server\share\... → \\?\UNC\server\share\... */
        int j = 0;
        const wchar_t *prefix = L"\\\\?\\UNC\\";
        while (*prefix) { out[j++] = *prefix++; }
        for (i = 2; i < wlen; i++)
        {
            out[j++] = src[i] == L'/' ? L'\\' : src[i];
        }
        out[j] = 0;
    }
    else
    {
        int j = 0;
        const wchar_t *prefix = L"\\\\?\\";
        while (*prefix) { out[j++] = *prefix++; }
        for (i = 0; i < wlen; i++)
        {
            out[j++] = src[i] == L'/' ? L'\\' : src[i];
        }
        out[j] = 0;
    }
    rigi_track_free(src);
    *buf = out;
    *len = wcslen(out);
    return 0;
}
#endif /* _WIN32 */

/* ===== 句柄实体与 NativeRc 生命周期 ===== */

/* 文件句柄：fd/HANDLE + 挂起在途槽（读；写/flush 槽在后续原语族接入）。
 * 在途槽闸内访问；同一句柄同类同时刻至多一个在途操作——契约禁止同
 * 流并发/重入（§4.4），冲突属运行时 bug，诊断 abort（stdin 先例）。 */
typedef struct RigiFsFile
{
#if defined(_WIN32)
    HANDLE handle; /* INVALID_HANDLE_VALUE = 已关闭（防御） */
#else
    int fd; /* -1 = 已关闭（防御） */
#endif
    _Atomic int gate; /* CAS 自旋闸：临界区极小（拷出/登记十几字节），
                         纯 CAS 足够（stdin 先例，不引平台锁） */
    int read_inflight;
    int read_done;
    int32_t read_result;
    uint8_t *read_dest;
    int32_t read_want;
    int64_t read_wake;
    int write_inflight;
    int write_done;
    int32_t write_result;
    const uint8_t *write_src;
    int32_t write_want;
    int64_t write_wake;
    int flush_inflight;
    int flush_done;
    int32_t flush_result;
    int64_t flush_wake;
    int64_t token; /* NativeRc 注册表身份；Carriage 仅携带此值 */
} RigiFsFile;

static RigiFsFile *rigi_fs_file_of(int64_t token, const char *face)
{
    if (token == 0)
    {
        fprintf(stderr, "rigi_rt: %s 收到空句柄（编译器 bug）\n", face);
        abort();
    }
    return (RigiFsFile *)rigi_native_rc_payload_of(token, face);
}

/* NativeRc 析构回调（最后一个强引用 release 时触发）= 关闭文件并释放
 * 结构。close 错误不上报：持久化错误归 flush 面（§4.5.6 flush 契约），
 * close 只释放句柄资源（与 .NET FileStream.Dispose 同步 close 同口径）。 */
static void rigi_fs_file_destroy(void *payload)
{
    RigiFsFile *f = (RigiFsFile *)payload;
#if defined(_WIN32)
    if (f->handle != INVALID_HANDLE_VALUE)
    {
        CloseHandle(f->handle);
        f->handle = INVALID_HANDLE_VALUE;
    }
#else
    if (f->fd >= 0)
    {
        close(f->fd);
        f->fd = -1;
    }
#endif
    rigi_track_free(f);
}

/* Span<u8> 共用解包（shim.c rigi_stdio_read_span_dest 同口径）：返回
 * payload 内目标地址 = 元素基址 32 + offset。区间越界属编译器 bug
 *（Rigi 包装层先行校验；VM 侧同形态抛 VmException），诊断 abort */
static uint8_t *rigi_fs_span_at(const RigiFatRef *buffer, int32_t offset,
    int32_t count, const char *face)
{
    if (buffer == NULL || buffer->payload == 0)
    {
        fprintf(stderr, "rigi_rt: %s 收到空缓冲区（编译器 bug）\n", face);
        abort();
    }
    uint8_t *base = (uint8_t *)(uintptr_t)buffer->payload;
    int32_t length;
    memcpy(&length, base + 24, sizeof(int32_t));
    if (offset < 0 || count < 0 || offset > length - count)
    {
        fprintf(stderr, "rigi_rt: %s 区间越界：offset=%d count=%d "
            "length=%d（编译器 bug）\n", face, offset, count, length);
        abort();
    }
    return base + 32 + offset;
}

/* i64 小端写入出参 Span（token/长度等定长输出；out 区间 [0,8) 由 Rigi
 * 层保证，防御校验同 span 口径） */
static void rigi_fs_write_i64le(uint8_t *dst, int64_t value)
{
    int i;
    for (i = 0; i < 8; i++)
    {
        dst[i] = (uint8_t)((uint64_t)value >> (i * 8));
    }
}

/* ===== fs_open（同步直调）=====
 * Linux 用 O_NONBLOCK 防 FIFO 等待，按打开句柄判型，仅普通文件成功。
 * 命名空间/元数据操作，本地文件系统上通常快速返回，不挂起（见文件头
 * 形态约定）。flags 组合（Rigi 层 FileWriteMode 映射）：
 *   读 = READ（只读打开已有文件）
 *   写 = WRITE（OpenExisting 只写已有不截断）
 *   写|CREATE_NEW = 仅新建（CreateNew，系统仅创建机制）
 *   写|CREATE|TRUNCATE = 创建或截断（CreateOrTruncate）
 *   写|CREATE = 打开或创建不截断（OpenOrCreate）
 *   写|APPEND|CREATE = 追加（Append；不存在则创建，系统追加机制使
 *   每次写入到达当时末尾，§4.5.6）
 * mode：POSIX 创建权限位（0666 受 umask，§4.5.5 宿主权限规则）；
 * Windows 忽略（正常继承的安全描述符）。成功：out[0..8) 写 NativeRc
 * token；返回 0。失败返回 -归一码（目录当文件开归 E_ISDIR——
 * ERROR_ACCESS_DENIED 与权限不足不可分，失败路径补查目标属性，仅错误
 * 路径非「先查询再打开」）。 */
int32_t rigi_fs_open(const rigi_string *path, int32_t flags, int32_t mode,
    const RigiFatRef *out)
{
    uint8_t *out8;
    RigiFsFile *f;
    if (path == NULL || path->data == NULL || path->len < 0
        || path->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_open 收到非法路径参数（编译器 bug）\n");
        abort();
    }
    out8 = rigi_fs_span_at(out, 0, 8, "rigi_fs_open");
#if defined(_WIN32)
    wchar_t *wide = NULL;
    HANDLE h;
    DWORD access = 0;
    DWORD disp;
    int32_t rc = rigi_fs_utf8_to_wide(path->data, path->len, &wide);
    int wlen;
    if (rc != 0)
    {
        return rc;
    }
    wlen = (int)wcslen(wide);
    rc = rigi_fs_wide_long_path(&wide, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wide);
        return rc;
    }
    if (flags & RIGI_FS_F_READ) { access |= GENERIC_READ; }
    if (flags & RIGI_FS_F_APPEND) {
        /* 追加（§4.5.6：系统追加机制使每次写入始终到达当时末尾）。
         * FILE_APPEND_DATA 使 NtWriteFile 忽略句柄当前位置、每次写落位
         * 当时末尾；FILE_READ_ATTRIBUTES 供 §4.5.6「具体文件流提供
         * getLength」的长度查询（GetFileSizeEx 查 FileStandardInformation
         * 需要该权限位；不含 GENERIC_READ，不放开内容读）——7-3 文件流
         * 的追加形态保留 getLength 面所需 */
        access |= FILE_APPEND_DATA | FILE_READ_ATTRIBUTES;
    }
    else if (flags & RIGI_FS_F_WRITE) { access |= GENERIC_WRITE; }
    if (flags & RIGI_FS_F_CREATE_NEW) { disp = CREATE_NEW; }
    else if ((flags & RIGI_FS_F_CREATE) && (flags & RIGI_FS_F_TRUNCATE))
    {
        disp = CREATE_ALWAYS;
    }
    else if (flags & RIGI_FS_F_CREATE) { disp = OPEN_ALWAYS; }
    else { disp = OPEN_EXISTING; }
    h = CreateFileW(wide, access,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, disp,
        FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE)
    {
        DWORD err = GetLastError();
        if (err == ERROR_ACCESS_DENIED)
        {
            /* 目录当文件开：ACCESS_DENIED 与权限不足不可分——失败路径
             * 补查目标属性（仅错误路径，非常规「先查询再打开」） */
            DWORD attr = GetFileAttributesW(wide);
            if (attr != INVALID_FILE_ATTRIBUTES
                && (attr & FILE_ATTRIBUTE_DIRECTORY))
            {
                rigi_track_free(wide);
                return -RIGI_FS_E_ISDIR;
            }
        }
        rigi_track_free(wide);
        return -rigi_fs_map_winerr(err);
    }
    rigi_track_free(wide);
    f = (RigiFsFile *)rigi_track_malloc(sizeof(RigiFsFile));
    f->handle = h;
#else
    char *zpath;
    int fd;
    int oflags = 0;
    if (path->len == 0)
    {
        return -RIGI_FS_E_INVAL; /* Path 层已拒空文本，防御 */
    }
    zpath = (char *)rigi_track_malloc((size_t)path->len + 1);
    memcpy(zpath, path->data, (size_t)path->len);
    zpath[path->len] = 0;
    if (flags & RIGI_FS_F_READ) { oflags |= O_RDONLY; }
    else { oflags |= O_WRONLY; }
    if (flags & RIGI_FS_F_APPEND) { oflags |= O_APPEND; }
    /* FIFO 的只读/只写 open 在另一端缺席时会无限等待；只对 Linux
     * 使用 O_NONBLOCK，使打开立即返回，再按已打开 fd 的 fstat 分类。
     * 确认普通文件后清除 O_NONBLOCK，保持原有后续 I/O 句柄语义。 */
#if defined(__linux__)
    oflags |= O_NONBLOCK;
#endif
    if (flags & RIGI_FS_F_CREATE_NEW) { oflags |= O_CREAT | O_EXCL; }
    else
    {
        if (flags & RIGI_FS_F_CREATE) { oflags |= O_CREAT; }
        if (flags & RIGI_FS_F_TRUNCATE) { oflags |= O_TRUNC; }
    }
    fd = open(zpath, oflags, (mode_t)mode);
    if (fd < 0)
    {
        int e = errno;
#if defined(__linux__)
        if (e == ENXIO)
        {
            /* 无 fd 的 ENXIO：FIFO 写端无读者，或 AF_UNIX pathname
             * socket 无法按普通文件 open。只在失败路径用 O_PATH 取得
             * 单独句柄再 fstat 判型，不先 stat 路径；非普通文件归
             * WrongType，其余及补查失败仍保留原始 ENXIO。 */
            int probe = open(zpath, RIGI_FS_O_PATH | O_CLOEXEC);
            if (probe >= 0)
            {
                struct stat probed;
                int wrong = fstat(probe, &probed) == 0
                    && !S_ISREG(probed.st_mode)
                    && !S_ISDIR(probed.st_mode);
                close(probe);
                if (wrong)
                {
                    rigi_track_free(zpath);
                    return -RIGI_FS_E_WRONGTYPE;
                }
            }
        }
#endif
        rigi_track_free(zpath);
        return -rigi_fs_map_errno(e);
    }
    rigi_track_free(zpath);
    /* Linux O_RDONLY 可以打开真实目录；文件面必须按已经打开的句柄
     * 身份拒绝目录（不能事先 stat 路径，以免分类与打开对象竞态）。 */
    struct stat opened;
    if (fstat(fd, &opened) != 0)
    {
        int e = errno;
        close(fd);
        return -rigi_fs_map_errno(e);
    }
    if (S_ISDIR(opened.st_mode))
    {
        close(fd);
        return -RIGI_FS_E_ISDIR;
    }
    if (!S_ISREG(opened.st_mode))
    {
        close(fd);
        return -RIGI_FS_E_WRONGTYPE;
    }
#if defined(__linux__)
    /* 普通文件不再需要仅为防 FIFO 挂起设置的非阻塞状态。 */
    int fdflags = fcntl(fd, F_GETFL);
    if (fdflags < 0 || fcntl(fd, F_SETFL, fdflags & ~O_NONBLOCK) < 0)
    {
        int e = errno;
        close(fd);
        return -rigi_fs_map_errno(e);
    }
#endif
    f = (RigiFsFile *)rigi_track_malloc(sizeof(RigiFsFile));
    f->fd = fd;
#endif
    atomic_init(&f->gate, 0);
    f->read_inflight = 0;
    f->read_done = 0;
    f->read_result = 0;
    f->read_dest = NULL;
    f->read_want = 0;
    f->read_wake = 0;
    f->write_inflight = 0;
    f->write_done = 0;
    f->write_result = 0;
    f->write_src = NULL;
    f->write_want = 0;
    f->write_wake = 0;
    f->flush_inflight = 0;
    f->flush_done = 0;
    f->flush_result = 0;
    f->flush_wake = 0;
    f->token = rigi_native_rc_create(f, rigi_fs_file_destroy);
    rigi_fs_write_i64le(out8, f->token);
    return 0;
}

/* ===== 挂起读（start/take 两段式，stdin 先例直复刻）===== */

#ifdef RIGI_HAS_LIBUV

/* CAS 自旋闸：临界区极小（拷出/登记十几字节），纯 CAS 足够（stdin
 * 先例，不引平台锁）。只服务挂起面（同步原语单次系统调用内完成，
 * 不跨 await——无需闸） */
static void rigi_fs_lock(RigiFsFile *f)
{
    int expected = 0;
    while (!atomic_compare_exchange_weak_explicit(&f->gate, &expected, 1,
        memory_order_acquire, memory_order_relaxed))
    {
        expected = 0;
    }
}

static void rigi_fs_unlock(RigiFsFile *f)
{
    atomic_store_explicit(&f->gate, 0, memory_order_release);
}

#if defined(_WIN32)
/* Windows 读线程体（_beginthreadex 约定）：返回即线程终止，句柄由
 * 创建侧立即关闭（detach 语义——不等待读完成） */
static unsigned __stdcall rigi_fs_read_main(void *arg)
{
    RigiFsFile *f = (RigiFsFile *)arg;
#else
static void *rigi_fs_read_main(void *arg)
{
    RigiFsFile *f = (RigiFsFile *)arg;
    pthread_detach(pthread_self());
#endif
    /* 请求参数在 start 闸内拷出后离闸使用（stdin 先例） */
    rigi_fs_lock(f);
    uint8_t *dest = f->read_dest;
    int32_t want = f->read_want;
    int64_t wake = f->read_wake;
    rigi_fs_unlock(f);
    int64_t n;
#if defined(_WIN32)
    DWORD got = 0;
    /* ReadFile 单次阻塞读：TRUE + got = 实际字节数（允许少于请求数；
     * got == 0 = 当前 EOF——§4.5.6：EOF 不粘滞，之后再次读取可以看到
     * 新增内容，故无 stdin 式 EOF 短路，每轮 start 都真读） */
    if (ReadFile(f->handle, dest, (DWORD)want, &got, NULL))
    {
        n = (int64_t)got;
    }
    else
    {
        n = -(int64_t)rigi_fs_map_winerr(GetLastError());
    }
#else
    /* 单次系统调用阻塞读：n > 0 实际字节数；0 = EOF；< 0 错误。
     * EINTR（信号中断）重试——不是流错误 */
    do
    {
        n = read(f->fd, dest, (size_t)want);
    } while (n < 0 && errno == EINTR);
    if (n < 0)
    {
        n = -(int64_t)rigi_fs_map_errno(errno);
    }
#endif
    /* 结果登记先于事件触发（§19.3 握手的 signal 侧顺序——恢复协程必见
     * 结果）；rigi_event_signal 粘滞幂等，闸外调用 */
    rigi_fs_lock(f);
    f->read_result = (int32_t)n;
    f->read_done = 1;
    f->read_inflight = 0;
    rigi_fs_unlock(f);
    rigi_event_signal(wake);
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}

/* 启动一次挂起读（fs_read_start 原语面）：把 buffer[offset..offset+count)
 * 的读请求卸载到现起 detached 后台线程（绝不占 Compute Worker），完成
 * 后该线程触发 wakeHandle 事件。返回 0 = 已卸载（Rigi 层挂起等唤醒后
 * fs_read_take 取结果）；< 0 = 立即失败（不挂起，Rigi 层直接映射抛错）。
 * 挂起期间保活：句柄由 Rigi 层 FileHandle 强引用持有至 take 返回；
 * 缓冲区调用期间借用（§3.2，stdin Span 借用先例） */
int32_t rigi_fs_read_start(int64_t handle, const RigiFatRef *buffer,
    int32_t offset, int32_t count, int64_t wake)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_read_start");
    uint8_t *dest = rigi_fs_span_at(buffer, offset, count,
        "rigi_fs_read_start");
    rigi_fs_lock(f);
    if (f->read_inflight)
    {
        fprintf(stderr, "rigi_rt: fs 句柄已有在途读（同流并发/重入违反"
            "契约）\n");
        abort();
    }
    f->read_dest = dest;
    f->read_want = count;
    f->read_wake = wake;
    f->read_inflight = 1;
    f->read_done = 0;
    rigi_fs_unlock(f);
#if defined(_WIN32)
    /* _beginthreadex 做 CRT 初始化；句柄立即关闭即 detach——读完成由
     * 事件通道异步交付（stdin 先例） */
    uintptr_t thread = _beginthreadex(NULL, 0, rigi_fs_read_main, f, 0,
        NULL);
    if (thread == 0)
    {
        fprintf(stderr, "rigi_rt: fs 读线程创建失败（环境耗尽）\n");
        abort();
    }
    CloseHandle((HANDLE)thread);
#else
    pthread_t thread;
    if (pthread_create(&thread, NULL, rigi_fs_read_main, f) != 0)
    {
        fprintf(stderr, "rigi_rt: fs 读线程创建失败（环境耗尽）\n");
        abort();
    }
#endif
    return 0;
}

/* 取上一次挂起读的结果（fs_read_take 原语面）：只发生在事件唤醒之后
 *（结果登记 happens-before signal，恢复必见 done），未就绪即取属时序
 * bug，防御诊断 abort（stdin 先例）。返回 ≥ 0 = 实际读取字节数（0 =
 * 当前 EOF，不粘滞）；< 0 = -归一码 */
int32_t rigi_fs_read_take(int64_t handle)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_read_take");
    rigi_fs_lock(f);
    if (!f->read_done)
    {
        fprintf(stderr, "rigi_rt: fs_read_take 结果未就绪（时序 bug）\n");
        abort();
    }
    int32_t result = f->read_result;
    f->read_done = 0;
    rigi_fs_unlock(f);
    return result;
}

/* ===== 挂起写 / 挂起 flush（read 同款两段式）===== */

#if defined(_WIN32)
/* Windows 写线程体：单次阻塞写（部分写允许——返回值即实际写出字节，
 * Rigi 层 7-3 文件流负责循环补齐）；append 语义由 open 时的
 * FILE_APPEND_DATA 句柄保证（每次写到达当时末尾，§4.5.6） */
static unsigned __stdcall rigi_fs_write_main(void *arg)
{
    RigiFsFile *f = (RigiFsFile *)arg;
#else
static void *rigi_fs_write_main(void *arg)
{
    RigiFsFile *f = (RigiFsFile *)arg;
    pthread_detach(pthread_self());
#endif
    rigi_fs_lock(f);
    const uint8_t *src = f->write_src;
    int32_t want = f->write_want;
    int64_t wake = f->write_wake;
    rigi_fs_unlock(f);
    int64_t n;
#if defined(_WIN32)
    DWORD put = 0;
    if (WriteFile(f->handle, src, (DWORD)want, &put, NULL))
    {
        n = (int64_t)put;
    }
    else
    {
        n = -(int64_t)rigi_fs_map_winerr(GetLastError());
    }
#else
    do
    {
        n = write(f->fd, src, (size_t)want);
    } while (n < 0 && errno == EINTR);
    if (n < 0)
    {
        n = -(int64_t)rigi_fs_map_errno(errno);
    }
#endif
    /* 结果登记先于事件触发（read 同序——恢复协程必见结果） */
    rigi_fs_lock(f);
    f->write_result = (int32_t)n;
    f->write_done = 1;
    f->write_inflight = 0;
    rigi_fs_unlock(f);
    rigi_event_signal(wake);
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}

/* 启动一次挂起写（fs_write_start 原语面）：把 buffer[offset..offset+count)
 * 的写请求卸载到现起 detached 后台线程（绝不占 Compute Worker），完成
 * 后触发 wakeHandle。返回 0 = 已卸载（挂起等唤醒后 fs_write_take）；
 * < 0 = 立即失败（不挂起）。返回值 ≥ 0 = 实际写出字节数（允许少于
 * count 的部分写，7-3 层循环补齐）。缓冲区调用期间借用（§3.2） */
int32_t rigi_fs_write_start(int64_t handle, const RigiFatRef *buffer,
    int32_t offset, int32_t count, int64_t wake)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_write_start");
    const uint8_t *src = rigi_fs_span_at(buffer, offset, count,
        "rigi_fs_write_start");
    rigi_fs_lock(f);
    if (f->write_inflight)
    {
        fprintf(stderr, "rigi_rt: fs 句柄已有在途写（同流并发/重入违反"
            "契约）\n");
        abort();
    }
    f->write_src = src;
    f->write_want = count;
    f->write_wake = wake;
    f->write_inflight = 1;
    f->write_done = 0;
    rigi_fs_unlock(f);
#if defined(_WIN32)
    uintptr_t thread = _beginthreadex(NULL, 0, rigi_fs_write_main, f, 0,
        NULL);
    if (thread == 0)
    {
        fprintf(stderr, "rigi_rt: fs 写线程创建失败（环境耗尽）\n");
        abort();
    }
    CloseHandle((HANDLE)thread);
#else
    pthread_t thread;
    if (pthread_create(&thread, NULL, rigi_fs_write_main, f) != 0)
    {
        fprintf(stderr, "rigi_rt: fs 写线程创建失败（环境耗尽）\n");
        abort();
    }
#endif
    return 0;
}

/* 取上一次挂起写的结果（fs_write_take 原语面，read_take 同口径）：
 * ≥ 0 = 实际写出字节数；< 0 = -归一码 */
int32_t rigi_fs_write_take(int64_t handle)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_write_take");
    rigi_fs_lock(f);
    if (!f->write_done)
    {
        fprintf(stderr, "rigi_rt: fs_write_take 结果未就绪（时序 bug）\n");
        abort();
    }
    int32_t result = f->write_result;
    f->write_done = 0;
    rigi_fs_unlock(f);
    return result;
}

#if defined(_WIN32)
/* Windows flush 线程体：FlushFileBuffers（§4.5.6 系统持久化刷新，
 * 不是库缓冲提交） */
static unsigned __stdcall rigi_fs_flush_main(void *arg)
{
    RigiFsFile *f = (RigiFsFile *)arg;
#else
static void *rigi_fs_flush_main(void *arg)
{
    RigiFsFile *f = (RigiFsFile *)arg;
    pthread_detach(pthread_self());
#endif
    rigi_fs_lock(f);
    int64_t wake = f->flush_wake;
    rigi_fs_unlock(f);
    int32_t rc;
#if defined(_WIN32)
    rc = FlushFileBuffers(f->handle) ? 0
        : -rigi_fs_map_winerr(GetLastError());
#else
    int r;
    /* fsync 可被信号中断（EINTR）——重试不是流错误 */
    do
    {
        r = fsync(f->fd);
    } while (r != 0 && errno == EINTR);
    rc = r == 0 ? 0 : -rigi_fs_map_errno(errno);
#endif
    rigi_fs_lock(f);
    f->flush_result = rc;
    f->flush_done = 1;
    f->flush_inflight = 0;
    rigi_fs_unlock(f);
    rigi_event_signal(wake);
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}

/* 启动一次挂起 flush（fs_flush_start 原语面）：等待系统持久化刷新
 * 完成（Windows FlushFileBuffers / Linux fsync，§4.5.6），卸载到后台
 * 线程不占 Compute Worker。返回 0 = 已卸载；< 0 = 立即失败 */
int32_t rigi_fs_flush_start(int64_t handle, int64_t wake)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_flush_start");
    rigi_fs_lock(f);
    if (f->flush_inflight)
    {
        fprintf(stderr, "rigi_rt: fs 句柄已有在途 flush（同流并发/重入"
            "违反契约）\n");
        abort();
    }
    f->flush_wake = wake;
    f->flush_inflight = 1;
    f->flush_done = 0;
    rigi_fs_unlock(f);
#if defined(_WIN32)
    uintptr_t thread = _beginthreadex(NULL, 0, rigi_fs_flush_main, f, 0,
        NULL);
    if (thread == 0)
    {
        fprintf(stderr, "rigi_rt: fs flush 线程创建失败（环境耗尽）\n");
        abort();
    }
    CloseHandle((HANDLE)thread);
#else
    pthread_t thread;
    if (pthread_create(&thread, NULL, rigi_fs_flush_main, f) != 0)
    {
        fprintf(stderr, "rigi_rt: fs flush 线程创建失败（环境耗尽）\n");
        abort();
    }
#endif
    return 0;
}

/* 取上一次挂起 flush 的结果（fs_flush_take 原语面）：0 = 持久化完成；
 * < 0 = -归一码 */
int32_t rigi_fs_flush_take(int64_t handle)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_flush_take");
    rigi_fs_lock(f);
    if (!f->flush_done)
    {
        fprintf(stderr, "rigi_rt: fs_flush_take 结果未就绪（时序 bug）\n");
        abort();
    }
    int32_t result = f->flush_result;
    f->flush_done = 0;
    rigi_fs_unlock(f);
    return result;
}

#else /* !RIGI_HAS_LIBUV：挂起面降级诊断 abort（worker.c 同纪律；无
       * libuv 形态下 yield 分流先达 rigi_alarm_wait abort，本面不另设
       * 降级） */

int32_t rigi_fs_read_start(int64_t handle, const RigiFatRef *buffer,
    int32_t offset, int32_t count, int64_t wake)
{
    (void)handle; (void)buffer; (void)offset; (void)count; (void)wake;
    fprintf(stderr,
        "rigi_rt: 文件挂起读原语需要 libuv 能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

int32_t rigi_fs_read_take(int64_t handle)
{
    (void)handle;
    fprintf(stderr,
        "rigi_rt: 文件挂起读原语需要 libuv 能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

int32_t rigi_fs_write_start(int64_t handle, const RigiFatRef *buffer,
    int32_t offset, int32_t count, int64_t wake)
{
    (void)handle; (void)buffer; (void)offset; (void)count; (void)wake;
    fprintf(stderr,
        "rigi_rt: 文件挂起写原语需要 libuv 能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

int32_t rigi_fs_write_take(int64_t handle)
{
    (void)handle;
    fprintf(stderr,
        "rigi_rt: 文件挂起写原语需要 libuv 能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

int32_t rigi_fs_flush_start(int64_t handle, int64_t wake)
{
    (void)handle; (void)wake;
    fprintf(stderr,
        "rigi_rt: 文件挂起 flush 原语需要 libuv 能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

int32_t rigi_fs_flush_take(int64_t handle)
{
    (void)handle;
    fprintf(stderr,
        "rigi_rt: 文件挂起 flush 原语需要 libuv 能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

#endif /* RIGI_HAS_LIBUV */

/* ===== 同步原语族（命名空间/元数据操作，open 同分界：本地文件系统上
 * 通常快速返回，不挂起——见文件头形态约定）===== */

/* ===== stat 结构（48 字节小端定长，Rigi 层 FsStatInfo 解析；时间以
 * (epoch 毫秒 i64, 纳秒余量 i32) 对携带亚毫秒部分——core.time.TimeStamp
 * 的自然形态；毫秒位 = INT64_MIN 表示该字段宿主不可得，Rigi 层组装成
 * null，不伪造时钟精度也不用元数据变更时间冒充创建时间，§4.5.3）=====
 *   [0..4)   kind: i32   0=File 1=Directory 2=Link 3=Other
 *   [4..12)  length: i64 普通文件字节长度；非普通文件 -1（不伪造长度，
 *                        §4.5.3「非普通文件不伪造长度」）
 *   [12..20) mtime ms / [20..24) mtime ns
 *   [24..32) atime ms / [32..36) atime ns
 *   [36..44) birth ms / [44..48) birth ns
 */
#define RIGI_FS_STAT_SIZE 48

static void rigi_fs_fill_stat(uint8_t *out, int32_t kind, int64_t length,
    int64_t mtms, int32_t mtns, int64_t atms, int32_t atns, int64_t btms,
    int32_t btns)
{
    int32_t k32 = kind;
    memcpy(out, &k32, 4);
    rigi_fs_write_i64le(out + 4, length);
    rigi_fs_write_i64le(out + 12, mtms);
    rigi_fs_write_i64le(out + 24, atms);
    rigi_fs_write_i64le(out + 36, btms);
    int32_t n;
    n = mtns;
    memcpy(out + 20, &n, 4);
    n = atns;
    memcpy(out + 32, &n, 4);
    n = btns;
    memcpy(out + 44, &n, 4);
}

#if defined(_WIN32)
/* FILETIME（1601 纪 100ns）→ (epoch 毫秒, 纳秒余量)；零值 = 宿主不可得
 * → 哨兵（Windows 对不支持的字段给零 FILETIME） */
static void rigi_fs_ft_parts(const FILETIME *ft, int64_t *ms, int32_t *ns)
{
    int64_t v = ((int64_t)ft->dwHighDateTime << 32) | ft->dwLowDateTime;
    if (v == 0)
    {
        *ms = INT64_MIN;
        *ns = 0;
        return;
    }
    int64_t epoch100 = v - 116444736000000000LL;
    *ms = epoch100 / 10000;
    *ns = (int32_t)((epoch100 % 10000) * 100);
}
#else
/* timespec → (epoch 毫秒, 纳秒余量)。秒值超出 i64 毫秒可表示范围（远超
 * 文件时间实际范围）按不可得哨兵处理——不窄化数值掩盖范围问题（§4.5.3） */
static void rigi_fs_ts_parts(int64_t sec, int64_t nsec, int64_t *ms,
    int32_t *ns)
{
    if (sec > (INT64_MAX / 1000 - 1) || sec < (INT64_MIN / 1000 + 1))
    {
        *ms = INT64_MIN;
        *ns = 0;
        return;
    }
    *ms = sec * 1000 + nsec / 1000000;
    *ns = (int32_t)(nsec % 1000000);
}
#endif

/* ===== 定位与长度（句柄操作，同步）===== */

/* fs_seek：whence 0=SET 1=CUR 2=END；成功 out[0..8) 写新绝对位置并返回
 * 0；结果位置为负（越界）宿主报错归 INVAL（22），Rigi 层按范围错误抛
 * OutOfBoundException（§4.5.9 数值范围沿用既有异常） */
int32_t rigi_fs_seek(int64_t handle, int64_t offset, int32_t whence,
    const RigiFatRef *out)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_seek");
    uint8_t *out8 = rigi_fs_span_at(out, 0, 8, "rigi_fs_seek");
    if (whence < 0 || whence > 2)
    {
        return -RIGI_FS_E_INVAL;
    }
#if defined(_WIN32)
    LARGE_INTEGER off;
    LARGE_INTEGER newPos;
    off.QuadPart = offset;
    static const DWORD map[3] = { FILE_BEGIN, FILE_CURRENT, FILE_END };
    if (!SetFilePointerEx(f->handle, off, &newPos, map[whence]))
    {
        return -rigi_fs_map_winerr(GetLastError());
    }
    rigi_fs_write_i64le(out8, newPos.QuadPart);
#else
    static const int map2[3] = { SEEK_SET, SEEK_CUR, SEEK_END };
    off_t r = lseek(f->fd, (off_t)offset, map2[whence]);
    if (r < 0)
    {
        return -rigi_fs_map_errno(errno);
    }
    rigi_fs_write_i64le(out8, (int64_t)r);
#endif
    return 0;
}

/* fs_tell：out[0..8) 写当前游标绝对位置 */
int32_t rigi_fs_tell(int64_t handle, const RigiFatRef *out)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_tell");
    uint8_t *out8 = rigi_fs_span_at(out, 0, 8, "rigi_fs_tell");
#if defined(_WIN32)
    LARGE_INTEGER zero;
    LARGE_INTEGER pos;
    zero.QuadPart = 0;
    if (!SetFilePointerEx(f->handle, zero, &pos, FILE_CURRENT))
    {
        return -rigi_fs_map_winerr(GetLastError());
    }
    rigi_fs_write_i64le(out8, pos.QuadPart);
#else
    off_t r = lseek(f->fd, 0, SEEK_CUR);
    if (r < 0)
    {
        return -rigi_fs_map_errno(errno);
    }
    rigi_fs_write_i64le(out8, (int64_t)r);
#endif
    return 0;
}

/* fs_get_length：out[0..8) 写当前文件长度 */
int32_t rigi_fs_get_length(int64_t handle, const RigiFatRef *out)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_get_length");
    uint8_t *out8 = rigi_fs_span_at(out, 0, 8, "rigi_fs_get_length");
#if defined(_WIN32)
    LARGE_INTEGER size;
    if (!GetFileSizeEx(f->handle, &size))
    {
        return -rigi_fs_map_winerr(GetLastError());
    }
    rigi_fs_write_i64le(out8, size.QuadPart);
#else
    struct stat st;
    if (fstat(f->fd, &st) != 0)
    {
        return -rigi_fs_map_errno(errno);
    }
    rigi_fs_write_i64le(out8, (int64_t)st.st_size);
#endif
    return 0;
}

/* fs_set_length：缩短截断 / 增长补零，成功后游标保持不变——即使已在新
 * 末尾之后（§4.5.6 明文契约；Windows SetEndOfFile 不定义扩展区域内容，
 * 增长部分由本实现显式写零补齐，不假定宿主保证；POSIX ftruncate 内核
 * 保证扩展区域读零且不移动偏移，直接适用。失败保留已发生副作用返回
 * 错误码（§4.5.6），游标仍尽力恢复） */
int32_t rigi_fs_set_length(int64_t handle, int64_t length)
{
    RigiFsFile *f = rigi_fs_file_of(handle, "rigi_fs_set_length");
    if (length < 0)
    {
        return -RIGI_FS_E_INVAL; /* 负长度报范围错误（Rigi 层先行拦截） */
    }
#if defined(_WIN32)
    LARGE_INTEGER size;
    LARGE_INTEGER cur;
    LARGE_INTEGER target;
    if (!GetFileSizeEx(f->handle, &size))
    {
        return -rigi_fs_map_winerr(GetLastError());
    }
    cur.QuadPart = 0;
    if (!SetFilePointerEx(f->handle, cur, &cur, FILE_CURRENT))
    {
        return -rigi_fs_map_winerr(GetLastError());
    }
    target.QuadPart = length;
    if (!SetFilePointerEx(f->handle, target, NULL, FILE_BEGIN)
        || !SetEndOfFile(f->handle))
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        LARGE_INTEGER back;
        back.QuadPart = cur.QuadPart;
        SetFilePointerEx(f->handle, back, NULL, FILE_BEGIN);
        return -e;
    }
    if (length > size.QuadPart)
    {
        /* 增长补零：从旧末尾写零到新末尾（SetEndOfFile 不定义的内容） */
        static const uint8_t zeros[4096];
        LARGE_INTEGER at;
        at.QuadPart = size.QuadPart;
        if (!SetFilePointerEx(f->handle, at, NULL, FILE_BEGIN))
        {
            int32_t e = rigi_fs_map_winerr(GetLastError());
            LARGE_INTEGER back;
            back.QuadPart = cur.QuadPart;
            SetFilePointerEx(f->handle, back, NULL, FILE_BEGIN);
            return -e;
        }
        int64_t remain = length - size.QuadPart;
        int32_t werr = 0;
        while (remain > 0)
        {
            DWORD chunk = remain > (int64_t)sizeof(zeros)
                ? (DWORD)sizeof(zeros) : (DWORD)remain;
            DWORD put = 0;
            if (!WriteFile(f->handle, zeros, chunk, &put, NULL) || put == 0)
            {
                werr = rigi_fs_map_winerr(GetLastError());
                break;
            }
            remain -= (int64_t)put;
        }
        LARGE_INTEGER back;
        back.QuadPart = cur.QuadPart;
        SetFilePointerEx(f->handle, back, NULL, FILE_BEGIN);
        if (werr != 0)
        {
            return -werr;
        }
    }
    else
    {
        LARGE_INTEGER back;
        back.QuadPart = cur.QuadPart;
        SetFilePointerEx(f->handle, back, NULL, FILE_BEGIN);
    }
    return 0;
#else
    if (ftruncate(f->fd, (off_t)length) != 0)
    {
        return -rigi_fs_map_errno(errno);
    }
    return 0;
#endif
}

/* ===== 信息查询（路径操作，同步；followLinks 两形态 = stat/lstat）===== */

#if defined(_WIN32)
/* Windows stat/lstat 公共体：kind/length/时间组装。lstat（不跟随末段）
 * 以 OPEN_REPARSE_POINT 打开链接本身：reparse tag ∈ {SYMLINK,
 * MOUNT_POINT(junction)} 归 Link，其余 reparse tag 归 Other（「不把所有
 * 未知 reparse 类型一律认定为普通文件」，§4.5.3）；断链无法打开句柄时
 * 回退 GetFileAttributesExW（不跟随、不解析链接，断链仍可识别）。
 * 跟随形态直接打开目标（断链目标不可达 → NotFound）。 */
static int32_t rigi_fs_stat_impl(const rigi_string *path,
    const RigiFatRef *out, int follow)
{
    uint8_t *out8 = rigi_fs_span_at(out, 0, RIGI_FS_STAT_SIZE, "rigi_fs_stat");
    wchar_t *wide = NULL;
    int32_t rc = rigi_fs_utf8_to_wide(path->data, path->len, &wide);
    int wlen;
    if (rc != 0)
    {
        return rc;
    }
    wlen = (int)wcslen(wide);
    rc = rigi_fs_wide_long_path(&wide, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wide);
        return rc;
    }
    DWORD flags = FILE_FLAG_BACKUP_SEMANTICS
        | (follow ? 0 : FILE_FLAG_OPEN_REPARSE_POINT);
    HANDLE h = CreateFileW(wide, FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
        OPEN_EXISTING, flags, NULL);
    if (h == INVALID_HANDLE_VALUE)
    {
        DWORD err = GetLastError();
        rigi_track_free(wide);
        if (!follow && (err == ERROR_CANT_ACCESS_FILE
            || err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND
            || err == ERROR_ACCESS_DENIED))
        {
            /* 断链（或无权限打开链接本身）：回退属性查询——末段链接
             * 自身仍可被识别（§4.5.3「断链仍可被识别」） */
            WIN32_FILE_ATTRIBUTE_DATA ad;
            if (!GetFileAttributesExW(wide, GetFileExInfoStandard, &ad))
            {
                return -rigi_fs_map_winerr(GetLastError());
            }
            DWORD a = ad.dwFileAttributes;
            int32_t kind = (a & FILE_ATTRIBUTE_REPARSE_POINT) ? 2
                : (a & FILE_ATTRIBUTE_DIRECTORY) ? 1 : 0;
            int64_t mtms, atms, btms;
            int32_t mtns, atns, btns;
            rigi_fs_ft_parts(&ad.ftLastWriteTime, &mtms, &mtns);
            rigi_fs_ft_parts(&ad.ftLastAccessTime, &atms, &atns);
            rigi_fs_ft_parts(&ad.ftCreationTime, &btms, &btns);
            int64_t length = kind == 0
                ? (int64_t)ad.nFileSizeHigh << 32 | ad.nFileSizeLow : -1;
            rigi_fs_fill_stat(out8, kind, length, mtms, mtns, atms, atns,
                btms, btns);
            return 0;
        }
        return -rigi_fs_map_winerr(err);
    }
    BY_HANDLE_FILE_INFORMATION info;
    int32_t kind;
    int64_t length;
    int64_t mtms, atms, btms;
    int32_t mtns, atns, btns;
    if (!GetFileInformationByHandle(h, &info))
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        CloseHandle(h);
        rigi_track_free(wide);
        return -e;
    }
    DWORD a = info.dwFileAttributes;
    if (!follow && (a & FILE_ATTRIBUTE_REPARSE_POINT))
    {
        FILE_ATTRIBUTE_TAG_INFO tag;
        kind = 3;
        if (GetFileInformationByHandleEx(h, FileAttributeTagInfo, &tag,
            sizeof(tag)))
        {
            if (tag.ReparseTag == IO_REPARSE_TAG_SYMLINK
                || tag.ReparseTag == IO_REPARSE_TAG_MOUNT_POINT)
            {
                kind = 2; /* symlink 与 junction 都归 Link（§4.5.3） */
            }
        }
    }
    else
    {
        kind = (a & FILE_ATTRIBUTE_DIRECTORY) ? 1 : 0;
    }
    length = kind == 0
        ? (int64_t)info.nFileSizeHigh << 32 | info.nFileSizeLow : -1;
    rigi_fs_ft_parts(&info.ftLastWriteTime, &mtms, &mtns);
    rigi_fs_ft_parts(&info.ftLastAccessTime, &atms, &atns);
    rigi_fs_ft_parts(&info.ftCreationTime, &btms, &btns);
    CloseHandle(h);
    rigi_track_free(wide);
    rigi_fs_fill_stat(out8, kind, length, mtms, mtns, atms, atns, btms,
        btns);
    return 0;
}
#else
/* POSIX stat/lstat 公共体：主体类型/长度/mtime/atime 与错误仍来自原
 * stat/lstat；Linux 额外以 statx 请求真实 birth（仅返回 mask 含
 * STATX_BTIME 才采用）。二次路径查询可能与主体之间发生竞态：以
 * 设备号+inode 核身份，身份不同或 statx 失败均只把 birth 标不可得，
 * 不覆盖已成功的主体查询，更不借 ctime/mtime 冒充（§4.5.3）。 */
static int32_t rigi_fs_stat_impl(const rigi_string *path,
    const RigiFatRef *out, int follow)
{
    uint8_t *out8 = rigi_fs_span_at(out, 0, RIGI_FS_STAT_SIZE, "rigi_fs_stat");
    if (path->len == 0)
    {
        return -RIGI_FS_E_INVAL;
    }
    char *zpath = (char *)rigi_track_malloc((size_t)path->len + 1);
    memcpy(zpath, path->data, (size_t)path->len);
    zpath[path->len] = 0;
    struct stat st;
    int r = follow ? stat(zpath, &st) : lstat(zpath, &st);
    int e = errno;
    if (r != 0)
    {
        rigi_track_free(zpath);
        return -rigi_fs_map_errno(e);
    }
    int32_t kind;
    if (S_ISLNK(st.st_mode)) { kind = 2; }
    else if (S_ISDIR(st.st_mode)) { kind = 1; }
    else if (S_ISREG(st.st_mode)) { kind = 0; }
    else { kind = 3; }
    int64_t length = kind == 0 ? (int64_t)st.st_size : -1;
    int64_t mtms, atms, btms;
    int32_t mtns, atns, btns;
    rigi_fs_ts_parts((int64_t)st.st_mtim.tv_sec,
        (int64_t)st.st_mtim.tv_nsec, &mtms, &mtns);
    rigi_fs_ts_parts((int64_t)st.st_atim.tv_sec,
        (int64_t)st.st_atim.tv_nsec, &atms, &atns);
    btms = INT64_MIN; /* 无 statx / 无 STATX_BTIME / 二次查询失败或竞态 */
    btns = 0;
#if defined(__linux__) && defined(SYS_statx) && defined(STATX_BTIME)
    struct statx sx;
    memset(&sx, 0, sizeof(sx));
    int flags = follow ? 0 : AT_SYMLINK_NOFOLLOW;
    /* statx 为辅助查询：不让 ENOSYS、EOPNOTSUPP、权限及路径竞态
     * 覆盖成功的 stat/lstat。设备号+inode 不一致时拒绝混合两实体。 */
    if (syscall(SYS_statx, AT_FDCWD, zpath, flags, STATX_BTIME, &sx) == 0
        && (sx.stx_mask & STATX_BTIME) != 0
        && major(st.st_dev) == sx.stx_dev_major
        && minor(st.st_dev) == sx.stx_dev_minor
        && (uint64_t)st.st_ino == sx.stx_ino
        && sx.stx_btime.tv_nsec < 1000000000U)
    {
        rigi_fs_ts_parts(sx.stx_btime.tv_sec, sx.stx_btime.tv_nsec,
            &btms, &btns);
    }
#endif
    rigi_track_free(zpath);
    rigi_fs_fill_stat(out8, kind, length, mtms, mtns, atms, atns, btms,
        btns);
    return 0;
}
#endif

/* fs_stat（跟随末段链接）／fs_lstat（只查询末段链接本身，断链可识别）：
 * out[0..48) 写 stat 结构（布局见上），返回 0；< 0 = -归一码 */
int32_t rigi_fs_stat(const rigi_string *path, const RigiFatRef *out)
{
    if (path == NULL || path->data == NULL || path->len < 0
        || path->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_stat 收到非法路径参数（编译器 bug）\n");
        abort();
    }
    return rigi_fs_stat_impl(path, out, 1);
}

int32_t rigi_fs_lstat(const rigi_string *path, const RigiFatRef *out)
{
    if (path == NULL || path->data == NULL || path->len < 0
        || path->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_lstat 收到非法路径参数（编译器 bug）\n");
        abort();
    }
    return rigi_fs_stat_impl(path, out, 0);
}

/* ===== realpath（要求目标存在，解析链接返回绝对路径，§4.5.3）===== */

/* out[0..n) 写结果路径 UTF-8 字节，meta[0..4) 写字节数。返回 0 成功；
 * 正数 2 = out 缓冲不足（meta 回所需字节数，Rigi 层放大重试一次）；
 * < 0 = -归一码 */
int32_t rigi_fs_realpath(const rigi_string *path, const RigiFatRef *out,
    const RigiFatRef *meta)
{
    if (path == NULL || path->data == NULL || path->len < 0
        || path->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_realpath 收到非法路径参数（编译器"
            " bug）\n");
        abort();
    }
    int64_t blen;
    const uint8_t *bytes;
    uint8_t *heap = NULL;
#if defined(_WIN32)
    wchar_t *wide = NULL;
    int32_t rc = rigi_fs_utf8_to_wide(path->data, path->len, &wide);
    int wlen;
    if (rc != 0)
    {
        return rc;
    }
    wlen = (int)wcslen(wide);
    rc = rigi_fs_wide_long_path(&wide, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wide);
        return rc;
    }
    HANDLE h = CreateFileW(wide, FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
        OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
    if (h == INVALID_HANDLE_VALUE)
    {
        DWORD err = GetLastError();
        rigi_track_free(wide);
        return -rigi_fs_map_winerr(err);
    }
    rigi_track_free(wide);
    /* GetFinalPathNameByHandleW：返回 \\?\（本地）或 \\?\UNC\（UNC）形态
     *——前缀是内部原生前缀，剥回用户可表达形态（§4.5.2） */
    DWORD n = GetFinalPathNameByHandleW(h, NULL, 0,
        FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (n == 0)
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        CloseHandle(h);
        return -e;
    }
    wchar_t *wres = (wchar_t *)rigi_track_malloc(
        (size_t)n * sizeof(wchar_t));
    if (GetFinalPathNameByHandleW(h, wres, n,
        FILE_NAME_NORMALIZED | VOLUME_NAME_DOS) == 0)
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        rigi_track_free(wres);
        CloseHandle(h);
        return -e;
    }
    CloseHandle(h);
    const wchar_t *w = wres;
    if (wcsncmp(w, L"\\\\?\\UNC\\", 8) == 0)
    {
        /* \\?\UNC\server\share → \\server\share */
        wchar_t *restored = (wchar_t *)rigi_track_malloc(
            (size_t)(n + 1) * sizeof(wchar_t));
        restored[0] = L'\\';
        restored[1] = L'\\';
        const wchar_t *q = w + 8;
        wchar_t *p2 = restored + 2;
        while (*q) { *p2++ = *q++; }
        *p2 = 0;
        rigi_track_free(wres);
        wres = restored;
        w = wres;
    }
    else if (wcsncmp(w, L"\\\\?\\", 4) == 0)
    {
        w += 4;
    }
    int ulen = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, w, -1,
        NULL, 0, NULL, NULL);
    if (ulen <= 0)
    {
        rigi_track_free(wres);
        return -RIGI_FS_E_ENCODING;
    }
    heap = (uint8_t *)rigi_track_malloc((size_t)ulen);
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, w, -1,
        (char *)heap, ulen, NULL, NULL) <= 0)
    {
        rigi_track_free(wres);
        rigi_track_free(heap);
        return -RIGI_FS_E_ENCODING;
    }
    rigi_track_free(wres);
    blen = ulen - 1; /* WideCharToMultiByte(-1) 计入结尾 NUL */
    bytes = heap;
#else
    if (path->len == 0)
    {
        return -RIGI_FS_E_INVAL;
    }
    char *zpath = (char *)rigi_track_malloc((size_t)path->len + 1);
    memcpy(zpath, path->data, (size_t)path->len);
    zpath[path->len] = 0;
    /* resolved 形态：结果缓冲由本层 rigi_track_malloc 提供（PATH_MAX
     * 上限，POSIX 对非 NULL resolved 的要求）。不可用 realpath(zpath,
     * NULL) 形态——glibc 内部 malloc 出的指针绝不能流进
     * rigi_track_free（mimalloc 头 + 0xDD 毒化 memset 按头内 size 重写，
     * 跨分配器配对即段错误；Windows 分支不经此路径故未暴露，Linux
     * 现场管线首次可编可跑时才被真实暴露） */
    char *rbuf = (char *)rigi_track_malloc((size_t)PATH_MAX);
    char *r = realpath(zpath, rbuf); /* 失败/不存在返回 NULL（要求目标
                                      * 存在，§4.5.3） */
    int e = errno;
    rigi_track_free(zpath);
    if (r == NULL)
    {
        rigi_track_free(rbuf);
        return -rigi_fs_map_errno(e);
    }
    blen = (int64_t)strlen(r);
    bytes = heap = (uint8_t *)rbuf;
#endif
    /* 先探 out 容量再解包（span_at 对越界诊断 abort——不足属正常协议
     * 路径须回哨兵 2 而非 abort）：Span 元素数在前缀 [24..28) */
    uint8_t *meta8 = rigi_fs_span_at(meta, 0, 4, "rigi_fs_realpath");
    if (out == NULL || out->payload == 0)
    {
        fprintf(stderr, "rigi_rt: fs_realpath 收到空缓冲区（编译器 bug）\n");
        abort();
    }
    const uint8_t *obase = (const uint8_t *)(uintptr_t)out->payload;
    int32_t cap;
    memcpy(&cap, obase + 24, sizeof(int32_t));
    if (blen > (int64_t)0x7FFFFFFE)
    {
        if (heap != NULL) { rigi_track_free(heap); }
        return -RIGI_FS_E_NAMETOOLONG; /* 防御（i32 容量上限） */
    }
    int32_t blen32 = (int32_t)blen;
    memcpy(meta8, &blen32, 4);
    if (cap < 0 || (int64_t)cap < blen)
    {
        if (heap != NULL) { rigi_track_free(heap); }
        return 2; /* 缓冲不足，meta 已回所需字节数 */
    }
    uint8_t *out8 = (uint8_t *)(uintptr_t)obase + 32;
    memcpy(out8, bytes, (size_t)blen);
    rigi_track_free(heap);
    return 0;
}

/* ===== 创建 / 删除（§4.5.5：宿主权限规则；删除目标不存在默认报错）===== */

int32_t rigi_fs_mkdir(const rigi_string *path, int32_t mode)
{
    if (path == NULL || path->data == NULL || path->len < 0
        || path->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_mkdir 收到非法路径参数（编译器 bug）\n");
        abort();
    }
#if defined(_WIN32)
    wchar_t *wide = NULL;
    int32_t rc = rigi_fs_utf8_to_wide(path->data, path->len, &wide);
    int wlen;
    if (rc != 0)
    {
        return rc;
    }
    wlen = (int)wcslen(wide);
    rc = rigi_fs_wide_long_path(&wide, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wide);
        return rc;
    }
    if (!CreateDirectoryW(wide, NULL))
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        rigi_track_free(wide);
        return -e;
    }
    rigi_track_free(wide);
    return 0;
#else
    if (path->len == 0)
    {
        return -RIGI_FS_E_INVAL;
    }
    char *zpath = (char *)rigi_track_malloc((size_t)path->len + 1);
    memcpy(zpath, path->data, (size_t)path->len);
    zpath[path->len] = 0;
    /* 目录 0777 为基础受 umask 影响（§4.5.5）；mode 为 Rigi 层传入的
     * 权限位（首版固定 0777，参数留扩展） */
    int r = mkdir(zpath, (mode_t)mode & 0777);
    int e = errno;
    rigi_track_free(zpath);
    if (r != 0)
    {
        return -rigi_fs_map_errno(e);
    }
    return 0;
#endif
}

int32_t rigi_fs_rmdir(const rigi_string *path)
{
    if (path == NULL || path->data == NULL || path->len < 0
        || path->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_rmdir 收到非法路径参数（编译器 bug）\n");
        abort();
    }
#if defined(_WIN32)
    wchar_t *wide = NULL;
    int32_t rc = rigi_fs_utf8_to_wide(path->data, path->len, &wide);
    int wlen;
    if (rc != 0)
    {
        return rc;
    }
    wlen = (int)wcslen(wide);
    rc = rigi_fs_wide_long_path(&wide, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wide);
        return rc;
    }
    /* RemoveDirectoryW 只删真实空目录，不跟随末段链接删目标（§4.5.5）：
     * 对目录链接（junction）删除链接条目本身 */
    if (!RemoveDirectoryW(wide))
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        rigi_track_free(wide);
        return -e;
    }
    rigi_track_free(wide);
    return 0;
#else
    if (path->len == 0)
    {
        return -RIGI_FS_E_INVAL;
    }
    char *zpath = (char *)rigi_track_malloc((size_t)path->len + 1);
    memcpy(zpath, path->data, (size_t)path->len);
    zpath[path->len] = 0;
    /* rmdir 对末段链接删除链接条目本身（POSIX rmdir 语义），不跟随 */
    int r = rmdir(zpath);
    int e = errno;
    rigi_track_free(zpath);
    if (r != 0)
    {
        return -rigi_fs_map_errno(e);
    }
    return 0;
#endif
}

int32_t rigi_fs_unlink(const rigi_string *path)
{
    if (path == NULL || path->data == NULL || path->len < 0
        || path->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_unlink 收到非法路径参数（编译器 bug）\n");
        abort();
    }
#if defined(_WIN32)
    wchar_t *wide = NULL;
    int32_t rc = rigi_fs_utf8_to_wide(path->data, path->len, &wide);
    int wlen;
    if (rc != 0)
    {
        return rc;
    }
    wlen = (int)wcslen(wide);
    rc = rigi_fs_wide_long_path(&wide, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wide);
        return rc;
    }
    /* Windows 的 DeleteFileW 不能删目录 junction；RemoveDirectoryW
     * 才删除目录链接条目（不要求目标为空）。仅为选择系统调用检查 tag，
     * 不把未知 reparse 类型统一当 Link，也不沿链接递归清理（§4.5.5）。 */
    DWORD attr = GetFileAttributesW(wide);
    if (attr == INVALID_FILE_ATTRIBUTES)
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        rigi_track_free(wide);
        return -e;
    }
    if (attr & FILE_ATTRIBUTE_DIRECTORY)
    {
        if (!(attr & FILE_ATTRIBUTE_REPARSE_POINT))
        {
            rigi_track_free(wide);
            return -RIGI_FS_E_ISDIR;
        }
        HANDLE h = CreateFileW(wide, FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS
                | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
        if (h == INVALID_HANDLE_VALUE)
        {
            int32_t e = rigi_fs_map_winerr(GetLastError());
            rigi_track_free(wide);
            return -e;
        }
        FILE_ATTRIBUTE_TAG_INFO tag;
        BOOL tagged = GetFileInformationByHandleEx(h, FileAttributeTagInfo,
            &tag, sizeof(tag));
        DWORD tagerr = tagged ? ERROR_SUCCESS : GetLastError();
        CloseHandle(h);
        if (!tagged)
        {
            int32_t e = rigi_fs_map_winerr(tagerr);
            rigi_track_free(wide);
            return -e;
        }
        if (tag.ReparseTag != IO_REPARSE_TAG_MOUNT_POINT
            && tag.ReparseTag != IO_REPARSE_TAG_SYMLINK)
        {
            rigi_track_free(wide);
            return -RIGI_FS_E_ISDIR;
        }
        /* 先关闭查询句柄；按路径删除的最终判定交给非递归系统调用。 */
        if (!RemoveDirectoryW(wide))
        {
            int32_t e = rigi_fs_map_winerr(GetLastError());
            rigi_track_free(wide);
            return -e;
        }
    }
    else if (!DeleteFileW(wide))
    {
        int32_t e = rigi_fs_map_winerr(GetLastError());
        rigi_track_free(wide);
        return -e;
    }
    rigi_track_free(wide);
    return 0;
#else
    if (path->len == 0)
    {
        return -RIGI_FS_E_INVAL;
    }
    char *zpath = (char *)rigi_track_malloc((size_t)path->len + 1);
    memcpy(zpath, path->data, (size_t)path->len);
    zpath[path->len] = 0;
    /* unlink 删除文件或链接条目；目录目标 EISDIR */
    int r = unlink(zpath);
    int e = errno;
    rigi_track_free(zpath);
    if (r != 0)
    {
        return -rigi_fs_map_errno(e);
    }
    return 0;
#endif
}

/* fs_rename：replace = 0 → NoReplace（系统仅创建/不替换保证，遇任何
 * 已有目标条目报错，不能用 exists + 覆盖 rename 模拟，§4.5.7）；1 →
 * Replace（覆盖仅限文件/链接条目，不允许目录覆盖或合并）。源/目标末
 * 段为链接时操作条目本身（不跟随）。跨文件系统报 CrossDevice。 */
#if !defined(_WIN32)
#define RIGI_FS_RENAME_NOREPLACE 1u /* <linux/fs.h>；不依赖 _GNU_SOURCE */
#endif

int32_t rigi_fs_rename(const rigi_string *src, const rigi_string *dst,
    int32_t replace)
{
    if (src == NULL || src->data == NULL || src->len < 0
        || src->len > (int64_t)0x7FFFFFFE || dst == NULL || dst->data == NULL
        || dst->len < 0 || dst->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_rename 收到非法路径参数（编译器 bug）\n");
        abort();
    }
    if (replace != 0 && replace != 1)
    {
        return -RIGI_FS_E_INVAL;
    }
#if defined(_WIN32)
    wchar_t *wsrc = NULL;
    wchar_t *wdst = NULL;
    int32_t rc = rigi_fs_utf8_to_wide(src->data, src->len, &wsrc);
    int wlen;
    if (rc != 0)
    {
        return rc;
    }
    wlen = (int)wcslen(wsrc);
    rc = rigi_fs_wide_long_path(&wsrc, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wsrc);
        return rc;
    }
    rc = rigi_fs_utf8_to_wide(dst->data, dst->len, &wdst);
    if (rc != 0)
    {
        rigi_track_free(wsrc);
        return rc;
    }
    wlen = (int)wcslen(wdst);
    rc = rigi_fs_wide_long_path(&wdst, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wsrc);
        rigi_track_free(wdst);
        return rc;
    }
    /* Windows 的目录 junction 带 DIRECTORY|REPARSE：MoveFileExW 的
     * REPLACE_EXISTING 会以 ACCESS_DENIED 拒绝它；仅普通文件源 Replace
     * 遇末段明确 MOUNT_POINT 时改用 FileRenameInfoEx 的单次系统移动。
     * 检查只作窄路径选择，不当作锁：目标竞态中的真目录仍由系统拒绝；
     * 其他源/目标形态与 NoReplace 继续保留原 MoveFileExW 行为。 */
    if (replace)
    {
        DWORD dstattr = GetFileAttributesW(wdst);
        if (dstattr != INVALID_FILE_ATTRIBUTES
            && (dstattr & (FILE_ATTRIBUTE_DIRECTORY
                | FILE_ATTRIBUTE_REPARSE_POINT))
                == (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))
        {
            HANDLE target = CreateFileW(wdst, FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS
                    | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
            if (target == INVALID_HANDLE_VALUE)
            {
                int32_t e = rigi_fs_map_winerr(GetLastError());
                rigi_track_free(wsrc);
                rigi_track_free(wdst);
                return -e;
            }
            FILE_ATTRIBUTE_TAG_INFO tag;
            BOOL tagged = GetFileInformationByHandleEx(target,
                FileAttributeTagInfo, &tag, sizeof(tag));
            DWORD tagerr = tagged ? ERROR_SUCCESS : GetLastError();
            CloseHandle(target);
            if (!tagged)
            {
                int32_t e = rigi_fs_map_winerr(tagerr);
                rigi_track_free(wsrc);
                rigi_track_free(wdst);
                return -e;
            }
            if ((tag.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY
                    | FILE_ATTRIBUTE_REPARSE_POINT))
                    != (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))
            {
                rigi_track_free(wsrc);
                rigi_track_free(wdst);
                return -RIGI_FS_E_NOSYS;
            }
            if (tag.ReparseTag == IO_REPARSE_TAG_MOUNT_POINT)
            {
                HANDLE source = CreateFileW(wsrc, DELETE | FILE_READ_ATTRIBUTES,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    NULL, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, NULL);
                if (source == INVALID_HANDLE_VALUE)
                {
                    int32_t e = rigi_fs_map_winerr(GetLastError());
                    rigi_track_free(wsrc);
                    rigi_track_free(wdst);
                    return -e;
                }
                FILE_ATTRIBUTE_TAG_INFO srctag;
                BOOL sourced = GetFileInformationByHandleEx(source,
                    FileAttributeTagInfo, &srctag, sizeof(srctag));
                DWORD srcerr = sourced ? ERROR_SUCCESS : GetLastError();
                if (!sourced)
                {
                    CloseHandle(source);
                    int32_t e = rigi_fs_map_winerr(srcerr);
                    rigi_track_free(wsrc);
                    rigi_track_free(wdst);
                    return -e;
                }
                if (!(srctag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                    && !(srctag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
                {
                    /* DWORD Flags / HANDLE RootDirectory / DWORD 字节长度 /
                     * UTF-16 FileName；offsetof 保留 x64 对齐与 x86 ABI。
                     * 终止 NUL 不计入 FileNameLength，缓冲仍额外包含它。
                     * wdst 已经与旧入口同口径做长路径前缀转换。 */
                    size_t bytes = wcslen(wdst) * sizeof(wchar_t);
                    size_t size = offsetof(FILE_RENAME_INFO, FileName)
                        + bytes + sizeof(wchar_t);
                    if (bytes > (size_t)UINT32_MAX
                        || size > (size_t)UINT32_MAX)
                    {
                        CloseHandle(source);
                        rigi_track_free(wsrc);
                        rigi_track_free(wdst);
                        return -RIGI_FS_E_NAMETOOLONG;
                    }
                    FILE_RENAME_INFO *info = (FILE_RENAME_INFO *)
                        rigi_track_malloc(size);
                    memset(info, 0, size);
                    info->Flags = FILE_RENAME_FLAG_REPLACE_IF_EXISTS
                        | FILE_RENAME_FLAG_POSIX_SEMANTICS;
                    info->RootDirectory = NULL;
                    info->FileNameLength = (DWORD)bytes;
                    memcpy(info->FileName, wdst, bytes + sizeof(wchar_t));
                    BOOL moved = SetFileInformationByHandle(source,
                        FileRenameInfoEx, info, (DWORD)size);
                    DWORD err = moved ? ERROR_SUCCESS : GetLastError();
                    rigi_track_free(info);
                    CloseHandle(source);
                    int32_t result = 0;
                    if (!moved)
                    {
                        if (err == ERROR_INVALID_PARAMETER
                            || err == ERROR_NOT_SUPPORTED
                            || err == ERROR_INVALID_FUNCTION)
                        {
                            result = -RIGI_FS_E_NOSYS;
                        }
                        else if (err == ERROR_ACCESS_DENIED)
                        {
                            /* 最终系统调用拒绝真实目录时沿用 IsDirectory；
                             * 失败路径补查不将竞态后的普通目标误报目录。 */
                            DWORD attr = GetFileAttributesW(wdst);
                            result = attr != INVALID_FILE_ATTRIBUTES
                                && (attr & FILE_ATTRIBUTE_DIRECTORY)
                                && !(attr & FILE_ATTRIBUTE_REPARSE_POINT)
                                ? -RIGI_FS_E_ISDIR
                                : -rigi_fs_map_winerr(err);
                        }
                        else
                        {
                            result = -rigi_fs_map_winerr(err);
                        }
                    }
                    rigi_track_free(wsrc);
                    rigi_track_free(wdst);
                    return result;
                }
                CloseHandle(source);
            }
            else if (tag.ReparseTag != IO_REPARSE_TAG_SYMLINK)
            {
                rigi_track_free(wsrc);
                rigi_track_free(wdst);
                return -RIGI_FS_E_NOSYS;
            }
        }
    }
    /* MoveFileExW 不带 REPLACE_EXISTING 即系统不替换保证（目标已存在
     * 失败）——NoReplace 不靠先查询模拟（§4.5.7）；Replace 遇真实目录
     * 目标天然失败（ACCESS_DENIED），失败路径补查归 IsDirectory。 */
    if (!MoveFileExW(wsrc, wdst,
        replace ? MOVEFILE_REPLACE_EXISTING : 0))
    {
        DWORD err = GetLastError();
        if (replace && err == ERROR_ACCESS_DENIED)
        {
            DWORD attr = GetFileAttributesW(wdst);
            if (attr != INVALID_FILE_ATTRIBUTES
                && (attr & FILE_ATTRIBUTE_DIRECTORY))
            {
                rigi_track_free(wsrc);
                rigi_track_free(wdst);
                return -RIGI_FS_E_ISDIR;
            }
        }
        int32_t e = rigi_fs_map_winerr(err);
        rigi_track_free(wsrc);
        rigi_track_free(wdst);
        return -e;
    }
    rigi_track_free(wsrc);
    rigi_track_free(wdst);
    return 0;
#else
    if (src->len == 0 || dst->len == 0)
    {
        return -RIGI_FS_E_INVAL;
    }
    char *zsrc = (char *)rigi_track_malloc((size_t)src->len + 1);
    memcpy(zsrc, src->data, (size_t)src->len);
    zsrc[src->len] = 0;
    char *zdst = (char *)rigi_track_malloc((size_t)dst->len + 1);
    memcpy(zdst, dst->data, (size_t)dst->len);
    zdst[dst->len] = 0;
    int32_t rc = 0;
    /* lstat 不跟随源末段链接：只有真实目录源的 Replace 才需要系统
     * 不替换保证；文件与链接仍走允许覆盖的 rename。 */
    struct stat srcst;
    if (replace && lstat(zsrc, &srcst) != 0)
    {
        rc = -rigi_fs_map_errno(errno);
    }
    else if (!replace || S_ISDIR(srcst.st_mode))
    {
#ifdef SYS_renameat2
        /* NoReplace 及目录源 Replace 均由内核原子阻止目标替换；宿主
         * 或文件系统不能提供该保证时报 Unsupported，绝不回退 rename。 */
        long r = syscall(SYS_renameat2, AT_FDCWD, zsrc, AT_FDCWD, zdst,
            (unsigned int)RIGI_FS_RENAME_NOREPLACE);
        if (r != 0)
        {
            int e = errno;
            if (e == ENOSYS || e == EINVAL || e == EOPNOTSUPP)
            {
                rc = -RIGI_FS_E_NOSYS;
            }
            else if (replace && e == EEXIST)
            {
                /* 已有目标仅在失败后分类：真实目录 → IsDirectory，
                 * 文件/链接（含断链）→ AlreadyExists。 */
                struct stat dstst;
                rc = lstat(zdst, &dstst) == 0 && S_ISDIR(dstst.st_mode)
                    ? -RIGI_FS_E_ISDIR : -RIGI_FS_E_EXISTS;
            }
            else
            {
                rc = -rigi_fs_map_errno(e);
            }
        }
#else
        rc = -RIGI_FS_E_NOSYS; /* 无 renameat2 宿主：不能系统保证 */
#endif
    }
    else
    {
        /* 非目录源保留覆盖文件/链接的 rename 语义；目录目标由
         * rename 原子拒绝，不以事先查询模拟该保证。 */
        if (rename(zsrc, zdst) != 0)
        {
            int e = errno;
            struct stat dstst;
            rc = (e == EISDIR || e == ENOTDIR || e == ENOTEMPTY)
                && lstat(zdst, &dstst) == 0 && S_ISDIR(dstst.st_mode)
                ? -RIGI_FS_E_ISDIR : -rigi_fs_map_errno(e);
        }
    }
    rigi_track_free(zsrc);
    rigi_track_free(zdst);
    return rc;
#endif
}

/* ===== fs_same_file（施工块 7-6）：系统文件身份比较 =====
 * 按实际打开句柄的系统文件身份比较两个文件句柄是否指向同一文件
 *（§4.5.7「截断前按实际打开的源/目标系统文件身份拒绝自复制」）：
 *   - Windows：卷序列号 + 64 位文件索引（BY_HANDLE_FILE_INFORMATION；
 *     ReFS 的索引唯一性弱于 NTFS，卷序列号 + 索引仍是宿主公认的
 *     BY_HANDLE 身份口径，与 VM 侧同表）；
 *   - Linux：st_dev + st_ino（fstat，POSIX 文件身份）。
 * 身份在打开时已定型（open 跟随链接），比较不涉及任何路径访问——
 * 硬链接/不同路径访问同一文件即同一身份（不能只比较路径文本）。
 * out[0] 写 1（同一文件）/ 0（不同）；返回 0；< 0 = -归一码（句柄
 * 已关闭 -EBADF 防御；身份查询失败按宿主错误映射）。 */
int32_t rigi_fs_same_file(int64_t a, int64_t b, const RigiFatRef *out)
{
    uint8_t *out8 = rigi_fs_span_at(out, 0, 1, "rigi_fs_same_file");
    RigiFsFile *fa = rigi_fs_file_of(a, "rigi_fs_same_file");
    RigiFsFile *fb = rigi_fs_file_of(b, "rigi_fs_same_file");
    int same;
#if defined(_WIN32)
    BY_HANDLE_FILE_INFORMATION ia;
    BY_HANDLE_FILE_INFORMATION ib;
    if (fa->handle == INVALID_HANDLE_VALUE || fb->handle
        == INVALID_HANDLE_VALUE)
    {
        return -RIGI_FS_E_BADF;
    }
    if (!GetFileInformationByHandle(fa->handle, &ia))
    {
        return -rigi_fs_map_winerr(GetLastError());
    }
    if (!GetFileInformationByHandle(fb->handle, &ib))
    {
        return -rigi_fs_map_winerr(GetLastError());
    }
    same = (ia.dwVolumeSerialNumber == ib.dwVolumeSerialNumber
        && ia.nFileIndexHigh == ib.nFileIndexHigh
        && ia.nFileIndexLow == ib.nFileIndexLow);
#else
    struct stat sa;
    struct stat sb;
    if (fa->fd < 0 || fb->fd < 0)
    {
        return -RIGI_FS_E_BADF;
    }
    if (fstat(fa->fd, &sa) != 0)
    {
        return -rigi_fs_map_errno(errno);
    }
    if (fstat(fb->fd, &sb) != 0)
    {
        return -rigi_fs_map_errno(errno);
    }
    same = (sa.st_dev == sb.st_dev) && (sa.st_ino == sb.st_ino);
#endif
    out8[0] = same ? 1 : 0;
    return 0;
}

/* ===== 目录读取原语（§4.5.4 的地基；DirectoryReader 组装在 7-4）===== */

/* 目录句柄：FindFirstFileW 首条目缓存 / DIR*。dirread 为同步元数据
 * 操作（本地目录枚举快速返回，open 同分界）；同实例不支持并发或重入
 *（§4.5.4）——宿主面不设闸：同步调用在单次系统调用内完成，交错调用
 * 属被禁止的共享位置并发使用（语义未定义，不崩溃） */
typedef struct RigiFsDir
{
#if defined(_WIN32)
    HANDLE find; /* INVALID_HANDLE_VALUE = 已耗尽/关闭 */
    WIN32_FIND_DATAW data; /* find_valid 时为待交付条目 */
    int find_valid;
#else
    DIR *dir; /* NULL = 已关闭 */
#endif
    int64_t token;
} RigiFsDir;

static void rigi_fs_dir_destroy(void *payload)
{
    RigiFsDir *d = (RigiFsDir *)payload;
#if defined(_WIN32)
    if (d->find != INVALID_HANDLE_VALUE)
    {
        FindClose(d->find);
        d->find = INVALID_HANDLE_VALUE;
    }
#else
    if (d->dir != NULL)
    {
        closedir(d->dir);
        d->dir = NULL;
    }
#endif
    rigi_track_free(d);
}

/* fs_diropen：成功 out[0..8) 写目录句柄 token 返回 0；< 0 = -归一码。
 * 打开即验证（空目录合法——首条 dirread 即返回结束） */
int32_t rigi_fs_diropen(const rigi_string *path, const RigiFatRef *out)
{
    if (path == NULL || path->data == NULL || path->len < 0
        || path->len > (int64_t)0x7FFFFFFE)
    {
        fprintf(stderr, "rigi_rt: fs_diropen 收到非法路径参数（编译器"
            " bug）\n");
        abort();
    }
    uint8_t *out8 = rigi_fs_span_at(out, 0, 8, "rigi_fs_diropen");
    RigiFsDir *d = (RigiFsDir *)rigi_track_malloc(sizeof(RigiFsDir));
#if defined(_WIN32)
    wchar_t *wide = NULL;
    int32_t rc = rigi_fs_utf8_to_wide(path->data, path->len, &wide);
    int wlen;
    if (rc != 0)
    {
        rigi_track_free(d);
        return rc;
    }
    wlen = (int)wcslen(wide);
    rc = rigi_fs_wide_long_path(&wide, &wlen);
    if (rc != 0)
    {
        rigi_track_free(wide);
        rigi_track_free(d);
        return rc;
    }
    /* 拼通配末段：尾部已有分隔符只补 *（「实际 I/O 前也不静默删除尾部
     * 分隔符」的 Path 文本规则不受影响——这是原语层搜索串，不是路径
     * 文本重写） */
    int hasTail = wlen > 0 && (wide[wlen - 1] == L'\\' || wide[wlen - 1]
        == L'/');
    wchar_t *pat = (wchar_t *)rigi_track_malloc(
        ((size_t)wlen + 3) * sizeof(wchar_t));
    memcpy(pat, wide, (size_t)wlen * sizeof(wchar_t));
    pat[wlen] = hasTail ? L'*' : L'\\';
    if (!hasTail) { pat[wlen + 1] = L'*'; }
    pat[wlen + (hasTail ? 1 : 2)] = 0;
    rigi_track_free(wide);
    d->find = FindFirstFileW(pat, &d->data);
    int e = GetLastError();
    rigi_track_free(pat);
    if (d->find == INVALID_HANDLE_VALUE)
    {
        rigi_track_free(d);
        return -rigi_fs_map_winerr(e);
    }
    d->find_valid = 1;
#else
    if (path->len == 0)
    {
        rigi_track_free(d);
        return -RIGI_FS_E_INVAL;
    }
    char *zpath = (char *)rigi_track_malloc((size_t)path->len + 1);
    memcpy(zpath, path->data, (size_t)path->len);
    zpath[path->len] = 0;
    d->dir = opendir(zpath);
    int e = errno;
    rigi_track_free(zpath);
    if (d->dir == NULL)
    {
        rigi_track_free(d);
        return -rigi_fs_map_errno(e);
    }
#endif
    d->token = rigi_native_rc_create(d, rigi_fs_dir_destroy);
    rigi_fs_write_i64le(out8, d->token);
    return 0;
}

#if !defined(_WIN32)
/* 严格 UTF-8 校验（POSIX 目录名直接是字节——无法无损表达为 String 的
 * 名称报编码错误，不替换不跳过，§4.5.2；Windows 名称经 UTF-16 转换，
 * 合法性由转换面保证，本函数只在 POSIX 编译）。返回 1 合法 / 0 非法 */
static int rigi_fs_utf8_valid(const uint8_t *p, int64_t len)
{
    int64_t i = 0;
    while (i < len)
    {
        uint8_t b = p[i];
        if (b < 0x80) { i++; continue; }
        /* 首字节合法类与续字节数；lo/hi 为首续字节允许闭区间
         *（0xE0/0xF0 下界排除过长编码，0xED/0xF4 上界排除代理区与
         * 越出 U+10FFFF） */
        int n;
        uint8_t lo = 0x80;
        uint8_t hi = 0xBF;
        if (b >= 0xC2 && b <= 0xDF) { n = 1; }
        else if (b == 0xE0) { n = 2; lo = 0xA0; }
        else if (b >= 0xE1 && b <= 0xEC) { n = 2; }
        else if (b == 0xED) { n = 2; hi = 0x9F; }
        else if (b >= 0xEE && b <= 0xEF) { n = 2; }
        else if (b == 0xF0) { n = 3; lo = 0x90; }
        else if (b >= 0xF1 && b <= 0xF3) { n = 3; }
        else if (b == 0xF4) { n = 3; hi = 0x8F; }
        else { return 0; }
        if (i + n >= len) { return 0; } /* 序列越尾 */
        uint8_t c = p[i + 1];
        if (c < lo || c > hi) { return 0; }
        for (int k = 2; k <= n; k++)
        {
            c = p[i + k];
            if (c < 0x80 || c > 0xBF) { return 0; }
        }
        i += n + 1;
    }
    return 1;
}
#endif /* !_WIN32 */

/* fs_dirread：每次交付一个条目（跳过 "."/".."，不排序不递归，含隐藏
 * 项，§4.5.4）。out 写条目名称 UTF-8 字节；meta[0..4) = 名称字节数、
 * [4..8) = kind 提示（0/1/2/3，语义同 stat）、[8..12) = 提示有效标志。
 * 返回 0 = 条目；1 = 结束（此后持续 1，重扫须重新打开）；正数 2 = out
 * 不足（meta 名称字节数已回，Rigi 层放大重试一次）；< 0 = -归一码
 *（名称无法无损表达 → -1001 InvalidNameEncoding，§4.5.2）。 */
int32_t rigi_fs_dirread(int64_t handle, const RigiFatRef *out,
    const RigiFatRef *meta)
{
    if (handle == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_fs_dirread 收到空句柄（编译器"
            " bug）\n");
        abort();
    }
    RigiFsDir *d = (RigiFsDir *)rigi_native_rc_payload_of(handle,
        "rigi_fs_dirread");
    uint8_t *meta8 = rigi_fs_span_at(meta, 0, 12, "rigi_fs_dirread");
    if (out == NULL || out->payload == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_fs_dirread 收到空缓冲区（编译器"
            " bug）\n");
        abort();
    }
    const uint8_t *obase = (const uint8_t *)(uintptr_t)out->payload;
    int32_t ocap;
    memcpy(&ocap, obase + 24, sizeof(int32_t));
    uint8_t *odata = (uint8_t *)(uintptr_t)obase + 32;
    for (;;)
    {
#if defined(_WIN32)
        if (!d->find_valid)
        {
            return 1;
        }
        DWORD nlen = (DWORD)wcslen(d->data.cFileName);
        /* 快速跳过 ASCII "."/".." */
        if ((nlen == 1 && d->data.cFileName[0] == L'.')
            || (nlen == 2 && d->data.cFileName[0] == L'.'
                && d->data.cFileName[1] == L'.'))
        {
            if (!FindNextFileW(d->find, &d->data))
            {
                DWORD err = GetLastError();
                d->find_valid = 0;
                FindClose(d->find);
                d->find = INVALID_HANDLE_VALUE;
                if (err != ERROR_NO_MORE_FILES)
                {
                    return -rigi_fs_map_winerr(err);
                }
            }
            continue;
        }
        int need = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
            d->data.cFileName, (int)nlen, NULL, 0, NULL, NULL);
        if (need <= 0)
        {
            return -RIGI_FS_E_ENCODING;
        }
        if (need > ocap)
        {
            memcpy(meta8, &need, 4);
            return 2;
        }
        if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
            d->data.cFileName, (int)nlen, (char *)odata, ocap, NULL,
            NULL) <= 0)
        {
            return -RIGI_FS_E_ENCODING;
        }
        int32_t n32 = need;
        memcpy(meta8, &n32, 4);
        DWORD a = d->data.dwFileAttributes;
        int32_t kind = (a & FILE_ATTRIBUTE_REPARSE_POINT) ? 2
            : (a & FILE_ATTRIBUTE_DIRECTORY) ? 1 : 0;
        memcpy(meta8 + 4, &kind, 4);
        int32_t avail = 1;
        memcpy(meta8 + 8, &avail, 4);
        /* 推进到下一候选（耗尽即关句柄早释放） */
        if (!FindNextFileW(d->find, &d->data))
        {
            DWORD err = GetLastError();
            d->find_valid = 0;
            FindClose(d->find);
            d->find = INVALID_HANDLE_VALUE;
            if (err != ERROR_NO_MORE_FILES)
            {
                return -rigi_fs_map_winerr(err);
            }
        }
        return 0;
#else
        if (d->dir == NULL)
        {
            return 1;
        }
        errno = 0;
        struct dirent *ent = readdir(d->dir);
        if (ent == NULL)
        {
            if (errno != 0)
            {
                return -rigi_fs_map_errno(errno);
            }
            closedir(d->dir);
            d->dir = NULL;
            return 1;
        }
        if (ent->d_name[0] == '.'
            && (ent->d_name[1] == 0
                || (ent->d_name[1] == '.' && ent->d_name[2] == 0)))
        {
            continue;
        }
        int64_t nlen = (int64_t)strlen(ent->d_name);
        if (!rigi_fs_utf8_valid((const uint8_t *)ent->d_name, nlen))
        {
            return -RIGI_FS_E_ENCODING; /* 不替换不跳过（§4.5.2） */
        }
        int32_t need = (int32_t)nlen;
        if (need > ocap)
        {
            memcpy(meta8, &need, 4);
            return 2;
        }
        memcpy(odata, ent->d_name, (size_t)nlen);
        memcpy(meta8, &need, 4);
        int32_t kind = 0;
        int32_t avail = 0;
#ifdef _DIRENT_HAVE_D_TYPE
        if (ent->d_type == DT_REG) { kind = 0; avail = 1; }
        else if (ent->d_type == DT_DIR) { kind = 1; avail = 1; }
        else if (ent->d_type == DT_LNK) { kind = 2; avail = 1; }
        else if (ent->d_type != DT_UNKNOWN) { kind = 3; avail = 1; }
#endif
        memcpy(meta8 + 4, &kind, 4);
        memcpy(meta8 + 8, &avail, 4);
        return 0;
#endif
    }
}
