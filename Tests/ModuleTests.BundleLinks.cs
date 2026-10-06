using RigiCompiler.Modules;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestBundleProductLinks()
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        var config = ModuleConfigurationReader.Read(Minimal);
        var context = new ModuleBuildContext(folder, Path.Combine(folder, "product"), "default", new(folder, config, []));
        var product = context.ProductArtifact; Directory.CreateDirectory(product);
        File.WriteAllText(Path.Combine(product, "ordinary.txt"), "unchanged");
        CaseAssertions.CheckTrue("产品普通文件相对名与实际路径保持", ModuleBundle.ProductFiles(context).Single()
            == ("ordinary.txt", Path.Combine(product, "ordinary.txt")));
        var outside = Path.Combine(folder, "outside-empty"); Directory.CreateDirectory(outside);
        var link = Path.Combine(product, "empty-link");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { CaseAssertions.RecordSkip("Windows 当前令牌不支持目录链接创建，产品链接运行验证跳过：" + ex.Message); return; }
        var output = Path.Combine(folder, "must-not-publish.zip");
        try
        {
            CaseAssertions.CheckTrue("真实BundleCreate在有无leaf前拒绝外部空目录链接", Reject(() => ModuleBundle.Create(context, output))
                && !File.Exists(output) && !Directory.EnumerateFileSystemEntries(outside).Any());
        }
        finally { RemoveOwnLink(link); }
        var loop = Path.Combine(product, "loop"); Directory.CreateSymbolicLink(loop, product);
        try { CaseAssertions.CheckTrue("真实BundleCreate逐层拒循环链接不深递归", Reject(() => ModuleBundle.Create(context, output))); }
        finally { RemoveOwnLink(loop); }
        var saved = product + ".saved"; Directory.Move(product, saved);
        try
        {
            Directory.CreateSymbolicLink(product, outside);
            try { CaseAssertions.CheckTrue("真实BundleCreate先检查产品根链接", Reject(() => ModuleBundle.Create(context, output))); }
            finally { RemoveOwnLink(product); }
            Directory.CreateSymbolicLink(product, Path.Combine(folder, "missing-target"));
            try { CaseAssertions.CheckTrue("悬空产品根链接不会由DirectoryExists跳过", Reject(() => ModuleBundle.Create(context, output))); }
            finally { RemoveOwnLink(product); }
        }
        finally { Directory.Move(saved, product); }
        CaseAssertions.CheckTrue("精确删除自有链接保原产品与外部目录", File.ReadAllText(Path.Combine(product, "ordinary.txt")) == "unchanged"
            && !Directory.EnumerateFileSystemEntries(outside).Any());

        static void RemoveOwnLink(string path)
        {
            // 悬空目录链接的 Directory.Exists 为 false；Unix unlink 必须作用于链接本身。
            if (new DirectoryInfo(path).LinkTarget == null)
                throw new InvalidOperationException("夹具清理仅允许自己创建的目录链接：" + path);
            if (OperatingSystem.IsWindows()) Directory.Delete(path);
            else File.Delete(path);
        }
    }
}
