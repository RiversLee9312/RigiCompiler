using RigiCompiler.Bil;
using RigiCompiler.Middleware.Cache;

namespace RigiCompiler.Modules;

internal sealed record ModuleBuilt(ModuleBuildContext Context, ModuleArtifact Artifact, BilModule Linked,
    bool Compiled, string CacheStatus, int OwnSourceCount);
internal sealed record ModuleBuildResult(ModuleResolution Resolution, IReadOnlyList<ModuleBuilt> Modules)
{
    internal ModuleBuilt Entry => Modules[^1];
}

/// <summary>真实独立模块管线。依赖准备完毕后才进入当前模块缓存锁，回调不递归构建或取得 LLVM lease。</summary>
internal static class ModuleBuildService
{
    internal static ModuleResolution Resolve(string root) => ModuleResolver.Resolve(root, dependency =>
    {
        if (dependency.ModuleId != "stdlib@1.0.0") return null;
        var config = ModuleConfigurationReader.Read("schema: 1\nname: stdlib\nversion: 1.0.0\ntype: static-library\n");
        return new ResolvedModule(ModulePaths.Inside(root, ".rigi/compiler/stdlib/1.0.0"), config, []) { CompilerOwned = true };
    });

    internal static async Task<ModuleBuildResult> BuildAsync(string root, string? profile = null,
        string? cacheRoot = null, Func<ModuleBuilt, Task>? publish = null)
    {
        var resolution = Resolve(Path.GetFullPath(root));
        var entryContext = ModuleBuildContext.Create(resolution, profile);
        var results = new List<ModuleBuilt>();
        var compiler = NativeObjectIdentity.CompilerContentIdentity();
        foreach (var module in resolution.Ordered)
        {
            var context = entryContext.ForModule(module);
            Directory.CreateDirectory(ModulePaths.Inside(module.Root, "artifact/" + context.ProfileName));
            await ModuleHooks.RunAsync(context, ModuleHookPhase.BeforePublish);
            var transitive = new HashSet<string>(StringComparer.Ordinal);
            void Include(ResolvedModule item)
            {
                foreach (var dependency in item.Dependencies) { Include(dependency); transitive.Add(dependency.ModuleId); }
            }
            Include(module);
            var dependencies = results.Where(b => transitive.Contains(b.Artifact.ModuleId)).Select(b => b.Artifact).ToArray();
            var sources = module.CompilerOwned ? [] : ModuleSources.Read(module.Root, module.Configuration.Sources);
            var embedded = module.CompilerOwned ? StdlibSources.ReadInputs() : [];
            var receipt = ModulePaths.Inside(module.Root, "artifact/" + context.ProfileName + "/module.rgi");
            ModuleArtifact artifact;
            bool compiled;
            string status;
            if (!module.CompilerOwned && sources.Count == 0 && File.Exists(receipt))
            {
                // 缺源只能消费显式发布的 receipt，绝不按 ModuleId 猜上次内容缓存键。
                artifact = ModuleArtifactEnvelope.Read(receipt, module.ModuleId);
                ModuleArtifactEnvelope.Validate(artifact, dependencies);
                compiled = false; status = "prebuilt";
            }
            else
            {
                if (!module.CompilerOwned && sources.Count == 0)
                    throw new ModuleConfigurationException("模块没有选中源码或预编译产物：" + module.ModuleId);
                var before = context.Hooks(ModuleHookPhase.BeforePublish).ToArray();
                var key = ModuleBuildIdentity.Compute(context, sources, embedded, dependencies, compiler ?? "unverified");
                var request = ModulePaths.Inside(module.Root, "artifact/" + context.ProfileName + "/.request-" + Guid.NewGuid().ToString("N") + ".rgi");
                try
                {
                    var cached = ModuleArtifactCache.Get(cacheRoot ?? ArtifactCache.Root("module-cache"), key, module.ModuleId,
                        module.CompilerOwned, dependencies, request, () => Compile(module, sources, embedded, dependencies,
                            module.ModuleId == resolution.Entry.ModuleId && module.Configuration.Type == ModuleProductKind.Executable, key),
                        compiler != null && before.All(h => h.Inputs.Count != 0));
                    artifact = cached.Artifact; compiled = cached.Compiled;
                    status = cached.CacheUsed ? compiled ? "miss" : "hit" : "bypass";
                }
                finally { if (File.Exists(request)) File.Delete(request); }
            }
            var graph = SymbolGraph.CreateArtifactOnly(module.ModuleId);
            foreach (var dependency in dependencies) ModuleInterfaceImporter.Import(graph, dependency);
            var own = artifact.ReadBil();
            foreach (var helper in graph.ApprovedLateHelpers)
            {
                var canonical = CanonicalSymbolPrinter.PrintMethod(helper);
                if (own.Functions.Any(f => f.Symbol == canonical)) graph.LateHelperOverrides.Add(canonical);
            }
            var linked = ModuleApplicationLinker.Link(dependencies, own, graph);
            var built = new ModuleBuilt(context, artifact, linked, compiled, status, sources.Count);
            if (publish != null) await publish(built);
            await ModuleHooks.RunAsync(context, ModuleHookPhase.AfterPublish);
            // 公共 receipt 只在本模块所有发布步骤成功后原子替换；缓存只是内部语义产物。
            var temporary = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { ModuleArtifactEnvelope.Write(temporary, artifact); File.Move(temporary, receipt, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            results.Add(built);
        }
        return new(resolution, results);
    }

    private static ModuleArtifact Compile(ResolvedModule module, IReadOnlyList<ModuleSource> sources,
        IReadOnlyList<SourceInput> embedded, IReadOnlyList<ModuleArtifact> dependencies, bool finalApplication, string key)
    {
        var graph = module.CompilerOwned ? new SymbolGraph(module.ModuleId) : SymbolGraph.CreateArtifactOnly(module.ModuleId);
        foreach (var dependency in dependencies) ModuleInterfaceImporter.Import(graph, dependency);
        if (!module.CompilerOwned && !graph.Bootstrap.Any.Methods.Any(m => m.Name == "call???"))
            throw new ModuleConfigurationException("模块必须显式声明 stdlib 依赖：" + module.ModuleId);
        var roots = Frontend.ParseRoots(module.CompilerOwned ? embedded : sources.Select(s => s.Input()).ToArray());
        var unit = new CompilationUnit(graph, roots) { IsFinalModuleApplication = finalApplication };
        var bodies = CompileCommand.RunSemaPasses(unit, out _) ?? throw new ModuleConfigurationException("模块语义分析失败：" + module.ModuleId);
        var lowered = PerformanceMetrics.Measure("lowering.P4a", () => Lowerer.Lower(unit, bodies), module.ModuleId);
        var bil = PerformanceMetrics.Measure("lowering.P4b", () => BilEmitter.Emit(unit, lowered, module.ModuleId), module.ModuleId);
        if (unit.Diagnostics.HasErrors)
        { CompileCommand.EmitDiagnostics(unit.Diagnostics, 0); throw new ModuleConfigurationException("模块发射失败：" + module.ModuleId); }
        return ModuleInterface.Export(unit, bodies, bil, key, module.CompilerOwned);
    }
}
