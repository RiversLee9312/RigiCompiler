using System.Text.Json.Nodes;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Modules;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Cache;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestNativeArguments() => NativeArguments(false);
    private static void TestNativeCoroutineArguments() => NativeArguments(true);
    private static void NativeArguments(bool coroutine)
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        var std = CompileInterfaceProbe("stdlib@1.0.0", StdlibSources.ParseAll().ToArray(), [], true).Artifact;
        var source = "pub func main(args: Array\\<String>): i32 {\n" + (coroutine ? " yield core.coroutine.sleep(1)\n" : "")
            + " if (args.length == 0) { core.io.Console.println(\"empty\")\n return 7 }\n"
            + " if (args.length == 1) { throw new core.RuntimeException(\"argv-exception\") }\n"
            + " if (args.length != 4) { return 1 }\n if ((args[0] as String) != \"first\") { return 2 }\n"
            + " if ((args[1] as String) != \"\") { return 3 }\n if ((args[2] as String) != \"two words\") { return 4 }\n"
            + " if ((args[3] as String) != \"中文🦊\") { return 5 }\n core.io.Console.println(args[3] as String)\n return 42\n}\n";
        var (app, unit) = CompileInterfaceProbe("argv@1.0.0", [TestHarness.ParseRoot(source, "source/argv.rg")], [std], finalApplication: true);
        var linked = ModuleApplicationLinker.Link([std], app.ReadBil(), unit.Symbols);
        File.WriteAllText(Path.Combine(folder, "source.rg"), source); File.WriteAllText(Path.Combine(folder, "input.bil"), BilWriter.Write(linked));
        string[] arguments = ["first", "", "two words", "中文🦊"];
        var vm = BilVm.Run(linked, programArguments: arguments);
        TestHarness.CheckTrue("入口VM " + (coroutine ? "协程" : "同步") + " argv实际42", vm.Exception == null && vm.ReturnValue is VmI32 { Value: 42 } && vm.Stdout == "中文🦊\n", vm.Exception?.ToString() ?? "");
        var executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "argv.exe" : "argv");
        var compiled = NativeCommand.EmitAndLink(linked, executable, null, null, null, null, null, null, "module argv");
        File.WriteAllText(Path.Combine(folder, "compile.exit"), compiled.ToString());
        TestHarness.CheckTrue("真实默认O2 Native Array<String>入口发射", compiled == 0 && File.Exists(executable));
        var events = new JsonArray();
        foreach (var (label, args, expected, stdout) in new (string, string[], int, string)[]
        { ("many", arguments, 42, "中文🦊\n"), ("empty", [], 7, "empty\n"), ("exception", ["fail"], 1, "") })
        {
            var actualOut = ""; var actualErr = "未生成程序";
            var code = compiled == 0 ? ExternalProcess.Run(executable, args, out actualOut, out actualErr,
                environment: new Dictionary<string, string> { ["RIGI_RT_MEMTRACK"] = "1" }, closeStdin: true) : -1;
            // 每次启动同一产物，argv 不进入 BIL/native key；异常路径也必须归还桥持有的数组。
            File.WriteAllText(Path.Combine(folder, label + ".stdout"), actualOut);
            File.WriteAllText(Path.Combine(folder, label + ".stderr"), actualErr);
            File.WriteAllText(Path.Combine(folder, label + ".exit"), code.ToString());
            TestHarness.CheckTrue("同一Native产物OSargv " + label + "顺序/空串/空格/Unicode/生命周期", code == expected && actualOut == stdout
                && (label == "exception" ? actualErr.Contains("argv-exception", StringComparison.Ordinal) && !actualErr.Contains("memory leak", StringComparison.Ordinal) : actualErr.Length == 0), actualErr);
            events.Add((JsonNode)new JsonObject { ["label"] = label, ["exit"] = code, ["out"] = actualOut, ["err"] = actualErr });
        }
        if (OperatingSystem.IsLinux() && compiled == 0)
        {
            // Python bytes argv 直接通过 execve，绕过托管字符串转换，验证非法 POSIX 字节拒绝。
            var code = ExternalProcess.Run("python3", ["-c", "import os,sys; os.execve(os.fsencode(sys.argv[1]),[os.fsencode(sys.argv[1]),b'\\xff'],os.environ)", executable],
                out var stdout, out var stderr, environment: new Dictionary<string, string> { ["RIGI_RT_MEMTRACK"] = "1" }, closeStdin: true);
            File.WriteAllText(Path.Combine(folder, "invalid.stderr"), stderr); File.WriteAllText(Path.Combine(folder, "invalid.exit"), code.ToString());
            TestHarness.CheckTrue("实际execve非法UTF8参数在托管图分配前拒且无泄漏", code == 1 && stdout.Length == 0 && stderr.Contains("UTF-8", StringComparison.Ordinal)
                && !stderr.Contains("memory leak", StringComparison.Ordinal), stderr);
        }
        File.WriteAllText(Path.Combine(folder, "evidence.json"), new JsonObject { ["coroutine"] = coroutine, ["compileExit"] = compiled,
            ["compilerSha"] = NativeObjectIdentity.CompilerContentIdentity(), ["executableSha"] = File.Exists(executable) ? ArtifactCache.HashFile(executable) : null, ["runs"] = events }.ToJsonString());
    }
}
