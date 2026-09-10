using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
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
    // - 兄弟求值序保护（EvalOrderGuard）：receiver/左操作数等先求值槽位
    //   在后求值兄弟产前置语句时物化合成局部（修复前后置兄弟的短路前置
    // - 兄弟求值序保护（EvalOrderGuard）：receiver/左操作数等先求值槽位
    //   在后求值兄弟产前置语句时物化合成局部（修复前后置兄弟的短路前置
    //   先于前置兄弟执行——funcC 先于 funcA）
    // - bug O5：`?.` 调 void 方法（§3.4 + §15.1/§21.3）——then 块发
    //   invoke.noret（修复前恒走赋值管线发 invoke，BilVerifier §21.3 拒）
    public static partial class BilEmitterTests
    {
        // ===== bug O5：`?.` 调 void 方法 → invoke.noret =====
        private static void TestSafeAccessVoidCallEmission()
        {
            // 语句位 h?.bang()：SafeAccessRewriter 特判 void 实例调用，
            // then 块发 LoweredCallStatement（invoke.noret），s_result
            // 保持 null；receiver 为 null 时整体不调用
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "import core.io.Console\n" +
                "pub interface Hit { func bang() }\n" +
                "pub class Boom implements Hit {\n" +
                "    pub init()\n" +
                "    pub override func bang() { Console.println(\"bang\") }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h: Hit? = new Boom()\n" +
                "    h?.bang()\n" +
                "    h = null\n" +
                "    h?.bang()\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（?. 调 void 方法）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（?. 调 void 方法）", module);
            // 结构性事实：两处调用均 invoke.noret，无带结果 invoke 形态
            TestHarness.CheckTrue("两处 ?.void 调用均发 invoke.noret",
                System.Text.RegularExpressions.Regex.Matches(text,
                    System.Text.RegularExpressions.Regex.Escape(
                        "invoke.noret fn(Hit$bang()@.void)")).Count == 2
                && !text.Contains("invoke fn(Hit$bang"), text);
            // 端到端 VM：非空调用一次，null 不调用——stdout 恰一行 bang
            var runResult = BilVm.Run(module);
            TestHarness.CheckTrue("VM 运行无异常（?. 调 void 方法）",
                runResult.Exception == null, runResult.Exception?.ToString() ?? "");
            TestHarness.CheckTrue("VM stdout=bang 恰一次（null 不调用）",
                runResult.Stdout == "bang\n", runResult.Stdout);
            TestHarness.CheckTrue("VM 返回值 0（?. 调 void 方法）",
                runResult.ReturnValue is VmI32 n && n.Value == 0,
                runResult.ReturnValue?.ToStandardText() ?? "<null>");
        }

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
                "    var c = config(name = \"rigi\")\n" +
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
            // vargs 读+写（Q6）：容器 .vargs.nums 声明 .array<.any>，
            // get.array 结果临时 .nullable<.any>（内建数组读取恒可空），
            // 拆箱 cast 到 .nullable<.i32> 后 if? 解包；写位置元素 cast
            // 装箱到 .any 再 set.array
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func sum(nums: i32...): i32 {\n" +
                "    var first = nums[0] if? 0\n" +
                "    nums[1] = first\n" +
                "    return nums[1] if? 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（vargs 索引读写）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（vargs 索引读写）", module);
            BilTestHarness.CheckFnShape("vargs 索引读写（Q6 拆箱/装箱 cast）",
                module, "$sum()@.i32",
                ".vars { .i32 first, .nullable<.i32> .s0, .i32 .s1, .breakid .b0, " +
                ".nullable<.i32> .s2, .i32 .s3, .breakid .b1, .i32 .t0, " +
                ".nullable<.any> .t1, .nullable<.i32> .t2, .nullable<.i32> .t3, " +
                ".bool .t4, .i32 .t5, .i32 .t6, .i32 .t7, .any .t8, .i32 .t9, " +
                ".nullable<.any> .t10, .nullable<.i32> .t11, .nullable<.i32> .t12, " +
                ".bool .t13, .i32 .t14, .i32 .t15 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "get.array $.vargs.nums $.t0 $.t1\n" +
                "cast $.t1 $.t2 type(.nullable<.i32>)\n" +
                "set.var $.t2 $.s0\n" +
                "load res(#1) $.t3\n" +
                "cmp.ne $.s0 $.t3 $.t4\n" +
                "if $.t4 blk(if0-then) blk(if0-else) $.b0\n" +
                "set.var $.s1 $first\n" +
                "load res(#2) $.t7\n" +
                "cast $first $.t8 type(.any)\n" +
                "set.array $.vargs.nums $.t7 $.t8\n" +
                "load res(#2) $.t9\n" +
                "get.array $.vargs.nums $.t9 $.t10\n" +
                "cast $.t10 $.t11 type(.nullable<.i32>)\n" +
                "set.var $.t11 $.s2\n" +
                "load res(#1) $.t12\n" +
                "cmp.ne $.s2 $.t12 $.t13\n" +
                "if $.t13 blk(if1-then) blk(if1-else) $.b1\n" +
                "ret $.s3\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t5 type(.i32)\n" +
                "set.var $.t5 $.s1\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#0) $.t6\n" +
                "set.var $.t6 $.s1\n" +
                "}\n" +
                ".block if1-then {\n" +
                "cast $.s2 $.t14 type(.i32)\n" +
                "set.var $.t14 $.s3\n" +
                "}\n" +
                ".block if1-else {\n" +
                "load res(#0) $.t15\n" +
                "set.var $.t15 $.s3\n" +
                "}\n");

            // kwargs 读（Q6）：get.array 结果 .nullable<Pair>，拆箱后
            // ?. 取 key、if? 回退
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "func f(options: named String...): String {\n" +
                "    return options[0]?.key if? \"\"\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（kwargs 索引读）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（kwargs 索引读）", module2);
            BilTestHarness.CheckFnShape("kwargs 索引读（逐元素转换并重建 Pair）",
                module2, "$f()@.string",
                ".vars { .nullable<core::Pair<.string, .string>> .s0, .nullable<.string> .s1, .breakid .b0, .nullable<.string> .s2, .string .s3, .breakid .b1, .typeid .t0, .typeid .t1, .i32 .t2, .nullable<core::Pair<.string, .any>> .t3, .nullable<core::Pair<.string, .string>> .t4, .nullable<.string> .t5, .nullable<core::Pair<.string, .string>> .t6, .bool .t7, core::Pair<.string, .string> .t8, .string .t9, .nullable<.string> .t10, .nullable<.string> .t11, .bool .t12, .string .t13, .string .t14 }\n" +
                ".block entry entrypoint {\n" +
                "getid.type type(.any) $.t0\n" +
                "getid.type type(.string) $.t1\n" +
                "load res(#0) $.t2\n" +
                "get.array $.kwargs.options $.t2 $.t3\n" +
                "invoke fn(core::Pair$.static.convertArgument(source:.nullable<core::Pair<.string,.generic<$.generic.TFrom>>>)@.nullable<core::Pair<.string,.generic<$.generic.TTo>>>) $.t4 [$.t0, $.t1, $.t3]\n" +
                "set.var $.t4 $.s0\n" +
                "load res(#1) $.t5\n" +
                "set.var $.t5 $.s1\n" +
                "load res(#2) $.t6\n" +
                "cmp.ne $.s0 $.t6 $.t7\n" +
                "if $.t7 blk(if0-then) none $.b0\n" +
                "set.var $.s1 $.s2\n" +
                "load res(#1) $.t11\n" +
                "cmp.ne $.s2 $.t11 $.t12\n" +
                "if $.t12 blk(if1-then) blk(if1-else) $.b1\n" +
                "ret $.s3\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t8 type(core::Pair<.string, .string>)\n" +
                "get.field $.t8 $.t9 field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "cast $.t9 $.t10 type(.nullable<.string>)\n" +
                "set.var $.t10 $.s1\n" +
                "}\n" +
                ".block if1-then {\n" +
                "cast $.s2 $.t13 type(.string)\n" +
                "set.var $.t13 $.s3\n" +
                "}\n" +
                ".block if1-else {\n" +
                "load res(#3) $.t14\n" +
                "set.var $.t14 $.s3\n" +
                "}\n");

            // vargs 显式读改写回（Q6 后复合赋值索引形态由显式形态替代）：
            // 读侧拆箱 cast 参与运算、写回值装箱 cast 到 .any
            var (unit3, module3, _) = BilTestHarness.EmitBilUnit(
                "func bump(nums: i32...) {\n" +
                "    nums[0] = ((nums[0] if? 0) + 1)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（vargs 索引显式读改写回）", unit3);
            BilTestHarness.CheckBilValid("验证器零错误（vargs 索引显式读改写回）", module3);
            BilTestHarness.CheckFnShape("vargs 索引显式读改写回（读拆箱/写装箱）",
                module3, "$bump()@.void",
                ".vars { .nullable<.i32> .s0, .i32 .s1, .breakid .b0, .i32 .t0, " +
                ".nullable<.any> .t1, .nullable<.i32> .t2, .nullable<.i32> .t3, " +
                ".bool .t4, .i32 .t5, .i32 .t6, .i32 .t7, .i32 .t8, .i32 .t9, .any .t10 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "get.array $.vargs.nums $.t0 $.t1\n" +
                "cast $.t1 $.t2 type(.nullable<.i32>)\n" +
                "set.var $.t2 $.s0\n" +
                "load res(#1) $.t3\n" +
                "cmp.ne $.s0 $.t3 $.t4\n" +
                "if $.t4 blk(if0-then) blk(if0-else) $.b0\n" +
                "load res(#0) $.t7\n" +
                "load res(#2) $.t8\n" +
                "add $.s1 $.t8 $.t9\n" +
                "cast $.t9 $.t10 type(.any)\n" +
                "set.array $.vargs.nums $.t7 $.t10\n" +
                "ret\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t5 type(.i32)\n" +
                "set.var $.t5 $.s1\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#0) $.t6\n" +
                "set.var $.t6 $.s1\n" +
                "}\n");

            // 模型断言：get.array 结果临时按 .nullable<ABI 元素> 登记（.vars）
            var sumFn = module.Functions.Single(f => f.Symbol == "$sum()@.i32");
            TestHarness.CheckTrue("vargs get.array 结果临时类型 .nullable<.any>",
                sumFn.Vars.Any(v => v.TypeRef == ".nullable<.any>"));
            var fFn = module2.Functions.Single(f => f.Symbol == "$f()@.string");
            TestHarness.CheckTrue("kwargs get.array 结果临时类型 .nullable<core::Pair<.string, .any>>",
                fFn.Vars.Any(v => v.TypeRef == ".nullable<core::Pair<.string, .any>>"));
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
                "    pub init(n: String) {\n        name = n\n        size = 0\n    }\n" +
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

        // ===== 兄弟求值序保护（EvalOrderGuard）端到端 =====
        private static void TestEvalOrderGuardEmission()
        {
            // receiver 先于带 and 短路的实参前置（修复前 funcC 先于 funcA
            // 执行——后置兄弟的前置语句插在 receiver 降级产物之前）
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class A {\n" +
                "    pub func methodB(x: bool): i32 { return 1 }\n" +
                "}\n" +
                "func funcA(): A { return new A() }\n" +
                "func funcC(): bool { return true }\n" +
                "func funcD(): bool { return true }\n" +
                "pub func main() {\n" +
                "    funcA().methodB(funcC() and funcD())\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（receiver/短路实参求值序）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（receiver/短路实参求值序）", module);
            // 模型断言：entry 块内 funcA（receiver）→ funcC（实参前置）→
            // methodB 调用（then 块在 BIL 文本中排于 entry 之后，跨块顺序
            // 不能用文本 IndexOf 断言——funcD 单独断言在 then 块内）
            var mainFn = module.Functions.Single(f => f.Symbol == "$main()@.void");
            var entryInvokes = mainFn.Blocks[0].Instructions
                .OfType<InvokeInstruction>().Select(i => i.Method.Symbol).ToList();
            var funcAAt = entryInvokes.IndexOf("$funcA()@A");
            var funcCAt = entryInvokes.IndexOf("$funcC()@.bool");
            var methodBAt = entryInvokes.IndexOf("A$methodB(x:.bool)@.i32");
            TestHarness.CheckTrue("求值序：funcA（receiver）先于 funcC（实参前置）",
                funcAAt >= 0 && funcCAt > funcAAt);
            TestHarness.CheckTrue("求值序：funcC 先于 methodB 调用",
                funcCAt >= 0 && methodBAt > funcCAt);
            TestHarness.CheckTrue("funcD 在短路 then 块内（entry 块无 funcD）",
                !entryInvokes.Contains("$funcD()@.bool")
                && mainFn.Blocks.Skip(1).Any(b => b.Instructions
                    .OfType<InvokeInstruction>()
                    .Any(i => i.Method.Symbol == "$funcD()@.bool")));

            // 二元运算左操作数先于右操作数的短路前置
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "func left(): bool { return true }\n" +
                "func rightA(): bool { return true }\n" +
                "func rightB(): bool { return true }\n" +
                "pub func main() {\n" +
                "    var r = (left() == (rightA() and rightB()))\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（二元左操作数求值序）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（二元左操作数求值序）", module2);
            var mainFn2 = module2.Functions.Single(f => f.Symbol == "$main()@.void");
            var entryInvokes2 = mainFn2.Blocks[0].Instructions
                .OfType<InvokeInstruction>().Select(i => i.Method.Symbol).ToList();
            var leftAt = entryInvokes2.IndexOf("$left()@.bool");
            var rightAAt = entryInvokes2.IndexOf("$rightA()@.bool");
            TestHarness.CheckTrue("求值序：左操作数 left() 先于短路前置 rightA()",
                leftAt >= 0 && rightAAt > leftAt);
            TestHarness.CheckTrue("rightB() 在短路 then 块内（entry 块无 rightB）",
                !entryInvokes2.Contains("$rightB()@.bool")
                && mainFn2.Blocks.Skip(1).Any(b => b.Instructions
                    .OfType<InvokeInstruction>()
                    .Any(i => i.Method.Symbol == "$rightB()@.bool")));
        }

        // ===== 兄弟作用域同名局部唯一化（BIL §9.3 .vars 函数内唯一）=====
        private static void TestSiblingScopeLocalUniquification()
        {
            // 源码合法（SYNTAX §6：seq 即作用域）——修复前 .vars 按源码名
            // 平铺撞名，BilVerifier §21.1 误报「变量名重复 "v"」
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    seq { const v = 1 }\n" +
                "    seq { const v = 2 }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（兄弟 seq 同名局部）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（兄弟 seq 同名局部）", module);
            var mainFn = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            TestHarness.CheckTrue("撞名局部改名「名_N」（.vars 含 v 与 v_1）",
                mainFn.Vars.Any(v => v.Name == "v")
                && mainFn.Vars.Any(v => v.Name == "v_1"));
            // 端到端 VM：改名后声明/引用一致，正常运行返回 0
            var runResult = BilVm.Run(module);
            TestHarness.CheckTrue("VM 运行无异常（兄弟 seq 同名局部）",
                runResult.Exception == null, runResult.Exception?.ToString() ?? "");
            TestHarness.CheckTrue("VM 返回值 0（兄弟 seq 同名局部）",
                runResult.ReturnValue is VmI32 n && n.Value == 0,
                runResult.ReturnValue?.ToStandardText() ?? "<null>");

            // 同一函数两个 catch 子句的同名异常变量（e）——修复前同样
            // §21.1 误报；第二个 catch 必须绑定自己的 e（打印 B 而非 A）
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "import core.io.Console\n" +
                "class ErrorA : core.RuntimeException {\n" +
                "    pub override func getMessage(): String { return \"A\" }\n" +
                "}\n" +
                "class ErrorB : core.RuntimeException {\n" +
                "    pub override func getMessage(): String { return \"B\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new ErrorB()\n" +
                "    } catch (e: ErrorA) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 1\n" +
                "    } catch (e: ErrorB) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 0\n" +
                "    }\n" +
                "    return 3\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（双 catch 同名异常变量）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（双 catch 同名异常变量）", module2);
            var mainFn2 = module2.Functions.Single(f => f.Symbol == "$main()@.i32");
            TestHarness.CheckTrue("双 catch 同名异常变量改名（.vars 含 e 与 e_1）",
                mainFn2.Vars.Any(v => v.Name == "e")
                && mainFn2.Vars.Any(v => v.Name == "e_1"));
            var runResult2 = BilVm.Run(module2);
            TestHarness.CheckTrue("VM 运行无异常（双 catch 同名异常变量）",
                runResult2.Exception == null, runResult2.Exception?.ToString() ?? "");
            TestHarness.CheckTrue("VM 第二 catch 绑定自身 e（stdout=B）",
                runResult2.Stdout == "B\n", runResult2.Stdout);
            TestHarness.CheckTrue("VM 返回值 0（双 catch 同名异常变量）",
                runResult2.ReturnValue is VmI32 n2 && n2.Value == 0,
                runResult2.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // ===== 跨函数同形 try/catch 的 catch-table 按函数私有（§19.5/§21.5）=====
        private static void TestCrossFunctionCatchTablePrivate()
        {
            // 两函数各含 try/catch：block id 按函数独立编号（均 try0），
            // 修复前 catch-table 跨 fn 按元素文本去重撞键——后一函数的
            // try 指令共享前一函数的表（持别函数 block 对象），BilVerifier
            // §21.5 误报「block 引用越权："try0-catch0" 不属于当前函数」
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "import core.io.Console\n" +
                "func helper(): i32 {\n" +
                "    try { return 1 } catch (e: core.RuntimeException) { return 2 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try { Console.println(\"a\") }" +
                " catch (e: core.RuntimeException) { Console.println(\"b\") }\n" +
                "    return helper()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（跨函数同形 try/catch）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（跨函数同形 try/catch）", module);
            // 模型断言：两函数各持独立 catch-table 资源，条目 block 属本 fn
            //（MW11c 起 stdlib Mutex.runSynchronously 也产 catch-table——
            // 只数本用例两函数 try 指令引用的表，不再按全模块计数）
            var tables = module.Functions
                .Where(f => f.Symbol == "$helper()@.i32" || f.Symbol == "$main()@.i32")
                .SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions)
                .OfType<TryInstruction>()
                .Select(t => (BilCatchTableResource)t.CatchTable!)
                .Distinct().ToList();
            TestHarness.CheckTrue("两函数各登记独立 catch-table（跨函数不去重）",
                tables.Count == 2, "实际 " + tables.Count);
            foreach (var function in module.Functions.Where(
                f => f.Symbol == "$helper()@.i32" || f.Symbol == "$main()@.i32"))
            {
                var tryInstruction = function.Blocks
                    .SelectMany(b => b.Instructions)
                    .OfType<TryInstruction>().Single();
                var table = (BilCatchTableResource)tryInstruction.CatchTable!;
                TestHarness.CheckTrue("catch-table 条目 block 属本 fn（" + function.Symbol + "）",
                    table.Entries.All(e => function.Blocks.Contains(e.Handler)));
            }
            // 端到端 VM：正常路径打印 a、helper 返回 1
            var runResult = BilVm.Run(module);
            TestHarness.CheckTrue("VM 运行无异常（跨函数同形 try/catch）",
                runResult.Exception == null, runResult.Exception?.ToString() ?? "");
            TestHarness.CheckTrue("VM stdout=a（跨函数同形 try/catch）",
                runResult.Stdout == "a\n", runResult.Stdout);
            TestHarness.CheckTrue("VM 返回值 1（helper 正常路径）",
                runResult.ReturnValue is VmI32 n && n.Value == 1,
                runResult.ReturnValue?.ToStandardText() ?? "<null>");
        }
    }
}
