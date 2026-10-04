using System;
using System.IO;
using System.Text.Json;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// Logger 测试：
    /// - JSONL 文件写入：与控制台同门槛——默认 Warning 及以上落盘，
    ///   EnableVerbose 后 Verbose 也落盘，每行是合法 JSON，
    ///   ts/level/source/message 字段齐全且内容往返一致；
    /// - 控制台门槛：默认只显示 Warning 及以上，EnableVerbose 后显示 Verbose；
    /// - CLI 状态还原：CaptureState/RestoreState 把套件内 Reset 破坏的
    ///   --log-to/--verbose 状态还原（追加模式，已写入的日志不丢）。
    /// 每个用例前后用 Logger.Reset() 归位（关闭日志文件、VerboseEnabled 复位）。
    /// RunAll 整体以 CaptureState 进入、finally RestoreState 退出：本套件在
    /// 注册表中段，若把 CLI 经 --log-to 打开的日志文件关掉不还，后续套件的
    /// 日志会静默全部不落盘。
    /// </summary>
    public static class LoggerTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        public static void TestJsonlFileWrite()
        {
            Console.WriteLine("=== Testing Logger JSONL file write ===");

            Logger.Reset();
            var path = Path.Combine(Path.GetTempPath(), $"rigi_logger_test_{Guid.NewGuid():N}.jsonl");
            try
            {
                Logger.OpenLogFile(path);
                // 不 EnableVerbose：文件与控制台同门槛，Verbose 不落盘
                Logger.Verbose("Lexer", "verbose 消息");
                Logger.Warning("Parser", "warning 消息");
                Logger.Error("Lexer", "error 消息");
                Logger.Reset();  // 关闭并释放文件后再读

                var lines = File.ReadAllLines(path);
                Check("文件写入 2 行（默认不记 verbose）", lines.Length == 2);

                var expectedLevels = new[] { "warning", "error" };
                var expectedSources = new[] { "Parser", "Lexer" };
                var expectedMessages = new[] { "warning 消息", "error 消息" };
                bool allValid = lines.Length == expectedLevels.Length;
                for (int i = 0; i < lines.Length && allValid; i++)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(lines[i]);
                        var line = doc.RootElement;
                        allValid =
                            line.GetProperty("level").GetString() == expectedLevels[i] &&
                            line.GetProperty("source").GetString() == expectedSources[i] &&
                            line.GetProperty("message").GetString() == expectedMessages[i] &&
                            !string.IsNullOrEmpty(line.GetProperty("ts").GetString());
                    }
                    catch (JsonException)
                    {
                        allValid = false;
                    }
                }
                Check("每行是合法 JSON 且 ts/level/source/message 正确", allValid);
            }
            catch (Exception ex)
            {
                Fail("JSONL 文件写入", $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Logger.Reset();
                if (File.Exists(path)) File.Delete(path);
            }

            // EnableVerbose 后 Verbose 也落盘
            var verbosePath = Path.Combine(Path.GetTempPath(), $"rigi_logger_test_{Guid.NewGuid():N}.jsonl");
            try
            {
                Logger.Reset();
                Logger.OpenLogFile(verbosePath);
                Logger.EnableVerbose();
                Logger.Verbose("Lexer", "verbose 消息");
                Logger.Reset();

                var verboseLines = File.ReadAllLines(verbosePath);
                Check("EnableVerbose 后 verbose 落盘",
                    verboseLines.Length == 1 && verboseLines[0].Contains("\"verbose\""));
            }
            finally
            {
                Logger.Reset();
                if (File.Exists(verbosePath)) File.Delete(verbosePath);
            }
            Console.WriteLine();
        }

        public static void TestConsoleGating()
        {
            Console.WriteLine("=== Testing Logger console gating (VerboseEnabled) ===");

            Logger.Reset();
            // 先捕获再断言：避免 [PASS] 报告被重定向吞掉。
            // Logger 控制台输出走 stderr（M31）：诊断不污染 stdout 的数据流
            bool defaultOff, verboseHidden, warningShown, enabledOn, verboseShown;
            var originalError = Console.Error;
            var captured = new StringWriter();
            try
            {
                Console.SetError(captured);

                defaultOff = !Logger.VerboseEnabled;
                Logger.Verbose("Test", "hidden");
                verboseHidden = captured.ToString().Length == 0;

                Logger.Warning("Test", "shown");
                warningShown = captured.ToString().Contains("WARNING [Test]shown");

                Logger.EnableVerbose();
                enabledOn = Logger.VerboseEnabled;
                Logger.Verbose("Test", "visible");
                verboseShown = captured.ToString().Contains("VERBOSE [Test]visible");
            }
            finally
            {
                Console.SetError(originalError);
                Logger.Reset();
            }

            Check("VerboseEnabled 默认关闭", defaultOff);
            Check("默认 verbose 不进控制台", verboseHidden);
            Check("warning 始终进控制台", warningShown);
            Check("EnableVerbose 后 VerboseEnabled 开启", enabledOn);
            Check("开启后 verbose 进控制台", verboseShown);
            Console.WriteLine();
        }

        public static void TestCliStateRestore()
        {
            Console.WriteLine("=== Testing Logger CLI state capture/restore ===");

            Logger.Reset();
            var path = Path.Combine(Path.GetTempPath(), $"rigi_logger_test_{Guid.NewGuid():N}.jsonl");
            try
            {
                // 模拟 test --all --log-to + --verbose：套件进入前 CLI 已打开
                // 日志文件并写了历史行
                Logger.OpenLogFile(path);
                Logger.EnableVerbose();
                Logger.Warning("Cli", "before");

                var state = Logger.CaptureState();
                Logger.Reset();  // 套件自洁：CLI 状态被破坏
                Check("Reset 后日志路径清空", Logger.CurrentLogPath == null);
                Check("Reset 后 VerboseEnabled 复位", !Logger.VerboseEnabled);

                Logger.RestoreState(state);  // 套件退出：还原 CLI 状态
                Check("还原后日志路径恢复", Logger.CurrentLogPath == path);
                Check("还原后 VerboseEnabled 恢复", Logger.VerboseEnabled);

                Logger.Warning("Cli", "after");  // 还原后继续落盘
                Logger.Reset();  // 释放文件后再读
                var lines = File.ReadAllLines(path);
                Check("还原为追加（before/after 两行都在）", lines.Length == 2);
            }
            catch (Exception ex)
            {
                Fail("CLI 状态还原", $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Logger.Reset();
                if (File.Exists(path)) File.Delete(path);
            }
            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine($"  [PASS] {name}");
                passCount++;
            }
            else
            {
                Fail(name, "断言不成立");
            }
        }

        private static void Fail(string name, string message)
        {
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== 入口 =====
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        internal static ParallelSuiteRunner.SuiteSpec Spec { get; } = LegacySuiteSpecs.Counted("Logger",
        [
            (nameof(TestJsonlFileWrite), TestJsonlFileWrite),
            (nameof(TestConsoleGating), TestConsoleGating),
            (nameof(TestCliStateRestore), TestCliStateRestore),
        ], () => passCount = failCount = 0, () => (passCount, failCount));
    }
}
