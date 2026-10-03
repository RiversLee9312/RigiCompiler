using System.Text;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Symbols;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Modules;

/// <summary>产品事务的 Native 回调；Rigi 依赖只合并 BIL，每个最终映像只有一份运行时。</summary>
internal static class ModuleNativePublication
{
    internal static string FileName(ModuleConfiguration configuration) => configuration.Type switch
    {
        ModuleProductKind.Executable => OperatingSystem.IsWindows() ? "app.exe" : "app",
        ModuleProductKind.StaticLibrary => OperatingSystem.IsWindows() ? configuration.Name + ".lib" : "lib" + configuration.Name + ".a",
        _ => OperatingSystem.IsWindows() ? configuration.Name + ".dll" : "lib" + configuration.Name + ".so"
    };

    internal static Task PublishAsync(ModuleBuilt built, string stage)
    {
        var configuration = built.Context.Module.Configuration;
        var exports = configuration.Exports.Select(e => new NativeExport(e.Key, e.Value)).ToArray();
        if (built.Context.Module.CompilerOwned)
            exports = [new("rigi_std_abs_i32", "core.math::$abs(x:.i32)@.i32"),
                new("rigi_std_min_i32", "core.math::$min(a:.i32,b:.i32)@.i32"),
                new("rigi_std_max_i64", "core.math::$max(a:.i64,b:.i64)@.i64")];
        var ownFunctions = built.Artifact.ReadBil().Functions.Select(f => f.Symbol).ToHashSet(StringComparer.Ordinal);
        foreach (var export in exports)
            if (!ownFunctions.Contains(export.Canonical))
                throw new ModuleConfigurationException("C 导出必须属于当前模块的本地实现：" + export.Canonical);
        var kind = configuration.Type switch
        {
            ModuleProductKind.Executable => NativeBuildKind.Executable,
            ModuleProductKind.StaticLibrary => NativeBuildKind.StaticLibrary,
            _ => NativeBuildKind.DynamicLibrary
        };
        var module = kind == NativeBuildKind.Executable ? ModuleEntrypoint.Select(built).Module : built.Linked;
        var code = NativeCommand.EmitAndLink(module, Path.Combine(stage, FileName(configuration)),
            null, null, null, null, null, null, "module --publish", new(kind, exports));
        if (code != 0) throw new ModuleConfigurationException("Native 产品生成失败：" + configuration.ModuleId + "，exit " + code);
        if (kind == NativeBuildKind.Executable) return Task.CompletedTask;
        var header = new StringBuilder("#pragma once\n#include <stdint.h>\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n");
        foreach (var export in exports.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var signature = CanonicalSignature.Parse(export.Canonical);
            header.Append(NativeBuildOptions.CType(signature.ReturnTypeRef)).Append(' ').Append(export.Name).Append('(');
            header.Append(signature.Parameters.Count == 0 ? "void" : string.Join(", ",
                signature.Parameters.Select((p, i) => NativeBuildOptions.CType(p.TypeRef) + " arg" + i)));
            header.Append(");\n");
        }
        header.Append("#ifdef __cplusplus\n}\n#endif\n");
        File.WriteAllText(Path.Combine(stage, configuration.Name + ".h"), header.ToString());
        if (kind == NativeBuildKind.StaticLibrary)
        {
            var libuv = LibuvResolver.Resolve(null) ?? throw new ModuleConfigurationException("缺少 libuv 静态依赖");
            var mimalloc = MimallocResolver.Resolve(null) ?? throw new ModuleConfigurationException("缺少 mimalloc 静态依赖");
            var dependencies = Path.Combine(stage, "native-dependencies"); Directory.CreateDirectory(dependencies);
            File.Copy(libuv.StaticLibPath, Path.Combine(dependencies, Path.GetFileName(libuv.StaticLibPath)));
            File.Copy(mimalloc.StaticLibPath, Path.Combine(dependencies, Path.GetFileName(mimalloc.StaticLibPath)));
            // 不嵌套归档。宿主按下面顺序链接，禁止同时链接另一个带 Rigi RT 的产品。
            var system = OperatingSystem.IsWindows() ? "-lws2_32 -luserenv -liphlpapi -lpsapi -ladvapi32 -lshell32 -lole32 -luuid -lbcrypt" : "-lpthread -ldl -lm";
            // pkg-config 正确转义 -I/-L 路径；位置 archive 片段可能丢转义，使用精确文件名 -l: 保持静态选择与顺序。
            File.WriteAllText(Path.Combine(stage, configuration.Name + ".pc"),
                "prefix=${pcfiledir}\nName: " + configuration.Name + "\nDescription: Rigi scalar C library\nVersion: " + configuration.Version
                + "\nLibs: -L${prefix} -l:" + FileName(configuration) + " -L${prefix}/native-dependencies -l:" + Path.GetFileName(libuv.StaticLibPath)
                + " -l:" + Path.GetFileName(mimalloc.StaticLibPath) + " " + system + "\nCflags: -I${prefix}\n");
        }
        File.WriteAllText(Path.Combine(stage, "native-contract.txt"),
            "同一进程只允许一个 Rigi runtime owner；所有 API 在同一宿主线程同步调用。库须保留到进程退出，不支持 dlclose。\n"
            + "bool 使用 uint8_t（输入非零为 true），char 使用 uint32_t Unicode scalar。未捕获异常报告诊断并终止进程，异常不越过 C 边界。\n"
            + "禁止导出闭包发布协程任务。Rigi 模块消费者导入 module.rgi 的 BIL，不能额外链接这些 Native 库。\n");
        return Task.CompletedTask;
    }
}
