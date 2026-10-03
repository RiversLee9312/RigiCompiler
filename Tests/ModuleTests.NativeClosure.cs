using RigiCompiler.Modules;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Pipeline;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestNativeLibraryInitialization()
    {
        var std = CompileInterfaceProbe("stdlib@1.0.0", StdlibSources.ParseAll().ToArray(), [], true).Artifact;
        foreach (var (label, source, allowed) in new[]
        {
            ("同步Std及用户初始化", "priv var count: i32 = seed()\npriv func seed(): i32 { return 40 }\npriv shared singleton class State { pub init() {} }\npub func exported(): i32 { return count + 2 }", true),
            ("全局初始化发布任务", "priv var count: i32 = seed()\npriv func seed(): i32 { core.coroutine.sleep(10)\n return 40 }\npub func exported(): i32 { return 42 }", false),
            ("eager singleton构造发布任务", "priv shared singleton class State { pub init() { core.coroutine.sleep(10) } }\npub func exported(): i32 { return 42 }", false)
        })
        {
            var (artifact, unit) = CompileInterfaceProbe("lifecycle@1.0.0", [TestHarness.ParseRoot(source, "source/lifecycle.rg")], [std]);
            var linked = ModuleApplicationLinker.Link([std], artifact.ReadBil(), unit.Symbols);
            var canonical = linked.Functions.Single(f => f.Symbol.Contains("$exported(", StringComparison.Ordinal)).Symbol;
            var accepted = true; var diagnostic = "";
            try { MwPipeline.CreateDefault().Run(new MwContext(linked, new(NativeBuildKind.StaticLibrary, [new("exported", canonical)]))); }
            catch (MwNotSupportedException ex) { accepted = false; diagnostic = ex.Message; }
            TestHarness.CheckTrue("真实库初始化闭包 " + label, allowed ? accepted : !accepted && diagnostic.Contains("调度"), diagnostic);
            var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "source.rg"), source);
            File.WriteAllText(Path.Combine(folder, "input.bil"), RigiCompiler.Bil.BilWriter.Write(linked));
            File.WriteAllText(Path.Combine(folder, "result.txt"), accepted ? "accepted" : diagnostic);
        }
    }

    private static void TestNativeExportClosure()
    {
        var std = CompileInterfaceProbe("stdlib@1.0.0", StdlibSources.ParseAll().ToArray(), [], true).Artifact;
        foreach (var (label, source, hint) in new[]
        {
            ("构造器仅发布sleep不await", "class Starter { pub init() { core.coroutine.sleep(10) } }\npub func exported(): i32 { var value = new Starter()\n return 42 }", "调度"),
            ("同步签名yield真实taint", "pub func exported(): i32 { yield\n return 42 }", "挂起"),
            ("构造后虚派发目标不唯一", "open class Value { pub init()\n pub func read(): i32 { return 42 } }\npub func exported(): i32 { var value = new Value()\n return value.read() }", "虚派发")
        })
        {
            var (artifact, unit) = CompileInterfaceProbe("closure@1.0.0", [TestHarness.ParseRoot(source, "source/closure.rg")], [std]);
            var linked = ModuleApplicationLinker.Link([std], artifact.ReadBil(), unit.Symbols);
            var canonical = linked.Functions.Single(f => f.Symbol.Contains("$exported(", StringComparison.Ordinal)).Symbol;
            var context = new MwContext(linked, new(NativeBuildKind.StaticLibrary, [new("exported", canonical)]));
            var rejected = false; var diagnostic = "";
            try { MwPipeline.CreateDefault().Run(context); }
            catch (MwNotSupportedException ex) { diagnostic = ex.Message; rejected = diagnostic.Contains(hint, StringComparison.Ordinal); }
            TestHarness.CheckTrue("真实默认pipeline拒绝C闭包 " + label, rejected, diagnostic);
            var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "input.bil"), System.Text.Encoding.UTF8.GetBytes(RigiCompiler.Bil.BilWriter.Write(linked)));
            File.WriteAllText(Path.Combine(folder, "source.rg"), source);
            File.WriteAllText(Path.Combine(folder, "rejection.txt"), diagnostic);
        }
    }
}
