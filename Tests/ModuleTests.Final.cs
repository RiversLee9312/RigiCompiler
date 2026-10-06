using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Modules;
using RigiCompiler.Middleware.Cache;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestProductionLateIntegration()
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "worker-identity.json"), ModuleWorkerIdentity().ToJsonString());
        var root = Path.Combine(folder, "application");
        var provider = Path.Combine(root, "dependencies/late-provider/1.0.0");
        Directory.CreateDirectory(Path.Combine(root, "source")); Directory.CreateDirectory(Path.Combine(provider, "source"));
        File.WriteAllText(Path.Combine(root, "module.yaml"), "schema: 1\nname: late-app\nversion: 1.0.0\ntype: executable\n"
            + "source: [app.rg]\ndependencies: [{name: stdlib, version: 1.0.0}, {name: late-provider, version: 1.0.0}]\n"
            + "default-profile: debug\nprofiles: {debug: {target: vm}}\n");
        File.WriteAllText(Path.Combine(provider, "module.yaml"), "schema: 1\nname: late-provider\nversion: 1.0.0\ntype: static-library\n"
            + "source: [provider.rg]\ndependencies: [{name: stdlib, version: 1.0.0}]\ndefault-profile: debug\nprofiles: {debug: {target: vm}}\n");
        var app = Path.Combine(root, "source/app.rg");
        File.WriteAllText(app, LateApplicationSource); File.WriteAllText(Path.Combine(provider, "source/provider.rg"), LateProviderSource);
        var inputs = Path.Combine(folder, "performance-input"); Directory.CreateDirectory(inputs);
        File.Copy(app, Path.Combine(inputs, "app.rg")); File.Copy(Path.Combine(provider, "source/provider.rg"), Path.Combine(inputs, "provider.rg"));
        File.Copy(Path.Combine(root, "module.yaml"), Path.Combine(inputs, "app.yaml")); File.Copy(Path.Combine(provider, "module.yaml"), Path.Combine(inputs, "provider.yaml"));
        var cache = Path.Combine(folder, "module-cache");
        // 每轮只保留哈希、计数与耗时；不跨轮次根住多个完整符号/链接/VM 对象图。
        (JsonObject Evidence, string LinkedHash, string[] ArtifactHashes, bool Okay, int Compiles, bool Hits) Sample(string label)
        {
            var timer = Stopwatch.StartNew();
            var result = ModuleBuildService.BuildAsync(root, cacheRoot: cache).GetAwaiter().GetResult();
            var elapsed = timer.Elapsed.TotalMilliseconds;
            var selected = ModuleEntrypoint.Select(result.Entry);
            var linked = BilWriter.Write(selected.Module);
            var hash = ArtifactCache.Identity(linked);
            if (label is "cold" or "source-removed") File.WriteAllText(Path.Combine(folder, "linked-" + label + ".bil"), linked);
            var run = BilVm.Run(selected.Module, maxSteps: 1_000_000_000, entryPoint: selected.Canonical);
            var evidence = new JsonObject { ["phase"] = label, ["elapsedMs"] = elapsed, ["linkedHash"] = hash,
                ["vmExit"] = run.ReturnValue is VmI32 value ? value.Value : null, ["exception"] = run.Exception?.ToString(),
                ["modules"] = new JsonArray(result.Modules.Select(m => (JsonNode)new JsonObject
                { ["moduleId"] = m.Artifact.ModuleId, ["compiled"] = m.Compiled, ["status"] = m.CacheStatus,
                    ["ownSources"] = m.OwnSourceCount, ["inputDigest"] = m.Artifact.InputDigest, ["apiHash"] = m.Artifact.ApiHash,
                    ["bilHash"] = Convert.ToHexString(SHA256.HashData(m.Artifact.BilBytes)), ["interfaceHash"] = Convert.ToHexString(SHA256.HashData(m.Artifact.InterfaceBytes)) }).ToArray()) };
            File.WriteAllText(Path.Combine(folder, label + ".json"), evidence.ToJsonString());
            return (evidence, hash, result.Modules.Select(m => Convert.ToHexString(SHA256.HashData(m.Artifact.BilBytes))).ToArray(),
                run.Exception == null && run.ReturnValue is VmI32 { Value: 0 }, result.Modules.Count(m => m.Compiled),
                result.Modules.All(m => m.CacheStatus == "hit"));
        }
        var cold = Sample("cold");
        CaseAssertions.CheckTrue("production完整Std/provider/app冷编译三模块且真实全guard VM0", cold.Compiles == 3 && cold.Okay);
        var warm = Enumerable.Range(1, 3).Select(i => Sample("warm-" + i)).ToArray();
        CaseAssertions.CheckTrue("production模块warm3全compile0/各自hit/完整GP wrapper Serializable协程VM0字节等价",
            warm.All(w => w.Compiles == 0 && w.Hits && w.Okay && w.LinkedHash == cold.LinkedHash && w.ArtifactHashes.SequenceEqual(cold.ArtifactHashes)));
        // 移走 provider 真源码，再改变 consumer 输入，确保是消费者自身真实 P1–P4 而非 app hit。
        Directory.Move(Path.Combine(provider, "source"), Path.Combine(folder, "removed-provider-source"));
        File.AppendAllText(app, "\n// provider 已无源码，本消费者重新编译完整 late/helper/GP/wrapper 图\n");
        var removed = Sample("source-removed");
        var modules = removed.Evidence["modules"]!.AsArray();
        var dependency = modules.Single(m => m!["moduleId"]!.GetValue<string>() == "late-provider@1.0.0")!;
        var standard = modules.Single(m => m!["moduleId"]!.GetValue<string>() == "stdlib@1.0.0")!;
        var own = modules.Single(m => m!["moduleId"]!.GetValue<string>() == "late-app@1.0.0")!;
        CaseAssertions.CheckTrue("production缺源provider compile0/AST0/prebuilt且Std hit，consumer真实own AST编译",
            !dependency["compiled"]!.GetValue<bool>() && dependency["ownSources"]!.GetValue<int>() == 0
            && dependency["status"]!.GetValue<string>() == "prebuilt" && !standard["compiled"]!.GetValue<bool>()
            && standard["status"]!.GetValue<string>() == "hit" && own["compiled"]!.GetValue<bool>()
            && own["ownSources"]!.GetValue<int>() == 1 && removed.Compiles == 1);
        CaseAssertions.CheckTrue("缺provider AST consumer所有运行guard VM0且固定Native输入与冷/热BIL等价",
            removed.Okay && removed.LinkedHash == cold.LinkedHash && removed.ArtifactHashes.SequenceEqual(cold.ArtifactHashes));
        var times = warm.Select(w => w.Evidence["elapsedMs"]!.GetValue<double>()).Order().ToArray();
        File.WriteAllText(Path.Combine(folder, "module-cache-performance.json"), new JsonObject
        { ["compilerIdentity"] = NativeObjectIdentity.CompilerContentIdentity(), ["compilerAbi"] = ModuleInterface.CompilerAbi,
            ["cold"] = cold.Evidence.DeepClone(), ["warm"] = new JsonArray(warm.Select(w => w.Evidence.DeepClone()).ToArray()),
            ["warmMedianMs"] = times[1], ["sourceRemoved"] = removed.Evidence.DeepClone(), ["nativeMeasured"] = false,
            ["jobs"] = Environment.GetEnvironmentVariable("RIGI_JOBS"), ["gcHeapHardLimit"] = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
            ["leaseMemoryMiB"] = Environment.GetEnvironmentVariable("RIGI_RESOURCE_LEASE_MEMORY_MIB") }.ToJsonString());
    }
}
