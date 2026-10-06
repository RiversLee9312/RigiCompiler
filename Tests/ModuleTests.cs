using RigiCompiler.Modules;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    internal static TestSuiteData Spec => new("Module", Cases, sectionTitle: "Module");
    internal static IEnumerable<TestInventory.Case> InventoryCases => Spec.Cases.Select((c, i) => new TestInventory.Case(i, c.Label,
        MemoryMiB: c.Label is "A2.OriginCompilation" or "A2.OriginReserved" or "B1.InterfaceRoundTrip" or "B2.ProviderFactories" or "B2.LateApplication" or "C1.ModulePairCache" or "C2.ProductionDagCache" or "C3.CacheInputsProfilesHooks" or "C4.ProductionCacheRecovery" or "D0.ProgramArguments" or "D1.ModuleCommand" or "D2.ModuleDistribution" or "E1.NativeExportClosure" or "E2.NativeLibraries" or "E3.NativeArguments" or "E3.NativeCoroutineArguments" or "E4.StdNativePublication" or "E4.LibraryInitialization" or "E4.FullModuleCli" or "E4.FullModuleLibraries" or "F.ProductionLateIntegration" ? 2048 : 512));


    private static readonly (string Label, Action Run)[] Cases =
    [
        ("A1.StrictConfiguration", TestConfiguration),
        ("A1.DependencyGraph", TestDependencyGraph),
        ("A1.HookEnvironment", TestHookEnvironment)
        ,("A2.TransactionalLink", TestTransactionalLink)
        ,("A2.TypedResources", TestTypedResources)
        ,("A2.OriginCompilation", TestOriginCompilation)
        ,("B1.ManifestBootstrap", TestManifestBootstrap)
        ,("A2.OriginReserved", TestOriginReserved)
        ,("B1.InterfaceRoundTrip", TestInterfaceRoundTrip)
        ,("B2.ProviderFactories", TestProviderFactories)
        ,("B2.LateApplication", TestLateApplication)
        ,("C1.ModulePairCache", TestModulePairCache)
        ,("C2.ProductionDagCache", TestProductionDagCache)
        ,("C3.CacheInputsProfilesHooks", TestCacheInputsProfilesHooks)
        ,("C4.ProductionCacheRecovery", TestProductionCacheRecovery)
        ,("D0.ProgramArguments", TestProgramArguments)
        ,("D1.ModuleCommand", TestModuleCommand)
        ,("D2.ModuleDistribution", TestModuleDistribution)
        ,("D2.BundleProductLinks", TestBundleProductLinks)
        ,("E1.NativeExportRoots", TestNativeExportRoots)
        ,("E1.NativeExportClosure", TestNativeExportClosure)
        ,("E2.NativeLibraries", TestNativeLibraries)
        ,("E3.NativeArguments", TestNativeArguments)
        ,("E3.NativeCoroutineArguments", TestNativeCoroutineArguments)
        ,("E4.StdNativePublication", TestStdNativePublication)
        ,("E4.LibraryInitialization", TestNativeLibraryInitialization)
        ,("E4.FullModuleCli", TestFullModuleCli)
        ,("E4.FullModuleLibraries", TestFullModuleLibraries)
        ,("F.ProductionLateIntegration", TestProductionLateIntegration)
    ];
    private const string Minimal = "schema: 1\nname: app\nversion: 1.0.0\ntype: executable\n";
    private static bool Reject(Action action)
    {
        try { action(); return false; }
        catch (ModuleConfigurationException) { return true; }
    }
    private static void TestConfiguration()
    {
        var config = ModuleConfigurationReader.Read(Minimal);
        CaseAssertions.CheckTrue("schema 最小值与稳定 identity", config.ModuleId == "app@1.0.0"
            && config.SelectProfile().Target == ModuleRunTarget.Native && config.Sources.SequenceEqual(["**/*.rg"]));
        foreach (var (label, yaml) in new (string, string)[]
        {
            ("未知字段", Minimal + "typo: x\n"), ("重复 key", Minimal + "name: app\n"),
            ("name LF", Minimal.Replace("name: app", "name: \"app\\n\"")),
            ("version LF", Minimal.Replace("version: 1.0.0", "version: \"1.0.0\\n\"")),
            ("export LF", Minimal + "exports: {\"foo\\n\": bar}\n"),
            ("profile LF", Minimal + "profiles: {\"default\\n\": {target: vm}}\n"),
            ("alias 循环", Minimal + "hooks: &a [*a]\n"), ("多 document", Minimal + "---\n" + Minimal),
            ("source 越界", Minimal + "source: [../bad.rg]\n"), ("absolute source", Minimal + "source: [/bad.rg]\n"),
            ("Windows path", Minimal + "source: ['C:\\bad.rg']\n"), ("unknown schema", Minimal.Replace("schema: 1", "schema: 2")),
            ("version", Minimal.Replace("1.0.0", "01.0.0")), ("prerelease", Minimal.Replace("1.0.0", "1.0.0-01")),
            ("profile missing", Minimal + "default-profile: missing\n"),
            ("profile unknown field", Minimal + "profiles: {default: {target: vm, targets: vm}}\n"),
            ("hook multiline", Minimal + "hooks: [{phase: before-publish, environment: all, command: \"echo a\\necho b\"}]\n"),
            ("export runtime reserved", Minimal + "exports: {rigi_fake: foo}\n"),
            ("duplicate dependency", Minimal + "dependencies: [{name: lib, version: 1.0.0}, {name: lib, version: 1.0.0}]\n"),
            ("resource case collision", Minimal + "resources: [{source: one, destination: A}, {source: two, destination: a}]\n")
        }) CaseAssertions.CheckTrue("严格配置拒绝 " + label, Reject(() => ModuleConfigurationReader.Read(yaml)));
        var named = ModuleConfigurationReader.Read(Minimal + "default-profile: sandbox\nprofiles:\n  sandbox: {target: vm}\n  fast: {target: native, product: output/fast}\n");
        CaseAssertions.CheckTrue("任意 profile 与显式 target", named.SelectProfile().Name == "sandbox"
            && named.SelectProfile("fast").Target == ModuleRunTarget.Native);
    }
    private static void TestDependencyGraph()
    {
        WithFixture(root =>
        {
            Write(root, "app", "dependencies: [{name: z, version: 1.0.0}, {name: a, version: 1.0.0}]\n");
            Write(Path.Combine(root, "dependencies/a/1.0.0"), "a", "dependencies: [{name: common, version: 1.0.0}]\n");
            Write(Path.Combine(root, "dependencies/z/1.0.0"), "z", "dependencies: [{name: common, version: 1.0.0}]\n");
            Write(Path.Combine(root, "dependencies/common/1.0.0"), "common");
            var resolution = ModuleResolver.Resolve(root);
            CaseAssertions.CheckTrue("入口尾分隔符等价", ModuleResolver.Resolve(root + Path.DirectorySeparatorChar).Ordered
                .Select(m => m.Root).SequenceEqual(resolution.Ordered.Select(m => m.Root))
                && ModulePaths.Inside(root + Path.DirectorySeparatorChar, "product/default") == ModulePaths.Inside(root, "product/default"));
            CaseAssertions.CheckTrue("diamond DAG 稳定拓扑且唯一", resolution.Ordered.Select(m => m.ModuleId)
                .SequenceEqual(["common@1.0.0", "a@1.0.0", "z@1.0.0", "app@1.0.0"]));
            Write(Path.Combine(root, "dependencies/common/1.0.0"), "common", "dependencies: [{name: a, version: 1.0.0}]\n");
            CaseAssertions.CheckTrue("环在编译之前拒绝", Reject(() => ModuleResolver.Resolve(root)));
            Write(Path.Combine(root, "dependencies/common/1.0.0"), "wrong");
            CaseAssertions.CheckTrue("安装 dependency identity 不匹配拒绝", Reject(() => ModuleResolver.Resolve(root)));
            Write(Path.Combine(root, "dependencies/common/1.0.0"), "common");
            Write(Path.Combine(root, "dependencies/z/1.0.0"), "z", "dependencies: [{name: common, version: 2.0.0}]\n");
            Write(Path.Combine(root, "dependencies/common/2.0.0"), "common", version: "2.0.0");
            CaseAssertions.CheckTrue("同名不同 version 拒绝", Reject(() => ModuleResolver.Resolve(root)));
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "escape"), Path.GetTempPath());
                CaseAssertions.CheckTrue("已有 symlink 越界拒绝", Reject(() => ModulePaths.Inside(root, "escape/any")));
                File.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(root, "absent"));
                CaseAssertions.CheckTrue("悬空 symlink 拒绝", Reject(() => ModulePaths.Inside(root, "dangling")));
                var leafRoot = Path.Combine(root, "leaf"); Directory.CreateDirectory(leafRoot);
                File.CreateSymbolicLink(Path.Combine(leafRoot, "module.yaml"), Path.Combine(root, "module.yaml"));
                CaseAssertions.CheckTrue("配置叶节点 symlink 拒绝", Reject(() => ModuleResolver.Resolve(leafRoot)));
            }
        });
    }
    private static void TestHookEnvironment()
    {
        var raw = new System.Diagnostics.ProcessStartInfo { Arguments = "/d /s /c \"type \"%RIGI_RES%\\file.txt\"\"" };
        CaseAssertions.CheckTrue("Windows cmd 原始命令保留引号静态契约", RigiCompiler.PerfBaseline.ProcessIsolation.BuildWindowsCommand("cmd.exe", raw)
            == "cmd.exe /d /s /c \"type \"%RIGI_RES%\\file.txt\"\"");
        var ordinary = new System.Diagnostics.ProcessStartInfo(); ordinary.ArgumentList.Add("a\"b"); ordinary.ArgumentList.Add("c d");
        CaseAssertions.CheckTrue("Windows 普通 argv 仍用原CRT规则", RigiCompiler.PerfBaseline.ProcessIsolation.BuildWindowsCommand("tool.exe", ordinary)
            == "tool.exe \"a\\\"b\" \"c d\"");
        WithFixture(root =>
        {
            var shell = OperatingSystem.IsWindows()
                ? "echo %RIGI_SRC%&&echo %BUILD_ROOT%&&echo %PRODUCT%"
                : "printf '%s\\n' \"$RIGI_SRC\" \"$BUILD_ROOT\" \"$PRODUCT\"";
            string Hook(string command, string environment = "all") => "hooks: [{phase: before-publish, environment: " + environment
                + ", command: '" + command.Replace("'", "''") + "'}]\n";
            Write(root, "app", "dependencies: [{name: dep, version: 1.0.0}]\n" + Hook(shell));
            var depRoot = Path.Combine(root, "dependencies/dep/1.0.0");
            Write(depRoot, "dep", Hook(shell));
            var resolution = ModuleResolver.Resolve(root);
            var entry = ModuleBuildContext.Create(resolution);
            var dependency = entry.ForModule(resolution.Ordered[0]);
            var before = Environment.GetEnvironmentVariable("RIGI_SRC");
            var outputs = Task.WhenAll(ModuleHooks.RunAsync(entry, ModuleHookPhase.BeforePublish),
                ModuleHooks.RunAsync(dependency, ModuleHookPhase.BeforePublish)).GetAwaiter().GetResult();
            string Expected(ModuleBuildContext c) => string.Join(Environment.NewLine, [c.Source, c.BuildRoot, c.Product, ""]);
            CaseAssertions.CheckTrue("并行 hooks 各自 SRC/相同入口 PRODUCT", outputs[0][0].Stdout == Expected(entry)
                && outputs[1][0].Stdout == Expected(dependency));
            CaseAssertions.CheckTrue("父环境不被 hook 注入污染", before == Environment.GetEnvironmentVariable("RIGI_SRC"));
            Write(depRoot, "dep", Hook("exit 7"));
            var failing = entry.ForModule(ModuleResolver.Resolve(root).Ordered[0]);
            CaseAssertions.CheckTrue("hook 非零失败传播", Reject(() => ModuleHooks.RunAsync(failing,
                ModuleHookPhase.BeforePublish).GetAwaiter().GetResult()));
            Write(depRoot, "dep", Hook("exit 7", ModuleBuildContext.HostEnvironment == "linux" ? "windows" : "linux"));
            var foreign = entry.ForModule(ModuleResolver.Resolve(root).Ordered[0]);
            CaseAssertions.CheckTrue("hook 以执行宿主匹配", !foreign.Hooks(ModuleHookPhase.BeforePublish).Any());
            if (OperatingSystem.IsLinux())
            {
                Write(depRoot, "dep", Hook("sleep 10"));
                var slow = entry.ForModule(ModuleResolver.Resolve(root).Ordered[0]);
                CaseAssertions.CheckTrue("hook 超时灭树并 bounded drain", Reject(() => ModuleHooks.RunAsync(slow,
                    ModuleHookPhase.BeforePublish, TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult()));
            }
        });
    }
    private static void Write(string root, string name, string extra = "", string version = "1.0.0")
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "module.yaml"), Minimal.Replace("name: app", "name: " + name)
            .Replace("version: 1.0.0", "version: " + version) + extra);
    }
    private static void WithFixture(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "rigi-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
