using System.Runtime.ExceptionServices;

namespace RigiCompiler;

internal sealed record SourceInput(string Text, string SourceName,
    bool CompilerLibrary = false, bool Intrinsics = false, Exception? ReadError = null);

internal sealed record ParsedSource(RootASTNode? Root, Exception? Error, Logger.Capture Logs)
{
    internal RootASTNode GetRoot()
    {
        if (Error != null) ExceptionDispatchInfo.Capture(Error).Throw();
        return Root!;
    }
}

// input 在启动前已固定；每文件独占 Lexer、Parser 和 Validator（Parser 自带）。
internal static class Frontend
{
    internal static RootASTNode Parse(SourceInput source)
    {
        if (source.ReadError != null) ExceptionDispatchInfo.Capture(source.ReadError).Throw();
        var tokens = PerformanceMetrics.Measure("frontend.lexer",
            () => new Lexer().Tokenize(source.Text, source.SourceName), source.SourceName);
        var root = PerformanceMetrics.Measure("frontend.parser",
            () => (RootASTNode)new Parser().Parse(tokens), source.SourceName);
        root.IsCompilerLibrary = source.CompilerLibrary;
        root.IsIntrinsicDeclarations = source.Intrinsics;
        return root;
    }

    internal static ParsedSource[] ParseFiles(IReadOnlyList<string> files)
    {
        var inputs = files.Select(path =>
        {
            try { return new SourceInput(File.ReadAllText(path), path); }
            catch (Exception error) { return new SourceInput("", path, ReadError: error); }
        }).ToArray();
        return ParseMany(inputs);
    }

    internal static ParsedSource[] ParseMany(IReadOnlyList<SourceInput> sources,
        CancellationToken token = default) => PerformanceMetrics.Measure("frontend.files", () =>
        CompilerJobs.Map(sources.Count, i =>
        {
            using var logs = Logger.CaptureJob();
            try { return new ParsedSource(Parse(sources[i]), null, logs); }
            catch (Exception error) { return new ParsedSource(null, error, logs); }
        }, small: sources.Sum(source => (long)source.Text.Length * 2) < 64 * 1024,
            cancellationToken: token, phase: "frontend.files"));

    internal static RootASTNode[] ParseRoots(IReadOnlyList<SourceInput> sources,
        CancellationToken token = default)
    {
        var parsed = ParseMany(sources, token);
        var roots = new RootASTNode[parsed.Length];
        for (var i = 0; i < parsed.Length; i++)
        {
            parsed[i].Logs.Replay();
            roots[i] = parsed[i].GetRoot();
        }
        return roots;
    }
}
