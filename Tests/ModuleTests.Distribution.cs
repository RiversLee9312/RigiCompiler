using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Modules;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestModuleDistribution() => ModuleDistributionAsync().GetAwaiter().GetResult();
    private static async Task ModuleDistributionAsync()
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        var provider = Path.Combine(folder, "provider");
        Directory.CreateDirectory(Path.Combine(provider, "source"));
        Directory.CreateDirectory(Path.Combine(provider, "resources/data"));
        File.WriteAllText(Path.Combine(provider, "source/lib.rg"), "namespace packdemo\npub func answer(): i32 { return 42 }\n");
        File.WriteAllText(Path.Combine(provider, "resources/data/value.txt"), "original");
        var executableResource = Path.Combine(provider, "resources/data/tool.sh");
        File.WriteAllText(executableResource, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executableResource,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var windows = OperatingSystem.IsWindows();
        var host = ModuleBuildContext.HostEnvironment;
        var beforeCommand = windows
            ? "if not \"%RIGI_SRC%\"==\"%CD%\\source\" exit /b 7 & if not \"%RIGI_RES%\"==\"%CD%\\resources\" exit /b 7 & echo before:%BUILD_ROOT%>>\"%BUILD_ROOT%\\install-events\""
            : "test \"$RIGI_SRC\" = \"$PWD/source\" && test \"$RIGI_RES\" = \"$PWD/resources\" && printf \"before:%s\\n\" \"$BUILD_ROOT\" >> \"$BUILD_ROOT/install-events\"";
        var afterCommand = windows
            ? "if not \"%RIGI_ARTIFACT%\"==\"%CD%\\artifact\\debug\" exit /b 7 & echo after:%PRODUCT%>>\"%BUILD_ROOT%\\install-events\""
            : "test \"$RIGI_ARTIFACT\" = \"$PWD/artifact/debug\" && printf \"after:%s\\n\" \"$PRODUCT\" >> \"$BUILD_ROOT/install-events\"";
        var yaml = "schema: 1\nname: packlib\nversion: 1.0.0\ntype: static-library\n"
            + "dependencies: [{name: stdlib, version: 1.0.0}]\ndefault-profile: debug\nprofiles: {debug: {target: vm}}\n"
            + "resources: [{source: data, destination: assets}]\nhooks:\n"
            + "  - {phase: before-install, environment: " + host + ", command: '" + beforeCommand + "'}\n"
            + "  - {phase: after-install, environment: " + host + ", command: '" + afterCommand + "'}\n";
        File.WriteAllText(Path.Combine(provider, "module.yaml"), yaml);
        async Task<ModuleBuildContext> Publish(string root)
        {
            using var transaction = new ModuleProductPublication();
            var built = await ModuleBuildService.BuildAsync(root, publish: b => transaction.PublishAsync(b));
            transaction.Commit(); return built.Entry.Context;
        }
        var context = await Publish(provider);
        var productResource = Path.Combine(context.ProductArtifact, "assets/value.txt");
        TestHarness.CheckTrue("真实独立发布资源落namespacedPRODUCT而非共享根", File.ReadAllText(productResource) == "original"
            && !Directory.Exists(Path.Combine(context.Product, "assets")));
        var bundle = Path.Combine(folder, "packlib.zip"); ModuleBundle.Create(context, bundle);
        var repeat = Path.Combine(folder, "packlib-repeat.zip"); ModuleBundle.Create(context, repeat);
        string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        TestHarness.CheckTrue("同输入ZIP顺序/时间戳确定且带API/BIL/source/resource", Hash(bundle) == Hash(repeat));
        var client = Path.Combine(folder, "client"); ModuleCommand.Initialize(client);
        File.WriteAllText(Path.Combine(client, "module.yaml"), File.ReadAllText(Path.Combine(client, "module.yaml"))
            .Replace("  - {name: stdlib, version: 1.0.0}\n", "  - {name: stdlib, version: 1.0.0}\n  - {name: packlib, version: 1.0.0}\n"));
        File.WriteAllText(Path.Combine(client, "source/main.rg"), "import packdemo.*\npub func main(): i32 { return answer() }\n");
        var installed = await ModuleBundle.InstallAsync(client, bundle);
        var events = Path.Combine(client, "install-events");
        TestHarness.CheckTrue("真实ZIP目录identity/资源与stage→commit hook自身环境/共享PRODUCT", installed == Path.Combine(client, "dependencies", "packlib", "1.0.0")
            && File.ReadAllText(Path.Combine(installed, "resources/data/value.txt")) == "original"
            && File.ReadAllLines(events).SequenceEqual(["before:" + client, "after:" + Path.Combine(client, "product", "debug")]));
        TestHarness.CheckTrue("ZIP保留普通可执行rwx权限且不复制特殊位", windows
            || File.GetUnixFileMode(Path.Combine(installed, "resources/data/tool.sh")).HasFlag(UnixFileMode.UserExecute));
        var duplicate = await Task.WhenAll(ModuleBundle.InstallAsync(client, bundle), ModuleBundle.InstallAsync(client, bundle));
        TestHarness.CheckTrue("同identity同ZIP并发重复安装幂等且hook不重复", duplicate.All(p => p == installed) && File.ReadAllLines(events).Length == 2);
        var concurrentClient = Path.Combine(folder, "concurrent-client"); ModuleCommand.Initialize(concurrentClient);
        var parentEnvironment = Environment.GetEnvironmentVariable("BUILD_ROOT");
        var firstInstall = await Task.WhenAll(Task.Run(() => ModuleBundle.InstallAsync(concurrentClient, bundle)),
            Task.Run(() => ModuleBundle.InstallAsync(concurrentClient, bundle)));
        TestHarness.CheckTrue("真实同identity首次并发安装单提交/两hook且不污染父环境", firstInstall[0] == firstInstall[1]
            && File.ReadAllLines(Path.Combine(concurrentClient, "install-events")).SequenceEqual(["before:" + concurrentClient,
                "after:" + Path.Combine(concurrentClient, "product", "debug")])
            && Environment.GetEnvironmentVariable("BUILD_ROOT") == parentEnvironment
            && !Directory.GetDirectories(Path.GetDirectoryName(firstInstall[0])!).Any(p => Path.GetFileName(p).StartsWith(".install-", StringComparison.Ordinal)));
        Directory.Move(Path.Combine(installed, "source"), Path.Combine(installed, "source.removed"));
        var providerCompiled = -1; var returned = -1;
        {
            var built = await ModuleBuildService.BuildAsync(client);
            var vm = BilVm.Run(built.Entry.Linked);
            providerCompiled = built.Modules.Single(m => m.Artifact.ModuleId == "packlib@1.0.0").Compiled ? 1 : 0;
            returned = vm.ReturnValue is VmI32 value ? value.Value : -1;
            TestHarness.CheckTrue("已安装provider移走source后真实receipt导入compile0并VM42", built.Modules.Single(m => m.Artifact.ModuleId == "packlib@1.0.0")
                is { Compiled: false, CacheStatus: "prebuilt", OwnSourceCount: 0 } && vm.Exception == null && vm.ReturnValue is VmI32 { Value: 42 });
        }
        var cli = ExecuteModule("module", "--install", bundle, "--root", client);
        TestHarness.CheckTrue("正式module --install同ZIP原样幂等", cli.Code == 0 && cli.Out.Length == 0, cli.Err);
        var negatives = new JsonArray(); var index = 0;
        string Zip(params (string Path, string Contents, int Attributes)[] entries)
        {
            var path = Path.Combine(folder, "negative-" + index++ + ".zip");
            using var stream = File.Create(path); using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var item in entries)
            { var entry = zip.CreateEntry(item.Path); entry.ExternalAttributes = item.Attributes; using var writer = new StreamWriter(entry.Open()); writer.Write(item.Contents); }
            return path;
        }
        async Task<bool> RejectInstall(string path)
        { try { await ModuleBundle.InstallAsync(client, path); return false; } catch (ModuleConfigurationException) { return true; } }
        foreach (var path in new[] { "../escape.txt", "/escape.txt", "C:/escape.txt", "a\\b", "x/../y", "x//y" })
        {
            var rejected = await RejectInstall(Zip(("module.yaml", yaml, 0), (path, "bad", 0)));
            negatives.Add((JsonNode)new JsonObject { ["path"] = path, ["rejected"] = rejected });
            TestHarness.CheckTrue("ZIP拒越界 " + path, rejected);
        }
        TestHarness.CheckTrue("ZIP拒大小写冲突/重复file/父file冲突", await RejectInstall(Zip(("module.yaml", yaml, 0), ("a", "x", 0), ("A", "y", 0)))
            && await RejectInstall(Zip(("module.yaml", yaml, 0), ("a", "x", 0), ("a", "y", 0)))
            && await RejectInstall(Zip(("module.yaml", yaml, 0), ("a", "x", 0), ("a/b", "y", 0))));
        TestHarness.CheckTrue("ZIP拒符号链接/特殊节点/reparse", await RejectInstall(Zip(("module.yaml", yaml, 0), ("link", "outside", unchecked((int)0xa1ff0000))))
            && await RejectInstall(Zip(("module.yaml", yaml, 0), ("fifo", "", 0x11a40000)))
            && await RejectInstall(Zip(("module.yaml", yaml, 0), ("reparse", "", 0x400))));
        TestHarness.CheckTrue("ZIP拒根配置缺失/坏schema/同版本不同内容", await RejectInstall(Zip(("source/lib.rg", "", 0)))
            && await RejectInstall(Zip(("module.yaml", yaml + "typo: x\n", 0)))
            && await RejectInstall(Zip(("module.yaml", yaml, 0), ("source/changed.rg", "", 0))));
        TestHarness.CheckTrue("ZIP坏预编译receipt在hook及提交前拒绝", await RejectInstall(Zip(("module.yaml", yaml.Replace("name: packlib", "name: brokenpack"), 0),
            ("artifact/debug/module.rgi", "not-a-module", 0))) && !Directory.Exists(Path.Combine(client, "dependencies", "brokenpack", "1.0.0")));
        var wrong = yaml.Replace("version: 1.0.0\ntype", "version: 2.0.0\ntype");
        var dependency = wrong.Replace("{name: stdlib, version: 1.0.0}", "{name: wanted, version: 1.0.0, path: vendor}");
        TestHarness.CheckTrue("ZIP拒嵌入依赖identity错配", await RejectInstall(Zip(("module.yaml", dependency, 0), ("source/lib.rg", "", 0), ("vendor/module.yaml", Minimal, 0))));
        var failCommand = windows ? "(<nul set /p \"=B\") >> \"%BUILD_ROOT%\\install-fail-events\" & exit /b 7"
            : "printf B >> \"$BUILD_ROOT/install-fail-events\"; exit 7";
        var beforeFailure = wrong[..wrong.IndexOf("hooks:", StringComparison.Ordinal)] + "hooks: [{phase: before-install, environment: " + host + ", command: '" + failCommand + "'}]\n";
        var afterFailure = beforeFailure.Replace("2.0.0\ntype", "3.0.0\ntype").Replace("before-install", "after-install").Replace("=B", "=A").Replace("printf B", "printf A");
        TestHarness.CheckTrue("install前/后hook真实执行失败不留下新提交且原安装不动", await RejectInstall(Zip(("module.yaml", beforeFailure, 0), ("source/lib.rg", "pub func value(): i32 { return 7 }", 0)))
            && await RejectInstall(Zip(("module.yaml", afterFailure, 0), ("source/lib.rg", "pub func value(): i32 { return 7 }", 0)))
            && File.ReadAllText(Path.Combine(client, "install-fail-events")) == "BA"
            && !Directory.Exists(Path.Combine(client, "dependencies/packlib/2.0.0")) && !Directory.Exists(Path.Combine(client, "dependencies/packlib/3.0.0"))
            && File.ReadAllText(Path.Combine(installed, "resources/data/value.txt")) == "original");
        var oldReceipt = Hash(Path.Combine(context.Artifact, "module.rgi"));
        File.WriteAllText(Path.Combine(provider, "resources/data/value.txt"), "changed");
        File.WriteAllText(Path.Combine(provider, "module.yaml"), yaml + "  - {phase: after-publish, environment: all, command: 'exit 9'}\n");
        TestHarness.CheckTrue("生产afterpublish失败恢复旧资源及旧receipt", Reject(() => Publish(provider).GetAwaiter().GetResult())
            && File.ReadAllText(productResource) == "original" && Hash(Path.Combine(context.Artifact, "module.rgi")) == oldReceipt);
        File.WriteAllText(Path.Combine(provider, "module.yaml"), yaml);
        File.WriteAllText(Path.Combine(provider, "resources/extra.txt"), "case-conflict");
        File.WriteAllText(Path.Combine(provider, "module.yaml"), yaml.Replace("destination: assets}]", "destination: assets}, {source: extra.txt, destination: assets/Value.txt}]"));
        TestHarness.CheckTrue("嵌套资源规则大小写碰撞发布前拒绝且不覆盖旧产品", Reject(() => Publish(provider).GetAwaiter().GetResult())
            && File.ReadAllText(productResource) == "original");
        File.WriteAllText(Path.Combine(folder, "distribution-evidence.json"), new JsonObject
            { ["bundleSha"] = Hash(bundle), ["installed"] = installed, ["negativePaths"] = negatives,
                ["providerCompile"] = providerCompiled, ["vmReturn"] = returned, ["nativeProductsMeasured"] = false }.ToJsonString());
    }
}
