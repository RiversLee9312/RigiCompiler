namespace RigiCompiler.Modules;

public sealed record ResolvedModule(string Root, ModuleConfiguration Configuration,
    IReadOnlyList<ResolvedModule> Dependencies)
{
    public string ModuleId => Configuration.ModuleId;
    // 仅编译器 resolver 回调可授予来源；磁盘配置/产物没有此字段。
    internal bool CompilerOwned { get; init; }
}
public sealed record ModuleResolution(ResolvedModule Entry, IReadOnlyList<ResolvedModule> Ordered);

/// <summary>依赖先于宿主的稳定 DAG；同一入口闭包只允许一个名字对应一个精确版本。</summary>
public static class ModuleResolver
{
    public static ModuleResolution Resolve(string entryRoot,
        Func<ModuleDependency, ResolvedModule?>? compilerOwned = null)
    {
        entryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entryRoot));
        var visited = new Dictionary<string, ResolvedModule>(StringComparer.Ordinal);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var active = new List<string>();
        var ordered = new List<ResolvedModule>();
        ResolvedModule Visit(string root, ModuleConfiguration configuration, bool trusted = false)
        {
            var id = configuration.ModuleId;
            if (versions.TryGetValue(configuration.Name, out var version) && version != configuration.Version)
                throw new ModuleConfigurationException($"依赖版本冲突：{configuration.Name}@{version} 与 {id}");
            versions[configuration.Name] = configuration.Version;
            if (active.Contains(id, StringComparer.Ordinal))
                throw new ModuleConfigurationException("依赖环：" + string.Join(" -> ", active.Append(id)));
            if (visited.TryGetValue(id, out var prior))
            {
                if (prior.Root != root) throw new ModuleConfigurationException($"同一模块身份有不同根：{id}");
                return prior;
            }
            active.Add(id);
            var dependencies = new List<ResolvedModule>();
            foreach (var dependency in configuration.Dependencies.OrderBy(d => d.ModuleId, StringComparer.Ordinal))
            {
                var owned = dependency.Path == null ? compilerOwned?.Invoke(dependency) : null;
                if (owned != null)
                {
                    if (owned.ModuleId != dependency.ModuleId) throw new ModuleConfigurationException("compiler dependency identity 不匹配");
                    dependencies.Add(Visit(owned.Root, owned.Configuration, owned.CompilerOwned));
                    continue;
                }
                var depRoot = dependency.Path == null
                    ? ModulePaths.Inside(entryRoot, $"dependencies/{dependency.Name}/{dependency.Version}")
                    : ModulePaths.Inside(root, dependency.Path);
                var depConfig = ModuleConfigurationReader.ReadFile(ModulePaths.Inside(depRoot, "module.yaml"));
                if (depConfig.ModuleId != dependency.ModuleId)
                    throw new ModuleConfigurationException($"依赖 identity 不匹配：要求 {dependency.ModuleId}，实际 {depConfig.ModuleId}");
                dependencies.Add(Visit(depRoot, depConfig));
            }
            active.RemoveAt(active.Count - 1);
            var result = new ResolvedModule(root, configuration, dependencies) { CompilerOwned = trusted };
            visited.Add(id, result);
            ordered.Add(result);
            return result;
        }
        var entry = Visit(entryRoot, ModuleConfigurationReader.ReadFile(ModulePaths.Inside(entryRoot, "module.yaml")));
        return new(entry, ordered);
    }
}
