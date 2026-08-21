using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    // BilEmitter 实例成员与索引发射测试（.this/实例 invoke/get.field/set.field/init/operator 声明形态、§13.6 get.array/set.array）

    public static partial class BilEmitterTests
    {
        // ===== §9.3：init 参数映射赋值合成发射（无体 init 的 fn 定义 +
        // stdlib core::Pair 有体空体前插基线）=====
        private static void TestInitMappingEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const p = new Point(1)\n" +
                "    return p.x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（无体 init 映射发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（无体 init 映射发射）", module);
            // fn 形状黄金：无体 init 的合成映射赋值体（§21.2 fn 定义门槛
            // 落地——此前该形态「跳过 fn 定义」被验证器拒绝落盘）
            BilTestHarness.CheckFnShape("无体 init 合成体（set.field + ret）",
                module, "Point$init(x:.i32)@.void",
                ".vars {  }\n" +
                "set.field $x $.this field(Point#x@.i32)\n" +
                "ret\n");
            // 端到端语义闭环：new 传参 + get.field 读回映射字段
            BilTestHarness.CheckFnShape("main 形状（new + 读映射字段）",
                module, "$main()@.i32",
                ".vars { Point p, .i32 .t0, Point .t1, .i32 .t2 }\n" +
                "load res(#0) $.t0\n" +
                "new type(Point) $.t1 [$.t0]\n" +
                "set.var $.t1 $p\n" +
                "get.field $p $.t2 field(Point#x@.i32)\n" +
                "ret $.t2\n");
            // stdlib 基线：core::Pair 的 init（有体空体 + 双映射）映射赋值
            // 前插——fn 定义体由空（仅 ret）变两条 set.field（泛型字段
            // canonical；const 字段写入走 §21.8 init 豁免）
            BilTestHarness.CheckFnShape("core::Pair init 映射前插基线",
                module, "core::Pair$init(key:.generic<$.generic.TKey>," +
                    "value:.generic<$.generic.TValue>)@.void",
                ".vars {  }\n" +
                "set.field $key $.this field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "set.field $value $.this field(core::Pair#value@.generic<$.generic.TValue>)\n" +
                "ret\n");
        }

        private static void TestSuperEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "open class A {\n" +
                "    pub init(n: i32) {}\n" +
                "    pub open func f(x: i32): i32 { return x }\n" +
                "    pub open func ping() {}\n" +
                "}\n" +
                "class B: A {\n" +
                "    pub init(n: i32) { super(n) }\n" +
                "    pub override func f(x: i32): i32 { return super(x) }\n" +
                "    pub override func ping() { super() }\n" +
                "}\n");
            CheckNoErrors("全管线 super 发射", unit);
            BilTestHarness.CheckBilValid("验证器接受合法 ..super", module);
            var f = module.Functions.Single(fn => fn.Symbol == "B$f(x:.i32)@.i32");
            var fInvoke = f.Blocks[0].Instructions.OfType<InvokeInstruction>().Single();
            TestHarness.CheckTrue("super 返回调用发 ..super 且 $.this 首参",
                fInvoke.Method.Symbol == BilSpellings.SuperReservedFunction
                && fInvoke.Arguments[0].Name == ".this");
            var ping = module.Functions.Single(fn => fn.Symbol == "B$ping()@.void");
            TestHarness.CheckTrue("void super 发 invoke.noret ..super",
                ping.Blocks[0].Instructions.OfType<InvokeNoResultInstruction>().Any(invoke =>
                    invoke.Method.Symbol == BilSpellings.SuperReservedFunction
                    && invoke.Arguments[0].Name == ".this"));
            var init = module.Functions.Single(fn => fn.Symbol == "B$init(n:.i32)@.void");
            TestHarness.CheckTrue("init super 发 ..super",
                init.Blocks[0].Instructions.OfType<InvokeNoResultInstruction>().Any(invoke =>
                    invoke.Method.Symbol == BilSpellings.SuperReservedFunction));
        }

        // ===== S7c-2：实例成员发射（.this/实例 invoke/get.field/set.field/
        // init/operator 声明形态）=====
        private static void TestInstanceEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "    pub func add(n: i32): i32 { return value + n }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter(1)\n" +
                "    return c.add(2)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（实例成员发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（实例成员发射）", module);

            // init/operator/ext 声明形态（§8.4）——init 是 Counter .type
            // 的成员（类型成员嵌在类型声明内）；ext operator 是顶层裸条目
            TestHarness.CheckTrue("init 声明形态（普通 canonical + init 修饰符）",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "Counter")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Kind == BilMemberKind.Method
                        && d.Symbol == "Counter$init(v:.i32)@.void"
                        && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Init })
                        && d.Modifiers.Any(m => m is BilAccessibilityModifier
                            { Accessibility: BilAccessibility.Public })));
            TestHarness.CheckTrue("ext operator 声明形态（$$名 + ext + operator(名)）",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Method
                    && d.Symbol == "core::i32$$EnumerateInRange(end:.i32)" +
                        "@core.collections::IEnumerable<.i32>"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })
                    && d.Modifiers.Any(m => m is BilOperatorModifier op
                        && op.Name == "EnumerateInRange")));

            // .this 进 .args（§9.2：.return 后、普通参数前；§7.3）
            var addFn = module.Functions.Single(f => f.Symbol == "Counter$add(n:.i32)@.i32");
            TestHarness.CheckTrue("add 的 .args = [.return, .this, n]",
                addFn.Args.Count == 3
                && addFn.Args[0].Name == ".return"
                && addFn.Args[1].Name == ".this" && addFn.Args[1].TypeRef == "Counter"
                && addFn.Args[2].Name == "n");
            var initFn = module.Functions.Single(f => f.Symbol == "Counter$init(v:.i32)@.void");
            TestHarness.CheckTrue("init 的 .args = [.return(.void), .this, v]",
                initFn.Args.Count == 3
                && initFn.Args[0].TypeRef == ".void"
                && initFn.Args[1].Name == ".this");

            // get.field/set.field（§13.3）与 void init 末尾补 ret
            BilTestHarness.CheckFnShape("init 指令（set.field $v $.this）",
                module, "Counter$init(v:.i32)@.void",
                ".vars {  }\n" +
                "set.field $v $.this field(Counter#value@.i32)\n" +
                "ret\n");
            BilTestHarness.CheckFnShape("add 指令（get.field $.this + add）",
                module, "Counter$add(n:.i32)@.i32",
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "get.field $.this $.t0 field(Counter#value@.i32)\n" +
                "add $.t0 $n $.t1\n" +
                "ret $.t1\n");

            // 实例 invoke：receiver 求值作首实参（§7.3/§15.1）
            BilTestHarness.CheckFnShape("main 指令（new + 实例 invoke receiver 首参）",
                module, "$main()@.i32",
                ".vars { Counter c, .i32 .t0, Counter .t1, .i32 .t2, .i32 .t3 }\n" +
                "load res(#0) $.t0\n" +
                "new type(Counter) $.t1 [$.t0]\n" +
                "set.var $.t1 $c\n" +
                "load res(#1) $.t2\n" +
                "invoke fn(Counter$add(n:.i32)@.i32) $.t3 [$c, $.t2]\n" +
                "ret $.t3\n");
        }

        // T receiver 接口调用与接口变量调用 BIL 形态一致；运算符位置仍发 intrinsic
        private static void TestGenericParamMemberEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub interface Sized { pub func size(): i32 }\n" +
                "pub func viaIface(s: Sized): i32 { return s.size() }\n" +
                "pub func viaParam\\<T extends Sized>(s: T): i32 { return s.size() }\n" +
                "pub interface Addable { pub operator plus(other: Addable): Addable }\n" +
                "pub func addT\\<T extends Addable>(a: T, b: T): Addable { return a + b }\n");
            CheckNoErrors("全管线无诊断（T 有效成员发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（T 有效成员发射）", module);
            var ifaceFn = module.Functions.Single(f => f.Symbol == "$viaIface(s:Sized)@.i32");
            var paramFn = module.Functions.Single(f =>
                f.Symbol == "$viaParam(s:.generic<$.generic.T>)@.i32");
            var ifaceInvoke = ifaceFn.Blocks[0].Instructions
                .OfType<InvokeInstruction>().Single();
            var paramInvoke = paramFn.Blocks[0].Instructions
                .OfType<InvokeInstruction>().Single();
            TestHarness.CheckTrue("T receiver 与接口变量 invoke 同一方法符号",
                ifaceInvoke.Method.Symbol == paramInvoke.Method.Symbol
                && ifaceInvoke.Method.Symbol == "Sized$size()@.i32");
            BilTestHarness.CheckFnShape("运算符位置仍发 add", module,
                "$addT(a:.generic<$.generic.T>,b:.generic<$.generic.T>)@Addable",
                ".vars { Addable .t0 }\n" +
                "add $a $b $.t0\n" +
                "ret $.t0\n");
        }

        // ===== S8c：索引访问发射（§13.6 get.array/set.array + operator
        // §8.4 声明）=====
        private static void TestIndexEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "}\n" +
                "pub class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub init() { item = 0 }\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "pub class CounterBag {\n" +
                "    pub var first: Counter\n" +
                "    pub init(c: Counter) { first = c }\n" +
                "    pub operator getAtIndex(index: i32): Counter? { return first }\n" +
                "}\n" +
                "pub func makeBag(): Bag { return new Bag() }\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag()\n" +
                "    b[0] = 7\n" +
                "    var x = b[1] if? 0\n" +
                "    b[2] = ((b[2] if? 0) + 3)\n" +
                "    var cb = new CounterBag(new Counter(5))\n" +
                "    var y = cb[0]?.value if? 0\n" +
                "    var z = makeBag()[9] if? 0\n" +
                "    return ((((x + y) + z) + (b[0] if? 0)))\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（索引发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（索引发射）", module);

            // 用户类型 operator 声明形态（§8.4：$$名 canonical + operator(名)
            // 修饰符，类型成员嵌在 .type 声明内）
            TestHarness.CheckTrue("getAtIndex/setAtIndex 声明形态（$$名 + operator(名)）",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "Bag")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Symbol == "Bag$$getAtIndex(index:.i32)@.nullable<.i32>"
                        && d.Modifiers.Any(m => m is BilOperatorModifier op
                            && op.Name == "getAtIndex"))
                    && module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "Bag")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Symbol == "Bag$$setAtIndex(index:.i32,element:.i32)@.void"
                        && d.Modifiers.Any(m => m is BilOperatorModifier op
                            && op.Name == "setAtIndex")));

            // §13.6：写（set.array COLLECTION INDEX ELEMENT——COLLECTION/INDEX
            // 先物化，再 ELEMENT（与复合赋值同求值序）；操作数序仍按规范）、
            // 读（get.array）、复合（读一次 + 写回值物化 .s0 后写——§13.2
            // 单次求值，表达式位不再二次 get）、链式
            // （cb[0].value = get.array → get.field；makeBag()[9] = invoke →
            // get.array）
            BilTestHarness.CheckFnShape("main 指令（索引读写/显式读改写回/链式，Q6）",
                module, "$main()@.i32",
                ".vars { Bag b, .i32 x, CounterBag cb, .i32 y, .i32 z, .nullable<.i32> .s0, " +
                ".i32 .s1, .breakid .b0, .nullable<.i32> .s2, .i32 .s3, .breakid .b1, " +
                ".nullable<Counter> .s4, .nullable<.i32> .s5, .breakid .b2, " +
                ".nullable<.i32> .s6, .i32 .s7, .breakid .b3, .nullable<.i32> .s8, " +
                ".i32 .s9, .breakid .b4, .nullable<.i32> .s10, .i32 .s11, .breakid .b5, " +
                ".i32 .s12, Bag .t0, .i32 .t1, .i32 .t2, .i32 .t3, .nullable<.i32> .t4, " +
                ".nullable<.i32> .t5, .bool .t6, .i32 .t7, .i32 .t8, .i32 .t9, " +
                ".nullable<.i32> .t10, .nullable<.i32> .t11, .bool .t12, .i32 .t13, " +
                ".i32 .t14, .i32 .t15, .i32 .t16, .i32 .t17, .i32 .t18, Counter .t19, " +
                "CounterBag .t20, .i32 .t21, .nullable<Counter> .t22, .nullable<.i32> .t23, " +
                ".nullable<Counter> .t24, .bool .t25, Counter .t26, .i32 .t27, " +
                ".nullable<.i32> .t28, .nullable<.i32> .t29, .bool .t30, .i32 .t31, " +
                ".i32 .t32, Bag .t33, .i32 .t34, .nullable<.i32> .t35, .nullable<.i32> .t36, " +
                ".bool .t37, .i32 .t38, .i32 .t39, .i32 .t40, .i32 .t41, .i32 .t42, " +
                ".nullable<.i32> .t43, .nullable<.i32> .t44, .bool .t45, .i32 .t46, " +
                ".i32 .t47, .i32 .t48 }\n" +
                ".block entry entrypoint {\n" +
                "new type(Bag) $.t0 []\n" +
                "set.var $.t0 $b\n" +
                "load res(#0) $.t1\n" +
                "load res(#1) $.t2\n" +
                "set.array $b $.t1 $.t2\n" +
                "load res(#2) $.t3\n" +
                "get.array $b $.t3 $.t4\n" +
                "set.var $.t4 $.s0\n" +
                "load res(#3) $.t5\n" +
                "cmp.ne $.s0 $.t5 $.t6\n" +
                "if $.t6 blk(if0-then) blk(if0-else) $.b0\n" +
                "set.var $.s1 $x\n" +
                "load res(#4) $.t9\n" +
                "get.array $b $.t9 $.t10\n" +
                "set.var $.t10 $.s2\n" +
                "load res(#3) $.t11\n" +
                "cmp.ne $.s2 $.t11 $.t12\n" +
                "if $.t12 blk(if1-then) blk(if1-else) $.b1\n" +
                "load res(#4) $.t15\n" +
                "load res(#5) $.t16\n" +
                "add $.s3 $.t16 $.t17\n" +
                "set.array $b $.t15 $.t17\n" +
                "load res(#6) $.t18\n" +
                "new type(Counter) $.t19 [$.t18]\n" +
                "new type(CounterBag) $.t20 [$.t19]\n" +
                "set.var $.t20 $cb\n" +
                "load res(#0) $.t21\n" +
                "get.array $cb $.t21 $.t22\n" +
                "set.var $.t22 $.s4\n" +
                "load res(#3) $.t23\n" +
                "set.var $.t23 $.s5\n" +
                "load res(#7) $.t24\n" +
                "cmp.ne $.s4 $.t24 $.t25\n" +
                "if $.t25 blk(if2-then) none $.b2\n" +
                "set.var $.s5 $.s6\n" +
                "load res(#3) $.t29\n" +
                "cmp.ne $.s6 $.t29 $.t30\n" +
                "if $.t30 blk(if3-then) blk(if3-else) $.b3\n" +
                "set.var $.s7 $y\n" +
                "invoke fn($makeBag()@Bag) $.t33 []\n" +
                "load res(#8) $.t34\n" +
                "get.array $.t33 $.t34 $.t35\n" +
                "set.var $.t35 $.s8\n" +
                "load res(#3) $.t36\n" +
                "cmp.ne $.s8 $.t36 $.t37\n" +
                "if $.t37 blk(if4-then) blk(if4-else) $.b4\n" +
                "set.var $.s9 $z\n" +
                "add $x $y $.t40\n" +
                "add $.t40 $z $.t41\n" +
                "set.var $.t41 $.s12\n" +
                "load res(#0) $.t42\n" +
                "get.array $b $.t42 $.t43\n" +
                "set.var $.t43 $.s10\n" +
                "load res(#3) $.t44\n" +
                "cmp.ne $.s10 $.t44 $.t45\n" +
                "if $.t45 blk(if5-then) blk(if5-else) $.b5\n" +
                "add $.s12 $.s11 $.t48\n" +
                "ret $.t48\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t7 type(.i32)\n" +
                "set.var $.t7 $.s1\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#0) $.t8\n" +
                "set.var $.t8 $.s1\n" +
                "}\n" +
                ".block if1-then {\n" +
                "cast $.s2 $.t13 type(.i32)\n" +
                "set.var $.t13 $.s3\n" +
                "}\n" +
                ".block if1-else {\n" +
                "load res(#0) $.t14\n" +
                "set.var $.t14 $.s3\n" +
                "}\n" +
                ".block if2-then {\n" +
                "cast $.s4 $.t26 type(Counter)\n" +
                "get.field $.t26 $.t27 field(Counter#value@.i32)\n" +
                "cast $.t27 $.t28 type(.nullable<.i32>)\n" +
                "set.var $.t28 $.s5\n" +
                "}\n" +
                ".block if3-then {\n" +
                "cast $.s6 $.t31 type(.i32)\n" +
                "set.var $.t31 $.s7\n" +
                "}\n" +
                ".block if3-else {\n" +
                "load res(#0) $.t32\n" +
                "set.var $.t32 $.s7\n" +
                "}\n" +
                ".block if4-then {\n" +
                "cast $.s8 $.t38 type(.i32)\n" +
                "set.var $.t38 $.s9\n" +
                "}\n" +
                ".block if4-else {\n" +
                "load res(#0) $.t39\n" +
                "set.var $.t39 $.s9\n" +
                "}\n" +
                ".block if5-then {\n" +
                "cast $.s10 $.t46 type(.i32)\n" +
                "set.var $.t46 $.s11\n" +
                "}\n" +
                ".block if5-else {\n" +
                "load res(#0) $.t47\n" +
                "set.var $.t47 $.s11\n" +
                "}\n");
        }

        // ===== #20②：容器成员 Call 后缀后实例链端到端（Factory.make().field）=====
        private static void TestContainerCallSuffixChainEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Box {\n" +
                "    pub var field: i32\n" +
                "    pub init() { field = 3 }\n" +
                "}\n" +
                "pub class Factory {\n" +
                "    pub static func make(): Box { return new Box() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return Factory.make().field\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（容器 Call 后缀链）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（容器 Call 后缀链）", module);
            BilTestHarness.CheckFnShape("main 指令（静态调用 → get.field）",
                module, "$main()@.i32",
                ".vars { Box .t0, .i32 .t1 }\n" +
                "invoke fn(Factory$.static.make()@Box) $.t0 []\n" +
                "get.field $.t0 $.t1 field(Box#field@.i32)\n" +
                "ret $.t1\n");
        }
    }
}
