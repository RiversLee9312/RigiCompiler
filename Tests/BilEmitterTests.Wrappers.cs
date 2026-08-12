using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    // BilEmitter（P4b）wrapper 标记产物端到端（M88）：
    // - place 成员访问：Entity 读 = get.wrapper 值拷贝 + get.field；
    //   写 = set.wrapper.field wrapper(W)；字段-Value 读 = get.wrapper.field
    //   + get.field、写 = set.wrapper.field；
    // - 应用标记 §8.3.1 wrapped(W)（无隐藏字段声明）；
    // - proxy 模板 fn：wrapper-proxy(specific|wildcard) + get.self/invoke fn(..inner)；
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

        // ===== Entity 字段写（set.wrapper.field wrapper(W)）=====
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
            BilTestHarness.CheckFnShape("Entity 字段写（set.wrapper.field wrapper）",
                module, "$f(s:Service)@.void",
                ".vars { .string .t0 }\n" +
                "load res(#0) $.t0\n" +
                "set.wrapper.field $.t0 $s wrapper(Logged) " +
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

        // ===== 字段-Value：get.wrapper.field + get.field + 字段 wrapped 标记 =====
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
            BilTestHarness.CheckFnShape(
                "字段-Value 读写（get.wrapper.field+get.field / set.wrapper.field）",
                module, "$f(hero:Hero)@.i32",
                ".vars { Clamped .s0, .i32 .t0, Clamped .t1, .i32 .t2 }\n" +
                "load res(#0) $.t0\n" +
                "set.wrapper.field $.t0 $hero field(Hero#hp@.i32) wrapper(Clamped) " +
                "field(Clamped#min@.i32)\n" +
                "get.wrapper.field $hero field(Hero#hp@.i32) type(Clamped) $.t1\n" +
                "set.var $.t1 $.s0\n" +
                "get.field $.s0 $.t2 field(Clamped#min@.i32)\n" +
                "ret $.t2\n");
        }

        // ===== 同 owner 两字段同 W：hp/mp 必须产生不同 field(HOST_FIELD) =====
        private static void TestWrapperFieldValueSameWrapperTwoFields()
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
                "    @Clamped\n" +
                "    pub var mp: i32\n" +
                "    pub init(h: i32, m: i32) { hp = h\n mp = m }\n" +
                "}\n" +
                "pub func f(hero: Hero): i32 {\n" +
                "    hero.hp:Clamped.min = 1\n" +
                "    hero.mp:Clamped.min = 2\n" +
                "    return hero.hp:Clamped.min\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（同 W 两字段）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（同 W 两字段）", module);
            BilTestHarness.CheckFnShape("同 owner 两字段同 W（hp/mp 不同 HOST_FIELD）",
                module, "$f(hero:Hero)@.i32",
                ".vars { Clamped .s0, .i32 .t0, .i32 .t1, Clamped .t2, .i32 .t3 }\n" +
                "load res(#0) $.t0\n" +
                "set.wrapper.field $.t0 $hero field(Hero#hp@.i32) wrapper(Clamped) " +
                "field(Clamped#min@.i32)\n" +
                "load res(#1) $.t1\n" +
                "set.wrapper.field $.t1 $hero field(Hero#mp@.i32) wrapper(Clamped) " +
                "field(Clamped#min@.i32)\n" +
                "get.wrapper.field $hero field(Hero#hp@.i32) type(Clamped) $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(Clamped#min@.i32)\n" +
                "ret $.t3\n");
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
            BilTestHarness.CheckFnShape("复合赋值（读 = 值拷贝，写 = set.wrapper.field）",
                module, "$f(s:Service)@.void",
                ".vars { Counted .s0, .i32 .s1, Counted .t0, .i32 .t1, .i32 .t2, " +
                ".i32 .t3 }\n" +
                "get.wrapper $s type(Counted) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Counted#count@.i32)\n" +
                "load res(#0) $.t2\n" +
                "add $.t1 $.t2 $.t3\n" +
                "set.var $.t3 $.s1\n" +
                "set.wrapper.field $.s1 $s wrapper(Counted) " +
                "field(Counted#count@.i32)\n" +
                "ret\n");
        }

        // ===== 局部/静态 wrapper place 端到端正例 + 索引写归口负例 =====
        private static void TestWrapperPlaceEmissionGates()
        {
            // 局部 wrapper：读 getValue 路径经 place = get.wrapper.field($x,value)+get.field；
            // 写 = set.wrapper.field 链 field(value)→wrapper(W)→field(内层)
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    health:Clamped.min = 10\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（局部 wrapper place）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（局部 wrapper place）", module);
            AssertCellSubclassDeclaration(module, "局部 wrapper place",
                readOnly: false, elementType: ".i32");
            TestHarness.CheckTrue("局部 cell value 字段带 wrapped(Clamped)",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol.StartsWith("..cell.."))
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(m => m.Symbol.Contains("#value@")
                        && m.Modifiers.OfType<BilWrappedModifier>()
                            .Any(w => w.WrapperTypeRef == "Clamped")));
            BilTestHarness.CheckFnShape(
                "局部 wrapper place 读写（new cell + set.wrapper.field + get.wrapper.field）",
                module, "$f()@.i32",
                ".vars { ..cell..UUID health, Clamped .s0, .i32 .t0, ..cell..UUID .t1, " +
                ".i32 .t2, Clamped .t3, .i32 .t4 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $health\n" +
                "load res(#1) $.t2\n" +
                "set.wrapper.field $.t2 $health field(..cell..UUID#value@.i32) wrapper(Clamped) " +
                "field(Clamped#min@.i32)\n" +
                "get.wrapper.field $health field(..cell..UUID#value@.i32) type(Clamped) $.t3\n" +
                "set.var $.t3 $.s0\n" +
                "get.field $.s0 $.t4 field(Clamped#min@.i32)\n" +
                "ret $.t4\n");

            // 静态字段 place：宿主 = get.field.static 取 cell
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
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
                "    Holder.counter:SClamp.min = 5\n" +
                "    return Holder.counter:SClamp.min\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（静态字段 wrapper place）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（静态字段 wrapper place）", module2);
            var holder = module2.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Holder");
            var counter = holder.Members.OfType<BilSimpleMemberDeclaration>()
                .Single(d => d.Symbol.Contains("#.static.counter@"));
            TestHarness.CheckTrue("静态字段声明类型 = cell 子类（无 wrapped 在字段槽）",
                counter.Symbol.Contains("@..cell..")
                && !counter.Modifiers.OfType<BilWrappedModifier>().Any());
            BilTestHarness.CheckFnShape(
                "静态字段 place 读写（get.field.static 取 cell + wrapper.field）",
                module2, "$f()@.i32",
                ".vars { SClamp .s0, ..cell..UUID .t0, .i32 .t1, ..cell..UUID .t2, " +
                "SClamp .t3, .i32 .t4 }\n" +
                "get.field.static $.t0 type(Holder) field(Holder#.static.counter@..cell..UUID)\n" +
                "load res(#0) $.t1\n" +
                "set.wrapper.field $.t1 $.t0 field(..cell..UUID#value@.i32) wrapper(SClamp) " +
                "field(SClamp#min@.i32)\n" +
                "get.field.static $.t2 type(Holder) field(Holder#.static.counter@..cell..UUID)\n" +
                "get.wrapper.field $.t2 field(..cell..UUID#value@.i32) type(SClamp) $.t3\n" +
                "set.var $.t3 $.s0\n" +
                "get.field $.s0 $.t4 field(SClamp#min@.i32)\n" +
                "ret $.t4\n");

            // 索引写归口负例（保留）
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
        }

        // ===== 局部/静态 cell 存储扩展覆盖 =====
        private static void TestWrapperCellStorageCoverage()
        {
            // wrapped const 局部 → ReadonlyCell
            var (unitConst, moduleConst, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    const health: i32 = 50\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（wrapped const 局部）", unitConst);
            BilTestHarness.CheckBilValid("验证器零错误（wrapped const 局部）", moduleConst);
            AssertCellSubclassDeclaration(moduleConst, "wrapped const 局部",
                readOnly: true, elementType: ".i32");
            TestHarness.CheckTrue("const cell value 为 const + wrapped(Clamped)",
                moduleConst.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol.StartsWith("..cell.."))
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(m => m.Symbol.Contains("#value@")
                        && m.Modifiers.OfType<BilKeywordModifier>()
                            .Any(k => k.Keyword == BilKeyword.Const)
                        && m.Modifiers.OfType<BilWrappedModifier>()
                            .Any(w => w.WrapperTypeRef == "Clamped")));
            BilTestHarness.CheckFnShape("wrapped const 局部 place 读",
                moduleConst, "$f()@.i32",
                ".vars { ..cell..UUID health, Clamped .s0, .i32 .t0, ..cell..UUID .t1, " +
                "Clamped .t2, .i32 .t3 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $health\n" +
                "get.wrapper.field $health field(..cell..UUID#value@.i32) type(Clamped) $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(Clamped#min@.i32)\n" +
                "ret $.t3\n");

            // wrapped 局部被 lambda 捕获：capture 字段类型 = 已有 cell 子类；外层写 setValue
            var (unitCap, moduleCap, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    var g = func{(): i32 -> health}\n" +
                "    health = 60\n" +
                "    return g()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（wrapped 局部 lambda 捕获）", unitCap);
            BilTestHarness.CheckBilValid("验证器零错误（wrapped 局部 lambda 捕获）", moduleCap);
            AssertLambdaClass(moduleCap, "wrapped 局部捕获", "core::Func<.i32>",
                hasCaptureField: true, captureTypeFragment: "..cell..");
            TestHarness.CheckTrue("恰一个 cell 隐藏子类（不套第二层）",
                moduleCap.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Count(t => t.Symbol.StartsWith("..cell..")) == 1);
            BilTestHarness.CheckFnShape("wrapped 局部捕获外层（cell 引用实参 + setValue）",
                moduleCap, "$f()@.i32",
                ".vars { ..cell..UUID health, ..lambda..UUID g, .i32 .t0, ..cell..UUID .t1, " +
                "..lambda..UUID .t2, .i32 .t3, .i32 .t4 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $health\n" +
                "new type(..lambda..UUID) $.t2 [$health]\n" +
                "set.var $.t2 $g\n" +
                "load res(#1) $.t3\n" +
                "invoke.noret fn(core::Cell$setValue(v:.generic<$.generic.T>)@.void) " +
                "[$health, $.t3]\n" +
                "invoke.indirect $g $.t4 []\n" +
                "ret $.t4\n");
            var callFn = moduleCap.Functions.First(f => f.Symbol.Contains("$$call"));
            BilTestHarness.CheckFnShape("wrapped 局部捕获 $$call getValue",
                moduleCap, callFn.Symbol,
                ".vars { ..cell..UUID .t0, .i32 .t1 }\n" +
                "get.field $.this $.t0 field(..lambda..UUID#.capture.health@..cell..UUID)\n" +
                "invoke fn(core::Cell$getValue()@.generic<$.generic.T>) $.t1 [$.t0]\n" +
                "ret $.t1\n");

            // 局部 wrapper 复合赋值 + 深写
            var (unitComp, moduleComp, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    health:Clamped.min += 1\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（局部 wrapper 复合赋值）", unitComp);
            BilTestHarness.CheckBilValid("验证器零错误（局部 wrapper 复合赋值）", moduleComp);
            BilTestHarness.CheckFnShape("局部 wrapper 复合赋值 place.f += rhs",
                moduleComp, "$f()@.i32",
                ".vars { ..cell..UUID health, Clamped .s0, .i32 .s1, Clamped .s2, " +
                ".i32 .t0, ..cell..UUID .t1, Clamped .t2, .i32 .t3, .i32 .t4, .i32 .t5, " +
                "Clamped .t6, .i32 .t7 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $health\n" +
                "get.wrapper.field $health field(..cell..UUID#value@.i32) type(Clamped) $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(Clamped#min@.i32)\n" +
                "load res(#1) $.t4\n" +
                "add $.t3 $.t4 $.t5\n" +
                "set.var $.t5 $.s1\n" +
                "set.wrapper.field $.s1 $health field(..cell..UUID#value@.i32) wrapper(Clamped) " +
                "field(Clamped#min@.i32)\n" +
                "get.wrapper.field $health field(..cell..UUID#value@.i32) type(Clamped) $.t6\n" +
                "set.var $.t6 $.s2\n" +
                "get.field $.s2 $.t7 field(Clamped#min@.i32)\n" +
                "ret $.t7\n");

            var (unitDeep, moduleDeep, _) = BilTestHarness.EmitBilUnit(
                "pub struct Inner { pub var x: i32\n pub init(v: i32) { x = v } }\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Boxed {\n" +
                "    pub var sub: Inner\n" +
                "    pub init(_ -> sub)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Boxed\n" +
                "    var slot: i32 = 0\n" +
                "    slot:Boxed.sub.x = 3\n" +
                "    return slot:Boxed.sub.x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（局部 wrapper 深写）", unitDeep);
            BilTestHarness.CheckBilValid("验证器零错误（局部 wrapper 深写）", moduleDeep);
            BilTestHarness.CheckFnShape("局部 wrapper 深写 place.a.b",
                moduleDeep, "$f()@.i32",
                ".vars { ..cell..UUID slot, Boxed .s0, Inner .s1, Boxed .s2, " +
                ".i32 .t0, ..cell..UUID .t1, Boxed .t2, Inner .t3, .i32 .t4, " +
                "Boxed .t5, Inner .t6, .i32 .t7 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $slot\n" +
                "get.wrapper.field $slot field(..cell..UUID#value@.i32) type(Boxed) $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(Boxed#sub@Inner)\n" +
                "set.var $.t3 $.s1\n" +
                "load res(#1) $.t4\n" +
                "set.field $.t4 $.s1 field(Inner#x@.i32)\n" +
                "set.wrapper.field $.s1 $slot field(..cell..UUID#value@.i32) wrapper(Boxed) " +
                "field(Boxed#sub@Inner)\n" +
                "get.wrapper.field $slot field(..cell..UUID#value@.i32) type(Boxed) $.t5\n" +
                "set.var $.t5 $.s2\n" +
                "get.field $.s2 $.t6 field(Boxed#sub@Inner)\n" +
                "get.field $.t6 $.t7 field(Inner#x@.i32)\n" +
                "ret $.t7\n");

            // 静态字段普通读写（非 place）
            var (unitStatic, moduleStatic, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = 0\n" +
                "}\n" +
                "pub func g(): i32 {\n" +
                "    Holder.counter = 7\n" +
                "    return Holder.counter\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（静态字段普通读写）", unitStatic);
            BilTestHarness.CheckBilValid("验证器零错误（静态字段普通读写）", moduleStatic);
            BilTestHarness.CheckFnShape(
                "静态字段普通读写（get.field.static + setValue/getValue）",
                moduleStatic, "$g()@.i32",
                ".vars { ..cell..UUID .t0, .i32 .t1, ..cell..UUID .t2, .i32 .t3 }\n" +
                "get.field.static $.t0 type(Holder) field(Holder#.static.counter@..cell..UUID)\n" +
                "load res(#0) $.t1\n" +
                "invoke.noret fn(core::Cell$setValue(v:.generic<$.generic.T>)@.void) " +
                "[$.t0, $.t1]\n" +
                "get.field.static $.t2 type(Holder) field(Holder#.static.counter@..cell..UUID)\n" +
                "invoke fn(core::Cell$getValue()@.generic<$.generic.T>) $.t3 [$.t2]\n" +
                "ret $.t3\n");

            // 多 Value wrapper 分别 place（x:A / x:B）
            var (unitMulti, moduleMulti, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub var tag: String\n" +
                "    pub init(_ -> tag)\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper B {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 1\n" +
                "    return x:B.n\n" +
                "}\n" +
                "pub func g(): String {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 1\n" +
                "    return x:A.tag\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（多 Value wrapper 分别 place）", unitMulti);
            BilTestHarness.CheckBilValid("验证器零错误（多 Value wrapper 分别 place）",
                moduleMulti);
            BilTestHarness.CheckFnShape("x:B.n place 读",
                moduleMulti, "$f()@.i32",
                ".vars { ..cell..UUID x, B .s0, .i32 .t0, ..cell..UUID .t1, B .t2, .i32 .t3 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $x\n" +
                "get.wrapper.field $x field(..cell..UUID#value@.i32) type(B) $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(B#n@.i32)\n" +
                "ret $.t3\n");
            BilTestHarness.CheckFnShape("x:A.tag place 读",
                moduleMulti, "$g()@.string",
                ".vars { ..cell..UUID x, A .s0, .i32 .t0, ..cell..UUID .t1, A .t2, " +
                ".string .t3 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $x\n" +
                "get.wrapper.field $x field(..cell..UUID#value@.i32) type(A) $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(A#tag@.string)\n" +
                "ret $.t3\n");
        }

        // ===== M84：字段-Value 方法调用 / 索引读（get.wrapper.field）=====
        private static void TestWrapperFieldValueCallAndIndexEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
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
            CheckNoErrors("全管线无诊断（字段-Value 调用）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（字段-Value 调用）", module);
            BilTestHarness.CheckFnShape("字段-Value 方法调用（get.wrapper.field + invoke）",
                module, "$f(hero:Hero)@.i32",
                ".vars { Clamped .s0, Clamped .t0, .i32 .t1, .i32 .t2 }\n" +
                "get.wrapper.field $hero field(Hero#hp@.i32) type(Clamped) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "load res(#0) $.t1\n" +
                "invoke fn(Clamped$clamp(v:.i32)@.i32) $.t2 [$.s0, $.t1]\n" +
                "ret $.t2\n");

            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Indexed {\n" +
                "    pub var store: i32\n" +
                "    pub init(_ -> store)\n" +
                "    pub operator getAtIndex(index: i32): i32 { return store }\n" +
                "    pub operator setAtIndex(index: i32, value: i32) { store = value }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @Indexed\n" +
                "    pub var slot: i32\n" +
                "    pub init(v: i32) { slot = v }\n" +
                "}\n" +
                "pub func f(h: Holder): i32 {\n" +
                "    return h.slot:Indexed[0]\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（字段-Value 索引读）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（字段-Value 索引读）", module2);
            BilTestHarness.CheckFnShape("字段-Value 索引读（get.wrapper.field + get.array）",
                module2, "$f(h:Holder)@.i32",
                ".vars { Indexed .s0, Indexed .t0, .i32 .t1, .i32 .t2 }\n" +
                "get.wrapper.field $h field(Holder#slot@.i32) type(Indexed) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "load res(#0) $.t1\n" +
                "get.array $.s0 $.t1 $.t2\n" +
                "ret $.t2\n");

            var (unit3, _, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Indexed {\n" +
                "    pub var store: i32\n" +
                "    pub init(_ -> store)\n" +
                "    pub operator getAtIndex(index: i32): i32 { return store }\n" +
                "    pub operator setAtIndex(index: i32, value: i32) { store = value }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @Indexed\n" +
                "    pub var slot: i32\n" +
                "    pub init(v: i32) { slot = v }\n" +
                "}\n" +
                "pub func f(h: Holder) {\n" +
                "    h.slot:Indexed[0] = 1\n" +
                "}\n");
            TestHarness.CheckSemanticError("字段-Value 索引写归口", unit3.Diagnostics,
                "writes through wrapper place member or index chains");
        }

        // ===== M84：深层写穿 place.a.b... =====
        private static void TestWrapperDeepWriteEmission()
        {
            // 一层值中间
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub struct Inner {\n" +
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
            CheckNoErrors("全管线无诊断（一层值中间深写）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（一层值中间深写）", module);
            BilTestHarness.CheckFnShape("一层值中间深写（get + 叶写 + set.wrapper.field 写回）",
                module, "$f(s:Service)@.void",
                ".vars { Logged .s0, Inner .s1, Logged .t0, Inner .t1, .i32 .t2 }\n" +
                "get.wrapper $s type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Logged#sub@Inner)\n" +
                "set.var $.t1 $.s1\n" +
                "load res(#0) $.t2\n" +
                "set.field $.t2 $.s1 field(Inner#x@.i32)\n" +
                "set.wrapper.field $.s1 $s wrapper(Logged) field(Logged#sub@Inner)\n" +
                "ret\n");

            // 两层值中间
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "pub struct Leaf { pub var x: i32\n pub init(v: i32) { x = v } }\n" +
                "pub struct Mid { pub var leaf: Leaf\n pub init(l: Leaf) { leaf = l } }\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Box {\n" +
                "    pub var mid: Mid\n" +
                "    pub init(_ -> mid)\n" +
                "}\n" +
                "@Box\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Box.mid.leaf.x = 7\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（两层值中间深写）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（两层值中间深写）", module2);
            BilTestHarness.CheckFnShape("两层值中间深写（双写回）",
                module2, "$f(s:Service)@.void",
                ".vars { Box .s0, Mid .s1, Leaf .s2, Box .t0, Mid .t1, Leaf .t2, .i32 .t3 }\n" +
                "get.wrapper $s type(Box) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Box#mid@Mid)\n" +
                "set.var $.t1 $.s1\n" +
                "get.field $.s1 $.t2 field(Mid#leaf@Leaf)\n" +
                "set.var $.t2 $.s2\n" +
                "load res(#0) $.t3\n" +
                "set.field $.t3 $.s2 field(Leaf#x@.i32)\n" +
                "set.field $.s2 $.s1 field(Mid#leaf@Leaf)\n" +
                "set.wrapper.field $.s1 $s wrapper(Box) field(Box#mid@Mid)\n" +
                "ret\n");

            // 引用中间：叶写后不多余写回
            var (unit3, module3, _) = BilTestHarness.EmitBilUnit(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var node: Node\n" +
                "    pub init(_ -> node)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.node.x = 1\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（引用中间深写）", unit3);
            BilTestHarness.CheckBilValid("验证器零错误（引用中间深写）", module3);
            BilTestHarness.CheckFnShape("引用中间深写（无写回）",
                module3, "$f(s:Service)@.void",
                ".vars { Logged .s0, Node .s1, Logged .t0, Node .t1, .i32 .t2 }\n" +
                "get.wrapper $s type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Logged#node@Node)\n" +
                "set.var $.t1 $.s1\n" +
                "load res(#0) $.t2\n" +
                "set.field $.t2 $.s1 field(Node#x@.i32)\n" +
                "ret\n");

            // 只读值中间拒绝
            var (unit4, _, _) = BilTestHarness.EmitBilUnit(
                "pub struct Inner {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub const sub: Inner\n" +
                "    pub init(_ -> sub)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.sub.x = 1\n" +
                "}\n");
            TestHarness.CheckSemanticError("只读值中间深写拒绝", unit4.Diagnostics,
                "Cannot assign to const field");

            // 字段-Value place 一层值中间
            var (unit5, module5, _) = BilTestHarness.EmitBilUnit(
                "pub struct Inner {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Boxed {\n" +
                "    pub var sub: Inner\n" +
                "    pub init(_ -> sub)\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Boxed\n" +
                "    pub var hp: i32\n" +
                "    pub init(h: i32) { hp = h }\n" +
                "}\n" +
                "pub func f(hero: Hero) {\n" +
                "    hero.hp:Boxed.sub.x = 3\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（字段-Value 深写）", unit5);
            BilTestHarness.CheckBilValid("验证器零错误（字段-Value 深写）", module5);
            BilTestHarness.CheckFnShape("字段-Value 一层值中间深写",
                module5, "$f(hero:Hero)@.void",
                ".vars { Boxed .s0, Inner .s1, Boxed .t0, Inner .t1, .i32 .t2 }\n" +
                "get.wrapper.field $hero field(Hero#hp@.i32) type(Boxed) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Boxed#sub@Inner)\n" +
                "set.var $.t1 $.s1\n" +
                "load res(#0) $.t2\n" +
                "set.field $.t2 $.s1 field(Inner#x@.i32)\n" +
                "set.wrapper.field $.s1 $hero field(Hero#hp@.i32) wrapper(Boxed) " +
                "field(Boxed#sub@Inner)\n" +
                "ret\n");

            // 只读 getter 中间（无 setter）拒绝写回
            var (unit6, _, _) = BilTestHarness.EmitBilUnit(
                "pub struct Inner {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var sub: Inner {\n" +
                "        pub get(value: _) { return value }\n" +
                "    }\n" +
                "    pub init(_ -> sub)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.sub.x = 1\n" +
                "}\n");
            TestHarness.CheckSemanticError("无 setter 中间深写拒绝", unit6.Diagnostics,
                "has no setter");

            // RHS 与 receiver 求值序：宿主字段 getter 先于 RHS 副作用调用
            var (unit7, module7, _) = BilTestHarness.EmitBilUnit(
                "pub struct Inner {\n" +
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
                "pub func side(): i32 { return 9 }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.sub.x = side()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（深写求值序）", unit7);
            BilTestHarness.CheckBilValid("验证器零错误（深写求值序）", module7);
            var fFn = module7.Functions.Single(fn => fn.Symbol == "$f(s:Service)@.void");
            var ops = fFn.Blocks[0].Instructions.Select(i => i.Opcode).ToList();
            var getWrapperAt = ops.IndexOf("get.wrapper");
            var sideAt = ops.FindIndex(o => o is "invoke" or "invoke.noret");
            var setLeafAt = ops.IndexOf("set.field");
            var writebackAt = ops.IndexOf("set.wrapper.field");
            TestHarness.CheckTrue("求值序：get.wrapper 先于 side()",
                getWrapperAt >= 0 && sideAt > getWrapperAt);
            TestHarness.CheckTrue("求值序：side() 先于叶 set.field",
                sideAt >= 0 && setLeafAt > sideAt);
            TestHarness.CheckTrue("求值序：叶写先于 set.wrapper.field 写回",
                setLeafAt >= 0 && writebackAt > setLeafAt);
        }

        // ===== 混合边界：值/引用中间停止点锁定 =====
        private static void TestWrapperDeepWriteMixedBoundary()
        {
            // W.a(value).b(ref).x：引用中间停止，无写回
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub rich struct MidVal {\n" +
                "    pub var b: Node\n" +
                "    pub init(n: Node) { b = n }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Box {\n" +
                "    pub var a: MidVal\n" +
                "    pub init(_ -> a)\n" +
                "}\n" +
                "@Box\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Box.a.b.x = 1\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（值→引用边界）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（值→引用边界）", module);
            BilTestHarness.CheckFnShape("W.a(value).b(ref).x（引用停止无写回）",
                module, "$f(s:Service)@.void",
                ".vars { Box .s0, MidVal .s1, Node .s2, Box .t0, MidVal .t1, " +
                "Node .t2, .i32 .t3 }\n" +
                "get.wrapper $s type(Box) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Box#a@MidVal)\n" +
                "set.var $.t1 $.s1\n" +
                "get.field $.s1 $.t2 field(MidVal#b@Node)\n" +
                "set.var $.t2 $.s2\n" +
                "load res(#0) $.t3\n" +
                "set.field $.t3 $.s2 field(Node#x@.i32)\n" +
                "ret\n");

            // W.a(ref).b(value).x：值中间写回 a，引用 a 停止（无 set.wrapper.field）
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "pub struct Leaf {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub class MidRef {\n" +
                "    pub var b: Leaf\n" +
                "    pub init(l: Leaf) { b = l }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Box {\n" +
                "    pub var a: MidRef\n" +
                "    pub init(_ -> a)\n" +
                "}\n" +
                "@Box\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Box.a.b.x = 1\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（引用→值边界）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（引用→值边界）", module2);
            BilTestHarness.CheckFnShape("W.a(ref).b(value).x（值写回 a，无 set.wrapper.field）",
                module2, "$f(s:Service)@.void",
                ".vars { Box .s0, MidRef .s1, Leaf .s2, Box .t0, MidRef .t1, " +
                "Leaf .t2, .i32 .t3 }\n" +
                "get.wrapper $s type(Box) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Box#a@MidRef)\n" +
                "set.var $.t1 $.s1\n" +
                "get.field $.s1 $.t2 field(MidRef#b@Leaf)\n" +
                "set.var $.t2 $.s2\n" +
                "load res(#0) $.t3\n" +
                "set.field $.t3 $.s2 field(Leaf#x@.i32)\n" +
                "set.field $.s2 $.s1 field(MidRef#b@Leaf)\n" +
                "ret\n");
        }

        // ===== 深层复合赋值 place.a.b += rhs =====
        private static void TestWrapperDeepCompoundAssignmentEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub struct Inner {\n" +
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
                "pub func f(s: Service): i32 {\n" +
                "    s:Logged.sub.x += 1\n" +
                "    return s:Logged.sub.x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（深层复合赋值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（深层复合赋值）", module);
            BilTestHarness.CheckFnShape(
                "深层复合 place.a.b += rhs（get→RHS→叶set→反向set）",
                module, "$f(s:Service)@.i32",
                ".vars { Logged .s0, Inner .s1, .i32 .s2, Logged .s3, " +
                "Logged .t0, Inner .t1, .i32 .t2, .i32 .t3, .i32 .t4, Logged .t5, " +
                "Inner .t6, .i32 .t7 }\n" +
                "get.wrapper $s type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "get.field $.s0 $.t1 field(Logged#sub@Inner)\n" +
                "set.var $.t1 $.s1\n" +
                "get.field $.s1 $.t2 field(Inner#x@.i32)\n" +
                "load res(#0) $.t3\n" +
                "add $.t2 $.t3 $.t4\n" +
                "set.var $.t4 $.s2\n" +
                "set.field $.s2 $.s1 field(Inner#x@.i32)\n" +
                "set.wrapper.field $.s1 $s wrapper(Logged) field(Logged#sub@Inner)\n" +
                "get.wrapper $s type(Logged) $.t5\n" +
                "set.var $.t5 $.s3\n" +
                "get.field $.s3 $.t6 field(Logged#sub@Inner)\n" +
                "get.field $.t6 $.t7 field(Inner#x@.i32)\n" +
                "ret $.t7\n");
        }

        // ===== 共享写路径宿主稳定性：可变普通字段 host 在 RHS 前物化一次 =====
        // h.service:W.sub.x = h.replaceAndReturn()——service 无 getter 但 RHS
        // 可能替换该字段；host 必须物化到 .sN，get.wrapper 与 set.wrapper.field
        // 写回共用同一 .sN，RHS 后不再 get.field Host#service。
        private static void TestWrapperSharedHostFieldStability()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub struct Inner {\n" +
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
                "pub class Host {\n" +
                "    pub var service: Service\n" +
                "    pub init(s: Service) { service = s }\n" +
                "    pub func replaceAndReturn(): i32 {\n" +
                "        service = new Service()\n" +
                "        return 9\n" +
                "    }\n" +
                "}\n" +
                "pub func f(h: Host) {\n" +
                "    h.service:Logged.sub.x = h.replaceAndReturn()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（共享宿主字段稳定性深写）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（共享宿主字段稳定性深写）", module);
            BilTestHarness.CheckFnShape(
                "可变字段 host 深写（RHS 前物化一次，wrapper/写回共用 .sN）",
                module, "$f(h:Host)@.void",
                ".vars { Service .s0, Logged .s1, Inner .s2, Service .t0, " +
                "Logged .t1, Inner .t2, .i32 .t3 }\n" +
                "get.field $h $.t0 field(Host#service@Service)\n" +
                "set.var $.t0 $.s0\n" +
                "get.wrapper $.s0 type(Logged) $.t1\n" +
                "set.var $.t1 $.s1\n" +
                "get.field $.s1 $.t2 field(Logged#sub@Inner)\n" +
                "set.var $.t2 $.s2\n" +
                "invoke fn(Host$replaceAndReturn()@.i32) $.t3 [$h]\n" +
                "set.field $.t3 $.s2 field(Inner#x@.i32)\n" +
                "set.wrapper.field $.s2 $.s0 wrapper(Logged) " +
                "field(Logged#sub@Inner)\n" +
                "ret\n");

            // 直接 wrapper 字段复合：同一宿主稳定性规则
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counted {\n" +
                "    pub var count: i32\n" +
                "    pub init(_ -> count)\n" +
                "}\n" +
                "@Counted\n" +
                "pub class Service { pub init() }\n" +
                "pub class Host {\n" +
                "    pub var service: Service\n" +
                "    pub init(s: Service) { service = s }\n" +
                "    pub func replaceAndReturn(): i32 {\n" +
                "        service = new Service()\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n" +
                "pub func f(h: Host) {\n" +
                "    h.service:Counted.count += h.replaceAndReturn()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（共享宿主字段稳定性复合）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（共享宿主字段稳定性复合）", module2);
            BilTestHarness.CheckFnShape(
                "可变字段 host 直接复合（RHS 前物化，读/写共用 .sN）",
                module2, "$f(h:Host)@.void",
                ".vars { Service .s0, Counted .s1, .i32 .s2, Service .t0, " +
                "Counted .t1, .i32 .t2, .i32 .t3, .i32 .t4 }\n" +
                "get.field $h $.t0 field(Host#service@Service)\n" +
                "set.var $.t0 $.s0\n" +
                "get.wrapper $.s0 type(Counted) $.t1\n" +
                "set.var $.t1 $.s1\n" +
                "get.field $.s1 $.t2 field(Counted#count@.i32)\n" +
                "invoke fn(Host$replaceAndReturn()@.i32) $.t3 [$h]\n" +
                "add $.t2 $.t3 $.t4\n" +
                "set.var $.t4 $.s2\n" +
                "set.wrapper.field $.s2 $.s0 wrapper(Counted) " +
                "field(Counted#count@.i32)\n" +
                "ret\n");
        }

        // ===== M79：param:W（泛型参数 with 约束）get.wrapper / set.wrapper.field 端到端 =====
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
            BilTestHarness.CheckFnShape("param:W 字段写（set.wrapper.field wrapper）",
                module2, "$g(param:.generic<$.generic.T>)@.void",
                ".vars { .string .t0 }\n" +
                "load res(#0) $.t0\n" +
                "set.wrapper.field $.t0 $param wrapper(Logged) " +
                "field(Logged#level@.string)\n" +
                "ret\n");
        }

        // ===== M88：specific proxy 模板 fn（wrapper-proxy + get.self/invoke fn(..inner)）=====
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

            BilTestHarness.CheckFnShape("specific 模板 fn（get.self + invoke fn(..inner)）",
                module, "Logged$$.proxy.doSomething(arg:.i32)@.string",
                ".vars { .generic<$.generic.TTarget> host, " +
                ".generic<$.generic.TTarget> .t0, .string .t1 }\n" +
                "get.self $.t0\n" +
                "set.var $.t0 $host\n" +
                "invoke fn(..inner) $.t1 [$arg]\n" +
                "ret $.t1\n");
            BilTestHarness.CheckFnShape("caller（invoke 原名，烘焙归 Middleware）",
                module, "$caller(s:Service)@.string",
                ".vars { .i32 .t0, .string .t1 }\n" +
                "load res(#0) $.t0\n" +
                "invoke fn(Service$doSomething(arg:.i32)@.string) $.t1 [$s, $.t0]\n" +
                "ret $.t1\n");
        }

        // ===== M88：wildcard proxy 模板 fn（invoke fn(..inner) 包转发）=====
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

            // #27⑦：invoke fn(..inner) 操作数 = 泛型包（声明序）前置 + 值包
            BilTestHarness.CheckFnShape("wildcard 模板 fn（invoke fn(..inner) 泛型包+值包转发）",
                module, "Audited$$.proxy.*(symbol:.string)@.generic<$.generic.TReturn>",
                ".vars { .generic<$.generic.TReturn> .t0 }\n" +
                "invoke fn(..inner) $.t0 [$.generic.TNamedArgs, $.generic.TUnnamedArgs, " +
                "$.kwargs.namedArgs, $.vargs.unnamedArgs]\n" +
                "ret $.t0\n");
        }

        private static void TestProxySpecificVariadicEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Batched\\<TTarget> {\n" +
                "    operator .proxy.sum(nums: i32...): i32 { return inner(nums) }\n" +
                "}\n" +
                "@Batched\n" +
                "pub class Values {\n" +
                "    pub init()\n" +
                "    pub func sum(nums: i32...): i32 { return 0 }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（specific variadic 模板）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（specific variadic 模板）", module);
            var proxy = module.Functions.Single(f => f.Symbol.Contains(".proxy.sum"));
            TestHarness.CheckTrue("specific variadic .args 声明值包",
                proxy.Args.Any(a => a.Name == ".vargs.nums"));
            var innerInvoke = proxy.Blocks.SelectMany(b => b.Instructions)
                .OfType<InvokeInstruction>().Single(i =>
                    i.Method.Symbol == BilSpellings.InnerReservedFunction);
            TestHarness.Check("specific variadic invoke fn(..inner) 整包转发", ".vargs.nums",
                innerInvoke.Arguments.Single().Name);
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
                "    return service.fetchUserById\\<i32, String>(42)\n" +
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
                    && r.LiteralText == "\"Service$fetchUserById<.i32,.string>(.i32)@.any\""));

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

        // ===== #28③：interface 传染宿主降级资格（传递闭包只读查询）=====
        private static void TestDowngradeViaInterface()
        {
            const string wildcardW =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n";

            // 直接 implements 带 .proxy.* 的 interface
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(wildcardW +
                    "@Audited\n" +
                    "pub interface IService { }\n" +
                    "@Audited\n" +
                    "pub class SvcImpl implements IService { pub init() }\n" +
                    "pub func f(s: SvcImpl): Any { return s.fetch(1) }\n");
                CheckNoErrors("全管线无诊断（#28③ 直接 implements）", unit);
                BilTestHarness.CheckBilValid("验证器零错误（#28③ 直接 implements）", module);
                TestHarness.CheckTrue("#28③ 实现者显式 wrapped 标记",
                    module.LocalSymbols.OfType<BilTypeDeclaration>()
                        .Single(t => t.Symbol == "SvcImpl")
                        .Modifiers.OfType<BilWrappedModifier>().Any(m => m.WrapperTypeRef == "Audited"));
                TestHarness.CheckTrue("#28③ 调用点 invoke Any.call???",
                    module.Functions.Single(fn => fn.Symbol.StartsWith("$f("))
                        .Blocks[0].Instructions.OfType<InvokeInstruction>()
                        .Any(i => i.Method.Symbol.StartsWith("core::Any$call???")));
            }

            // 接口继承传递闭包
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(wildcardW +
                    "@Audited\n" +
                    "pub interface IBase { }\n" +
                    "@Audited\n" +
                    "pub interface IChild : IBase { }\n" +
                    "@Audited\n" +
                    "pub class ViaChild implements IChild { pub init() }\n" +
                    "pub func f(s: ViaChild): Any { return s.remote() }\n");
                CheckNoErrors("全管线无诊断（#28③ 传递闭包）", unit);
                BilTestHarness.CheckBilValid("验证器零错误（#28③ 传递闭包）", module);
                TestHarness.CheckTrue("#28③ 传递闭包 invoke Any.call???",
                    module.Functions.Single(fn => fn.Symbol.StartsWith("$f("))
                        .Blocks[0].Instructions.OfType<InvokeInstruction>()
                        .Any(i => i.Method.Symbol.StartsWith("core::Any$call???")));
            }

            // 仅 specific / 无 wrapper 不误放行
            {
                var (unitSpec, _, _) = BilTestHarness.EmitBilUnit(
                    "@WrapperTarget(.Entity)\n" +
                    "pub wrapper SpecificOnly {\n" +
                    "    operator .proxy.known(): i32 { return inner() }\n" +
                    "}\n" +
                    "@SpecificOnly\n" +
                    "pub interface IKnown { func known(): i32 }\n" +
                    "pub class SpecImpl implements IKnown {\n" +
                    "    pub init()\n" +
                    "    pub func known(): i32 { return 1 }\n" +
                    "}\n" +
                    "pub func f(s: SpecImpl) { s.missing() }\n");
                TestHarness.CheckSemanticError("#28③ 仅 specific 不降级（P4）",
                    unitSpec.Diagnostics, "Undefined member");
            }
        }

        // ===== #28④：if?/throw/复合赋值 RHS/索引写 降级 + cast 物化 =====
        private static void TestDowngradeExemptionPositions()
        {
            const string fixture =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service { pub init() }\n" +
                "pub class User { pub init() }\n";

            // if? 右操作数：Any → User cast
            {
                var (unit, module, text) = BilTestHarness.EmitBilUnit(fixture +
                    "pub func f(u: User?, s: Service): User { return u if? s.fetch() }\n");
                CheckNoErrors("全管线无诊断（if? 降级）", unit);
                BilTestHarness.CheckBilValid("验证器零错误（if? 降级）", module);
                TestHarness.CheckTrue("if? else 支 cast User",
                    text.Contains("call???") && text.Contains("cast") && text.Contains("type(User)"));
            }

            // throw 操作数：Any → Exception cast
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(fixture +
                    "pub func f(s: Service) { throw s.err() }\n");
                CheckNoErrors("全管线无诊断（throw 降级）", unit);
                BilTestHarness.CheckBilValid("验证器零错误（throw 降级）", module);
                var fail = module.Functions.Single(f => f.Symbol.StartsWith("$f("));
                var hasCastToException = fail.Blocks[0].Instructions
                    .OfType<CastInstruction>()
                    .Any(c => c.TargetType.TypeRef.Contains("Exception"));
                TestHarness.CheckTrue("throw 前 cast Exception", hasCastToException);
                TestHarness.CheckTrue("throw 指令存在",
                    fail.Blocks[0].Instructions.OfType<ThrowInstruction>().Any());
            }

            // 复合赋值 RHS：Any → String cast 后 add
            {
                var (unit, module, text) = BilTestHarness.EmitBilUnit(fixture +
                    "pub func f(s: Service): String {\n" +
                    "    var t = \"a\"\n" +
                    "    t += s.suffix()\n" +
                    "    return t\n" +
                    "}\n");
                CheckNoErrors("全管线无诊断（复合赋值降级）", unit);
                BilTestHarness.CheckBilValid("验证器零错误（复合赋值降级）", module);
                TestHarness.CheckTrue("复合赋值 RHS cast + add",
                    text.Contains("call???") && text.Contains("add") && text.Contains("type(.string)"));
            }

            // 索引写值：Any → User cast 后 set.array
            {
                var (unit, module, text) = BilTestHarness.EmitBilUnit(fixture +
                    "pub class Bag {\n" +
                    "    pub var item: User\n" +
                    "    pub init(_ -> item)\n" +
                    "    pub operator getAtIndex(index: i32): User { return item }\n" +
                    "    pub operator setAtIndex(index: i32, element: User) { item = element }\n" +
                    "}\n" +
                    "pub func f(b: Bag, s: Service) { b[0] = s.fetch() }\n");
                CheckNoErrors("全管线无诊断（索引写降级）", unit);
                BilTestHarness.CheckBilValid("验证器零错误（索引写降级）", module);
                TestHarness.CheckTrue("索引写 cast User + set.array",
                    text.Contains("call???") && text.Contains("set.array")
                    && text.Contains("type(User)"));
            }
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
