using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Toolchain;
using RigiCompiler.Middleware;

namespace RigiCompiler.Modules;

public sealed class ModuleCommand : ICommandLineCommand
{
    private sealed class Option : ICommandLineOption
    {
        public CommandLineMask Mask { get; }
        internal Option(string name, string description, int count = 0)
        { Mask = new() { Name = name, Description = description, MinArgs = count, MaxArgs = count, ArgsHint = count == 0 ? "" : "<值>" }; }
    }
    public CommandLineMask Mask { get; } = new() { Name = "module", Description = "独立模块配置、构建与运行", AllowsProgramArguments = true };
    public IReadOnlyList<ICommandLineOption> SubCommands { get; }
    public ModuleCommand()
    {
        var init = new Option("--init", "创建标准 module.yaml 与源码模板");
        var run = new Option("--run", "按 profile 自动构建并运行；-- 后为程序参数");
        var install = new Option("--install", "将 ZIP 模块原子安装到入口 dependencies/name/version", 1);
        var publish = new Option("--publish", "发布真实 Native 产品、接口、资源与 C 头文件");
        var bundle = new Option("--bundle", "完成发布后生成 ZIP 模块");
        Option[] actions = [init, run, publish, bundle, install];
        foreach (var action in actions)
            action.Mask.MutuallyExclusive.AddRange(actions.Where(a => a != action).Select(a => a.Mask.Name));
        SubCommands = [.. actions, new Option("--root", "模块根目录（默认当前目录）", 1), new Option("--profile", "配置中的任意命名 profile", 1), new Option("--output", "ZIP 输出路径，仅 --bundle 可用", 1), new VerboseOption(), new LogToOption()];
    }
    public int Execute(CommandLineParseResult result)
    {
        if (LoggerOptions.Apply(result) is { } error) { Logger.Error("Module", error); return 2; }
        if (!result.Has("--init") && !result.Has("--run") && !result.Has("--install") && !result.Has("--publish") && !result.Has("--bundle")
            || result.HasProgramArgumentDelimiter && !result.Has("--run") || result.Has("--output") && !result.Has("--bundle"))
        { Logger.Error("Module", "需要恰一个模块操作，程序参数仅 --run 支持"); return 2; }
        try
        {
            var root = Path.GetFullPath(result.Get("--root")?[0] ?? Directory.GetCurrentDirectory());
            if (result.Has("--init"))
            {
                if (result.Has("--profile")) throw new ModuleConfigurationException("--init 不接受 --profile");
                Initialize(root); return 0;
            }
            if (result.Has("--install"))
            {
                ModuleBundle.InstallAsync(root, Path.GetFullPath(result.Get("--install")![0]), result.Get("--profile")?[0]).GetAwaiter().GetResult();
                return 0;
            }
            var configuration = ModuleConfigurationReader.ReadFile(ModulePaths.Inside(root, "module.yaml"));
            if (result.Has("--run") && configuration.Type != ModuleProductKind.Executable)
                throw new ModuleConfigurationException("--run 只支持 executable 模块");
            var native = !result.Has("--run") || configuration.SelectProfile(result.Get("--profile")?[0]).Target == ModuleRunTarget.Native;
            using var publication = new ModuleProductPublication();
            var built = ModuleBuildService.BuildAsync(root, result.Get("--profile")?[0], publish:
                module => publication.PublishAsync(module, native ? ModuleNativePublication.PublishAsync : null)).GetAwaiter().GetResult().Entry;
            publication.Commit();
            if (!result.Has("--run"))
            {
                if (result.Has("--bundle"))
                    ModuleBundle.Create(built.Context, Path.GetFullPath(result.Get("--output")?[0]
                        ?? Path.Combine(root, configuration.Name + "-" + configuration.Version + ".zip")));
                return 0;
            }
            var entry = ModuleEntrypoint.Select(built);
            if (built.Context.Module.Configuration.SelectProfile(built.Context.ProfileName).Target == ModuleRunTarget.Vm)
            {
                var run = BilVm.Run(entry.Module, maxSteps: 1_000_000_000, entryPoint: entry.Canonical, programArguments: result.ProgramArguments);
                Console.Out.Write(run.Stdout); Console.Error.Write(run.Stderr);
                if (run.Exception != null) { Logger.Error("Module", run.Exception.Message); return 1; }
                return run.ReturnValue is VmI32 value ? value.Value : 0;
            }
            var executable = Path.Combine(built.Context.ProductArtifact, ModuleNativePublication.FileName(configuration));
            var code = ExternalProcess.Run(executable, result.ProgramArguments, out var stdout, out var stderr,
                workingDirectory: root, environment: built.Context.ChildEnvironment(), closeStdin: true);
            Console.Out.Write(stdout); Console.Error.Write(stderr); return code;
        }
        catch (Exception ex) when (ex is ModuleConfigurationException or IOException or InvalidDataException or UnauthorizedAccessException or VmException or ParserException or LexerException or MwNotSupportedException)
        { Logger.Error("Module", ex.Message); return 1; }
    }
    internal static void Initialize(string root)
    {
        Directory.CreateDirectory(root);
        var yaml = ModulePaths.Inside(root, "module.yaml");
        var source = ModulePaths.Inside(root, "source/main.rg");
        if (File.Exists(yaml) || File.Exists(source)) throw new ModuleConfigurationException("--init 不覆盖已有配置或 source/main.rg");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        try { ModuleConfigurationReader.ValidateIdentity(name, "1.0.0", "--init"); }
        catch (ModuleConfigurationException) { name = "app"; }
        var text = "schema: 1\nname: " + name + "\nversion: 1.0.0\ntype: executable\nsource: ['**/*.rg']\ndependencies:\n  - {name: stdlib, version: 1.0.0}\ndefault-profile: debug\nprofiles:\n  debug: {target: vm}\n  release: {target: native}\n  sandbox: {target: vm}\n  machine: {target: native}\n";
        ModuleConfigurationReader.Read(text);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "pub func main(): i32 {\n    core.io.Console.println(\"hello module\")\n    return 0\n}\n");
        File.WriteAllText(yaml, text);
    }
}
