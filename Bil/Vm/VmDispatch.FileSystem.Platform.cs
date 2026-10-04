using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // FileSystem.Platform 职责；与主文件共享同一类型、字段及生命周期。

        private static bool IsFsLinuxX64() => OperatingSystem.IsLinux()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64;

        // Linux x64 普通文件入口：非阻塞 openat 防 FIFO 等待，随后仅凭
        // fd 的完整 fstat 判型。FIFO 无读者/AF_UNIX socket 的 ENXIO
        // 没有 fd；失败路径用 O_PATH 新句柄判型，其余错误保留原 errno。
        private static int OpenLinuxRegularStream(string path, int flags,
            int mode, out FileStream? stream)
        {
            stream = null;
            const int fWrite = 0x2, fCreate = 0x8, fTruncate = 0x10,
                fCreateNew = 0x20;
            var write = (flags & fWrite) != 0;
            var oflags = (write ? FsO_WRONLY : 0) | FsO_NONBLOCK;
            if ((flags & fCreateNew) != 0) { oflags |= FsO_CREAT | FsO_EXCL; }
            else
            {
                if ((flags & fCreate) != 0) { oflags |= FsO_CREAT; }
                if ((flags & fTruncate) != 0) { oflags |= FsO_TRUNC; }
            }
            int fd;
            try
            {
                fd = OpenAt(FsAtFdcwd, path, oflags, mode);
                if (fd < 0)
                {
                    var e = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (e == FsErrnoEnxio
                        && ProbeLinuxNonRegular(path)) { return -FsWrongType; }
                    return -MapFsErrno(e);
                }
            }
            catch (DllNotFoundException) { return -FsErrnoEnosys; }
            catch (EntryPointNotFoundException) { return -FsErrnoEnosys; }
            return WrapLinuxRegularFd(fd, write ? FileAccess.Write : FileAccess.Read,
                out stream);
        }

        private static bool ProbeLinuxNonRegular(string path)
        {
            // 仅 ENXIO 失败时另开 O_PATH 句柄，既可判 socket 也可判
            // FIFO；不能用 pathname stat 代替句柄校验，也不能当作
            // 原读写句柄使用。打开/查询失败保留原始 ENXIO。
            int fd;
            try { fd = OpenAt(FsAtFdcwd, path, FsO_PATH | FsO_CLOEXEC, 0); }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            if (fd < 0) { return false; }
            using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(
                (IntPtr)fd, ownsHandle: true);
            try
            {
                if (fstat(fd, out var stat) != 0) { return false; }
                var kind = stat.Mode & FsSIfmt;
                return kind != FsSIfreg && kind != FsSIfdir;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static int WrapLinuxRegularFd(int fd, FileAccess access,
            out FileStream? stream)
        {
            stream = null;
            var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(
                (IntPtr)fd, ownsHandle: true);
            var transferred = false;
            try
            {
                if (fstat(fd, out var stat) != 0)
                {
                    return -MapFsErrno(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                var kind = stat.Mode & FsSIfmt;
                if (kind == FsSIfdir) { return -21; }
                if (kind != FsSIfreg) { return -FsWrongType; }
                var current = Fcntl(fd, FsFGetfl, 0);
                if (current < 0 || Fcntl(fd, FsFSetfl,
                    current & ~FsO_NONBLOCK) < 0)
                {
                    return -MapFsErrno(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                // 仅构造成功才将 SafeFileHandle 所有权交给 FileStream。
                stream = new FileStream(handle, access, 1);
                transferred = true;
                return 0;
            }
            catch (DllNotFoundException) { return -FsErrnoEnosys; }
            catch (EntryPointNotFoundException) { return -FsErrnoEnosys; }
            finally { if (!transferred) { handle.Dispose(); } }
        }

        // 打开 OS 追加专用文件流（§4.5.6「系统追加机制」的 VM 面唯一
        // 收口）。证据基线（playground/vm_append_atomic/ 探针实证）：
        // .NET FileMode.Append 两平台都不是系统追加——Windows 句柄
        // GENERIC_WRITE 含 FILE_WRITE_DATA，写经 NtWriteFile 提交显式
        // ByteOffset（落到句柄位置而非当时末尾）；Linux fdinfo flags 无
        // O_APPEND（02100001），ctor 仅 Seek 一次、写走 pwrite 显式
        // offset。因此：
        //   Windows：CreateFileW 仅申请 FILE_APPEND_DATA（不含
        //     FILE_WRITE_DATA）+ FILE_READ_ATTRIBUTES（getLength 查
        //     FileStandardInformation 所需；不放开内容读）+ SYNCHRONIZE
        //     ——内核契约：无 FILE_WRITE_DATA 的 FILE_APPEND_DATA 句柄
        //     上 WriteFile 忽略句柄当前位置、每笔写落当时末尾；
        //     FlushFileBuffers/Flush(true) 对该句柄可用（flush 持久化
        //     面）。OPEN_ALWAYS = 不存在则创建、存在不截断；
        //   Linux：openat(AT_FDCWD, O_WRONLY|O_CREAT|O_APPEND|
        //   O_NONBLOCK, 0666)，非阻塞打开防 FIFO 无读者等待，随后按
        //   已打开 fd 判型并清掉 O_NONBLOCK；O_APPEND 保留，确保每次
        //   write(2) 在系统调用内原子落在当时末尾；
        //   创建权限 0o666（rw-rw-rw-）受 umask（§4.5.5，对齐 .NET
        //   FileMode.Append 的 Unix 默认与 native 面）。
        //   openat 与 open 同为 variadic（POSIX：mode 为可选变参）。
        //   当前 libc.so.6 的带 mode openat 入口仅在 Linux x64 验证；
        //   未验证平台（macOS/freebsd x64、Linux 非 x64 等）经平台闸门
        //   明确报 Unsupported（-38），不在代码里对其它架构的 variadic
        //   ABI 行为做必对/必错断言；libc.so.6 缺失或入口缺失也受控归
        //   Unsupported，不漏宿主异常出流契约。
        // 两平台返回值均由 AppendWriteCore 的系统调用内选位兑现。
        // 失败返回 -归一码（MapFsWin32/MapFsErrno 同表；Windows 目录
        // 当文件开是 ACCESS_DENIED，失败路径补查目录归 -21）
        // 契约：返回 0 时 stream 必非 null，非 0 时 stream 恒 null
        //（int 返回不适用 NotNullWhen(true)——该属性仅约定 bool 返回；
        // 调用点以 rc==0 分支为准）
        internal static int OpenAppendStream(string path,
            out FileStream? stream)
        {
            stream = null;
            if (OperatingSystem.IsWindows())
            {
                var handle = CreateFileW(path,
                    FsWinFileAppendData | FsWinFileReadAttributes
                        | FsWinSynchronize,
                    FsWinShareReadWriteDelete, IntPtr.Zero, FsWinOpenAlways,
                    FsWinFileAttributeNormal, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (err == FsWinErrorAccessDenied
                        && Directory.Exists(path))
                    {
                        return -21; // 目录当文件开（IsDir，与 native 同码）
                    }
                    return -MapFsWin32(err);
                }
                try
                {
                    stream = new FileStream(handle, FileAccess.Write, 1);
                    return 0;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            // 非 Windows 仅限定 Linux x64（glibc）：libc.so.6 仅 glibc
            // 提供，带 mode 的 openat 入口只在 Linux x64 验证过——其余
            // 系统/架构（macOS、freebsd x64、Linux 非 x64 等）显式
            // Unsupported（-38），不把全部非 Windows 当 Linux，也不对
            // 未验证平台的 variadic ABI 行为做断言
            if (!OperatingSystem.IsLinux()
                || System.Runtime.InteropServices.RuntimeInformation
                    .ProcessArchitecture
                != System.Runtime.InteropServices.Architecture.X64)
            {
                return -FsErrnoEnosys; // Unsupported（=ENOSYS 归一同值）
            }
            int fd;
            try
            {
                fd = OpenAt(FsAtFdcwd, path,
                    FsO_WRONLY | FsO_CREAT | FsO_APPEND | FsO_NONBLOCK,
                    FsCreateMode0666);
                if (fd < 0)
                {
                    var e = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (e == FsErrnoEnxio && ProbeLinuxNonRegular(path))
                    {
                        return -FsWrongType;
                    }
                    return -MapFsErrno(e);
                }
            }
            catch (DllNotFoundException)
            {
                return -FsErrnoEnosys; // libc.so.6 缺失：受控 Unsupported
            }
            catch (EntryPointNotFoundException)
            {
                return -FsErrnoEnosys; // 入口缺失：受控 Unsupported
            }
            return WrapLinuxRegularFd(fd, FileAccess.Write, out stream);
        }

        // 追加写核心（§4.5.6：写入位置由 OS 在本系统调用内原子选择，
        // 到当时末尾）。返回实际写出字节数（允许短写，Rigi 层循环补齐
        // ——与 native 面单次系统调用形态一致）；错误抛 IOException
        //（HResult 低 16 位 = Win32 错误码/errno，由 MapFsError 归一）。
        // Windows WriteFile 无 overlapped = 同步句柄当前位置语义，而
        // append-only 句柄由内核强制落 EOF；Linux write(2) 由 O_APPEND
        // 强制落 EOF；EINTR（信号中断）重试——不是流错误
        internal static int AppendWriteCore(FileStream stream, byte[] buffer,
            int length)
        {
            var handle = stream.SafeFileHandle;
            if (OperatingSystem.IsWindows())
            {
                if (!WriteFile(handle, buffer, length, out var written,
                    IntPtr.Zero))
                {
                    throw new IOException("fs_write",
                        System.Runtime.InteropServices.Marshal.GetHRForLastWin32Error());
                }
                return written;
            }
            // SafeFileHandle 租借：DangerousGetHandle 取原始 fd 的调用
            // 期间显式 AddRef/Release——阻断 JIT 在原生 write 执行期间
            // 把句柄判不可达而提前终结关闭 fd 的窗口（SafeFileHandle
            // 官方模式；Windows 分支不需此步——WriteFile 直接收
            // SafeFileHandle，编组层自动保活）
            var refAdded = false;
            try
            {
                handle.DangerousAddRef(ref refAdded);
                var fd = (int)handle.DangerousGetHandle();
                long n;
                do
                {
                    n = Write(fd, buffer, (IntPtr)length);
                } while (n < 0 && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == FsErrnoEintr);
                if (n < 0)
                {
                    throw new IOException("fs_write",
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
                return (int)n;
            }
            finally
            {
                if (refAdded) { handle.DangerousRelease(); }
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string lpFileName, int dwDesiredAccess, int dwShareMode,
            IntPtr lpSecurityAttributes, int dwCreationDisposition,
            int dwFlagsAndAttributes, IntPtr hTemplateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            SetLastError = true)]
        private static extern bool WriteFile(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile, byte[] lpBuffer,
            int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        // openat 与 open 同为 variadic（POSIX：mode 为可选变参）。
        // 带 mode 入口仅在 Linux x64 + glibc 验证；调用点由
        // OpenAppendStream/OpenLinuxRegularStream 的平台闸门与异常捕获
        // 受控捕获约束（缺库/缺入口归 Unsupported，不漏宿主异常）
        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAt(int dirfd, string pathname,
            int flags, int mode);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "fcntl", SetLastError = true)]
        private static extern int Fcntl(int fd, int command, int value);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "write", SetLastError = true)]
        private static extern long Write(int fd, byte[] buf, IntPtr count);

        // renameat2（Linux；glibc ≥ 2.28 导出 renameat2 符号）：固定参数
        // 原型，无 openat 的 variadic ABI 假设，不设架构闸门（与 native
        // 面 SYS_renameat2 无架构限制同口径）；路径为 NUL 结尾字节串，
        // CharSet.Ansi 在 Unix 编组层即 UTF-8，与 OpenAt 先例同口径。
        // AT_FDCWD 双路径在单次系统调用内以同一 cwd 基准解析
        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "renameat2", SetLastError = true)]
        private static extern int RenameAt2(int oldDirFd, string oldPath,
            int newDirFd, string newPath, uint flags);

        // Unix Replace 非目录源只允许系统 rename：不可使用 File.Move 的
        // EXDEV 复制+删除回退（违背 §4.5.7 跨设备报错）。
        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "rename", SetLastError = true)]
        private static extern int RenameUnix(string oldPath, string newPath);

        // renameat2(RENAME_NOREPLACE) helper：返回 0 成功 / -归一码。
        // 内核在单次系统调用内原子完成「目标不存在检查 + 移动」裁决
        //（§4.5.7 系统保证，不用 exists + rename 模拟），文件、真实目
        // 录、符号链接/断链条目一视同仁（rename 系不跟随末段链接）。
        // errno 读取紧跟失败：ENOSYS/EINVAL/EOPNOTSUPP（内核或文件系
        // 统不提供该保证，EINVAL 对齐 native 特判——宿主不认识该原语
        // 不伪装成路径错）→ Unsupported；EXDEV → CrossDevice（不退化
        // 复制删除）；其余 MapFsErrno 同表。错误原样报告、不隐式重试
        // 或回退（与现有 native 面同口径；本任务不重写异步 IO 层）。
        // 缺库/缺入口受控 Unsupported
        private static int RenameNoReplaceLinux(string src, string dst)
        {
            try
            {
                if (RenameAt2(FsAtFdcwd, src, FsAtFdcwd, dst,
                        FsRenameNoReplace) == 0)
                {
                    return 0;
                }
                var errno = System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error();
                if (errno == FsErrnoEinval || errno == FsErrnoEnosys)
                {
                    return -FsErrnoEnosys;
                }
                return -MapFsErrno(errno); // EOPNOTSUPP 95 → 38 同表
            }
            catch (DllNotFoundException)
            {
                return -FsErrnoEnosys; // libc.so.6 缺失：受控 Unsupported
            }
            catch (EntryPointNotFoundException)
            {
                return -FsErrnoEnosys; // 入口缺失：受控 Unsupported
            }
        }

        // Replace 非目录源：普通 rename 原子覆盖文件/链接；跨设备原样
        // EXDEV，绝不经 BCL File.Move 的复制+删除回退。仅 Linux 可用。
        private static int RenameReplaceFileLinux(string src, string dst)
        {
            try
            {
                if (RenameUnix(src, dst) == 0)
                {
                    return 0;
                }
                var errno = System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error();
                if (errno == 20 || errno == 21 || errno == 39)
                {
                    // 仅在系统调用失败后分类目录目标，不以查询保证安全。
                    try
                    {
                        var attrs = File.GetAttributes(dst);
                        if ((attrs & FileAttributes.Directory) != 0
                            && (attrs & FileAttributes.ReparsePoint) == 0)
                        {
                            return -21;
                        }
                    }
                    catch (Exception ex) when (ex is IOException
                        or UnauthorizedAccessException or ArgumentException)
                    {
                        // 并发目标改变时保留原始系统错误。
                    }
                }
                return -MapFsErrno(errno);
            }
            catch (DllNotFoundException)
            {
                return -FsErrnoEnosys;
            }
            catch (EntryPointNotFoundException)
            {
                return -FsErrnoEnosys;
            }
        }

    }
}
