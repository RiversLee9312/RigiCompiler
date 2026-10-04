using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // FileSystem.Open 职责；与主文件共享同一类型、字段及生命周期。

        // Resource 是 FileStream（文件句柄）或 DirState（目录句柄）；
        // read/write/flush 三类在途槽互相独立（同一句柄同类同时刻至多
        // 一个在途——契约禁止同流并发/重入（§4.4），冲突诊断抛错）
        private sealed class VmFsFile
        {
            public required object Resource { get; init; }
            // 追加模式（fs_open 带 FAppend|FCreate）：§4.5.6「系统追加
            // 机制使每次写入始终到达当时末尾」。句柄本身必须是 OS 追加
            // 专用形态（Windows 仅 FILE_APPEND_DATA 的句柄 / Linux
            // O_APPEND fd，见 FsOpen→OpenAppendStream）——.NET 的
            // FileMode.Append 只是「打开时定位一次末尾」的普通写句柄
            //（Windows GENERIC_WRITE 含 FILE_WRITE_DATA，Linux fdinfo
            // 实测无 O_APPEND），不能只凭 FileMode 名字推断追加语义；
            // 写线程体据此走 AppendWriteCore（单次系统调用内由内核选位，
            // 绝不 Seek(End)+Write——两步用户态非原子，其他进程可在
            // 两步之间增长文件致本笔覆盖）
            public bool AppendMode;
            public bool ReadInflight;
            public int ReadResult;
            public bool WriteInflight;
            public int WriteResult;
            public bool FlushInflight;
            public int FlushResult;
        }

        // 目录句柄状态：diropen 预热首条目（native FindFirstFileW 同口径
        // ——打开即验证，空目录合法）；结束后持续返回结束
        private sealed class DirState
        {
            public required IEnumerator<FileSystemInfo> Enumerator { get; init; }
            public FileSystemInfo? Pending;
            public bool Finished;
        }

        private static FileStream RequireFsStream(VmFsFile file, string hook)
        {
            if (file.Resource is not FileStream stream)
            {
                throw new VmException(hook + "：句柄不是文件（目录句柄不可用）");
            }
            return stream;
        }

        private static DirState RequireFsDir(VmFsFile file, string hook)
        {
            if (file.Resource is not DirState dir)
            {
                throw new VmException(hook + "：句柄不是目录（文件句柄不可用）");
            }
            return dir;
        }

        // .NET 异常 → 归一错误码（与 rigi_rt fs.c 的 rigi_fs_map_winerr
        // /rigi_fs_map_errno 及 Rigi 层 fsErrKind 三方同表；Windows 的
        // IOException HResult 低 16 位是 Win32 错误码——IO 错误族的宿主
        // 形态；Unix 的 IOException HResult 低 16 位是 errno，走同值的
        // errno 归一表。UnauthorizedAccessException 由调用点先判「目录
        // 当文件开」补救，到达本表即按权限不足归类）
        private static int MapFsError(Exception ex)
        {
            switch (ex)
            {
                case FileNotFoundException:
                case DirectoryNotFoundException:
                    return 2;   // NotFound
                case UnauthorizedAccessException:
                    return 13;  // PermissionDenied
                case NotSupportedException:
                    return 38;  // Unsupported
                case ArgumentException:
                    return 22;  // InvalidPath
                case OutOfMemoryException:
                    return 12;
                case IOException io:
                    return OperatingSystem.IsWindows()
                        ? MapFsWin32(io.HResult & 0xFFFF)
                        : MapFsErrno(io.HResult & 0xFFFF);
                default:
                    return 1000; // Other
            }
        }

        // Unix errno → 归一码（与 rigi_rt fs.c rigi_fs_map_errno 同表同值
        // ——EDQUOT 归 NoSpace、ETXTBSY 归 SharingViolation、EOPNOTSUPP
        // 归 Unsupported，注释同源）
        private static int MapFsErrno(int e)
        {
            switch (e)
            {
                case 2: return 2;    // ENOENT
                case 1:             // EPERM
                case 13: return 13; // EACCES
                case 17: return 17; // EEXIST
                case 18: return 18; // EXDEV
                case 20: return 20; // ENOTDIR
                case 21: return 21; // EISDIR
                case 22: return 22; // EINVAL
                case 26: return 26; // ETXTBSY ≈ 共享冲突
                case 28: return 28; // ENOSPC
                case 122: return 28; // EDQUOT 配额满按 NoSpace
                case 30: return 30; // EROFS
                case 31: return 31; // EMLINK
                case 36: return 36; // ENAMETOOLONG
                case 39: return 39; // ENOTEMPTY
                case 40: return 40; // ELOOP（链接循环，不伪装 NotFound）
                case 9: return 9;   // EBADF
                case 12: return 12; // ENOMEM
                case 38: return 38; // ENOSYS
                case 95: return 38; // EOPNOTSUPP
                case 5: return 5;   // EIO
                default: return 1000;
            }
        }

        private static int MapFsWin32(int err)
        {
            switch (err)
            {
                case 2:   // ERROR_FILE_NOT_FOUND
                case 3:   // ERROR_PATH_NOT_FOUND
                case 15:  // ERROR_INVALID_DRIVE
                    return 2;
                case 5:   // ERROR_ACCESS_DENIED
                case 1314: // ERROR_PRIVILEGE_NOT_HELD
                case 998: // ERROR_NOACCESS
                    return 13;
                case 80:  // ERROR_FILE_EXISTS
                case 183: // ERROR_ALREADY_EXISTS
                    return 17;
                case 17:  // ERROR_NOT_SAME_DEVICE
                    return 18;
                case 267: // ERROR_DIRECTORY
                    return 20;
                case 145: // ERROR_DIR_NOT_EMPTY
                    return 39;
                case 112: // ERROR_DISK_FULL
                case 39:  // ERROR_HANDLE_DISK_FULL
                    return 28;
                case 32:  // ERROR_SHARING_VIOLATION
                case 33:  // ERROR_LOCK_VIOLATION
                    return 26;
                case 206: // ERROR_FILENAME_EXCED_RANGE
                case 111: // ERROR_BUFFER_OVERFLOW
                    return 36;
                case 123: // ERROR_INVALID_NAME
                case 87:  // ERROR_INVALID_PARAMETER
                case 131: // ERROR_NEGATIVE_SEEK
                    return 22;
                case 6:   // ERROR_INVALID_HANDLE
                    return 9;
                case 14:  // ERROR_OUTOFMEMORY
                    return 12;
                case 50:  // ERROR_NOT_SUPPORTED
                    return 38;
                case 1920: // ERROR_CANT_ACCESS_FILE（断链的跟随打开）
                    return 2;
                case 29:  // ERROR_WRITE_FAULT
                case 30:  // ERROR_READ_FAULT
                case 31:  // ERROR_GEN_FAILURE
                    return 5;
                default:
                    return 1000;
            }
        }

        // i64 小端写入出参 Span（与 rigi_rt fs.c rigi_fs_write_i64le
        // 同布局；out 长度由 Rigi 层保证 ≥ 8，元素恒 VmU8）
        private static void WriteFsI64Le(VmSpan span, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                span.Elements[i] = new VmU8((byte)(value >> (i * 8)));
            }
        }

        // fs_open（同步直调，Linux x64 用 O_NONBLOCK + 已打开 fd 的
        // fstat 防 FIFO 无对端占住 Worker）：flags 位定义与 rigi_rt
        // fs.c RIGI_FS_F_* 逐位一致；
        // bufferSize 1 = 近无缓冲（对齐 native 直读直写语义）；FileShare
        // ReadWrite|Delete = §4.5.6「默认允许其他进程读写，以及平台支持
        // 的重命名或删除」
        internal VmValue FsOpen(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 4 || args[1] is not VmI32 flags
                || args[2] is not VmI32 mode)
            {
                throw new VmException("fs_open 需要 (String, i32, i32, Span<u8>)");
            }
            var pathValue = args[0] is VmAny pathAny ? pathAny.Payload : args[0];
            if (pathValue is not VmString path)
            {
                throw new VmException("fs_open 第一参数必须是 String");
            }
            var outValue = args[3] is VmAny outAny ? outAny.Payload : args[3];
            if (outValue is not VmSpan outSpan || outSpan.ElementType != ".u8")
            {
                throw new VmException("fs_open 第四参数必须是 Span<u8>");
            }
            const int FRead = 0x1, FAppend = 0x4, FCreate = 0x8,
                FTruncate = 0x10, FCreateNew = 0x20;
            var f = flags.Value;
            FileMode fileMode;
            if ((f & FCreateNew) != 0) { fileMode = FileMode.CreateNew; }
            else if ((f & FCreate) != 0 && (f & FTruncate) != 0)
            {
                fileMode = FileMode.Create;
            }
            else if ((f & FCreate) != 0 && (f & FAppend) != 0)
            {
                fileMode = FileMode.Append;
            }
            else if ((f & FCreate) != 0) { fileMode = FileMode.OpenOrCreate; }
            else { fileMode = FileMode.Open; }
            var access = (f & FRead) != 0 ? FileAccess.Read : FileAccess.Write;
            FileStream? stream = null;
            var registered = false;
            try
            {
                if (fileMode == FileMode.Append)
                {
                    // 追加专用句柄保留内核 O_APPEND 原子性。
                    var rc = OpenAppendStream(path.Value, out stream);
                    if (rc != 0) { return new VmI32(rc); }
                }
                else if (IsFsLinuxX64())
                {
                    // Linux FIFO 的只读/只写 open 均可能等待对端；用
                    // O_NONBLOCK 打开，再按已打开 fd 判定普通文件。
                    var rc = OpenLinuxRegularStream(path.Value, f, mode.Value,
                        out stream);
                    if (rc != 0) { return new VmI32(rc); }
                }
                else
                {
                    if (OperatingSystem.IsLinux())
                    {
                        // 其它 Linux 架构的 openat/fstat ABI 尚未验证，
                        // 不能回退到可能同步等待 FIFO 的 FileStream。
                        return new VmI32(-FsErrnoEnosys);
                    }
                    stream = new FileStream(path.Value, fileMode, access,
                        FileShare.ReadWrite | FileShare.Delete, 1);
                }
                long token;
                lock (_nativeRcGate)
                {
                    token = NewHandle();
                    _nativeRcStrong[token] = 1;
                    lock (_fsGate)
                    {
                        _fsFiles[token] = new VmFsFile
                        {
                            Resource = stream!, // rc==0 蕴含非 null
                            AppendMode = fileMode == FileMode.Append,
                        };
                    }
                }
                registered = true;
                WriteFsI64Le(outSpan, token);
                return new VmI32(0);
            }
            catch (UnauthorizedAccessException)
            {
                // 目录当文件开：与 rigi_fs_open 的 Windows 补救同口径
                //（.NET 对目录开 FileStream 抛 UnauthorizedAccessException；
                // 失败路径补查，仅错误路径非「先查询再打开」）
                if (Directory.Exists(path.Value)) { return new VmI32(-21); }
                return new VmI32(-13);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
            finally
            {
                // 登记失败（异常/返回错误）时句柄不放漏——正常/异常路径
                // 都不泄漏 OS 资源
                if (!registered && stream != null) { stream.Dispose(); }
            }
        }

    }
}
