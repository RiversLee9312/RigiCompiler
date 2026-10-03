namespace RigiCompiler.Tests;

/// <summary>旧 suite 入口适配；父进程交给共同 dispatcher，spawned 只执行本批动作。</summary>
public static class ParallelSuiteRunner
{
    public sealed class SuiteSpec(string suiteName, IReadOnlyList<(string Label, Action Run)> cases,
        string? sectionTitle = null, Action? beforeSpawn = null)
    {
        public string SuiteName { get; } = suiteName;
        public IReadOnlyList<(string Label, Action Run)> Cases { get; } = cases;
        public string? SectionTitle { get; } = sectionTitle;
        public Action? BeforeSpawn { get; } = beforeSpawn;
    }
    public static int RunAll(SuiteSpec spec) => RunRange(spec, 0, spec.Cases.Count - 1);
    public static int RunWithArgs(SuiteSpec spec, IReadOnlyList<string> args)
    {
        if (!TestRunner.IsSpawned) return TestRunner.RunSuite(TestRunner.GetSuiteNumber(spec.SuiteName), args);
        if (args.Count == 1 && args[0] == "list")
        { for (int i = 0; i < spec.Cases.Count; i++) Console.WriteLine($"{i}: {spec.Cases[i].Label}"); return 0; }
        int[] indices;
        if (args.Count > 1 && args[0] == "label")
        {
            if (args.Skip(1).Any(label => !spec.Cases.Any(c => c.Label == label))) return 2;
            indices = spec.Cases.Select((c, i) => (c, i)).Where(c => args.Skip(1).Contains(c.c.Label, StringComparer.Ordinal)).Select(c => c.i).ToArray();
        }
        else if (args.Count == 2 && int.TryParse(args[0], out int from) && int.TryParse(args[1], out int to)
            && from >= 0 && to >= from && to < spec.Cases.Count) indices = Enumerable.Range(from, to - from + 1).ToArray();
        else { Console.Error.WriteLine($"{spec.SuiteName} 选择器无效"); return 2; }
        return RunInProcess(spec, indices);
    }
    public static int RunRange(SuiteSpec spec, int from, int to)
    {
        if (from < 0 || to < from || to >= spec.Cases.Count) return 2;
        var indices = Enumerable.Range(from, to - from + 1).ToArray();
        if (!TestRunner.IsSpawned) return LegacyDispatcher.RunIndices(spec.SuiteName, indices);
        return RunInProcess(spec, indices);
    }
    private static int RunInProcess(SuiteSpec spec, IReadOnlyList<int> indices)
    {
        TestHarness.Reset();
        if (spec.SectionTitle != null) TestHarness.Section(spec.SectionTitle);
        foreach (int index in indices)
        {
            try { spec.Cases[index].Run(); }
            catch (Exception ex) { TestHarness.CheckTrue(spec.Cases[index].Label + "：测试驱动异常", false, ex.ToString()); }
        }
        return TestHarness.Summary(spec.SuiteName);
    }
        public static void PrintFailureOutput(string stdout)
        {
            var lines = FailureOutputLines(stdout);
            if (lines.Count == 0)
            {
                return;
            }
            Console.WriteLine($"    [stdout 全量（滤 [PASS]）共 {lines.Count} 行]");
            foreach (var line in lines)
            {
                Console.WriteLine("      | " + line);
            }
        }

        // 失败相关 stdout 行提取（滤掉 [PASS] 与空行，其余一律保留）
        public static IReadOnlyList<string> FailureOutputLines(string stdout)
        {
            var result = new List<string>();
            foreach (var raw in stdout.Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0
                    || line.StartsWith("  [PASS] ", StringComparison.Ordinal))
                {
                    continue;
                }
                result.Add(line);
            }
            return result;
        }

}
