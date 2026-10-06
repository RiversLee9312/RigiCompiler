using RigiCompiler.Bil;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Pipeline;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestNativeExportRoots()
    {
        var (_, module) = EmitIndependentProbe("exports@1.0.0", "namespace exportdemo\npriv func hidden(value: i32): i32 { return value + 1 }\npub func answer(value: i32): i32 { return hidden(value) }\npub class Box\\<T> { pub init()\n pub static func read(): i32 { return 7 } }\npub func generic\\<T>(): i32 { return 7 }\npub func stringResult(): String { return \"unsupported\" }\n");
        var answer = module.Functions.Single(f => f.Symbol.Contains("$answer(", StringComparison.Ordinal)).Symbol;
        var options = new NativeBuildOptions(NativeBuildKind.StaticLibrary, [new("export_answer", answer)]);
        var context = new MwContext(module, options);
        new MwPipeline().Add(new LayoutStage()).Add(new MirBuildStage()).Run(context);
        CaseAssertions.CheckTrue("C导出非entry根进入布局及真实MIR私有实现闭包", context.Mir!.Functions.Any(f => f.Symbol.Canonical == answer)
            && context.Mir.Functions.Any(f => BilLogicalName.Method(f.Symbol.Canonical) == "hidden") && context.Layout != null);
        var rejected = false;
        try { options.ValidateSynchronousClosure(context.Mir, new HashSet<string>([answer], StringComparer.Ordinal)); }
        catch (MwNotSupportedException) { rejected = true; }
        CaseAssertions.CheckTrue("同步签名export的plain tainted身份仍明确拒绝", rejected);
        foreach (var name in new[] { "hidden", "read", "generic", "stringResult" })
        {
            var fn = module.Functions.Single(f => BilLogicalName.Method(f.Symbol) == name);
            rejected = false;
            try { _ = new MwContext(module, new(NativeBuildKind.DynamicLibrary, [new("test_export", fn.Symbol)])); }
            catch (MwNotSupportedException) { rejected = true; }
            CaseAssertions.CheckTrue("C导出资格拒绝 " + name, rejected);
        }
        CaseAssertions.CheckTrue("固定scalar C字宽与bool字节/char码点合同", NativeBuildOptions.CType(".bool") == "uint8_t"
            && NativeBuildOptions.CType(".char") == "uint32_t" && NativeBuildOptions.CType(".i16") == "int16_t");
    }
}
