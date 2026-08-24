using System;
using System.Collections.Generic;
using System.IO;

namespace RigiCompiler.Middleware
{
    /// <summary>native --file：输入的 BIL 文件（1 个或多个，合并为一个模块过门禁）。</summary>
    public class NativeFileOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--file",
            Description = "输入的 BIL 文件（1 个或多个；§17 命名空间切片经多文件合并）",
            ArgsHint = "<路径...>",
            MinArgs = 1,
            MaxArgs = int.MaxValue,
        };
    }

    /// <summary>native --out：输出的原生目标文件路径。</summary>
    public class NativeOutOption : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "--out",
            Description = "输出的原生目标文件路径（.o/.obj）",
            ArgsHint = "<路径>",
            MinArgs = 1,
            MaxArgs = 1,
        };
    }

    /// <summary>
    /// native：Middleware 驱动（MIDDLEWARE_ARCHITECTURE §11 Cli/）——BIL 文本经
    /// Gate 门禁 → MwContext（驻留符号表）→ 进程内 LLVM 管线发射原生目标文件。
    /// MW0 骨架：函数体尚未 lowering（MW3 起），产物为空模块 .o；lld 链接
    /// rigi_rt 随 MW1 接入。
    /// </summary>
    public class NativeCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new()
        {
            Name = "native",
            Description = "Middleware：BIL → 原生目标文件（进程内 LLVM 管线；lld 链接随 MW1 接入）",
        };

        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = new ICommandLineOption[]
        {
            new NativeFileOption(),
            new NativeOutOption(),
            new VerboseOption(),
            new LogToOption(),
        };

        public int Execute(CommandLineParseResult result)
        {
            // --verbose / --log-to 在主体之前应用
            if (LoggerOptions.Apply(result) is { } loggerError)
            {
                Console.Error.WriteLine(loggerError);
                return 2;
            }

            var files = result.Get("--file");
            if (files == null)
            {
                Console.Error.WriteLine("native 需要 --file <路径...> 指定 BIL 文件");
                return 2;
            }
            var outPath = result.Get("--out")?[0];
            if (outPath == null)
            {
                Console.Error.WriteLine("native 需要 --out <路径> 指定目标文件输出路径");
                return 2;
            }

            // 读入全部 BIL 文本
            var inputs = new List<(string SourceName, string Text)>();
            foreach (var file in files)
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or FileNotFoundException or DirectoryNotFoundException)
                {
                    Console.Error.WriteLine($"无法读取 BIL 文件 {file}: {ex.Message}");
                    return 1;
                }
                inputs.Add((file, text));
            }

            // Gate 门禁：非法 BIL 逐条报 stderr 并拒绝（BIL §23）
            var gate = BilGate.Accept(inputs);
            if (!gate.IsAccepted)
            {
                foreach (var error in gate.Errors)
                {
                    Console.Error.WriteLine(error);
                }
                return 1;
            }
            Logger.Verbose("Middleware",
                $"门禁通过：{gate.Module!.Functions.Count} fn，符号段 {gate.Module.LocalSymbols.Count} 本地条目");

            // MW0：符号表驻留于 MwContext；空模块 .o 发射（函数体 lowering 随 MW3 起）
            var context = new MwContext(gate.Module!);
            if (!ObjectEmitter.TryEmitObject(context, outPath, out var emitError))
            {
                Console.Error.WriteLine($"目标文件发射失败 {outPath}: {emitError}");
                return 2;
            }
            Logger.Verbose("Middleware", $"已发射目标文件 {outPath}");
            return 0;
        }
    }
}
