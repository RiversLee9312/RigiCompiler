using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// e2e 语料套件（Q27 方案，git 跟踪）：扫描 Tests/e2e/rigi 下的 .rg 语料，
    /// 经进程内全管线（编译 → BIL → VM 执行）跑端到端断言。
    ///
    /// 语料组织：
    ///   rigi/*.rg        —— 单文件用例（文件名为用例名）；
    ///   rigi/&lt;目录&gt;/*.rg —— 多文件用例（同目录即同组，组内全部 .rg 按文件名
    ///                        序一起编译，目录名为用例名）。
    ///
    /// 旁注约定（写在组内任一 .rg 文件的注释行里，按文件名序合并）：
    ///   // expect-output: &lt;一行 stdout&gt;   可多条，按序拼成期望 stdout
    ///                                     （无此旁注 = 期望 stdout 为空）；
    ///   // expect-exit: &lt;整数&gt;            断言 main 返回的 i32 退出码（可省略）；
    ///   // expect-error: &lt;诊断子串&gt;       可多条；出现即负例——断言编译失败
    ///                                     且每条子串都能在 Error 级诊断中找到。
    ///   // e2e-slow-gate: &lt;理由&gt;        慢速压力用例门控（慢例评审，块 4-3
    ///                                     收尾）：默认套件跑（RunAll/数值区间，
    ///                                     含并行子进程——子进程继承环境变量，
    ///                                     过滤口径一致、区间索引不错位）跳过
    ///                                     该用例；环境变量 RIGI_E2E_SLOW=1
    ///                                     时并入默认跑。按用例名过滤（非数字
    ///                                     suite-args）不受门控限制，便于单例
    ///                                     调试，会打印提示行。
    ///
    /// 套件支持 --suite-args &lt;子串...&gt; 按用例名过滤（便于单条调试）。
    /// </summary>
    public static class E2eCorpusTests
    {
        // 单个用例：一组源文件 + 旁注期望
        private sealed class E2eCase
        {
            public required string Name { get; init; }
            public required List<string> Files { get; init; }
            public List<string> ExpectOutputs { get; } = new();
            public int? ExpectExit { get; set; }
            public List<string> ExpectErrors { get; } = new();
            public bool IsNegative => ExpectErrors.Count > 0;
            // 慢速压力用例（e2e-slow-gate 旁注）：默认套件跑跳过，
            // RIGI_E2E_SLOW=1 或按名显式过滤时仍运行
            public bool SlowGate { get; set; }
        }

        internal static void RunExactCase(string name)
        {
            var kase = Discover().SingleOrDefault(c => c.Name == name)
                ?? throw new ArgumentException($"未知语料: {name}");
            RunCase(kase);
        }

        // 慢速门控开关：环境变量 RIGI_E2E_SLOW=1 启用默认跑
        private static bool SlowGateEnabled =>
            string.Equals(Environment.GetEnvironmentVariable("RIGI_E2E_SLOW"), "1",
                StringComparison.Ordinal);

        public static int RunAll()
        {
            if (Discover().Count == 0)
            {
                return FailNoCases();
            }
            return ParallelSuiteRunner.RunAll(Spec);
        }

        // --suite-args 双模：两个非负整数且 from<=to → 数值区间（基座可并行）；
        // 否则按用例名子串过滤（恒进程内，单例调试语义）
        public static int RunWithArgs(IReadOnlyList<string> args)
        {
            if (args.Count >= 2
                && int.TryParse(args[0], out var from)
                && int.TryParse(args[1], out var to)
                && from >= 0 && to >= from)
            {
                return ParallelSuiteRunner.RunWithArgs(Spec, args);
            }
            return RunNameFilter(args);
        }

        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Discover().Select((entry, index) => new TestInventory.Case(index, entry.Name, entry.SlowGate, "RIGI_E2E_SLOW"));

        internal static ParallelSuiteRunner.SuiteSpec ExecutionSpec => new("E2e", Discover()
            .Select(kase => (kase.Name, (Action)(() => RunCase(kase)))).ToArray(), sectionTitle: "E2e");

        internal static ParallelSuiteRunner.SuiteSpec Spec
        {
            get
            {
                var cases = new List<(string Label, Action Run)>();
                var skipped = new List<string>();
                foreach (var kase in Discover())
                {
                    if (kase.SlowGate && !SlowGateEnabled)
                    {
                        skipped.Add(kase.Name);
                        continue;
                    }
                    var captured = kase;
                    cases.Add((captured.Name, () => RunCase(captured)));
                }
                // 门控跳过只在非派生进程汇报一次（子进程同样过滤，口径一致，
                // 但汇报行重复 16 份只会刷屏）
                if (skipped.Count > 0 && !TestRunner.IsSpawned)
                {
                    Console.WriteLine(
                        $"  [slow-gate] e2e 慢速压力用例跳过 {skipped.Count} 例" +
                        $"（{string.Join(", ", skipped)}）；RIGI_E2E_SLOW=1 并入默认跑，" +
                        "或 test --run 56 --suite-args <名字> 单独调试");
                }
                return new ParallelSuiteRunner.SuiteSpec("E2e", cases, sectionTitle: "E2e");
            }
        }

        private static int RunNameFilter(IReadOnlyList<string> filters)
        {
            TestHarness.Reset();
            TestHarness.Section("E2e");
            var stopwatch = Stopwatch.StartNew();
            var cases = Discover().Where(c => filters.Any(f =>
                c.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
            if (cases.Count == 0)
            {
                TestHarness.CheckTrue("语料发现", false,
                    $"未找到任何用例（语料根: {CorpusRoot()}）");
                return TestHarness.Summary("E2e");
            }
            foreach (var kase in cases)
            {
                // 按名显式过滤不受慢速门控限制（单例调试语义），但给出提示
                if (kase.SlowGate && !SlowGateEnabled)
                {
                    Console.WriteLine(
                        $"  [slow-gate] {kase.Name} 为慢速压力用例（RIGI_E2E_SLOW=1 默认并入）");
                }
                RunCase(kase);
            }
            stopwatch.Stop();
            Console.WriteLine($"  （e2e 语料 {cases.Count} 条，耗时 {stopwatch.ElapsedMilliseconds} ms）");
            return TestHarness.Summary("E2e");
        }

        private static int FailNoCases()
        {
            TestHarness.Reset();
            TestHarness.Section("E2e");
            TestHarness.CheckTrue("语料发现", false,
                $"未找到任何用例（语料根: {CorpusRoot()}）");
            return TestHarness.Summary("E2e");
        }

        // 输出/发布语料优先；开发源码回退由统一定位器控制。
        private static string CorpusRoot([CallerFilePath] string selfPath = "") =>
            TestCorpusPaths.Resolve("Tests/e2e/rigi", selfPath);

        // 发现全部用例：根下散文件为单文件用例，每个子目录为一组多文件用例
        private static List<E2eCase> Discover()
        {
            var root = CorpusRoot();
            var cases = new List<E2eCase>();
            if (!Directory.Exists(root))
            {
                return cases;
            }
            foreach (var file in Directory.GetFiles(root, "*.rg")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                cases.Add(LoadCase(Path.GetFileNameWithoutExtension(file),
                    new List<string> { file }));
            }
            foreach (var dir in Directory.GetDirectories(root)
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var files = Directory.GetFiles(dir, "*.rg")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count > 0)
                {
                    cases.Add(LoadCase(Path.GetFileName(dir), files));
                }
            }
            return cases;
        }

        // 组装用例并解析全部源文件中的旁注（按文件名序合并）
        private static E2eCase LoadCase(string name, List<string> files)
        {
            var kase = new E2eCase { Name = name, Files = files };
            foreach (var file in files)
            {
                ParseAnnotations(kase, file);
            }
            return kase;
        }

        // 逐行扫描注释旁注：expect-output / expect-exit / expect-error
        private static void ParseAnnotations(E2eCase kase, string file)
        {
            foreach (var rawLine in File.ReadLines(file))
            {
                var line = rawLine.TrimEnd('\r').TrimStart();
                if (!line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                var comment = line.Substring(2).TrimStart();
                if (TryAnnotation(comment, "expect-output:", out var outputLine))
                {
                    kase.ExpectOutputs.Add(outputLine);
                }
                else if (TryAnnotation(comment, "expect-error:", out var errorPart))
                {
                    kase.ExpectErrors.Add(errorPart);
                }
                else if (TryAnnotation(comment, "e2e-slow-gate:", out _))
                {
                    // 慢速压力用例门控：理由文本只作文档，不参与逻辑
                    kase.SlowGate = true;
                }
                else if (TryAnnotation(comment, "expect-exit:", out var exitText))
                {
                    kase.ExpectExit = int.Parse(exitText.Trim());
                }
            }
        }

        // 匹配旁注键并取值：键后恰好一个引导空格被剥掉，其余内容原样保留
        //（空值合法——expect-output: 空串表示期望一行空输出）
        private static bool TryAnnotation(string comment, string key, out string value)
        {
            if (comment.StartsWith(key, StringComparison.Ordinal))
            {
                var rest = comment.Substring(key.Length);
                if (rest.StartsWith(" ", StringComparison.Ordinal))
                {
                    rest = rest.Substring(1);
                }
                value = rest;
                return true;
            }
            value = "";
            return false;
        }

        // 执行单个用例：stdlib + 组内全部源文件 → P1 → P2 → P3；
        // 负例在 P3 后断言诊断，正例继续 P4a → P4b → VM 运行断言
        private static void RunCase(E2eCase kase)
        {
            try
            {
                var roots = new List<RootASTNode>();
                roots.AddRange(StdlibSources.ParseAll());
                roots.AddRange(Frontend.ParseRoots(kase.Files.Select(file =>
                    new SourceInput(File.ReadAllText(file), Path.GetFileName(file))).ToArray()));
                var unit = new CompilationUnit(roots.ToArray());
                var declarations = DeclarationCollector.Collect(unit);
                DeclarationResolver.Resolve(unit, declarations);
                var bodies = Binder.Bind(unit, declarations);

                if (kase.IsNegative)
                {
                    CheckNegative(kase, unit);
                    return;
                }

                if (unit.Diagnostics.HasErrors)
                {
                    TestHarness.CheckTrue(kase.Name + "：编译零诊断", false,
                        DescribeErrors(unit));
                    return;
                }
                TestHarness.CheckTrue(kase.Name + "：编译零诊断", true);

                var lowered = Lowerer.Lower(unit, bodies);
                var module = BilEmitter.Emit(unit, lowered, SanitizeModuleName(kase.Name));
                var result = BilVm.Run(module);

                if (result.Exception != null)
                {
                    TestHarness.CheckTrue(kase.Name + "：VM 无异常", false,
                        result.Exception.ToString());
                    return;
                }
                TestHarness.CheckTrue(kase.Name + "：VM 无异常", true);

                var expectedStdout = kase.ExpectOutputs.Count == 0
                    ? ""
                    : string.Join("\n", kase.ExpectOutputs) + "\n";
                TestHarness.Check(kase.Name + "：stdout", result.Stdout, expectedStdout);

                if (kase.ExpectExit.HasValue)
                {
                    TestHarness.CheckTrue(kase.Name + $"：退出码 {kase.ExpectExit.Value}",
                        result.ReturnValue is VmI32 n && n.Value == kase.ExpectExit.Value,
                        result.ReturnValue?.ToStandardText() ?? "<null>");
                }
            }
            catch (Exception exception)
            {
                TestHarness.CheckTrue(kase.Name, false, exception.ToString());
            }
        }

        // 负例断言：编译必须有 Error 级诊断，且每条 expect-error 子串都有命中
        private static void CheckNegative(E2eCase kase, CompilationUnit unit)
        {
            var errors = unit.Diagnostics.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count == 0)
            {
                TestHarness.CheckTrue(kase.Name + "：编译失败（负例）", false,
                    "编译未报任何错误");
                return;
            }
            TestHarness.CheckTrue(kase.Name + "：编译失败（负例）", true);
            var actual = string.Join("; ", errors.Select(d => $"{d.Phase}: {d.Message}"));
            foreach (var expected in kase.ExpectErrors)
            {
                TestHarness.CheckTrue(kase.Name + $"：诊断含 [{expected}]",
                    errors.Any(d => d.Message.Contains(expected)), actual);
            }
        }

        private static string DescribeErrors(CompilationUnit unit)
        {
            return string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                d => $"{d.Phase} {d.Severity}: {d.Message}"));
        }

        // 用例名转 BIL 模块名（目录名/连字符等非字母数字字符替换为下划线）
        private static string SanitizeModuleName(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars);
        }
    }
}
