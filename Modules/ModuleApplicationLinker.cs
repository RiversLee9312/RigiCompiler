using RigiCompiler.Bil;

namespace RigiCompiler.Modules;

/// <summary>只允许最终应用替换经可信 interface 审批的晚期 helper，仍使用事务型通用 linker。</summary>
internal static class ModuleApplicationLinker
{
    internal static BilModule Link(IReadOnlyList<ModuleArtifact> dependencies, BilModule application, SymbolGraph graph)
    {
        var approved = graph.ApprovedLateHelpers.ToDictionary(CanonicalSymbolPrinter.PrintMethod, StringComparer.Ordinal);
        var modules = new List<BilModule>();
        foreach (var artifact in dependencies)
        {
            var module = artifact.ReadBil();
            if (artifact.CompilerOwned)
                foreach (var canonical in graph.LateHelperOverrides)
                {
                    if (!approved.TryGetValue(canonical, out var method) || method.OriginModuleId != artifact.ModuleId
                        || application.Functions.Count(f => f.Symbol == canonical) != 1)
                        throw new BilLinkException("晚期 helper 不属于可信 provider 或缺唯一 final body");
                    var original = module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Single(d => d.Symbol == canonical);
                    var replacement = application.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Single(d => d.Symbol == canonical);
                    BilModuleLinker.Compatible(original, replacement, module.Resources);
                    module.Functions.RemoveAll(f => f.Symbol == canonical);
                    module.LocalSymbols.Remove(original);
                    module.ExternalSymbols.Add(original);
                }
            modules.Add(module);
        }
        modules.Add(application);
        return BilModuleLinker.Link(modules);
    }
}
