using System.Security.Cryptography;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Modules;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private const string LateProviderSource = """
        namespace late
        import core.serialization.*
        import core.coroutine.*
        pub async func pending(): i32 { yield sleep(10)
            return 43 }
        pub func opaqueCold(body: core.AsyncFunc\<i32>): Task\<i32> { return new Task\<i32>(body) }
        priv var entered: i32 = 0
        pub func enteredCount(): i32 { return entered }
        pub async func contend(mutex: Mutex): i32 {
            const lock = await mutex.acquire()
            entered = entered + 1
            mutex.release(lock)
            return 47
        }
        @WrapperTarget(.Entity)
        pub wrapper EchoProxy\<TTarget> {
            pub var calls: i32 = 0
            pub init()
            operator .proxy.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(
                symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...
            ): TReturn {
                calls = calls + 1
                return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)
            }
        }
        @EchoProxy
        pub class EchoHost { pub func echo\<T>(value: T): T { return value } }
        pub class Forwarder\<T> {
            pub init()
            pub func accept\<U with EchoProxy\<T>>(value: U): i32 { return 17 }
            pub func forward\<U with EchoProxy\<T>>(value: U): i32 { return this.accept(value) }
        }
        @WrapperTarget(.Entity)
        pub wrapper Boost {
            pub var seed: i32 = 0
            pub init(value: i32) { seed = value }
            operator .proxy.read(): i32 { return seed + inner() }
        }
        @Boost(9)
        pub class Wrapped { pub func read(): i32 { return 1 } }
        @Serializable
        pub class Decorated {
            pub var stored: i32 { pub get(value: _) { return value }
                pub set(value: _) {} } = 17
            pub var computed: i32 { pub get(_: _) { return stored + 1 } }
            @Temporary(resume=(func{(): i32 -> 42} as core.Func\<i32>))
            pub var cache: i32 = 0
        }
        @Serializable
        pub shared open class Base {
            pub var id: i32
            pub init(_ -> id)
        }
        @Serializable
        pub shared class Item : Base {
            pub var value: i32
            pub init(id: i32, _ -> value) { super(id) }
        }
        @Serializable
        pub shared class Envelope\<T with Serializable> {
            pub var item: T?
            pub init(_ -> item)
        }
        @Serializable
        priv shared class Hidden : Base {
            pub var value: i32
            pub init(_ -> value) { super(31) }
        }
        pub func hiddenParcel(): Parcel { return new Hidden(31):Serializable.toParcel() }
        @Serializable
        pub enum struct Marker {} [Cold -> 3, Hot -> 91]
        pub enum struct Shape {
            pub var area: double
            pub init(_ -> area)
        }[Circle(_), Square(_)]
        """;

    private const string LateApplicationSource = """
        import late.*
        import core.serialization.*
        import core.collections.*
        import core.io.*
        import core.text.*
        import core.serialization.json.*
        import core.coroutine.*
        @EchoProxy
        pub class ConsumerHost { pub func echo\<T>(value: T): T { return value } }
        pub class BadMessage : core.RuntimeException {
            pub init() { super("module-fallback") }
            pub override func getMessage(): String { throw new core.RuntimeException("getter-failed") }
        }
        pub func unhandled(): i32 { throw new BadMessage() }
        @Boost(5)
        pub class LocalWrapped { pub func read(): i32 { return 2 } }
        @Serializable
        pub class Local {
            pub var n: i32
            pub init(_ -> n)
        }
        pub func main(): i32 {
            const pendingTask = pending()
            if ((await pendingTask) != 43) { return 25 }
            const cold = opaqueCold(func{async (): i32 -> 49})
            cold.run()
            if ((await cold) != 49) { return 26 }
            const mutex = new Mutex()
            const lock = await mutex.acquire()
            const blocked = contend(mutex)
            yield
            yield
            if (enteredCount() != 0) { return 27 }
            mutex.release(lock)
            if (((await blocked) != 47) or (enteredCount() != 1)) { return 28 }
            const providerEcho = new EchoHost()
            if (providerEcho.echo\<i32>(37) != 37) { return 29 }
            if (providerEcho:EchoProxy.calls != 1) { return 32 }
            if ((new Forwarder\<EchoHost>()).forward(new EchoHost()) != 17) { return 30 }
            const consumerEcho = new ConsumerHost()
            if (consumerEcho.echo\<i32>(29) != 29) { return 31 }
            if (consumerEcho:EchoProxy.calls != 1) { return 33 }
            const source = new Envelope\<Item>(new Item(7, 3))
            const copy = deepCopy(source)
            if ((copy.item as Item).value != 3) { return 1 }
            if ((copy.item as Item).id != 7) { return 2 }
            if (fieldsOf\<Item>().length != 2) { return 3 }
            const envelopeFields = fieldsOf(typeOf(source))
            if (envelopeFields.length != 1) { return 4 }
            const itemField = envelopeFields[0] as FieldInfo
            if (itemField.typeName != "late::Item") { return 5 }
            if (not itemField.nullable) { return 6 }
            if (deepCopy(new Local(19)).n != 19) { return 7 }
            if (fieldsOf\<Local>().length != 1) { return 8 }
            if (not (deepCopy(Marker.Hot) is .Hot)) { return 9 }
            const cases = casesOf\<Shape>()
            if (cases.length != 2) { return 10 }
            const circle = cases[0] as EnumCaseInfo
            if (circle.name != "Circle") { return 11 }
            if (circle.fields.length != 1) { return 12 }
            if (not isSerializable("late::Item")) { return 13 }
            if (isSerializable("Unknown")) { return 14 }
            var rejected = false
            try { const bad = fieldsOf("Unknown") }
            catch (e: core.IllegalArgumentException) { rejected = true }
            if (not rejected) { return 15 }
            const hidden = hiddenParcel()
            const restored = fromParcel\<Base>(hidden)
            if (typeNameOf(typeOf(restored)) != hidden.typeName) { return 16 }
            if ((new Wrapped()).read() != 10) { return 17 }
            if ((new LocalWrapped()).read() != 7) { return 18 }
            const decorated = deepCopy(new Decorated())
            if ((decorated.stored != 17) or (decorated.computed != 18)) { return 19 }
            if (decorated.cache != 42) { return 20 }
            if (fieldsOf\<Decorated>().length != 1) { return 21 }
            const items = new List\<Item>()
            items.add(new Item(3, 8))
            if ((deepCopy(items).getAtIndex(0L) as Item).value != 8) { return 22 }
            const lookup = new Map\<String, Item>()
            lookup.set("a", new Item(2, 11))
            if ((deepCopy(lookup).tryGet("a") as Item).value != 11) { return 23 }
            const stream = new MemoryInputStream((new Utf8Encoder()).encode("[{\"id\":4,\"value\":13}]"))
            try {
                const decoded = (new JsonSerializer()).readAs\<Array\<Item>>(stream)
                if ((decoded[0] as Item).value != 13) { return 24 }
            } finally(e) { stream.dispose() }
            return 0
        }
        """;

    private static void TestLateApplication()
    {
        var folder = InterfaceProbeFolder(); Directory.CreateDirectory(folder);
        var (std, _) = CompileInterfaceProbe("stdlib@1.0.0", StdlibSources.ParseAll().ToArray(), [], true);
        File.WriteAllBytes(Path.Combine(folder, "stdlib.interface.json"), std.InterfaceBytes);
        File.WriteAllBytes(Path.Combine(folder, "stdlib.bil"), std.BilBytes);
        var providerPath = Path.Combine(folder, "provider.rg");

        File.WriteAllText(providerPath, LateProviderSource);
        var (provider, _) = CompileInterfaceProbe("late-provider@1.0.0",
            Frontend.ParseRoots([new SourceInput(File.ReadAllText(providerPath), "source/provider.rg")]), [std]);
        File.WriteAllBytes(Path.Combine(folder, "provider.interface.json"), provider.InterfaceBytes);
        File.WriteAllBytes(Path.Combine(folder, "provider.bil"), provider.BilBytes);
        File.Delete(providerPath);

        File.WriteAllText(Path.Combine(folder, "app.rg"), LateApplicationSource);
        var before = SHA256.HashData(std.BilBytes);
        var (app, unit) = CompileInterfaceProbe("late-app@1.0.0", [CompilerTestTools.ParseRoot(LateApplicationSource, "source/app.rg")], [std, provider], finalApplication: true);
        File.WriteAllBytes(Path.Combine(folder, "app.bil"), app.BilBytes);
        File.WriteAllBytes(Path.Combine(folder, "app.interface.json"), app.InterfaceBytes);
        var linked = ModuleApplicationLinker.Link([std, provider], app.ReadBil(), unit.Symbols);
        File.WriteAllText(Path.Combine(folder, "linked.bil"), BilWriter.Write(linked));
        CaseAssertions.CheckTrue("final只审批20个可信std late helper且无provider AST", unit.Symbols.ApprovedLateHelpers.Count == 20
            && unit.Symbols.LateHelperOverrides.Count == 20 && unit.SourceFiles.Count == 1 && !File.Exists(providerPath));
        CaseAssertions.CheckTrue("late替换不修改cached std BIL", before.SequenceEqual(SHA256.HashData(std.BilBytes)));
        CaseAssertions.CheckTrue("普通source lookup没有private Serializable host", !unit.Symbols.GetNamespace(["late"]).Types.Any(t => t.Name == "Hidden")
            && unit.Symbols.ImportedUserHosts.Any(t => t.Name == "Hidden"));
        var importedWrapped = unit.Symbols.GetNamespace(["late"]).Types.Single(t => t.Name == "Wrapped");
        CaseAssertions.CheckTrue("imported wrapper应用无AST且provider安装器保留", importedWrapped.AppliedWrappers.Single().Syntax == null
            && importedWrapped.Methods.Any(m => m.Name == BilSpellings.InitWrapperMethodName));
        BilTestHarness.CheckBilValid("artifact-only用户codec与final late body typed BIL", linked);
        var machine = new BilVm(linked);
        var run = machine.Run();
        CaseAssertions.CheckTrue("artifact-only Std真实Dispatcher运行而非降级单线程通道", machine.LastContext!.Dispatch.HasDispatcher);
        CaseAssertions.CheckTrue("provider移走后泛型/继承/私有host/本地codec与反射真实VM", run.Exception == null && run.ReturnValue is VmI32 { Value: 0 },
            run.Exception?.ToString() ?? run.ReturnValue?.ToString() ?? "无返回值");
        var languageException = machine.LastContext!.LanguageException("core::RuntimeException", "module-message");
        CaseAssertions.CheckTrue("VM内置异常准确写入Std protected消息字段", languageException.ExceptionObject is IVmFieldHost exceptionHost
            && exceptionHost.TryReadField(machine.LastContext.RuntimeField("core::Exception#message@.string"), out var message)
            && message is VmString { Value: "module-message" });
        var failureModule = BilReader.Read(BilWriter.Write(linked));
        var failureIndex = failureModule.LocalSymbols.FindIndex(s => s is BilSimpleMemberDeclaration m
            && m.Symbol == "$unhandled()@.i32");
        var failureDeclaration = (BilSimpleMemberDeclaration)failureModule.LocalSymbols[failureIndex];
        failureModule.LocalSymbols[failureIndex] = new BilSimpleMemberDeclaration(failureDeclaration.Kind,
            failureDeclaration.Symbol, failureDeclaration.Modifiers.Concat([new BilKeywordModifier(BilKeyword.Entrypoint)]).ToArray());
        File.WriteAllText(Path.Combine(folder, "unhandled.bil"), BilWriter.Write(failureModule));
        var failure = new BilVm(failureModule).Run(entryPoint: "$unhandled()@.i32");
        CaseAssertions.CheckTrue("uncaught getMessage失败时保留真实protected字段消息", failure.Exception?.Message.Contains("module-fallback", StringComparison.Ordinal) == true,
            failure.Exception?.ToString() ?? "无异常");
    }
}
