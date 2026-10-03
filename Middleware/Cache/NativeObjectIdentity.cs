using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Emit;

namespace RigiCompiler.Middleware.Cache;

internal static class NativeObjectIdentity
{
    // Release 的 CoreCLR runtimeconfig 也可关闭动态代码；该能力开关不是执行映像身份。
    // 有托管 Location 时必须验证该 PE，验证失败不能退回 dotnet 宿主摘要。
    internal static bool UsesManagedImages { get; } = HasManagedLocation(typeof(NativeObjectIdentity).Assembly);
    private static readonly LoadedLibraryIdentity? Compiler = CaptureCompiler();
    private static readonly LoadedLibraryIdentity? Binding = UsesManagedImages ? CaptureManaged(typeof(LLVM).Assembly) : Compiler;
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "只检测已加载程序集是否提供独立托管映像路径，不以动态代码能力推断 NativeAOT。")]
    private static bool HasManagedLocation(Assembly assembly) => !string.IsNullOrEmpty(assembly.Location);
    // 托管模块还核对实际已加载 MVID，拒绝首次查询前磁盘替换后的 assembly。
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "此方法只在 CoreCLR 使用 Assembly.Location；NativeAOT 使用实际进程映射与 /proc/self/exe。")]
    private static LoadedLibraryIdentity? CaptureManaged(Assembly assembly)
    {
        try
        {
            // 同一份字节同时核 MVID 与 SHA，不能核旧 PE 后另读已替换文件摘要。
            var bytes = File.ReadAllBytes(assembly.Location);
            using var image = new PEReader(new MemoryStream(bytes, writable: false));
            var metadata = image.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId) return null;
            return LoadedLibraryIdentity.CaptureFile(assembly.Location, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (ArtifactCache.IsCacheIo(ex) || ex is BadImageFormatException or NotSupportedException) { return null; }
    }
    private static LoadedLibraryIdentity? CaptureCompiler()
    {
        if (UsesManagedImages) return CaptureManaged(typeof(NativeObjectIdentity).Assembly);
        try
        {
            return Environment.ProcessPath is { } path ? LoadedLibraryIdentity.CaptureFile(path,
                OperatingSystem.IsLinux() ? ArtifactCache.HashFile("/proc/self/exe") : null) : null;
        }
        catch (Exception ex) when (ArtifactCache.IsCacheIo(ex)) { return null; }
    }
    internal const string Schema = "whole-program-object-v1";
    internal const string BackendAbi = "rigi-native-abi-v3-scalar-library-argv";
    internal const string BuildKind = "whole-program-executable";
    // 模块 BIL 缓存复用已加载 compiler 的 MVID/内容/映射校验；无需 LLVM 查询。
    internal static string? CompilerContentIdentity() => Compiler?.Verify();
    internal static string? BindingContentIdentity() => Binding?.Verify();
    internal static string Compute(string canonicalBil, string compiler, string binding,
        string llvm, string target, string layout, string runtime, string buildKind = BuildKind,
        string optimization = "default<O2>", string cpu = "generic", string features = "",
        string relocation = "PIC", string codeModel = "default", string objectAbi = "host-object-v1") =>
        ArtifactCache.Identity(Schema, BackendAbi, buildKind, canonicalBil, compiler, binding,
            llvm, target, layout, runtime, optimization, cpu, features, relocation, codeModel, "codegen-level-default", objectAbi);
    internal static string? TryCompute(BilModule module, string target, string layout, string? runtime,
        string buildKind = BuildKind)
    {
        if (runtime == null) return null;
        try
        {
            var llvm = LlvmHost.ContentIdentity();
            if (llvm == null) return null;
            var compiler = Compiler?.Verify();
            var binding = Binding?.Verify();
            if (compiler == null || binding == null) return null;
            return Compute(BilWriter.Write(module), compiler, binding, llvm, target, layout, runtime, buildKind);
        }
        catch (Exception ex) when (ArtifactCache.IsCacheIo(ex) || ex is NotSupportedException) { return null; }
    }
}
