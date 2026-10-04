using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // FileSystem.Directory 职责；与主文件共享同一类型、字段及生命周期。

        // fs_diropen：打开即验证（native FindFirstFileW 同口径；空目录
        // 合法，不存在/非目录在打开面报错）。OS 打开目录的时机是 BCL
        // 实现细节：可发生在 DirectoryInfo/GetEnumerator 建立期（.NET 10
        // 实证双平台缺失/非目录多在 GetEnumerator 抛出，Unix 构造期即
        // openat），也可推迟到首次 MoveNext——三段同属一次「打开」的生
        // 命周期，共用同一受控异常边界（边界不变量，不按平台特判）。
        // 失败收尾：未建枚举器不释放（不空解引用），已建就地释放；只
        // 有打开+预热全部成功才分配 token 并登记，登记中途失败回滚登
        // 计并释放，绝不发布半开句柄
        internal VmValue FsDirOpen(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_diropen", args, 0).Value;
            var outSpan = RequireFsSpan("fs_diropen", args, 1);
            FileSystemInfo? first = null;
            IEnumerator<FileSystemInfo>? enumerator = null;
            try
            {
                enumerator = new DirectoryInfo(path)
                    .EnumerateFileSystemInfos().GetEnumerator();
                if (enumerator.MoveNext())
                {
                    first = enumerator.Current;
                }
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                // 枚举器 Dispose 是纯句柄关闭不上抛（BCL 单异常模型），
                // 不遮盖主错误
                enumerator?.Dispose();
                // 路径是文件：.NET 对文件路径枚举的异常形态不固定（与
                // native ERROR_DIRECTORY→20 不一致），失败路径补查归
                // NotDirectory（open 同口径，仅错误路径补查）
                if (!Directory.Exists(path) && File.Exists(path))
                {
                    return new VmI32(-20);
                }
                return new VmI32(-MapFsError(ex));
            }
            long token = 0;
            try
            {
                lock (_nativeRcGate)
                {
                    token = NewHandle();
                    _nativeRcStrong[token] = 1;
                    lock (_fsGate)
                    {
                        _fsFiles[token] = new VmFsFile
                        {
                            Resource = new DirState
                            {
                                Enumerator = enumerator,
                                Pending = first,
                                Finished = first == null,
                            },
                        };
                    }
                }
            }
            catch
            {
                // 登记中途失败（仅 OOM 类）：回滚半登记与强引用计数并
                // 释放枚举器，异常原样上抛，不发布无主 token
                lock (_nativeRcGate)
                {
                    _nativeRcStrong.Remove(token);
                }
                lock (_fsGate)
                {
                    _fsFiles.Remove(token);
                }
                enumerator.Dispose();
                throw;
            }
            WriteFsI64Le(outSpan, token);
            return new VmI32(0);
        }

        // fs_dirread：每次交付一个条目（跳过 "."/".."——.NET 枚举器本就
        // 不产 "."/".."；不排序不递归，含隐藏项）。meta[0..4) 名称字节
        // 数、[4..8) kind 提示、[8..12) 提示有效标志；返回 0 条目 / 1
        // 结束（此后持续 1）/ 2 out 不足 / < 0 -归一码。后续条目的
        // MoveNext 是同步元数据操作（与 native readdir 同口径），在闸内
        // 完成
        internal VmValue FsDirRead(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 3 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_dirread 需要 (i64, Span, Span)");
            }
            var outSpan = RequireFsSpan("fs_dirread", args, 1);
            var metaSpan = RequireFsSpan("fs_dirread", args, 2);
            DirState state;
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out var file))
                {
                    throw new VmException("fs_dirread 无效句柄");
                }
                state = RequireFsDir(file, "fs_dirread");
                if (state.Finished && state.Pending == null)
                {
                    return new VmI32(1);
                }
                FileSystemInfo cur;
                if (state.Pending != null)
                {
                    cur = state.Pending;
                    state.Pending = null;
                }
                else
                {
                    try
                    {
                        if (!state.Enumerator.MoveNext())
                        {
                            state.Finished = true;
                            return new VmI32(1);
                        }
                        cur = state.Enumerator.Current!;
                    }
                    catch (Exception ex) when (ex is IOException
                        or UnauthorizedAccessException or ArgumentException
                        or OutOfMemoryException)
                    {
                        return new VmI32(-MapFsError(ex));
                    }
                }
                var attrs = cur.Attributes;
                var kind = (attrs & FileAttributes.ReparsePoint) != 0 ? 2
                    : (attrs & FileAttributes.Directory) != 0 ? 1 : 0;
                var bytes = System.Text.Encoding.UTF8.GetBytes(cur.Name);
                if (bytes.Length > outSpan.Length)
                {
                    WriteFsI32Le(metaSpan, 0, bytes.Length);
                    return new VmI32(2);
                }
                for (var i = 0; i < bytes.Length; i++)
                {
                    outSpan.Elements[i] = new VmU8(bytes[i]);
                }
                WriteFsI32Le(metaSpan, 0, bytes.Length);
                WriteFsI32Le(metaSpan, 4, kind);
                WriteFsI32Le(metaSpan, 8, 1);
                return new VmI32(0);
            }
        }

        // ===== fs_same_file（施工块 7-6）：系统文件身份比较 =====
        // 与 rigi_rt fs.c rigi_fs_same_file 双宿主同语义：按已打开句柄
        // 的系统文件身份（Windows 卷序列号 + 64 位文件索引；
        // Linux st_dev + st_ino）判定两句柄是否同一文件——自复制拒绝
        // 的判定面（§4.5.7），比较不涉及路径文本。out[0] 写 1/0；返回
        // 0；< 0 = -归一码（身份查询失败按 MapFsError 映射；Linux 仅
        // x64 开放 fstat 身份读取，未验证架构/入口缺失——如旧 glibc 无
        // fstat 导出——/libc 缺失一律归 -38 Unsupported：身份不可取得
        // 时上层必须收到错误而不是「不同」，否则自复制检查形同虚设）
        internal VmValue FsSameFile(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 3 || args[0] is not VmI64 ha
                || args[1] is not VmI64 hb)
            {
                throw new VmException("fs_same_file 需要 (i64, i64, Span<u8>)");
            }
            var outSpan = RequireFsSpan("fs_same_file", args, 2);
            var streamA = RequireFsStream(FsFileOf(ha.Value, "fs_same_file"),
                "fs_same_file");
            var streamB = RequireFsStream(FsFileOf(hb.Value, "fs_same_file"),
                "fs_same_file");
            bool same;
            // SafeFileHandle 租借（AppendWriteCore 同口径）：Windows 侧
            // P/Invoke 直接收 SafeFileHandle（marshaler 调用期间自动保
            // 活）；Linux 侧取原始 fd 前显式 DangerousAddRef，finally
            // 归还——两次 fstat 与全部错误/Unsupported 返回分支都被覆盖，
            // 查询失败绝不放行截断（§4.5.7）
            var safeA = streamA.SafeFileHandle;
            var safeB = streamB.SafeFileHandle;
            var keepA = false;
            var keepB = false;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!GetFileInformationByHandle(safeA, out var ia)
                        || !GetFileInformationByHandle(safeB, out var ib))
                    {
                        return new VmI32(-MapFsWin32(
                            System.Runtime.InteropServices.Marshal
                                .GetLastWin32Error()));
                    }
                    same = ia.VolumeSerialNumber == ib.VolumeSerialNumber
                        && ia.FileIndexHigh == ib.FileIndexHigh
                        && ia.FileIndexLow == ib.FileIndexLow;
                }
                else if (OperatingSystem.IsLinux()
                    && System.Runtime.InteropServices.RuntimeInformation
                        .ProcessArchitecture == System.Runtime.InteropServices
                            .Architecture.X64)
                {
                    // 仅 Linux x64 开放 fstat 身份读取（ABI 实证限定的
                    // 生产平台，见 FsLinuxX64Stat 注释）；其余宿主（非
                    // Linux、非 x64）身份不可取得 → Unsupported，绝不猜
                    // 测身份、更不按「不同」继续截断（§4.5.7）
                    safeA.DangerousAddRef(ref keepA);
                    safeB.DangerousAddRef(ref keepB);
                    var fdA = (int)safeA.DangerousGetHandle().ToInt64();
                    var fdB = (int)safeB.DangerousGetHandle().ToInt64();
                    if (fstat(fdA, out var statA) != 0)
                    {
                        return new VmI32(-MapFsErrno(
                            System.Runtime.InteropServices.Marshal
                                .GetLastWin32Error()));
                    }
                    if (fstat(fdB, out var statB) != 0)
                    {
                        return new VmI32(-MapFsErrno(
                            System.Runtime.InteropServices.Marshal
                                .GetLastWin32Error()));
                    }
                    // 只读身份字段（st_dev@0、st_ino@8，完整 144 字节镜像
                    // 的身份前缀），其余字段不参与判定
                    same = statA.Dev == statB.Dev && statA.Ino == statB.Ino;
                }
                else
                {
                    return new VmI32(-38);
                }
            }
            catch (System.EntryPointNotFoundException)
            {
                // 宿主无身份查询入口（如 glibc < 2.33 无 fstat 导出）：
                // 不能判定 → Unsupported（不猜测身份，§4.5.7 不模拟）
                return new VmI32(-38);
            }
            catch (System.DllNotFoundException)
            {
                // 宿主无 libc.so.6（musl 等）：身份入口缺失同 Unsupported，
                // 不漏宿主异常出流契约（OpenAppendStream 同口径）
                return new VmI32(-38);
            }
            finally
            {
                // 租借归还：条件化（未 AddRef 的平台分支/失败路径不受影响）
                if (keepA) { safeA.DangerousRelease(); }
                if (keepB) { safeB.DangerousRelease(); }
            }
            outSpan.Elements[0] = new VmU8(same ? (byte)1 : (byte)0);
            return new VmI32(0);
        }

        // Win32 FILETIME 官方形态：两个 32 位 DWORD、4 字节对齐——不能
        // 用 long 充当：托管 Sequential 默认按自然对齐把 long 放 8 的
        // 倍数偏移，会把整个后段字段错位（本结构曾因此把
        // VolumeSerialNumber 放到 32、FileIndexHigh/Low 压到 48/52，
        // 而 native 只写 52 字节——FileIndexLow 读到未初始化尾隙，身份
        // 判定不可信且不保证复现）。internal 供测试断言布局
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct FsFileTime
        {
            public uint LowDateTime;   // dwLowDateTime @0
            public uint HighDateTime;  // dwHighDateTime @4
        } // 8 字节，对齐 4

        // Win32 BY_HANDLE_FILE_INFORMATION 托管镜像。ABI 真值（Windows
        // SDK 10.0.26100.0 头经 clang -target x86_64-pc-windows-msvc
        // 探针实测，playground/fs_copy_identity/win_abi_truth.log）：
        // sizeof=52、align=4；dwFileAttributes@0、三个 FILETIME@4/12/20、
        // dwVolumeSerialNumber@28、nFileSizeHigh/Low@32/36、
        // nNumberOfLinks@40、nFileIndexHigh/Low@44/48。全字段 4 对齐无
        // 隐式填充，Marshal.SizeOf/OffsetOf 由 VmFsIdentityTests 机械
        // 锁定（源头与 rigi_rt fs.c 同一 Win32 定义，双宿主一致）
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct FsByHandleInfo
        {
            public uint FileAttributes;       // @0
            public FsFileTime CreationTime;   // @4
            public FsFileTime LastAccessTime; // @12
            public FsFileTime LastWriteTime;  // @20
            public uint VolumeSerialNumber;   // @28（身份：卷序列号）
            public uint FileSizeHigh;         // @32
            public uint FileSizeLow;          // @36
            public uint NumberOfLinks;        // @40
            public uint FileIndexHigh;        // @44（身份：64 位文件索引高半）
            public uint FileIndexLow;         // @48（身份：低半）
        } // 52 字节，对齐 4

        // 直接收 SafeFileHandle：marshaler 在调用期间自动保活句柄，
        // 不经 DangerousGetHandle 裸指针（租借口径见 FsSameFile）
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            out FsByHandleInfo info);

        // POSIX glibc x86_64 struct stat 完整镜像（fstat 唯一合法接收
        // 面）。ABI 真值（WSL Ubuntu glibc 2.39 + gcc 13.3 -m64 实测
        // #include <sys/stat.h>，playground/fs_copy_identity/
        // linux_stat_truth.log）：sizeof=144、align=8；st_dev@0、
        // st_ino@8、st_nlink@16、st_mode@24、st_uid@28、st_gid@32、
        // __pad0@36、st_rdev@40、st_size@48、st_blksize@56、
        // st_blocks@64、st_atim@72、st_mtim@88、st_ctim@104、glibc
        // 保留@120..136。历史教训：只声明 16 字节前缀吃 144 字节写入 =
        // 栈越界破坏（身份读垃圾 → 同文件误判「不同」→ 自复制未拒 →
        // 源截断归零，probe_fstat_wsl.log）；只放大缓冲不声明字段仍是
        // ABI 猜测，均不可接受。本镜像全字段/保留字段逐字节对应、无
        // 隐式填充，Marshal.SizeOf/OffsetOf 由 VmFsIdentityTests 机械
        // 锁定；仅在已核实的 Linux x64 生产平台传入 fstat（native 面
        // rigi_rt 用真 C struct stat，无此层）
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct FsLinuxX64Stat
        {
            public ulong Dev;        // st_dev @0（设备号——身份）
            public ulong Ino;        // st_ino @8（inode——身份）
            public ulong Nlink;      // st_nlink @16
            public uint Mode;        // st_mode @24
            public uint Uid;         // st_uid @28
            public uint Gid;         // st_gid @32
            public uint Pad0;        // __pad0 @36（原结构对齐保留）
            public ulong Rdev;       // st_rdev @40
            public ulong Size;       // st_size @48
            public ulong Blksize;    // st_blksize @56
            public ulong Blocks;     // st_blocks @64
            public ulong AtimSec;    // st_atim.tv_sec @72
            public ulong AtimNsec;   // st_atim.tv_nsec @80
            public ulong MtimSec;    // st_mtim.tv_sec @88
            public ulong MtimNsec;   // st_mtim.tv_nsec @96
            public ulong CtimSec;    // st_ctim.tv_sec @104
            public ulong CtimNsec;   // st_ctim.tv_nsec @112
            public ulong Reserved0;  // __glibc_reserved[0] @120
            public ulong Reserved1;  // __glibc_reserved[1] @128
            public ulong Reserved2;  // __glibc_reserved[2] @136
        } // 144 字节，对齐 8（36→40 的空隙与原 __pad0 一致）

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "fstat", SetLastError = true)]
        private static extern int fstat(int fd, out FsLinuxX64Stat stat);

        private static long RequireI64(string hook, IReadOnlyList<VmValue> args, int index)
        {
            if (args.Count <= index || args[index] is not VmI64 value)
            {
                throw new VmException(hook + "：参数 " + index + " 需要 i64");
            }
            return value.Value;
        }

    }
}
