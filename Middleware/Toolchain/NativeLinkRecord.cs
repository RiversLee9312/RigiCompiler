using System.Text.RegularExpressions;
using RigiCompiler.Middleware.Cache;

namespace RigiCompiler.Middleware.Toolchain;

// 链接输入仅作本次记录，绝不进入优化对象 key；每次请求必须真实 relink。
internal static class NativeLinkRecord
{
    internal static void Plan(string clang, IReadOnlyList<string> args)
    {
        if (!PerformanceMetrics.Enabled) return;
        PerformanceMetrics.Event("link-input", "native.link-flags", "observed", string.Join("\n", args));
        Fingerprint(clang);
        foreach (var arg in args) if (File.Exists(arg)) Fingerprint(arg);
        try
        {
            if (ExternalProcess.Run(clang, args.Concat(new[] { "-###" }).ToArray(), out _, out var semantics,
                closeStdin: true, parentPhase: "native.link-plan") != 0) return;
            PerformanceMetrics.Event("link-input", "native.link-driver", "observed", semantics);
            // clang 的真实驱动命令含实际 lld 与 CRT 路径；路径空格由引号保留。
            foreach (Match match in Regex.Matches(semantics, "\"([^\"\r\n]+)\""))
                if (File.Exists(match.Groups[1].Value)) Fingerprint(match.Groups[1].Value);
        }
        catch (InvalidOperationException) { PerformanceMetrics.Event("link-input", "native.link-driver", "unknown"); }
    }
    internal static void Actual(string trace)
    {
        if (!PerformanceMetrics.Enabled) return;
        foreach (var line in trace.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = line.Trim();
            if (!File.Exists(path))
            {
                // lld --trace 的 archive(member) 行保存完整 archive 身份。
                var member = Regex.Match(path, @"^(.*?)\([^()]*\)$");
                if (member.Success) path = member.Groups[1].Value;
            }
            if (File.Exists(path)) Fingerprint(path);
        }
    }
    private static void Fingerprint(string path)
    {
        try { PerformanceMetrics.Event("link-input", "native.link-file", "observed",
            Path.GetFullPath(path) + " SHA256=" + ArtifactCache.HashFile(path)); }
        catch (Exception ex) when (ArtifactCache.IsCacheIo(ex))
        { PerformanceMetrics.Event("link-input", "native.link-file", "unknown", Path.GetFullPath(path)); }
    }
}
