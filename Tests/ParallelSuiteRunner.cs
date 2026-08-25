using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 线性脚本套件的统一并行基座：case 数组（索引即 case 号）切连续
    /// 区间派生自身子进程。fuzz 两套件保持自有并行，不走本基座。
    /// caseCount&gt;1 且非 --spawned 时恒并行，子进程数 =
    /// min(ProcessorCount, caseCount)；单 case / spawned 进程内执行。
    /// </summary>
    public static class ParallelSuiteRunner
    {
        public sealed class SuiteSpec
        {
            public string SuiteName { get; }
            public IReadOnlyList<(string Label, Action Run)> Cases { get; }
            public string? SectionTitle { get; }
            public Action? BeforeSpawn { get; }

            public SuiteSpec(string suiteName, IReadOnlyList<(string Label, Action Run)> cases,
                string? sectionTitle = null, Action? beforeSpawn = null)
            {
                SuiteName = suiteName;
                Cases = cases;
                SectionTitle = sectionTitle;
                BeforeSpawn = beforeSpawn;
            }
        }

        public static int RunAll(SuiteSpec spec) =>
            RunRange(spec, 0, spec.Cases.Count - 1);

        public static int RunWithArgs(SuiteSpec spec, IReadOnlyList<string> args)
        {
            if (args.Count < 2
                || !int.TryParse(args[0], out var from)
                || !int.TryParse(args[1], out var to)
                || from < 0 || to < from || to >= spec.Cases.Count)
            {
                Console.Error.WriteLine(
                    $"{spec.SuiteName} --suite-args 需要 <from> <to>" +
                    $"（0 <= from <= to < {spec.Cases.Count}）");
                Console.Error.Flush();
                return 1;
            }
            return RunRange(spec, from, to);
        }

        public static int RunRange(SuiteSpec spec, int from, int to)
        {
            if (spec.Cases.Count == 0)
            {
                TestHarness.Reset();
                if (spec.SectionTitle != null)
                {
                    TestHarness.Section(spec.SectionTitle);
                }
                return TestHarness.Summary(spec.SuiteName);
            }
            var caseCount = to - from + 1;
            if (caseCount > 1 && !TestRunner.IsSpawned)
            {
                return RunParallel(spec, from, to);
            }
            return RunInProcess(spec, from, to);
        }

        private static int RunInProcess(SuiteSpec spec, int from, int to)
        {
            TestHarness.Reset();
            if (spec.SectionTitle != null)
            {
                TestHarness.Section(spec.SectionTitle);
            }
            for (var i = from; i <= to; i++)
            {
                spec.Cases[i].Run();
            }
            return TestHarness.Summary(spec.SuiteName);
        }

        private static int RunParallel(SuiteSpec spec, int from, int to)
        {
            var caseCount = to - from + 1;
            var suiteNumber = TestRunner.GetSuiteNumber(spec.SuiteName);
            if (suiteNumber < 1)
            {
                Console.Error.WriteLine(
                    $"{spec.SuiteName} 并行化：找不到套件编号，回退进程内执行");
                return RunInProcess(spec, from, to);
            }

            spec.BeforeSpawn?.Invoke();

            var workerCount = Math.Min(Environment.ProcessorCount, caseCount);
            Console.WriteLine(
                $"  [parallel] {spec.SuiteName}：case#{from}..#{to}（共 {caseCount} 例），" +
                $"切 {workerCount} 个子进程");
            Console.Out.Flush();

            var children = new List<ChildResult>();
            var start = from;
            for (var w = 0; w < workerCount; w++)
            {
                var len = caseCount / workerCount + (w < caseCount % workerCount ? 1 : 0);
                var childFrom = start;
                var childTo = start + len - 1;
                start += len;
                children.Add(StartChild(suiteNumber, childFrom, childTo));
            }
            foreach (var child in children)
            {
                WaitChild(child);
            }

            var totalPass = 0;
            var totalFail = 0;
            var failedChildren = new List<ChildResult>();
            Console.WriteLine("  [parallel] 子进程汇总：");
            foreach (var child in children)
            {
                var completion = ParseCompletion(child.Stdout, spec.SuiteName);
                var ok = child.StartError == null && child.ExitCode == 0
                    && completion is { Fail: 0 };
                if (ok)
                {
                    totalPass += completion!.Value.Pass;
                    Console.WriteLine(
                        $"    case#{child.From}..{child.To}（{child.To - child.From + 1} 例）：" +
                        $"{completion.Value.Pass} passed");
                }
                else
                {
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

            Console.WriteLine(
                $"=== {spec.SuiteName}: {totalPass} passed, {totalFail} failed ===（并行 {workerCount} 子进程）");
            Console.Out.Flush();
            return totalFail;
        }

        private static ChildResult StartChild(int suiteNumber, int childFrom, int childTo)
        {
            var child = new ChildResult { From = childFrom, To = childTo };
            string assemblyPath;
            try
            {
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
            return child;
        }

        private static void WaitChild(ChildResult child)
        {
            if (child.StartError != null || child.Process == null)
            {
                return;
            }
            var process = child.Process;
            process.WaitForExit();
            child.ExitCode = process.ExitCode;
            child.Stdout = child.StdoutTask?.GetAwaiter().GetResult() ?? "";
            child.Stderr += child.StderrTask?.GetAwaiter().GetResult() ?? "";
            process.Dispose();
        }

        private static (int Pass, int Fail)? ParseCompletion(string stdout, string suiteName)
        {
            var marker = $"=== {suiteName}: ";
            var idx = stdout.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }
            var rest = stdout[(idx + marker.Length)..];
            var newline = rest.IndexOf('\n');
            var line = (newline < 0 ? rest : rest[..newline]).Trim();
            var end = line.IndexOf(" ===", StringComparison.Ordinal);
            if (end >= 0)
            {
                line = line[..end].Trim();
            }
            var parts = line.Split(',');
            if (parts.Length != 2)
            {
                return null;
            }
            var passParts = parts[0].Trim().Split(' ');
            var failParts = parts[1].Trim().Split(' ');
            if (passParts.Length == 0 || failParts.Length == 0)
            {
                return null;
            }
            if (!int.TryParse(passParts[0], out var pass))
            {
                return null;
            }
            if (!int.TryParse(failParts[0], out var fail))
            {
                return null;
            }
            return (pass, fail);
        }

        private static string DescribeChildFailure(ChildResult child)
        {
            if (child.StartError != null)
            {
                return $"子进程启动失败：{child.StartError}";
            }
            if (child.ExitCode != 0)
            {
                return $"子进程退出码 {child.ExitCode}";
            }
            return "子进程输出缺失完成统计";
        }

        private static void PrintChildTail(string streamName, string output)
        {
            var lines = output.Replace("\r", "").Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => l.Length > 0)
                .ToArray();
            if (lines.Length == 0)
            {
                return;
            }
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
            public string? StartError;
            public Process? Process;
            public Task<string>? StdoutTask;
            public Task<string>? StderrTask;
        }
    }
}
