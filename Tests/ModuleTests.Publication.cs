using System.Text.Json.Nodes;
using RigiCompiler.Modules;
using RigiCompiler.Middleware.Cache;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "仅记录实际测试入口；NativeAOT 的空 Location 明确表示没有 DLL 路径。")]
    private static void TestStdNativePublication()
    {
        if (!OperatingSystem.IsLinux()) { CaseAssertions.RecordSkip("真实标准库 archive C 宿主当前仅 Linux；Windows 未实测"); return; }
        var folder = InterfaceProbeFolder(); var root = Path.Combine(folder, "std client with space");
        Directory.CreateDirectory(Path.Combine(root, "source"));
        File.WriteAllText(Path.Combine(root, "module.yaml"), "schema: 1\nname: stdclient\nversion: 1.0.0\ntype: executable\ndependencies: [{name: stdlib, version: 1.0.0}]\n");
        File.WriteAllText(Path.Combine(root, "source/main.rg"), "pub func main(): i32 { return 0 }\n");
        using var publication = new ModuleProductPublication();
        var result = ModuleBuildService.BuildAsync(root, publish: built => publication.PublishAsync(built,
            built.Context.Module.CompilerOwned ? ModuleNativePublication.PublishAsync : null)).GetAwaiter().GetResult();
        publication.Commit();
        var standard = result.Modules.Single(b => b.Context.Module.CompilerOwned);
        var product = standard.Context.ProductArtifact;
        var library = Path.Combine(product, "libstdlib.a");
        CaseAssertions.CheckTrue("生产trustedStd发布真实archive/有意义pub scalar根/头文件", File.Exists(library)
            && File.ReadAllText(Path.Combine(product, "stdlib.h")).Contains("rigi_std_abs_i32"));
        var source = Path.Combine(folder, "std-host.c"); var host = Path.Combine(folder, "std-host");
        File.WriteAllText(source, "#include <assert.h>\n#include \"stdlib.h\"\nint main(void){assert(rigi_std_abs_i32(-42)==42);assert(rigi_std_min_i32(3,7)==3);assert(rigi_std_max_i64(17,42)==42);return 0;}\n");
        // pkg-config 的 shell 转义输出须由消费者解析；不能用独立 clang argv 绕过 .pc 路径契约。
        const string compile = "import subprocess,sys,shlex,os\nenv=os.environ.copy();env['PKG_CONFIG_PATH']=os.path.dirname(sys.argv[3])\np=subprocess.run(['pkg-config','--cflags','--libs','stdlib'],capture_output=True,text=True,env=env)\nprint(p.stdout,end='')\nif p.returncode: print(p.stderr,file=sys.stderr);sys.exit(p.returncode)\nsys.exit(subprocess.call([sys.argv[1],sys.argv[2]]+shlex.split(p.stdout)+['-fuse-ld=lld','-o',sys.argv[4]]))";
        var code = ExternalProcess.Run("python3", ["-c", compile, ToolchainResolver.ResolveClang(null)!, source,
            Path.Combine(product, "stdlib.pc"), host], out var stdout, out var stderr, closeStdin: true);
        File.WriteAllText(Path.Combine(folder, "std-c-link.log"), stdout + stderr); File.WriteAllText(Path.Combine(folder, "std-c-link.exit"), code.ToString());
        CaseAssertions.CheckTrue("实际pkg-config消费带空格产品路径/头文件/archive独立依赖", code == 0, stderr);
        code = code == 0 ? ExternalProcess.Run(host, [], out stdout, out stderr, closeStdin: true,
            environment: new Dictionary<string, string> { ["RIGI_RT_MEMTRACK"] = "1" }) : -1;
        File.WriteAllText(Path.Combine(folder, "std-c-run.log"), stdout + stderr); File.WriteAllText(Path.Combine(folder, "std-c-run.exit"), code.ToString());
        CaseAssertions.CheckTrue("Std C实际body/once生命周期/MEMTRACK零", code == 0 && stdout.Length == 0 && stderr.Length == 0, stderr);
        File.WriteAllText(Path.Combine(folder, "publication-evidence.json"), new JsonObject
        { ["runExit"] = code, ["archiveSha"] = File.Exists(library) ? ArtifactCache.HashFile(library) : null,
            ["compilerSha"] = NativeObjectIdentity.CompilerContentIdentity(),
            ["compilerPath"] = typeof(Frontend).Assembly.Location }.ToJsonString());
    }
}
