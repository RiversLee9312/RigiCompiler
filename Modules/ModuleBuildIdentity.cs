using System.Security.Cryptography;
using RigiCompiler.Middleware.Cache;

namespace RigiCompiler.Modules;

/// <summary>一个模块的内容键；有序输入及来源均显式入键，不依赖上次构建索引。</summary>
internal static class ModuleBuildIdentity
{
    internal static string Compute(ModuleBuildContext context, IReadOnlyList<ModuleSource> sources,
        IReadOnlyList<SourceInput> embedded, IReadOnlyList<ModuleArtifact> dependencies, string compiler,
        string compilerAbi = ModuleInterface.CompilerAbi)
    {
        var module = context.Module;
        var fields = new List<string> { ModuleArtifactCache.Schema, compilerAbi, compiler,
            module.ModuleId, module.CompilerOwned.ToString(), context.ProfileName, ModuleBuildContext.HostEnvironment };
        fields.Add(module.CompilerOwned ? "embedded-stdlib" : Convert.ToBase64String(File.ReadAllBytes(ModulePaths.Inside(module.Root, "module.yaml"))));
        var selected = module.Configuration.SelectProfile(context.ProfileName);
        fields.Add(selected.Target.ToString()); fields.Add(selected.Entry ?? module.Configuration.Entry ?? "");
        fields.Add(selected.Product ?? "");
        foreach (var dependency in dependencies)
        {
            fields.Add(dependency.ModuleId); fields.Add(dependency.ApiHash); fields.Add(dependency.CompilerOwned.ToString());
            // 完整接口含 bilDigest：默认/固定 slot helper 仅 body 改动也使消费者失效。
            fields.Add(Convert.ToHexString(SHA256.HashData(dependency.InterfaceBytes)));
        }
        foreach (var source in sources) { fields.Add(source.RelativePath); fields.Add(Convert.ToBase64String(source.Bytes)); }
        foreach (var input in embedded) { fields.Add(input.SourceName); fields.Add(input.Text); }
        foreach (var hook in context.Hooks(ModuleHookPhase.BeforePublish).Concat(context.Hooks(ModuleHookPhase.AfterPublish)))
        {
            fields.Add(hook.Phase.ToString()); fields.Add(hook.Command);
            foreach (var input in hook.Inputs)
            { fields.Add(input); fields.Add(Convert.ToBase64String(File.ReadAllBytes(ModulePaths.Inside(module.Root, input)))); }
        }
        foreach (var pair in context.ChildEnvironment().OrderBy(p => p.Key, StringComparer.Ordinal))
        { fields.Add(pair.Key); fields.Add(pair.Value); }
        return ArtifactCache.Identity(fields.ToArray());
    }
}
