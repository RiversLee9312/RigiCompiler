using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static void TestOriginReserved()
    {
        const string wrapped = "namespace same\n@WrapperTarget(.Method)\nshared wrapper W { pub var marker: i64 = 7L }\n@W\npub func call() {}\n";
        var (_, one) = EmitIndependentProbe("wrapper-a@1.0.0", wrapped);
        var (_, two) = EmitIndependentProbe("wrapper-b@1.0.0", wrapped.Replace("func call()", "func other()"));
        var linked = BilModuleLinker.Link([one, two]);
        BilTestHarness.CheckBilValid("module wrapped globals宿主隔离且BIL合法", linked);
        var hosts = linked.LocalSymbols.OfType<BilTypeDeclaration>()
            .Where(t => t.Symbol.Contains("..globals.host__m_", StringComparison.Ordinal)).ToArray();
        CaseAssertions.CheckTrue("两个module各有独立wrapper singleton宿主", hosts.Length == 2
            && hosts[0].Symbol != hosts[1].Symbol);
        var context = new RigiCompiler.Middleware.MwContext(linked);
        new RigiCompiler.Middleware.Pipeline.MwPipeline()
            .Add(new RigiCompiler.Middleware.Pipeline.LayoutStage())
            .Add(new RigiCompiler.Middleware.Pipeline.MirBuildStage()).Run(context);
        CaseAssertions.CheckTrue("无调用的singleton wrapper初值仍进真实MIR可达闭包", hosts.All(h =>
            context.Mir!.Functions.Any(f => f.Symbol.Canonical.StartsWith(h.Symbol + "$", StringComparison.Ordinal)
                && BilLogicalName.Method(f.Symbol.Canonical) == BilSpellings.InitWrapperMethodName)));
        var (_, enumBil) = EmitIndependentProbe("enum@1.0.0", "enum struct Color {}[Red]\nclass Host { priv var value: Color = .Red\n pub init() }\n");
        BilTestHarness.CheckBilValid("module private enum字段声明初值匹配逻辑名字", enumBil);
        var unit = new CompilationUnit("stdlib-probe@1.0.0", true, StdlibSources.ParseIntrinsics());
        var declarations = DeclarationCollector.Collect(unit);
        DeclarationResolver.Resolve(unit, declarations);
        var helper = unit.Symbols.GetNamespace(["core"]).Methods.Single(m => m.Name == "any_hash");
        var helperBil = new BilModule();
        helperBil.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, CanonicalSymbolPrinter.PrintMethod(helper),
            [new BilKeywordModifier(BilKeyword.Native), new BilNativeSymbolModifier("any_hash"), new BilNativeLibraryModifier("rigi_rt")]));
        helperBil.Metadata.Add(new BilMetadataEntry(BilCompilerHelpers.MetadataPrefix + "any_hash", BilScalarType.String,
            "\"" + CanonicalSymbolPrinter.PrintMethod(helper) + "\""));
        CaseAssertions.CheckTrue("可信intrinsics helper记录准确module私有canonical", BilCompilerHelpers.Resolve(helperBil, "any_hash")
            == CanonicalSymbolPrinter.PrintMethod(helper) && helper.OriginModuleId == unit.ModuleIdentity);
        helperBil.Metadata.Clear();
        CaseAssertions.CheckTrue("用户同逻辑名不获helper重定向权限", BilCompilerHelpers.Resolve(helperBil, "any_hash") == null);
        var proxyMembers = new List<RigiCompiler.Middleware.Symbols.MwMemberSymbol>();
        var proxyOwner = new RigiCompiler.Middleware.Symbols.MwTypeSymbol(
            new BilTypeDeclaration("same::W", BilTypeKind.Wrapper), false, proxyMembers, []);
        var suffix = "__m_" + new string('a', 64);
        var valueProxy = new RigiCompiler.Middleware.Symbols.MwMemberSymbol(new BilSimpleMemberDeclaration(
            BilMemberKind.Method, "same::W$$.proxy.get" + suffix + "(value:.i32)@.i32",
            [new BilWrapperProxyModifier(BilProxyKind.Specific)]), proxyOwner, false);
        var methodProxy = new RigiCompiler.Middleware.Symbols.MwMemberSymbol(new BilSimpleMemberDeclaration(
            BilMemberKind.Method, "same::W$$.proxy.read" + suffix + "()@.i32",
            [new BilWrapperProxyModifier(BilProxyKind.Specific)]), proxyOwner, false);
        proxyMembers.AddRange([valueProxy, methodProxy]);
        var privateMethod = new RigiCompiler.Middleware.Symbols.MwMemberSymbol(new BilSimpleMemberDeclaration(
            BilMemberKind.Method, "same::Host$read__m_" + new string('b', 64) + "()@.i32"), null, false);
        CaseAssertions.CheckTrue("私有Value代理保实际canonical并在wrapper宿主内识别", ReferenceEquals(valueProxy,
            RigiCompiler.Middleware.Binding.ProxyMatcher.FindValueProxy(proxyOwner, false)));
        CaseAssertions.CheckTrue("跨模块private方法与specific代理按源名匹配", ReferenceEquals(methodProxy,
            RigiCompiler.Middleware.Binding.ProxyMatcher.FindSpecificMethodProxy(proxyOwner, privateMethod)));
        var runtime = new BilModule();
        var runtimeCanonical = "core.coroutine::$rigi_sync_mutex_acquire" + suffix + "(mutex:.i64)@.void";
        runtime.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, runtimeCanonical,
            [new BilKeywordModifier(BilKeyword.Native), new BilNativeSymbolModifier("rigi_sync_mutex_acquire"),
                new BilNativeLibraryModifier("rigi_rt")]));
        CaseAssertions.CheckTrue("普通同名private原语不获runtime前缀授权", new RigiCompiler.Middleware.MwContext(runtime)
            .CompilerMember("core.coroutine::$rigi_sync_mutex_acquire(") == null);
        BilCompilerSymbols.Register(runtime, runtimeCanonical);
        CaseAssertions.CheckTrue("可信runtime前缀经准确metadata找实际声明", new RigiCompiler.Middleware.MwContext(runtime)
            .CompilerMember("core.coroutine::$rigi_sync_mutex_acquire(")?.Canonical == runtimeCanonical);
        var queueField = "core.coroutine::Dispatcher" + suffix + "#mainQueue" + suffix
            + "@core.coroutine::CoroutineCarriageQueue" + suffix;
        BilCompilerSymbols.Register(runtime, queueField);
        CaseAssertions.CheckTrue("runtime字段宿主成员及私有槽类型保持准确投影", BilCompilerSymbols.ResolveField(runtime,
            "core.coroutine::Dispatcher#mainQueue@core.coroutine::CoroutineCarriageQueue") == queueField);
        var hookContext = new VmContext(runtime);
        CaseAssertions.CheckTrue("普通同logical私有Mutex.enter不获VM机制hook", !hookContext.Hooks.TryInvokeMethod(hookContext,
            "core.coroutine::Mutex$enter" + suffix + "()@.void", [], out _));
        var mutexEnter = new BilSimpleMemberDeclaration(BilMemberKind.Method,
            "core.coroutine::Mutex$enter" + suffix + "()@.void");
        runtime.LocalSymbols.Add(mutexEnter);
        var mutexContext = new RigiCompiler.Middleware.MwContext(runtime);
        var enterCall = new RigiCompiler.Middleware.Mir.MirCall(
            mutexContext.Symbols.FindMember(mutexEnter.Symbol)!, [], null);
        CaseAssertions.CheckTrue("普通private Mutex.enter不成为Native挂起原语",
            !RigiCompiler.Middleware.Passes.CoroutineSplitPass.IsMutexEnter(mutexContext, enterCall));
        BilCompilerSymbols.Register(runtime, mutexEnter.Symbol);
        mutexContext = new RigiCompiler.Middleware.MwContext(runtime);
        CaseAssertions.CheckTrue("可信private Mutex.enter准确识别Native挂起点",
            RigiCompiler.Middleware.Passes.CoroutineSplitPass.IsMutexEnter(mutexContext, enterCall));
    }
    private static void TestManifestBootstrap()
    {
        var artifact = SymbolGraph.CreateArtifactOnly("app@1.0.0");
        CaseAssertions.CheckTrue("artifact-only factory零源码入口", artifact.Bootstrap.DeclarationSource == null
            && artifact.Bootstrap.Any.Methods.Count == 0 && !artifact.Bootstrap.SourceTypes.ContainsKey("Pair"));
        var legacy = new SymbolGraph();
        foreach (var (name, type) in artifact.Bootstrap.SourceTypes)
        {
            var expected = legacy.Bootstrap.SourceTypes[name];
            CaseAssertions.CheckTrue("固定 manifest ABI " + name, type.Kind == expected.Kind
                && type.BilAlias == expected.BilAlias && type.BilStandardConstructor == expected.BilStandardConstructor
                && type.IsRich == expected.IsRich && type.IsShared == expected.IsShared
                && type.IsValueTypeBranch == expected.IsValueTypeBranch && type.IntrinsicOps.SetEquals(expected.IntrinsicOps)
                && type.GenericParameters.Select(p => p.Name).SequenceEqual(expected.GenericParameters.Select(p => p.Name))
                && type.StableIdentity == expected.StableIdentity);
        }
    }
    private static (CompilationUnit Unit, BilModule Bil) EmitIndependentProbe(string id, string source)
    {
        var unit = new CompilationUnit(id, true, CompilerTestTools.ParseRoot(source, "source/probe.rg"));
        var declarations = DeclarationCollector.Collect(unit);
        DeclarationResolver.Resolve(unit, declarations);
        var bound = Binder.Bind(unit, declarations);
        var lowered = Lowerer.Lower(unit, bound);
        var module = BilEmitter.Emit(unit, lowered, id);
        CaseAssertions.CheckTrue(id + "独立语义无错误", !unit.Diagnostics.HasErrors,
            string.Join(';', unit.Diagnostics.Diagnostics.Select(d => d.Message)));
        return (unit, module);
    }
    private static void TestOriginCompilation()
    {
        var (a, first) = EmitIndependentProbe("a@1.0.0", "namespace same\npriv var value: i32 = 11\nfunc helper(): i32 { return value }\n@EntryPoint\npub func run(): i32 { return helper() }\n");
        var (b, second) = EmitIndependentProbe("b@1.0.0", "namespace same\npriv var value: i32 = 22\nfunc helper(): i32 { return value }\npub func other(): i32 { return helper() }\n");
        var aHelper = a.Symbols.GetNamespace(["same"]).Methods.Single(m => m.Name == "helper");
        var bHelper = b.Symbols.GetNamespace(["same"]).Methods.Single(m => m.Name == "helper");
        CaseAssertions.CheckTrue("源名与同namespace保持，private canonical隔离", aHelper.Name == bHelper.Name
            && aHelper.Namespace?.FullName == bHelper.Namespace?.FullName
            && CanonicalSymbolPrinter.Print(aHelper) != CanonicalSymbolPrinter.Print(bHelper));
        CaseAssertions.CheckTrue("central logicalName还原private helper", BilLogicalName.Method(CanonicalSymbolPrinter.Print(aHelper)) == "helper");
        var linked = BilModuleLinker.Link([second, first]);
        BilTestHarness.CheckBilValid("两个module同namespace私有函数与初始化链接验证", linked);
        CaseAssertions.CheckTrue("module init DAG顺序", BilModuleInitialization.Order(linked)
            .SequenceEqual(BilModuleInitialization.Order(second).Concat(BilModuleInitialization.Order(first))));
        var vm = new BilVm(linked).Run();
        CaseAssertions.CheckTrue("两个独立module初始化及真正VM入口", vm.Exception == null && vm.ReturnValue is VmI32 { Value: 11 }, vm.Exception?.ToString() ?? "");
        var internalMember = new MethodSymbol("member", MethodKind.Regular)
            { Accessibility = Accessibility.Internal, OriginModuleId = a.ModuleIdentity };
        CaseAssertions.CheckTrue("internal同Module可见", AccessChecker.IsAccessible(internalMember, a.SourceFiles[0], null, null));
        CaseAssertions.CheckTrue("internal跨Module拒绝", !AccessChecker.IsAccessible(internalMember, b.SourceFiles[0], null, null));
        aHelper.IsImported = true; aHelper.SourceFile = null;
        CaseAssertions.CheckTrue("无AST private imported不授权同relative路径", !AccessChecker.IsAccessible(aHelper, b.SourceFiles[0], bHelper.Namespace, null));
        var protectedMember = new MethodSymbol("member", MethodKind.Regular, ns: aHelper.Namespace)
            { Accessibility = Accessibility.Protected, OriginModuleId = a.ModuleIdentity };
        CaseAssertions.CheckTrue("protected同包跨module依既有规则可见", AccessChecker.IsAccessible(protectedMember, b.SourceFiles[0], aHelper.Namespace, null));
        var privateType = new TypeSymbol("Hidden", TypeKind.Class, aHelper.Namespace) { OriginModuleId = a.ModuleIdentity };
        privateType.GenericParameters.Add(new GenericParameterSymbol("T"));
        var closed = a.Symbols.GetConstructedType(privateType, a.Symbols.Bootstrap.Int32);
        CaseAssertions.CheckTrue("constructed保来源与private owner投影", closed.OriginModuleId == a.ModuleIdentity
            && CanonicalSymbolPrinter.PrintType(closed).Contains("Hidden__m_", StringComparison.Ordinal));
    }
}
