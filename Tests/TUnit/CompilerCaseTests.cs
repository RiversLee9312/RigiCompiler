using RigiCompiler.Tests;
using TUnit.Core;

namespace RigiCompiler.TUnitTests;

public static class CaseData
{
    internal static TimeSpan? SelectedTimeout(string id)
    {
        var values = Environment.GetEnvironmentVariable("RIGI_TEST_SELECTION_TIMEOUTS")?.Split(';') ?? [];
        var value = values.FirstOrDefault(item => item.StartsWith(id + "|", StringComparison.Ordinal));
        return value == null ? null : TimeSpan.FromMilliseconds(double.Parse(value[(id.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }
    private static IEnumerable<CaseDescriptor> SelectedCases() => CiShardSelection.Current is { } shard ? shard.Cases
        : Environment.GetEnvironmentVariable("RIGI_TEST_SELECTION") is { Length: > 0 } selected
        ? selected.Split(';').Distinct(StringComparer.Ordinal).Select(id => CaseCatalog.Find(id) ?? throw new ArgumentException("未知选择 ID：" + id))
        : CaseCatalog.All;
    public static IEnumerable<TestDataRow<string>> All() => SelectedCases().Select(c =>
        new TestDataRow<string>(c.Id, DisplayName: c.Id,
            Categories: [c.Id, c.Suite, "CompilerCase", c.Group, c.Trait]));
}

public class CompilerCaseTests
{
    private static readonly CaseWorkerClient Worker = new();

    [Test]
    [MethodDataSource(typeof(CaseData), nameof(CaseData.All))]
    public async Task Run(string caseId, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 仅用于外部验收取消的门控；正常运行使用框架传入的 token。
        if (int.TryParse(Environment.GetEnvironmentVariable("RIGI_TEST_CANCEL_AFTER_MS"), out var milliseconds))
            cancellation.CancelAfter(milliseconds);
        var outcome = await Worker.RunAsync(caseId, cancellation.Token, CaseData.SelectedTimeout(caseId));
        CaseResultJournal.Record(outcome);
        switch (outcome.Status)
        {
            case CaseStatus.Pass: return;
            case CaseStatus.Skip: Skip.Test(outcome.SkipReason!); return;
            case CaseStatus.Cancel:
                // 先等 worker 灭树/排空完成，再通知框架取消当前测试。
                var execution = TestContext.Current!.Execution;
                execution.Cancel();
                throw new OperationCanceledException(outcome.Diagnostics, execution.CancellationToken);
            default: throw new InvalidOperationException($"{caseId}: {outcome.Failures}/{outcome.Assertions} 条断言失败\n{outcome.Diagnostics}");
        }
    }
}

public class GenericDiscoveryTests
{
    [Test]
    [Category("Generic")]
    [GenerateGenericTest(typeof(int))]
    public void ClosedGeneric<T>()
    {
        if (typeof(T) != typeof(int)) throw new InvalidOperationException("闭合泛型发现错误");
    }
}
