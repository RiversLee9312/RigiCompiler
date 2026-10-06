using System.Collections.Concurrent;
using System.Text.Json;

namespace RigiCompiler.Tests;

/// <summary>内部断言证据独立于框架行数；每个实际执行 ID 都保留结构化结果。</summary>
internal static class CaseResultJournal
{
    private static readonly ConcurrentDictionary<string, CaseOutcome> Outcomes = new(StringComparer.Ordinal);
    internal static IReadOnlyCollection<string> CaseIds => Outcomes.Keys.ToArray();
    internal static Action<Utf8JsonWriter>? WriteCiMetadata { get; set; }
    internal static string ResultsPath => Path.GetFullPath(Environment.GetEnvironmentVariable("RIGI_TEST_RESULTS")
        ?? Path.Combine(Environment.CurrentDirectory, "TestResults", "case-results.json"));

    // MTP 可重启独立 testhost：父进程只读本次新鲜 journal，不能把自身空字典当 child 结果。
    internal static IReadOnlyDictionary<string, CaseStatus> CompletedStatuses()
    {
        if (!Outcomes.IsEmpty) return Outcomes.ToDictionary(item => item.Key, item => item.Value.Status, StringComparer.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllBytes(ResultsPath));
        var root = document.RootElement;
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var ids = cases.Select(item => item.GetProperty("id").GetString()!).ToArray();
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("compilerCaseRows").GetInt32() != cases.Length
            || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
            || root.GetProperty("failures").GetInt32() != 0
            || root.GetProperty("assertions").GetInt64() != cases.Sum(item => item.GetProperty("assertions").GetInt64())
            || cases.Any(item => item.GetProperty("failures").GetInt32() != 0 || item.GetProperty("assertions").GetInt32() < 0
                || item.GetProperty("status").GetString() is not ("Pass" or "Skip")
                || item.GetProperty("status").GetString() == "Pass" && item.GetProperty("assertions").GetInt32() == 0
                || item.GetProperty("status").GetString() == "Skip" && string.IsNullOrWhiteSpace(item.GetProperty("skipReason").GetString())))
            throw new InvalidDataException("本次 testhost journal 的身份、状态或断言计数无效。");
        return cases.ToDictionary(item => item.GetProperty("id").GetString()!,
            item => item.GetProperty("status").GetString() == "Pass" ? CaseStatus.Pass : CaseStatus.Skip, StringComparer.Ordinal);
    }
    internal static void Record(CaseOutcome outcome)
    {
        if (!Outcomes.TryAdd(outcome.CaseId, outcome)) throw new InvalidOperationException("同一发现 ID 重复执行：" + outcome.CaseId);
    }
    internal static void Write()
    {
        if (Outcomes.IsEmpty) return;
        var path = ResultsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var results = Outcomes.Values.OrderBy(outcome => outcome.CaseId, StringComparer.Ordinal).ToArray();
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        if (WriteCiMetadata is { } metadata) { json.WriteStartObject("ci"); metadata(json); json.WriteEndObject(); }
        json.WriteNumber("compilerCaseRows", results.Length);
        json.WriteNumber("assertions", results.Sum(result => result.Assertions));
        json.WriteNumber("failures", results.Sum(result => result.Failures));
        json.WriteStartArray("cases");
        foreach (var result in results)
        {
            json.WriteStartObject(); json.WriteString("id", result.CaseId); json.WriteString("status", result.Status.ToString());
            json.WriteNumber("assertions", result.Assertions); json.WriteNumber("failures", result.Failures);
            json.WriteString("skipReason", result.SkipReason); json.WriteString("diagnostics", result.Diagnostics);
            json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteEndObject();
        // 框架结束后其 Console 捕获器可能已经关闭，直接使用进程标准输出报告证据路径。
        using var display = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        display.WriteLine($"内部断言：{results.Sum(result => result.Assertions)} 条，{results.Sum(result => result.Failures)} 失败；{results.Length} 个 provider 发现行。证据：{Path.GetFullPath(path)}");
    }
}
