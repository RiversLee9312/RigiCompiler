using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    // BilEmitter（P4b）wrapper place 成员访问端到端（S11c，SYNTAX §14.5 +
    // BIL §12.4 注记/§13.3/§8.3.1）——解 M79 归口。
    // 覆盖：Entity 字段读（get.wrapper 值拷贝 + get.field）/字段写
    //（set.field.embedded）/方法调用（值拷贝 receiver）/嵌套链
    //（get.wrapper 逐级）/索引读（值拷贝 + get.array）；字段-Value 应用
    // 字段读写（get/set.field.embedded，宿主值 = 字段属主对象）；复合赋值
    //（读/写分离 + 宿主单次求值）；`.wrapper.` 隐藏字段 §8.3.1 声明发射
    //（priv var backing compiler-generated）；归口负例（局部/静态存储、
    // 字段-Value 方法调用、索引写、深层写穿）。
    public static partial class BilEmitterTests
    {
        // ===== Entity 字段读 + 隐藏字段声明（§8.3.1）=====
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

            // 隐藏字段声明（§8.3.1：priv var backing compiler-generated）
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            var hidden = service.Members.OfType<BilSimpleMemberDeclaration>()
                .Single(d => d.Symbol == "Service#.wrapper.Logged@Logged");
            TestHarness.Check("隐藏字段声明形态（§8.3.1）",
                "Field|Service#.wrapper.Logged@Logged|priv,var,backing,compiler-generated",
                hidden.Kind + "|" + hidden.Symbol + "|" + string.Join(",",
                    hidden.Modifiers.Select(m => m.Render())));

            // 读取 = get.wrapper 值拷贝 + get.field（§12.4 注记）
            BilTestHarness.CheckFnShape("Entity 字段读（get.wrapper + get.field）",
                module, "$f(s:Service)@.string",
                ".vars { Logged .s0, Logged .t0, .string .t1 }\n" +
                "get.wrapper $s type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Logged#level@.string)\n" +
                "ret $.t1\n");
        }

        // ===== Entity 字段写（set.field.embedded）=====
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
            BilTestHarness.CheckFnShape("Entity 字段写（set.field.embedded）",
                module, "$f(s:Service)@.void",
                ".vars { .string .t0 }\n" +
                "load res(#0) $.t0\n" +
                "set.field.embedded $.t0 $s field(Service#.wrapper.Logged@Logged) " +
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

        // ===== 嵌套链（wrapper 的 wrapper）：get.wrapper 逐级 =====
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
            // 两级隐藏字段都发射（wrapper 类型自身的隐藏字段同 §8.3.1）
            var outer = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Outer");
            TestHarness.CheckTrue("wrapper 类型自身的隐藏字段声明（Outer 上的 Inner）",
                outer.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "Outer#.wrapper.Inner@Inner"
                    && d.Modifiers.Any(m => m is BilKeywordModifier
                        { Keyword: BilKeyword.CompilerGenerated })));
        }

        // ===== Entity 索引读（值拷贝 + get.array，§13.6 三元组）=====
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

        // ===== 字段-Value 应用：embedded 读写（宿主 = 字段属主对象）=====
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
            // 隐藏字段挂字段宿主（Hero）
            var hero = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Hero");
            TestHarness.CheckTrue("字段-Value 隐藏字段挂字段宿主",
                hero.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "Hero#.wrapper.Clamped@Clamped"
                    && d.Modifiers.Any(m => m is BilKeywordModifier
                        { Keyword: BilKeyword.CompilerGenerated })));
            BilTestHarness.CheckFnShape("字段-Value 读写（get/set.field.embedded）",
                module, "$f(hero:Hero)@.i32",
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "load res(#0) $.t0\n" +
                "set.field.embedded $.t0 $hero field(Hero#.wrapper.Clamped@Clamped) " +
                "field(Clamped#min@.i32)\n" +
                "get.field.embedded $hero $.t1 field(Hero#.wrapper.Clamped@Clamped) " +
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
            BilTestHarness.CheckFnShape("复合赋值（读 = 值拷贝，写 = embedded）",
                module, "$f(s:Service)@.void",
                ".vars { Counted .s0, .i32 .s1, Counted .t0, .i32 .t1, .i32 .t2, " +
                ".i32 .t3 }\n" +
                "get.wrapper $s type(Counted) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Counted#count@.i32)\n" +
                "load res(#0) $.t2\n" +
                "add $.t1 $.t2 $.t3\n" +
                "set.var $.t3 $.s1\n" +
                "set.field.embedded $.s1 $s field(Service#.wrapper.Counted@Counted) " +
                "field(Counted#count@.i32)\n" +
                "ret\n");
        }

        // ===== 归口负例 =====
        private static void TestWrapperPlaceEmissionGates()
        {
            // 局部变量 wrapper place（栈帧存储合成归后续里程碑）
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
                "wrapper place storage for local variables");

            // 静态字段 wrapper place（静态存储合成归后续里程碑）
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
                "wrapper place storage for local variables");

            // 字段-Value 方法调用（get.wrapper VALUE 规则未定义该形态）
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

            // wrapper place 索引写（BIL 无 embedded 索引指令）
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

            // 深层成员写穿（place.a.b——写穿中间拷贝会丢失）
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
        // ===== S11d proxy 烘焙端到端（specific 单环）=====
        // 验收：invoke 原名（转发壳）→ 特化链 → 原始体，全链合法 BIL
        private static void TestProxyBakingEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n" +
                "pub func caller(s: Service): String { return s.doSomething(1) }\n");
            CheckNoErrors("全管线无诊断（specific 烘焙）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（specific 烘焙）", module);

            // 声明形态（§8.4）：特化 wrapper-proxy(specific)、原始体
            // wrapper-proxy(original) 均 priv；转发壳（原名 fn）是普通
            // 成员声明，不带 wrapper-proxy
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.Check("原始体声明（wrapper-proxy(original)）",
                "Method|Service$.wrapped.doSomething(arg:.i32)@.string|priv,wrapper-proxy(original)",
                RenderMember(service, "Service$.wrapped.doSomething(arg:.i32)@.string"));
            TestHarness.Check("特化声明（wrapper-proxy(specific)）",
                "Method|Service$.proxy.0.doSomething(arg:.i32)@.string|priv,wrapper-proxy(specific)",
                RenderMember(service, "Service$.proxy.0.doSomething(arg:.i32)@.string"));
            TestHarness.Check("转发壳声明（普通成员，无 wrapper-proxy）",
                "Method|Service$doSomething(arg:.i32)@.string|pub",
                RenderMember(service, "Service$doSomething(arg:.i32)@.string"));
            // wrapper 类型内的 proxy 声明模板不进 BIL（编译期模板，
            // 自身无 fn 定义）
            var logged = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Logged");
            TestHarness.CheckTrue("proxy 声明模板不进符号段",
                logged.Members.OfType<BilSimpleMemberDeclaration>()
                    .All(d => !d.Symbol.Contains(".proxy."))
                && service.Members.OfType<BilSimpleMemberDeclaration>()
                    .Count(d => d.Symbol.Contains(".proxy.")) == 1);

            // fn 定义平铺：转发壳 = invoke 链首；原始体 = 用户方法体；
            // 特化体 = inner 调用链末原始体
            BilTestHarness.CheckFnShape("转发壳 fn（invoke 链首）",
                module, "Service$doSomething(arg:.i32)@.string",
                ".vars { .string .t0 }\n" +
                "invoke fn(Service$.proxy.0.doSomething(arg:.i32)@.string) $.t0 [$.this, $arg]\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("原始体 fn（用户方法体）",
                module, "Service$.wrapped.doSomething(arg:.i32)@.string",
                ".vars { .string .t0 }\n" +
                "load res(#0) $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("特化 fn（inner = 原始体调用）",
                module, "Service$.proxy.0.doSomething(arg:.i32)@.string",
                ".vars { .string .t0 }\n" +
                "invoke fn(Service$.wrapped.doSomething(arg:.i32)@.string) $.t0 [$.this, $arg]\n" +
                "ret $.t0\n");
            // 调用点零改动（声明侧烘焙骑 vtable）：caller 仍 invoke 原名
            BilTestHarness.CheckFnShape("caller（invoke 原名，烘焙对外透明）",
                module, "$caller(s:Service)@.string",
                ".vars { .i32 .t0, .string .t1 }\n" +
                "load res(#0) $.t0\n" +
                "invoke fn(Service$doSomething(arg:.i32)@.string) $.t1 [$s, $.t0]\n" +
                "ret $.t1\n");
        }

        // ===== S11d proxy 烘焙端到端（wildcard 单环 + 解包 shim）=====
        private static void TestProxyWildcardBakingEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(id: i32): String { return \"r\" }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（wildcard 烘焙）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（wildcard 烘焙）", module);

            // shim 与特化均 wrapper-proxy(wildcard)；shim 具名包形参 =
            // §14.7 ABI 形态 .array<core::Pair<.string, .any>>
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.Check("解包 shim 声明（wrapper-proxy(wildcard)）",
                "Method|Service$.proxy.unwrap.0.fetch(namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.string|priv,wrapper-proxy(wildcard)",
                RenderMember(service, "Service$.proxy.unwrap.0.fetch(" +
                    "namedArgs:.array<core::Pair<.string, .any>>,unnamedArgs:.array<.any>)@.string"));
            TestHarness.Check("wildcard 特化声明（wrapper-proxy(wildcard)）",
                "Method|Service$.proxy.0.fetch(id:.i32)@.string|priv,wrapper-proxy(wildcard)",
                RenderMember(service, "Service$.proxy.0.fetch(id:.i32)@.string"));

            // 特化体：前奏三形参物化（symbol 常量 + 双包打包）+ inner = shim
            BilTestHarness.CheckFnShape("wildcard 特化 fn（前奏物化 + invoke shim）",
                module, "Service$.proxy.0.fetch(id:.i32)@.string",
                ".vars { .string symbol, .array<core::Pair<.string, .any>> namedArgs, " +
                ".array<.any> unnamedArgs, .string .t0, " +
                ".array<core::Pair<.string, .any>> .t1, .any .t2, .array<.any> .t3, " +
                ".string .t4 }\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $symbol\n" +
                "new type(.array<core::Pair<.string, .any>>) $.t1 []\n" +
                "set.var $.t1 $namedArgs\n" +
                "cast $id $.t2 type(.any)\n" +
                "new type(.array<.any>) $.t3 [$.t2]\n" +
                "set.var $.t3 $unnamedArgs\n" +
                "invoke fn(Service$.proxy.unwrap.0.fetch(namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.string) $.t4 [$.this, $namedArgs, $unnamedArgs]\n" +
                "ret $.t4\n");
            // shim：逐元素 cast 解包 + invoke 原始体
            BilTestHarness.CheckFnShape("解包 shim fn（cast 解包 + invoke 原始体）",
                module, "Service$.proxy.unwrap.0.fetch(namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.string",
                ".vars { .i32 .t0, .any .t1, .i32 .t2, .string .t3 }\n" +
                "load res(#0) $.t0\n" +
                "get.array $unnamedArgs $.t0 $.t1\n" +
                "cast $.t1 $.t2 type(.i32)\n" +
                "invoke fn(Service$.wrapped.fetch(id:.i32)@.string) $.t3 [$.this, $.t2]\n" +
                "ret $.t3\n");
        }

        // ===== S11d proxy 烘焙端到端（get 访问器链）=====
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
            CheckNoErrors("全管线无诊断（访问器链烘焙）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（访问器链烘焙）", module);

            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            // getter 转发壳声明走字段槽（getter(FIELD)，无 wrapper-proxy）；
            // 原始体/特化走类型成员表（wrapper-proxy(original/specific)）
            TestHarness.Check("getter 转发壳声明（字段槽 getter(FIELD)）",
                "Method|Service$.get.name@.string|pub,getter(Service#name@.string)",
                RenderMember(service, "Service$.get.name@.string"));
            TestHarness.Check("getter 原始体声明（wrapper-proxy(original)）",
                "Method|Service$.wrapped.get.name()@.string|priv,wrapper-proxy(original)",
                RenderMember(service, "Service$.wrapped.get.name()@.string"));
            TestHarness.Check("getter 特化声明（wrapper-proxy(specific)）",
                "Method|Service$.proxy.0.get.name()@.string|priv,wrapper-proxy(specific)",
                RenderMember(service, "Service$.proxy.0.get.name()@.string"));

            BilTestHarness.CheckFnShape("getter 转发壳 fn（invoke get 链首）",
                module, "Service$.get.name@.string",
                ".vars { .string .t0 }\n" +
                "invoke fn(Service$.proxy.0.get.name()@.string) $.t0 [$.this]\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("getter 原始体 fn（自动访问器合成体）",
                module, "Service$.wrapped.get.name()@.string",
                ".vars { .string .t0 }\n" +
                "get.field $.this $.t0 field(Service#name@.string)\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("getter 特化 fn（value 前奏 = invoke 下一环）",
                module, "Service$.proxy.0.get.name()@.string",
                ".vars { .string value, .string .t0 }\n" +
                "invoke fn(Service$.wrapped.get.name()@.string) $.t0 [$.this]\n" +
                "set.var $.t0 $value\n" +
                "ret $value\n");
        }

        // ===== S11e call??? 降级发射端到端（单环，验收核心）=====
        // 验收（SYNTAX §14.7 + BIL §15.4）：未声明方法调用点降级为
        // invoke router（胖值三实参：symbol 字符串资源 + 具名空包 + 位置
        // 包含 cast）→ router 直通体 invoke 链首特化 → 特化零前奏直通链
        // 末 Any.call??? → 链末默认体 new NoSuchMethodException + throw
        private static void TestDowngradeEmissionSingle()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
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

            // 声明形态（§8.4）：router wrapper-proxy(router)、降级特化
            // wrapper-proxy(wildcard) 均 priv；Any.call??? 宿主内建无声明
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.Check("router 声明（wrapper-proxy(router)）",
                "Method|Service$call???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any|priv,wrapper-proxy(router)",
                RenderMember(service, "Service$call???(symbol:.string," +
                    "namedArgs:.array<core::Pair<.string, .any>>,unnamedArgs:.array<.any>)@.any"));
            TestHarness.Check("降级特化声明（wrapper-proxy(wildcard)）",
                "Method|Service$.proxy.0.???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any|priv,wrapper-proxy(wildcard)",
                RenderMember(service, "Service$.proxy.0.???(symbol:.string," +
                    "namedArgs:.array<core::Pair<.string, .any>>,unnamedArgs:.array<.any>)@.any"));

            // 降级请求 symbol 字符串资源内容（§14.7：位置实参只写类型、
            // 返回段恒 .any；本样例无具名实参）
            TestHarness.CheckTrue("降级请求 symbol 资源内容",
                module.Resources.OfType<BilScalarResource>().Any(r =>
                    r.Type == BilScalarType.String
                    && r.LiteralText == "\"Service$fetchUserById(.i32)@.any\""));

            // 调用点：invoke router——receiver 在前、三实参（symbol 资源 /
            // 具名空包构造 / 位置包含 cast 42→Any）。资源按本 fn 内首次
            // 出现顺序重编号（#0 = symbol 字符串、#1 = 42）
            BilTestHarness.CheckFnShape("调用点 fn（invoke router 胖值三参）",
                module, "$f(service:Service)@.any",
                ".vars { .string .t0, .array<core::Pair<.string, .any>> .t1, .i32 .t2, .any .t3, " +
                ".array<.any> .t4, .any .t5 }\n" +
                "load res(#0) $.t0\n" +
                "new type(.array<core::Pair<.string, .any>>) $.t1 []\n" +
                "load res(#1) $.t2\n" +
                "cast $.t2 $.t3 type(.any)\n" +
                "new type(.array<.any>) $.t4 [$.t3]\n" +
                "invoke fn(Service$call???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any) $.t5 [$service, $.t0, $.t1, $.t4]\n" +
                "ret $.t5\n");
            // router fn：直通体 invoke 链首特化（this + 胖值三形参逐一引用）
            BilTestHarness.CheckFnShape("router fn（invoke 链首特化）",
                module, "Service$call???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any",
                ".vars { .any .t0 }\n" +
                "invoke fn(Service$.proxy.0.???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any) $.t0 [$.this, $symbol, $namedArgs, $unnamedArgs]\n" +
                "ret $.t0\n");
            // 特化 fn：零前奏直通链末 Any.call???（receiver 沿宿主 cast 到
            // Any——InstanceCallRewriter §6.5 物化；symbol 由 CallVisitors
            // 自动补为当前 fn 形参引用）
            BilTestHarness.CheckFnShape("降级特化 fn（invoke Any.call??? 链末）",
                module, "Service$.proxy.0.???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any",
                ".vars { .any .t0, .any .t1 }\n" +
                "cast $.this $.t0 type(.any)\n" +
                "invoke fn(core::Any$call???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any) $.t1 [$.t0, $symbol, $namedArgs, $unnamedArgs]\n" +
                "ret $.t1\n");
            // Any.call??? 链末默认体（RUNTIME §14.2）：new NoSuchMethodException + throw
            BilTestHarness.CheckFnShape("Any.call??? 默认体（new + throw）",
                module, "core::Any$call???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any",
                ".vars { core::NoSuchMethodException .t0 }\n" +
                "new type(core::NoSuchMethodException) $.t0 [$symbol]\n" +
                "throw $.t0\n");
        }

        // ===== S11e 值位置 cast 物化（§12.1/§6.5）=====
        // 未声明方法降级调用返回 Any（胖值 ABI），赋给 class 局部时
        // EnsureDeclaredType 物化 cast .any → User（引用不等即物化）
        private static void TestDowngradeCastMaterialization()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
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
            // invoke（Any 结果）后 cast .any → User 物化，再 set.var 落局部
            // （#0 = symbol 字符串，本 fn 内首次出现）
            BilTestHarness.CheckFnShape("值位置 cast 物化（invoke 后 cast User）",
                module, "$f(service:Service)@User",
                ".vars { User u, .string .t0, .array<core::Pair<.string, .any>> .t1, " +
                ".array<.any> .t2, .any .t3, User .t4 }\n" +
                "load res(#0) $.t0\n" +
                "new type(.array<core::Pair<.string, .any>>) $.t1 []\n" +
                "new type(.array<.any>) $.t2 []\n" +
                "invoke fn(Service$call???(symbol:.string,namedArgs:.array<core::Pair<.string, .any>>," +
                "unnamedArgs:.array<.any>)@.any) $.t3 [$service, $.t0, $.t1, $.t2]\n" +
                "cast $.t3 $.t4 type(User)\n" +
                "set.var $.t4 $u\n" +
                "ret $u\n");
        }

        // ===== S11e 语句位置降级调用 =====
        // `service.ping()` 裸语句：非 void 调用的语句位置形态走既有路径
        //（丢弃返回值），合法 BIL
        private static void TestDowngradeStatementPosition()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
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
            TestHarness.CheckTrue("语句位置降级 fn 平铺",
                module.Functions.Any(f => f.Symbol == "$f(service:Service)@.void")
                && module.Functions.Any(f => f.Symbol.StartsWith("Service$call???")));
        }

        // ===== S11e 双 wrapper 双环（链长 2）=====
        // 两个 .proxy.* wrapper 应用 → 降级链 .proxy.0 → .proxy.1 →
        // Any.call???；router 直通 .proxy.0，逐环直通下一环，端到端合法
        private static void TestDowngradeDoubleChain()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W1 {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W2 {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
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
            CheckNoErrors("全管线无诊断（双环）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（双环）", module);
            // 两个降级特化 fn 定义平铺（.proxy.0 → .proxy.1 → Any.call???）
            var service = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Service");
            TestHarness.CheckTrue("双环特化声明齐全（.proxy.0/.proxy.1 均 wrapper-proxy(wildcard)）",
                service.Members.OfType<BilSimpleMemberDeclaration>()
                    .Count(d => d.Symbol.StartsWith("Service$.proxy.") && d.Symbol.Contains(".???"))
                    == 2
                && service.Members.OfType<BilSimpleMemberDeclaration>()
                    .All(d => !d.Symbol.StartsWith("Service$.proxy.") || !d.Symbol.Contains(".???")
                        || d.Modifiers.OfType<BilWrapperProxyModifier>()
                            .Any(m => m.Kind == BilProxyKind.Wildcard)));
            // 链首特化 invoke 下一环特化（.proxy.0 体引用 .proxy.1）
            TestHarness.CheckTrue("链首特化 invoke 下一环（.proxy.0 → .proxy.1）",
                module.Functions.Any(f => f.Symbol.StartsWith("Service$.proxy.0.???")
                    && f.Blocks.SelectMany(b => b.Instructions).OfType<InvokeInstruction>()
                        .Any(i => i.Method.Symbol.StartsWith("Service$.proxy.1.???"))));
            // 链末特化 invoke Any.call???
            TestHarness.CheckTrue("链末特化 invoke Any.call???",
                module.Functions.Any(f => f.Symbol.StartsWith("Service$.proxy.1.???")
                    && f.Blocks.SelectMany(b => b.Instructions).OfType<InvokeInstruction>()
                        .Any(i => i.Method.Symbol.StartsWith("core::Any$call???"))));
        }

        // ===== S11e 负例：无 .proxy.* 链不降级 =====
        // wrapper 不含普通方法类别 .proxy.* → P2 不合成降级链 → 未声明方法
        // 调用保持既有 Undefined member 语义错误，不产生 BIL
        private static void TestDowngradeGateNoChain()
        {
            var (unit, _, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func f(service: Service): Any {\n" +
                "    return service.unknownMethod(1)\n" +
                "}\n");
            TestHarness.CheckSemanticError("无 .proxy.* 链不降级（Undefined member）",
                unit.Diagnostics, "Undefined member");
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
