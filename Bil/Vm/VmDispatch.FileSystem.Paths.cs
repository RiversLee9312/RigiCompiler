using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // FileSystem.Paths 职责；与主文件共享同一类型、字段及生命周期。

        // fs_realpath：FullName/GetFullPath 仅词法绝对化，不能解析中间
        // 目录链接，也可能错误地在链接之前消解「..」。先以一次 cwd 固定
        // 相对路径的调用基准（只组合、不整理分量），再交给系统解析完整
        // 路径：Windows 已打开句柄最终路径 / Linux libc realpath。
        // 成功时 out 为严格 UTF-8，meta[0..4) 为所需字节数；容量不足仍
        // 返回正哨兵 2，错误用负归一码，不能将循环/权限伪装 NotFound。
        internal VmValue FsRealpath(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_realpath", args, 0).Value;
            var outSpan = RequireFsSpan("fs_realpath", args, 1);
            var metaSpan = RequireFsSpan("fs_realpath", args, 2);
            try
            {
                if (path.Length == 0 || path.Contains('\0'))
                {
                    return new VmI32(-22);
                }
                var absolute = Path.IsPathFullyQualified(path)
                    ? path : Path.Combine(Environment.CurrentDirectory, path);
                var rc = FsResolveRealpath(absolute, out var resolved);
                if (rc != 0) { return new VmI32(rc); }
                byte[] bytes;
                try
                {
                    bytes = new System.Text.UTF8Encoding(false, true)
                        .GetBytes(resolved!);
                }
                catch (System.Text.EncoderFallbackException)
                {
                    return new VmI32(-1001); // 无法无损表达的名称
                }
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
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException or NotSupportedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // 返回 0/负归一码；系统调用期间由 SafeFileHandle 编组保活句柄。
        // GetFinalPathNameByHandle 的 DOS 卷名结果携内部 \\?\ 前缀，
        // 还原为用户可表达的本地盘符/UNC 路径（与 native fs.c 同形态）。
        private static int FsResolveRealpath(string absolute,
            out string? resolved)
        {
            resolved = null;
            if (OperatingSystem.IsWindows())
            {
                // 与 native fs.c 一致：长的完整盘符/UNC 路径在内部加
                // Win32 扩展前缀，绝不把该前缀作为结果路径返回。
                var openPath = absolute;
                if (absolute.Length >= 248)
                {
                    var normalized = absolute.Replace('/', '\\');
                    openPath = normalized.StartsWith(@"\\", StringComparison.Ordinal)
                        ? @"\\?\UNC\" + normalized.Substring(2)
                        : @"\\?\" + normalized;
                }
                using var handle = CreateFileW(openPath,
                    FsWinFileReadAttributes, FsWinShareReadWriteDelete,
                    IntPtr.Zero, 3 /* OPEN_EXISTING */,
                    0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS：目录 */,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    return -MapFsWin32(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                // 返回值是所需字符数（不足时含 NUL）；句柄始终保持打开。
                var count = GetFinalPathNameByHandleW(handle, IntPtr.Zero, 0, 0);
                if (count == 0)
                {
                    return -MapFsWin32(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                if (count >= 32768) { return -36; }
                while (true)
                {
                    var buffer = new char[checked((int)count + 1)];
                    var written = GetFinalPathNameByHandleW(handle, buffer,
                        (uint)buffer.Length, 0);
                    if (written == 0)
                    {
                        return -MapFsWin32(System.Runtime.InteropServices.Marshal
                            .GetLastWin32Error());
                    }
                    if (written >= buffer.Length)
                    {
                        if (written >= 32768) { return -36; }
                        count = written;
                        continue;
                    }
                    var result = new string(buffer, 0, (int)written);
                    if (result.StartsWith(@"\\?\UNC\",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = @"\\" + result.Substring(8);
                    }
                    else if (result.StartsWith(@"\\?\",
                        StringComparison.Ordinal))
                    {
                        resolved = result.Substring(4);
                    }
                    else
                    {
                        resolved = result;
                    }
                    return 0;
                }
            }
            if (!OperatingSystem.IsLinux()) { return -38; }
            try
            {
                // libc 分配的 realpath(path,NULL) 只能由 libc free 释放，
                // 绝不交给 VM/native 的追踪分配器；不使用 PtrToStringUTF8
                // 的替换解码，非法 UTF-8 必须归名称编码错误。
                var input = new System.Text.UTF8Encoding(false, true)
                    .GetBytes(absolute + "\0");
                var result = FsLibcRealpath(input, IntPtr.Zero);
                if (result == IntPtr.Zero)
                {
                    return -MapFsErrno(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                try
                {
                    var length = FsLibcStrlen(result);
                    if (length > int.MaxValue - 1) { return -36; }
                    var bytes = new byte[(int)length];
                    System.Runtime.InteropServices.Marshal.Copy(result, bytes,
                        0, bytes.Length);
                    try
                    {
                        resolved = new System.Text.UTF8Encoding(false, true)
                            .GetString(bytes);
                        return 0;
                    }
                    catch (System.Text.DecoderFallbackException)
                    {
                        return -1001;
                    }
                }
                finally { FsLibcFree(result); }
            }
            catch (System.Text.EncoderFallbackException) { return -1001; }
            catch (DllNotFoundException) { return -38; }
            catch (EntryPointNotFoundException) { return -38; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            IntPtr buffer, uint length, uint flags);

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            [System.Runtime.InteropServices.Out] char[] buffer,
            uint length, uint flags);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr FsLibcRealpath(byte[] path, IntPtr resolved);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "strlen")]
        private static extern UIntPtr FsLibcStrlen(IntPtr value);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "free")]
        private static extern void FsLibcFree(IntPtr value);

        // fs_mkdir：只创建末级，父级须存在，已存在报错（§4.5.5）；权限
        // 位 Windows 忽略（正常继承的安全描述符），Unix 走 .NET 默认
        //（0777 & umask，与 native mkdir(mode & 0777) 同宿主规则）
        internal VmValue FsMkdir(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_mkdir", args, 0).Value;
            try
            {
                Directory.CreateDirectory(path);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // fs_rmdir：只删真实空目录，不跟随末段链接删目标（§4.5.5）
        internal VmValue FsRmdir(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_rmdir", args, 0).Value;
            try
            {
                Directory.Delete(path, recursive: false);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // Windows 目录链接须由 RemoveDirectoryW 删除条目，DeleteFileW
        // 只适于文件（含文件符号链接）。属性与 tag 只用于选择非递归调用，
        // 不把未知 reparse 标签擅自归 Link；系统删除调用负责最终成败。
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool FsGetFileAttributeTag(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            int infoClass, out FsFileAttributeTagInfo info, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "RemoveDirectoryW", SetLastError = true)]
        private static extern bool FsRemoveDirectory(string path);

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FsFileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        // fs_unlink：文件/链接删除（§4.5.5），真实目录报 IsDirectory。
        internal VmValue FsUnlink(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_unlink", args, 0).Value;
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if ((attrs & FileAttributes.ReparsePoint) == 0)
                        return new VmI32(-21);
                    if (OperatingSystem.IsWindows())
                    {
                        using var handle = CreateFileW(path,
                            FsWinFileReadAttributes, FsWinShareReadWriteDelete,
                            IntPtr.Zero, 3 /* OPEN_EXISTING */,
                            0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */,
                            IntPtr.Zero);
                        if (handle.IsInvalid)
                            return new VmI32(-MapFsWin32(System.Runtime.InteropServices
                                .Marshal.GetLastWin32Error()));
                        if (!FsGetFileAttributeTag(handle, 9 /* FileAttributeTagInfo */,
                            out var tag, 8))
                            return new VmI32(-MapFsWin32(System.Runtime.InteropServices
                                .Marshal.GetLastWin32Error()));
                        // MOUNT_POINT=junction，SYMLINK=目录符号链接。
                        if (tag.ReparseTag != 0xA0000003
                            && tag.ReparseTag != 0xA000000C)
                            return new VmI32(-21);
                        // 查询句柄先关闭以免影响按路径删除；删除调用不跟随末段。
                        handle.Dispose();
                        if (!FsRemoveDirectory(path))
                            return new VmI32(-MapFsWin32(System.Runtime.InteropServices
                                .Marshal.GetLastWin32Error()));
                        return new VmI32(0);
                    }
                }
                File.Delete(path);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        private static extern bool FsSetFileRenameInfoEx(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            int infoClass, byte[] info, uint size);

        // 只为 Windows 普通文件源 Replace→末段 MOUNT_POINT 分流：路径探测
        // 不锁定目标；最终仍由一次系统 rename 裁决，不能预删后再移动。
        private static int? FsTryReplaceWindowsJunction(string src, string dst)
        {
            static string NativePath(string path)
            {
                // 与 native fs.c 一致：仅绝对长盘符/UNC 路径加内部前缀。
                if (path.Length < 248 || !Path.IsPathFullyQualified(path))
                    return path;
                var normalized = path.Replace('/', '\\');
                return normalized.StartsWith(@"\\?\", StringComparison.Ordinal)
                    ? normalized
                    : normalized.StartsWith(@"\\", StringComparison.Ordinal)
                        ? @"\\?\UNC\" + normalized.Substring(2)
                        : @"\\?\" + normalized;
            }

            var targetPath = NativePath(dst);
            using (var target = CreateFileW(targetPath, FsWinFileReadAttributes,
                FsWinShareReadWriteDelete, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */,
                IntPtr.Zero))
            {
                if (target.IsInvalid)
                    return -MapFsWin32(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                if (!FsGetFileAttributeTag(target, 9 /* FileAttributeTagInfo */,
                    out var targetTag, 8))
                    return -MapFsWin32(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                if ((targetTag.FileAttributes & 0x410 /* DIRECTORY|REPARSE */)
                    != 0x410)
                    return -38; // 查询后换型/标签不确定，不能借该分支覆盖。
                if (targetTag.ReparseTag == 0xA000000C /* SYMLINK */)
                    return null; // 原文件链接路径保持原有 File.Move 行为。
                if (targetTag.ReparseTag != 0xA0000003 /* MOUNT_POINT */)
                    return -38; // 未知目录 reparse 标签不可猜为 Link。
            }

            using var source = CreateFileW(NativePath(src),
                0x00010000 | FsWinFileReadAttributes /* DELETE|READ_ATTRIBUTES */,
                FsWinShareReadWriteDelete, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x00200000 /* OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (source.IsInvalid)
                return -MapFsWin32(System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error());
            if (!FsGetFileAttributeTag(source, 9 /* FileAttributeTagInfo */,
                out var sourceTag, 8))
                return -MapFsWin32(System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error());
            if ((sourceTag.FileAttributes & 0x410 /* DIRECTORY|REPARSE */) != 0)
                return null; // 链接源/目录源仍由旧入口按原类型处理。

            byte[] name;
            try
            {
                name = new System.Text.UnicodeEncoding(false, false, true)
                    .GetBytes(NativePath(dst));
            }
            catch (System.Text.EncoderFallbackException)
            {
                return -1001; // 名称编码不可无损表达。
            }
            // FILE_RENAME_INFO_EX：x64 字段偏移 0/8/16/20，x86 为
            // 0/4/8/12；尾部含 NUL，FileNameLength 只记有效 UTF-16 字节。
            var fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
            if (name.Length > int.MaxValue - fileNameOffset - sizeof(char))
                return -36;
            var info = new byte[fileNameOffset + name.Length + sizeof(char)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                info.AsSpan(0, 4), 3 /* REPLACE_IF_EXISTS|POSIX_SEMANTICS */);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                info.AsSpan(fileNameOffset - 4, 4), name.Length);
            name.CopyTo(info, fileNameOffset);
            bool moved;
            int err;
            try
            {
                moved = FsSetFileRenameInfoEx(source, 22 /* FileRenameInfoEx */,
                    info, (uint)info.Length);
                err = moved ? 0 : System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error(); // 必须在关闭句柄/任何补查之前捕获。
            }
            catch (DllNotFoundException) { return -38; }
            catch (EntryPointNotFoundException) { return -38; }
            if (moved) return 0;
            if (err is 1 or 50 or 87 /* INVALID_FUNCTION / NOT_SUPPORTED / INVALID_PARAMETER */)
                return -38;
            if (err == FsWinErrorAccessDenied)
            {
                try
                {
                    var attrs = File.GetAttributes(dst);
                    if ((attrs & FileAttributes.Directory) != 0
                        && (attrs & FileAttributes.ReparsePoint) == 0)
                        return -21; // 竞态换成真实目录：系统拒绝，仍归 IsDirectory。
                }
                catch (FileNotFoundException) { /* 保留原系统错误。 */ }
                catch (DirectoryNotFoundException) { /* 保留原系统错误。 */ }
            }
            return -MapFsWin32(err);
        }

        // fs_rename：replace = 0 → NoReplace（Windows MoveFileEx 不带
        // REPLACE_EXISTING = 系统不替换保证；Unix/Linux 在路径捕获后、
        // 按源类型分支之前统一走 renameat2(RENAME_NOREPLACE) 系统保证
        //——文件、真实目录、符号链接/断链条目（末段不跟随）一视同仁，
        // 不用 exists + rename 模拟（§4.5.7）；.NET File.Move/
        // Directory.Move 的 Unix 实现是 lstat 前检 + rename（BCL 源码
        // 自注 checks are not atomic），不能当保证）；1 → Replace（覆
        // 盖仅限文件/链接条目，目录目标报错，§4.5.7）。目录移动两模式
        // 都要求目标不存在（不允许目录覆盖或合并）
        internal VmValue FsRename(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 3 || args[2] is not VmI32 replace)
            {
                throw new VmException("fs_rename 需要 (String, String, i32)");
            }
            var src = RequireFsString("fs_rename", args, 0).Value;
            var dst = RequireFsString("fs_rename", args, 1).Value;
            try
            {
                // Unix NoReplace：不查询源类型，直接交系统原子裁决——
                // 不 lstat/exists 锁定目标。两路径单次系统调用同一
                // AT_FDCWD 基准解析（相对路径同 cwd，与 native 同口径）。
                // 非 Linux Unix（macOS 等）无统一不替换原语：受控
                // Unsupported，不以近似模拟承担契约（缺库/缺入口/内核
                // 或文件系统不支持由 RenameNoReplaceLinux 同口径归一）
                if (replace.Value == 0 && !OperatingSystem.IsWindows())
                {
                    if (!OperatingSystem.IsLinux())
                    {
                        return new VmI32(-FsErrnoEnosys);
                    }
                    return new VmI32(RenameNoReplaceLinux(src, dst));
                }
                var srcAttrs = File.GetAttributes(src);
                var sourceIsDirectory = (srcAttrs & FileAttributes.Directory) != 0
                    && (srcAttrs & FileAttributes.ReparsePoint) == 0;
                if (replace.Value != 0 && !OperatingSystem.IsWindows())
                {
                    if (!OperatingSystem.IsLinux())
                    {
                        return new VmI32(-FsErrnoEnosys);
                    }
                    if (sourceIsDirectory)
                    {
                        // 目录源 Replace 仍须原子阻止目标替换：Directory.Move
                        // 在 Unix 先检查再 rename，不能提供系统保证。
                        var rc = RenameNoReplaceLinux(src, dst);
                        if (rc == -17 && Directory.Exists(dst))
                        {
                            return new VmI32(-21);
                        }
                        return new VmI32(rc);
                    }
                    return new VmI32(RenameReplaceFileLinux(src, dst));
                }
                if (sourceIsDirectory)
                {
                    Directory.Move(src, dst); // Windows 既有路径
                    return new VmI32(0);
                }
                if (replace.Value != 0 && OperatingSystem.IsWindows())
                {
                    // 不存在目标沿用 File.Move；只检查末段目录 reparse
                    // 候选，避免改变普通文件、文件链接与 NoReplace 行为。
                    FileAttributes dstAttrs;
                    try { dstAttrs = File.GetAttributes(dst); }
                    catch (FileNotFoundException) { dstAttrs = 0; }
                    catch (DirectoryNotFoundException) { dstAttrs = 0; }
                    if ((dstAttrs & (FileAttributes.Directory
                        | FileAttributes.ReparsePoint))
                        == (FileAttributes.Directory | FileAttributes.ReparsePoint))
                    {
                        var special = FsTryReplaceWindowsJunction(src, dst);
                        if (special.HasValue) return new VmI32(special.Value);
                    }
                }
                File.Move(src, dst, overwrite: replace.Value != 0);
                return new VmI32(0);
            }
            catch (UnauthorizedAccessException)
            {
                // Windows Replace 遇目录目标（MoveFileEx 失败形态）：
                // 失败路径补查归 IsDirectory（native 同口径）
                if (replace.Value != 0 && Directory.Exists(dst))
                {
                    return new VmI32(-21);
                }
                return new VmI32(-13);
            }
            catch (Exception ex) when (ex is IOException
                or ArgumentException or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

    }
}
