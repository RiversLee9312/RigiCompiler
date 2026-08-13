using System;
using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler
{
    /// <summary>
    /// 命令行选项/子命令的自描述元数据：解析器与帮助生成器完全由它驱动，
    /// 不写死任何选项名。
    /// </summary>
    public class CommandLineMask
    {
        // 名字：COMMAND 为裸词（如 "compile"），子命令含 -- 前缀（如 "--file"）
        public string Name { get; set; } = "";
        // 中文描述，用于帮助文本
        public string Description { get; set; } = "";
        // 帮助文本里的参数占位（如 "<路径...>"、"[编号...]"）；无参选项留空
        public string ArgsHint { get; set; } = "";
        // 参数个数约束：MaxArgs = int.MaxValue 表示任意个数
        public int MinArgs { get; set; } = 0;
        public int MaxArgs { get; set; } = 0;
        public bool AllowsArbitraryArgs => MaxArgs == int.MaxValue;
        // 互斥子命令名列表（含 -- 前缀）：同现时解析报错
        public List<string> MutuallyExclusive { get; } = new();
    }

    /// <summary>命令行插件公共接口：COMMAND 与子命令都暴露一个 Mask。</summary>
    public interface ICommandLineOption
    {
        CommandLineMask Mask { get; }
    }

    /// <summary>
    /// 顶层 COMMAND 插件：除 Mask 外还持有自己的子命令列表，并承载行为（Execute）。
    /// COMMAND 的 Mask.MinArgs/MaxArgs 约束直接跟随 COMMAND 的裸参数
    /// （如 help 的名字参数；compile/test 的 MaxArgs=0，裸参数即"游离参数"报错）。
    /// </summary>
    public interface ICommandLineCommand : ICommandLineOption
    {
        IReadOnlyList<ICommandLineOption> SubCommands { get; }
        int Execute(CommandLineParseResult result);
    }

    /// <summary>解析结果：匹配到的 COMMAND + 裸参数 + 子命令名（含 --）→ 参数列表。</summary>
    public class CommandLineParseResult
    {
        public ICommandLineCommand Command { get; set; } = null!;
        public List<string> CommandArgs { get; } = new();
        public Dictionary<string, List<string>> SubCommandArgs { get; } = new();

        public bool Has(string subCommand) => SubCommandArgs.ContainsKey(subCommand);

        public List<string>? Get(string subCommand) =>
            SubCommandArgs.TryGetValue(subCommand, out var args) ? args : null;
    }

    /// <summary>
    /// 数据驱动的命令行解析器：argv[0] 匹配 COMMAND（裸词）；之后 --x（支持 --x=v
    /// 形态）开启新子命令，后续非 -- 开头的 token 归当前子命令；子命令之前的裸
    /// token 归 COMMAND 自己（由其 Mask 约束个数）。扫描后统一做参数个数/互斥/
    /// 重复校验。失败时 error 为中文提示，调用方打印到 stderr 并以退出码 2 结束。
    /// </summary>
    public static class CommandLineParser
    {
        public static bool TryParse(string[] args, out CommandLineParseResult? result, out string? error)
        {
            result = null;
            error = null;

            if (args.Length == 0)
            {
                error = "缺少 COMMAND（可用 help 查看全部命令）";
                return false;
            }

            var command = CommandLineRegistry.Commands.FirstOrDefault(c => c.Mask.Name == args[0]);
            if (command == null)
            {
                error = $"未知 COMMAND: {args[0]}（可用 help 查看全部命令）";
                return false;
            }

            var parseResult = new CommandLineParseResult { Command = command };
            List<string>? currentArgs = null;  // 当前子命令的参数槽；null 表示还没遇到子命令

            for (int i = 1; i < args.Length; i++)
            {
                var token = args[i];
                if (token.StartsWith("--"))
                {
                    // --x=v 形态拆成名字 + 内联首参
                    var name = token;
                    string? inlineValue = null;
                    int eq = token.IndexOf('=');
                    if (eq > 0)
                    {
                        name = token[..eq];
                        inlineValue = token[(eq + 1)..];
                    }

                    var sub = command.SubCommands.FirstOrDefault(s => s.Mask.Name == name);
                    if (sub == null)
                    {
                        error = $"未知子命令: {name}（COMMAND: {command.Mask.Name}）";
                        return false;
                    }
                    if (parseResult.SubCommandArgs.ContainsKey(name))
                    {
                        error = $"子命令重复出现: {name}";
                        return false;
                    }
                    currentArgs = new List<string>();
                    if (!string.IsNullOrEmpty(inlineValue)) currentArgs.Add(inlineValue);
                    parseResult.SubCommandArgs[name] = currentArgs;
                }
                else if (currentArgs != null)
                {
                    currentArgs.Add(token);
                }
                else
                {
                    // 子命令之前的裸 token 归 COMMAND 自己；compile/test 的 Mask.MaxArgs=0，
                    // 即不允许裸参数 → "游离参数"报错
                    if (command.Mask.MaxArgs == 0)
                    {
                        error = $"游离参数: {token}（{command.Mask.Name} 不接受裸参数，参数必须跟在某个子命令后）";
                        return false;
                    }
                    parseResult.CommandArgs.Add(token);
                }
            }

            // COMMAND 裸参数个数校验
            if (!CheckCount(command.Mask, parseResult.CommandArgs.Count, out error)) return false;

            // 子命令参数个数校验
            foreach (var (name, list) in parseResult.SubCommandArgs)
            {
                var mask = command.SubCommands.First(s => s.Mask.Name == name).Mask;
                if (!CheckCount(mask, list.Count, out error)) return false;
            }

            // 互斥校验
            foreach (var name in parseResult.SubCommandArgs.Keys)
            {
                var mask = command.SubCommands.First(s => s.Mask.Name == name).Mask;
                foreach (var ex in mask.MutuallyExclusive)
                {
                    if (parseResult.SubCommandArgs.ContainsKey(ex))
                    {
                        error = $"子命令互斥: {name} 与 {ex} 不能同时使用";
                        return false;
                    }
                }
            }

            result = parseResult;
            return true;
        }

        private static bool CheckCount(CommandLineMask mask, int actual, out string? error)
        {
            error = null;
            if (actual >= mask.MinArgs && actual <= mask.MaxArgs) return true;
            string expect = mask.AllowsArbitraryArgs
                ? $"至少 {mask.MinArgs} 个"
                : mask.MinArgs == mask.MaxArgs
                    ? $"{mask.MinArgs} 个"
                    : $"{mask.MinArgs}~{mask.MaxArgs} 个";
            error = $"{mask.Name} 参数个数错误：需要 {expect}参数，实际 {actual} 个";
            return false;
        }
    }

    /// <summary>命令注册表：全部顶层 COMMAND 的单一数据源（解析与帮助生成都从这里取）。</summary>
    public static class CommandLineRegistry
    {
        public static readonly ICommandLineCommand[] Commands =
        {
            new CompileCommand(),
            new TestCommand(),
            new HelpCommand(),
        };
    }

    /// <summary>帮助文本生成器：概览 / COMMAND 详情 / 子命令详情全部由注册表程序生成。</summary>
    public static class CommandLineHelp
    {
        // 概览：全部 COMMAND 及各自子命令（裸 rigic 与 help 无参时的输出）
        public static void PrintOverview()
        {
            Console.WriteLine("Rigi 编译器命令行");
            Console.WriteLine();
            Console.WriteLine("用法: rigic <COMMAND> [--sub-cmd [args...]...]");
            Console.WriteLine();
            foreach (var cmd in CommandLineRegistry.Commands)
            {
                Console.WriteLine($"{cmd.Mask.Name,-8}{cmd.Mask.Description}");
                foreach (var sub in cmd.SubCommands)
                {
                    Console.WriteLine($"    {Signature(sub.Mask),-28}{sub.Mask.Description}");
                }
            }
            Console.WriteLine();
            Console.WriteLine("详情: help <COMMAND>；单个子命令: help <COMMAND>.<子命令名>（不带 -- 前缀）");
        }

        // 按名字寻址打印：无点 → COMMAND 详情；带一个点 → 子命令详情。找不到返回 false
        public static bool PrintByName(string name)
        {
            int dot = name.IndexOf('.');
            if (dot < 0)
            {
                var cmd = FindCommand(name);
                if (cmd == null) return false;
                PrintCommand(cmd);
                return true;
            }
            var owner = FindCommand(name[..dot]);
            if (owner == null) return false;
            var sub = owner.SubCommands.FirstOrDefault(s => s.Mask.Name == "--" + name[(dot + 1)..]);
            if (sub == null) return false;
            PrintSubCommand(owner, sub);
            return true;
        }

        // COMMAND 详情：描述 + 用法行 + 子命令列表
        public static void PrintCommand(ICommandLineCommand cmd)
        {
            Console.WriteLine($"{cmd.Mask.Name} - {cmd.Mask.Description}");
            Console.WriteLine();
            var usage = $"用法: {cmd.Mask.Name}";
            if (cmd.Mask.ArgsHint.Length > 0) usage += $" {cmd.Mask.ArgsHint}";
            foreach (var sub in cmd.SubCommands)
            {
                usage += $" [{Signature(sub.Mask)}]";
            }
            Console.WriteLine(usage);
            if (cmd.SubCommands.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("子命令:");
                foreach (var sub in cmd.SubCommands)
                {
                    Console.WriteLine($"  {Signature(sub.Mask),-28}{sub.Mask.Description}");
                }
            }
        }

        // 子命令详情：签名 + 参数个数 + 互斥
        public static void PrintSubCommand(ICommandLineCommand owner, ICommandLineOption sub)
        {
            var mask = sub.Mask;
            Console.WriteLine($"{owner.Mask.Name} {Signature(mask)} - {mask.Description}");
            Console.WriteLine();
            Console.WriteLine($"参数个数: {CountDescription(mask)}");
            Console.WriteLine($"互斥: {(mask.MutuallyExclusive.Count == 0 ? "无" : string.Join(", ", mask.MutuallyExclusive))}");
        }

        private static ICommandLineCommand? FindCommand(string name) =>
            CommandLineRegistry.Commands.FirstOrDefault(c => c.Mask.Name == name);

        // 帮助里的签名：名字 + 参数占位（无参选项只有名字）
        private static string Signature(CommandLineMask mask) =>
            mask.ArgsHint.Length == 0 ? mask.Name : $"{mask.Name} {mask.ArgsHint}";

        private static string CountDescription(CommandLineMask mask) =>
            mask.AllowsArbitraryArgs
                ? (mask.MinArgs == 0 ? "任意个数" : $"至少 {mask.MinArgs} 个")
                : mask.MinArgs == mask.MaxArgs
                    ? (mask.MaxArgs == 0 ? "无参数" : $"{mask.MinArgs} 个")
                    : $"{mask.MinArgs}~{mask.MaxArgs} 个";
    }
}
