using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    // BilEmitter 的 S8e 部分：访问器声明发射（§8.3 字段形态标记 + §8.4
    // getter(FIELD)/setter(FIELD) 声明，字段槽驱动）与 override/abstract
    // 投影（§8.2/§8.4 关键字修饰）。

    public static partial class BilEmitterTests
    {
        // ===== S8e：访问器声明发射（类 backing 访问器 + 全局自动访问器 +
        // computed 形态标记；表达式/语句发射引用逻辑字段零改动）=====
        private static void TestAccessorEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "namespace app\n" +
                "pub var height: i32 {\n" +
                "    pub get\n" +
                "    pub set\n" +
                "} = 200\n" +
                "pub class Counter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) {\n" +
                "            return value\n" +
                "        }\n" +
                "        priv set(value: _) {\n" +
                "        }\n" +
                "    }\n" +
                "    pub init() { value = 0 }\n" +
                "}\n" +
                "pub class Ratio {\n" +
                "    pub var raw: i32\n" +
                "    pub var doubled: i32 {\n" +
                "        pub get(_: _) {\n" +
                "            return raw + raw\n" +
                "        }\n" +
                "    }\n" +
                "    pub init(r: i32) { raw = r }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter()\n" +
                "    return (c.value + height)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（访问器发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（访问器发射）", module);

            // 字段声明形态标记（§8.3：backing/computed → readable →
            // writable → compiler-generated，访问级全显式）
            var counterMembers = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol == "app::Counter")
                .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                .ToList();
            TestHarness.CheckTrue("backing 字段行（backing readable writable compiler-generated）",
                counterMembers.Any(d => d.Kind == BilMemberKind.Field
                    && d.Symbol == "app::Counter#value@.i32"
                    && d.Modifiers.Any(m => m is BilAccessibilityModifier
                        { Accessibility: BilAccessibility.Public })
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Backing })
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Readable })
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Writable })
                    && d.Modifiers.Any(m => m is BilKeywordModifier
                        { Keyword: BilKeyword.CompilerGenerated })));

            // 访问器声明由字段槽驱动：字段声明后按 get→set 顺序紧跟
            var fieldIndex = counterMembers.FindIndex(d => d.Symbol == "app::Counter#value@.i32");
            TestHarness.CheckTrue("getter/setter 声明紧跟字段（get→set 顺序）",
                fieldIndex >= 0 && counterMembers.Count > fieldIndex + 2
                && counterMembers[fieldIndex + 1].Symbol == "app::Counter$.get.value@.i32"
                && counterMembers[fieldIndex + 2].Symbol == "app::Counter$.set.value@.i32");
            TestHarness.CheckTrue("getter 声明形态（getter(FIELD) + pub）",
                counterMembers.Any(d => d.Kind == BilMemberKind.Method
                    && d.Symbol == "app::Counter$.get.value@.i32"
                    && d.Modifiers.Any(m => m is BilAccessorModifier
                        { Kind: BilAccessorKind.Getter } a && a.FieldSymbol == "app::Counter#value@.i32")
                    && d.Modifiers.Any(m => m is BilAccessibilityModifier
                        { Accessibility: BilAccessibility.Public })));
            TestHarness.CheckTrue("setter 声明形态（setter(FIELD) + priv 落定）",
                counterMembers.Any(d => d.Kind == BilMemberKind.Method
                    && d.Symbol == "app::Counter$.set.value@.i32"
                    && d.Modifiers.Any(m => m is BilAccessorModifier
                        { Kind: BilAccessorKind.Setter } a && a.FieldSymbol == "app::Counter#value@.i32")
                    && d.Modifiers.Any(m => m is BilAccessibilityModifier
                        { Accessibility: BilAccessibility.Private })));

            // 全局自动访问器：§8.4.1 段内裸条目（不包裹在 .type 中）
            TestHarness.CheckTrue("全局访问器字段裸条目（backing readable writable compiler-generated）",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Field
                    && d.Symbol == "app::#height@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Backing })
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Readable })
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Writable })
                    && d.Modifiers.Any(m => m is BilKeywordModifier
                        { Keyword: BilKeyword.CompilerGenerated })));
            TestHarness.CheckTrue("全局 getter/setter 裸条目（getter(FIELD)/setter(FIELD)）",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Method
                    && d.Symbol == "app::$.get.height@.i32"
                    && d.Modifiers.Any(m => m is BilAccessorModifier
                        { Kind: BilAccessorKind.Getter } a && a.FieldSymbol == "app::#height@.i32"))
                && module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Method
                    && d.Symbol == "app::$.set.height@.i32"
                    && d.Modifiers.Any(m => m is BilAccessorModifier
                        { Kind: BilAccessorKind.Setter } a && a.FieldSymbol == "app::#height@.i32")));

            // computed 字段行：computed readable（无 writable、无 compiler-generated、
            // 无 setter 声明）
            var ratioMembers = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol == "app::Ratio")
                .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                .ToList();
            TestHarness.CheckTrue("computed 字段行（computed readable，无 writable/compiler-generated）",
                ratioMembers.Any(d => d.Kind == BilMemberKind.Field
                    && d.Symbol == "app::Ratio#doubled@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Computed })
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Readable })
                    && !d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Writable })
                    && !d.Modifiers.Any(m => m is BilKeywordModifier
                        { Keyword: BilKeyword.CompilerGenerated })));
            TestHarness.CheckTrue("computed 仅 getter 声明（无 setter）",
                ratioMembers.Any(d => d.Symbol == "app::Ratio$.get.doubled@.i32")
                && !ratioMembers.Any(d => d.Symbol.Contains("set.doubled")));

            // 资源形状（§18.1 标量去重：init 的 i32 0 与 stdlib 基线同键）
            BilTestHarness.CheckResShape("资源（访问器样例）", module,
                "#0 = string \"\\n\"\n#1 = i32 0\n#2 = bool false\n#3 = i32 1\n#4 = bool true");

            // fn 形状黄金：backing getter/setter（setter 体首隐含赋值合成）
            BilTestHarness.CheckFnShape("backing getter fn（value → get.field）",
                module, "app::Counter$.get.value@.i32",
                ".vars { .i32 .t0 }\n" +
                "get.field $.this $.t0 field(app::Counter#value@.i32)\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("backing setter fn（体首隐含 backing = value）",
                module, "app::Counter$.set.value@.i32",
                ".vars {  }\n" +
                "set.field $value $.this field(app::Counter#value@.i32)\n" +
                "ret\n");
            // fn 形状黄金：全局自动访问器（§13.4 静态投影 get/set.field.static）
            BilTestHarness.CheckFnShape("全局自动 getter fn（合成 return value）",
                module, "app::$.get.height@.i32",
                ".vars { .i32 .t0 }\n" +
                "get.field.static $.t0 type(app) field(app::#height@.i32)\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("全局自动 setter fn（仅隐含赋值）",
                module, "app::$.set.height@.i32",
                ".vars {  }\n" +
                "set.field.static $value type(app) field(app::#height@.i32)\n" +
                "ret\n");
            // 读写使用点发射引用逻辑字段（§8.3：get.field/set.field 始终引用
            // 逻辑字段，表达式/语句发射零改动）
            BilTestHarness.CheckFnShape("main 指令（访问器字段读 = 逻辑字段 get.field）",
                module, "app::$main()@.i32",
                ".vars { app::Counter c, app::Counter .t0, .i32 .t1, .i32 .t2, .i32 .t3 }\n" +
                "new type(app::Counter) $.t0 []\n" +
                "set.var $.t0 $c\n" +
                "get.field $c $.t1 field(app::Counter#value@.i32)\n" +
                "get.field.static $.t2 type(app) field(app::#height@.i32)\n" +
                "add $.t1 $.t2 $.t3\n" +
                "ret $.t3\n");
        }

        // ===== S8e：override/abstract 投影（§8.2 类型修饰 + §8.4 方法
        // 关键字修饰；abstract 方法无 fn 定义）=====
        private static void TestOverrideProjection()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "namespace shapes\n" +
                "pub open class Base {\n" +
                "    pub open func area(): i32 { return 0 }\n" +
                "}\n" +
                "pub class Square : Base {\n" +
                "    pub override func area(): i32 { return 1 }\n" +
                "}\n" +
                "pub abstract class Concept {\n" +
                "    pub abstract func id(): i32\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("全管线无诊断（override 投影）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（override 投影）", module);

            // open 类型修饰投影（§8.2）
            TestHarness.CheckTrue("open class 类型声明（pub open）",
                module.LocalSymbols.OfType<BilTypeDeclaration>().Any(t =>
                    t.Symbol == "shapes::Base" && t.Kind == BilTypeKind.Class
                    && t.Modifiers.Any(m => m is BilAccessibilityModifier
                        { Accessibility: BilAccessibility.Public })
                    && t.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Open })));
            // override 方法投影（§8.4）；基类 open 方法不携带 override
            var squareMembers = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol == "shapes::Square")
                .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>());
            TestHarness.CheckTrue("override 方法声明（pub override）",
                squareMembers.Any(d => d.Symbol == "shapes::Square$area()@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Override })
                    && d.Modifiers.Any(m => m is BilAccessibilityModifier
                        { Accessibility: BilAccessibility.Public })));
            var baseMembers = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol == "shapes::Base")
                .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>());
            TestHarness.CheckTrue("基类 open 方法无 override 修饰",
                baseMembers.Any(d => d.Symbol == "shapes::Base$area()@.i32"
                    && !d.Modifiers.Any(m => m is BilKeywordModifier
                        { Keyword: BilKeyword.Override })));
            // abstract：类型与方法双侧投影；abstract 方法无 fn 定义
            TestHarness.CheckTrue("abstract class 类型声明（pub abstract）",
                module.LocalSymbols.OfType<BilTypeDeclaration>().Any(t =>
                    t.Symbol == "shapes::Concept"
                    && t.Modifiers.Any(m => m is BilKeywordModifier
                        { Keyword: BilKeyword.Abstract })));
            TestHarness.CheckTrue("abstract 方法声明（pub abstract）",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "shapes::Concept")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Symbol == "shapes::Concept$id()@.i32"
                        && d.Modifiers.Any(m => m is BilKeywordModifier
                            { Keyword: BilKeyword.Abstract })));
            TestHarness.CheckTrue("abstract 方法无 fn 定义",
                !module.Functions.Any(f => f.Symbol.Contains("Concept$id")));
        }
    }
}
