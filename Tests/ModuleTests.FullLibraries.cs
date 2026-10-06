using System.Text.Json.Nodes;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Modules;
using RigiCompiler.Middleware.Cache;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestFullModuleLibraries()
    {
        if (!OperatingSystem.IsLinux()) { CaseAssertions.RecordSkip("真实CLI archive/DSO及C异常边界当前仅Linux；Windows未实测"); return; }
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "worker-identity.json"), ModuleWorkerIdentity().ToJsonString());
        var root = Path.Combine(folder, "library with space"); Directory.CreateDirectory(Path.Combine(root, "source"));
        const string source = "namespace libdemo\npriv var count: i32 = 40\n"
            + "@EntryPoint\npub func main(): i32 { count = 999\n return 99 }\n"
            + "pub func add(a: i32, b: i32): i32 { return core.math.abs(a + b) }\n"
            + "pub func next(delta: i32): i32 { count = count + delta\n return count }\n"
            + "pub func fail(): i32 { throw new core.RuntimeException(\"c-boundary-failure\") }\n";
        File.WriteAllText(Path.Combine(root, "source/lib.rg"), source);
        string Configuration(string kind) => "schema: 1\nname: libdemo\nversion: 1.0.0\ntype: " + kind
            + "\ndependencies: [{name: stdlib, version: 1.0.0}]\ndefault-profile: debug\nprofiles: {debug: {target: vm}}\n"
            + "exports:\n  api_add: 'libdemo::$add(a:.i32,b:.i32)@.i32'\n  api_next: 'libdemo::$next(delta:.i32)@.i32'\n  api_fail: 'libdemo::$fail()@.i32'\n";
        void Save(string label, int code, string stdout, string stderr)
        {
            File.WriteAllText(Path.Combine(folder, label + ".exit"), code.ToString());
            File.WriteAllText(Path.Combine(folder, label + ".stdout"), stdout); File.WriteAllText(Path.Combine(folder, label + ".stderr"), stderr);
        }
        File.WriteAllText(Path.Combine(root, "module.yaml"), Configuration("dyn-library"));
        var published = ExecuteModule("module", "--publish", "--root", root);
        Save("dynamic-publish", published.Code, published.Out, published.Err);
        var product = Path.Combine(root, "product/debug/modules/libdemo/1.0.0"); var shared = Path.Combine(product, "liblibdemo.so");
        CaseAssertions.CheckTrue("正式CLI dyn-library生成真实so及C头文件", published.Code == 0 && File.Exists(shared)
            && File.Exists(Path.Combine(product, "libdemo.h")), published.Err);
        if (published.Code != 0) return;
        var sharedSha = ArtifactCache.HashFile(shared);
        var wrongRun = ExecuteModule("module", "--run", "--root", root);
        Save("library-run-reject", wrongRun.Code, wrongRun.Out, wrongRun.Err);
        CaseAssertions.CheckTrue("library --run早拒且不破坏已发布Native产品", wrongRun.Code == 1 && sharedSha == ArtifactCache.HashFile(shared));
        var clang = ToolchainResolver.ResolveClang(null)!;
        var dynamicSource = Path.Combine(folder, "dynamic-host.c"); var dynamicHost = Path.Combine(folder, "dynamic-host");
        File.WriteAllText(dynamicSource, "#include <assert.h>\n#include <stdint.h>\n#include <dlfcn.h>\n#include <string.h>\n"
            + "int main(int argc,char**argv){assert(argc>=2);void*h=dlopen(argv[1],RTLD_NOW);assert(h);"
            + "int32_t(*add)(int32_t,int32_t)=(int32_t(*)(int32_t,int32_t))dlsym(h,\"api_add\");"
            + "int32_t(*next)(int32_t)=(int32_t(*)(int32_t))dlsym(h,\"api_next\");int32_t(*fail)(void)=(int32_t(*)(void))dlsym(h,\"api_fail\");"
            + "assert(add&&next&&fail);assert(dlsym(h,\"rigi_library_ensure\")==0);assert(add(20,22)==42);assert(next(2)==42);assert(next(1)==43);"
            + "if(argc==3){fail();return 9;}return 0;}\n");
        var code = ExternalProcess.Run(clang, [dynamicSource, "-ldl", "-o", dynamicHost], out var stdout, out var stderr, closeStdin: true);
        Save("dynamic-c-link", code, stdout, stderr); CaseAssertions.CheckTrue("实际C动态宿主链接", code == 0, stderr);
        var environment = new Dictionary<string, string> { ["RIGI_RT_MEMTRACK"] = "1" };
        code = code == 0 ? ExternalProcess.Run(dynamicHost, [shared], out stdout, out stderr, environment: environment, closeStdin: true) : -1;
        Save("dynamic-c-run", code, stdout, stderr);
        CaseAssertions.CheckTrue("真实CLI DSO C body/Std调用/once状态/不运行Rigi main/隐藏RT/MEMTRACK", code == 0 && stdout.Length == 0 && stderr.Length == 0, stderr);
        code = ExternalProcess.Run(dynamicHost, [shared, "fail"], out stdout, out stderr, environment: environment, closeStdin: true);
        Save("dynamic-c-uncaught", code, stdout, stderr);
        CaseAssertions.CheckTrue("真实C边界未捕获异常typed消息/终止1/无泄漏", code == 1 && stdout.Length == 0
            && stderr.Contains("core::RuntimeException: c-boundary-failure", StringComparison.Ordinal)
            && !stderr.Contains("memory leak", StringComparison.OrdinalIgnoreCase), stderr);
        File.WriteAllText(Path.Combine(root, "module.yaml"), Configuration("static-library"));
        var bundle = Path.Combine(folder, "library bundle.zip");
        var bundled = ExecuteModule("module", "--bundle", "--root", root, "--output", bundle);
        Save("static-bundle", bundled.Code, bundled.Out, bundled.Err);
        var archive = Path.Combine(product, "liblibdemo.a");
        CaseAssertions.CheckTrue("正式static-library bundle先发布实际a/header/依赖pc", bundled.Code == 0 && File.Exists(archive)
            && File.Exists(Path.Combine(product, "libdemo.pc")), bundled.Err);
        var staticSource = Path.Combine(folder, "static-host.c"); var staticHost = Path.Combine(folder, "static-host");
        File.WriteAllText(staticSource, "#include <assert.h>\n#include \"libdemo.h\"\nint main(void){assert(api_add(20,22)==42);assert(api_next(2)==42);assert(api_next(1)==43);return 0;}\n");
        const string compile = "import subprocess,sys,shlex,os\nenv=os.environ.copy();env['PKG_CONFIG_PATH']=sys.argv[3]\np=subprocess.run(['pkg-config','--cflags','--libs','libdemo'],capture_output=True,text=True,env=env)\nprint(p.stdout,end='')\nif p.returncode: print(p.stderr,file=sys.stderr);sys.exit(p.returncode)\nsys.exit(subprocess.call([sys.argv[1],sys.argv[2]]+shlex.split(p.stdout)+['-fuse-ld=lld','-o',sys.argv[4]]))";
        code = ExternalProcess.Run("python3", ["-c", compile, clang, staticSource, product, staticHost], out stdout, out stderr, closeStdin: true);
        Save("static-c-link", code, stdout, stderr); CaseAssertions.CheckTrue("CLI实际a通过带空格pc及头文件C链接", code == 0, stderr);
        code = code == 0 ? ExternalProcess.Run(staticHost, [], out stdout, out stderr, environment: environment, closeStdin: true) : -1;
        Save("static-c-run", code, stdout, stderr);
        CaseAssertions.CheckTrue("真实CLI静态C调用实现/初始化一次/不运行main/MEMTRACK", code == 0 && stdout.Length == 0 && stderr.Length == 0, stderr);
        var client = Path.Combine(folder, "consumer");
        var initialized = ExecuteModule("module", "--init", "--root", client);
        var installed = ExecuteModule("module", "--install", bundle, "--root", client); Save("install", installed.Code, installed.Out, installed.Err);
        CaseAssertions.CheckTrue("发布library bundle正式install", initialized.Code == 0 && installed.Code == 0, installed.Err);
        var provider = Path.Combine(client, "dependencies/libdemo/1.0.0");
        Directory.Move(Path.Combine(provider, "source"), Path.Combine(folder, "removed-provider-source"));
        File.WriteAllText(Path.Combine(client, "module.yaml"), "schema: 1\nname: consumer\nversion: 1.0.0\ntype: executable\n"
            + "dependencies: [{name: stdlib, version: 1.0.0}, {name: libdemo, version: 1.0.0}]\ndefault-profile: debug\nprofiles: {debug: {target: vm}}\n");
        File.WriteAllText(Path.Combine(client, "source/main.rg"), "namespace client\n@EntryPoint\npub func main(): i32 { return libdemo.add(20,22) }\n");
        var result = ModuleBuildService.BuildAsync(client).GetAwaiter().GetResult();
        var dependency = result.Modules.Single(b => b.Artifact.ModuleId == "libdemo@1.0.0");
        var selected = ModuleEntrypoint.Select(result.Entry);
        var vm = BilVm.Run(selected.Module, entryPoint: selected.Canonical);
        CaseAssertions.CheckTrue("provider sourceRemoved真实compile0/ownAST0且依赖main不误选", dependency is
            { Compiled: false, OwnSourceCount: 0, CacheStatus: "prebuilt" } && vm.Exception == null && vm.ReturnValue is VmI32 { Value: 42 });
        var consumed = ExecuteModule("module", "--run", "--root", client); Save("consumer-cli", consumed.Code, consumed.Out, consumed.Err);
        CaseAssertions.CheckTrue("正式CLI消费者只API/BIL导入并运行自身入口42", consumed.Code == 42 && consumed.Out.Length == 0, consumed.Err);
        File.WriteAllText(Path.Combine(folder, "library-evidence.json"), new JsonObject
        { ["dynamicSha"] = sharedSha, ["staticSha"] = ArtifactCache.HashFile(archive), ["providerCompiled"] = dependency.Compiled,
            ["providerOwnSources"] = dependency.OwnSourceCount, ["consumerExit"] = consumed.Code }.ToJsonString());
    }
}
