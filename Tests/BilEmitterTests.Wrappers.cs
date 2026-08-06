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
    }
}
