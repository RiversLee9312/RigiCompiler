using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private sealed class ProgramArgumentOption(string name, int count = 0) : ICommandLineOption
    {
        public CommandLineMask Mask { get; } = new() { Name = name, MinArgs = count, MaxArgs = count };
    }
    // parser 纯契约不修改生产 registry；正式 module CLI 使用同一 opt-in mask。
    private sealed class ProgramArgumentCommand : ICommandLineCommand
    {
        public CommandLineMask Mask { get; } = new() { Name = "module", AllowsProgramArguments = true };
        public IReadOnlyList<ICommandLineOption> SubCommands { get; } = [new ProgramArgumentOption("--run"), new ProgramArgumentOption("--profile", 1)];
        public int Execute(CommandLineParseResult result) => 0;
    }
    private static void TestProgramArguments()
    {
        ICommandLineCommand[] commands = [new ProgramArgumentCommand()];
        var parsed = CommandLineParser.TryParse(["module", "--run", "--profile", "sandbox", "--", "first", "", "two words", "--x"],
            commands, out var result, out _);
        TestHarness.CheckTrue("optin精确delimiter原样保留空串/空格/option样argv", parsed && result!.HasProgramArgumentDelimiter
            && result.ProgramArguments.SequenceEqual(["first", "", "two words", "--x"]) && result.Get("--profile")!.Single() == "sandbox");
        TestHarness.CheckTrue("原commands不接受programdelimiter", !CommandLineParser.TryParse(["compile", "--", "--x"], out _, out _));
        TestHarness.CheckTrue("delimiter前仍完成mask个数校验", !CommandLineParser.TryParse(["module", "--profile", "--", "ignored"], commands, out _, out _));
        var std = CompileInterfaceProbe("stdlib@1.0.0", StdlibSources.ParseAll().ToArray(), [], true).Artifact;
        var source = "pub func main(args: Array\\<String>): i32 {\n"
            + " if (args.length != 4) { return 1 }\n if ((args[0] as String) != \"first\") { return 2 }\n"
            + " if ((args[1] as String) != \"\") { return 3 }\n if ((args[2] as String) != \"two words\") { return 4 }\n"
            + " if ((args[3] as String) != \"中文🦊\") { return 5 }\n core.io.Console.println(args[3] as String)\n return 42\n}";
        var (artifact, unit) = CompileInterfaceProbe("argsapp@1.0.0", [TestHarness.ParseRoot(source, "source/args.rg")], [std], finalApplication: true);
        var linked = Modules.ModuleApplicationLinker.Link([std], artifact.ReadBil(), unit.Symbols);
        var actual = BilVm.Run(linked, programArguments: ["first", "", "two words", "中文🦊"]);
        TestHarness.CheckTrue("同一BIL真实main Array<String>顺序/空参/Unicode及返回值", actual.Exception == null
            && actual.ReturnValue is VmI32 { Value: 42 } && actual.Stdout == "中文🦊\n", actual.Exception?.ToString() ?? "");
        var empty = BilVm.Run(linked, programArguments: []);
        TestHarness.CheckTrue("同一BIL再次启动argv非烘焙且空数组真实length0", empty.ReturnValue is VmI32 { Value: 1 });
        var rejected = false;
        try { BilVm.Run(linked, programArguments: [new string('\ud800', 1)]); }
        catch (VmException ex) { rejected = ex.Message.Contains("Unicode", StringComparison.Ordinal); }
        TestHarness.CheckTrue("VM宿主无效代理对入口边界拒绝", rejected);
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "source.rg"), source);
        File.WriteAllText(Path.Combine(folder, "linked.bil"), BilWriter.Write(linked));
    }
}
