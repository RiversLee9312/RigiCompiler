using RigiCompiler.Bil;
using RigiCompiler.Middleware.Cache;

namespace RigiCompiler.Modules;

/// <summary>只缓存一个模块的接口/BIL；依赖已在进入本模块锁之前完成。</summary>
internal static class ModuleArtifactCache
{
    internal const string Schema = "independent-module-pair-v1";
    internal sealed record Result(ModuleArtifact Artifact, bool Compiled, bool CacheUsed);
    internal static Result Get(string root, string key, string moduleId, bool compilerOwned,
        IReadOnlyList<ModuleArtifact> dependencies, string destination, Func<ModuleArtifact> compile,
        bool enabled = true)
    {
        var compiled = false;
        void Build(string path)
        {
            var artifact = compile();
            if (artifact.ModuleId != moduleId || artifact.InputDigest != key || artifact.CompilerOwned != compilerOwned)
                throw new ModuleConfigurationException("模块 builder 输出身份不符");
            ModuleArtifactEnvelope.Validate(artifact, dependencies);
            ModuleArtifactEnvelope.Write(path, artifact);
            compiled = true;
        }
        void ValidateCached(string path)
        {
            try
            {
                var artifact = ModuleArtifactEnvelope.Read(path, moduleId, compilerOwned);
                if (artifact.InputDigest != key) throw new ModuleConfigurationException("模块缓存输入身份不符");
                ModuleArtifactEnvelope.Validate(artifact, dependencies);
            }
            catch (Exception ex) when (ex is ModuleConfigurationException or BilParseException or BilLinkException)
            { throw new InvalidOperationException("模块缓存产物损坏", ex); }
        }
        var used = enabled && ArtifactCache.TryMaterialize(root, key, "module.rgi", destination,
            Build, ValidateCached, out _);
        if (!used) Build(destination);
        var result = ModuleArtifactEnvelope.Read(destination, moduleId, compilerOwned);
        PerformanceMetrics.Event("cache", "module-bil", used ? compiled ? "miss" : "hit" : "bypass", moduleId + ":" + key);
        return new(result, compiled, used);
    }
}
