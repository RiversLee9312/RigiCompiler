using System.Text;
using System.Text.RegularExpressions;

namespace RigiCompiler.Modules;

internal sealed record ModuleSource(string RelativePath, byte[] Bytes)
{
    internal SourceInput Input()
    {
        using var reader = new StreamReader(new MemoryStream(Bytes, false), Encoding.UTF8, true);
        return new(reader.ReadToEnd(), "source/" + RelativePath);
    }
}

/// <summary>selector 顺序优先，每个 selector 内相对路径 ordinal 排序并去重。</summary>
internal static class ModuleSources
{
    internal static IReadOnlyList<ModuleSource> Read(string moduleRoot, IReadOnlyList<string> selectors)
    {
        var root = ModulePaths.Inside(moduleRoot, "source");
        if (!Directory.Exists(root)) return [];
        var paths = new List<string>();
        void Visit(string relative)
        {
            var directory = relative.Length == 0 ? root : ModulePaths.Inside(root, relative);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetRelativePath(root, entry).Replace('\\', '/');
                var checkedPath = ModulePaths.Inside(root, name);
                if (Directory.Exists(checkedPath)) Visit(name); else paths.Add(name);
            }
        }
        Visit("");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModuleSource>();
        foreach (var selector in selectors)
        {
            ModulePaths.ValidateRelative(selector, true);
            var pattern = "\\A" + Regex.Escape(selector).Replace("\\*\\*/", "(?:.*/)?")
                .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "\\z";
            var matcher = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            foreach (var path in paths.Order(StringComparer.Ordinal))
                if (matcher.IsMatch(path) && seen.Add(path))
                    result.Add(new(path, File.ReadAllBytes(ModulePaths.Inside(root, path))));
        }
        return result;
    }
}
