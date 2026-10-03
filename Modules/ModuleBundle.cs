using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RigiCompiler.Modules;

internal static class ModuleBundle
{
    private const string Receipt = ".rigi-install-receipt";

    // 调用者必须先完成发布；选中的产品放在安装后可消费的 artifact/<profile>/product 下。
    internal static void Create(ModuleBuildContext context, string output)
    {
        // 先消费产品目录边界，空目录/悬空根/循环链接也必须拒绝，不能等有 leaf 才检查。
        var products = ProductFiles(context).ToArray();
        ModuleArtifactEnvelope.Read(ModulePaths.Inside(context.Module.Root,
            "artifact/" + context.ProfileName + "/module.rgi"), context.Module.ModuleId);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name, string path)
        {
            if (files.TryGetValue(name, out var previous) && previous != path)
                throw new ModuleConfigurationException("bundle 文件来源冲突：" + name);
            files[name] = path;
        }
        void Tree(string relative)
        {
            if (!Directory.Exists(ModulePaths.Inside(context.Module.Root, relative))
                && !File.Exists(ModulePaths.Inside(context.Module.Root, relative))) return;
            foreach (var file in ModuleFiles.Tree(context.Module.Root, relative))
                if (!Path.GetFileName(file.Path).StartsWith(".request-", StringComparison.Ordinal)
                    && !file.Path.EndsWith(".tmp", StringComparison.Ordinal) && Path.GetFullPath(file.Path) != Path.GetFullPath(output))
                    Add(file.Relative, file.Path);
        }
        Add("module.yaml", ModulePaths.Inside(context.Module.Root, "module.yaml"));
        Tree("source"); Tree("resources"); Tree("artifact");
        foreach (var input in context.Module.Configuration.Hooks.SelectMany(h => h.Inputs)) Tree(input);
        foreach (var dependency in context.Module.Configuration.Dependencies)
            if (dependency.Path != null) Tree(dependency.Path);
        foreach (var file in products) Add("artifact/" + context.ProfileName + "/product/" + file.Relative, file.Path);
        var names = new ModuleFiles.Names(); foreach (var name in files.Keys) names.Add(name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, false, Encoding.UTF8))
                foreach (var file in files.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    var permissions = OperatingSystem.IsWindows() ? 0x1a4 : (int)File.GetUnixFileMode(file.Value) & 0x1ff;
                    entry.ExternalAttributes = (0x8000 | permissions) << 16; // 只携带普通文件和 rwx，不恢复 setuid 等特权位。
                    using var input = File.OpenRead(file.Value); using var destination = entry.Open(); input.CopyTo(destination);
                }
            File.Move(temporary, output, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static IEnumerable<(string Relative, string Path)> ProductFiles(ModuleBuildContext context)
    {
        var relativeRoot = "modules/" + context.Module.Configuration.Name + "/" + context.Module.Configuration.Version;
        var root = ModulePaths.Inside(context.Product, relativeRoot);
        if (!Directory.Exists(root)) yield break;
        foreach (var file in ModuleFiles.Tree(context.Product, relativeRoot))
            yield return (Path.GetRelativePath(root, file.Path).Replace('\\', '/'), file.Path);
    }

    internal static async Task<string> InstallAsync(string entryRoot, string bundle, string? profile = null)
    {
        var entryConfig = ModuleConfigurationReader.ReadFile(ModulePaths.Inside(entryRoot, "module.yaml"));
        var entry = new ResolvedModule(Path.GetFullPath(entryRoot), entryConfig, []);
        var entryContext = ModuleBuildContext.Create(new(entry, [entry]), profile);
        using var stream = new FileStream(bundle, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexString(SHA256.HashData(stream)); stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8);
        var names = new ModuleFiles.Names(); var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in archive.Entries)
        {
            var directory = item.FullName.EndsWith('/');
            var name = directory ? item.FullName[..^1] : item.FullName;
            ModulePaths.ValidateRelative(name);
            var mode = (item.ExternalAttributes >>> 16) & 0xf000;
            if (name == Receipt || (item.ExternalAttributes & 0x400) != 0
                || mode != 0 && mode != (directory ? 0x4000 : 0x8000))
                throw new ModuleConfigurationException("ZIP 不允许链接/特殊节点或安装 receipt：" + name);
            if (!explicitNames.Add(name)) throw new ModuleConfigurationException("ZIP 重复路径：" + name);
            names.Add(name, directory);
        }
        var configurationEntry = archive.Entries.SingleOrDefault(e => e.FullName == "module.yaml")
            ?? throw new ModuleConfigurationException("ZIP 根缺少 module.yaml");
        using var reader = new StreamReader(configurationEntry.Open(), Encoding.UTF8, true);
        var config = ModuleConfigurationReader.Read(await reader.ReadToEndAsync(), bundle + ":module.yaml");
        var relativeTarget = "dependencies/" + config.Name + "/" + config.Version;
        var target = ModulePaths.Inside(entryRoot, relativeTarget);
        var lockPath = ModulePaths.Inside(entryRoot, "dependencies/.locks/" + config.Name + "-" + config.Version + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var installationLock = await ModuleFiles.LockAsync(lockPath);
        if (Directory.Exists(target))
        {
            var installed = ModuleConfigurationReader.ReadFile(ModulePaths.Inside(target, "module.yaml"));
            var receipt = ModulePaths.Inside(target, Receipt);
            if (installed.ModuleId == config.ModuleId && File.Exists(receipt) && File.ReadAllText(receipt) == digest)
                return target; // 同身份、同 bundle 的重复安装不重新执行 hook，也不覆盖用户文件。
            throw new ModuleConfigurationException("同版本已有不同内容的安装：" + config.ModuleId);
        }
        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); var moved = false;
        try
        {
            foreach (var item in archive.Entries)
            {
                if (item.FullName.EndsWith('/'))
                { Directory.CreateDirectory(ModulePaths.Inside(stage, item.FullName[..^1])); continue; }
                var destination = ModulePaths.Inside(stage, item.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = item.Open(); using var output = File.Create(destination); await input.CopyToAsync(output);
                if (!OperatingSystem.IsWindows() && ((item.ExternalAttributes >>> 16) & 0xf000) != 0) File.SetUnixFileMode(destination,
                    (UnixFileMode)((item.ExternalAttributes >>> 16) & 0x1ff));
            }
            Validate(stage, config.ModuleId);
            var staged = entryContext.ForModule(new(stage, config, []));
            await ModuleHooks.RunAsync(staged, ModuleHookPhase.BeforeInstall);
            Validate(stage, config.ModuleId);
            Directory.Move(stage, target); moved = true;
            await ModuleHooks.RunAsync(entryContext.ForModule(new(target, config, [])), ModuleHookPhase.AfterInstall);
            Validate(target, config.ModuleId);
            File.WriteAllText(ModulePaths.Inside(target, Receipt), digest);
            return target;
        }
        catch
        {
            // 只删除本次提交的目录；hook 对进程外世界的副作用不属于目录事务。
            if (moved && Directory.Exists(target)) Directory.Delete(target, true);
            throw;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static void Validate(string root, string expected)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        void Version(string name, string version)
        {
            if (versions.TryGetValue(name, out var previous) && previous != version)
                throw new ModuleConfigurationException("bundle 依赖版本冲突：" + name);
            versions[name] = version;
        }
        void Visit(string folder, string identity)
        {
            var config = ModuleConfigurationReader.ReadFile(ModulePaths.Inside(folder, "module.yaml"));
            if (config.ModuleId != identity) throw new ModuleConfigurationException("bundle 依赖 identity 不匹配：" + identity);
            Version(config.Name, config.Version);
            if (!active.Add(identity)) throw new ModuleConfigurationException("bundle 依赖环：" + identity);
            var artifactRoot = ModulePaths.Inside(folder, "artifact");
            var hasArtifact = false;
            if (Directory.Exists(artifactRoot))
                foreach (var file in ModuleFiles.Tree(folder, "artifact").Where(f => Path.GetFileName(f.Path) == "module.rgi"))
                { ModuleArtifactEnvelope.Read(file.Path, identity); hasArtifact = true; } // 信任始终由消费方 resolver 授予。
            if (!hasArtifact && ModuleSources.Read(folder, config.Sources).Count == 0)
                throw new ModuleConfigurationException("bundle 没有选中源码或预编译模块：" + identity);
            foreach (var dependency in config.Dependencies)
            {
                Version(dependency.Name, dependency.Version);
                var path = ModulePaths.Inside(folder, dependency.Path ?? "dependencies/" + dependency.Name + "/" + dependency.Version);
                if (Directory.Exists(path)) Visit(path, dependency.ModuleId);
                else if (dependency.Path != null) throw new ModuleConfigurationException("bundle 缺少显式 path 依赖：" + dependency.ModuleId);
            }
            active.Remove(identity);
        }
        Visit(root, expected);
    }
}
