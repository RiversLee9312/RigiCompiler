using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// VM fs_same_file 系统文件身份机制测试（STDLIB §4.5.7）：直接驱动
    /// VmDispatch.FsOpen/FsSameFile 原语。契约要点：Overwrite 截断前按
    /// 实际打开源/目标的系统文件身份拒绝自复制（不能只比较路径文本，
    /// 不能只先查名称再打开截断）。本套件锁定身份判定的四矩阵：① 相
    /// 同句柄、② 独立打开同文件（File.copy 实际形态）、③ 硬链接别名
    ///（fixture 由测试侧 link(2)/CreateHardLinkW 提供，不加 Rigi 链接
    /// 创建公开 API）、④ 不同文件——全部与 Windows 卷序列号+文件索引 /
    /// Linux st_dev+st_ino 的系统语义对拍。
    /// 回归锚点（Linux VM 自复制未拒 → 源截断归零事故）：旧实现用 16
    /// 字节 out 结构接收 glibc fstat 的完整 struct stat 写入（x86_64
    /// 144 字节），托管栈帧越界破坏后身份读到垃圾——同文件误判「不同」
    /// → 自复制未被拒绝 → File.copy 截断源文件（实证 playground/
    /// fs_copy_identity/probe_fstat_wsl.log；256 字节缓冲修复形态全绿
    /// probe_fstat_buf_wsl.log）。本套件防两点回归：(a) 身份判定值正确
    /// （四矩阵全过）；(b) 身份读取失败不得当「不同」——无效 token、
    /// 目录句柄等失败面必须异常/负码，绝不返回 same=0 放行截断。
    /// </summary>
    public static class VmFsIdentityTests
    {



        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static TestSuiteData Spec => new(
            "VmFsIdentity", Cases, sectionTitle: "VmFsIdentity");

        private static readonly (string Label, Action Run)[] Cases =
        {
            // ①② 合并在 TestSameHandleMatrix（相同句柄 + 独立打开同文件
            // 两矩阵同属「同身份」面，共享 fixture）
            ("TestSameHandleMatrix", TestSameHandleMatrix),
            ("TestHardlinkAliasSameIdentity", TestHardlinkAliasSameIdentity),
            ("TestDifferentFilesDiffer", TestDifferentFilesDiffer),
            ("TestIdentityFailureNotDifferent", TestIdentityFailureNotDifferent),
            // 双平台接收布局机械锁定：Marshal.SizeOf/OffsetOf 与 C 探针
            // 真值逐值对照（真值日志见 playground/fs_copy_identity/
            // win_abi_truth.log 与 linux_stat_truth.log）
            ("TestAbiLayoutAssertions", TestAbiLayoutAssertions),
            ("TestFsOpenRegularOnly", TestFsOpenRegularOnly),
        };

        // 最小宿主模块（Fs 原语不依赖模块内容，样板同 VmFsNoReplaceTests）
        private const string ModuleSource =
            "pub func main(): i32 { return 0 }\n";

        private static VmDispatch NewDispatch()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(ModuleSource);
            CaseAssertions.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var context = new VmContext(module);
            context.InitializeSingletons();
            context.InvokeGlobalInitializers();
            return context.Dispatch;
        }

        private static string NewRoot()
        {
            // worker 的 TMP 根已隔离请求；保留完整 GUID，但缩短二级目录，
            // 避免 pathname socket 再叠长前缀后超过 Linux sun_path 的 108 字节。
            var dir = Path.Combine(Path.GetTempPath(),
                "fi" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // fs_open 直驱：只读打开（FS_F_READ=0x1、mode 0，与 stdlib
        // fsOpen(path, FS_F_READ, 0) 同参），返回登记 token
        private static long OpenRead(VmDispatch dispatch, string path)
        {
            var outSpan = new VmSpan(".u8", 8, new VmU8(0), isShared: false);
            var rc = ((VmI32)dispatch.FsOpen(new VmValue[]
            {
                new VmString(path), new VmI32(0x1), new VmI32(0), outSpan,
            })).Value;
            CaseAssertions.CheckTrue("fs_open 成功", rc == 0, "rc=" + rc);
            long token = 0;
            for (var i = 7; i >= 0; i--)
            {
                token = (token << 8)
                    | ((VmU8)outSpan.Elements[i]).Value;
            }
            return token;
        }

        // fs_same_file 直驱：返回 (rc, same)；rc==0 时 same 才有效
        private static (int Rc, bool Same) SameFile(VmDispatch dispatch,
            long a, long b)
        {
            var outSpan = new VmSpan(".u8", 1, new VmU8(0), isShared: false);
            var rc = ((VmI32)dispatch.FsSameFile(new VmValue[]
            {
                new VmI64(a), new VmI64(b), outSpan,
            })).Value;
            return (rc, rc == 0 && ((VmU8)outSpan.Elements[0]).Value != 0);
        }

        // 用例内句柄统一经 native_rc release 收口（析构回调关闭底层流）
        private static void Release(VmDispatch dispatch, long token)
        {
            dispatch.NativeRcRelease(new VmValue[] { new VmI64(token) });
        }

        private static void CheckSame(string name, VmDispatch dispatch,
            long a, long b, bool wantSame)
        {
            var (rc, same) = SameFile(dispatch, a, b);
            CaseAssertions.CheckTrue(name + "（rc==0）", rc == 0, "rc=" + rc);
            CaseAssertions.CheckTrue(name, same == wantSame,
                "same=" + same + " want=" + wantSame);
        }

        // Linux FIFO 无对端时 O_RDONLY/O_WRONLY 旧路径同步挂住 Worker。
        // 用命名管道的零写端/零读端验证读、普通写、追加均立即返回
        // WrongType=-1002；普通文件及其链接仍允许，目录仍 IsDirectory。
        // 外部 shell watchdog 负责兜底本用例的旧版无限等待。
        private static void TestFsOpenRegularOnly()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture
                != Architecture.X64) { CaseAssertions.RecordSkip("SKIP Linux x64-only regular fixture"); return; }
            var dispatch = NewDispatch();
            var root = NewRoot();
            try
            {
                var fifo = Path.Combine(root, "pipe");
                if (MkFifo(fifo, 0x180) != 0)
                {
                    CaseAssertions.CheckTrue("mkfifo fixture", false,
                        "errno=" + Marshal.GetLastWin32Error());
                    return;
                }
                var output = new VmSpan(".u8", 8, new VmU8(0), isShared: false);
                foreach (var (label, flags) in new[]
                {
                    ("read", 1), ("write", 2), ("append", 2 | 4 | 8),
                })
                {
                    var rc = ((VmI32)dispatch.FsOpen(new VmValue[]
                    {
                        new VmString(fifo), new VmI32(flags), new VmI32(0x1B6), output,
                    })).Value;
                    CaseAssertions.CheckTrue("FIFO 无对端 " + label + " WrongType",
                        rc == -1002, "rc=" + rc);
                }
                // AF_UNIX pathname socket 读/写/追加 open 均可返回 ENXIO，
                // 无法得到原 fd；失败路径须以 O_PATH 句柄重新判型。
                var socketPath = Path.Combine(root, "socket");
                using (var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified))
                {
                    socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(
                        socketPath));
                    foreach (var (label, flags) in new[]
                    {
                        ("read", 1), ("write", 2), ("append", 2 | 4 | 8),
                    })
                    {
                        var rc = ((VmI32)dispatch.FsOpen(new VmValue[]
                        {
                            new VmString(socketPath), new VmI32(flags),
                            new VmI32(0x1B6), output,
                        })).Value;
                        CaseAssertions.CheckTrue("socket pathname " + label + " WrongType",
                            rc == -1002, "rc=" + rc);
                    }
                }
                var normal = Path.Combine(root, "regular.bin");
                File.WriteAllText(normal, "ok");
                var alias = Path.Combine(root, "alias.bin");
                File.CreateSymbolicLink(alias, normal);
                var token = OpenRead(dispatch, alias);
                Release(dispatch, token);
                var directoryRc = ((VmI32)dispatch.FsOpen(new VmValue[]
                {
                    new VmString(root), new VmI32(1), new VmI32(0), output,
                })).Value;
                CaseAssertions.CheckTrue("真实目录仍 IsDirectory",
                    directoryRc == -21, "rc=" + directoryRc);
            }
            finally
            {
                var fifo = Path.Combine(root, "pipe");
                if (File.Exists(fifo)) { File.Delete(fifo); }
                var socket = Path.Combine(root, "socket");
                if (File.Exists(socket)) { File.Delete(socket); }
                Directory.Delete(root, recursive: true);
            }
        }

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "mkfifo", SetLastError = true)]
        private static extern int MkFifo(string path, uint mode);

        // ===== 四矩阵 ①②：相同句柄 same；独立打开同文件 same（File.copy
        // 的实际判定形态——两个独立打开句柄必须判同，绝不能只比较路径
        // 文本；此形态正是 Linux VM 事故中被垃圾身份破坏的分支）=====
        private static void TestSameHandleMatrix()
        {
            var dispatch = NewDispatch();
            var root = NewRoot();
            try
            {
                var a = Path.Combine(root, "a.bin");
                File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
                var t1 = OpenRead(dispatch, a);
                try
                {
                    CheckSame("相同句柄判同", dispatch, t1, t1, true);
                    var t2 = OpenRead(dispatch, a);
                    try
                    {
                        CheckSame("独立打开同文件判同（自复制判定面）",
                            dispatch, t1, t2, true);
                        CheckSame("参数交换序仍判同（对称性）",
                            dispatch, t2, t1, true);
                    }
                    finally { Release(dispatch, t2); }
                }
                finally { Release(dispatch, t1); }
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // ===== 四矩阵 ③：硬链接别名判同（不同路径访问同一文件）。fixture
        // 由测试侧系统调用提供：Linux link(2) / Windows CreateHardLinkW
        //（NTFS 原生硬链接，无需特权；失败受控跳过，不造通过断言）。
        // 同目录与跨子目录两种别名形态都判同——「跨目录 alias 读同实
        // 体」是自复制拒绝（§4.5.7「经不同路径访问同一个文件」）的直
        // 接矩阵成员 =====
        private static void TestHardlinkAliasSameIdentity()
        {
            var dispatch = NewDispatch();
            var root = NewRoot();
            try
            {
                var target = Path.Combine(root, "target.bin");
                var alias = Path.Combine(root, "alias.bin");
                File.WriteAllBytes(target, new byte[] { 5, 6, 7, 8 });
                if (!TryCreateHardLink(alias, target, out var fixtureError))
                {
                    CaseAssertions.RecordSkip("  SKIP TestHardlinkAliasSameIdentity"
                        + "：硬链接 fixture 不可用（"
                        + (fixtureError ?? "无错误消息") + "）");
                    return;
                }
                // tt 取得后全程由唯一的外层 finally 保底释放（恰好一次）；
                // at/st 各自成功打开后独立 finally；任何中间失败分支都不
                // 手动释放 tt，杜绝未来双重释放
                var tt = OpenRead(dispatch, target);
                try
                {
                    // 同目录别名段
                    var at = OpenRead(dispatch, alias);
                    try
                    {
                        CheckSame("硬链接别名（同目录不同名）判同",
                            dispatch, tt, at, true);
                    }
                    finally { Release(dispatch, at); }

                    // 跨子目录别名段：另一目录条目读同一实体。同目录建
                    // 链已成功而二级建链失败属环境异常，记 FAIL 不静默
                    // 跳过；失败分支不手动释放 tt（外层统一收口）
                    var sub = Path.Combine(root, "sub");
                    Directory.CreateDirectory(sub);
                    var subAlias = Path.Combine(sub, "alias2.bin");
                    if (TryCreateHardLink(subAlias, target,
                            out var subError))
                    {
                        var st = OpenRead(dispatch, subAlias);
                        try
                        {
                            CheckSame("跨目录别名（子目录条目读同实体）判同",
                                dispatch, tt, st, true);
                        }
                        finally { Release(dispatch, st); }
                    }
                    else
                    {
                        CaseAssertions.CheckTrue("跨子目录硬链接 fixture 成功",
                            false, subError ?? "fixture 失败但未返回错误消息");
                    }
                }
                finally { Release(dispatch, tt); }
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // ===== 四矩阵 ④：不同文件判异（Overwrite 正常复制的前提）。
        // 同目录两文件必同设备，判异只可能来自 inode/文件索引不同——
        // 锁住「真读到身份」而不是两边都读到恒定垃圾恰好相等 =====
        private static void TestDifferentFilesDiffer()
        {
            var dispatch = NewDispatch();
            var root = NewRoot();
            try
            {
                var a = Path.Combine(root, "a.bin");
                var b = Path.Combine(root, "b.bin");
                File.WriteAllBytes(a, new byte[] { 1 });
                File.WriteAllBytes(b, new byte[] { 2 });
                var ta = OpenRead(dispatch, a);
                var tb = OpenRead(dispatch, b);
                try
                {
                    CheckSame("不同文件判异", dispatch, ta, tb, false);
                    CheckSame("参数交换序仍判异（对称性）",
                        dispatch, tb, ta, false);
                }
                finally
                {
                    Release(dispatch, ta);
                    Release(dispatch, tb);
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // ===== 身份读取失败不得当「不同」（防御不变量，§4.5.7）：无效
        // token（未登记句柄）与已释放 token 都必须以宿主异常浮出（上层
        // fsSameIdentity 对 rc!=0 一律抛 FileSystemException——不存在
        // 「查询失败 → same=false → 继续截断」的静默路径）。此断言锁的
        // 是失败面分类：身份失败若被吞成「不同」，这里会先以异常形态
        // 暴露 =====
        private static void TestIdentityFailureNotDifferent()
        {
            var dispatch = NewDispatch();
            var root = NewRoot();
            try
            {
                var a = Path.Combine(root, "a.bin");
                File.WriteAllBytes(a, new byte[] { 1 });
                var t = OpenRead(dispatch, a);
                try
                {
                    // ① 未登记 token：必须异常（无效句柄），不能静默 same=0
                    var threwInvalid = false;
                    try
                    {
                        _ = SameFile(dispatch, 987654321L, t);
                    }
                    catch (VmException)
                    {
                        threwInvalid = true;
                    }
                    CaseAssertions.CheckTrue("无效 token 报异常不放行",
                        threwInvalid, "未抛 VmException");

                    // ② 已释放 token 再查：同样必须异常（native_rc release
                    // 已析构关闭底层流并撤销登记）
                    var rt = OpenRead(dispatch, a);
                    Release(dispatch, rt);
                    var releasedThrew = false;
                    try
                    {
                        _ = SameFile(dispatch, rt, t);
                    }
                    catch (VmException)
                    {
                        releasedThrew = true;
                    }
                    CaseAssertions.CheckTrue("已释放 token 再查报异常不放行",
                        releasedThrew, "未抛 VmException");
                }
                finally { Release(dispatch, t); }
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // ===== 双平台接收布局机械锁定：托管镜像的 Marshal.SizeOf/
        // OffsetOf 与 C 探针真值逐值对照。真值来源（独立于被测 Fs 原语
        // 的编译期头文件事实）：
        //   Windows：SDK 10.0.26100.0 头 + clang -target
        //     x86_64-pc-windows-msvc 探针 → win_abi_truth.log
        //     （sizeof=52、VolumeSerialNumber@28、FileIndexHigh@44、
        //     FileIndexLow@48——曾因 FILETIME 用 long 充当错位到
        //     32/48/52，FileIndexLow 读到未初始化尾隙）
        //   Linux：WSL glibc 2.39 + gcc -m64 实测 <sys/stat.h> →
        //     linux_stat_truth.log（sizeof=144、st_dev@0、st_ino@8）
        // 托管布局跨平台确定，断言在两平台无条件执行 =====
        private static void TestAbiLayoutAssertions()
        {
            // —— Windows：FsFileTime / FsByHandleInfo ——
            CaseAssertions.CheckTrue("FsFileTime 大小=8",
                Marshal.SizeOf<VmDispatch.FsFileTime>() == 8,
                "got=" + Marshal.SizeOf<VmDispatch.FsFileTime>());
            CheckOffset<VmDispatch.FsFileTime>("LowDateTime", 0);
            CheckOffset<VmDispatch.FsFileTime>("HighDateTime", 4);
            CaseAssertions.CheckTrue("FsByHandleInfo 大小=52",
                Marshal.SizeOf<VmDispatch.FsByHandleInfo>() == 52,
                "got=" + Marshal.SizeOf<VmDispatch.FsByHandleInfo>());
            CheckOffset<VmDispatch.FsByHandleInfo>("FileAttributes", 0);
            CheckOffset<VmDispatch.FsByHandleInfo>("CreationTime", 4);
            CheckOffset<VmDispatch.FsByHandleInfo>("LastAccessTime", 12);
            CheckOffset<VmDispatch.FsByHandleInfo>("LastWriteTime", 20);
            CheckOffset<VmDispatch.FsByHandleInfo>("VolumeSerialNumber", 28);
            CheckOffset<VmDispatch.FsByHandleInfo>("FileSizeHigh", 32);
            CheckOffset<VmDispatch.FsByHandleInfo>("FileSizeLow", 36);
            CheckOffset<VmDispatch.FsByHandleInfo>("NumberOfLinks", 40);
            CheckOffset<VmDispatch.FsByHandleInfo>("FileIndexHigh", 44);
            CheckOffset<VmDispatch.FsByHandleInfo>("FileIndexLow", 48);

            // —— Linux：FsLinuxX64Stat（glibc x86_64 struct stat）——
            CaseAssertions.CheckTrue("FsLinuxX64Stat 大小=144",
                Marshal.SizeOf<VmDispatch.FsLinuxX64Stat>() == 144,
                "got=" + Marshal.SizeOf<VmDispatch.FsLinuxX64Stat>());
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Dev", 0);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Ino", 8);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Nlink", 16);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Mode", 24);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Uid", 28);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Gid", 32);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Pad0", 36);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Rdev", 40);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Size", 48);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Blksize", 56);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Blocks", 64);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("AtimSec", 72);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("AtimNsec", 80);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("MtimSec", 88);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("MtimNsec", 96);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("CtimSec", 104);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("CtimNsec", 112);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Reserved0", 120);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Reserved1", 128);
            CheckOffset<VmDispatch.FsLinuxX64Stat>("Reserved2", 136);
            CaseAssertions.CheckTrue("FsLinuxX64Statx 大小=256",
                Marshal.SizeOf<VmDispatch.FsLinuxX64Statx>() == 256);
            CheckOffset<VmDispatch.FsLinuxX64Statx>("Mask", 0);
            CheckOffset<VmDispatch.FsLinuxX64Statx>("Ino", 32);
            CheckOffset<VmDispatch.FsLinuxX64Statx>("BirthSec", 80);
            CheckOffset<VmDispatch.FsLinuxX64Statx>("BirthNsec", 88);
            CheckOffset<VmDispatch.FsLinuxX64Statx>("DevMajor", 136);
            CheckOffset<VmDispatch.FsLinuxX64Statx>("DevMinor", 140);
        }

        private static void CheckOffset<T>(string field, int want)
            where T : struct
        {
            var got = (int)Marshal.OffsetOf<T>(field);
            CaseAssertions.CheckTrue(typeof(T).Name + "." + field
                + " 偏移=" + want, got == want, "got=" + got);
        }

        // 硬链接 fixture：Linux link(2)（glibc/musl 同名导出），
        // Windows CreateHardLinkW（NTFS 原生）。返回 false = fixture 不
        // 可用（上层受控跳过），不伪造成功
        private static bool TryCreateHardLink(string linkPath,
            string targetPath, out string? error)
        {
            error = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!CreateHardLinkW(linkPath, targetPath, IntPtr.Zero))
                    {
                        error = "CreateHardLinkW 失败 Win32Error="
                            + Marshal.GetLastWin32Error();
                        return false;
                    }
                    return true;
                }
                if (OperatingSystem.IsLinux())
                {
                    var rc = link(targetPath, linkPath);
                    if (rc != 0)
                    {
                        error = "link(2) 失败 errno="
                            + Marshal.GetLastWin32Error();
                        return false;
                    }
                    return true;
                }
                error = "非 Windows/Linux 宿主，无 fixture";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName,
            string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int link(string oldpath, string newpath);
    }
}
