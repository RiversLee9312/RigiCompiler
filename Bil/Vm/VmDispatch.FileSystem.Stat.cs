using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // FileSystem.Stat 职责；与主文件共享同一类型、字段及生命周期。

        // DateTime → (epoch 毫秒 i64, 纳秒余量 i32) 小端写入（Ticks 为
        // 0 = Windows 零 FILETIME「不可得」→ 哨兵；1970 前的负值按向下
        // 取整分解保证纳秒余量恒非负——TimeStamp 的 0..999999 约束）
        private static void WriteFsTimeLe(VmSpan span, int offset, DateTime t)
        {
            long ms;
            int ns;
            if (t.Ticks == 0)
            {
                ms = long.MinValue;
                ns = 0;
            }
            else
            {
                var epoch100 = t.Ticks - 621355968000000000L;
                ms = Math.DivRem(epoch100, 10000, out var rem);
                if (rem < 0)
                {
                    ms -= 1;
                    rem += 10000;
                }
                ns = (int)(rem * 100);
            }
            WriteFsI64LeAt(span, offset, ms);
            WriteFsI32Le(span, offset + 8, ns);
        }

        // 定点 i64 小端写入（与 WriteFsI64Le 同布局，offset 变体）
        private static void WriteFsI64LeAt(VmSpan span, int offset, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                span.Elements[offset + i] = new VmU8((byte)(value >> (i * 8)));
            }
        }

        // stat 结构 48 字节小端（布局 = rigi_rt fs.c RIGI_FS_STAT_SIZE：
        // kind i32 / length i64 / 三组 (毫秒 i64 + 纳秒 i32)）
        private static void WriteFsStatLe(VmSpan span, int kind, long length,
            DateTime mtime, DateTime atime, DateTime birth)
        {
            WriteFsI32Le(span, 0, kind);
            WriteFsI64LeAt(span, 4, length);
            WriteFsTimeLe(span, 12, mtime);
            WriteFsTimeLe(span, 24, atime);
            WriteFsTimeLe(span, 36, birth);
        }

        // POSIX 秒/纳秒保持原始精度，负 epoch 也采用向下取整。
        private static void WriteFsPosixTimeLe(VmSpan span, int offset,
            long seconds, long nanos)
        {
            var ms = checked(seconds * 1000 + nanos / 1_000_000);
            WriteFsI64LeAt(span, offset, ms);
            WriteFsI32Le(span, offset + 8, (int)(nanos % 1_000_000));
        }

        // Linux x64 主体必须是同一次 stat/lstat 快照：类型、时间与身份
        // 来自同一结构。statx 只是辅助 birth 查询；二次路径查询若换了
        // inode/设备或无 BTIME mask，不覆盖成功主体，也绝不借 ctime 代替。
        private static int FsLinuxStat(string path, bool follow, VmSpan output)
        {
            if (path.Length == 0 || path.Contains('\0')) { return -22; }
            // 两次查询共用调用时的 cwd 基准；不规范化 ..，以免穿越链接。
            var absolute = Path.IsPathFullyQualified(path) ? path
                : Path.Combine(Environment.CurrentDirectory, path);
            try
            {
                var rc = follow ? FsLibcStat(absolute, out var body)
                    : FsLibcLstat(absolute, out body);
                if (rc != 0)
                {
                    return -MapFsErrno(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                var mode = body.Mode & 0xF000U;
                var kind = mode == 0xA000 ? 2 : mode == 0x4000 ? 1
                    : mode == 0x8000 ? 0 : 3;
                WriteFsI32Le(output, 0, kind);
                WriteFsI64LeAt(output, 4, kind == 0 ? (long)body.Size : -1);
                WriteFsPosixTimeLe(output, 12, (long)body.MtimSec,
                    (long)body.MtimNsec);
                WriteFsPosixTimeLe(output, 24, (long)body.AtimSec,
                    (long)body.AtimNsec);
                WriteFsI64LeAt(output, 36, long.MinValue);
                WriteFsI32Le(output, 44, 0);
                try
                {
                    // libc statx(2)；不可用/失败只表示 birth 不可得。
                    if (FsLibcStatx(-100, absolute, follow ? 0 : 0x100,
                            0x800, out var extra) == 0
                        && (extra.Mask & 0x800) != 0
                        && extra.DevMajor == FsLinuxDevMajor(body.Dev)
                        && extra.DevMinor == FsLinuxDevMinor(body.Dev)
                        && extra.Ino == body.Ino
                        && extra.BirthNsec < 1_000_000_000)
                    {
                        WriteFsPosixTimeLe(output, 36, extra.BirthSec,
                            extra.BirthNsec);
                    }
                }
                catch (DllNotFoundException) { /* 无 statx：仅 birth 不可得 */ }
                catch (EntryPointNotFoundException) { /* 旧 libc：同上 */ }
                return 0;
            }
            catch (DllNotFoundException) { return -38; }
            catch (EntryPointNotFoundException) { return -38; }
        }

        // Linux dev_t 的 glibc major/minor 位展开（sys/sysmacros.h）。
        private static uint FsLinuxDevMajor(ulong dev) =>
            (uint)(((dev >> 8) & 0xfff) | ((dev >> 32) & ~0xfffUL));
        private static uint FsLinuxDevMinor(ulong dev) =>
            (uint)((dev & 0xff) | ((dev >> 12) & ~0xffUL));

        // Linux 内核 struct statx：固定 256 字节，不能用字段前缀 out
        // 参数接收（内核会写满完整结构）。偏移由 C sizeof/offsetof 探针
        // 实测：mask@0、ino@32、btime.tv_sec@80、tv_nsec@88、
        // dev_major@136、dev_minor@140；仅用于已核实的 Linux x64。
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Explicit, Size = 256)]
        internal struct FsLinuxX64Statx
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public uint Mask;
            [System.Runtime.InteropServices.FieldOffset(32)] public ulong Ino;
            [System.Runtime.InteropServices.FieldOffset(80)] public long BirthSec;
            [System.Runtime.InteropServices.FieldOffset(88)] public uint BirthNsec;
            [System.Runtime.InteropServices.FieldOffset(136)] public uint DevMajor;
            [System.Runtime.InteropServices.FieldOffset(140)] public uint DevMinor;
        }

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "stat", SetLastError = true)]
        private static extern int FsLibcStat(
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)]
            string path, out FsLinuxX64Stat stat);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "lstat", SetLastError = true)]
        private static extern int FsLibcLstat(
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)]
            string path, out FsLinuxX64Stat stat);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "statx", SetLastError = true)]
        private static extern int FsLibcStatx(int dirfd,
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)]
            string path, int flags, uint mask, out FsLinuxX64Statx statx);

        // fs_stat（跟随末段链接）/ fs_lstat（只查询末段链接本身）：File
        // .GetAttributes 不跟随末段（Win32 GetFileAttributesW 语义；Unix
        // .NET 6+ lstat 语义，symlink 报 ReparsePoint 位）——lstat 直接
        // 采用；stat 遇末段链接经 ResolveLinkTarget(final) 解析（断链 →
        // NotFound）。时间/长度经 FileSystemInfo（同为不跟随末段语义）。
        // Windows 侧 reparse tag 不细分（junction/symlink/未知统一 Link
        // 提示）；native 侧细分（junction/symlink → Link、未知 → Other）
        // ——语料不构造未知 reparse 对象，公共契约不受影响
        private VmValue FsStatImpl(IReadOnlyList<VmValue> args, bool follow,
            string hook)
        {
            var path = RequireFsString(hook, args, 0).Value;
            var outSpan = RequireFsSpan(hook, args, 1);
            try
            {
                if (OperatingSystem.IsLinux()
                    && System.Runtime.InteropServices.RuntimeInformation
                        .ProcessArchitecture == System.Runtime.InteropServices
                            .Architecture.X64)
                {
                    return new VmI32(FsLinuxStat(path, follow, outSpan));
                }
                var attrs = File.GetAttributes(path);
                var current = path;
                if (follow && (attrs & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo link = Directory.Exists(path)
                        ? new DirectoryInfo(path)
                        : new FileInfo(path);
                    var final = link.ResolveLinkTarget(
                        returnFinalTarget: true);
                    if (final == null)
                    {
                        return new VmI32(-2); // 断链：跟随目标不可达
                    }
                    current = final.FullName;
                    attrs = File.GetAttributes(current);
                }
                var isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
                var isDir = (attrs & FileAttributes.Directory) != 0;
                var kind = isReparse ? 2 : isDir ? 1 : 0;
                FileSystemInfo fsi = isDir
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                fsi.Refresh();
                var length = kind == 0 && fsi is FileInfo fi ? fi.Length : -1;
                WriteFsStatLe(outSpan, kind, length, fsi.LastWriteTimeUtc,
                    fsi.LastAccessTimeUtc, OperatingSystem.IsWindows()
                        ? fsi.CreationTimeUtc : DateTime.MinValue);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        internal VmValue FsStat(IReadOnlyList<VmValue> args)
        {
            return FsStatImpl(args, follow: true, "fs_stat");
        }

        internal VmValue FsLstat(IReadOnlyList<VmValue> args)
        {
            return FsStatImpl(args, follow: false, "fs_lstat");
        }

    }
}
