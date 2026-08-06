using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    // BilEmitter（P4b）wrapper 标记产物端到端（M88）：
    // - place 成员访问：Entity 读 = get.wrapper 值拷贝 + get.field；
    //   写 = set.field.embedded wrapper(W)；字段-Value = embedded 链；
    // - 应用标记 §8.3.1 wrapped(W)（无隐藏字段声明）；
    // - proxy 模板 fn：wrapper-proxy(specific|wildcard) + get.self/call.inner；
    // - 降级调用点：invoke core::Any$call??? + symbol 资源 + 双包。
    public static partial class BilEmitterTests
    {
        // ===== Entity 字段读 + wrapped 应用标记（§8.3.1）=====
        private static void TestWrapperEntityReadEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): String {\n" +
                "    return s:Logged.level\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（Entity 字段读）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（Entity 字段读）", module);

            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.CheckTrue("Service 带 wrapped(Logged) 应用标记",
                service.Modifiers.OfType<BilWrappedModifier>()
                    .Any(m => m.WrapperTypeRef == "Logged"));
            TestHarness.CheckTrue("无 .wrapper. 隐藏字段声明",
                !service.Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(d => d.Symbol.Contains("#.wrapper.")));

            BilTestHarness.CheckFnShape("Entity 字段读（get.wrapper + get.field）",
                module, "$f(s:Service)@.string",
                ".vars { Logged .s0, Logged .t0, .string .t1 }\n" +
                "get.wrapper $s type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Logged#level@.string)\n" +
                "ret $.t1\n");
        }

        // ===== Entity 字段写（set.field.embedded wrapper(W)）=====
        private static void TestWrapperEntityWriteEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.level = \"TRACE\"\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（Entity 字段写）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（Entity 字段写）", module);
            BilTestHarness.CheckFnShape("Entity 字段写（set.field.embedded wrapper）",
                module, "$f(s:Service)@.void",
                ".vars { .string .t0 }\n" +
                "load res(#0) $.t0\n" +
                "set.field.embedded $.t0 $s wrapper(Logged) " +
                "field(Logged#level@.string)\n" +
                "ret\n");
        }

        // ===== Entity 方法调用（值拷贝 receiver）=====
        private static void TestWrapperEntityCallEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func dump(): String { return level }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): String {\n" +
                "    return s:Logged.dump()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（Entity 方法调用）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（Entity 方法调用）", module);
            BilTestHarness.CheckFnShape("Entity 方法调用（get.wrapper + invoke）",
                module, "$f(s:Service)@.string",
                ".vars { Logged .s0, Logged .t0, .string .t1 }\n" +
                "get.wrapper $s type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "invoke fn(Logged$dump()@.string) $.t1 [$.s0]\n" +
                "ret $.t1\n");
        }

        // ===== 嵌套链：get.wrapper 逐级 + wrapped 标记 =====
        private static void TestWrapperNestedChainEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Inner {\n" +
                "    pub var tag: String\n" +
                "    pub init(_ -> tag)\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "@Inner\n" +
                "pub wrapper Outer {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Outer\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): String {\n" +
                "    return s:Outer:Inner.tag\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（嵌套链）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（嵌套链）", module);
            BilTestHarness.CheckFnShape("嵌套链读（get.wrapper 逐级 + get.field）",
                module, "$f(s:Service)@.string",
                ".vars { Outer .s0, Inner .s1, Outer .t0, Inner .t1, .string .t2 }\n" +
                "get.wrapper $s type(Outer) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.wrapper $.s0 type(Inner) $.t1\n" +
                "set.var $.t1 $.s1\n" +
                "get.field $.s1 $.t2 field(Inner#tag@.string)\n" +
                "ret $.t2\n");
            var outer = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Outer");
            TestHarness.CheckTrue("Outer 带 wrapped(Inner)",
                outer.Modifiers.OfType<BilWrappedModifier>()
                    .Any(m => m.WrapperTypeRef == "Inner"));
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.CheckTrue("Service 带 wrapped(Outer)",
                service.Modifiers.OfType<BilWrappedModifier>()
                    .Any(m => m.WrapperTypeRef == "Outer"));
        }

        // ===== Entity 索引读（值拷贝 + get.array）=====
        private static void TestWrapperIndexReadEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Indexed {\n" +
                "    pub var store: i32\n" +
                "    pub init(_ -> store)\n" +
                "    pub operator getAtIndex(index: i32): i32 { return store }\n" +
                "}\n" +
                "@Indexed\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): i32 {\n" +
                "    return s:Indexed[0]\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（Entity 索引读）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（Entity 索引读）", module);
            BilTestHarness.CheckFnShape("Entity 索引读（get.wrapper + get.array）",
                module, "$f(s:Service)@.i32",
                ".vars { Indexed .s0, Indexed .t0, .i32 .t1, .i32 .t2 }\n" +
                "get.wrapper $s type(Indexed) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "load res(#0) $.t1\n" +
                "get.array $.s0 $.t1 $.t2\n" +
                "ret $.t2\n");
        }

        // ===== 字段-Value：embedded wrapper(W) + 字段 wrapped 标记 =====
        private static void TestWrapperFieldValueEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32\n" +
                "    pub init(h: i32) { hp = h }\n" +
                "}\n" +
                "pub func f(hero: Hero): i32 {\n" +
                "    hero.hp:Clamped.min = 10\n" +
                "    return hero.hp:Clamped.min\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（字段-Value）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（字段-Value）", module);
            var hero = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Hero");
            var hp = hero.Members.OfType<BilSimpleMemberDeclaration>()
                .Single(d => d.Symbol == "Hero#hp@.i32");
            TestHarness.CheckTrue("字段-Value 应用标记 wrapped(Clamped)",
                hp.Modifiers.OfType<BilWrappedModifier>()
                    .Any(m => m.WrapperTypeRef == "Clamped"));
            TestHarness.CheckTrue("无 .wrapper. 隐藏字段",
                !hero.Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(d => d.Symbol.Contains("#.wrapper.")));
            BilTestHarness.CheckFnShape("字段-Value 读写（get/set.field.embedded wrapper）",
                module, "$f(hero:Hero)@.i32",
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "load res(#0) $.t0\n" +
                "set.field.embedded $.t0 $hero wrapper(Clamped) " +
                "field(Clamped#min@.i32)\n" +
                "get.field.embedded $hero $.t1 wrapper(Clamped) " +
                "field(Clamped#min@.i32)\n" +
                "ret $.t1\n");
        }

        // ===== Entity 复合赋值（读/写分离 + 宿主单次求值）=====
        private static void TestWrapperCompoundAssignmentEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counted {\n" +
                "    pub var count: i32\n" +
                "    pub init(_ -> count)\n" +
                "}\n" +
                "@Counted\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Counted.count += 1\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（复合赋值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（复合赋值）", module);
            BilTestHarness.CheckFnShape("复合赋值（读 = 值拷贝，写 = embedded wrapper）",
                module, "$f(s:Service)@.void",
                ".vars { Counted .s0, .i32 .s1, Counted .t0, .i32 .t1, .i32 .t2, " +
                ".i32 .t3 }\n" +
                "get.wrapper $s type(Counted) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Counted#count@.i32)\n" +
                "load res(#0) $.t2\n" +
                "add $.t1 $.t2 $.t3\n" +
                "set.var $.t3 $.s1\n" +
                "set.field.embedded $.s1 $s wrapper(Counted) " +
                "field(Counted#count@.i32)\n" +
                "ret\n");
        }

        // ===== 归口负例 =====
        private static void TestWrapperPlaceEmissionGates()
        {
            var (unit, _, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            TestHarness.CheckSemanticError("局部 wrapper place 归口", unit.Diagnostics,
                "local/static wrapper place storage is not supported yet");

            var (unit2, _, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = 0\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    return Holder.counter:SClamp.min\n" +
                "}\n");
            TestHarness.CheckSemanticError("静态字段 wrapper place 归口", unit2.Diagnostics,
                "local/static wrapper place storage is not supported yet");

            var (unit3, _, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "    pub func clamp(v: i32): i32 { return v }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32\n" +
                "    pub init(h: i32) { hp = h }\n" +
                "}\n" +
                "pub func f(hero: Hero): i32 {\n" +
                "    return hero.hp:Clamped.clamp(1)\n" +
                "}\n");
            TestHarness.CheckSemanticError("字段-Value 方法调用归口", unit3.Diagnostics,
                "cannot be materialized as a value");

            var (unit4, _, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Indexed {\n" +
                "    pub var store: i32\n" +
                "    pub init(_ -> store)\n" +
                "    pub operator getAtIndex(index: i32): i32 { return store }\n" +
                "    pub operator setAtIndex(index: i32, value: i32) { store = value }\n" +
                "}\n" +
                "@Indexed\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Indexed[0] = 1\n" +
                "}\n");
            TestHarness.CheckSemanticError("wrapper place 索引写归口", unit4.Diagnostics,
                "writes through wrapper place member or index chains");

            var (unit5, _, _) = BilTestHarness.EmitBilUnit(
                "pub class Inner {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var sub: Inner\n" +
                "    pub init(_ -> sub)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.sub.x = 1\n" +
                "}\n");
            TestHarness.CheckSemanticError("深层写穿归口", unit5.Diagnostics,
                "writes through wrapper place member or index chains");
        }

        // ===== M79：param:W（泛型参数 with 约束）get.wrapper / embedded 端到端 =====
        private static void TestGenericParamWithWrapperEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "pub func f\\<T with Logged>(param: T): String {\n" +
                "    return param:Logged.level\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（param:W 读）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（param:W 读）", module);
            BilTestHarness.CheckFnShape("param:W 字段读（get.wrapper + get.field）",
                module, "$f(param:.generic<$.generic.T>)@.string",
                ".vars { Logged .s0, Logged .t0, .string .t1 }\n" +
                "get.wrapper $param type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Logged#level@.string)\n" +
                "ret $.t1\n");

            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "pub func g\\<T with Logged>(param: T) {\n" +
                "    param:Logged.level = \"TRACE\"\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（param:W 写）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（param:W 写）", module2);
            BilTestHarness.CheckFnShape("param:W 字段写（set.field.embedded wrapper）",
                module2, "$g(param:.generic<$.generic.T>)@.void",
                ".vars { .string .t0 }\n" +
                "load res(#0) $.t0\n" +
                "set.field.embedded $.t0 $param wrapper(Logged) " +
                "field(Logged#level@.string)\n" +
                "ret\n");
        }

        // ===== M88：specific proxy 模板 fn（wrapper-proxy + get.self/call.inner）=====
        private static void TestProxyBakingEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): String {\n" +
                "        var host = self\n" +
                "        return inner(arg)\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n" +
                "pub func caller(s: Service): String { return s.doSomething(1) }\n");
            CheckNoErrors("全管线无诊断（specific 模板）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（specific 模板）", module);

            var logged = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Logged");
            TestHarness.Check("specific proxy 模板声明",
                "Method|Logged$$.proxy.doSomething(arg:.i32)@.string|" +
                "priv,operator(.proxy.doSomething),wrapper-proxy(specific)",
                RenderMember(logged, "Logged$$.proxy.doSomething(arg:.i32)@.string"));
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.CheckTrue("Service wrapped(Logged<Service>)",
                service.Modifiers.OfType<BilWrappedModifier>()
                    .Any(m => m.WrapperTypeRef == "Logged<Service>"));
            TestHarness.CheckTrue("无烘焙合成 fn（.proxy.<序>/.wrapped.）",
                !service.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol.Contains("$.proxy.0.") || d.Symbol.Contains("$.wrapped.")));

            BilTestHarness.CheckFnShape("specific 模板 fn（get.self + call.inner）",
                module, "Logged$$.proxy.doSomething(arg:.i32)@.string",
                ".vars { .generic<$.generic.TTarget> host, " +
                ".generic<$.generic.TTarget> .t0, .string .t1 }\n" +
                "get.self $.t0\n" +
                "set.var $.t0 $host\n" +
                "call.inner $.t1 [$arg]\n" +
                "ret $.t1\n");
            BilTestHarness.CheckFnShape("caller（invoke 原名，烘焙归 Middleware）",
                module, "$caller(s:Service)@.string",
                ".vars { .i32 .t0, .string .t1 }\n" +
                "load res(#0) $.t0\n" +
                "invoke fn(Service$doSomething(arg:.i32)@.string) $.t1 [$s, $.t0]\n" +
                "ret $.t1\n");
        }

        // ===== M88：wildcard proxy 模板 fn（call.inner 包转发）=====
        private static void TestProxyWildcardBakingEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(id: i32): String { return \"r\" }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（wildcard 模板）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（wildcard 模板）", module);

            var audited = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Audited");
            TestHarness.Check("wildcard proxy 模板声明",
                "Method|Audited$$.proxy.*(symbol:.string)@.generic<$.generic.TReturn>|" +
                "priv,operator(.proxy.*),wrapper-proxy(wildcard)",
                RenderMember(audited,
                    "Audited$$.proxy.*(symbol:.string)@.generic<$.generic.TReturn>"));
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.CheckTrue("无解包 shim / 特化合成",
                !service.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol.Contains(".proxy.unwrap.") || d.Symbol.Contains("$.proxy.0.")));

            BilTestHarness.CheckFnShape("wildcard 模板 fn（call.inner 包转发）",
                module, "Audited$$.proxy.*(symbol:.string)@.generic<$.generic.TReturn>",
                ".vars { .generic<$.generic.TReturn> .t0 }\n" +
                "call.inner $.t0 [$.kwargs.namedArgs, $.vargs.unnamedArgs]\n" +
                "ret $.t0\n");
        }

        // ===== M88：get 访问器 proxy 模板（标记产物，无合成链）=====
        private static void TestProxyAccessorBakingEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField { return value }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub var name: String { get }\n" +
                "    pub init(_ -> name)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（get 模板）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（get 模板）", module);

            var logged = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Logged");
            TestHarness.Check("get proxy 模板声明",
                "Method|Logged$$.proxy.get.name(value:.generic<$.generic.TField>)@" +
                ".generic<$.generic.TField>|" +
                "priv,operator(.proxy.get.name),wrapper-proxy(specific)",
                RenderMember(logged,
                    "Logged$$.proxy.get.name(value:.generic<$.generic.TField>)@" +
                    ".generic<$.generic.TField>"));
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.Check("getter 普通声明（无 wrapper-proxy）",
                "Method|Service$.get.name@.string|pub,getter(Service#name@.string)",
                RenderMember(service, "Service$.get.name@.string"));
            TestHarness.CheckTrue("无 .wrapped.get / .proxy.0.get 合成",
                !service.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol.Contains(".wrapped.get") || d.Symbol.Contains(".proxy.0.get")));
        }

        // ===== M88：降级调用点 → invoke core::Any$call??? =====
        private static void TestDowngradeEmissionSingle()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func f(service: Service): Any {\n" +
                "    return service.fetchUserById(42)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（降级单环）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（降级单环）", module);

            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.CheckTrue("Service wrapped(W)",
                service.Modifiers.OfType<BilWrappedModifier>()
                    .Any(m => m.WrapperTypeRef == "W"));
            TestHarness.CheckTrue("无 router/降级特化合成",
                !service.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol.Contains("call???") || d.Symbol.Contains(".proxy.0.???")));
            TestHarness.CheckTrue("降级请求 symbol 资源",
                module.Resources.OfType<BilScalarResource>().Any(r =>
                    r.Type == BilScalarType.String
                    && r.LiteralText == "\"Service$fetchUserById(.i32)@.any\""));

            BilTestHarness.CheckFnShape("调用点（invoke Any.call??? 胖值三参）",
                module, "$f(service:Service)@.any",
                ".vars { .any .t0, .string .t1, .array<core::Pair<.string, .any>> .t2, " +
                ".i32 .t3, .any .t4, .array<.any> .t5, .any .t6 }\n" +
                "cast $service $.t0 type(.any)\n" +
                "load res(#0) $.t1\n" +
                "new type(.array<core::Pair<.string, .any>>) $.t2 []\n" +
                "load res(#1) $.t3\n" +
                "cast $.t3 $.t4 type(.any)\n" +
                "new type(.array<.any>) $.t5 [$.t4]\n" +
                "invoke fn(core::Any$call???(symbol:.string," +
                "namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any) $.t6 [$.t0, $.t1, $.t2, $.t5]\n" +
                "ret $.t6\n");
        }

        // ===== M88：值位置 cast 物化（Any → User）=====
        private static void TestDowngradeCastMaterialization()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "pub class User {\n" +
                "    pub var id: i32\n" +
                "    pub init(i: i32) { id = i }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func f(service: Service): User {\n" +
                "    var u: User = service.fetch()\n" +
                "    return u\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（cast 物化）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（cast 物化）", module);
            BilTestHarness.CheckFnShape("值位置 cast 物化（invoke Any.call??? → cast User）",
                module, "$f(service:Service)@User",
                ".vars { User u, .any .t0, .string .t1, " +
                ".array<core::Pair<.string, .any>> .t2, .array<.any> .t3, " +
                ".any .t4, User .t5 }\n" +
                "cast $service $.t0 type(.any)\n" +
                "load res(#0) $.t1\n" +
                "new type(.array<core::Pair<.string, .any>>) $.t2 []\n" +
                "new type(.array<.any>) $.t3 []\n" +
                "invoke fn(core::Any$call???(symbol:.string," +
                "namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any) $.t4 [$.t0, $.t1, $.t2, $.t3]\n" +
                "cast $.t4 $.t5 type(User)\n" +
                "set.var $.t5 $u\n" +
                "ret $u\n");
        }

        // ===== M88：语句位置降级 =====
        private static void TestDowngradeStatementPosition()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func f(service: Service) {\n" +
                "    service.ping()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（语句位置）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（语句位置）", module);
            BilTestHarness.CheckFnShape("语句位置降级（invoke Any.call??? 丢弃结果）",
                module, "$f(service:Service)@.void",
                ".vars { .any .t0, .string .t1, .array<core::Pair<.string, .any>> .t2, " +
                ".array<.any> .t3, .any .t4 }\n" +
                "cast $service $.t0 type(.any)\n" +
                "load res(#0) $.t1\n" +
                "new type(.array<core::Pair<.string, .any>>) $.t2 []\n" +
                "new type(.array<.any>) $.t3 []\n" +
                "invoke fn(core::Any$call???(symbol:.string," +
                "namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any) $.t4 [$.t0, $.t1, $.t2, $.t3]\n" +
                "ret\n");
        }

        // ===== M88：双 wrapper 应用标记（调用点仍直 inv Any.call???）=====
        private static void TestDowngradeDoubleChain()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W1 {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W2 {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W1\n" +
                "@W2\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func f(service: Service): Any {\n" +
                "    return service.fetch()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（双环标记）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（双环标记）", module);
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            var wraps = service.Modifiers.OfType<BilWrappedModifier>()
                .Select(m => m.WrapperTypeRef).ToArray();
            TestHarness.Check("双环 wrapped 序（outer→inner）",
                "W1,W2", string.Join(",", wraps));
            TestHarness.CheckTrue("无双环特化合成",
                !module.Functions.Any(f => f.Symbol.Contains(".proxy.0.???")
                    || f.Symbol.Contains(".proxy.1.???")));
            TestHarness.CheckTrue("调用点仍 invoke Any.call???",
                module.Functions.Single(f => f.Symbol.StartsWith("$f("))
                    .Blocks[0].Instructions.OfType<InvokeInstruction>()
                    .Any(i => i.Method.Symbol.StartsWith("core::Any$call???")));
        }

        // ===== M88：无 .proxy.* 不降级 =====
        private static void TestDowngradeGateNoChain()
        {
            var (unit, _, _) = BilTestHarness.EmitBilUnit(
                "pub class Plain { pub init() }\n" +
                "pub func f(p: Plain) { p.missing() }\n");
            TestHarness.CheckSemanticError("无 wrapper 不降级", unit.Diagnostics,
                "Undefined member");
        }

        private static string RenderMember(BilTypeDeclaration type, string symbol)
        {
            var declaration = type.Members.OfType<BilSimpleMemberDeclaration>()
                .Single(d => d.Symbol == symbol);
            return declaration.Kind + "|" + declaration.Symbol + "|" + string.Join(",",
                declaration.Modifiers.Select(m => m.Render()));
        }
    }
}
