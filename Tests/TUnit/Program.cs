using RigiCompiler.Tests;

namespace RigiCompiler.TUnitTests;

internal static class Program
{
    // 官方生成 Application helper 仍负责注册框架；自定义入口仅在框架启动前分流 worker。
#pragma warning disable TUnit0034
    public static Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "--isolated-worker")
            return Task.FromResult(RunWorker(args[1..]));
        if (args.Length > 0 && args[0] == "--compat")
        {
            Environment.SetEnvironmentVariable(CiShardSelection.ContextVariable, null);
            return RunCompatible(args[1..]);
        }
        if (args.Length > 0 && args[0] == "--ci-shard")
            return RunCiShard(args[1..]);
        // 官方 MTP helper 可重新启动 testhost；专用入口的小上下文通过环境跨进程传递。
        CiShardSelection.RestoreTestHostContext();
        return RunFramework(args);
    }
#pragma warning restore TUnit0034

    private static async Task<int> RunFramework(string[] args, IReadOnlyCollection<string>? expectedIds = null,
        string? fullTrx = null, bool includeContracts = true)
    {
        try
        {
            if (expectedIds != null)
            {
                Environment.SetEnvironmentVariable("RIGI_TEST_RESULTS", CaseResultJournal.ResultsPath);
                if (File.Exists(CaseResultJournal.ResultsPath)) File.Delete(CaseResultJournal.ResultsPath);
            }
            var exitCode = await MicrosoftTestingPlatformApplication.RunAsync(args);
            if (exitCode != 0) return exitCode;
            var completed = expectedIds == null ? null : CaseResultJournal.CompletedStatuses();
            if (expectedIds != null && !expectedIds.Order(StringComparer.Ordinal).SequenceEqual(completed!.Keys.Order(StringComparer.Ordinal)))
                return DiscoveryFailure("实际 provider 结果 ID 与选择清单不一致，拒绝漏发现或重复执行。");
            if (fullTrx != null && !FullRunProof.IsComplete(fullTrx, completed!, includeContracts))
                return DiscoveryFailure("全量 TRX 未覆盖全部 provider、框架契约和闭合泛型发现。");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
        { return DiscoveryFailure("无法核对本次新鲜 testhost 证据：" + exception.Message); }
        finally { CaseResultJournal.Write(); }
    }

    private static int DiscoveryFailure(string message)
    {
        using var output = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false));
        output.WriteLine(message);
        return 9;
    }

    private static async Task<int> RunCiShard(string[] args)
    {
        CiShardSelection shard;
        try { shard = CiShardSelection.Parse(args); }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        foreach (var variable in new[] { "RIGI_TEST_SELECTION", "RIGI_TEST_SELECTION_TIMEOUTS", "RIGI_TEST_EXPLICIT_SELECTION" })
            Environment.SetEnvironmentVariable(variable, null);
        CiShardSelection.Current = shard;
        Environment.SetEnvironmentVariable(CiShardSelection.ContextVariable, $"{shard.Index} {shard.Count}");
        var directory = Environment.GetEnvironmentVariable("RIGI_TEST_RESULTS") is { Length: > 0 } results
            ? Path.GetDirectoryName(Path.GetFullPath(results))! : Path.Combine(AppContext.BaseDirectory, "TestResults");
        var trx = Path.Combine(directory, $"rigi-shard-{shard.Index}-{Environment.ProcessId}.trx");
        if (File.Exists(trx)) File.Delete(trx);
        CiShardEvidence evidence;
        try { evidence = CiShardEvidence.Begin(shard, directory, trx); }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        string[] framework = ["--minimum-expected-tests", "1", "--report-trx", "--report-trx-filename", trx, "--results-directory", directory];
        // 只有片 0 发现真实框架契约；其余片复用官方 category 过滤，不重复执行 Generic。
        if (shard.Index != 0) framework = [.. framework, "--treenode-filter", "/*/*/*/*[Category=CompilerCase]"];
        int exitCode = 1;
        try { return exitCode = await RunFramework(framework, shard.Cases.Select(item => item.Id).ToArray(), trx, shard.Index == 0); }
        finally { evidence.Write(exitCode); }
    }

    private static int RunWorker(string[] args)
    {
        if (args.Length != 4 || args[0] != "--case-id" || args[2] != "--result-file") return 2;
        WorkerEnvironment.IsWorker = true;
        var outcome = CaseCatalog.Run(args[1]);
        outcome.Write(args[3]);
        if (CaseCatalog.Find(args[1]) == null) return 2;
        return outcome.Status switch { CaseStatus.Pass or CaseStatus.Skip => 0, CaseStatus.Cancel => 130, _ => 1 };
    }

    private static Task<int> RunCompatible(string[] args)
    {
        if (!CommandLineParser.TryParse(["test", .. args], out var parsed, out var error))
        { Console.Error.WriteLine(error); return Task.FromResult(2); }
        if (LoggerOptions.Apply(parsed!) is { } loggerError)
        { Console.Error.WriteLine(loggerError); return Task.FromResult(2); }
        if (parsed!.Has("--worker") || parsed.Has("--spawned") || parsed.Has("--case-id") || parsed.Has("--result-file"))
        { Console.Error.WriteLine("隔离 worker 属于 TUnit 宿主内部协议，请使用框架测试入口。"); return Task.FromResult(2); }
        if (parsed.Has("--suite-args") && !parsed.Has("--all") && parsed.Get("--run") is not { Count: > 0 })
        { Console.Error.WriteLine("--suite-args 需要 test --run。"); return Task.FromResult(2); }
        if (parsed.Has("--inventory"))
        { TestInventory.Write(Console.OpenStandardOutput(), FullRunProof.ContractIdentities); return Task.FromResult(0); }
        if (parsed.Has("--all"))
        {
            if (parsed.Has("--suite-args")) return Task.FromResult(2);
            foreach (var variable in new[] { "RIGI_TEST_SELECTION", "RIGI_TEST_SELECTION_TIMEOUTS", "RIGI_TEST_EXPLICIT_SELECTION" })
                Environment.SetEnvironmentVariable(variable, null);
            var directory = Environment.GetEnvironmentVariable("RIGI_TEST_RESULTS") is { Length: > 0 } results
                ? Path.GetDirectoryName(Path.GetFullPath(results))! : Path.Combine(AppContext.BaseDirectory, "TestResults");
            var trx = Path.Combine(directory, "rigi-full-" + Environment.ProcessId + ".trx");
            if (File.Exists(trx)) File.Delete(trx);
            // MTP minimum 不计算 Skip；精确完成性由结果 ID 集合和 TRX 验证，门控/平台跳过仍有效。
            return RunFramework(["--minimum-expected-tests", "1", "--report-trx", "--report-trx-filename", trx,
                "--results-directory", directory], CaseCatalog.All.Select(item => item.Id).ToArray(), trx);
        }
        if (parsed.Get("--run") is { Count: > 0 } numbers)
        {
            try
            {
                var indices = numbers.Select(number => int.TryParse(number, out var index) && index >= 1 && index <= TestSuiteCatalog.Count
                    ? index : throw new ArgumentException("无效套件编号：" + number)).ToArray();
                if (parsed.Get("--suite-args") is { Count: 1 } list && list[0] == "list")
                {
                    foreach (var index in indices)
                    {
                        foreach (var item in TestInventory.Cases(TestSuiteCatalog.Names[index - 1])!) Console.WriteLine($"{item.Index}: {item.Label}");
                    }
                    return Task.FromResult(0);
                }
                var selection = indices.SelectMany(number => CaseSelection.Select(number, parsed.Get("--suite-args"))).DistinctBy(item => item.Id).ToArray();
                // 兼容选择仅传给框架数据源；执行不会回到旧套件总调度。
                Environment.SetEnvironmentVariable("RIGI_TEST_SELECTION", string.Join(';', selection.Select(item => item.Id)));
                Environment.SetEnvironmentVariable("RIGI_TEST_SELECTION_TIMEOUTS", string.Join(';', selection.Where(item => item.Timeout is { } timeout && timeout > TimeSpan.Zero)
                    .Select(item => item.Id + "|" + item.Timeout!.Value.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                Environment.SetEnvironmentVariable("RIGI_TEST_EXPLICIT_SELECTION", parsed.Has("--suite-args") ? "1" : null);
                // 全部被合法跳过也算实际发现；精确 ID 对照保证零发现不能假绿。
                return RunFramework(["--treenode-filter", "/*/*/*/*[Category=CompilerCase]"], selection.Select(item => item.Id).ToArray());
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            { Console.Error.WriteLine(exception.Message); return Task.FromResult(2); }
        }
        TestSuiteCatalog.PrintMenu();
        return Task.FromResult(0);
    }
}
