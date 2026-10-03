namespace RigiCompiler.Modules;

/// <summary>请求级绝对路径与环境；依赖始终继承入口 BUILD_ROOT/PRODUCT，不污染父进程。</summary>
public sealed record ModuleBuildContext(string BuildRoot, string Product, string ProfileName,
    ResolvedModule Module)
{
    public string Source => Path.Combine(Module.Root, "source");
    public string Resources => Path.Combine(Module.Root, "resources");
    public string Artifact => Path.Combine(Module.Root, "artifact", ProfileName);
    public string ProductArtifact => Path.Combine(Product, "modules", Module.Configuration.Name, Module.Configuration.Version);
    public static string HostEnvironment => OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux" : throw new PlatformNotSupportedException("模块构建当前支持 Linux/Windows 宿主");

    public static ModuleBuildContext Create(ModuleResolution resolution, string? profileName = null)
    {
        var profile = resolution.Entry.Configuration.SelectProfile(profileName);
        var product = ModulePaths.Inside(resolution.Entry.Root, profile.Product ?? "product/" + profile.Name);
        return new(resolution.Entry.Root, product, profile.Name, resolution.Entry);
    }
    public ModuleBuildContext ForModule(ResolvedModule module) => this with { Module = module,
        ProfileName = module.Configuration.Profiles.ContainsKey(ProfileName) ? ProfileName : module.Configuration.DefaultProfile };
    public IReadOnlyDictionary<string, string> ChildEnvironment() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["RIGI_RES"] = Path.GetFullPath(Resources), ["RIGI_SRC"] = Path.GetFullPath(Source),
        ["RIGI_ARTIFACT"] = Path.GetFullPath(Artifact), ["BUILD_ROOT"] = Path.GetFullPath(BuildRoot),
        ["PRODUCT"] = Path.GetFullPath(Product)
    };
    public IEnumerable<ModuleHook> Hooks(ModuleHookPhase phase) => Module.Configuration.Hooks
        .Where(h => h.Phase == phase && (h.Environment == "all" || h.Environment == HostEnvironment));
}
