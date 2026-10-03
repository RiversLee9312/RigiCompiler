using System.Diagnostics;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Cache;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Middleware.Emit;

// 身份来自已经实际映射的库，禁止猜 exe 旁同名文件或只读版本字符串。
internal sealed class LoadedLibraryIdentity
{
    private readonly string path;
    private readonly string digest;
    private readonly string? inode;
    private LoadedLibraryIdentity(string path, string digest, string? inode)
    { this.path = path; this.digest = digest; this.inode = inode; }
    internal static LoadedLibraryIdentity? Capture()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var paths = process.Modules.Cast<ProcessModule>().Select(m => m.FileName)
                .Where(p => Path.GetFileName(p).StartsWith("libLLVM", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (paths.Length != 1 || !File.Exists(paths[0])) return null;
            return CaptureFile(paths[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
    internal static LoadedLibraryIdentity? CaptureFile(string path, string? expectedDigest = null)
    {
        try
        {
            var inode = MappedInode(path);
            if (OperatingSystem.IsLinux() && inode == null) return null;
            var before = DiskInode(path);
            if (OperatingSystem.IsLinux() && before != inode) return null;
            var digest = ArtifactCache.HashFile(path);
            if (expectedDigest != null && expectedDigest != digest) return null;
            if (OperatingSystem.IsLinux() && (DiskInode(path) != inode || MappedInode(path) != inode)) return null;
            return new LoadedLibraryIdentity(path, digest, inode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
    internal string? Verify()
    {
        try
        {
            // 首次加载的身份不变；旧映射绝不能搭配磁盘上的新库内容。
            if (OperatingSystem.IsLinux() && (MappedInode(path) != inode || DiskInode(path) != inode)) return null;
            return ArtifactCache.HashFile(path) == digest ? digest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
    private static string? MappedInode(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 6) continue;
            var file = string.Join(" ", columns.Skip(5));
            if (file == path + " (deleted)") return null;
            if (file == path)
            {
                var device = columns[3].Split(':');
                var major = Convert.ToUInt64(device[0], 16);
                var minor = Convert.ToUInt64(device[1], 16);
                // Linux makedev：/proc 的 major:minor 与 stat st_dev 使用相同设备身份。
                var encoded = ((major & 0xfff) << 8) | (minor & 0xff)
                    | ((major & ~0xfffUL) << 32) | ((minor & ~0xffUL) << 12);
                mapped.Add(encoded.ToString("x") + ":" + columns[4]);
            }
        }
        return mapped.Count == 1 ? mapped.Single() : null;
    }
    private static string? DiskInode(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        return ExternalProcess.Run("stat", new[] { "-L", "-c", "%D:%i", path }, out var value,
            out _, closeStdin: true) == 0
            ? NormalizeDiskIdentity(value.Trim()) : null;
    }
    private static string NormalizeDiskIdentity(string value)
    {
        var fields = value.Split(':');
        return Convert.ToUInt64(fields[0], 16).ToString("x") + ":" + fields[1];
    }
}
