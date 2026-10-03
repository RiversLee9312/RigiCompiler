namespace RigiCompiler.Modules;

internal static class ModuleFiles
{
    internal static async Task<FileStream> LockAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = Environment.TickCount64 + 600_000;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33 && Environment.TickCount64 < deadline)
            { await Task.Delay(25); }
        }
    }
    // 每层先检查真实链接元数据；目录遍历不依赖 glob 的隐式 symlink 行为。
    internal static IEnumerable<(string Relative, string Path)> Tree(string root, string relative)
    {
        var path = ModulePaths.Inside(root, relative);
        if (File.Exists(path)) { yield return (relative, path); yield break; }
        if (!Directory.Exists(path)) throw new ModuleConfigurationException("模块文件或目录不存在：" + relative);
        foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
        {
            var name = relative + "/" + Path.GetFileName(child);
            foreach (var file in Tree(root, name)) yield return file;
        }
    }

    // 以跨平台大小写规则预检文件、目录以及父路径，禁止同一 product/ZIP 覆盖。
    internal sealed class Names
    {
        private readonly Dictionary<string, (string Name, bool Directory)> names = new(StringComparer.OrdinalIgnoreCase);
        internal void Add(string name, bool directory = false)
        {
            ModulePaths.ValidateRelative(name);
            var parts = name.Split('/');
            for (var i = 1; i <= parts.Length; i++)
            {
                var prefix = string.Join('/', parts.Take(i));
                var isDirectory = i != parts.Length || directory;
                if (names.TryGetValue(prefix, out var previous))
                {
                    if (previous.Name != prefix || previous.Directory != isDirectory || i == parts.Length && !directory)
                        throw new ModuleConfigurationException("模块路径大小写/文件目录冲突：" + name);
                }
                else names.Add(prefix, (prefix, isDirectory));
            }
        }
    }
}
