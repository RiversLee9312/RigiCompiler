using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    // BilEmitter（P4b）ext 收尾端到端样例（M80，SYNTAX §4.4 + BIL §8.3/§8.4/
    // §8.4.1/§7.3）——勾销技术债 #22④（ext 字段 + 访问器路径已通无端到端
    // 样例），并锁定 M80 修复（内建类型 ext 字段 + 访问器的声明发射）。
    // 覆盖：ext 实例字段读写与实例方法调用（用户类型）、ext 字段 + backing
    // 访问器（用户类型）、ext 字段 + computed 访问器（内建 String——SYNTAX
    // §4.4 原文示例形态）、ext static 字段/常量/方法、ext 字段复合赋值。
    public static partial class BilEmitterTests
    {
        // ===== ext 实例字段读写 + 实例方法调用（用户类型）=====
        private static void TestExtInstanceFieldAndMethodEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "}\n" +
                "pub ext var Counter.extra: i32\n" +
                "pub ext func Counter.twice(): i32 { return (this.value * 2) }\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter(3)\n" +
                "    c.extra = 5\n" +
                "    var sum = (c.extra + c.twice())\n" +
                "    return sum\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（ext 实例字段/方法）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（ext 实例字段/方法）", module);

            // ext 成员声明在目标 .type 内（§8.3/§8.4），带 ext 修饰符
            var counter = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Counter");
            TestHarness.CheckTrue("ext 字段声明（目标 .type 内 + ext 修饰符）",
                counter.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Field && d.Symbol == "Counter#extra@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })));
            TestHarness.CheckTrue("ext 方法声明（ext 修饰符）",
                counter.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Method && d.Symbol == "Counter$twice()@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })));

            // ext 字段读写 = 普通 get.field/set.field（§13.3）；ext 方法
            // 调用的 receiver 是 .this 首参（§7.3）
            BilTestHarness.CheckFnShape("ext 字段读写 + ext 方法调用（main 指令）",
                module, "$main()@.i32",
                ".vars { Counter c, .i32 sum, .i32 .t0, Counter .t1, .i32 .t2, " +
                ".i32 .t3, .i32 .t4, .i32 .t5 }\n" +
                "load res(#0) $.t0\n" +
                "new type(Counter) $.t1 [$.t0]\n" +
                "set.var $.t1 $c\n" +
                "load res(#1) $.t2\n" +
                "set.field $.t2 $c field(Counter#extra@.i32)\n" +
                "get.field $c $.t3 field(Counter#extra@.i32)\n" +
                "invoke fn(Counter$twice()@.i32) $.t4 [$c]\n" +
                "add $.t3 $.t4 $.t5\n" +
                "set.var $.t5 $sum\n" +
                "ret $sum\n");
            BilTestHarness.CheckFnShape("ext 方法 fn 的 .args/.this 与指令",
                module, "Counter$twice()@.i32",
                ".vars { .i32 .t0, .i32 .t1, .i32 .t2 }\n" +
                "get.field $.this $.t0 field(Counter#value@.i32)\n" +
                "load res(#0) $.t1\n" +
                "mul $.t0 $.t1 $.t2\n" +
                "ret $.t2\n");
        }

        // ===== ext 字段 + backing 访问器（用户类型）=====
        private static void TestExtAccessorEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Meter {\n" +
                "    pub var raw: i32\n" +
                "    pub init(r: i32) { raw = r }\n" +
                "}\n" +
                "pub ext var Meter.km: i32 {\n" +
                "    get(value: _) { return value }\n" +
                "    set(value: _) { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Meter(7)\n" +
                "    m.km = 9\n" +
                "    return m.km\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（ext 访问器）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（ext 访问器）", module);

            // 声明形态（§8.3/§8.4）：字段 backing/readable/writable/
            // compiler-generated + getter(FIELD)/setter(FIELD) 字段槽驱动
            var meter = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Meter");
            var kmField = meter.Members.OfType<BilSimpleMemberDeclaration>()
                .Single(d => d.Symbol == "Meter#km@.i32");
            TestHarness.CheckTrue("ext 字段声明带 backing/readable/writable/compiler-generated",
                kmField.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })
                && meter.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "Meter$.get.km@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })
                    && d.Modifiers.OfType<BilAccessorModifier>().Any(a =>
                        a.Kind == BilAccessorKind.Getter && a.FieldSymbol == "Meter#km@.i32"))
                && meter.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "Meter$.set.km@.i32"
                    && d.Modifiers.OfType<BilAccessorModifier>().Any(a =>
                        a.Kind == BilAccessorKind.Setter && a.FieldSymbol == "Meter#km@.i32")));

            // 访问器 fn：value 别名 backing（getter 读 / setter 隐含写入）
            BilTestHarness.CheckFnShape("ext getter fn（value 别名读 backing）",
                module, "Meter$.get.km@.i32",
                ".vars { .i32 .t0 }\n" +
                "get.field $.this $.t0 field(Meter#km@.i32)\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("ext setter fn（隐含 backing = value）",
                module, "Meter$.set.km@.i32",
                ".vars {  }\n" +
                "set.field $value $.this field(Meter#km@.i32)\n" +
                "ret\n");
            // 使用点读写 = get.field/set.field（访问器派发是 BIL §13.3 语义）
            BilTestHarness.CheckFnShape("ext 访问器使用点（main 指令）",
                module, "$main()@.i32",
                ".vars { Meter m, .i32 .t0, Meter .t1, .i32 .t2, .i32 .t3 }\n" +
                "load res(#0) $.t0\n" +
                "new type(Meter) $.t1 [$.t0]\n" +
                "set.var $.t1 $m\n" +
                "load res(#1) $.t2\n" +
                "set.field $.t2 $m field(Meter#km@.i32)\n" +
                "get.field $m $.t3 field(Meter#km@.i32)\n" +
                "ret $.t3\n");
        }

        // ===== ext 字段 + computed 访问器（内建 String，M80 修复锁定）=====
        private static void TestExtBuiltinAccessorEmission()
        {
            // SYNTAX §4.4 原文示例形态：修复前访问器声明缺失，fn 定义被
            // §21.2 拒绝不落盘
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub ext var String.isEmpty: bool {\n" +
                "    get(_: _) { return true }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = \"abc\".isEmpty\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（内建 ext 访问器）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（内建 ext 访问器）", module);

            // §8.4.1 裸条目：字段 + getter 声明（M80 修复后随字段随迁）
            TestHarness.CheckTrue("内建 ext 字段裸条目（ext computed readable）",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Field && d.Symbol == "core::String#isEmpty@.bool"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })));
            TestHarness.CheckTrue("内建 ext getter 声明随迁（getter(FIELD) + ext）",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "core::String$.get.isEmpty@.bool"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })
                    && d.Modifiers.OfType<BilAccessorModifier>().Any(a =>
                        a.Kind == BilAccessorKind.Getter
                        && a.FieldSymbol == "core::String#isEmpty@.bool")));
            BilTestHarness.CheckFnShape("内建 ext getter fn（computed 体）",
                module, "core::String$.get.isEmpty@.bool",
                ".vars { .bool .t0 }\n" +
                "load res(#0) $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("内建 ext 字段使用点（main 指令）",
                module, "$main()@.i32",
                ".vars { .bool b, .string .t0, .bool .t1, .i32 .t2 }\n" +
                "load res(#0) $.t0\n" +
                "get.field $.t0 $.t1 field(core::String#isEmpty@.bool)\n" +
                "set.var $.t1 $b\n" +
                "load res(#1) $.t2\n" +
                "ret $.t2\n");
        }

        // ===== ext static 字段/常量/方法 =====
        private static void TestExtStaticEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Config {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub ext static var Config.defaultSize: i32\n" +
                "pub ext static const Config.maxSize: i32\n" +
                "pub ext static func Config.makeDefault(): i32 { return 1 }\n" +
                "pub func main(): i32 {\n" +
                "    Config.defaultSize = 4\n" +
                "    var a = Config.defaultSize\n" +
                "    var b = Config.maxSize\n" +
                "    return (a + (b + Config.makeDefault()))\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（ext static）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（ext static）", module);

            // 声明形态：.static-field/.static-method + .static. canonical + ext
            var config = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Config");
            TestHarness.CheckTrue("ext static 字段声明（.static-field + ext）",
                config.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.StaticField
                    && d.Symbol == "Config#.static.defaultSize@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext }))
                && config.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.StaticField
                    && d.Symbol == "Config#.static.maxSize@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Const })
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext }))
                && config.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.StaticMethod
                    && d.Symbol == "Config$.static.makeDefault()@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })));

            // 读写 get/set.field.static（§13.4）；静态调用无 receiver
            BilTestHarness.CheckFnShape("ext static 读写与调用（main 指令）",
                module, "$main()@.i32",
                ".vars { .i32 a, .i32 b, .i32 .t0, .i32 .t1, .i32 .t2, .i32 .t3, " +
                ".i32 .t4, .i32 .t5 }\n" +
                "load res(#0) $.t0\n" +
                "set.field.static $.t0 type(Config) field(Config#.static.defaultSize@.i32)\n" +
                "get.field.static $.t1 type(Config) field(Config#.static.defaultSize@.i32)\n" +
                "set.var $.t1 $a\n" +
                "get.field.static $.t2 type(Config) field(Config#.static.maxSize@.i32)\n" +
                "set.var $.t2 $b\n" +
                "invoke fn(Config$.static.makeDefault()@.i32) $.t3 []\n" +
                "add $b $.t3 $.t4\n" +
                "add $a $.t4 $.t5\n" +
                "ret $.t5\n");
        }

        // ===== ext 字段复合赋值（单次求值脱糖）=====
        private static void TestExtCompoundAssignmentEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "}\n" +
                "pub ext var Counter.extra: i32\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter(3)\n" +
                "    c.extra += 1\n" +
                "    return c.extra\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（ext 字段复合赋值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（ext 字段复合赋值）", module);
            // receiver 是纯读取局部（IsSideEffectFree）→ 零物化；
            // get.field → add → set.field 同一 place
            BilTestHarness.CheckFnShape("ext 字段复合赋值（main 指令）",
                module, "$main()@.i32",
                ".vars { Counter c, .i32 .t0, Counter .t1, .i32 .t2, .i32 .t3, " +
                ".i32 .t4, .i32 .t5, .i32 .t6 }\n" +
                "load res(#0) $.t0\n" +
                "new type(Counter) $.t1 [$.t0]\n" +
                "set.var $.t1 $c\n" +
                "get.field $c $.t2 field(Counter#extra@.i32)\n" +
                "load res(#1) $.t3\n" +
                "add $.t2 $.t3 $.t4\n" +
                "set.field $.t4 $c field(Counter#extra@.i32)\n" +
                "get.field $c $.t5 field(Counter#extra@.i32)\n" +
                "get.field $c $.t6 field(Counter#extra@.i32)\n" +
                "ret $.t6\n");
        }

        // ===== String.length 内建字段（bug17，.bootstrap.rg ext const i64）=====
        private static void TestStringLengthFieldEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var n = \"hello\".length\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（String.length）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（String.length）", module);

            // 内建 String 不进符号段，ext 字段按 §8.4.1 裸条目落地：
            // core::String#length@.i64 + pub/const/ext；无 backing 存储、
            // 无访问器（值由 VM get.field 直读，同 Array.length 通道）
            TestHarness.CheckTrue("String.length 裸条目（pub const ext i64）",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Field && d.Symbol == "core::String#length@.i64"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Const })
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })));
            TestHarness.CheckTrue("String.length 无访问器随迁",
                !module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol.Contains("length") && d.Symbol.Contains("$.get.")
                    && d.Symbol.StartsWith("core::String", StringComparison.Ordinal)));
            // 读取点 = 普通 get.field（§22.4 独立语义操作）
            BilTestHarness.CheckFnShape("String.length 读取点（main 指令）",
                module, "$main()@.i32",
                ".vars { .i64 n, .string .t0, .i64 .t1, .i32 .t2 }\n" +
                "load res(#0) $.t0\n" +
                "get.field $.t0 $.t1 field(core::String#length@.i64)\n" +
                "set.var $.t1 $n\n" +
                "load res(#1) $.t2\n" +
                "ret $.t2\n");
        }
    }
}
