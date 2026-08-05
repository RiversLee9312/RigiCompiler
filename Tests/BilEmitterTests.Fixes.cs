using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    // BilEmitter（P4b）修复批次回归测试：
    // - Nullable\<泛型参数\> 的 null 资源按 §7.5 canonical（.generic<$.generic.T>）
    //   投影登记（修复前一律误报 P4 Error 并跳过函数体）
    // - variadic 参数写入映射隐藏包变量（set.var $.vargs./$.kwargs.——
    //   修复前写原名，引用未声明变量）
    // - 具名包结果类型 = .array<core::Pair<.string, .any>>（与 .kwargs 契约
    //   同元素类型）
    // - variadic 参数索引访问的装箱/拆箱 cast 物化（BIL §7.1 ABI ↔ P3
    //   体内视角桥接——修复前 §21.3 按容器声明推元素期望全拒）
    // - ext 字段声明带 ext 修饰符（§8.3）
    // - const/var 字段修饰符发射（§8.3 表序）+ init 内写 const 字段
    //   端到端（§21.8 init 豁免配套）
    public static partial class BilEmitterTests
    {
        // ===== Nullable\<T\>（T 泛型参数）null 资源 §7.5 投影 =====
        private static void TestGenericNullableNullResource()
        {
            // T? 局部初始化 null 字面量（修复前 RegisterNullResource 误判
            // 「not typed as Nullable<T>」并跳过函数体）+ 嵌套泛型转发
            // （.generic.T 转发路径上的 T? 返回类型）+ 静态实参对照。
            // （if? / ?. 在 T? 上另有 P3 侧 gate——nullable 判定不认泛型
            // 参数，Semantic/ 修复归另一批次，此处覆盖 P4 可到达形态）
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func setNull\\<T>(): T? {\n" +
                "    var y: T? = null\n" +
                "    return y\n" +
                "}\n" +
                "func forward\\<T>(): T? {\n" +
                "    return setNull\\<T>()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = setNull\\<i32>()\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（Nullable<泛型参数>）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（Nullable<泛型参数>）", module);

            // null 资源键 = §7.5 canonical（.generic<$.generic.T> 形态）
            TestHarness.CheckTrue("null 资源按 §7.5 泛型投影登记",
                module.Resources.Any(r => r is BilNullResource n
                    && n.TypeRef == ".generic<$.generic.T>"));

            // .vars 的 T? 局部声明 = .nullable<.generic<$.generic.T>>
            var setNullFn = module.Functions.Single(f => f.Symbol
                == "$setNull()@.nullable<.generic<$.generic.T>>");
            TestHarness.CheckTrue("setNull 的 .vars 含 .nullable<.generic<$.generic.T>> 条目",
                setNullFn.Vars.Any(v => v.TypeRef == ".nullable<.generic<$.generic.T>>"));
        }

        // ===== variadic 参数写入映射隐藏包变量 =====
        private static void TestVarArgsParameterAssignment()
        {
            // nums = nums：读侧已映射 .vargs.nums，写侧修复前用原名
            // （.args/.vars 均无该名）——验证器报未声明变量
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func f(nums: i32...) {\n" +
                "    nums = nums\n" +
                "}\n" +
                "func g(options: named String...) {\n" +
                "    options = options\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（variadic 参数赋值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（variadic 参数赋值）", module);
            BilTestHarness.CheckFnShape("variadic 写入映射 .vargs（f 指令）",
                module, "$f()@.void",
                ".vars {  }\n" +
                "set.var $.vargs.nums $.vargs.nums\n" +
                "ret\n");
            BilTestHarness.CheckFnShape("具名 variadic 写入映射 .kwargs（g 指令）",
                module, "$g()@.void",
                ".vars {  }\n" +
                "set.var $.kwargs.options $.kwargs.options\n" +
                "ret\n");
        }

        // ===== 具名包结果类型与 .kwargs 契约同元素类型 =====
        private static void TestNamedPackResultType()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func config(options: named String...): Any { return options }\n" +
                "pub func main() {\n" +
                "    var c = config(name = \"latte\")\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（具名包结果类型）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（具名包结果类型）", module);
            // 打包结果临时变量的 .vars 类型 = .array<core::Pair<.string, .any>>
            // （core::Pair 非内建，canonical 投影而非 .pair 构造头别名）
            var mainFn = module.Functions.Single(f => f.Symbol == "$main()@.void");
            TestHarness.CheckTrue("具名包结果 .vars 类型 = .array<core::Pair<.string, .any>>",
                mainFn.Vars.Any(v => v.TypeRef == ".array<core::Pair<.string, .any>>"));
        }

        // ===== variadic 参数索引访问装箱/拆箱（BIL §7.1 ABI ↔ P3 视角）=====
        private static void TestVarArgsIndexBoxingEmission()
        {
            // vargs 读+写：容器 .vargs.nums 声明 .array<.any>，P3 体内
            // 元素类型 i32——读位置 get.array 结果临时 .any 后 cast 拆箱
            // 到 .i32，写位置元素 cast 装箱到 .any 再 set.array
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func sum(nums: i32...): i32 {\n" +
                "    var first = nums[0]\n" +
                "    nums[1] = first\n" +
                "    return nums[1]\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（vargs 索引读写）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（vargs 索引读写）", module);
            BilTestHarness.CheckFnShape("vargs 索引读写（拆箱/装箱 cast）",
                module, "$sum()@.i32",
                ".vars { .i32 first, .i32 .t0, .any .t1, .i32 .t2, .i32 .t3, " +
                ".any .t4, .i32 .t5, .any .t6, .i32 .t7 }\n" +
                "load res(#0) $.t0\n" +
                "get.array $.vargs.nums $.t0 $.t1\n" +
                "cast $.t1 $.t2 type(.i32)\n" +
                "set.var $.t2 $first\n" +
                "load res(#1) $.t3\n" +
                "cast $first $.t4 type(.any)\n" +
                "set.array $.vargs.nums $.t3 $.t4\n" +
                "load res(#1) $.t5\n" +
                "get.array $.vargs.nums $.t5 $.t6\n" +
                "cast $.t6 $.t7 type(.i32)\n" +
                "ret $.t7\n");

            // kwargs 读：.kwargs.options 元素 .pair<.string, .any>——
            // get.array 结果临时 core::Pair<.string, .any>（canonical 投影，
            // 别名与容器声明对齐）后 cast 拆箱到 P3 元素 Pair<String, String>，
            // 再经 get.field 取 key
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "func f(options: named String...): String {\n" +
                "    return options[0].key\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（kwargs 索引读）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（kwargs 索引读）", module2);
            BilTestHarness.CheckFnShape("kwargs 索引读（Pair 拆箱 cast）",
                module2, "$f()@.string",
                ".vars { .i32 .t0, core::Pair<.string, .any> .t1, " +
                "core::Pair<.string, .string> .t2, .string .t3 }\n" +
                "load res(#0) $.t0\n" +
                "get.array $.kwargs.options $.t0 $.t1\n" +
                "cast $.t1 $.t2 type(core::Pair<.string, .string>)\n" +
                "get.field $.t2 $.t3 field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "ret $.t3\n");

            // vargs 复合赋值：读侧 get.array .any + 拆箱 cast 参与运算、
            // 写侧结果装箱 cast 到 .any 后 set.array（单次求值脱糖贯通）
            var (unit3, module3, _) = BilTestHarness.EmitBilUnit(
                "func bump(nums: i32...) {\n" +
                "    nums[0] += 1\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（vargs 索引复合赋值）", unit3);
            BilTestHarness.CheckBilValid("验证器零错误（vargs 索引复合赋值）", module3);
            BilTestHarness.CheckFnShape("vargs 索引复合赋值（读拆箱/写装箱）",
                module3, "$bump()@.void",
                ".vars { .i32 .t0, .i32 .t1, .any .t2, .i32 .t3, .i32 .t4, " +
                ".i32 .t5, .any .t6, .i32 .t7, .any .t8, .i32 .t9 }\n" +
                "load res(#0) $.t0\n" +
                "load res(#0) $.t1\n" +
                "get.array $.vargs.nums $.t1 $.t2\n" +
                "cast $.t2 $.t3 type(.i32)\n" +
                "load res(#1) $.t4\n" +
                "add $.t3 $.t4 $.t5\n" +
                "cast $.t5 $.t6 type(.any)\n" +
                "set.array $.vargs.nums $.t0 $.t6\n" +
                "load res(#0) $.t7\n" +
                "get.array $.vargs.nums $.t7 $.t8\n" +
                "cast $.t8 $.t9 type(.i32)\n" +
                "ret\n");

            // 模型断言：get.array 结果临时按 ABI 元素类型登记（.vars）
            var sumFn = module.Functions.Single(f => f.Symbol == "$sum()@.i32");
            TestHarness.CheckTrue("vargs get.array 结果临时类型 .any",
                sumFn.Vars.Any(v => v.TypeRef == ".any"));
            var fFn = module2.Functions.Single(f => f.Symbol == "$f()@.string");
            TestHarness.CheckTrue("kwargs get.array 结果临时类型 core::Pair<.string, .any>",
                fFn.Vars.Any(v => v.TypeRef == "core::Pair<.string, .any>"));
        }

        // ===== ext 字段声明带 ext 修饰符（§8.3）=====
        private static void TestExtFieldDeclarationModifier()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Local { }\n" +
                "ext var Local.hook: i32\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("全管线无诊断（ext 字段声明）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（ext 字段声明）", module);
            TestHarness.CheckTrue("ext 字段声明带 ext 修饰符",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "Local")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Kind == BilMemberKind.Field
                        && d.Symbol == "Local#hook@.i32"
                        && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })));
        }

        // ===== const/var 字段修饰符发射（§8.3）+ init 豁免端到端 =====
        private static void TestConstFieldModifierEmission()
        {
            // 本类 const 字段 + 手写 init 体赋值（P3 init 豁免，
            // ConstFieldRules）——继承形态（Entry : core.Pair 手写 init 写
            // key/value）由 Values 套件解构用例覆盖，此处锁定本类形态
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "class Config {\n" +
                "    pub const name: String\n" +
                "    pub var size: i32\n" +
                "    pub init(n: String) {\n        name = n\n    }\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("全管线无诊断（const 字段 init 写入）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（const 字段 init 写入）", module);

            // 模型断言：const/var 修饰符按 §8.3 表序紧随访问级之后
            var config = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Config");
            var nameField = config.Members.OfType<BilSimpleMemberDeclaration>()
                .Single(d => d.Symbol == "Config#name@.string");
            var sizeField = config.Members.OfType<BilSimpleMemberDeclaration>()
                .Single(d => d.Symbol == "Config#size@.i32");
            TestHarness.CheckTrue("const 字段声明带 const 修饰符（紧随访问级）",
                nameField.Modifiers.Count == 2
                && nameField.Modifiers[1] is BilKeywordModifier { Keyword: BilKeyword.Const });
            TestHarness.CheckTrue("var 字段声明带 var 修饰符（紧随访问级）",
                sizeField.Modifiers.Count == 2
                && sizeField.Modifiers[1] is BilKeywordModifier { Keyword: BilKeyword.Var });

            // 文本断言（BilWriter 输出契约）：声明行渲染含 const/var
            TestHarness.CheckTrue("声明行渲染 .field Config#name@.string pub const",
                text.Contains(".field Config#name@.string pub const"));
            TestHarness.CheckTrue("声明行渲染 .field Config#size@.i32 pub var",
                text.Contains(".field Config#size@.i32 pub var"));
        }

        // ===== 收窄区域内复合赋值端到端（P4b place 剥壳修复）=====
        private static void TestSmartCastCompoundAssignmentEmission()
        {
            // P3 复合赋值 Target 保留 SmartCast 包装（收窄类型参与运算
            // 定型），P4a 物化为 LoweredCastExpression 包 place——修复前
            // P4b place switch 落 default 抛「非法赋值目标」
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func f(s: String?): String? {\n" +
                "    if (s != null) {\n" +
                "        s += \"b\"\n" +
                "    }\n" +
                "    return s\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = f(\"a\")\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（收窄区域内复合赋值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（收窄区域内复合赋值）", module);

            // 剥壳后写原 place：set.var $s（参数 s 原变量，非合成临时）
            var fn = module.Functions.Single(f => f.Symbol
                == "$f(s:.nullable<.string>)@.nullable<.string>");
            TestHarness.CheckTrue("复合赋值写回原 place（set.var $s）",
                fn.Blocks.SelectMany(b => b.Instructions)
                    .OfType<SetVarInstruction>()
                    .Any(i => i.Target.Name == "s"));
        }
    }
}
