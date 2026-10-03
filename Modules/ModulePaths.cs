namespace RigiCompiler.Modules;

public static class ModulePaths
{
    public static void ValidateRelative(string path, bool glob = false)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\')
            || path.Contains(':') || path.IndexOf('\0') >= 0
            || path.Split('/').Any(part => part is "" or "." or "..")
            || !glob && path.IndexOfAny(['*', '?', '[', ']']) >= 0)
            throw new ModuleConfigurationException($"非法或越界相对路径 '{path}'");
    }

    public static string Inside(string root, string relative)
    {
        ValidateRelative(relative);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ModuleConfigurationException($"路径逃离模块根 '{relative}'");
        // 对已存在的每一段查链接，不能通过 symlink 绕过词法边界。
        var current = fullRoot;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            // LinkTarget 查链接本身，包括 target 不存在的悬空链接。
            if (new FileInfo(current).LinkTarget != null || new DirectoryInfo(current).LinkTarget != null)
                throw new ModuleConfigurationException($"模块路径不允许符号链接 '{relative}'");
        }
        return path;
    }
}
