namespace RigiCompiler.Modules;

/// <summary>请求级产品事务。after-publish 可看到已发布文件；失败只恢复本请求持有的目录。</summary>
internal sealed class ModuleProductPublication : IDisposable
{
    private sealed record Change(string Target, string? Backup);
    private readonly List<Change> changes = [];
    private readonly List<FileStream> locks = [];
    private bool committed;

    internal async Task PublishAsync(ModuleBuilt built, Func<ModuleBuilt, string, Task>? native = null)
    {
        var context = built.Context;
        // 锁覆盖 after-publish 与最终 commit/rollback，不能只保护 Directory.Move 的瞬间。
        locks.Add(await ModuleFiles.LockAsync(ModulePaths.Inside(context.Product,
            ".locks/" + context.Module.Configuration.Name + "-" + context.Module.Configuration.Version + ".lock")));
        var target = ModulePaths.Inside(context.Product, "modules/" + context.Module.Configuration.Name + "/" + context.Module.Configuration.Version);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string? backup = null;
        try
        {
            ModuleArtifactEnvelope.Write(Path.Combine(stage, "module.rgi"), built.Artifact);
            if (native != null) await native(built, stage);
            var names = new ModuleFiles.Names();
            foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                names.Add(Path.GetRelativePath(stage, file).Replace('\\', '/'));
            var copies = new List<(string Source, string Destination)>();
            foreach (var resource in context.Module.Configuration.Resources)
            {
                var source = ModulePaths.Inside(context.Resources, resource.Source);
                foreach (var file in ModuleFiles.Tree(context.Resources, resource.Source))
                {
                    var destination = File.Exists(source) ? resource.Destination
                        : resource.Destination + "/" + Path.GetRelativePath(source, file.Path).Replace('\\', '/');
                    names.Add(destination); copies.Add((file.Path, destination));
                }
            }
            foreach (var copy in copies)
            {
                var destination = ModulePaths.Inside(stage, copy.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(copy.Source, destination);
            }
            if (Directory.Exists(target))
            {
                backup = Path.Combine(parent, ".backup-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, backup);
            }
            try { Directory.Move(stage, target); }
            catch { if (backup != null) Directory.Move(backup, target); throw; }
            changes.Add(new(target, backup));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    internal void Commit()
    {
        committed = true;
        foreach (var change in changes)
            if (change.Backup != null)
                try { Directory.Delete(change.Backup, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Logger.Warning("Module", "已发布产品的旧备份清理失败：" + ex.Message); }
    }
    public void Dispose()
    {
        try
        {
            if (!committed)
                foreach (var change in changes.AsEnumerable().Reverse())
                {
                    if (Directory.Exists(change.Target)) Directory.Delete(change.Target, true);
                    if (change.Backup != null) Directory.Move(change.Backup, change.Target);
                }
        }
        finally { foreach (var held in locks) held.Dispose(); }
    }
}
