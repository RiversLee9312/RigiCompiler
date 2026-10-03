using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RigiCompiler
{
    // 日志级别：Verbose 是诊断噪音（默认不进控制台），Warning/Error 始终输出到控制台
    public enum LogLevel
    {
        Verbose,
        Warning,
        Error
    }

    /// <summary>
    /// 全局日志通道：Lexer/Parser 日志的唯一出口
    /// （禁止在 Lexer/Parser 里直接 Console.WriteLine）。
    ///
    /// 两路输出相互独立：
    /// - 控制台：默认只显示 Warning 及以上；EnableVerbose() 后也显示 Verbose；
    /// - 文件（--log-to）：JSONL 每行一条 {"ts":..., "level":..., "source":..., "message":...}；
    ///   与控制台同门槛——Verbose 默认不落盘（全量测试的逐 token verbose 会产生
    ///   GB 级日志），需 EnableVerbose()（--verbose）显式开启。
    /// </summary>
    public static class Logger
    {
        private static StreamWriter? logWriter;
        private static readonly object writeGate = new();
        private static readonly AsyncLocal<Capture?> capture = new();

        // 每个 indexed job 独立缓冲，join 后由父线程按输入序回放。
        internal sealed class Capture : IDisposable
        {
            private readonly Capture? previous = capture.Value;
            private readonly List<(LogLevel Level, string Source, string Message)> entries = new();
            internal Capture() { capture.Value = this; }
            internal void Replay()
            {
                foreach (var (level, source, message) in entries) Write(level, source, message);
            }
            public void Dispose() { capture.Value = previous; }
            internal void Add(LogLevel level, string source, string message) => entries.Add((level, source, message));
        }
        internal static Capture CaptureJob() => new();

        // 当前日志文件路径（供 CaptureState 快照；Reset 时清空）
        private static string? logPath;

        // 控制台 Verbose 门槛（不影响文件日志）
        public static bool VerboseEnabled { get; private set; }

        // 当前日志文件路径（null = 未打开日志文件；测试断言用）
        internal static string? CurrentLogPath => logPath;

        public static void EnableVerbose()
        {
            VerboseEnabled = true;
        }

        // 打开日志文件：覆盖创建、UTF-8（无 BOM）、AutoFlush
        public static void OpenLogFile(string path)
        {
            logWriter?.Dispose();
            logWriter = new StreamWriter(path, append: false, encoding: new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            logPath = path;
        }

        public static void Verbose(string source, string message) => Write(LogLevel.Verbose, source, message);
        public static void Warning(string source, string message) => Write(LogLevel.Warning, source, message);
        public static void Error(string source, string message) => Write(LogLevel.Error, source, message);

        public static void Write(LogLevel level, string source, string message)
        {
            // 先按旧门槛过滤，关闭 verbose 时不能积累逐字符/逐 token 日志。
            if (level == LogLevel.Verbose && !VerboseEnabled) return;
            if (capture.Value is { } job) { job.Add(level, source, message); return; }
            lock (writeGate)
            {
            // 控制台门槛：Verbose 需显式开启；保持既有 "VERBOSE [source]{message}" 前缀风格。
            // 走 stderr（M31）：编译器诊断不污染 stdout——compile --parse-only 的
            // AST JSONL 输出到 stdout，任何按行解析 JSONL 的下游都依赖其纯净
            if (level != LogLevel.Verbose || VerboseEnabled)
            {
                Console.Error.WriteLine($"{level.ToString().ToUpperInvariant()} [{source}]{message}");
            }
            // 文件日志与控制台同门槛：Verbose 默认不落盘（诊断时先 --verbose）
            if (level != LogLevel.Verbose || VerboseEnabled)
            {
                logWriter?.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["ts"] = DateTimeOffset.Now.ToString("o"),
                    ["level"] = level.ToString().ToLowerInvariant(),
                    ["source"] = source,
                    ["message"] = message
                }));
            }
            }
        }

        // 测试用：关闭并释放日志文件、VerboseEnabled 归位
        internal static void Reset()
        {
            logWriter?.Dispose();
            logWriter = null;
            logPath = null;
            VerboseEnabled = false;
        }

        // CLI 日志状态快照/还原：测试套件自洁（Reset）会关掉 CLI 经 --log-to
        // 已打开的日志文件并复位 VerboseEnabled，导致后续套件的日志静默不落盘。
        // 套件进入时 CaptureState、退出时 RestoreState 即可把 CLI 状态还原。
        internal static (string? LogPath, bool Verbose) CaptureState()
        {
            return (logPath, VerboseEnabled);
        }

        // 还原 CaptureState 快照：先归位（关闭套件自己打开的文件），再按需以
        // 追加模式重开 CLI 日志文件（追加而非覆盖：套件运行前已写入的日志必须保留）
        internal static void RestoreState((string? LogPath, bool Verbose) state)
        {
            Reset();
            if (state.LogPath != null)
            {
                logWriter = new StreamWriter(state.LogPath, append: true, encoding: new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                logPath = state.LogPath;
            }
            VerboseEnabled = state.Verbose;
        }
    }
}
