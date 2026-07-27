using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LatteCompiler
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
    /// - 文件（--log-to）：不做级别过滤，所有级别（含 Verbose）都以 JSONL 写入，
    ///   每行一条 {"ts":..., "level":..., "source":..., "message":...}，便于 grep 诊断。
    /// </summary>
    public static class Logger
    {
        private static StreamWriter? logWriter;

        // 控制台 Verbose 门槛（不影响文件日志）
        public static bool VerboseEnabled { get; private set; }

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
        }

        public static void Verbose(string source, string message) => Write(LogLevel.Verbose, source, message);
        public static void Warning(string source, string message) => Write(LogLevel.Warning, source, message);
        public static void Error(string source, string message) => Write(LogLevel.Error, source, message);

        public static void Write(LogLevel level, string source, string message)
        {
            // 控制台门槛：Verbose 需显式开启；保持既有 "VERBOSE [source]{message}" 前缀风格
            if (level != LogLevel.Verbose || VerboseEnabled)
            {
                Console.WriteLine($"{level.ToString().ToUpperInvariant()} [{source}]{message}");
            }
            // 文件日志不过滤级别（用途就是从一大坨日志里 grep）
            logWriter?.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["ts"] = DateTimeOffset.Now.ToString("o"),
                ["level"] = level.ToString().ToLowerInvariant(),
                ["source"] = source,
                ["message"] = message
            }));
        }

        // 测试用：关闭并释放日志文件、VerboseEnabled 归位
        internal static void Reset()
        {
            logWriter?.Dispose();
            logWriter = null;
            VerboseEnabled = false;
        }
    }
}
