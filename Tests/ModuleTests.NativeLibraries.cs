using System.Text.Json.Nodes;
using RigiCompiler.Bil;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestNativeLibraries()
    {
        if (!OperatingSystem.IsLinux()) { CaseAssertions.RecordSkip("真实 C archive/dlopen 门禁当前仅 Linux；Windows ABI 另由 CI 验证"); return; }
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        const string source = "namespace cprovider\npriv var count: i32 = 40\npub func add(a: i32, b: i32): i32 { return a + b }\npub func next(delta: i32): i32 { count = count + delta\n return count }\n"
            + "pub func boolean(value: bool): bool { return value }\npub func character(value: char): char { return value }\n"
            + "pub func signed8(value: i8): i8 { return value }\npub func unsigned8(value: u8): u8 { return value }\n"
            + "pub func signed16(value: i16): i16 { return value }\npub func unsigned16(value: u16): u16 { return value }\n"
            + "pub func real32(value: float): float { return value }\npub func real64(value: double): double { return value }\n";
        var (_, bil) = EmitIndependentProbe("cprovider@1.0.0", source);
        File.WriteAllText(Path.Combine(folder, "source.rg"), source); File.WriteAllText(Path.Combine(folder, "input.bil"), BilWriter.Write(bil));
        var exports = bil.Functions.Where(f => !BilLogicalName.IsGlobalInitializer(f.Symbol)).Select(f => new NativeExport(BilLogicalName.Method(f.Symbol), f.Symbol)).ToArray();
        var library = Path.Combine(folder, "libcprovider.a"); var shared = Path.Combine(folder, "libcprovider.so");
        var staticExit = NativeCommand.EmitAndLink(bil, library, null, Path.Combine(folder, "static.ll"), null, null, null, null,
            "module static-library", new(NativeBuildKind.StaticLibrary, exports));
        CaseAssertions.CheckTrue("真实defaultO2 PIC对象归档.a", staticExit == 0 && File.Exists(library));
        var dynamicExit = NativeCommand.EmitAndLink(bil, shared, null, Path.Combine(folder, "dynamic.ll"), null, null, null, null,
            "module dyn-library", new(NativeBuildKind.DynamicLibrary, exports));
        CaseAssertions.CheckTrue("真实defaultO2 PIC runtime+deps链接.so", dynamicExit == 0 && File.Exists(shared));
        var clang = ToolchainResolver.ResolveClang(null)!;
        var declarations = string.Join("\n", exports.Select(e =>
        {
            var signature = RigiCompiler.Middleware.Symbols.CanonicalSignature.Parse(e.Canonical);
            return NativeBuildOptions.CType(signature.ReturnTypeRef) + " " + e.Name + "(" + (signature.Parameters.Count == 0 ? "void" :
                string.Join(",", signature.Parameters.Select(p => NativeBuildOptions.CType(p.TypeRef)))) + ");";
        }));
        var checks = "assert(add(20,22)==42); assert(next(2)==42); assert(next(1)==43); assert(boolean(7)==1); assert(boolean(0)==0);"
            + "assert(character(0x1f98a)==0x1f98a); assert(signed8(-101)==-101); assert(unsigned8(251)==251);"
            + "assert(signed16(-30001)==-30001); assert(unsigned16(60001)==60001); assert(real32(1.25f)==1.25f); assert(real64(3.125)==3.125);";
        var staticSource = Path.Combine(folder, "static.c");
        File.WriteAllText(staticSource, "#include <stdint.h>\n#include <assert.h>\n" + declarations + "\nint main(void){" + checks + "return 0;}\n");
        var uv = LibuvResolver.Resolve(null)!; var mi = MimallocResolver.Resolve(null)!;
        var staticExe = Path.Combine(folder, "static-host");
        var cexit = ExternalProcess.Run(clang, [staticSource, library, uv.StaticLibPath, mi.StaticLibPath, "-lpthread", "-ldl", "-lm", "-fuse-ld=lld", "-o", staticExe],
            out var cout, out var cerr, closeStdin: true);
        File.WriteAllText(Path.Combine(folder, "static-link.log"), cout + cerr); File.WriteAllText(Path.Combine(folder, "static-link.exit"), cexit.ToString());
        CaseAssertions.CheckTrue("C宿主直接链接.a与明确外部RT依赖", cexit == 0, cerr);
        var environment = new Dictionary<string, string> { ["RIGI_RT_MEMTRACK"] = "1" };
        var run = cexit == 0 ? ExternalProcess.Run(staticExe, [], out cout, out cerr, environment: environment, closeStdin: true) : -1;
        File.WriteAllText(Path.Combine(folder, "static-run.log"), cout + cerr); File.WriteAllText(Path.Combine(folder, "static-run.exit"), run.ToString());
        CaseAssertions.CheckTrue("真实C静态调用body+once全局状态+bool/char/窄整数/浮点MEMTRACK", run == 0 && cout.Length == 0 && cerr.Length == 0, cerr);
        var dynamicSource = Path.Combine(folder, "dynamic.c");
        var bindings = string.Join("\n", exports.Select(e =>
        {
            var signature = RigiCompiler.Middleware.Symbols.CanonicalSignature.Parse(e.Canonical);
            var arguments = signature.Parameters.Count == 0 ? "void" : string.Join(",", signature.Parameters.Select(p => NativeBuildOptions.CType(p.TypeRef)));
            return "typedef " + NativeBuildOptions.CType(signature.ReturnTypeRef) + " (*fn_" + e.Name + ")(" + arguments + "); fn_" + e.Name
                + " " + e.Name + "=(fn_" + e.Name + ")dlsym(handle,\"" + e.Name + "\"); assert(" + e.Name + ");";
        }));
        File.WriteAllText(dynamicSource, "#include <stdint.h>\n#include <assert.h>\n#include <dlfcn.h>\nint main(int argc,char**argv){assert(argc==2); void*handle=dlopen(argv[1],RTLD_NOW);assert(handle);"
            + bindings + "assert(dlsym(handle,\"rigi_library_ensure\")==0);" + checks + "return 0;}\n");
        var dynamicExe = Path.Combine(folder, "dynamic-host");
        cexit = ExternalProcess.Run(clang, [dynamicSource, "-ldl", "-o", dynamicExe], out cout, out cerr, closeStdin: true);
        File.WriteAllText(Path.Combine(folder, "dynamic-link.log"), cout + cerr); File.WriteAllText(Path.Combine(folder, "dynamic-link.exit"), cexit.ToString());
        CaseAssertions.CheckTrue("C宿主dlopen/dlsym链接", cexit == 0, cerr);
        run = cexit == 0 && dynamicExit == 0 ? ExternalProcess.Run(dynamicExe, [shared], out cout, out cerr, environment: environment, closeStdin: true) : -1;
        File.WriteAllText(Path.Combine(folder, "dynamic-run.log"), cout + cerr); File.WriteAllText(Path.Combine(folder, "dynamic-run.exit"), run.ToString());
        CaseAssertions.CheckTrue("真实DSO body/once状态/scalar ABI与hidden RT且MEMTRACK零", run == 0 && cout.Length == 0 && cerr.Length == 0, cerr);
        File.WriteAllText(Path.Combine(folder, "evidence.json"), new JsonObject { ["staticCompileExit"] = staticExit, ["dynamicCompileExit"] = dynamicExit,
            ["dynamicHostExit"] = run, ["librarySha"] = File.Exists(library) ? RigiCompiler.Middleware.Cache.ArtifactCache.HashFile(library) : null,
            ["sharedSha"] = File.Exists(shared) ? RigiCompiler.Middleware.Cache.ArtifactCache.HashFile(shared) : null,
            ["compilerSha"] = RigiCompiler.Middleware.Cache.NativeObjectIdentity.CompilerContentIdentity(), ["target"] = LlvmHost.HostTriple }.ToJsonString());
    }
}
