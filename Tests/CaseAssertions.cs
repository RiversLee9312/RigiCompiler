using System.Collections.Concurrent;

namespace RigiCompiler.Tests;

/// <summary>一个隔离请求的结构化断言证据；没有套件级 Reset/Summary 或全局计数生命周期。</summary>
public static class CaseAssertions
{
    internal sealed record Evidence(string Label, bool Passed, string Detail);
    internal sealed class Scope : IDisposable
    {
        private readonly Scope? previous;
        private readonly ConcurrentQueue<Evidence> evidence = new();
        internal Scope(Scope? previous) { this.previous = previous; }
        internal int AssertionCount => evidence.Count;
        internal int FailureCount => evidence.Count(item => !item.Passed);
        internal int PassedCount => evidence.Count(item => item.Passed);
        internal string? SkipReason { get; set; }
        internal void Record(Evidence value) => evidence.Enqueue(value);
        public void Dispose() => Active.Value = previous;
    }
    private static readonly AsyncLocal<Scope?> Active = new();
    internal static Scope Current => Active.Value ?? throw new InvalidOperationException("断言必须属于隔离 case scope");
    internal static Scope Begin()
    {
        if (!WorkerEnvironment.IsWorker) throw new InvalidOperationException("断言 scope 只能在隔离 worker 内建立");
        var scope = new Scope(Active.Value); Active.Value = scope; return scope;
    }
    // 私有词法/协议辅助函数同样进入当前请求，CallerMemberName 为真实动作证据。
    internal static void Record(bool passed, [System.Runtime.CompilerServices.CallerMemberName] string label = "", string detail = "") =>
        Current.Record(new(label, passed, detail));

    public static void RecordSkip(string reason) { Current.SkipReason = reason; Console.WriteLine(reason); }
    public static void Check(string label, string actual, string expected) =>
        CheckTrue(label, actual == expected, actual == expected ? "" : $"expected: {expected}\nactual: {actual}");
    public static void CheckTrue(string label, bool condition, string detail = "")
    {
        Record(condition, label, detail);
        Console.WriteLine(condition ? $"  [PASS] {label}" : $"  [FAIL] {label}{(detail.Length > 0 ? " => " + detail : "")}");
    }
    public static void CheckParseError(string label, Action parse, string expectedMessagePart)
    {
        try { parse(); CheckTrue(label, false, "应失败但成功了"); }
        catch (Exception exception) when (exception is ParserException or LexerException)
        { CheckTrue(label, exception.Message.Contains(expectedMessagePart), $"expected part: {expectedMessagePart}\nactual: {exception.Message}"); }
    }
    public static void CheckSemanticError(string label, DiagnosticBag bag, string expectedMessagePart) =>
        CheckTrue(label, bag.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Message.Contains(expectedMessagePart)),
            $"expected part: {expectedMessagePart}\nactual: [{string.Join("; ", bag.Diagnostics.Select(diagnostic => $"{diagnostic.Phase} {diagnostic.Severity}: {diagnostic.Message}"))}]");
}
