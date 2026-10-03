using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RigiCompiler.Middleware.Cache;

// 缓存只保存完整目录；稳定 sibling 锁永不删除，避免 Linux inode 锁分裂。
internal static class ArtifactCache
{
    private static readonly ConcurrentDictionary<string, object> Flights = new(StringComparer.Ordinal);
    internal static string Root(string kind)
    {
        var configured = Environment.GetEnvironmentVariable("RIGI_CACHE_ROOT");
        return Path.Combine(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rigi")
            : Path.GetFullPath(configured), kind);
    }
    internal static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    internal static string Identity(params string[] fields)
    {
        // 长度前缀防止边界碰撞；顺序本身属于身份。
        var text = new StringBuilder();
        foreach (var field in fields) text.Append(field.Length).Append(':').Append(field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    internal static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    internal static bool IsCacheIo(Exception ex) => ex is IOException or UnauthorizedAccessException;

    // builder 的语义/工具链异常必须原样传播；仅缓存设施 I/O 可退化。
    // 产物在锁内复制到本次请求路径；链接不会持有可被修复替换的缓存文件。
    internal static bool TryMaterialize(string root, string key, string artifact,
        string destination, Action<string> builder, Action<string>? validate, out bool rebuilt)
    {
        Emit.LlvmHost.RequireNotOwned();
        rebuilt = false;
        var entry = Path.Combine(root, key);
        lock (Flights.GetOrAdd(entry, _ => new object()))
        {
            FileStream fileLock;
            try
            {
                PrivateDirectory(root);
                var locks = Path.Combine(root, ".locks");
                PrivateDirectory(locks);
                var lockPath = Path.Combine(locks, key + ".lock");
                var deadline = Environment.TickCount64 + 600_000;
                while (true)
                {
                    try { fileLock = new FileStream(lockPath, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None); break; }
                    catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33
                        && Environment.TickCount64 < deadline)
                    { Thread.Sleep(25); }
                }
            }
            catch (Exception ex) when (IsCacheIo(ex)) { return false; }
            using (fileLock)
            {
                var valid = false;
                try
                {
                    var manifest = Path.Combine(entry, "manifest");
                    var path = Path.Combine(entry, artifact);
                    if (File.Exists(manifest) && File.Exists(path))
                    {
                        var lines = File.ReadAllLines(manifest);
                        valid = lines.Length == 2 && lines[0] == key && lines[1] == HashFile(path);
                        if (valid && validate != null)
                        {
                            try { validate(path); }
                            catch (InvalidOperationException) { valid = false; }
                        }
                    }
                    if (valid) { File.Copy(path, destination, true); return true; }
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                }
                catch (Exception ex) when (IsCacheIo(ex)) { return false; }
                var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
                try { PrivateDirectory(staging); }
                catch (Exception ex) when (IsCacheIo(ex)) { return false; }
                try
                {
                    // 编译总写请求私有路径，缓存盘故障不会伪装成 clang/LLVM 诊断。
                    builder(destination);
                    validate?.Invoke(destination);
                    rebuilt = true;
                    try
                    {
                        var candidate = Path.Combine(staging, artifact);
                        File.Copy(destination, candidate);
                        File.WriteAllText(Path.Combine(staging, "manifest"), key + "\n" + HashFile(candidate) + "\n");
                        Directory.Move(staging, entry);
                    }
                    catch (Exception ex) when (IsCacheIo(ex)) { /* 请求已有完整产物，正常继续。 */ }
                    return true;
                }
                finally
                {
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                    catch (Exception ex) when (IsCacheIo(ex)) { }
                }
            }
        }
    }
}
