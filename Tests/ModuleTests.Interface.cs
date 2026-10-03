using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Modules;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static string InterfaceProbeFolder([CallerFilePath] string sourcePath = "")
    {
        var repository = Path.GetDirectoryName(Path.GetDirectoryName(sourcePath))!;
        // 发布资产测试不依赖仓库存在；原仓库可用时保留真实 raw/artifact。
        var root = Directory.Exists(repository) ? Path.Combine(repository, "playground", "module-validation-20261001", "interface-fixture")
            : Path.Combine(Path.GetTempPath(), "rigi-module-interface-" + Environment.ProcessId);
        return Path.Combine(root, "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
    }
    private static (ModuleArtifact Artifact, CompilationUnit Unit) CompileInterfaceProbe(string id, RootASTNode[] sources,
        IReadOnlyList<ModuleArtifact> dependencies, bool compilerOwned = false, bool finalApplication = false,
        string inputDigest = "probe-input")
    {
        var graph = compilerOwned ? new SymbolGraph(id) : SymbolGraph.CreateArtifactOnly(id);
        foreach (var dependency in dependencies) ModuleInterfaceImporter.Import(graph, dependency);
        var unit = new CompilationUnit(graph, sources) { IsFinalModuleApplication = finalApplication };
        var declarations = DeclarationCollector.Collect(unit);
        DeclarationResolver.Resolve(unit, declarations);
        var bound = Binder.Bind(unit, declarations);
        var lowered = Lowerer.Lower(unit, bound);
        var bil = BilEmitter.Emit(unit, lowered, id);
        if (unit.Diagnostics.HasErrors) throw new InvalidOperationException(string.Join('\n', unit.Diagnostics.Diagnostics.Select(d => d.Message)));
        return (ModuleInterface.Export(unit, bound, bil, inputDigest, compilerOwned), unit);
    }
    private static void TestInterfaceRoundTrip()
    {
        var (std, _) = CompileInterfaceProbe("stdlib@1.0.0", StdlibSources.ParseAll().ToArray(), [], true);
        TestHarness.CheckTrue("可信std provider确实输出BIL/API配对", std.ReadBil().Functions.Count > 100 && std.InterfaceBytes.Length > 10000);
        var folder = InterfaceProbeFolder();
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "stdlib.interface.json"), std.InterfaceBytes);
        File.WriteAllBytes(Path.Combine(folder, "stdlib.bil"), std.BilBytes);
        var providerFile = Path.Combine(folder, "provider.rg");
        File.WriteAllText(providerFile, "namespace api\npriv func seed(): i32 { return 37 }\npub class Holder\\<T> { pub var value: T\n pub init(_ -> value) }\npub func result(): i32 { return seed() }\npub func identity\\<T>(value: T): T { return value }\n");
        var (provider, providerUnit) = CompileInterfaceProbe("provider@1.0.0", Frontend.ParseRoots([new SourceInput(File.ReadAllText(providerFile), "source/provider.rg")]), [std]);
        File.WriteAllBytes(Path.Combine(folder, "provider.interface.json"), provider.InterfaceBytes);
        File.WriteAllBytes(Path.Combine(folder, "provider.bil"), provider.BilBytes);
        File.Delete(providerFile);
        var (app, unit) = CompileInterfaceProbe("app@1.0.0", [TestHarness.ParseRoot(
            "import api.*\npub func main(): i32 { var holder = new Holder\\<i32>(identity\\<i32>(result()))\n return holder.value }\n", "source/app.rg")], [std, provider]);
        TestHarness.CheckTrue("consumer真实只绑定自身source且provider文件已移走", unit.SourceFiles.Count == 1 && !File.Exists(providerFile)
            && unit.Symbols.Bootstrap.DeclarationSource == null);
        var imported = unit.Symbols.GetNamespace(["api"]).Types.Single(t => t.Name == "Holder");
        TestHarness.CheckTrue("开放GP导入保持owner声明单例", ReferenceEquals(imported.Fields.Single(f => f.Name == "value").FieldType, imported.GenericParameters[0])
            && imported.SourceFile == null && imported.DeclarationSpan?.sourceName == "source/provider.rg");
        TestHarness.CheckTrue("private源lookup不泄露provider实现", !unit.Symbols.GetNamespace(["api"]).Methods.Any(m => m.Name == "seed"));
        var linked = BilModuleLinker.Link([std.ReadBil(), provider.ReadBil(), app.ReadBil()]);
        BilTestHarness.CheckBilValid("无provider AST的真正独立接口/BIL链接", linked);
        var result = BilVm.Run(linked);
        TestHarness.CheckTrue("artifact-only std+provider+app真实VM泛型体返回37", result.Exception == null && result.ReturnValue is VmI32 { Value: 37 }, result.Exception?.ToString() ?? "");
        var graph = SymbolGraph.CreateArtifactOnly("shape@1.0.0"); ModuleInterfaceImporter.Import(graph, std);
        TestHarness.CheckTrue("CallWildcard与Pair在consumer P1前即就绪", graph.Bootstrap.CallWildcard.Parameters.Count == 3
            && graph.GetNamespace(["core"]).Types.Any(t => t.Name == "Pair" && !t.IsBuiltin));
        var span = graph.Bootstrap.SpanDefinition;
        TestHarness.CheckTrue("artifact-only Span固定界与实例方法", span.GenericParameters[0].Constraints.Count == 1
            && ReferenceEquals(span.GenericParameters[0].Constraints[0].Bound, graph.Bootstrap.ValueType) && span.Methods.Count != 0);
        ModuleArtifact Tamper(Action<JsonObject> action)
        {
            var root = JsonNode.Parse(std.InterfaceBytes)!.AsObject(); action(root);
            var payload = Encoding.UTF8.GetBytes(root["payload"]!.ToJsonString());
            var hash = Convert.ToHexString(SHA256.HashData(payload)); root["apiHash"] = hash;
            return std with { InterfaceBytes = Encoding.UTF8.GetBytes(root.ToJsonString()), ApiHash = hash };
        }
        var alias = Tamper(root => root["payload"]!["declarations"]!.AsArray().Single(n => n!["tag"]!.GetValue<string>() == "type" && n["builtin"]!.GetValue<bool>() && n["name"]!.GetValue<string>() == "i32")!["alias"] = ".i64");
        TestHarness.CheckTrue("可信接口i32 alias篡改拒绝", Reject(() => ModuleInterfaceImporter.Import(SymbolGraph.CreateArtifactOnly("bad@1.0.0"), alias)));
        var map = Tamper(root => root["payload"]!["declarations"]!.AsArray().Single(n => n!["tag"]!.GetValue<string>() == "type" && n["builtin"]!.GetValue<bool>() && n["name"]!.GetValue<string>() == "Map")!["gps"]!.AsArray().RemoveAt(1));
        TestHarness.CheckTrue("可信接口Map元数篡改拒绝", Reject(() => ModuleInterfaceImporter.Import(SymbolGraph.CreateArtifactOnly("bad@1.0.0"), map)));
        JsonObject Declaration(JsonObject root, string tag, string name) => root["payload"]!["declarations"]!.AsArray()
            .Single(n => n!["tag"]!.GetValue<string>() == tag && n["name"]!.GetValue<string>() == name
                && (name != "Span" || n["builtin"]!.GetValue<bool>())
                && (name != "..encode.graph" || n["owner"]!.GetValue<string>().Contains("/type/..ISerializable/", StringComparison.Ordinal)))!.AsObject();
        (string Label, Action<JsonObject> Change)[] invalid =
        [
            ("逐声明未知字段", r => Declaration(r, "type", "..ISerializable")["unexpected"] = true),
            ("逐GP未知字段", r => Declaration(r, "type", "Span")["gps"]![0]!["unexpected"] = true),
            ("逐参数未知字段", r => Declaration(r, "method", "call???")["parameters"]![0]!["unexpected"] = true),
            ("逐case未知字段", r => r["payload"]!["declarations"]!.AsArray().First(n => n!["tag"]!.GetValue<string>() == "case")!["unexpected"] = true),
            ("GP坏owner", r => Declaration(r, "type", "Span")["gps"]![0]!["identity"]!["ownerId"] = "missing-owner"),
            ("builtin GP错index", r => Declaration(r, "type", "Span")["gps"]![0]!["identity"]!["index"] = 999),
            ("悬空类型引用", r => Declaration(r, "method", "call???")["parameters"]![0]!["type"]!["typeId"] = "missing-type"),
            ("新声明假来源", r => Declaration(r, "type", "..ISerializable")["module"] = "forged@1.0.0"),
            ("required field空引用", r => Declaration(r, "type", "Parcel")["fields"]!.AsArray().Add(null)),
            ("成员错owner", r => Declaration(r, "method", "..encode.graph")["owner"] = Declaration(r, "type", "Parcel")["id"]!.DeepClone()),
            ("固定Span约束", r => Declaration(r, "type", "Span")["gps"]![0]!["constraints"]!.AsArray().Clear()),
            ("固定Any完整参数", r => Declaration(r, "method", "call???")["parameters"]![2]!["type"] = Declaration(r, "method", "call???")["parameters"]![0]!["type"]!.DeepClone()),
            ("Serializable接口方法", r => Declaration(r, "method", "..encode.graph")["static"] = true),
            ("接口和BIL不同native ABI", r => Declaration(r, "method", "any_hash")["nativeLibrary"] = "fake-runtime")
        ];
        foreach (var (label, change) in invalid)
            TestHarness.CheckTrue("无AST严格接口拒绝 " + label, Reject(() => ModuleInterfaceImporter.Import(SymbolGraph.CreateArtifactOnly("bad@1.0.0"), Tamper(change))));
        TestHandleBindingForgery(std.ReadBil());
        var ordinary = SymbolGraph.CreateArtifactOnly("ordinary@1.0.0");
        TestHarness.CheckTrue("同名stdlib接口无resolver信任不能自授ABI能力", Reject(() => ModuleInterfaceImporter.Import(ordinary, std with { CompilerOwned = false }))
            && ordinary.Bootstrap.Any.Methods.Count == 0 && ordinary.ImportedSymbols.Values.All(s => !s.IsCompilerLibrary));
        ModuleArtifact OrdinaryMap(bool shared, bool constraint)
        {
            var root = JsonNode.Parse(std.InterfaceBytes)!.AsObject();
            var definition = root["payload"]!["declarations"]!.AsArray().Single(n => n!["tag"]!.GetValue<string>() == "type"
                && n["builtin"]!.GetValue<bool>() && n["name"]!.GetValue<string>() == "Map")!.DeepClone();
            definition["gps"]![0]!["shared"] = shared;
            if (constraint) definition["gps"]![0]!["constraints"]!.AsArray().Add((JsonNode)new JsonObject { ["kind"] = (int)GenericConstraintKind.Extends,
                ["bound"] = new JsonObject { ["typeId"] = "bootstrap/namespace/core/type/ValueType/0", ["args"] = new JsonArray() } });
            root["payload"] = new JsonObject { ["declarations"] = new JsonArray(definition), ["closedTypes"] = new JsonArray() };
            var bil = Encoding.UTF8.GetBytes(BilWriter.Write(new BilModule()));
            root["bilDigest"] = Convert.ToHexString(SHA256.HashData(bil));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root["payload"]!.ToJsonString()))); root["apiHash"] = hash;
            return std with { InterfaceBytes = Encoding.UTF8.GetBytes(root.ToJsonString()), ApiHash = hash, BilBytes = bil, CompilerOwned = false };
        }
        var mapGraph = SymbolGraph.CreateArtifactOnly("ordinary-map@1.0.0");
        ModuleInterfaceImporter.Import(mapGraph, OrdinaryMap(false, true));
        TestHarness.CheckTrue("ordinary单Map无metadata不能覆盖bootstrap约束", mapGraph.Bootstrap.MapDefinition.GenericParameters.All(g => g.Constraints.Count == 0 && !g.RequiresSharedSafe));
        var sharedGraph = SymbolGraph.CreateArtifactOnly("ordinary-shared-map@1.0.0");
        TestHarness.CheckTrue("ordinary单Map shared伪造拒绝且manifest未变", Reject(() => ModuleInterfaceImporter.Import(sharedGraph, OrdinaryMap(true, false)))
            && sharedGraph.Bootstrap.MapDefinition.GenericParameters.All(g => g.Constraints.Count == 0 && !g.RequiresSharedSafe));
    }
    private static void TestHandleBindingForgery(BilModule trusted)
    {
        var native = trusted.LocalSymbols.OfType<BilSimpleMemberDeclaration>()
            .Single(m => m.Modifiers.OfType<BilNativeSymbolModifier>().Any(n => n.Symbol == "handle_target"));
        var counterfeit = new BilModule();
        var fake = native.Symbol.Replace("__m_" + ModuleOrigin.Hash("stdlib@1.0.0"), "__m_" + ModuleOrigin.Hash("user@1.0.0"), StringComparison.Ordinal);
        counterfeit.LocalSymbols.Add(new BilSimpleMemberDeclaration(native.Kind, fake, native.Modifiers.ToArray()));
        TestHarness.CheckTrue("普通用户同logical同native符号不获Handle入口权限", BilVerifier.Verify(counterfeit)
            .Any(e => e.Message.Contains("Handle native 机制入口", StringComparison.Ordinal)));
        var original = trusted.Functions.Single(f => BilCompilerSymbols.Logical(f.Symbol).StartsWith("core::$handle_load(", StringComparison.Ordinal));
        var caller = new BilFunction(original.Symbol.Replace("__m_" + ModuleOrigin.Hash("stdlib@1.0.0"), "__m_" + ModuleOrigin.Hash("user@1.0.0"), StringComparison.Ordinal));
        caller.Args.AddRange(original.Args); caller.Vars.AddRange(original.Vars); caller.Blocks.AddRange(original.Blocks);
        trusted.Functions.Add(caller);
        var declaration = trusted.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Single(m => m.Symbol == original.Symbol);
        trusted.LocalSymbols.Add(new BilSimpleMemberDeclaration(declaration.Kind, caller.Symbol, declaration.Modifiers.ToArray()));
        TestHarness.CheckTrue("其他模块同logical helper不能冒可信Handle调用者", BilVerifier.Verify(trusted)
            .Any(e => e.Context == caller.Symbol && e.Message.Contains("Handle 隐藏机制不能", StringComparison.Ordinal)));
    }
}
