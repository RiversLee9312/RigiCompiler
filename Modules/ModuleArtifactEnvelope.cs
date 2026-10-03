using System.Text;
using System.Text.Json;
using RigiCompiler.Bil;

namespace RigiCompiler.Modules;

/// <summary>接口与 BIL 的单文件原子单元。信任位由 resolver 授予，不写入磁盘协议。</summary>
internal static class ModuleArtifactEnvelope
{
    private static readonly byte[] Magic = "RIGIMOD1\n"u8.ToArray();
    internal static void Write(string path, ModuleArtifact artifact)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(artifact.InterfaceBytes.Length); writer.Write(artifact.InterfaceBytes);
        writer.Write(artifact.BilBytes.Length); writer.Write(artifact.BilBytes);
    }
    internal static ModuleArtifact Read(string path, string expectedModuleId, bool compilerOwned = false)
    {
        try
        {
            // 发布 receipt 可被并发原子替换；读者固定到已打开的完整文件，不阻止 Windows rename。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new ModuleConfigurationException("module.rgi 格式或版本无效");
            byte[] Part()
            {
                var length = reader.ReadInt32();
                if (length < 0 || length > stream.Length - stream.Position)
                    throw new ModuleConfigurationException("module.rgi 长度无效");
                return reader.ReadBytes(length);
            }
            var api = Part(); var bil = Part();
            if (stream.Position != stream.Length) throw new ModuleConfigurationException("module.rgi 存在尾部数据");
            using var json = JsonDocument.Parse(api);
            var root = json.RootElement;
            var result = new ModuleArtifact(ModuleInterface.Str(root, "moduleId")!,
                ModuleInterface.Str(root, "inputDigest")!, ModuleInterface.Str(root, "apiHash")!, api, bil, compilerOwned);
            if (result.ModuleId != expectedModuleId) throw new ModuleConfigurationException("module.rgi 模块身份不符");
            using var validated = ModuleInterface.Validate(result);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or EndOfStreamException)
        { throw new ModuleConfigurationException("module.rgi 格式无效：" + ex.Message); }
    }
    internal static void Validate(ModuleArtifact artifact, IReadOnlyList<ModuleArtifact> dependencies)
    {
        var graph = SymbolGraph.CreateArtifactOnly(artifact.ModuleId);
        foreach (var dependency in dependencies) ModuleInterfaceImporter.Import(graph, dependency);
        ModuleInterfaceImporter.Import(graph, artifact);
        var own = artifact.ReadBil();
        // 外部标记 wrapper 的纯构造事实必须由依赖实现闭包验证；独立模块保留 external 声明。
        // 命中后的 late override 仍只从可信审批集合恢复，不能授权普通同名方法。
        foreach (var method in graph.ApprovedLateHelpers)
        {
            var canonical = CanonicalSymbolPrinter.PrintMethod(method);
            if (own.Functions.Any(f => f.Symbol == canonical)) graph.LateHelperOverrides.Add(canonical);
        }
        var linked = ModuleApplicationLinker.Link(dependencies, own, graph);
        var errors = BilVerifier.Verify(linked);
        if (errors.Count != 0) throw new ModuleConfigurationException("模块 BIL 验证失败：" + errors[0]);
    }
}
