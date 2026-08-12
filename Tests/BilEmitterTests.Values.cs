using System.Collections.Generic;
using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    // BilEmitter 值形态发射测试（cast/字符串插值/?. 安全调用/if? 空值回退/解构/is-supers-with/typeOf）

    public static partial class BilEmitterTests
    {
        // ===== S7e：cast 发射（§12.1/§12.2）=====
        private static void TestCastEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func f(s: String): String {\n" +
                "    return s as String\n" +
                "}\n" +
                "func g(s: String): String? {\n" +
                "    return s as? String\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（cast 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（cast 发射）", module);
            BilTestHarness.CheckFnShape("as 指令文本", module, "$f(s:.string)@.string",
                ".vars { .string .t0 }\n" +
                "cast $s $.t0 type(.string)\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("as? 指令文本", module, "$g(s:.string)@.nullable<.string>",
                ".vars { .nullable<.string> .t0 }\n" +
                "cast.safe $s $.t0 type(.string)\n" +
                "ret $.t0\n");
            // 结构性事实：§12.1 三操作数形状（SOURCE RESULT type(TARGET_TYPE)）
            var castInstruction = module.Functions.Single(f => f.Symbol == "$f(s:.string)@.string")
                .Blocks[0].Instructions.Single(i => i is CastInstruction);
            TestHarness.CheckTrue("cast 三操作数（§12.1）",
                castInstruction.Operands.Count == 3
                && castInstruction.Operands[0] is BilVariableOperand
                && castInstruction.Operands[1] is BilVariableOperand
                && castInstruction.Operands[2] is BilTypeOperand);
        }

        // ===== S7f：字符串插值端到端（§3.8：toString/add 链 + 装箱 cast + §11.2 拼接）=====
        private static void TestStringInterpolationEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main() {\n" +
                "    var name = \"world\"\n" +
                "    var count = 3\n" +
                "    core.io.Console.println(\"Hello ${name}, count=${count + 1}\")\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（插值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（插值）", module);
            BilTestHarness.CheckFnShape("main 指令与 .vars（插值端到端）", module, "$main()@.void",
                ".vars { .string name, .i32 count, .string .t0, .i32 .t1, .string .t2, " +
                ".string .t3, .string .t4, .string .t5, .i32 .t6, .i32 .t7, .any .t8, " +
                ".string .t9, .string .t10 }\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $name\n" +
                "load res(#1) $.t1\n" +
                "set.var $.t1 $count\n" +
                "load res(#2) $.t2\n" +
                "add $.t2 $name $.t3\n" +
                "load res(#3) $.t4\n" +
                "add $.t3 $.t4 $.t5\n" +
                "load res(#4) $.t6\n" +
                "add $count $.t6 $.t7\n" +
                "cast $.t7 $.t8 type(.any)\n" +
                "invoke fn(core::Any$toString()@.string) $.t9 [$.t8]\n" +
                "add $.t5 $.t9 $.t10\n" +
                "invoke.noret fn(core.io::Console$.static.println(text:.string)@.void) [$.t10]\n" +
                "ret\n");
            // 结构性事实：String 段直拼无 toString；非 String 段一经 cast 一 invoke
            var mainInstructions = module.Functions.Single(f => f.Symbol == "$main()@.void")
                .Blocks[0].Instructions;
            TestHarness.CheckTrue("toString 调用恰一次（仅非 String 段）",
                mainInstructions.Count(i => i is InvokeInstruction) == 1);
        }

        // ===== S7f：`?.` 发射（§3.4 脱糖：null 检查 + if + unwrap/wrap cast）=====
        private static void TestSafeAccessEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func f(u: User?): String? { return u?.name }\n");
            CheckNoErrors("全管线无诊断（?.）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（?.）", module);
            BilTestHarness.CheckFnShape("f 指令与 .vars（?. 发射）",
                module, "$f(u:.nullable<User>)@.nullable<.string>",
                ".vars { .nullable<User> .s0, .nullable<.string> .s1, .nullable<.string> .t0, " +
                ".nullable<User> .t1, .bool .t2, User .t3, .string .t4, .nullable<.string> .t5 }\n" +
                ".block entry entrypoint {\n" +
                "set.var $u $.s0\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $.s1\n" +
                "load res(#1) $.t1\n" +
                "cmp.ne $.s0 $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) none\n" +
                "ret $.s1\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t3 type(User)\n" +
                "get.field $.t3 $.t4 field(User#name@.string)\n" +
                "cast $.t4 $.t5 type(.nullable<.string>)\n" +
                "set.var $.t5 $.s1\n" +
                "}\n");
            // 结构性事实：null 资源形态（§19.1：null type(元素类型)）
            TestHarness.CheckTrue("null 资源按元素类型登记（R_5/R_6）",
                module.Resources.Any(r => r is BilNullResource n
                    && n.TypeRef == ".string")
                && module.Resources.Any(r => r is BilNullResource n
                    && n.TypeRef == "User"));
        }

        // ===== S7f：`if?` 发射（非空分支 unwrap / 空分支回退，延迟求值）=====
        private static void TestNullFallbackEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func g(u: User?): User { return u if? new User(\"anon\") }\n");
            CheckNoErrors("全管线无诊断（if?）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（if?）", module);
            BilTestHarness.CheckFnShape("g 指令与 .vars（if? 发射）",
                module, "$g(u:.nullable<User>)@User",
                ".vars { .nullable<User> .s0, User .s1, .nullable<User> .t0, .bool .t1, " +
                "User .t2, .string .t3, User .t4 }\n" +
                ".block entry entrypoint {\n" +
                "set.var $u $.s0\n" +
                "load res(#0) $.t0\n" +
                "cmp.ne $.s0 $.t0 $.t1\n" +
                "if $.t1 blk(if0-then) blk(if0-else)\n" +
                "ret $.s1\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t2 type(User)\n" +
                "set.var $.t2 $.s1\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#1) $.t3\n" +
                "new type(User) $.t4 [$.t3]\n" +
                "set.var $.t4 $.s1\n" +
                "}\n");
        }

        // ===== S7f：解构发射（§3.4 精确字段读取；基类泛型字段访问）=====
        private static void TestDestructuringEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "pub func h(): String {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "    return k\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（解构）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（解构）", module);
            BilTestHarness.CheckFnShape("h 指令与 .vars（解构发射）", module, "$h()@.string",
                ".vars { .string k, .i32 v, Entry .s0, .string .t0, .i32 .t1, Entry .t2, " +
                ".string .t3, .i32 .t4 }\n" +
                "load res(#0) $.t0\n" +
                "load res(#1) $.t1\n" +
                "new type(Entry) $.t2 [$.t0, $.t1]\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "set.var $.t3 $k\n" +
                "get.field $.s0 $.t4 field(core::Pair#value@.generic<$.generic.TValue>)\n" +
                "set.var $.t4 $v\n" +
                "ret $k\n");
            BilTestHarness.CheckFnShape("init 内基类字段写入（替换后类型）",
                module, "Entry$init(k:.string,v:.i32)@.void",
                ".vars {  }\n" +
                "set.field $k $.this field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "set.field $v $.this field(core::Pair#value@.generic<$.generic.TValue>)\n" +
                "ret\n");
        }

        // ===== S8a：is/supers/with 发射（§12.3；黄金文本经 --emit-bil 冒烟核定）=====
        private static void TestTypeCheckEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "wrapper Serializable { }\n" +
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "pub func f(d: Dog): bool {\n    return d is Animal\n}\n" +
                "pub func g(d: Dog): bool {\n    return d supers Animal\n}\n" +
                "pub func h(d: Dog): bool {\n    return d with Serializable\n}\n" +
                "pub func k(d: Dog): bool {\n    var t = typeOf(d)\n    return d is t\n}\n");
            CheckNoErrors("全管线无诊断（类型谓词发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（类型谓词发射）", module);
            BilTestHarness.CheckFnShape("type.is 指令与 .vars", module, "$f(d:Dog)@.bool",
                ".vars { .bool .t0 }\n" +
                "type.is $d type(Animal) $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("type.supers 指令与 .vars", module, "$g(d:Dog)@.bool",
                ".vars { .bool .t0 }\n" +
                "type.supers $d type(Animal) $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("type.with 指令与 .vars", module, "$h(d:Dog)@.bool",
                ".vars { .bool .t0 }\n" +
                "type.with $d type(Serializable) $.t0\n" +
                "ret $.t0\n");
            // 动态形态：getid.var 前置（typeOf 值形态）+ type.is.indirect 三变量操作数
            BilTestHarness.CheckFnShape("type.is.indirect 指令与 .vars（getid.var 前置）",
                module, "$k(d:Dog)@.bool",
                ".vars { .typeid<Dog> t, .typeid<Dog> .t0, .bool .t1 }\n" +
                "getid.var $d $.t0\n" +
                "set.var $.t0 $t\n" +
                "type.is.indirect $d $t $.t1\n" +
                "ret $.t1\n");
            // 结构性事实：§12.3 静态三操作数形状（VALUE type(TARGET_TYPE) RESULT_BOOL）
            var isInstruction = module.Functions.Single(f => f.Symbol == "$f(d:Dog)@.bool")
                .Blocks[0].Instructions
                .Single(i => i is DirectTypeCheckInstruction { Kind: BilTypeCheckKind.Is });
            TestHarness.CheckTrue("type.is 三操作数（§12.3）",
                isInstruction.Operands.Count == 3
                && isInstruction.Operands[0] is BilVariableOperand
                && isInstruction.Operands[1] is BilTypeOperand
                && isInstruction.Operands[2] is BilVariableOperand);
            // 结构性事实：§12.3 动态三操作数全变量（VALUE TYPEID_VAR RESULT_BOOL）
            var indirectInstruction = module.Functions.Single(f => f.Symbol == "$k(d:Dog)@.bool")
                .Blocks[0].Instructions
                .Single(i => i is IndirectTypeCheckInstruction { Kind: BilTypeCheckKind.Is });
            TestHarness.CheckTrue("type.is.indirect 三操作数全变量（§12.3）",
                indirectInstruction.Operands.Count == 3
                && indirectInstruction.Operands.All(o => o is BilVariableOperand));
        }

        // ===== S8a：typeOf 发射（§12.5；黄金文本经 --emit-bil 冒烟核定）=====
        private static void TestTypeOfEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "pub func m(): Type\\<Animal> {\n    return typeOf(Animal)\n}\n" +
                "pub func n(d: Dog): Type\\<Dog> {\n    return typeOf(d)\n}\n");
            CheckNoErrors("全管线无诊断（typeOf 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（typeOf 发射）", module);
            BilTestHarness.CheckFnShape("getid.type 指令与 .vars（类型形态）",
                module, "$m()@.typeid<Animal>",
                ".vars { .typeid<Animal> .t0 }\n" +
                "getid.type type(Animal) $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("getid.var 指令与 .vars（值形态）",
                module, "$n(d:Dog)@.typeid<Dog>",
                ".vars { .typeid<Dog> .t0 }\n" +
                "getid.var $d $.t0\n" +
                "ret $.t0\n");
            // 结构性事实：§12.5 getid.type 双操作数（type(TYPE_SYMBOL) TARGET_TYPEID）
            var getIdType = module.Functions.Single(f => f.Symbol == "$m()@.typeid<Animal>")
                .Blocks[0].Instructions.Single(i => i is GetIdTypeInstruction);
            TestHarness.CheckTrue("getid.type 双操作数（§12.5）",
                getIdType.Operands.Count == 2
                && getIdType.Operands[0] is BilTypeOperand
                && getIdType.Operands[1] is BilVariableOperand);
            // 结构性事实：§12.5 getid.var 双操作数（VALUE TARGET_TYPEID）
            var getIdVar = module.Functions.Single(f => f.Symbol == "$n(d:Dog)@.typeid<Dog>")
                .Blocks[0].Instructions.Single(i => i is GetIdVarInstruction);
            TestHarness.CheckTrue("getid.var 双操作数全变量（§12.5）",
                getIdVar.Operands.Count == 2
                && getIdVar.Operands.All(o => o is BilVariableOperand));
        }

        // ===== S9e：泛型隐藏参数物化（§7.2 调用序 + §12.5 getid.type +
        // §8.2 generic(...) 子句）=====
        // ===== M108：invoke.indirect 泛型 ABI（typeid 前缀与 direct 同构）=====
        private static void TestIndirectGenericEmission()
        {
            // 显式泛型实参间接调用：f\<i32>(x) → getid.type + invoke.indirect
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Mapper {\n" +
                "    pub operator call\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "func main() {\n" +
                "    var f = new Mapper()\n" +
                "    var v = f\\<i32>(1)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（间接泛型调用）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（间接泛型调用）", module);

            // $$call fn .args：.return → .this → .generic.T → 普通参数
            var callFn = module.Functions.Single(f => f.Symbol.Contains("$$call")
                && f.Symbol.Contains("Mapper"));
            TestHarness.CheckTrue("泛型 $$call .args 含 .generic.T = .typeid",
                callFn.Args.Any(a => a.Name == ".generic.T" && a.TypeRef == ".typeid"));

            // 调用点：getid.type 前置 + invoke.indirect 实参 [typeid, value]
            BilTestHarness.CheckFnShape("间接泛型 typeid 前缀（main）", module, "$main()@.void",
                ".vars { Mapper f, .i32 v, Mapper .t0, .typeid .t1, .i32 .t2, .i32 .t3 }\n" +
                "new type(Mapper) $.t0 []\n" +
                "set.var $.t0 $f\n" +
                "getid.type type(.i32) $.t1\n" +
                "load res(#0) $.t2\n" +
                "invoke.indirect $f $.t3 [$.t1, $.t2]\n" +
                "set.var $.t3 $v\n" +
                "ret\n");

            // 泛型可变包命中 call 运算符：.array<.typeid<.any>> 前置
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "pub class Collector {\n" +
                "    pub operator call\\<TArgs...>(values: TArgs...): i32 { return 0 }\n" +
                "}\n" +
                "func main() {\n" +
                "    var f = new Collector()\n" +
                "    var v = f(1, \"s\")\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（间接泛型包）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（间接泛型包）", module2);
            var main2 = module2.Functions.Single(f => f.Symbol == "$main()@.void");
            var news = main2.Blocks[0].Instructions.OfType<NewInstruction>().ToList();
            TestHarness.CheckTrue("间接泛型包 .array<.typeid<.any>> 构造",
                news.Any(n => n.Type.TypeRef == ".array<.typeid<.any>>"
                    && n.Arguments.Count == 2));
            TestHarness.CheckTrue("间接包调用含 invoke.indirect",
                main2.Blocks[0].Instructions.OfType<InvokeIndirectInstruction>().Any());
            var typeIds = main2.Blocks[0].Instructions.OfType<GetIdTypeInstruction>().ToList();
            TestHarness.CheckTrue("间接包 typeid 物化（i32/String）", typeIds.Count >= 2);
        }

        private static void TestGenericEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func identity\\<T>(x: T): T { return x }\n" +
                "func pass\\<T>(x: T): T { return identity\\<T>(x) }\n" +
                "func main() {\n" +
                "    var v = identity\\<i32>(1)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（泛型隐藏参数）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（泛型隐藏参数）", module);

            // fn 定义 .args：§7.2 序 .return → .generic.T = .typeid → 普通参数
            var identityFn = module.Functions.Single(f => f.Symbol
                == "$identity(x:.generic<$.generic.T>)@.generic<$.generic.T>");
            TestHarness.CheckTrue("泛型 fn .args 顺序（.return → .generic.T → 普通）",
                identityFn.Args.Count == 3
                && identityFn.Args[0].Name == ".return"
                && identityFn.Args[0].TypeRef == ".generic<$.generic.T>"
                && identityFn.Args[1].Name == ".generic.T" && identityFn.Args[1].TypeRef == ".typeid"
                && identityFn.Args[2].Name == "x"
                && identityFn.Args[2].TypeRef == ".generic<$.generic.T>");

            // 静态实参：getid.type type(...) 物化 .typeid 临时变量 → 实参前置
            BilTestHarness.CheckFnShape("静态泛型实参物化（main）", module, "$main()@.void",
                ".vars { .i32 v, .typeid .t0, .i32 .t1, .i32 .t2 }\n" +
                "getid.type type(.i32) $.t0\n" +
                "load res(#0) $.t1\n" +
                "invoke fn($identity(x:.generic<$.generic.T>)@.generic<$.generic.T>) $.t2 [$.t0, $.t1]\n" +
                "set.var $.t2 $v\n" +
                "ret\n");

            // 嵌套泛型调用：转发接收的 .generic.T 隐藏参数（零指令，无 getid.type；
            // .args 顺序已由上面的 fn .args 结构断言覆盖）
            BilTestHarness.CheckFnShape("嵌套泛型调用转发（pass）", module,
                "$pass(x:.generic<$.generic.T>)@.generic<$.generic.T>",
                ".vars { .generic<$.generic.T> .t0 }\n" +
                "invoke fn($identity(x:.generic<$.generic.T>)@.generic<$.generic.T>) $.t0 [$.generic.T, $x]\n" +
                "ret $.t0\n");

            // .type 声明 generic(...) 子句（§8.2，S9e 定稿：泛型参数名列表；
            // stdlib core::Pair 定义级发射验证）
            TestHarness.CheckTrue(".type generic 子句（core::Pair）",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .First(t => t.Symbol == "core::Pair") is { } pairDeclaration
                    && pairDeclaration.GenericParameters.Count == 2
                    && pairDeclaration.GenericParameters[0] == "TKey"
                    && pairDeclaration.GenericParameters[1] == "TValue");
        }

        private static void TestGenericVarianceEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "class Producer\\<out T> { }\n" +
                "class Consumer\\<in T> { }\n" +
                "func takeProducer(value: Producer\\<Animal>) { }\n" +
                "func takeConsumer(value: Consumer\\<Dog>) { }\n" +
                "func main(producer: Producer\\<Dog>, consumer: Consumer\\<Animal>) {\n" +
                "    takeProducer(producer)\n" +
                "    takeConsumer(consumer)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（泛型型变）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（泛型型变）", module);
            var producer = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Producer");
            var consumer = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Consumer");
            TestHarness.CheckTrue("BIL 泛型声明保留 out/in",
                producer.GenericVariances.Single() == BilGenericVariance.Out
                && consumer.GenericVariances.Single() == BilGenericVariance.In);
            TestHarness.CheckTrue("BIL 泛型声明 writer 保留 out/in",
                BilWriter.Write(module).Contains("generic(out T)")
                && BilWriter.Write(module).Contains("generic(in T)"));
        }

        // ===== S9d：可变参数 vargs/kwargs（§7.1 隐藏包 + §14 特权构造打包）=====
        private static void TestVarArgsEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func sum(numbers: i32...): i32 { return 1 }\n" +
                "func config(options: named String...): Any { return options }\n" +
                "func main() {\n" +
                "    var s = sum(1, 2, 3)\n" +
                "    var c = config(name = \"latte\")\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（vargs/kwargs）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（vargs/kwargs）", module);

            // fn .args：普通参数区无 numbers/options（canonical 亦无——
            // PrintMethod 跳过可变参数）；隐藏条目 .vargs./.kwargs. 在末位
            var sumFn = module.Functions.Single(f => f.Symbol == "$sum()@.i32");
            TestHarness.CheckTrue("位置包隐藏条目（.vargs.numbers = .array<.any>）",
                sumFn.Args.Count == 2 && sumFn.Args[1].Name == ".vargs.numbers"
                && sumFn.Args[1].TypeRef == ".array<.any>");
            var configFn = module.Functions.Single(f => f.Symbol == "$config()@.any");
            TestHarness.CheckTrue("具名包隐藏条目（.kwargs.options = .array<.pair<.string, .any>>）",
                configFn.Args.Count == 2 && configFn.Args[1].Name == ".kwargs.options"
                && configFn.Args[1].TypeRef == ".array<.pair<.string, .any>>");

            // 体内引用：$vargs.numbers / $kwargs.options 映射（零指令）
            TestHarness.CheckTrue("体内 kwargs 引用映射",
                configFn.Blocks[0].Instructions
                    .OfType<CastInstruction>().Any(i =>
                        i.Operands[0] is BilVariableOperand { Name: ".kwargs.options" }));

            // 调用点：位置包 new type(.array<.any>) 装箱打包；具名包 pair 逐项
            // 后装入 .array<core::Pair<.string, .any>>（元素 = pair canonical——
            // core::Pair 非内建无 .pair 构造头投影；与 .kwargs 契约的
            // .array<.pair<.string, .any>> 同元素类型，§7.1）
            var mainFn = module.Functions.Single(f => f.Symbol == "$main()@.void");
            var news = mainFn.Blocks[0].Instructions.OfType<NewInstruction>().ToList();
            TestHarness.CheckTrue("位置包 array 构造 + 具名包 pair/array 构造",
                news.Any(n => n.Type.TypeRef == ".array<.any>"
                    && n.Arguments.Count == 3)
                && news.Any(n => n.Type.TypeRef == "core::Pair<.string, .any>")
                && news.Any(n => n.Type.TypeRef == ".array<core::Pair<.string, .any>>"
                    && n.Arguments.Count == 1));
        }

        // ===== S9d-2：泛型可变参数包（§7.1 隐藏参数 .generic.TArgs/
        // .generic.TValues——位置 .array<.typeid>、具名 .map<.string, .typeid>；
        // §7.2 序：固定泛型 → 泛型可变包 → 普通参数 → 值包）=====
        private static void TestGenericVarArgsEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func collect\\<TArgs...>(values: TArgs...): i32 { return 1 }\n" +
                "pub func update\\<named TValues...>(configs: named TValues...): bool { return true }\n" +
                "pub func main() {\n" +
                "    var c = collect(1, \"s\")\n" +
                "    var u = update(isDarkMode = true, userName = \"Andy\")\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（泛型可变参数）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（泛型可变参数）", module);

            // fn .args：泛型包隐藏条目按 §7.2 序（固定泛型 → 泛型包 → 值包）
            var collectFn = module.Functions.Single(f => f.Symbol == "$collect()@.i32");
            TestHarness.CheckTrue("位置泛型包隐藏条目（.generic.TArgs = .array<.typeid<.any>>）",
                collectFn.Args.Count == 3
                && collectFn.Args[1].Name == ".generic.TArgs"
                && collectFn.Args[1].TypeRef == ".array<.typeid<.any>>"
                && collectFn.Args[2].Name == ".vargs.values"
                && collectFn.Args[2].TypeRef == ".array<.any>");
            var updateFn = module.Functions.Single(f => f.Symbol == "$update()@.bool");
            TestHarness.CheckTrue("具名泛型包隐藏条目（.generic.TValues = .map<.string, .typeid<.any>>）",
                updateFn.Args.Count == 3
                && updateFn.Args[1].Name == ".generic.TValues"
                && updateFn.Args[1].TypeRef == ".map<.string, .typeid<.any>>"
                && updateFn.Args[2].Name == ".kwargs.configs"
                && updateFn.Args[2].TypeRef == ".array<.pair<.string, .any>>");

            // 调用点：位置包 new .array<.typeid<.any>> [getid.type...]；具名包
            // pair 逐项（名 + getid.type）→ new .map<.string, .typeid<.any>>
            var mainFn = module.Functions.Single(f => f.Symbol == "$main()@.void");
            var mainNews = mainFn.Blocks[0].Instructions.OfType<NewInstruction>().ToList();
            TestHarness.CheckTrue("位置包 .typeid 数组构造（2 元素）",
                mainNews.Any(n => n.Type.TypeRef == ".array<.typeid<.any>>"
                    && n.Arguments.Count == 2));
            TestHarness.CheckTrue("具名包 pair/map 构造",
                mainNews.Any(n => n.Type.TypeRef == "core::Pair<.string, .typeid<.any>>")
                && mainNews.Any(n => n.Type.TypeRef == ".map<.string, .typeid<.any>>"
                    && n.Arguments.Count == 2));
            var typeIds = mainFn.Blocks[0].Instructions.OfType<GetIdTypeInstruction>().ToList();
            TestHarness.CheckTrue("typeid 物化（i32/String/bool 三项）", typeIds.Count >= 3);
        }
    }
}
