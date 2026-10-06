using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RigiCompiler.Modules;
using RigiCompiler.Middleware.Cache;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestProductionCacheRecovery()
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        Write(folder, "cacheapp", "dependencies: [{name: cacheprovider, version: 1.0.0}, {name: stdlib, version: 1.0.0}]\n");
        var providerRoot = Path.Combine(folder, "dependencies/cacheprovider/1.0.0");
        Write(providerRoot, "cacheprovider", "dependencies: [{name: stdlib, version: 1.0.0}]\n");
        Directory.CreateDirectory(Path.Combine(folder, "source")); Directory.CreateDirectory(Path.Combine(providerRoot, "source"));
        File.WriteAllText(Path.Combine(providerRoot, "source/provider.rg"), "namespace cacheapi\npub func value(): i32 { return 41 }");
        var source = Path.Combine(folder, "source/main.rg"); File.WriteAllText(source, "import cacheapi.*\npub func main(): i32 { return value() }");
        var cache = Path.Combine(folder, "cache");
        ModuleBuildResult Build() => ModuleBuildService.BuildAsync(folder, cacheRoot: cache).GetAwaiter().GetResult();
        var snapshot = Path.Combine(folder, "performance-input"); Directory.CreateDirectory(snapshot);
        File.Copy(source, Path.Combine(snapshot, "main.rg"));
        File.Copy(Path.Combine(providerRoot, "source/provider.rg"), Path.Combine(snapshot, "provider.rg"));
        File.Copy(Path.Combine(folder, "module.yaml"), Path.Combine(snapshot, "app.yaml"));
        File.Copy(Path.Combine(providerRoot, "module.yaml"), Path.Combine(snapshot, "provider.yaml"));
        // 性能样本只保留字节产物与计数，不跨轮次根住所有重复的链接对象图。
        (ModuleArtifact[] Artifacts, double Ms, int Compiles, bool Hits, bool VmOkay) Timed()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew(); var result = Build(); var ms = timer.Elapsed.TotalMilliseconds;
            return (result.Modules.Select(m => m.Artifact).ToArray(), ms, result.Modules.Count(m => m.Compiled),
                result.Modules.All(m => m.CacheStatus == "hit"), BilVm.Run(result.Entry.Linked).ReturnValue is VmI32 { Value: 41 });
        }
        var cold = Timed(); var warms = Enumerable.Range(0, 3).Select(_ => Timed()).ToArray();
        CaseAssertions.CheckTrue("生产独立BIL缓存cold1/warm3编译计数及VM41对拍", cold.Compiles == 3 && cold.VmOkay
            && warms.All(w => w.Compiles == 0 && w.Hits && w.VmOkay)
            && warms.All(w => w.Artifacts.Zip(cold.Artifacts).All(p => p.First.BilBytes.SequenceEqual(p.Second.BilBytes))));
        var own = warms[^1].Artifacts[^1];
        var samples = new JsonArray(warms.Select(w => (JsonNode)new JsonObject { ["elapsedMs"] = w.Ms,
            ["compiles"] = w.Compiles, ["cache"] = "module-bil" }).ToArray());
        File.WriteAllText(Path.Combine(folder, "module-cache-performance.json"), new JsonObject
        {
            ["coldMs"] = cold.Ms, ["coldCompiles"] = cold.Compiles, ["warm"] = samples,
            ["warmMedianMs"] = warms.Select(w => w.Ms).Order().ElementAt(1), ["backendNativeMeasured"] = false,
            ["compilerIdentity"] = NativeObjectIdentity.CompilerContentIdentity(), ["compilerAbi"] = ModuleInterface.CompilerAbi,
            ["sourceDigest"] = own.InputDigest, ["os"] = ModuleBuildContext.HostEnvironment,
            ["jobs"] = Environment.GetEnvironmentVariable("RIGI_JOBS"),
            ["leaseMemoryMiB"] = Environment.GetEnvironmentVariable("RIGI_RESOURCE_LEASE_MEMORY_MIB"),
            ["gcHeapHardLimit"] = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit")
        }.ToJsonString());
        (ModuleArtifact Artifact, bool OwnCompiled, bool DepsHit) Recover()
        { var result = Build(); return (result.Entry.Artifact, result.Entry.Compiled, result.Modules.Take(2).All(m => !m.Compiled)); }
        var pair = Path.Combine(cache, own.InputDigest, "module.rgi");
        File.WriteAllBytes(pair, [0, 1, 2]);
        var repaired = Recover();
        CaseAssertions.CheckTrue("生产pair摘要坏仅重编该app", repaired.OwnCompiled && repaired.DepsHit
            && repaired.Artifact.BilBytes.SequenceEqual(own.BilBytes));
        var document = JsonNode.Parse(own.InterfaceBytes)!.AsObject(); document["payload"]!["forged"] = true;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document["payload"]!.ToJsonString())));
        document["apiHash"] = hash;
        var poison = own with { ApiHash = hash, InterfaceBytes = Encoding.UTF8.GetBytes(document.ToJsonString()) };
        ModuleArtifactEnvelope.Write(pair, poison);
        File.Copy(pair, Path.Combine(folder, "poisoned-before-repair.rgi"));
        File.WriteAllText(Path.Combine(cache, own.InputDigest, "manifest"), own.InputDigest + "\n" + ArtifactCache.HashFile(pair) + "\n");
        var strict = Recover();
        CaseAssertions.CheckTrue("生产重hash未知DTO键也只重编该app", strict.OwnCompiled && strict.DepsHit
            && strict.Artifact.InterfaceBytes.SequenceEqual(own.InterfaceBytes));
        File.AppendAllText(source, "\n// 同key两请求的全生产路径\n");
        var receipt = Path.Combine(folder, "artifact/default/module.rgi");
        var requests = Enumerable.Range(0, 2).Select(_ => Task.Run(Build)).ToArray();
        var oldHash = ArtifactCache.HashFile(receipt);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var completeReads = 0;
        while (requests.Any(t => !t.IsCompleted))
        {
            using (var stream = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                observed.Add(Convert.ToHexString(SHA256.HashData(stream)));
            completeReads++;
            Task.WhenAny(Task.WhenAll(requests), Task.Delay(100)).GetAwaiter().GetResult();
        }
        Task.WaitAll(requests);
        CaseAssertions.CheckTrue("生产同key双请求只有一次app真实Compile", requests.Sum(t => t.Result.Modules.Count(m => m.Compiled)) == 1
            && requests.All(t => t.Result.Entry.Artifact.BilBytes.SequenceEqual(requests[0].Result.Entry.Artifact.BilBytes)));
        var newHash = ArtifactCache.HashFile(receipt);
        CaseAssertions.CheckTrue("并发发布中receipt连续读取始终完整且最终pair匹配", completeReads != 0 && oldHash != newHash
            && observed.All(h => h == oldHash || h == newHash)
            && ModuleArtifactEnvelope.Read(receipt, "cacheapp@1.0.0").InterfaceBytes.SequenceEqual(requests[0].Result.Entry.Artifact.InterfaceBytes));
        File.WriteAllText(Path.Combine(folder, "receipt-observation.json"), new JsonObject { ["oldHash"] = oldHash,
            ["newHash"] = newHash, ["completeReads"] = completeReads, ["observed"] = new JsonArray(observed.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()) }.ToJsonString());
        var previous = File.ReadAllBytes(receipt);
        File.WriteAllText(source, "pub func main(): i32 { return missingValue }");
        var starts = 0;
        var failed = Reject(() => ModuleBuildService.BuildAsync(folder, cacheRoot: cache, publish: built =>
        { if (built.Artifact.ModuleId == "cacheapp@1.0.0") starts++; return Task.CompletedTask; }).GetAwaiter().GetResult());
        var context = ModuleBuildContext.Create(ModuleBuildService.Resolve(folder));
        var failedKey = ModuleBuildIdentity.Compute(context, ModuleSources.Read(folder, context.Module.Configuration.Sources), [],
            cold.Artifacts.Take(2).ToArray(), NativeObjectIdentity.CompilerContentIdentity()!);
        CaseAssertions.CheckTrue("真实语义失败不发布新cache/receipt且不调用app发布器", failed && starts == 0
            && !Directory.Exists(Path.Combine(cache, failedKey)) && File.ReadAllBytes(receipt).SequenceEqual(previous));
    }
    private static void TestCacheInputsProfilesHooks()
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        var before = OperatingSystem.IsWindows() ? "echo B>>\"%RIGI_ARTIFACT%\\hook.log\"" : "printf B >> \"$RIGI_ARTIFACT/hook.log\"";
        var after = OperatingSystem.IsWindows() ? "echo A>>\"%RIGI_ARTIFACT%\\hook.log\"" : "printf A >> \"$RIGI_ARTIFACT/hook.log\"";
        var config = "dependencies: [{name: stdlib, version: 1.0.0}]\ndefault-profile: sandbox\n"
            + "profiles: {sandbox: {target: vm, product: product/shared}, alternate: {target: vm, product: product/shared}}\n"
            + "hooks:\n  - {phase: before-publish, environment: all, command: '" + before + "', inputs: [build-input.txt]}\n"
            + "  - {phase: after-publish, environment: all, command: '" + after + "', inputs: [build-input.txt]}\n";
        Write(folder, "hookapp", config);
        Directory.CreateDirectory(Path.Combine(folder, "source"));
        File.WriteAllText(Path.Combine(folder, "build-input.txt"), "one");
        var source = Path.Combine(folder, "source/main.rg");
        File.WriteAllText(source, "import core.serialization.Serializable\npub func identity\\<T>(value: T): T { return value }\npub func main(): i32 { return 23 }");
        var cache = Path.Combine(folder, "cache");
        ModuleBuildResult Build(string? profile = null) => ModuleBuildService.BuildAsync(folder, profile, cache).GetAwaiter().GetResult();
        var cold = Build(); var hot = Build();
        var hookLog = Path.Combine(folder, "artifact/sandbox/hook.log");
        CaseAssertions.CheckTrue("命中也真实执行before/after且host与VMtarget分离", !hot.Entry.Compiled
            && File.ReadAllText(hookLog).Count(c => c == 'B') == 2 && File.ReadAllText(hookLog).Count(c => c == 'A') == 2);
        File.WriteAllText(Path.Combine(folder, "build-input.txt"), "two");
        var inputChanged = Build();
        CaseAssertions.CheckTrue("声明hook输入字节变动真实失效app而Std热命中", inputChanged.Entry.Compiled && !inputChanged.Modules[0].Compiled);
        File.WriteAllText(source, File.ReadAllText(source).Replace("identity\\<T>", "identity\\<T with Serializable>", StringComparison.Ordinal));
        var constraints = Build();
        CaseAssertions.CheckTrue("GP约束变动真实失效及接口API变化", constraints.Entry.Compiled
            && constraints.Entry.Artifact.ApiHash != inputChanged.Entry.Artifact.ApiHash && !constraints.Modules[0].Compiled);
        var alternate = Build("alternate");
        CaseAssertions.CheckTrue("任意profile真实独立键及自己的artifact目录", alternate.Entry.Compiled
            && alternate.Entry.Artifact.InputDigest != constraints.Entry.Artifact.InputDigest && !alternate.Modules[0].Compiled
            && File.Exists(Path.Combine(folder, "artifact/alternate/module.rgi")));
        var context = constraints.Entry.Context; var inputs = ModuleSources.Read(folder, context.Module.Configuration.Sources);
        ModuleArtifact[] dependencies = [constraints.Modules[0].Artifact];
        var key = ModuleBuildIdentity.Compute(context, inputs, [], dependencies, "compiler-one");
        CaseAssertions.CheckTrue("生产键函数明确分隔compiler内容ABI及信任来源", key != ModuleBuildIdentity.Compute(context, inputs, [], dependencies, "compiler-two")
            && key != ModuleBuildIdentity.Compute(context, inputs, [], dependencies, "compiler-one", "abi-next")
            && key != ModuleBuildIdentity.Compute(context, inputs, [], [dependencies[0] with { CompilerOwned = false }], "compiler-one"));
        Write(folder, "hookapp", config.Replace("inputs: [build-input.txt]", "inputs: []", StringComparison.Ordinal));
        var bypassOne = Build(); var bypassTwo = Build();
        CaseAssertions.CheckTrue("任意before无声明输入保守bypass每轮真实编译", bypassOne.Entry.Compiled && bypassTwo.Entry.Compiled
            && bypassOne.Entry.CacheStatus == "bypass" && bypassTwo.Entry.CacheStatus == "bypass");
        var untrusted = Path.Combine(folder, "ordinary-stdlib"); Write(untrusted, "stdlib");
        Directory.CreateDirectory(Path.Combine(untrusted, "source")); File.WriteAllText(Path.Combine(untrusted, "source/main.rg"), "pub func main(): i32 { return 0 }");
        CaseAssertions.CheckTrue("普通同名stdlib根不获内建resolver信任且缺显式依赖拒绝", !ModuleBuildService.Resolve(untrusted).Entry.CompilerOwned
            && Reject(() => ModuleBuildService.BuildAsync(untrusted, cacheRoot: cache).GetAwaiter().GetResult()));
        File.WriteAllText(Path.Combine(folder, "hook-counts.json"), new JsonObject
            { ["cold"] = cold.Entry.CacheStatus, ["hot"] = hot.Entry.CacheStatus, ["inputChanged"] = inputChanged.Entry.CacheStatus,
                ["constraints"] = constraints.Entry.CacheStatus, ["alternate"] = alternate.Entry.CacheStatus,
                ["bypassOne"] = bypassOne.Entry.CacheStatus, ["bypassTwo"] = bypassTwo.Entry.CacheStatus }.ToJsonString());
    }
    private static void TestProductionDagCache()
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        Write(folder, "consumer", "dependencies: [{name: provider, version: 1.0.0}, {name: stdlib, version: 1.0.0}]\n"
            + "default-profile: sandbox\nprofiles: {sandbox: {target: vm}}\n");
        var providerRoot = Path.Combine(folder, "dependencies/provider/1.0.0");
        Write(providerRoot, "provider", "dependencies: [{name: stdlib, version: 1.0.0}]\n");
        Directory.CreateDirectory(Path.Combine(folder, "source")); Directory.CreateDirectory(Path.Combine(providerRoot, "source"));
        var providerSource = Path.Combine(providerRoot, "source/provider.rg");
        File.WriteAllText(providerSource, "namespace api\npub func result(value: i32 = 7): i32 { return value }");
        File.WriteAllText(Path.Combine(folder, "source/main.rg"), "import api.*\npub func main(): i32 { return result() }");
        var cache = Path.Combine(folder, "module-cache");
        ModuleBuildResult Build() => ModuleBuildService.BuildAsync(folder, cacheRoot: cache).GetAwaiter().GetResult();
        int Run(ModuleBuildResult result) => BilVm.Run(result.Entry.Linked).ReturnValue is VmI32 value ? value.Value : -1;
        var cold = Build(); var hot = Build();
        CaseAssertions.CheckTrue("生产DAG各模块冷编一次热编零次", cold.Modules.Count == 3 && cold.Modules.All(m => m.Compiled)
            && hot.Modules.All(m => !m.Compiled && m.CacheStatus == "hit"));
        CaseAssertions.CheckTrue("artifact-only生产Std/provider/app冷热VM等价7", Run(cold) == 7 && Run(hot) == 7
            && cold.Modules.Zip(hot.Modules).All(pair => pair.First.Artifact.BilBytes.SequenceEqual(pair.Second.Artifact.BilBytes)));
        CaseAssertions.CheckTrue("依赖选自身default且共享入口PRODUCT", hot.Modules[1].Context.ProfileName == "default"
            && hot.Entry.Context.ProfileName == "sandbox" && hot.Modules.All(m => m.Context.Product == hot.Entry.Context.Product));
        var prior = hot.Modules[1].Artifact;
        File.WriteAllText(providerSource, File.ReadAllText(providerSource).Replace("= 7", "= 8", StringComparison.Ordinal));
        var changed = Build();
        CaseAssertions.CheckTrue("默认helper实现变更失效provider与consumer且Std仍hit", !changed.Modules[0].Compiled
            && changed.Modules[1].Compiled && changed.Entry.Compiled && Run(changed) == 8
            && prior.InputDigest != changed.Modules[1].Artifact.InputDigest);
        File.Move(providerSource, Path.Combine(providerRoot, "provider.rg.removed"));
        File.AppendAllText(Path.Combine(folder, "source/main.rg"), "\n// 仅consumer本轮改变\n");
        var removed = Build();
        CaseAssertions.CheckTrue("移走provider源码真实prebuilt非contentshit且只编consumer", !removed.Modules[0].Compiled
            && !removed.Modules[1].Compiled && removed.Modules[1].CacheStatus == "prebuilt"
            && removed.Modules[1].OwnSourceCount == 0 && removed.Entry.Compiled && removed.Entry.OwnSourceCount == 1 && Run(removed) == 8);
        File.WriteAllBytes(Path.Combine(folder, "linked.bil"), Encoding.UTF8.GetBytes(BilWriter.Write(removed.Entry.Linked)));
        JsonArray Counts(ModuleBuildResult result) => new(result.Modules.Select(m => (JsonNode)new JsonObject
            { ["id"] = m.Artifact.ModuleId, ["compiled"] = m.Compiled, ["status"] = m.CacheStatus, ["ownSources"] = m.OwnSourceCount }).ToArray());
        File.WriteAllText(Path.Combine(folder, "build-counts.json"), new JsonObject
            { ["cold"] = Counts(cold), ["hot"] = Counts(hot), ["changed"] = Counts(changed), ["sourceRemoved"] = Counts(removed) }.ToJsonString());
    }
    private static void TestModulePairCache()
    {
        var (stdlib, _) = CompileInterfaceProbe("stdlib@1.0.0", StdlibSources.ParseAll().ToArray(), [], true);
        ModuleArtifact[] dependencies = [stdlib];
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source"); Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "a.rg"), "pub func value(): i32 { return 7 }");
        File.WriteAllText(Path.Combine(source, "z.rg"), "pub func other(): i32 { return 8 }");
        File.WriteAllText(Path.Combine(source, "nested", "q.rg"), "pub func nested(): i32 { return 9 }");
        var inputs = ModuleSources.Read(folder, ["z.rg", "**/*.rg", "a.rg"]);
        CaseAssertions.CheckTrue("selector优先且每项ordinal排序去重", inputs.Select(i => i.RelativePath)
            .SequenceEqual(["z.rg", "a.rg", "nested/q.rg"]));
        var key = ArtifactCache.Identity(ModuleArtifactCache.Schema, ModuleInterface.CompilerAbi,
            "cached@1.0.0", string.Join('|', inputs.Select(i => i.RelativePath)),
            string.Join('|', inputs.Select(i => Convert.ToBase64String(i.Bytes))));
        var cache = Path.Combine(folder, "cache");
        var builds = 0;
        ModuleArtifact Compile()
        {
            Interlocked.Increment(ref builds);
            return CompileInterfaceProbe("cached@1.0.0", Frontend.ParseRoots(inputs.Select(i => i.Input()).ToArray()),
                dependencies, inputDigest: key).Artifact;
        }
        ModuleArtifactCache.Result Get(string destination) => ModuleArtifactCache.Get(cache, key,
            "cached@1.0.0", false, dependencies, Path.Combine(folder, destination), Compile);
        var cold = Get("cold.rgi"); var hot = Get("hot.rgi");
        CaseAssertions.CheckTrue("独立module冷编一次热不编且字节等价", builds == 1 && cold.Compiled && !hot.Compiled
            && cold.Artifact.InterfaceBytes.SequenceEqual(hot.Artifact.InterfaceBytes)
            && cold.Artifact.BilBytes.SequenceEqual(hot.Artifact.BilBytes));
        CaseAssertions.CheckTrue("包不持久化compilerOwned信任位", !ModuleArtifactEnvelope.Read(
            Path.Combine(folder, "hot.rgi"), "cached@1.0.0").CompilerOwned);
        var entry = Path.Combine(cache, key); var pair = Path.Combine(entry, "module.rgi");
        File.WriteAllBytes(pair, [1, 2, 3]);
        var repaired = Get("repaired.rgi");
        CaseAssertions.CheckTrue("单文件摘要损坏重建自愈", builds == 2 && repaired.Compiled
            && File.ReadAllBytes(pair).SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "repaired.rgi"))));
        // whole-file摘要正确仍须消费层验DTO/BIL，不只核manifest。
        var badBil = Encoding.UTF8.GetBytes("这不是BIL");
        var document = JsonNode.Parse(hot.Artifact.InterfaceBytes)!.AsObject();
        document["bilDigest"] = Convert.ToHexString(SHA256.HashData(badBil));
        var poisoned = hot.Artifact with { BilBytes = badBil, InterfaceBytes = Encoding.UTF8.GetBytes(document.ToJsonString()) };
        ModuleArtifactEnvelope.Write(pair, poisoned);
        File.WriteAllText(Path.Combine(entry, "manifest"), key + "\n" + ArtifactCache.HashFile(pair) + "\n");
        var validated = Get("validated.rgi");
        CaseAssertions.CheckTrue("重算manifest但非法BIL仍自愈", builds == 3 && validated.Compiled);
        var concurrentKey = ArtifactCache.Identity(key, "concurrent"); var concurrentBuilds = 0;
        var tasks = Enumerable.Range(0, 3).Select(i => Task.Run(() => ModuleArtifactCache.Get(cache, concurrentKey,
            "cached@1.0.0", false, dependencies, Path.Combine(folder, "concurrent-" + i + ".rgi"), () =>
            {
                Interlocked.Increment(ref concurrentBuilds);
                return CompileInterfaceProbe("cached@1.0.0", Frontend.ParseRoots(inputs.Select(s => s.Input()).ToArray()),
                    dependencies, inputDigest: concurrentKey).Artifact;
            }))).ToArray();
        Task.WaitAll(tasks);
        CaseAssertions.CheckTrue("同key真实语义builder单飞", concurrentBuilds == 1 && tasks.Count(t => t.Result.Compiled) == 1
            && tasks.All(t => t.Result.Artifact.BilBytes.SequenceEqual(tasks[0].Result.Artifact.BilBytes)));
        var failure = new ModuleConfigurationException("本次builder失败"); var failedKey = ArtifactCache.Identity(key, "failed");
        var sameFailure = false;
        try { ModuleArtifactCache.Get(cache, failedKey, "cached@1.0.0", false, dependencies,
            Path.Combine(folder, "failed.rgi"), () => throw failure); }
        catch (ModuleConfigurationException ex) { sameFailure = ReferenceEquals(ex, failure); }
        CaseAssertions.CheckTrue("builder原异常保留且失败不发布entry", sameFailure && !Directory.Exists(Path.Combine(cache, failedKey)));
        var blocked = Path.Combine(folder, "blocked-cache"); File.WriteAllText(blocked, "不是目录");
        var bypass = ModuleArtifactCache.Get(blocked, key, "cached@1.0.0", false, dependencies,
            Path.Combine(folder, "bypass.rgi"), Compile);
        CaseAssertions.CheckTrue("缓存设施IO失败本请求直接编且不吞builder", !bypass.CacheUsed && bypass.Compiled && builds == 4);
        if (OperatingSystem.IsLinux())
        {
            File.CreateSymbolicLink(Path.Combine(source, "escape.rg"), Path.Combine(folder, "cold.rgi"));
            CaseAssertions.CheckTrue("源selector消费拒symlink", Reject(() => ModuleSources.Read(folder, ["**/*.rg"])));
        }
    }
}
