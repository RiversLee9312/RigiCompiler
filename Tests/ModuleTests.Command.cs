using RigiCompiler.Bil;
using RigiCompiler.Modules;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static (int Code, string Out, string Err) ExecuteModule(params string[] args)
    {
        if (!CommandLineParser.TryParse(args, out var parsed, out var error)) throw new InvalidOperationException(error);
        var oldOut = Console.Out; var oldErr = Console.Error;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Console.SetOut(stdout); Console.SetError(stderr);
        try { var code = parsed!.Command.Execute(parsed); return (code, stdout.ToString(), stderr.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }
    private static void TestModuleCommand()
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        var root = Path.Combine(folder, "cliapp");
        var initialized = ExecuteModule("module", "--init", "--root", root);
        TestHarness.CheckTrue("真实registry module模板schema/显式Std/任意profile", initialized.Code == 0
            && ModuleConfigurationReader.ReadFile(Path.Combine(root, "module.yaml")).SelectProfile().Name == "debug");
        var run = ExecuteModule("module", "--run", "--root", root);
        TestHarness.CheckTrue("模板own-source独立管线VM运行stdout仅程序", run.Code == 0 && run.Out == "hello module\n", run.Err);
        var original = File.ReadAllText(Path.Combine(root, "source/main.rg"));
        TestHarness.CheckTrue("init拒覆盖原源码", ExecuteModule("module", "--init", "--root", root).Code == 1
            && File.ReadAllText(Path.Combine(root, "source/main.rg")) == original);
        File.WriteAllText(Path.Combine(root, "source/main.rg"), "pub func main(args: Array\\<String>): i32 {\n if (args.length == 4) { core.io.Console.println(\"four\") } else { core.io.Console.println(\"empty\") }\n return 23\n}\n");
        var arguments = ExecuteModule("module", "--run", "--root", root, "--profile", "sandbox", "--", "", "two words", "中文🦊", "--not-option");
        TestHarness.CheckTrue("真实CLI delimiter/任意profile/argv/非零exit转递", arguments.Code == 23 && arguments.Out == "four\n", arguments.Err);
        var empty = ExecuteModule("module", "--run", "--root", root, "--");
        TestHarness.CheckTrue("同模块cachehit空argv非烘焙", empty.Code == 23 && empty.Out == "empty\n", empty.Err);
        TestHarness.CheckTrue("旧顶层run移除且init不能接程序argv", !CommandLineParser.TryParse(["run", "--file", "a.rg"], out _, out _)
            && ExecuteModule("module", "--init", "--root", root, "--").Code == 2);
        File.WriteAllText(Path.Combine(root, "source/main.rg"), "pub func main(): i32 { core.io.Console.println(\"must-not-start\")\n return missingValue }\n");
        var failed = ExecuteModule("module", "--run", "--root", root);
        TestHarness.CheckTrue("构建失败没有启动程序", failed.Code != 0 && failed.Out.Length == 0, failed.Err);
        var evidence = new System.Text.Json.Nodes.JsonObject
        { ["template"] = run.Code, ["arguments"] = arguments.Code, ["empty"] = empty.Code, ["failed"] = failed.Code, ["stdout"] = arguments.Out };
        File.WriteAllText(Path.Combine(folder, "cli-results.json"), evidence.ToJsonString());
    }
}
