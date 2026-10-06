using System.Text.Json;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

/// <summary>只验证旁路、异常、进程隔离与选择契约；不启动全量测试。</summary>
public static class PerformanceMetricsTests
{


    internal static TestSuiteData Spec { get; } = new("PerformanceMetrics",
    [
        (nameof(TestRunnerSelection), () => WithDirectory(TestRunnerSelection)),
        (nameof(TestTelemetryContract), () => WithDirectory(TestTelemetryContract)),
        (nameof(TestFailedScopes), () => WithDirectory(TestFailedScopes)),
        (nameof(TestUnwritableDirectory), () => WithDirectory(TestUnwritableDirectory)),
        (nameof(TestExternalTimeout), () => WithDirectory(TestExternalTimeout)),
        (nameof(TestCacheRoot), () => WithDirectory(TestCacheRoot)),
    ], sectionTitle: "PerformanceMetrics");

    private static void WithDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rigi-metrics-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void TestRunnerSelection(string directory)
    {
        var labels = new[] { "known" };
        var selected = CaseSelection.SelectLabels(labels, ["known"]);
        bool rejected = false;
        try { CaseSelection.SelectLabels(labels, ["known", "unknown"]); }
        catch (ArgumentException) { rejected = true; }
        CaseAssertions.CheckTrue("纯选择 / 未知 label 整批拒绝 / 精确 label 不执行动作", rejected && selected.SequenceEqual(labels));
    }

    private static void TestTelemetryContract(string directory)
    {
        var source = Path.Combine(directory, "valid.rg");
        File.WriteAllText(source, "pub func main(): i32 { return 0 }\n");
        var metricDirectory = Path.Combine(directory, "metrics");
        var (executable, prefix) = TestArtifacts.CompilerCommand;
        var arguments = prefix.Concat(new[] { "compile", "--file", source, "--parse-only" }).ToArray();
        var disabled = new Dictionary<string, string> { ["RIGI_PROFILE_DIR"] = "" };
        var enabled = new Dictionary<string, string> { ["RIGI_PROFILE_DIR"] = metricDirectory };
        int first = ExternalProcess.Run(executable, arguments, out var stdout, out var stderr,
            environment: disabled, timeoutMilliseconds: 30_000, closeStdin: true);
        int second = ExternalProcess.Run(executable, arguments, out var profiledStdout, out var profiledStderr,
            environment: enabled, timeoutMilliseconds: 30_000, closeStdin: true);
        CaseAssertions.CheckTrue("opt-in 不改变 stdout/stderr/退出码", first == 0 && second == 0 && stdout == profiledStdout && stderr == profiledStderr);
        ExternalProcess.Run(executable, arguments, out _, out _, environment: enabled,
            timeoutMilliseconds: 30_000, closeStdin: true);
        var files = Directory.GetFiles(metricDirectory, "metrics-*.jsonl");
        CaseAssertions.CheckTrue("两个进程写不同 JSONL 文件", files.Length == 2 && files.Distinct().Count() == 2);
        var processIds = new HashSet<int>();
        var scopeProcessIds = new HashSet<int>();
        var workerProcessIds = new HashSet<int>();
        bool validFields = true;
        foreach (var file in files)
        foreach (var line in File.ReadLines(file))
        {
            using var json = JsonDocument.Parse(line);
            var value = json.RootElement;
            var processId = value.GetProperty("processId").GetInt32();
            processIds.Add(processId);
            validFields &= processId > 0 && value.TryGetProperty("parentPhaseId", out _);
            // 阶段资源 scope 与 join 后的 compiler-workers 事件共用 JSONL；
            // 分别验证完整契约，不能把事件当作缺少资源字段的 scope。
            switch (value.GetProperty("kind").GetString())
            {
                case "scope":
                    scopeProcessIds.Add(processId);
                    validFields &= value.GetProperty("scope").GetString() == "current-process"
                        && value.GetProperty("wallMilliseconds").GetDouble() >= 0
                        && value.GetProperty("managedAllocatedBytes").GetInt64() >= 0
                        && value.GetProperty("processLifetimePeakRssBytes").GetInt64() >= 0;
                    break;
                case "compiler-workers":
                    workerProcessIds.Add(processId);
                    var count = value.GetProperty("jobCount").GetInt32();
                    var limit = value.GetProperty("workerLimit").GetInt32();
                    var peak = value.GetProperty("activeWorkerPeak").GetInt32();
                    validFields &= count > 0 && limit > 0 && peak > 0 && peak <= Math.Min(count, limit)
                        && value.GetProperty("startedJobCount").GetInt32() == count
                        && value.GetProperty("completedJobCount").GetInt32() == count
                        && value.GetProperty("grantedCpuSlots").GetInt32()
                            == (value.GetProperty("leaseAcquired").GetBoolean() ? limit : 0)
                        && value.GetProperty("status").GetString() == "completed";
                    break;
                default:
                    validFields = false;
                    break;
            }
        }
        CaseAssertions.CheckTrue("JSONL 资源字段及进程 ID", validFields && processIds.Count == 2
            && scopeProcessIds.SetEquals(processIds) && workerProcessIds.SetEquals(processIds));

    }

    private static void TestFailedScopes(string directory)
    {
        var source = Path.Combine(directory, "valid.rg");
        File.WriteAllText(source, "pub func main(): i32 { return 0 }\n");
        var metricDirectory = Path.Combine(directory, "metrics");
        var (executable, prefix) = TestArtifacts.CompilerCommand;
        var arguments = prefix.Concat(new[] { "compile", "--file", source, "--parse-only" }).ToArray();
        var disabled = new Dictionary<string, string> { ["RIGI_PROFILE_DIR"] = "" };
        var enabled = new Dictionary<string, string> { ["RIGI_PROFILE_DIR"] = metricDirectory };
        File.WriteAllText(source, "pub func main( {\n");
        int failed = ExternalProcess.Run(executable, arguments, out _, out _, environment: enabled,
            timeoutMilliseconds: 30_000, closeStdin: true);
        var failedRows = Directory.GetFiles(metricDirectory, "*.jsonl").SelectMany(File.ReadLines)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            CaseAssertions.CheckTrue("异常和非零命令都闭合 failed scope", failed != 0 && failedRows.Any(d =>
                d.RootElement.GetProperty("phase").GetString() == "frontend.parser" && d.RootElement.GetProperty("status").GetString() == "failed")
                && failedRows.Any(d => d.RootElement.GetProperty("phase").GetString() == "cli.command" && d.RootElement.GetProperty("status").GetString() == "failed"));
        }
        finally { foreach (var row in failedRows) row.Dispose(); }

    }

    private static void TestUnwritableDirectory(string directory)
    {
        var source = Path.Combine(directory, "valid.rg");
        File.WriteAllText(source, "pub func main(): i32 { return 0 }\n");
        var metricDirectory = Path.Combine(directory, "metrics");
        var (executable, prefix) = TestArtifacts.CompilerCommand;
        var arguments = prefix.Concat(new[] { "compile", "--file", source, "--parse-only" }).ToArray();
        var disabled = new Dictionary<string, string> { ["RIGI_PROFILE_DIR"] = "" };
        var enabled = new Dictionary<string, string> { ["RIGI_PROFILE_DIR"] = metricDirectory };
        ExternalProcess.Run(executable, arguments, out var stdout, out var stderr, environment: disabled, timeoutMilliseconds: 30_000, closeStdin: true);
        // 旁路目录错误不得变更编译结果；路径指向普通文件也应静默禁写。
        File.WriteAllText(source, "pub func main(): i32 { return 0 }\n");
        int invalidDirectory = ExternalProcess.Run(executable, arguments, out var fallbackStdout, out var fallbackStderr,
            environment: new Dictionary<string, string> { ["RIGI_PROFILE_DIR"] = source },
            timeoutMilliseconds: 30_000, closeStdin: true);
        CaseAssertions.CheckTrue("不可写遥测目录不改变输出", invalidDirectory == 0 && fallbackStdout == stdout && fallbackStderr == stderr);

    }

    private static void TestExternalTimeout(string directory)
    {
        if (!OperatingSystem.IsLinux()) { CaseAssertions.RecordSkip("Linux 进程超时专项"); return; }
        if (OperatingSystem.IsLinux())
        {
            bool threw = false;
            try { ExternalProcess.Run("/bin/sh", ["-c", "sleep 10"], out _, out _, timeoutMilliseconds: 50, closeStdin: true, parentPhase: "test.timeout-parent"); }
            catch (InvalidOperationException) { threw = true; }
            CaseAssertions.CheckTrue("ExternalProcess 超时抛受控失败", threw);
        }

    }

    private static void TestCacheRoot(string directory)
    {
        var oldRoot = Environment.GetEnvironmentVariable("RIGI_CACHE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("RIGI_CACHE_ROOT", directory);
            CaseAssertions.CheckTrue("runtime-cache 私有根覆盖", RigiRtBuilder.GetCacheRoot() == Path.Combine(directory, "runtime-cache"));
            Environment.SetEnvironmentVariable("RIGI_CACHE_ROOT", null);
            CaseAssertions.CheckTrue("runtime-cache 默认路径兼容", RigiRtBuilder.GetCacheRoot() == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rigi", "runtime-cache"));
        }
        finally { Environment.SetEnvironmentVariable("RIGI_CACHE_ROOT", oldRoot); }
    }
}
