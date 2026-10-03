using System.Text.RegularExpressions;
using RigiCompiler.Middleware.Cache;

namespace RigiCompiler.Middleware.Toolchain;

// 无法证明 codegen dependency closure 时绕缓存；诊断与实际编译仍交 clang。
internal static class ToolchainIdentity
{
    internal static string? TryCapture(string clang, IReadOnlyList<string> args,
        string directory, IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            // Linux 用动态加载器列出完整依赖；Windows 尚无已验证的 closure 探测，安全绕过。
            if (!OperatingSystem.IsLinux() || environment.TryGetValue("LD_AUDIT", out var audit)
                && !string.IsNullOrWhiteSpace(audit)) return null;
            var resolved = File.ResolveLinkTarget(clang, true)?.FullName ?? Path.GetFullPath(clang);
            var dependencies = new SortedSet<string>(StringComparer.Ordinal) { resolved };
            var ldd = ExternalProcess.Run("ldd", new[] { resolved }, out var libraries, out _,
                environment: environment, replaceEnvironment: true);
            if (ldd != 0 || libraries.Contains("not found", StringComparison.Ordinal)) return null;
            foreach (Match match in Regex.Matches(libraries, @"(?:=> )?(/[^\r\n]+?) \("))
                dependencies.Add(match.Groups[1].Value.Trim());
            if (dependencies.Count < 2) return null;
            var probeArgs = args.Concat(new[] { "-###" }).ToArray();
            if (ExternalProcess.Run(clang, probeArgs, out _, out var semantics, directory,
                environment, replaceEnvironment: true) != 0 || !semantics.Contains("\"-cc1\"", StringComparison.Ordinal)) return null;
            // driver config 可以引入任意插件/外部 profile；未知附加输入安全绕缓存。
            if (new[] { "-load", "-plugin", "-fpass-plugin", "-profile", "-fprofile", "-fmodule", "-include-pch" }
                .Any(option => semantics.Contains("\"" + option, StringComparison.Ordinal))) return null;
            foreach (var line in semantics.Split('\n'))
                if (line.StartsWith("Configuration file: ", StringComparison.Ordinal))
                    dependencies.Add(line["Configuration file: ".Length..].Trim());
            return ArtifactCache.Identity(semantics.Replace(directory, "<runtime-source>", StringComparison.Ordinal),
                string.Join("\n", dependencies.Select(p => p + ":" + ArtifactCache.HashFile(p))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception) { return null; }
    }
}
