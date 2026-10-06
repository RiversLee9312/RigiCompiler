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
    /// 合法程序再过 BilVm 执行（生成器不产循环/递归；VM 仍加步数上限兜底）。
    ///
    /// 逐用例不变量（任一违反即失败并落盘）：
    /// 1. 编译器永不崩溃——任何异常逃逸都是 bug（含 VM 托管异常）；
    /// 2. 合法程序（ExpectError=false 且非软 miss）必须零 Error、过 verifier、VM 无异常；
    /// 3. 注入负例（ExpectError=true）必须报 Error 诊断而非崩溃/静默通过；
    /// 3b. 软 miss（SoftMiss）：事先不断言合法/非法；只要求不崩溃，
    ///     若编译通过则 verifier 过 + VM 不崩（步数上限受控终止算不崩）；
    /// 4. 诊断不重复刷屏——同 (阶段, 级别, 位置, 消息) 不得出现两次；
    /// 5. 诊断确定性——每 60 例抽 1 例编译两次，诊断序列逐字一致。
    ///
    /// 失败落盘：Tests/fuzz-failures/fail-case#{i}-*.rg + .txt（含领域/
    /// 模板/问题/复现命令）；复现：dotnet run -- test --run N --suite-args i i
    /// （N 为 StressFuzz 套件号，单例区间 caseCount=1 会打印完整源码）。
    ///
    /// 确定性：用例 i 由 (Seed, i) 唯一确定（每用例独立种子，与区间无关）。
    /// 并行：非 spawned 入口进入共同 dispatcher 的稀疏全局索引小批；
    /// spawned 只执行本批动作，绝不二次派生。
    /// 规模：默认 3000；RIGI_STRESSFUZZ_CASES 覆盖；GitHub Actions
    /// （GITHUB_ACTIONS=true）默认冒烟 600，ci.yml 无需改动。
    /// </summary>
    public static class StressFuzzTests
    {
        internal const int Seed = 20260821;
        internal const long VmMaxSteps = 100_000;
        private const int DefaultCaseCount = 3000;
        private const int CiSmokeCaseCount = 600;  // CI 冒烟量（无 env 时）
        private const int DeterminismEvery = 60;
        private const int ProgressEvery = 25;


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
        private static int softMissCases;      // 软 miss 用例
        private static int softMissCompiled;   // 软 miss 且编译通过
        private static readonly Dictionary<string, int> domainFrequency = new();
        private static readonly Dictionary<string, int> messageFrequency = new();

        private static readonly List<string> failureLog = new();
        private static int dumpedFailures;     // 落盘计数（上限保护）

        // --suite-args <from> <to>：含两端的 case 序号区间（种子固定，区间可复现）


        internal static int RunSelected(IReadOnlyList<int> indices)
        {
            int from = indices.Min(), to = indices.Max();
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Stress Fuzz Tests (P20)           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");
            Console.Out.Flush();

            crashes = parseFailures = verifierFailures = vmFailures = 0;
            legalRejected = injectionMissed = nondeterministic = duplicateDiagnostics = 0;
            cleanCases = errorCases = softMissCases = softMissCompiled = 0;
            domainFrequency.Clear();
            messageFrequency.Clear();
            failureLog.Clear();
            dumpedFailures = 0;

            int caseCount = indices.Count;
            ReportProgress($"StressFuzz 全局索引 {string.Join(',', indices)}（跨度 case#{from}..#{to}）（种子 {Seed}，共 {caseCount} 例）");

            var stopwatch = Stopwatch.StartNew();
            int ran = 0;
            foreach (int i in indices)
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
                $"{CaseAssertions.Current.PassedCount} passed, {CaseAssertions.Current.FailureCount} failed，耗时 {stopwatch.ElapsedMilliseconds} ms");
            Console.WriteLine($"  分类：编译器/VM 崩溃 {crashes} / 前端或生成器异常 {parseFailures} / " +
                $"BIL 验证失败 {verifierFailures} / VM 异常 {vmFailures} / " +
                $"合法被拒 {legalRejected} / 注入未报 {injectionMissed} / " +
                $"诊断不确定 {nondeterministic} / 重复诊断 {duplicateDiagnostics}");
            Console.WriteLine($"  覆盖：合法零诊断 {cleanCases} / 含 Error {errorCases} / " +
                $"软miss {softMissCases}（其中编译通过 {softMissCompiled}）；领域分布：");
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
            Console.WriteLine($"=== Stress Fuzz Tests Complete: {CaseAssertions.Current.PassedCount} passed, {CaseAssertions.Current.FailureCount} failed ===");
            Console.Out.Flush();
            return CaseAssertions.Current.FailureCount;
        }

        private static void ReportProgress(string message)
        {
            Console.WriteLine("  [progress] " + message);
            Console.Out.Flush();
            Logger.Verbose("StressFuzz", message);
        }

        private static void DumpSource(int index, FuzzCase kase)
        {
            string expect = kase.SoftMiss
                ? "软miss（不崩溃；通过则 verifier+VM）"
                : (kase.ExpectError ? "Error" : "零诊断 + VM");
            Console.WriteLine($"  [dump] case#{index}（种子 {Seed}，领域 {kase.Domain}/{kase.Template}，" +
                $"期望 {expect}）：");
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
                roots.Add(CompilerTestTools.ParseRoot(source, name));
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
            if (kase.SoftMiss) softMissCases++;
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

            // 不变量 2/3：期望与诊断对齐（软 miss 事先不断言合法/非法，跳过）
            if (ok && !kase.SoftMiss && kase.ExpectError && !unit!.Diagnostics.HasErrors)
            {
                // 注入负例静默通过——错误注入必然非法，漏报即编译器 bug
                injectionMissed++;
                Fail(index, kase, "错误注入未报任何 Error 诊断（应拒绝却通过）");
                ok = false;
            }
            if (ok && !kase.SoftMiss && !kase.ExpectError && unit!.Diagnostics.HasErrors)
            {
                legalRejected++;
                Fail(index, kase, "合法程序被报 Error: " +
                    string.Join("; ", unit.Diagnostics.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"{d.Phase}: {d.Message}")));
                ok = false;
            }
            if (ok && kase.SoftMiss && !unit!.Diagnostics.HasErrors) softMissCompiled++;

            // 不变量 2 续：合法或软 miss 编译通过 ⇒ BIL 必须过 BilVerifier
            if (ok && !unit!.Diagnostics.HasErrors)
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

            // 不变量 2 续：编译通过 ⇒ VM 执行（步数上限兜底；软 miss 受控终止不算崩）
            if (ok && !unit!.Diagnostics.HasErrors)
            {
                BilVmResult result;
                try
                {
                    result = BilVm.Run(module!, VmMaxSteps);
                }
                catch (Exception ex)
                {
                    crashes++;
                    Fail(index, kase, $"BilVm 托管崩溃 {ex.GetType().Name}: {ex.Message}\n{FirstFrames(ex)}");
                    result = new BilVmResult("", "", null, null);
                    ok = false;
                }
                if (ok && result.Exception != null && !kase.SoftMiss)
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

            if (ok) CaseAssertions.Record(true); else CaseAssertions.Record(false);
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
            int suiteNumber = TestSuiteCatalog.GetNumber("StressFuzz");
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
                int suiteNumber = TestSuiteCatalog.GetNumber("StressFuzz");
                File.WriteAllText(Path.Combine(dir, $"fail-case{index}.txt"),
                    $"case#{index} 种子 {Seed}\n" +
                    $"领域: {kase.Domain}/{kase.Template}\n" +
                    $"期望: {(kase.SoftMiss ? "软miss（不崩溃；通过则 verifier+VM）" : (kase.ExpectError ? "Error 诊断" : "零诊断 + verifier + VM"))}\n" +
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
