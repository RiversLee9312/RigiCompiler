using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigiCompiler.PerfBaseline;

/// <summary>独立 BCL 工具：按注册名发现测试，用受预算控制的代表输入生成可复现数据。</summary>
internal static class Baseline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static bool Successful(JsonObject run) => run["exitCode"]!.GetValue<int>() == 0 && run["observedSkip"]?.GetValue<bool>() != true;
    private static bool ToolchainSkipped(JsonObject run) => run["skipReason"]?["kind"]?.GetValue<string>() == "toolchain";
    private static readonly string[] Categories = ["csharp-build", "csharp-publish", "source-to-bil", "bil-to-native", "native-run", "test", "end-to-end"];
    internal static double Median(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("无样本");
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }
    private static string String(JsonNode node, string key, string fallback = "") => node[key]?.GetValue<string>() ?? fallback;
    private static int Integer(JsonNode node, string key, int fallback) => node[key]?.GetValue<int>() ?? fallback;
    private static string? ResolveExecutable(string executable)
    {
        if (File.Exists(executable)) return Path.GetFullPath(executable);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(directory, executable);
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (OperatingSystem.IsWindows() && File.Exists(path + ".exe")) return Path.GetFullPath(path + ".exe");
        }
        return null;
    }
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }
    private static void Save(string path, JsonNode value) => File.WriteAllText(path, value.ToJsonString(JsonOptions) + "\n");
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    private static Dictionary<string, string> EnvironmentValues(JsonNode? values) => values?.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()) ?? [];

    internal static async Task<int> Run(string profilePath, string outputPath)
    {
        var profile = JsonNode.Parse(await File.ReadAllTextAsync(profilePath)) ?? throw new ArgumentException("空 profile");
        var repo = Path.GetFullPath(String(profile, "repoRoot", "."));
        var output = Path.GetFullPath(outputPath);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("输出目录必须为空，避免污染冷缓存或覆盖基线");
        var hotRuns = Integer(profile, "hotRuns", 3);
        var warmupRuns = Integer(profile, "warmupRuns", 1);
        var timeout = Integer(profile, "timeoutSeconds", 120);
        var budgetSeconds = Integer(profile, "budgetSeconds", 900);
        if (hotRuns < 1 || warmupRuns < 1 || timeout < 1 || budgetSeconds < 1) throw new ArgumentException("重复数与时间预算必须为正数");
        Directory.CreateDirectory(output);
        var cache = Path.Combine(output, "controlled-cache");
        var commonEnv = EnvironmentValues(profile["environment"]);
        commonEnv["RIGI_CACHE_ROOT"] = cache;
        var compiler = profile["compiler"] ?? throw new ArgumentException("需要 compiler.fileName / arguments");
        string Expand(string text) => text.Replace("{repo}", repo).Replace("{output}", output);
        var compilerExe = Expand(String(compiler, "fileName"));
        var compilerArgs = compiler["arguments"]?.AsArray().Select(a => Expand(a!.GetValue<string>())).ToArray() ?? [];
        // 所有命令均用 ArgumentList；profile 中的参数不会被当作 shell 代码。
        var discovery = await Execute(compilerExe, [.. compilerArgs, "test", "--inventory"], repo, commonEnv,
            Math.Min(timeout, 60), Path.Combine(output, "discovery"));
        if (discovery["exitCode"]!.GetValue<int>() != 0) throw new InvalidOperationException("测试清单发现失败，见 discovery.stderr.log");
        var inventory = JsonNode.Parse(await File.ReadAllTextAsync(String(discovery, "stdoutLog")))!;
        Save(Path.Combine(output, "discovered-inventory.json"), inventory);
        var suites = inventory["suites"]!.AsArray().ToDictionary(s => String(s!, "name"), s => s!);
        var commands = new List<JsonObject>();
        foreach (var node in profile["commands"]?.AsArray() ?? []) commands.Add(node!.AsObject());
        foreach (var node in profile["suites"]?.AsArray() ?? [])
        {
            var name = String(node!, "name");
            if (!suites.TryGetValue(name, out var suite)) throw new ArgumentException($"未知 suite：{name}");
            var labels = node!["labels"]?.AsArray().Select(l => l!.GetValue<string>()).ToArray() ?? [];
            var groups = node["groups"]?.AsArray().Select(g => g!.GetValue<string>()).ToArray() ?? [];
            var range = node["range"]?.AsArray();
            var selector = String(suite, "selector");
            var arguments = new List<string>(compilerArgs) { "test", "--run", suite["number"]!.ToString() };
            if ((labels.Length > 0 ? 1 : 0) + (groups.Length > 0 ? 1 : 0) + (range != null ? 1 : 0) > 1) throw new ArgumentException("labels/groups/range 互斥");
            if (labels.Length > 0)
            {
                var known = suite["cases"]!.AsArray().Select(c => String(c!, "label")).ToHashSet(StringComparer.Ordinal);
                if (labels.Any(l => !known.Contains(l))) throw new ArgumentException($"{name} 含未知 label");
                if (selector == "exact-label") { arguments.Add("--suite-args"); arguments.Add("label"); arguments.AddRange(labels); }
                else if (selector == "name-filter")
                {
                    // Native/E2E 的原选择器是子串；多标签拆独立命令，避免一个短标签扩大选择。
                    foreach (var label in labels)
                    {
                        if (name == "E2e" && known.Any(other => other != label && other.Contains(label, StringComparison.OrdinalIgnoreCase)))
                            throw new ArgumentException($"E2e label {label} 的子串还会选中其他用例，请用 range 或更明确标签");
                        var single = new List<string>(arguments) { "--suite-args", label };
                        commands.Add(SuiteCommand(name, single, compilerExe, node, [label], []));
                    }
                    continue;
                }
                else throw new ArgumentException($"{name} 不支持标签选择");
            }
            else if (groups.Length > 0)
            {
                var known = suite["groups"]?.AsArray().Select(g => g!.GetValue<string>()).ToHashSet() ?? [];
                if (groups.Any(g => !known.Contains(g)) || groups.Length != 1) throw new ArgumentException($"{name} 含未知 group");
                arguments.Add("--suite-args"); arguments.AddRange(groups);
            }
            else if (range != null)
            {
                if (range.Count != 2 || selector == "suite") throw new ArgumentException($"{name} 不支持该 range");
                int from = range[0]!.GetValue<int>(), to = range[1]!.GetValue<int>();
                var activeCases = suite["cases"]!.AsArray().Where(c => String(c!, "status") != "gate-excluded").ToArray();
                if (from < 0 || to < from || to >= activeCases.Length) throw new ArgumentException($"{name} range 越界");
                labels = activeCases.Skip(from).Take(to - from + 1).Select(c => String(c!, "label")).ToArray();
                arguments.Add("--suite-args"); arguments.Add(from.ToString()); arguments.Add(to.ToString());
            }
            commands.Add(SuiteCommand(name, arguments, compilerExe, node, labels, groups));
        }
        if (commands.Count == 0) throw new ArgumentException("profile 没有选择任何命令或 suite");
        foreach (var command in commands)
        {
            if (Integer(command, "hotRuns", hotRuns) < 1 || Integer(command, "timeoutSeconds", timeout) < 1)
                throw new ArgumentException("每条 command 的 hotRuns / timeoutSeconds 必须为正数");
        }
        var names = commands.Select(c => String(c, "name")).ToArray();
        if (names.Any(n => n.Length == 0 || n.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.')) || names.Distinct().Count() != names.Length)
            throw new ArgumentException("command.name 必须唯一且仅用字母数字、点、短线或下划线");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1, ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["os"] = RuntimeInformation.OSDescription, ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription, ["cpuCount"] = Environment.ProcessorCount,
            ["machine"] = Environment.MachineName, ["cpuModel"] = CpuModel(), ["machineResources"] = MachineResources.Capture(),
            ["repoRoot"] = repo, ["profilePath"] = Path.GetFullPath(profilePath), ["profileSha256"] = Hash(profilePath),
            ["profile"] = profile.DeepClone(), ["timeoutSeconds"] = timeout, ["budgetSeconds"] = budgetSeconds,
            ["coldRuns"] = 1, ["warmupRuns"] = warmupRuns, ["hotRuns"] = hotRuns,
            ["cacheRoot"] = cache, ["cacheCondition"] = "cold controls only RIGI content cache; OS page cache and C# obj/bin caches are not controlled",
            ["metricMeaning"] = "wall includes process startup; CPU/RSS describe the launched process only; managed allocations are in compiler metrics and exclude LLVM native allocations",
            ["environment"] = JsonSerializer.SerializeToNode(commonEnv),
            ["inheritedSwitches"] = JsonSerializer.SerializeToNode(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(e => e.Key.ToString()!.StartsWith("RIGI_") || e.Key.ToString()!.StartsWith("DOTNET_") || e.Key.ToString() == "GITHUB_ACTIONS")
                .ToDictionary(e => e.Key.ToString()!, e => e.Value?.ToString())),
            ["discoveryRun"] = discovery,
        };
        var compilerArtifacts = new JsonArray();
        foreach (var artifact in new[] { ResolveExecutable(compilerExe) ?? compilerExe }.Concat(compilerArgs).Where(File.Exists).Distinct())
            compilerArtifacts.Add(new JsonObject { ["path"] = Path.GetFullPath(artifact), ["sha256"] = Hash(artifact),
                ["lastWriteUtc"] = File.GetLastWriteTimeUtc(artifact).ToString("O") });
        manifest["compilerArtifacts"] = compilerArtifacts;
        var dependencyArtifacts = new JsonArray();
        foreach (var item in profile["dependencyArtifacts"]?.AsArray() ?? [])
        {
            var path = Expand(item!.GetValue<string>());
            if (!File.Exists(path)) throw new ArgumentException("依赖产物不存在：" + path);
            dependencyArtifacts.Add(new JsonObject { ["path"] = Path.GetFullPath(path), ["sha256"] = Hash(path) });
        }
        manifest["dependencyArtifacts"] = dependencyArtifacts;
        var metadataEnv = new Dictionary<string, string>(commonEnv);
        var head = await Execute("git", ["rev-parse", "HEAD"], repo, metadataEnv, 30, Path.Combine(output, "source-head"));
        var dirty = await Execute("git", ["status", "--porcelain=v1", "--untracked-files=all"], repo, metadataEnv, 30, Path.Combine(output, "source-dirty"));
        manifest["sourceHead"] = (await File.ReadAllTextAsync(String(head, "stdoutLog"))).Trim();
        manifest["sourceDirty"] = new FileInfo(String(dirty, "stdoutLog")).Length > 0;
        manifest["sourceStatus"] = await File.ReadAllTextAsync(String(dirty, "stdoutLog"));
        var tracked = await Execute("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], repo, metadataEnv, 30, Path.Combine(output, "source-files"));
        var sourceHashes = new JsonArray();
        foreach (var relative in (await File.ReadAllTextAsync(String(tracked, "stdoutLog"))).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().Order())
        {
            var path = Path.Combine(repo, relative);
            if (File.Exists(path)) sourceHashes.Add(new JsonObject { ["path"] = relative, ["sha256"] = Hash(path) });
        }
        manifest["sourceFiles"] = sourceHashes;
        var toolVersions = new JsonArray();
        foreach (var tool in profile["toolchains"]?.AsArray() ?? [])
        {
            var exe = Expand(String(tool!, "fileName"));
            var parameters = tool!["arguments"]?.AsArray().Select(a => Expand(a!.GetValue<string>())).ToArray() ?? ["--version"];
            var version = await Execute(exe, parameters, repo, metadataEnv, 30, Path.Combine(output, "tool-" + toolVersions.Count));
            version["stdout"] = await File.ReadAllTextAsync(String(version, "stdoutLog"));
            var resolved = ResolveExecutable(exe);
            version["resolvedFileName"] = resolved;
            if (resolved != null) version["executableSha256"] = Hash(resolved);
            toolVersions.Add(version);
        }
        manifest["toolchains"] = toolVersions;
        var selections = new JsonArray();
        foreach (var category in Categories) selections.Add(new JsonObject { ["category"] = category, ["status"] = commands.Any(c => String(c, "category") == category) ? "selected" : "not-selected" });
        manifest["categoryCoverage"] = selections;
        Save(Path.Combine(output, "manifest.json"), manifest);
        var runs = new List<JsonObject>();
        var budget = Stopwatch.StartNew();
        int failure = 0;
        foreach (var command in commands)
        {
            var name = String(command, "name");
            var category = String(command, "category");
            var count = Integer(command, "hotRuns", hotRuns);
            if (count < 1) throw new ArgumentException("command.hotRuns 必须为正数");
            for (int i = 0; i < 1 + warmupRuns + count; i++)
            {
                var condition = i == 0 ? "cold" : i <= warmupRuns ? "warmup" : "hot";
                if (budget.Elapsed.TotalSeconds >= budgetSeconds) { failure = 124; break; }
                var prefix = Path.Combine(output, "runs", name + "-" + condition + "-" + i);
                Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
                var environment = new Dictionary<string, string>(commonEnv);
                foreach (var pair in EnvironmentValues(command["environment"])) environment[pair.Key] = pair.Value;
                environment["RIGI_CACHE_ROOT"] = cache;
                environment["RIGI_PROFILE_DIR"] = prefix + ".metrics";
                // 每个工作负载有独立缓存根：其冷样本不会被之前命令的 runtime 预热。
                environment["RIGI_CACHE_ROOT"] = Path.Combine(cache, name);
                var executable = Expand(String(command, "fileName"));
                var parameters = new List<string>();
                var globInputs = new List<string>();
                foreach (var arg in command["arguments"]?.AsArray() ?? [])
                {
                    var expanded = Expand(arg!.GetValue<string>());
                    if (expanded.StartsWith("{glob:") && expanded.EndsWith('}'))
                    {
                        var glob = expanded[6..^1];
                        var found = Directory.GetFiles(Path.GetDirectoryName(glob)!, Path.GetFileName(glob)).Order(StringComparer.Ordinal).ToArray();
                        if (found.Length == 0) throw new ArgumentException("glob 未找到输入：" + glob);
                        parameters.AddRange(found);
                        globInputs.AddRange(found);
                    }
                    else parameters.Add(expanded);
                }
                var inputs = new JsonArray();
                foreach (var path in (command["inputs"]?.AsArray().Select(input => Expand(input!.GetValue<string>())) ?? []).Concat(globInputs).Distinct(StringComparer.Ordinal))
                {
                    if (!File.Exists(path)) throw new ArgumentException("输入不存在：" + path);
                    inputs.Add(new JsonObject { ["path"] = Path.GetFullPath(path), ["sha256"] = Hash(path) });
                }
                var runTimeout = Math.Min(Integer(command, "timeoutSeconds", timeout), Math.Max(1, (int)Math.Ceiling(budgetSeconds - budget.Elapsed.TotalSeconds)));
                Console.WriteLine($"{name} {condition} #{i}（超时 {runTimeout}s）");
                var run = await Execute(executable, parameters, repo, environment, runTimeout, prefix);
                run["name"] = name; run["category"] = category; run["condition"] = condition; run["ordinal"] = i;
                run["inputs"] = inputs; run["suite"] = command["suite"]?.DeepClone(); run["labels"] = command["labels"]?.DeepClone(); run["groups"] = command["groups"]?.DeepClone();
                run["metricsDirectory"] = environment["RIGI_PROFILE_DIR"];
                var testLog = category == "test" ? await File.ReadAllTextAsync(String(run, "stdoutLog")) : "";
                bool missingToolchain = String(command, "suite") == "NativeE2E" && testLog.Contains("（跳过：未找到 clang 工具链", StringComparison.Ordinal);
                bool reportedSkip = testLog.Contains("[SKIP]", StringComparison.Ordinal);
                run["skipReason"] = missingToolchain ? new JsonObject { ["kind"] = "toolchain", ["tool"] = "clang", ["scope"] = "entire-suite", ["evidence"] = "NativeE2E explicit missing-clang message" }
                    : reportedSkip ? new JsonObject { ["kind"] = "test-reported", ["scope"] = "unknown", ["evidence"] = "test stdout contains [SKIP]" } : null;
                run["observedSkip"] = missingToolchain || reportedSkip;
                runs.Add(run);
                await File.AppendAllTextAsync(Path.Combine(output, "runs.jsonl"), run.ToJsonString() + "\n");
                if (run["exitCode"]!.GetValue<int>() != 0) { failure = run["exitCode"]!.GetValue<int>(); break; }
            }
            if (failure != 0) break;
        }
        // 发现与执行分开保存；不能把断言总数或未选中的用例算作执行覆盖。
        foreach (var suite in inventory["suites"]!.AsArray())
        {
            var selected = runs.Where(r => String(r, "suite") == String(suite!, "name")).ToArray();
            bool succeeded = selected.Any(Successful);
            if (selected.Any(ToolchainSkipped)) suite!["status"] = "skipped-toolchain";
            if (succeeded) suite!["status"] = selected.Any(r => r["groups"] is JsonArray { Count: > 0 }) ? "not-enumerable" : "executed";
            foreach (var entry in suite!["cases"]!.AsArray())
            {
                bool wasSelected = selected.Any(r => Successful(r) &&
                    (r["labels"] is not JsonArray labels || labels.Count == 0 || labels.Any(l => l!.GetValue<string>() == String(entry!, "label"))));
                if (wasSelected && String(entry!, "status") != "skipped-toolchain" &&
                    (String(entry!, "status") != "gate-excluded" || selected.Any(r => r["labels"] is JsonArray labels && labels.Any(l => l!.GetValue<string>() == String(entry!, "label"))))) entry!["status"] = "executed";
            }
            if (selected.Length > 0)
            {
                suite["selectedRuns"] = selected.Length;
                suite["selectedGroups"] = Strings(selected.SelectMany(r => r["groups"]?.AsArray().Select(g => g!.GetValue<string>()) ?? []).Distinct());
            }
        }
        Save(Path.Combine(output, "coverage.json"), inventory);
        var summaries = new JsonArray();
        foreach (var command in commands)
        {
            var commandRuns = runs.Where(r => String(r, "name") == String(command, "name")).ToArray();
            var samples = commandRuns.Where(r => String(r, "condition") == "hot" && Successful(r)).Select(r => r["wallMilliseconds"]!.GetValue<double>()).ToArray();
            var failedRuns = commandRuns.Where(r => r["exitCode"]!.GetValue<int>() != 0).ToArray();
            var status = failedRuns.Length > 0 ? "failed" : samples.Length > 0 ? "executed" : commandRuns.Any(ToolchainSkipped) ? "skipped-toolchain" : commandRuns.Length == 0 ? "not-selected" : "not-executed";
            var summary = new JsonObject { ["name"] = String(command, "name"), ["category"] = String(command, "category"), ["hotSamples"] = samples.Length,
                ["quality"] = samples.Length >= 3 ? "baseline" : samples.Length > 0 ? "exploratory" : "no-successful-hot-samples", ["status"] = status,
                ["exitCode"] = failedRuns.Length > 0 ? failedRuns[0]["exitCode"]!.DeepClone() : commandRuns.Length > 0 ? JsonValue.Create(0) : null,
                ["skipReason"] = commandRuns.FirstOrDefault(r => r["observedSkip"]?.GetValue<bool>() == true)?["skipReason"]?.DeepClone() };
            if (samples.Length > 0) { summary["medianWallMilliseconds"] = Median(samples); summary["minWallMilliseconds"] = samples.Min(); summary["maxWallMilliseconds"] = samples.Max(); }
            summaries.Add(summary);
        }
        Save(Path.Combine(output, "summary.json"), new JsonObject { ["exitCode"] = failure, ["elapsedSeconds"] = budget.Elapsed.TotalSeconds, ["workloads"] = summaries });
        return failure == 0 ? 0 : Math.Clamp(failure, 1, 255);
    }

    private static JsonObject SuiteCommand(string name, IEnumerable<string> args, string exe, JsonNode? selection, string[] labels, string[] groups) => new()
    {
        ["name"] = "test-" + name + (labels.Length == 1 ? "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(labels[0])))[..8] : ""),
        ["category"] = "test", ["fileName"] = exe, ["arguments"] = Strings(args), ["suite"] = name,
        ["labels"] = Strings(labels), ["groups"] = Strings(groups), ["hotRuns"] = selection?["hotRuns"]?.DeepClone(),
    };

    private static string CpuModel()
    {
        if (OperatingSystem.IsLinux()) return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name")) ?? "unknown";
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    internal static async Task<JsonObject> Execute(string executable, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, int timeoutSeconds, string prefix)
    {
        if (timeoutSeconds < 1) throw new ArgumentException("timeout 必须为正数");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(prefix))!);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var isolation = new ProcessIsolation();
        var clock = Stopwatch.StartNew();
        var result = new JsonObject { ["fileName"] = executable, ["arguments"] = Strings(args), ["workingDirectory"] = workingDirectory,
            ["environmentOverrides"] = JsonSerializer.SerializeToNode(environment), ["timeoutSeconds"] = timeoutSeconds,
            ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["stdoutLog"] = prefix + ".stdout.log", ["stderrLog"] = prefix + ".stderr.log",
            ["rssSamplingIntervalMilliseconds"] = 100, ["processIsolation"] = isolation.Kind };
        ContainedProcess? process = null;
        Task? drain = null;
        int exit = 127; bool timedOut = false; long sampledPeak = 0; long? lifetimePeak = null; double? cpu = null;
        try
        {
            process = isolation.Start(info); result["processId"] = process.Id; process.StandardInput.Close();
            await using var stdout = File.Create(prefix + ".stdout.log");
            await using var stderr = File.Create(prefix + ".stderr.log");
            drain = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(stdout), process.StandardError.BaseStream.CopyToAsync(stderr));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var wait = process.WaitForExitAsync(cancellation.Token);
            try
            {
                while (!wait.IsCompleted)
                {
                    try { process.Refresh(); sampledPeak = Math.Max(sampledPeak, process.WorkingSet64); lifetimePeak = process.PeakWorkingSet64; cpu = process.TotalProcessorTime.TotalMilliseconds; }
                    catch (InvalidOperationException) { }
                    await Task.WhenAny(wait, Task.Delay(100, cancellation.Token));
                }
                await wait;
                await drain.WaitAsync(cancellation.Token);
                exit = process.ExitCode;
                try { cpu = process.TotalProcessorTime.TotalMilliseconds; lifetimePeak = Math.Max(lifetimePeak ?? 0, process.PeakWorkingSet64); } catch (InvalidOperationException) { }
            }
            catch (OperationCanceledException)
            {
                timedOut = true; exit = 124;
                // 只终止本工具创建的进程树，并等管道排空，避免重跑时残留占资源。
                isolation.Kill(process);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                try { await drain.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            await File.WriteAllTextAsync(prefix + ".stdout.log", "");
            await File.WriteAllTextAsync(prefix + ".stderr.log", exception.Message);
            result["startError"] = exception.Message;
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    isolation.Kill(process);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    if (drain != null) await drain.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally { process.Dispose(); }
            }
        }
        result["wallMilliseconds"] = clock.Elapsed.TotalMilliseconds;
        result["cpuMilliseconds"] = cpu; result["sampledPeakRssBytes"] = sampledPeak; result["processLifetimePeakRssBytes"] = lifetimePeak;
        result["exitCode"] = exit; result["timedOut"] = timedOut;
        result["status"] = timedOut ? "timed-out" : exit == 0 ? "completed" : "failed";
        return result;
    }

    internal static async Task<int> SelfTest()
    {
        var failures = new List<string>();
        void Check(bool condition, string label) { if (!condition) failures.Add(label); }
        Check(Median([9, 1, 3]) == 3 && Median([4, 1, 2, 3]) == 2.5, "median");
        var directory = Path.Combine(Path.GetTempPath(), "rigi-perf-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var failed = await Execute("/bin/sh", ["-c", "printf evidence; exit 7"], directory, new Dictionary<string, string>(), 3, Path.Combine(directory, "failed"));
                Check(failed["exitCode"]!.GetValue<int>() == 7 && await File.ReadAllTextAsync(String(failed, "stdoutLog")) == "evidence", "failure propagation/log");
                var timeout = await Execute("/bin/sh", ["-c", "sleep 30 & echo $! > child.pid; wait"], directory, new Dictionary<string, string>(), 1, Path.Combine(directory, "timeout"));
                Check(timeout["timedOut"]!.GetValue<bool>() && timeout["exitCode"]!.GetValue<int>() == 124, "timeout");
                int pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "child.pid")));
                var stat = "/proc/" + pid + "/stat";
                Check(!File.Exists(stat) || File.ReadAllText(stat).Split(' ')[2] == "Z", "timeout child cleanup");
                var orphan = await Execute("/bin/sh", ["-c", "sleep 30 & echo $! > orphan.pid; exit 0"], directory, new Dictionary<string, string>(), 1, Path.Combine(directory, "orphan"));
                int orphanPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "orphan.pid")));
                var orphanStat = "/proc/" + orphanPid + "/stat";
                Check(orphan["timedOut"]!.GetValue<bool>() && (!File.Exists(orphanStat) || File.ReadAllText(orphanStat).Split(' ')[2] == "Z"), "exited root/background pipe cleanup");
                var fake = Path.Combine(directory, "compiler.sh");
                await File.WriteAllTextAsync(fake, "if [ \"$2\" = \"--inventory\" ]; then\n" +
                    "echo '{\"schemaVersion\":1,\"suites\":[{\"name\":\"Probe\",\"number\":99,\"selector\":\"exact-label\",\"granularity\":\"case\",\"cases\":[{\"label\":\"known\",\"status\":\"not-selected\"}]}]}'\n" +
                    "else printf '%s\\n' \"$@\"; fi\n");
                var profilePath = Path.Combine(directory, "profile.json");
                var profile = new JsonObject
                {
                    ["repoRoot"] = Directory.GetCurrentDirectory(), ["hotRuns"] = 3, ["warmupRuns"] = 1,
                    ["compiler"] = new JsonObject { ["fileName"] = "/bin/sh", ["arguments"] = Strings([fake]) },
                    ["suites"] = new JsonArray(new JsonObject { ["name"] = "Probe", ["labels"] = Strings(["known"]) }),
                };
                Save(profilePath, profile);
                var selectedOutput = Path.Combine(directory, "selected");
                Check(await Run(profilePath, selectedOutput) == 0, "selected profile run");
                var rows = (await File.ReadAllLinesAsync(Path.Combine(selectedOutput, "runs.jsonl"))).Select(l => JsonNode.Parse(l)!).ToArray();
                Check(rows.Length == 5 && rows.All(r => r["arguments"]!.AsArray().Any(a => a!.GetValue<string>() == "99")), "dynamic suite number/repeats");
                profile["suites"]![0]!["labels"] = Strings(["unknown"]);
                Save(profilePath, profile);
                bool rejected = false;
                try { await Run(profilePath, Path.Combine(directory, "unknown")); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "unknown profile label rejected");
                profile["suites"] = new JsonArray();
                profile["commands"] = new JsonArray(new JsonObject { ["name"] = "fail", ["category"] = "test", ["fileName"] = "/bin/sh", ["arguments"] = Strings(["-c", "exit 7"]) });
                Save(profilePath, profile);
                Check(await Run(profilePath, Path.Combine(directory, "run-failure")) == 7, "run failure propagation");
                var failedSummary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "run-failure", "summary.json")))!["workloads"]![0]!;
                Check(String(failedSummary, "status") == "failed" && failedSummary["exitCode"]!.GetValue<int>() == 7 && failedSummary["hotSamples"]!.GetValue<int>() == 0, "failed summary has exit code");
                await File.WriteAllTextAsync(fake, "if [ \"$2\" = \"--inventory\" ]; then\n" +
                    "echo '{\"suites\":[{\"name\":\"NativeE2E\",\"number\":99,\"selector\":\"name-filter\",\"toolchainAvailable\":false,\"cases\":[{\"label\":\"known\",\"status\":\"skipped-toolchain\"}]}]}'\n" +
                    "else echo '（跳过：未找到 clang 工具链；开发机配置后生效）'; fi\n");
                profile["commands"] = new JsonArray(); profile["hotRuns"] = 1;
                profile["suites"] = new JsonArray(new JsonObject { ["name"] = "NativeE2E", ["labels"] = Strings(["known"]) });
                Save(profilePath, profile);
                var skipDirectory = Path.Combine(directory, "skip");
                Check(await Run(profilePath, skipDirectory) == 0, "skip run exit preserved");
                var skipSummary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(skipDirectory, "summary.json")))!["workloads"]![0]!;
                Check(String(skipSummary, "status") == "skipped-toolchain" && skipSummary["hotSamples"]!.GetValue<int>() == 0 && skipSummary["medianWallMilliseconds"] == null,
                    "skip excluded from baseline median");
                Check((await File.ReadAllLinesAsync(Path.Combine(skipDirectory, "runs.jsonl"))).Length == 3, "suite inherits hotRuns=1");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine(failures.Count == 0 ? "PerfBaseline self-test: PASS" : "PerfBaseline self-test: FAIL " + string.Join(", ", failures));
        return failures.Count;
    }
}
