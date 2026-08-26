using System.Text;
using RigiCompiler;

// 统一 UTF-8 输出：Windows 上 Console 默认走控制台代码页（CI en-US runner 为
// CP437），中文诊断会被 '?' 替换；显式固定 UTF-8 使重定向/管道场景字节确定
Console.OutputEncoding = new UTF8Encoding(false);

// Rigi 编译器命令行入口：rigic <COMMAND> [--sub-cmd [args...]...]
// COMMAND 共三个：compile / test / help（注册表见 Core/CommandLine.cs，
// 插件实现见 Core/Commands.cs；帮助文本由注册表程序生成，可用 help 查看）。
// 无参数 → 等同于 help（打印概览）。
if (args.Length == 0)
{
    CommandLineHelp.PrintOverview();
    return 0;
}

// 解析失败（未知 COMMAND/子命令、参数个数错误、互斥同现、游离参数）→ stderr + 退出码 2
if (!CommandLineParser.TryParse(args, out var result, out var error))
{
    Console.Error.WriteLine(error);
    return 2;
}

return result!.Command.Execute(result);
