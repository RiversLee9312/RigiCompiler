using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 压力 fuzz 测试（P20：错误注入 + 随机程序生成，覆盖本轮修复的 bug
    /// 领域）。与 SemanticsFuzz 互补：那边打重载/默认值/具名实参面，
    /// 这边打泛型×可空、继承×wrapper、嵌套 struct 写穿、接口菱形/like、
    /// 构造体系、shared/async、模块/访问七大领域（生成器见
    /// StressFuzzGenerator，模板源自 e2e 语料的参数化变体）。
    ///
    /// 驱动管线：parse（P1）→ 语义（P1–P3）→ emit BIL（P4）→ BilVerifier；
    /// 合法程序再过 BilVm 执行（生成器不产循环/递归，无死循环风险）。
    ///
    /// 逐用例不变量（任一违反即失败并落盘）：
    /// 1. 编译器永不崩溃——任何异常逃逸都是 bug（含 VM 托管异常）；
    /// 2. 合法程序（ExpectError=false）必须零 Error、过 verifier、VM 无异常；
    /// 3. 注入负例（ExpectError=true）必须报 Error 诊断而非崩溃/静默通过；
    /// 4. 诊断不重复刷屏——同 (阶段, 级别, 位置, 消息) 不得出现两次；
    /// 5. 诊断确定性——每 60 例抽 1 例编译两次，诊断序列逐字一致。
    ///
    /// 失败落盘：Tests/fuzz-failures/fail-case#{i}-*.rg + .txt（含领域/
    /// 模板/问题/复现命令）；复现：dotnet run -- test --run N --suite-args i i
    /// （N 为 StressFuzz 套件号，单例区间 caseCount=1 会打印完整源码）。
    ///
    /// 确定性：用例 i 由 (Seed, i) 唯一确定（每用例独立种子，与区间无关）。
    /// 并行：区间 >100 且非 --spawned 时切 min(ProcessorCount, caseCount)
    /// 个连续子区间，派生 `dotnet exec <dll>` 子进程并行。
    /// 规模：默认 3000；RIGI_STRESSFUZZ_CASES 覆盖；GitHub Actions
    /// （GITHUB_ACTIONS=true）默认冒烟 600，ci.yml 无需改动。
    /// </summary>
    public static class StressFuzzTests
    {
        internal const int Seed = 20260821;
        private const int DefaultCaseCount = 3000;
        private const int CiSmokeCaseCount = 600;  // CI 冒烟量（无 env 时）
        private const int DeterminismEvery = 60;
        private const int ProgressEvery = 25;
        private const int SpawnThreshold = 100;

        private static int passCount;
        private static int failCount;
        // 异常分类计数（crash = 编译器/VM bug；parseFailure = 生成器或前端 bug）
        private static int crashes;
        private static int parseFailures;
        private static int verifierFailures;
        private static int vmFailures;
        private static int legalRejected;      // 合法程序被报 Error（编译器或生成器 bug）
        private static int injectionMissed;    // 注入负例静默通过（编译器 bug）
        private static int nondeterministic;
        private static int duplicateDiagnostics;

        // 覆盖统计（证明合法/非法两路都充分命中）
        private static int cleanCases;         // 合法且零诊断
        private static int errorCases;         // 含 Error 诊断
        private static readonly Dictionary<string, int> domainFrequency = new();
        private static readonly Dictionary<string, int> messageFrequency = new();

        private static readonly List<string> failureLog = new();
        private static int dumpedFailures;     // 落盘计数（上限保护）

        // --suite-args <from> <to>：含两端的 case 序号区间（种子固定，区间可复现）
        public static int RunWithArgs(IReadOnlyList<string> args)
        {
            if (args.Count != 2
                || !int.TryParse(args[0], out int from)
                || !int.TryParse(args[1], out int to)
                || from < 0 || to < from)
            {
                Console.Error.WriteLine(
                    "StressFuzz --suite-args 需要 <from> <to>（含两端的 case 序号，from>=0 且 to>=from）");
                Console.Error.Flush();
                return 1;
            }
            return RunRange(from, to);
        }

        public static int RunAll()
        {
            int caseCount = DefaultCaseCount;
            // CI（GitHub Actions）默认冒烟量，本地默认全量；env 显式覆盖优先
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                caseCount = CiSmokeCaseCount;
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("RIGI_STRESSFUZZ_CASES"),
                    out int overrideCount) && overrideCount > 0)
            {
                caseCount = overrideCount;
            }
            return RunRange(0, caseCount - 1);
        }

        private static int RunRange(int from, int to)
        {
            int caseCount = to - from + 1;
            // 并行门槛：区间 > 100 且本进程不是被派生的测试子进程（--spawned）
            if (caseCount > SpawnThreshold && !TestRunner.IsSpawned)
                return RunParallel(from, to);
            return RunInProcess(from, to);
        }

        private static int RunInProcess(int from, int to)
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Stress Fuzz Tests (P20)           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");
            Console.Out.Flush();

            passCount = failCount = 0;
            crashes = parseFailures = verifierFailures = vmFailures = 0;
            legalRejected = injectionMissed = nondeterministic = duplicateDiagnostics = 0;
            cleanCases = errorCases = 0;
            domainFrequency.Clear();
            messageFrequency.Clear();
            failureLog.Clear();
            dumpedFailures = 0;

            int caseCount = to - from + 1;
            ReportProgress($"StressFuzz 区间 case#{from}..#{to}（种子 {Seed}，共 {caseCount} 例）");

            var stopwatch = Stopwatch.StartNew();
            int ran = 0;
            for (int i = from; i <= to; i++)
            {
                var kase = StressFuzzGenerator.Generate(i);
                // 进度打在 RunCase 之前：卡死时能看到正在跑的编号
                if (ran % ProgressEvery == 0 || caseCount <= ProgressEvery)
                    ReportProgress($"StressFuzz 开始 case#{i}（种子 {Seed}，本区间已跑 {ran}/{caseCount}）");
                if (caseCount <= 10)
                    DumpSource(i, kase);
                RunCase(i, kase);
                ran++;
            }
            stopwatch.Stop();

            Console.WriteLine($"  fuzz 汇总：{caseCount} 用例（种子 {Seed}），" +
                $"{passCount} passed, {failCount} failed，耗时 {stopwatch.ElapsedMilliseconds} ms");
            Console.WriteLine($"  分类：编译器/VM 崩溃 {crashes} / 前端或生成器异常 {parseFailures} / " +
                $"BIL 验证失败 {verifierFailures} / VM 异常 {vmFailures} / " +
                $"合法被拒 {legalRejected} / 注入未报 {injectionMissed} / " +
                $"诊断不确定 {nondeterministic} / 重复诊断 {duplicateDiagnostics}");
            Console.WriteLine($"  覆盖：合法零诊断 {cleanCases} / 含 Error {errorCases}；领域分布：");
            foreach (var pair in domainFrequency.OrderByDescending(p => p.Value))
            {
                Console.WriteLine($"      ×{pair.Value}  {pair.Key}");
            }
            Console.WriteLine("  诊断消息 Top8：");
            foreach (var pair in messageFrequency.OrderByDescending(p => p.Value).Take(8))
            {
                Console.WriteLine($"      ×{pair.Value}  {pair.Key}");
            }
            foreach (string line in failureLog.Take(10))
            {
                Console.WriteLine(line);
            }
            if (failureLog.Count > 10)
            {
                Console.WriteLine($"  ...（其余 {failureLog.Count - 10} 条省略）");
            }
            Console.WriteLine($"=== Stress Fuzz Tests Complete: {passCount} passed, {failCount} failed ===");
            Console.Out.Flush();
            return failCount;
        }

        // ===== 并行派生（仅父进程）：总区间切连续不重叠子区间，并集 = 完整区间 =====

        private static int RunParallel(int from, int to)
        {
            int caseCount = to - from + 1;
            // 用注册名找套件号，不硬编码：注册表顺序调整后仍然稳妥
            int suiteNumber = TestRunner.GetSuiteNumber("StressFuzz");
            if (suiteNumber < 1)
            {
                Console.Error.WriteLine("StressFuzz 并行化：找不到 StressFuzz 套件编号，回退进程内执行");
                return RunInProcess(from, to);
            }

            int workerCount = Math.Min(Environment.ProcessorCount, caseCount);
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Stress Fuzz Tests (P20)           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");
            Console.Out.Flush();
            ReportProgress($"StressFuzz 并行化：区间 case#{from}..#{to}（种子 {Seed}，共 {caseCount} 例），" +
                $"切 {workerCount} 个子进程（阈值 >{SpawnThreshold}）");

            // 确定性切分：前 caseCount % workerCount 个子进程各多领 1 例
            var children = new List<ChildResult>();
            int start = from;
            for (int w = 0; w < workerCount; w++)
            {
                int len = caseCount / workerCount + (w < caseCount % workerCount ? 1 : 0);
                int childFrom = start;
                int childTo = start + len - 1;
                start += len;
                // 先全部启动再统一等待：子进程真正并行执行
                children.Add(StartChild(suiteNumber, childFrom, childTo));
            }
            foreach (var child in children)
            {
                WaitChild(child);
            }

            int totalPass = 0;
            int totalFail = 0;
            var failedChildren = new List<ChildResult>();
            Console.WriteLine("  [parallel] 子进程汇总：");
            foreach (var child in children)
            {
                var completion = ParseCompletion(child.Stdout);
                bool ok = child.StartError == null && !child.TimedOut && child.ExitCode == 0
                    && completion is { Fail: 0 };
                if (ok)
                {
                    totalPass += completion!.Value.Pass;
                    Console.WriteLine($"    case#{child.From}..{child.To}（{child.To - child.From + 1} 例）：" +
                        $"{completion.Value.Pass} passed");
                }
                else
                {
                    // 有完成统计则精确累计；无（起不来/超时/崩溃）整段按失败计
                    if (completion != null)
                    {
                        totalPass += completion.Value.Pass;
                        totalFail += completion.Value.Fail;
                    }
                    else
                    {
                        totalFail += child.To - child.From + 1;
                    }
                    failedChildren.Add(child);
                }
            }

            foreach (var child in failedChildren)
            {
                Console.WriteLine($"  [FAIL] case#{child.From}..{child.To}：{DescribeChildFailure(child)}");
                PrintChildTail("stdout", child.Stdout);
                PrintChildTail("stderr", child.Stderr);
            }

            Console.WriteLine($"=== Stress Fuzz Tests Complete: {totalPass} passed, {totalFail} failed ===");
            Console.Out.Flush();
            return totalFail;
        }

        private static ChildResult StartChild(int suiteNumber, int childFrom, int childTo)
        {
            var child = new ChildResult { From = childFrom, To = childTo };

            // dotnet run 场景下 Environment.ProcessPath 指向 dotnet 宿主；
            // 取入口 dll 路径，用 `dotnet exec <dll>` 起子进程最稳妥
            string assemblyPath;
            try
            {
                // 单文件发布下 Location 为空字符串属预期，下方有分支处理
#pragma warning disable IL3000
                assemblyPath = Assembly.GetEntryAssembly()?.Location
                    ?? throw new InvalidOperationException("GetEntryAssembly() 为 null");
#pragma warning restore IL3000
            }
            catch (Exception ex)
            {
                child.StartError = $"获取编译器 dll 路径失败：{ex.Message}";
                return child;
            }

            var runViaDotnetExec = assemblyPath.Length > 0;
            if (!runViaDotnetExec)
            {
                assemblyPath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("ProcessPath 为 null");
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = runViaDotnetExec ? "dotnet" : assemblyPath,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (runViaDotnetExec)
                {
                    startInfo.ArgumentList.Add("exec");
                    startInfo.ArgumentList.Add(assemblyPath);
                }
                startInfo.ArgumentList.Add("test");
                startInfo.ArgumentList.Add("--run");
                startInfo.ArgumentList.Add(suiteNumber.ToString());
                startInfo.ArgumentList.Add("--suite-args");
                startInfo.ArgumentList.Add(childFrom.ToString());
                startInfo.ArgumentList.Add(childTo.ToString());
                startInfo.ArgumentList.Add("--spawned");

                child.Process = new Process { StartInfo = startInfo };
            }
            catch (Exception ex)
            {
                child.StartError = $"构建子进程启动信息失败：{ex.Message}";
                return child;
            }

            try
            {
                if (!child.Process.Start())
                {
                    child.StartError = "Process.Start 返回 false";
                    child.Process.Dispose();
                    child.Process = null;
                    return child;
                }
            }
            catch (Exception ex)
            {
                child.StartError = $"{ex.GetType().Name}: {ex.Message}";
                child.Process?.Dispose();
                child.Process = null;
                return child;
            }

            if (child.Process is not { } process)
            {
                child.StartError = "子进程句柄缺失";
                return child;
            }
            child.StdoutTask = process.StandardOutput.ReadToEndAsync();
            child.StderrTask = process.StandardError.ReadToEndAsync();
            int childCases = childTo - childFrom + 1;
            // 超时按每例 1s 估算（编译 + VM 实测约 250ms/例，留 4 倍余量），下限 120s
            child.TimeoutMs = Math.Max(120_000, childCases * 1000);
            return child;
        }

        private static void WaitChild(ChildResult child)
        {
            if (child.StartError != null || child.Process == null) return;

            var process = child.Process;
            if (!process.WaitForExit(child.TimeoutMs))
            {
                child.TimedOut = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    child.Stderr += $"\n[parent] 终止子进程失败：{ex.Message}";
                }
                if (!process.WaitForExit(10_000))
                {
                    child.ExitCode = -1;
                    child.Stdout = "";
                    child.Stderr += "\n[parent] 子进程在 Kill 后 10s 仍未退出，放弃读取输出";
                    process.Dispose();
                    return;
                }
            }

            child.ExitCode = process.ExitCode;
            child.Stdout = child.StdoutTask?.GetAwaiter().GetResult() ?? "";
            child.Stderr += child.StderrTask?.GetAwaiter().GetResult() ?? "";
            process.Dispose();
        }

        // 解析子进程 stdout 末尾的完成统计行："N passed, M failed"
        private static (int Pass, int Fail)? ParseCompletion(string stdout)
        {
            const string marker = "=== Stress Fuzz Tests Complete: ";
            int idx = stdout.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;

            string rest = stdout[(idx + marker.Length)..];
            int newline = rest.IndexOf('\n');
            string line = (newline < 0 ? rest : rest[..newline]).Trim();
            int end = line.IndexOf(" ===", StringComparison.Ordinal);
            if (end >= 0) line = line[..end].Trim();

            var parts = line.Split(',');
            if (parts.Length != 2) return null;
            var passParts = parts[0].Trim().Split(' ');
            var failParts = parts[1].Trim().Split(' ');
            if (passParts.Length == 0 || failParts.Length == 0) return null;
            if (!int.TryParse(passParts[0], out int pass)) return null;
            if (!int.TryParse(failParts[0], out int fail)) return null;
            return (pass, fail);
        }

        private static string DescribeChildFailure(ChildResult child)
        {
            if (child.StartError != null) return $"子进程启动失败：{child.StartError}";
            if (child.TimedOut) return $"子进程超时（>{child.TimeoutMs} ms），已终止";
            if (child.ExitCode != 0) return $"子进程退出码 {child.ExitCode}";
            return "子进程输出缺失完成统计";
        }

        private static void PrintChildTail(string streamName, string output)
        {
            var lines = output.Replace("\r", "").Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => l.Length > 0)
                .ToArray();
            if (lines.Length == 0) return;
            const int tailLines = 15;
            var tail = lines.Skip(Math.Max(0, lines.Length - tailLines)).ToArray();
            Console.WriteLine($"    [{streamName} 末尾 {tail.Length} 行]");
            foreach (var line in tail)
            {
                Console.WriteLine("      | " + line);
            }
        }

        private sealed class ChildResult
        {
            public int From;
            public int To;
            public int ExitCode = -1;
            public string Stdout = "";
            public string Stderr = "";
            public bool TimedOut;
            public int TimeoutMs;
            public string? StartError;
            public Process? Process;
            public Task<string>? StdoutTask;
            public Task<string>? StderrTask;
        }

        private static void ReportProgress(string message)
        {
            Console.WriteLine("  [progress] " + message);
            Console.Out.Flush();
            Logger.Verbose("StressFuzz", message);
        }

        private static void DumpSource(int index, FuzzCase kase)
        {
            Console.WriteLine($"  [dump] case#{index}（种子 {Seed}，领域 {kase.Domain}/{kase.Template}，" +
                $"期望 {(kase.ExpectError ? "Error" : "零诊断 + VM")}）：");
            foreach (var (name, source) in kase.Files)
            {
                Console.WriteLine($"    --- {name} ---");
                foreach (string line in source.Split('\n'))
                {
                    Console.WriteLine("      | " + line);
                }
            }
            Console.Out.Flush();
        }

        // ===== 单用例驱动与不变量断言 =====

        // 多根全管线：stdlib + 用例全部文件 → P1 → P2 → P3（→ P4 → BIL）
        private static (CompilationUnit Unit, BilModule? Module) CompileCase(FuzzCase kase)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            foreach (var (name, source) in kase.Files)
            {
                roots.Add(TestHarness.ParseRoot(source, name));
            }
            var unit = new CompilationUnit(roots.ToArray());
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            var bodies = Binder.Bind(unit, declarations);
            if (unit.Diagnostics.HasErrors)
            {
                return (unit, null);
            }
            var lowered = Lowerer.Lower(unit, bodies);
            var module = BilEmitter.Emit(unit, lowered, "stressfuzz");
            return (unit, module);
        }

        private static void RunCase(int index, FuzzCase kase)
        {
            domainFrequency[kase.Domain] = domainFrequency.GetValueOrDefault(kase.Domain) + 1;
            bool ok = true;   // 任一不变量违反即 false，出口统一计数
            CompilationUnit? unit = null;
            BilModule? module = null;
            try
            {
                (unit, module) = CompileCase(kase);
            }
            catch (Exception ex) when (ex is LexerException || ex is ParserException)
            {
                // 生成器只应产出合法语法——命中即生成器 bug 或前端 bug
                parseFailures++;
                Fail(index, kase, $"前端拒绝生成源 {ex.GetType().Name}: {ex.Message}");
                ok = false;
            }
            catch (Exception ex)
            {
                crashes++;
                Fail(index, kase, $"编译器崩溃 {ex.GetType().Name}: {ex.Message}\n{FirstFrames(ex)}");
                ok = false;
            }

            // 覆盖统计与消息频次
            if (ok)
            {
                if (unit!.Diagnostics.HasErrors) errorCases++;
                else if (unit.Diagnostics.Diagnostics.Count == 0) cleanCases++;
                foreach (var d in unit.Diagnostics.Diagnostics)
                {
                    string key = $"{d.Phase}: {d.Message}";
                    messageFrequency[key] = messageFrequency.GetValueOrDefault(key) + 1;
                }
            }

            // 不变量 2/3：期望与诊断对齐
            if (ok && kase.ExpectError && !unit!.Diagnostics.HasErrors)
            {
                // 注入负例静默通过——错误注入必然非法，漏报即编译器 bug
                injectionMissed++;
                Fail(index, kase, "错误注入未报任何 Error 诊断（应拒绝却通过）");
                ok = false;
            }
            if (ok && !kase.ExpectError && unit!.Diagnostics.HasErrors)
            {
                legalRejected++;
                Fail(index, kase, "合法程序被报 Error: " +
                    string.Join("; ", unit.Diagnostics.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"{d.Phase}: {d.Message}")));
                ok = false;
            }

            // 不变量 2 续：合法 ⇒ BIL 必须过 BilVerifier
            if (ok && !kase.ExpectError)
            {
                IReadOnlyList<BilVerificationError> errors;
                try
                {
                    errors = BilVerifier.Verify(module!);
                }
                catch (Exception ex)
                {
                    crashes++;
                    Fail(index, kase, $"BilVerifier 崩溃 {ex.GetType().Name}: {ex.Message}");
                    errors = new List<BilVerificationError>();
                    ok = false;
                }
                if (ok && errors.Count > 0)
                {
                    verifierFailures++;
                    Fail(index, kase, "零诊断但 BIL 验证失败: " +
                        string.Join("; ", errors.Select(e => e.ToString())));
                    ok = false;
                }
            }

            // 不变量 2 续：合法 ⇒ VM 执行不崩（生成器不产循环/递归，无挂死路径）
            if (ok && !kase.ExpectError)
            {
                BilVmResult result;
                try
                {
                    result = BilVm.Run(module!);
                }
                catch (Exception ex)
                {
                    crashes++;
                    Fail(index, kase, $"BilVm 托管崩溃 {ex.GetType().Name}: {ex.Message}\n{FirstFrames(ex)}");
                    result = new BilVmResult("", "", null, null);
                    ok = false;
                }
                if (ok && result.Exception != null)
                {
                    vmFailures++;
                    Fail(index, kase, "合法程序 VM 执行异常: " + result.Exception);
                    ok = false;
                }
            }

            // 不变量 4：同 (阶段, 级别, 位置, 消息) 诊断不得重复
            if (ok)
            {
                var seen = new HashSet<string>();
                foreach (var d in unit!.Diagnostics.Diagnostics)
                {
                    if (!seen.Add(KeyOf(d)))
                    {
                        duplicateDiagnostics++;
                        Fail(index, kase, "重复诊断: " + KeyOf(d));
                        ok = false;
                        break;
                    }
                }
            }

            // 不变量 5：确定性抽查（同一进程内编译两次，诊断序列逐字一致）
            if (ok && index % DeterminismEvery == 0)
            {
                string fingerprint = Fingerprint(unit!);
                CompilationUnit second;
                try
                {
                    (second, _) = CompileCase(kase);
                }
                catch (Exception ex)
                {
                    crashes++;
                    Fail(index, kase, $"确定性复查编译崩溃 {ex.GetType().Name}: {ex.Message}");
                    ok = false;
                    second = unit!;
                }
                if (ok && Fingerprint(second) != fingerprint)
                {
                    nondeterministic++;
                    Fail(index, kase, "两次编译诊断序列不一致");
                    ok = false;
                }
            }

            if (ok) passCount++; else failCount++;
        }

        private static string KeyOf(Diagnostic d)
        {
            return $"{d.Phase}|{d.Severity}|{SpanKey(d.Span)}|{d.Message}";
        }

        private static string SpanKey(CharRange? span)
        {
            return span == null
                ? "-"
                : $"{span.Value.Start.offset}-{span.Value.End.offset}";
        }

        private static string Fingerprint(CompilationUnit unit)
        {
            return string.Join("\n", unit.Diagnostics.Diagnostics.Select(KeyOf));
        }

        private static string FirstFrames(Exception ex)
        {
            var lines = (ex.StackTrace ?? "").Split('\n');
            return string.Join("\n", lines.Take(4));
        }

        // ===== 失败落盘（Tests/fuzz-failures/，git 已忽略）=====

        private static void Fail(int index, FuzzCase kase, string problem)
        {
            string dumpDir = DumpFailure(index, kase, problem);
            var sb = new StringBuilder();
            sb.Append($"  [FAIL] case#{index}（种子 {Seed}，{kase.Domain}/{kase.Template}）：{problem}\n");
            if (dumpDir.Length > 0)
            {
                sb.Append($"      落盘: {dumpDir}\n");
            }
            int suiteNumber = TestRunner.GetSuiteNumber("StressFuzz");
            sb.Append($"      复现: dotnet run -- test --run {suiteNumber} --suite-args {index} {index}\n");
            foreach (var (name, source) in kase.Files)
            {
                sb.Append($"      --- {name} ---\n");
                foreach (string line in source.Split('\n'))
                {
                    sb.Append("      | ").Append(line).Append('\n');
                }
            }
            failureLog.Add(sb.ToString());
        }

        // 失败用例源码与元信息写盘；返回落盘目录（失败则空串）。
        // 上限 200 个防洪水；目录不可写时回落 %TEMP%\rigi-fuzz-failures。
        private static string DumpFailure(int index, FuzzCase kase, string problem)
        {
            if (dumpedFailures >= 200) return "";
            try
            {
                string dir = Path.Combine(Directory.GetCurrentDirectory(),
                    "Tests", "fuzz-failures");
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                    dir = Path.Combine(Path.GetTempPath(), "rigi-fuzz-failures");
                    Directory.CreateDirectory(dir);
                }

                foreach (var (name, source) in kase.Files)
                {
                    string suffix = kase.Files.Length == 1
                        ? ".rg"
                        : "-" + name.Replace(".rg", "") + ".rg";
                    File.WriteAllText(
                        Path.Combine(dir, $"fail-case{index}{suffix}"), source);
                }
                int suiteNumber = TestRunner.GetSuiteNumber("StressFuzz");
                File.WriteAllText(Path.Combine(dir, $"fail-case{index}.txt"),
                    $"case#{index} 种子 {Seed}\n" +
                    $"领域: {kase.Domain}/{kase.Template}\n" +
                    $"期望: {(kase.ExpectError ? "Error 诊断" : "零诊断 + verifier + VM")}\n" +
                    $"问题: {problem}\n" +
                    $"复现: dotnet run -- test --run {suiteNumber} --suite-args {index} {index}\n");
                dumpedFailures++;
                return dir;
            }
            catch (Exception ex)
            {
                Logger.Verbose("StressFuzz", $"失败落盘异常：{ex.Message}");
                return "";
            }
        }
    }
}
