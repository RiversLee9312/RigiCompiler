using System.Globalization;
using RigiCompiler.Tests;

namespace RigiCompiler.TUnitTests;

/// <summary>仅 CI 专用入口设置分片；全量目录与普通本地入口始终完整。</summary>
internal sealed record CiShardSelection(int Index, int Count, IReadOnlyList<CaseDescriptor> Cases)
{
    internal static CiShardSelection? Current { get; set; }
    internal const string ContextVariable = "RIGI_TEST_INTERNAL_CI_SHARD";

    internal static void RestoreTestHostContext()
    {
        if (Environment.GetEnvironmentVariable(ContextVariable) is not { Length: > 0 } context) return;
        Current = Parse(context.Split(' '));
        CiShardEvidence.AttachToJournal(Current);
    }

    internal static CiShardSelection Parse(string[] arguments)
    {
        if (arguments.Length != 2
            || !int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            throw new ArgumentException("--ci-shard 需要两个非负十进制整数：index count。");
        return Select(CaseCatalog.All, index, count);
    }

    internal static CiShardSelection Select(IReadOnlyList<CaseDescriptor> all, int index, int count)
    {
        if (count <= 0 || index < 0 || index >= count) throw new ArgumentException("CI 分片要求 count > 0 且 0 <= index < count。");
        if (all.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != all.Count)
            throw new ArgumentException("完整目录存在重复稳定 ID。");
        // 每套件按稳定身份排序轮转，均衡 Native/fuzz；一个 seed 批次始终是不可拆的发现行。
        var selected = all.GroupBy(item => item.Suite, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .SelectMany(group => group.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Where((_, position) => position % count == index)).ToArray();
        if (selected.Length == 0) throw new ArgumentException("CI 分片为空，拒绝零发现假绿。");
        return new(index, count, Array.AsReadOnly(selected));
    }

    internal static void VerifyGuard()
    {
        var all = CaseCatalog.All;
        foreach (int count in new[] { 3, 16 })
        {
            var shards = Enumerable.Range(0, count).Select(index => Select(all, index, count)).ToArray();
            var combined = shards.SelectMany(shard => shard.Cases).ToArray();
            if (combined.Length != all.Count || combined.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != all.Count
                || !combined.Select(item => item.Id).Order(StringComparer.Ordinal).SequenceEqual(all.Select(item => item.Id).Order(StringComparer.Ordinal))
                || combined.Sum(item => item.InputCount) != all.Sum(item => item.InputCount))
                throw new InvalidOperationException("CI 分片必须互斥且完整保留 provider 与 seed 输入。");
            foreach (var suite in all.GroupBy(item => item.Suite, StringComparer.Ordinal))
            {
                var rows = shards.Select(shard => shard.Cases.Count(item => item.Suite == suite.Key)).ToArray();
                if (rows.Max() - rows.Min() > 1) throw new InvalidOperationException("套件轮转分区不均衡：" + suite.Key);
            }
        }
        foreach (var arguments in new[] { Array.Empty<string>(), new[] { "0", "0" }, ["-1", "16"], ["16", "16"],
            ["0", "x"], ["0", "2147483648"], ["1", "2", "3"], [int.MaxValue.ToString(), int.MaxValue.ToString()] })
        {
            try { Parse(arguments); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("非法 CI 分片参数不得通过。");
        }
        try { Select(all, all.Count, all.Count + 1); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("空片不得通过。");
    }
}
