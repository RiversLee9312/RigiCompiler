using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestProviderFactories()
    {
        var (std, _) = CompileInterfaceProbe("stdlib@1.0.0", StdlibSources.ParseAll().ToArray(), [], true);
        var folder = Path.Combine(InterfaceProbeFolder(), "factories"); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "stdlib.interface.json"), std.InterfaceBytes);
        File.WriteAllBytes(Path.Combine(folder, "stdlib.bil"), std.BilBytes);
        var providerFile = Path.Combine(folder, "provider.rg");
        File.WriteAllText(providerFile, """
            namespace factories
            priv var count: i32 = 0
            priv func next(): i32 { count += 1
                return count + 10 }
            pub func read(value: i32 = next()): i32 { return value }
            pub func generic\<T>(value: T, fallback: i32 = next()): i32 { return fallback }
            pub class Box\<T> {
                pub init() {}
                pub static func read(value: i32 = 7): i32 { return value }
            }
            pub class Host\<T> {
                pub init() {}
                pub func method\<U>(owner: T, value: U, fallback: i32 = next()): i32 { return fallback }
            }
            priv var order: i32 = 0
            priv func ordinal(): i32 { order += 1
                return order }
            pub func tick(): i32 { return ordinal() }
            pub enum struct Ordered {
                pub const prefix: i32
                pub const dynamic: i32
                pub init(_ -> prefix, _ -> dynamic)
            }[Hole(ordinal(), _)]
            pub enum struct Choice {
                pub const prefix: i32
                pub const dynamic: i32
                pub init(_ -> prefix, _ -> dynamic)
            }[Zero(next(), 2), Hole(next(), _)]
            """);
        var providerSource = File.ReadAllText(providerFile);
        var (provider, _) = CompileInterfaceProbe("factories@1.0.0", Frontend.ParseRoots([new SourceInput(providerSource, "source/provider.rg")]), [std]);
        const string orderSource = "import factories.*\npub func main(): i32 { var h = Ordered.Hole(tick())\n return (h.prefix * 10) + h.dynamic }";
        var (direct, _) = CompileInterfaceProbe("order-direct@1.0.0", Frontend.ParseRoots([new SourceInput(providerSource, "source/provider.rg"), new SourceInput(orderSource, "source/app.rg")]), [std]);
        var directRun = BilVm.Run(BilModuleLinker.Link([std.ReadBil(), direct.ReadBil()]));
        File.WriteAllBytes(Path.Combine(folder, "source-direct.bil"), direct.BilBytes);
        File.WriteAllBytes(Path.Combine(folder, "provider.interface.json"), provider.InterfaceBytes);
        File.WriteAllBytes(Path.Combine(folder, "provider.bil"), provider.BilBytes); File.Delete(providerFile);
        var (app, unit) = CompileInterfaceProbe("factory-app@1.0.0", [CompilerTestTools.ParseRoot("""
            import factories.*
            pub func main(): i32 {
                var a = read()
                var b = read()
                var g = generic\<String>("x")
                var z = Choice.Zero
                var h = Choice.Hole(dynamic = 5)
                return ((((a + b) + g) + z.prefix) + h.prefix) + h.dynamic
            }
            """, "source/app.rg")], [std, provider]);
        var generic = unit.Symbols.GetNamespace(["factories"]).Methods.Single(m => m.Name == "generic");
        CaseAssertions.CheckTrue("default factory无provider AST且GP复用owner单例", generic.Parameters[1].DefaultValue == null
            && generic.Parameters[1].HasDefaultValue && generic.Parameters[1].DefaultFactory is { } factory
            && ReferenceEquals(factory.GenericParameters.Single(), generic.GenericParameters.Single()) && factory.SourceFile == null);
        var choice = unit.Symbols.GetNamespace(["factories"]).Types.Single(t => t.Name == "Choice");
        CaseAssertions.CheckTrue("zero-hole与非zero-hole case有准确providerfactory", choice.Cases.All(c => c.SourceFile == null && c.CaseFactory != null)
            && choice.Cases.Single(c => c.Name == "Zero").CaseFactory!.Parameters.Count == 0
            && choice.Cases.Single(c => c.Name == "Hole").CaseFactory!.Parameters.Count == 2
            && choice.Cases.Single(c => c.Name == "Hole").FixedArgumentFactories is [not null, null]);
        var linked = BilModuleLinker.Link([std.ReadBil(), provider.ReadBil(), app.ReadBil()]);
        BilTestHarness.CheckBilValid("provider默认/enum工厂链接 typed BIL", linked);
        var run = BilVm.Run(linked);
        CaseAssertions.CheckTrue("源码移走后default每次求值+泛型default+enum固定洞真实VM70", !File.Exists(providerFile)
            && run.Exception == null && run.ReturnValue is VmI32 { Value: 70 }, run.Exception?.ToString() ?? run.ReturnValue?.ToString() ?? "");
        var (orderedApp, _) = CompileInterfaceProbe("order-app@1.0.0", [CompilerTestTools.ParseRoot(orderSource, "source/app.rg")], [std, provider]);
        var orderedRun = BilVm.Run(BilModuleLinker.Link([std.ReadBil(), provider.ReadBil(), orderedApp.ReadBil()]));
        File.WriteAllBytes(Path.Combine(folder, "ordered-app.bil"), orderedApp.BilBytes);
        CaseAssertions.CheckTrue("case固定/洞副作用保持source与artifact init声明序12", directRun.Exception == null && directRun.ReturnValue is VmI32 { Value: 12 }
            && orderedRun.Exception == null && orderedRun.ReturnValue is VmI32 { Value: 12 }, orderedRun.Exception?.ToString() ?? orderedRun.ReturnValue?.ToString() ?? "");
        var (staticApp, _) = CompileInterfaceProbe("static-app@1.0.0", [CompilerTestTools.ParseRoot("import factories.*\npub func main(): i32 { return Box.read() }", "source/app.rg")], [std, provider]);
        var staticBil = BilModuleLinker.Link([std.ReadBil(), provider.ReadBil(), staticApp.ReadBil()]);
        File.WriteAllText(Path.Combine(folder, "static-linked.bil"), BilWriter.Write(staticBil));
        BilTestHarness.CheckBilValid("泛型宿主static default不要求宿主typeid", staticBil);
        CaseAssertions.CheckTrue("provider源码移走后Box定义名static默认值VM7", BilVm.Run(staticBil).ReturnValue is VmI32 { Value: 7 });
        var (instanceApp, instanceUnit) = CompileInterfaceProbe("instance-app@1.0.0", [CompilerTestTools.ParseRoot("import factories.*\npub func main(): i32 { return new Host\\<String>().method\\<i32>(\"x\", 5) }", "source/app.rg")], [std, provider]);
        var method = instanceUnit.Symbols.GetNamespace(["factories"]).Types.Single(t => t.Name == "Host").Methods.Single(m => m.Name == "method");
        CaseAssertions.CheckTrue("instance default owner+method GP有序同一对象", method.Parameters[2].DefaultFactory!.GenericParameters
            .SequenceEqual(method.Owner!.GenericParameters.Concat(method.GenericParameters)));
        var instanceBil = BilModuleLinker.Link([std.ReadBil(), provider.ReadBil(), instanceApp.ReadBil()]);
        File.WriteAllText(Path.Combine(folder, "instance-linked.bil"), BilWriter.Write(instanceBil));
        BilTestHarness.CheckBilValid("instance两层GP default真实typed BIL", instanceBil);
        CaseAssertions.CheckTrue("provider移走instance Host<String>.method<i32>默认值VM11", BilVm.Run(instanceBil).ReturnValue is VmI32 { Value: 11 });
        var root = System.Text.Json.Nodes.JsonNode.Parse(provider.InterfaceBytes)!.AsObject();
        var declarations = root["payload"]!["declarations"]!.AsArray();
        var genericRecord = declarations.Single(n => n!["tag"]!.GetValue<string>() == "method" && n["name"]!.GetValue<string>() == "generic")!;
        var factoryId = genericRecord["parameters"]![1]!["factory"]!.GetValue<string>();
        declarations.Single(n => n!["id"]!.GetValue<string>() == factoryId)!["gps"]![0]!["identity"]!["ownerId"] = factoryId;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root["payload"]!.ToJsonString())));
        root["apiHash"] = hash;
        var tampered = provider with { InterfaceBytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()), ApiHash = hash };
        var graph = SymbolGraph.CreateArtifactOnly("bad-factory@1.0.0"); Modules.ModuleInterfaceImporter.Import(graph, std);
        CaseAssertions.CheckTrue("defaultfactory自声明同名T而非alias原owner拒绝", Reject(() => Modules.ModuleInterfaceImporter.Import(graph, tampered)));
        foreach (var change in new[] { "type", "name", "index" })
        {
            var invalid = System.Text.Json.Nodes.JsonNode.Parse(provider.InterfaceBytes)!.AsObject();
            var records = invalid["payload"]!["declarations"]!.AsArray();
            var choiceRecord = records.Single(n => n!["tag"]!.GetValue<string>() == "type" && n["name"]!.GetValue<string>() == "Choice")!;
            var holeRecord = records.Single(n => n!["tag"]!.GetValue<string>() == "case" && n["name"]!.GetValue<string>() == "Hole"
                && n["owner"]!.GetValue<string>() == choiceRecord["id"]!.GetValue<string>())!["holes"]![0]!;
            if (change == "type") holeRecord["type"] = new System.Text.Json.Nodes.JsonObject {
                ["typeId"] = records.Single(n => n!["tag"]!.GetValue<string>() == "type" && n["builtin"]!.GetValue<bool>()
                    && n["name"]!.GetValue<string>() == "bool")!["id"]!.GetValue<string>(), ["args"] = new System.Text.Json.Nodes.JsonArray() };
            else if (change == "name") holeRecord["name"] = "bogus";
            else holeRecord["index"] = 99;
            var invalidHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(invalid["payload"]!.ToJsonString())));
            invalid["apiHash"] = invalidHash;
            var invalidArtifact = provider with { InterfaceBytes = System.Text.Encoding.UTF8.GetBytes(invalid.ToJsonString()), ApiHash = invalidHash };
            var isolated = SymbolGraph.CreateArtifactOnly("bad-hole@1.0.0"); Modules.ModuleInterfaceImporter.Import(isolated, std);
            CaseAssertions.CheckTrue("enum hole ABI 篡改拒绝 " + change, Reject(() => Modules.ModuleInterfaceImporter.Import(isolated, invalidArtifact)));
        }
    }
}
