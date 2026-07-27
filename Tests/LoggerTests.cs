using System;
using System.IO;
using System.Text.Json;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// Logger 测试：
    /// - JSONL 文件写入：所有级别（含 Verbose）都落盘，每行是合法 JSON，
    ///   ts/level/source/message 字段齐全且内容往返一致；
    /// - 控制台门槛：默认只显示 Warning 及以上，EnableVerbose 后显示 Verbose。
    /// 每个用例前后用 Logger.Reset() 归位（关闭日志文件、VerboseEnabled 复位）。
    /// </summary>
    public static class LoggerTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        public static void TestJsonlFileWrite()
        {
            Console.WriteLine("=== Testing Logger JSONL file write ===");

            Logger.Reset();
            var path = Path.Combine(Path.GetTempPath(), $"latte_logger_test_{Guid.NewGuid():N}.jsonl");
            try
            {
                Logger.OpenLogFile(path);
                // 不 EnableVerbose：文件日志不受控制台门槛影响，Verbose 也应落盘
                Logger.Verbose("Lexer", "verbose 消息");
                Logger.Warning("Parser", "warning 消息");
                Logger.Error("Lexer", "error 消息");
                Logger.Reset();  // 关闭并释放文件后再读

                var lines = File.ReadAllLines(path);
                Check("文件写入 3 行", lines.Length == 3);

                var expectedLevels = new[] { "verbose", "warning", "error" };
                var expectedSources = new[] { "Lexer", "Parser", "Lexer" };
                var expectedMessages = new[] { "verbose 消息", "warning 消息", "error 消息" };
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
            Console.WriteLine();
        }

        public static void TestConsoleGating()
        {
            Console.WriteLine("=== Testing Logger console gating (VerboseEnabled) ===");

            Logger.Reset();
            // 先捕获再断言：避免 [PASS] 报告被重定向吞掉
            bool defaultOff, verboseHidden, warningShown, enabledOn, verboseShown;
            var originalOut = Console.Out;
            var captured = new StringWriter();
            try
            {
                Console.SetOut(captured);

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
                Console.SetOut(originalOut);
                Logger.Reset();
            }

            Check("VerboseEnabled 默认关闭", defaultOff);
            Check("默认 verbose 不进控制台", verboseHidden);
            Check("warning 始终进控制台", warningShown);
            Check("EnableVerbose 后 VerboseEnabled 开启", enabledOn);
            Check("开启后 verbose 进控制台", verboseShown);
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
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Logger Tests                      ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestJsonlFileWrite();
            TestConsoleGating();

            Console.WriteLine($"=== Logger Tests Complete: {passCount} passed, {failCount} failed ===");
            return failCount;
        }
    }
}
