using System.IO.Compression;
using System.Text.Json.Nodes;
using RigiCompiler.Modules;
using RigiCompiler.Middleware.Cache;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "测试证据记录实际托管入口；AOT 使用 ProcessPath。")]
    private static JsonObject ModuleWorkerIdentity() => new()
    {
        ["mode"] = NativeObjectIdentity.UsesManagedImages ? "CoreCLR" : "native-process-image",
        ["dynamicCodeSupported"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
        ["path"] = typeof(Frontend).Assembly.Location,
        ["processPath"] = Environment.ProcessPath,
        ["effectiveOverride"] = Environment.GetEnvironmentVariable("RIGI_TEST_RIGIC"),
        ["contentSha"] = NativeObjectIdentity.CompilerContentIdentity(),
        ["diskSha"] = typeof(Frontend).Assembly.Location is { Length: > 0 } path ? ArtifactCache.HashFile(path) : null,
        ["mvid"] = typeof(Frontend).Assembly.ManifestModule.ModuleVersionId.ToString()
    };

    private static void TestFullModuleCli()
    {
        if (!OperatingSystem.IsLinux()) { CaseAssertions.RecordSkip("完整 Native CLI 产品门禁当前仅 Linux；Windows 未实测"); return; }
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "worker-identity.json"), ModuleWorkerIdentity().ToJsonString());
        var root = Path.Combine(folder, "cli app with space");
        var results = new JsonArray();
        (int Code, string Out, string Err) Call(string label, params string[] args)
        {
            var result = ExecuteModule(args);
            File.WriteAllText(Path.Combine(folder, label + ".stdout"), result.Out);
            File.WriteAllText(Path.Combine(folder, label + ".stderr"), result.Err);
            File.WriteAllText(Path.Combine(folder, label + ".exit"), result.Code.ToString());
            results.Add((JsonNode)new JsonObject { ["label"] = label, ["exit"] = result.Code });
            return result;
        }
        var initialized = Call("init", "module", "--init", "--root", root);
        CaseAssertions.CheckTrue("正式CLI init创建可继续全流程模板", initialized.Code == 0, initialized.Err);
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        File.WriteAllText(Path.Combine(root, "resources/info.txt"), "module-resource");
        const string start = "cliapp::$start(args:.array<.string>)@.i32";
        var hook = "test -f \"$PRODUCT/modules/cliapp/1.0.0/data/info.txt\" && printf A >> \"$BUILD_ROOT/events\"";
        File.WriteAllText(Path.Combine(root, "module.yaml"), "schema: 1\nname: cliapp\nversion: 1.0.0\ntype: executable\n"
            + "dependencies: [{name: stdlib, version: 1.0.0}]\ndefault-profile: debug\nprofiles:\n"
            + "  debug: {target: vm, entry: '" + start + "'}\n  release: {target: native, entry: '" + start + "'}\n"
            + "resources: [{source: info.txt, destination: data/info.txt}]\nhooks:\n"
            + "  - {phase: after-publish, environment: linux, inputs: [resources/info.txt], command: " + JsonValue.Create(hook)!.ToJsonString() + "}\n");
        File.WriteAllText(Path.Combine(root, "source/main.rg"), "namespace cliapp\npub func main(): i32 { return 99 }\n"
            + "pub func start(args: Array\\<String>): i32 {\n if (args.length != 3) { return 9 }\n"
            + " if ((args[0] as String) != \"\") { return 10 }\n if ((args[1] as String) != \"two words\") { return 11 }\n"
            + " core.io.Console.println(args[2] as String)\n return 37\n}\n");
        var vm = Call("vm", "module", "--run", "--root", root, "--profile", "debug", "--", "", "two words", "中文🦊");
        CaseAssertions.CheckTrue("CLI debug真实VM选配置入口/argv/资源发布hook", vm.Code == 37 && vm.Out == "中文🦊\n", vm.Err);
        var published = Call("publish", "module", "--publish", "--root", root, "--profile", "debug");
        var product = Path.Combine(root, "product/debug/modules/cliapp/1.0.0");
        var executable = Path.Combine(product, "app");
        CaseAssertions.CheckTrue("publish即使VM profile仍生成真实exe与Std archive", published.Code == 0 && File.Exists(executable)
            && File.Exists(Path.Combine(root, "product/debug/modules/stdlib/1.0.0/libstdlib.a")), published.Err);
        var direct = ExternalProcess.Run(executable, ["", "two words", "中文🦊"], out var stdout, out var stderr,
            environment: new Dictionary<string, string> { ["RIGI_RT_MEMTRACK"] = "1" }, closeStdin: true);
        File.WriteAllText(Path.Combine(folder, "standalone.stdout"), stdout); File.WriteAllText(Path.Combine(folder, "standalone.stderr"), stderr);
        File.WriteAllText(Path.Combine(folder, "standalone.exit"), direct.ToString());
        CaseAssertions.CheckTrue("已publish standalone真实OSargv/配置入口/MEMTRACK", direct == 37 && stdout == "中文🦊\n" && stderr.Length == 0, stderr);
        var native = Call("native", "module", "--run", "--root", root, "--profile", "release", "--", "", "two words", "中文🦊");
        CaseAssertions.CheckTrue("CLI release读YAML native并转递真实argv/exit", native.Code == 37 && native.Out == "中文🦊\n", native.Err);
        var zip = Path.Combine(folder, "cli bundle.zip");
        var bundled = Call("bundle", "module", "--bundle", "--root", root, "--profile", "debug", "--output", zip);
        using (var archive = File.Exists(zip) ? ZipFile.OpenRead(zip) : null)
            CaseAssertions.CheckTrue("CLI bundle先发布后包含根配置/源码/资源/真实Native产品", bundled.Code == 0 && archive != null
                && archive.GetEntry("module.yaml") != null && archive.GetEntry("source/main.rg") != null
                && archive.GetEntry("resources/info.txt") != null && archive.GetEntry("artifact/debug/product/app") != null, bundled.Err);
        var client = Path.Combine(folder, "install-client");
        var clientInit = Call("client-init", "module", "--init", "--root", client);
        var installed = Call("install", "module", "--install", zip, "--root", client);
        var target = Path.Combine(client, "dependencies/cliapp/1.0.0");
        CaseAssertions.CheckTrue("CLI第五操作install真实exe/API资源及幂等", clientInit.Code == 0 && installed.Code == 0
            && File.Exists(Path.Combine(target, "artifact/debug/product/app"))
            && ArtifactCache.HashFile(executable) == ArtifactCache.HashFile(Path.Combine(target, "artifact/debug/product/app"))
            && Call("install-again", "module", "--install", zip, "--root", client).Code == 0, installed.Err);
        CaseAssertions.CheckTrue("缓存命中也执行after-publish且资源在hook前可见", File.ReadAllText(Path.Combine(root, "events")) == "AAAA");
        File.WriteAllText(Path.Combine(folder, "cli-evidence.json"), new JsonObject
        { ["calls"] = results, ["exeSha"] = ArtifactCache.HashFile(executable), ["zipSha"] = ArtifactCache.HashFile(zip) }.ToJsonString());
    }
}
