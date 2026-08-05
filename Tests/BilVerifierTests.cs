using System.Collections.Generic;
using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// BIL 验证器（BilVerifier，M58）测试：
    /// 正例——中端全管线产出（覆盖 M44–M54 各发射特性）必须验证器零错误；
    /// 负例——手工构造/改造非法模块，按 §21 规则逐类断言命中。
    /// S8c 增补：§13.6 索引严格三元组查询（get.array/set.array 手工模块
    /// 基线正例 + 索引/元素/结果类型不符与无索引运算符负例）。
    /// S8e 增补：访问器与 override 全管线正例 + §21.8 访问器声明负例
    /// （getter(FIELD) 引用未声明字段 / getter 修饰配非 $.get. 形态方法
    /// 符号 / backing 与 computed 共存，手工模块基例）。
    /// M64 增补：§18 hint 指令（string 资源正例 + 非 string 资源 /
    /// 模块外资源负例）。
    /// M75 增补（S11 规范定稿落地）：§12.3 type.is.case（case 符号/
    /// 操作数类型/结果 bool 负例 + case 声明宿主与判别值资源负例）与
    /// §13.3 嵌套字段访问（宿主字段不可解析/非隐藏字段名/宿主类型非
    /// wrapper/内层字段不可解析/静态内层/get·set 类型/宿主对象/const
    /// 写入负例，手工模块基线正例）。
    /// 验证器修复批次增补：namespaced 全局函数 invoke（owner 以 "::"
    /// 结尾无 receiver）、普通参数+值包混合调用（§7.2 值包在普通参数
    /// 之后）、loop.rev DA（§16.4 body 至少一次）、try-finally 无 catch
    /// 判终止、同名不同元数类型共存反查（符号+元数键，手工模块）、
    /// 跨 fn 越权块类型检查不级联、保留名家族空余部/非法字符负例。
    /// const 发射开闸批次增补：§21.8 init 豁免（init 体内写实例 const
    /// 字段正例——set.field 与 set.field.embedded 双形态；普通方法写入
    /// 与 init 内静态写入反例，手工模块）。
    /// TypesCompatible canonical 全等收紧批次增补：内建标量别名 ↔
    /// canonical 与标准构造头 ↔ canonical 泛型宿主（含无边界 .typeid ≡
    /// .typeid<.any>、.pair ↔ core::Pair 嵌套）正例；构造类型实参不同/
    /// 同名不同命名空间/嵌套构造实参不同/同名不同元数负例（手工模块，
    /// set.var 两端比对）。
    /// </summary>
    public static class BilVerifierTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("BilVerifier");

            // ===== 正例：全管线产出验证器零错误 =====
            Positive("hello world",
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"Hello, world!\")\n" +
                "    return 0\n" +
                "}\n");
            Positive("局部声明/赋值/运算",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    x = x * 3\n" +
                "    return x\n" +
                "}\n");
            Positive("带返回值 invoke 与 new",
                "pub func double(a: i32): i32 { return a * 2 }\n" +
                "pub func main(): i32 {\n" +
                "    var d = double(21)\n" +
                "    return d\n" +
                "}\n");
            Positive("实例成员（this/get.field/set.field/实例 invoke）",
                "pub class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "    pub func add(n: i32): i32 { return value + n }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter(1)\n" +
                "    return c.add(2)\n" +
                "}\n");
            Positive("if/值块/短路",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    if ((x > 0) and (x < 10)) { x = x + 1 } else { x = x - 1 }\n" +
                "    return x\n" +
                "}\n");
            Positive("while/break/continue",
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        i = i + 1\n" +
                "        if (i == 5) { continue }\n" +
                "        if (i == 9) { break }\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            Positive("do-while",
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    do { i = i + 1 } while (i < 3)\n" +
                "    return i\n" +
                "}\n");
            Positive("for（迭代协议）",
                "pub func main(): i32 {\n" +
                "    var sum: i32 = 0\n" +
                "    for (i in 0 to 3) { sum = sum + i }\n" +
                "    return sum\n" +
                "}\n");
            Positive("常量 switch",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 2\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 10 }\n" +
                "        (2) -> { return 20 }\n" +
                "        default -> { return 30 }\n" +
                "    }\n" +
                "}\n");
            Positive("throw 与 try/catch/finally",
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.Exception()\n" +
                "    } catch (e: core.Exception) {\n" +
                "        return 1\n" +
                "    } finally (f) {\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            Positive("cast/is/typeOf",
                "pub func main(): i32 {\n" +
                "    var a: Any = 1\n" +
                "    var b: i32 = 0\n" +
                "    if (a is i32) { b = a as i32 }\n" +
                "    var t = typeOf(a)\n" +
                "    return b\n" +
                "}\n");
            Positive("字符串插值",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 42\n" +
                "    core.io.Console.println(\"x = ${x}\")\n" +
                "    return 0\n" +
                "}\n");
            Positive("?. 安全调用",
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func f(u: User?): String? { return u?.name }\n" +
                "pub func main(): i32 { return 0 }\n");
            Positive("if? 空值回退",
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func g(u: User?): User { return u if? new User(\"anon\") }\n" +
                "pub func main(): i32 { return 0 }\n");
            Positive("解构声明",
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "    return v\n" +
                "}\n");
            Positive("索引访问（get.array/set.array，S8c）",
                "pub class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub init() { item = 0 }\n" +
                "    pub operator getAtIndex(index: i32): i32 { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag()\n" +
                "    b[0] = 7\n" +
                "    b[1] += 2\n" +
                "    return b[2]\n" +
                "}\n");
            Positive("访问器（backing/computed/全局自动，S8e）",
                "namespace app\n" +
                "pub var height: i32 {\n" +
                "    pub get\n" +
                "    pub set\n" +
                "} = 200\n" +
                "pub class Counter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { }\n" +
                "    }\n" +
                "    pub var doubled: i32 {\n" +
                "        pub get(_: _) { return value + value }\n" +
                "    }\n" +
                "    pub init() { value = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter()\n" +
                "    c.value = 1\n" +
                "    return (c.value + height) + c.doubled\n" +
                "}\n");
            Positive("override/abstract 投影（S8e）",
                "pub open class Base {\n" +
                "    pub open func area(): i32 { return 0 }\n" +
                "}\n" +
                "pub class Square : Base {\n" +
                "    pub override func area(): i32 { return 1 }\n" +
                "}\n" +
                "pub abstract class Concept {\n" +
                "    pub abstract func id(): i32\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Square()\n" +
                "    return s.area()\n" +
                "}\n");
            // 验证器修复批次：invoke 的 owner 段以 "::" 结尾是命名空间前缀
            // （全局函数），无 receiver——首实参不得被当 .this 吞掉
            Positive("namespaced 全局函数调用（invoke owner 命名空间前缀）",
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    var alarm = sleep(1000)\n" +
                "    return 0\n" +
                "}\n");
            // 验证器修复批次：§7.2 调用序——值包（.vargs/.kwargs）在普通
            // 参数之后逐条比对，只有 .generic.* 前导跳过
            Positive("普通参数 + 值包混合调用（§7.2 调用序）",
                "func sum(first: i32, rest: i32...): i32 { return first }\n" +
                "func config(name: String, options: named i32...): i32 { return 0 }\n" +
                "pub func main(): i32 {\n" +
                "    return sum(1, 2, 3) + config(\"a\", x = 1, y = 2)\n" +
                "}\n");
            // 验证器修复批次：§16.4 loop.rev 执行序 body → judge → condition，
            // body 至少执行一次——judge 与循环出口以 body 出口态分析
            Positive("do-while 体内赋值循环后可见（loop.rev DA）",
                "pub func main(): i32 {\n" +
                "    var x: i32\n" +
                "    do { x = 1 } while (x < 5)\n" +
                "    return x\n" +
                "}\n");
            // 验证器修复批次：try-finally 无 catch 时 catch-table 为空，
            // body 终止即判终止
            Positive("try-finally 无 catch 判终止（entrypoint 结构化）",
                "func f(): i32 {\n" +
                "    try { return 1 } finally (e) { core.io.Console.println(\"f\") }\n" +
                "}\n" +
                "pub func main(): i32 { return f() }\n");

            // ===== S11：§12.3 type.is.case / §13.3 嵌套字段访问（手工模块）=====
            // P3 尚未发射（is .Case 与 wrapper place 归 S11），故不走全管线
            // 正例；以手工模块断言指令形态与验证器规则。宿主 Service 带
            // Logged wrapper 隐藏字段（§5.3 命名）、RequestResult enum + 两
            // case（Failed 判别值资源 R_FC）；main(svc, e) 参数入口已赋值
            BilTestHarness.CheckBilValid("S11 手工模块（基线，is.case + embedded 正例）",
                S11Module(
                    new IsCaseInstruction(BilOp.Var("e"),
                        BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("b")),
                    new GetEmbeddedFieldInstruction(BilOp.Var("svc"), BilOp.Var("lv"),
                        BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#level@.string")),
                    new SetEmbeddedFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                        BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#level@.string"))));

            // ===== 负例：非法模块按规则命中 =====
            NegativeCases();

            return TestHarness.Summary("BilVerifier");
        }

        private static void Positive(string label, string userSource)
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(userSource);
            TestHarness.CheckTrue(label + "：全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            BilTestHarness.CheckBilValid(label + "：验证器零错误", module);
        }

        // 最小合法模块：fn($main()@.i32) + 声明 + entry block（load R_0 → ret）
        private static BilModule MinimalModule(out BilScalarResource zero,
            out BilBlock entry)
        {
            var module = new BilModule();
            zero = new BilScalarResource("R_0", BilScalarType.I32, "0");
            module.Resources.Add(zero);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(zero, BilOp.Var("x")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // 索引验证手工模块（S8c §13.6）：Vec 声明指定 operator 成员 + main
        // （v/i/s/flag 赋值后执行给定的索引指令，ret $x 收尾）——Vec 置于
        // ExternalSymbols（本地非 native 方法必须带 fn 定义，§21.2；手工
        // 模块不构造 operator 函数体，以外部声明形态聚焦指令侧检查）。
        // 负例均在其上改造（替换 operator 声明或索引指令）
        private static BilModule IndexModule(BilSimpleMemberDeclaration[] vecOperators,
            params BilInstruction[] indexInstructions)
        {
            var module = new BilModule();
            var i32Resource = new BilScalarResource("R_0", BilScalarType.I32, "0");
            var stringResource = new BilScalarResource("R_1", BilScalarType.String, "\"a\"");
            var boolResource = new BilScalarResource("R_2", BilScalarType.Bool, "true");
            module.Resources.Add(i32Resource);
            module.Resources.Add(stringResource);
            module.Resources.Add(boolResource);
            var vecDeclaration = new BilTypeDeclaration("Vec", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            foreach (var op in vecOperators)
            {
                vecDeclaration.Members.Add(op);
            }
            module.ExternalSymbols.Add(vecDeclaration);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            main.Vars.Add(new BilVarDeclaration("Vec", "v"));
            main.Vars.Add(new BilVarDeclaration(".i32", "i"));
            main.Vars.Add(new BilVarDeclaration(".string", "s"));
            main.Vars.Add(new BilVarDeclaration(".bool", "flag"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(i32Resource, BilOp.Var("x")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"), BilOp.Var("v"),
                new List<BilVariableOperand>()));
            entry.Instructions.Add(new LoadInstruction(i32Resource, BilOp.Var("i")));
            entry.Instructions.Add(new LoadInstruction(stringResource, BilOp.Var("s")));
            entry.Instructions.Add(new LoadInstruction(boolResource, BilOp.Var("flag")));
            entry.Instructions.AddRange(indexInstructions);
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // Vec 的索引运算符声明（$$名 canonical + pub + operator(名)，§8.4）
        private static BilSimpleMemberDeclaration IndexOperator(string symbol, string name)
        {
            return new BilSimpleMemberDeclaration(BilMemberKind.Method, symbol,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier(name),
                });
        }

        private static void NegativeCases()
        {
            // 基线：最小手工模块本身必须合法（负例均在其上改造）
            BilTestHarness.CheckBilValid("最小手工模块（基线）", MinimalModule(out _, out _));

            // §21.1：版本号
            var m = MinimalModule(out _, out _);
            m.BilVersion = "2.0";
            BilTestHarness.CheckBilInvalid("版本号不受支持", m, "不支持的 BIL 版本");

            // §21.1：保留名声明为局部变量
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i32", ".this"));
            BilTestHarness.CheckBilInvalid("保留名作局部变量", m, "保留名");

            // §21.1：.void 局部变量
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".void", "v"));
            BilTestHarness.CheckBilInvalid(".void 局部变量", m, ".void");

            // §21.5：无 entrypoint
            m = MinimalModule(out _, out var entryBlock);
            entryBlock.Instructions.Clear();
            m.Functions[0].Blocks[0] = new BilBlock("entry");
            m.Functions[0].Blocks[0].Instructions.Add(new RetInstruction());
            BilTestHarness.CheckBilInvalid("无 entrypoint block", m, "恰有一个 entrypoint");

            // §21.5：双 entrypoint
            m = MinimalModule(out _, out _);
            var second = new BilBlock("second", BilBlockModifier.Entrypoint);
            second.Instructions.Add(new RetInstruction());
            m.Functions[0].Blocks.Add(second);
            BilTestHarness.CheckBilInvalid("双 entrypoint block", m, "恰有一个 entrypoint");

            // §21.5：entry 落尾
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.RemoveAt(entryBlock.Instructions.Count - 1);
            BilTestHarness.CheckBilInvalid("entry 落尾", m, "不得以落尾结束");

            // §21.5：跨函数 block 引用（if 引用了别的函数的块）
            m = MinimalModule(out var zero2, out _);
            var otherFn = new BilFunction("$other()@.void");
            otherFn.Args.Add(new BilArgDeclaration(".return", ".void"));
            var otherBlock = new BilBlock("other", BilBlockModifier.Entrypoint);
            otherBlock.Instructions.Add(new RetInstruction());
            otherFn.Blocks.Add(otherBlock);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$other()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            m.Functions.Add(otherFn);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "c"));
            m.Functions[0].Blocks[0].Instructions.Insert(1, new IfInstruction(
                BilOp.Var("c"), otherBlock, null));
            BilTestHarness.CheckBilInvalid("跨函数 block 引用", m, "不属于当前函数");

            // §21.2：未声明变量
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("y")));
            BilTestHarness.CheckBilInvalid("未声明变量", m, "未声明的变量");

            // §21.4：读前未赋值
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.RemoveAt(0);   // 去掉 load，直接 ret $x
            BilTestHarness.CheckBilInvalid("读前未赋值", m, "在赋值前被读取");

            // §21.3：set.var 两端类型不等
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "b"));
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("b")));
            BilTestHarness.CheckBilInvalid("set.var 类型不等", m, "set.var 两端");

            // §21.3：cmp 结果非 .bool
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.Insert(1, new BinaryIntrinsicInstruction(
                BilBinaryOp.CmpEq, BilOp.Var("x"), BilOp.Var("x"), BilOp.Var("x")));
            BilTestHarness.CheckBilInvalid("cmp 结果非 bool", m, "cmp 结果");

            // §21.6：breakid 被普通读写
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "bk"));
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("bk")));
            BilTestHarness.CheckBilInvalid("breakid 普通读写", m, "不得被普通读写");

            // §21.5：非 void 裸 ret
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions[entryBlock.Instructions.Count - 1] = new RetInstruction();
            BilTestHarness.CheckBilInvalid("非 void 裸 ret", m, "不得裸 ret");

            // §21.3：invoke 实参类型不符
            m = MinimalModule(out _, out entryBlock);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$callee(a:.i32)@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var callee = new BilFunction("$callee(a:.i32)@.void");
            callee.Args.Add(new BilArgDeclaration(".return", ".void"));
            callee.Args.Add(new BilArgDeclaration("a", ".i32"));
            var calleeEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            calleeEntry.Instructions.Add(new RetInstruction());
            callee.Blocks.Add(calleeEntry);
            m.Functions.Add(callee);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "flag"));
            entryBlock.Instructions.Insert(1, new InvokeNoResultInstruction(
                BilOp.Fn("$callee(a:.i32)@.void"), new[] { BilOp.Var("flag") }));
            BilTestHarness.CheckBilInvalid("invoke 实参类型不符", m, "invoke 实参 0");

            // §21.2：native 方法不得有 fn 定义
            m = MinimalModule(out _, out _);
            const string nativeSymbol = "core.io::Console$.static.print(text:.string)@.void";
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticMethod,
                nativeSymbol,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.Native),
                    new BilNativeSymbolModifier("print"),
                    new BilNativeLibraryModifier("latte_rt"),
                }));
            var nativeBody = new BilFunction(nativeSymbol);
            nativeBody.Args.Add(new BilArgDeclaration(".return", ".void"));
            nativeBody.Args.Add(new BilArgDeclaration("text", ".string"));
            var nativeEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            nativeEntry.Instructions.Add(new RetInstruction());
            nativeBody.Blocks.Add(nativeEntry);
            m.Functions.Add(nativeBody);
            BilTestHarness.CheckBilInvalid("native 带 fn 定义", m, "native 方法不得存在 fn 定义");

            // §21.2：声明关键字与符号 .static. 标记不一致
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::App$.static.run()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            BilTestHarness.CheckBilInvalid("static 标记不一致", m, "不一致");

            // §21.8：class 带 rich
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Rich", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich)));
            BilTestHarness.CheckBilInvalid("class 带 rich", m, "rich 仅适用");

            // §21.8：wrapper 未带 rich
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public)));
            BilTestHarness.CheckBilInvalid("wrapper 未带 rich", m, "必须显式带 rich");

            // §21.2：资源不属于本模块
            m = MinimalModule(out _, out entryBlock);
            var orphan = new BilScalarResource("R_Orphan", BilScalarType.I32, "1");
            entryBlock.Instructions.Insert(1, new LoadInstruction(orphan, BilOp.Var("x")));
            BilTestHarness.CheckBilInvalid("资源不属于本模块", m, "不属于本模块");

            // §21.2：类型符号不可解析
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration("com.example::Missing", "ghost"));
            entryBlock.Instructions.Insert(1, new NewInstruction(
                BilOp.Type("com.example::Missing"), BilOp.Var("ghost"),
                new List<BilVariableOperand>()));
            BilTestHarness.CheckBilInvalid("类型符号不可解析", m, "不可解析");

            // §21.3：if 条件非 .bool
            m = MinimalModule(out _, out entryBlock);
            var thenBlock = new BilBlock("then");
            m.Functions[0].Blocks.Add(thenBlock);
            entryBlock.Instructions.Insert(1, new IfInstruction(BilOp.Var("x"), thenBlock, null));
            BilTestHarness.CheckBilInvalid("if 条件非 bool", m, "if 条件");

            // §21.3：ret 返回值类型不符
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "bb"));
            entryBlock.Instructions[entryBlock.Instructions.Count - 1] =
                new RetInstruction(BilOp.Var("bb"));
            BilTestHarness.CheckBilInvalid("ret 类型不符", m, "ret 返回值");

            // §21.5：continue 引用 switch token
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "sw"));
            var caseBlock = new BilBlock("case0");
            caseBlock.Instructions.Add(new ContinueInstruction(BilOp.Var("sw")));
            var defaultBlock = new BilBlock("default");
            m.Functions[0].Blocks.Add(caseBlock);
            m.Functions[0].Blocks.Add(defaultBlock);
            var table = new BilSwitchTableResource("R_Sw", ".i32", new List<string> { "1" });
            m.Resources.Add(table);
            entryBlock.Instructions.Insert(1, new SwitchInstruction(
                BilOp.Var("x"), table, new List<BilBlock> { caseBlock }, defaultBlock,
                BilOp.Var("sw")));
            BilTestHarness.CheckBilInvalid("continue 引用 switch token", m,
                "continue 不得引用 switch");

            // §21.4：if 分支合并后读取（then 内赋值的变量，合并后视为未赋值）
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "cond3"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "y"));
            var trueRes = new BilScalarResource("R_T3", BilScalarType.Bool, "true");
            m.Resources.Add(trueRes);
            var thenAssign = new BilBlock("then-assign");
            thenAssign.Instructions.Add(new SetVarInstruction(BilOp.Var("x"), BilOp.Var("y")));
            m.Functions[0].Blocks.Add(thenAssign);
            entryBlock.Instructions.Insert(1, new LoadInstruction(trueRes, BilOp.Var("cond3")));
            entryBlock.Instructions.Insert(2, new IfInstruction(
                BilOp.Var("cond3"), thenAssign, null));
            entryBlock.Instructions.Insert(3, new SetVarInstruction(BilOp.Var("y"), BilOp.Var("x")));
            // y 只在 then 分支赋值，if 落尾后读取 y —— 合并取交集，报未赋值；
            // 同时 then 内 set.var $x $y 读取未赋值的 y 也报（同变量去重后各一条）
            BilTestHarness.CheckBilInvalid("if 分支合并后读取", m, "在赋值前被读取");

            // §21.4：judge 未写 condition
            m = MinimalModule(out zero2, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "cond2"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "lp2"));
            var body2 = new BilBlock("loop-body");
            var judge2 = new BilBlock("loop-judge");
            m.Functions[0].Blocks.Add(body2);
            m.Functions[0].Blocks.Add(judge2);
            var trueResource = new BilScalarResource("R_T", BilScalarType.Bool, "true");
            m.Resources.Add(trueResource);
            entryBlock.Instructions.Insert(1, new LoadInstruction(trueResource, BilOp.Var("cond2")));
            entryBlock.Instructions.Insert(2, new LoopInstruction(
                BilOp.Var("cond2"), body2, null, judge2, BilOp.Var("lp2"), false));
            BilTestHarness.CheckBilInvalid("judge 未写 condition", m, "未对条件变量");

            // ===== S8c：§13.6 索引严格三元组查询 =====
            // 基线：手工索引模块本身必须合法（get.array/set.array 正例）
            BilTestHarness.CheckBilValid("索引手工模块（基线，get/set 正例）",
                IndexModule(BothIndexOperators(),
                    new GetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("s")),
                    new SetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("s"))));

            // §13.6：set.array 索引类型与 setAtIndex param[0] 不符
            BilTestHarness.CheckBilInvalid("set.array 索引类型不符（无精确匹配）",
                IndexModule(BothIndexOperators(),
                    new SetArrayInstruction(BilOp.Var("v"), BilOp.Var("flag"), BilOp.Var("s"))),
                "无精确匹配");

            // §13.6：set.array 元素类型与 setAtIndex param[1] 不符
            BilTestHarness.CheckBilInvalid("set.array 元素类型不符（无精确匹配）",
                IndexModule(BothIndexOperators(),
                    new SetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("i"))),
                "无精确匹配");

            // §13.6：get.array 结果类型与 getAtIndex 返回类型不符
            BilTestHarness.CheckBilInvalid("get.array 结果类型不符",
                IndexModule(BothIndexOperators(),
                    new GetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("x"))),
                "get.array 目标变量");

            // §13.6：可解析类型无索引运算符实现
            BilTestHarness.CheckBilInvalid("可解析类型无索引运算符",
                IndexModule(new BilSimpleMemberDeclaration[0],
                    new GetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("s"))),
                "没有 getAtIndex 索引运算符实现");

            // ===== S8e：§21.8 访问器声明 =====
            // 基线：手工访问器模块本身必须合法（字段 local 裸条目 + 访问器
            // 方法声明 external——外部声明无需 fn 定义，聚焦声明侧修饰规则）
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "#v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Backing),
                    new BilKeywordModifier(BilKeyword.Readable),
                }));
            m.ExternalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$.get.v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilAccessorModifier(BilAccessorKind.Getter, "#v@.i32"),
                }));
            BilTestHarness.CheckBilValid("访问器手工模块（基线，getter 正例）", m);

            // §21.8：getter(FIELD) 的 FIELD 必须可解析为已声明字段符号
            m.ExternalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$.get.w@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilAccessorModifier(BilAccessorKind.Getter, "#w@.i32"),
                }));
            BilTestHarness.CheckBilInvalid("getter(FIELD) 引用未声明字段", m,
                "访问器修饰引用的字段符号不可解析");

            // §21.8：getter 修饰与方法符号形态必须一致（非 $.get. 形态即拒）
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "#v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Backing),
                    new BilKeywordModifier(BilKeyword.Readable),
                }));
            m.ExternalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$v()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilAccessorModifier(BilAccessorKind.Getter, "#v@.i32"),
                }));
            BilTestHarness.CheckBilInvalid("getter 修饰配非 $.get. 形态方法符号", m,
                "修饰与方法符号形态不符");

            // §21.8：backing 与 computed 是互斥的存储形态标记
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "#v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Backing),
                    new BilKeywordModifier(BilKeyword.Computed),
                }));
            BilTestHarness.CheckBilInvalid("backing 与 computed 共存", m,
                "backing 与 computed 不得共存");

            // ===== M64：§18 hint 指令 =====
            // 基线：string 资源 + entry block 内 hint——合法（纯位置标记，
            // 不读写变量、不参与 DA、不是终结指令）
            m = MinimalModule(out _, out entryBlock);
            var hintResource = new BilScalarResource("R_Hint", BilScalarType.String, "\"{}\"");
            m.Resources.Add(hintResource);
            entryBlock.Instructions.Insert(0, new HintInstruction(hintResource));
            BilTestHarness.CheckBilValid("hint 指令（string 资源，正例）", m);

            // §21.3：hint 引用非 string 资源即非法
            m = MinimalModule(out var hintI64, out entryBlock);
            entryBlock.Instructions.Insert(0, new HintInstruction(hintI64));
            BilTestHarness.CheckBilInvalid("hint 引用非 string 资源", m,
                "必须是 string 资源");

            // §21.2：hint 引用的资源必须属于本模块
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.Insert(0, new HintInstruction(
                new BilScalarResource("R_Foreign", BilScalarType.String, "\"{}\"")));
            BilTestHarness.CheckBilInvalid("hint 引用模块外资源", m,
                "不属于本模块");

            // ===== S11：§12.3 type.is.case 与 §13.3 嵌套字段访问负例 =====
            // 基线见 RunAll 的 S11Module 正例；负例均在其上改造

            // §21.2：is.case 的 case 符号未登记
            m = S11Module(new IsCaseInstruction(BilOp.Var("e"),
                BilOp.Case("com.example::RequestResult.Missing"), BilOp.Var("b")));
            BilTestHarness.CheckBilInvalid("is.case case 符号不可解析", m,
                "case 符号不可解析");

            // §21.3：is.case 操作数类型必须严格等于 case 的 enum 类型
            m = S11Module(new IsCaseInstruction(BilOp.Var("svc"),
                BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("b")));
            BilTestHarness.CheckBilInvalid("is.case 操作数类型不符", m,
                "type.is.case 的操作数");

            // §21.3：is.case 结果必须为 .bool
            m = S11Module(new IsCaseInstruction(BilOp.Var("e"),
                BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("lv")));
            BilTestHarness.CheckBilInvalid("is.case 结果非 bool", m,
                "type.is.case 结果");

            // §21.2：case 声明宿主不是 enum-struct（声明侧）
            m = MinimalModule(out _, out _);
            var classWithCase = new BilTypeDeclaration("com.example::Service",
                BilTypeKind.Class, new BilAccessibilityModifier(BilAccessibility.Public));
            classWithCase.Members.Add(new BilCaseDeclaration("com.example::Service.Foo"));
            m.LocalSymbols.Add(classWithCase);
            BilTestHarness.CheckBilInvalid("case 声明宿主非 enum-struct", m,
                "宿主类型不是 enum-struct");

            // §21.2：case 判别值资源未登记
            m = MinimalModule(out _, out _);
            var orphanCase = new BilTypeDeclaration("com.example::RequestResult",
                BilTypeKind.EnumStruct, new BilAccessibilityModifier(BilAccessibility.Public));
            orphanCase.Members.Add(new BilCaseDeclaration("com.example::RequestResult.Failed",
                new[] { new BilCaseParameter("errorCode", ".i32") },
                discriminantResource: "R_OrphanCase"));
            m.LocalSymbols.Add(orphanCase);
            BilTestHarness.CheckBilInvalid("case 判别值资源未登记", m,
                "未登记");

            // §21.2：embedded 宿主字段符号不可解析
            m = S11Module(new GetEmbeddedFieldInstruction(BilOp.Var("svc"), BilOp.Var("lv"),
                BilOp.Field("com.example::Service#name@.string"),
                BilOp.Field("core.logging::Logged#level@.string")));
            BilTestHarness.CheckBilInvalid("embedded 宿主字段不可解析", m,
                "宿主字段符号不可解析");

            // §21.3：embedded 宿主字段名必须以 .wrapper. 开头（§5.3 保留名）
            m = S11Module(
                new GetEmbeddedFieldInstruction(BilOp.Var("svc"), BilOp.Var("lv"),
                    BilOp.Field("com.example::Service#note@.string"),
                    BilOp.Field("core.logging::Logged#level@.string")));
            var serviceWithNote = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "com.example::Service" });
            serviceWithNote.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Service#note@.string",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            BilTestHarness.CheckBilInvalid("embedded 宿主字段非隐藏字段", m,
                "不是 wrapper 隐藏字段");

            // §21.3：embedded 宿主字段类型必须解析为 wrapper 类型——声明
            // 可反查且非 wrapper 时报错（查不到声明的场景按降级原则跳过，
            // 与 VerifyWrapperType 一致）
            m = S11Module(
                new GetEmbeddedFieldInstruction(BilOp.Var("svc"), BilOp.Var("lv"),
                    BilOp.Field("com.example::Service#.wrapper.com.example::Plain@com.example::Plain"),
                    BilOp.Field("core.logging::Logged#level@.string")));
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Plain",
                BilTypeKind.Class, new BilAccessibilityModifier(BilAccessibility.Public)));
            var serviceWithFake = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "com.example::Service" });
            serviceWithFake.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Service#.wrapper.com.example::Plain@com.example::Plain",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            BilTestHarness.CheckBilInvalid("embedded 宿主字段类型非 wrapper", m,
                "不是 wrapper 类型");

            // §21.2：embedded 内层字段符号不可解析
            m = S11Module(new GetEmbeddedFieldInstruction(BilOp.Var("svc"), BilOp.Var("lv"),
                BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#ghost@.string")));
            BilTestHarness.CheckBilInvalid("embedded 内层字段不可解析", m,
                "内层字段符号不可解析");

            // §21.3：embedded 内层字段必须是实例字段（静态字段拒绝）
            m = S11Module(
                new GetEmbeddedFieldInstruction(BilOp.Var("svc"), BilOp.Var("lv"),
                    BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#.static.flag@.bool")));
            var loggedWithStatic = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "core.logging::Logged" });
            loggedWithStatic.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.logging::Logged#.static.flag@.bool",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            BilTestHarness.CheckBilInvalid("embedded 内层字段为静态字段", m,
                "必须是实例字段");

            // §21.3：embedded get 结果类型必须严格等于内层字段类型
            m = S11Module(new GetEmbeddedFieldInstruction(BilOp.Var("svc"), BilOp.Var("x"),
                BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#level@.string")));
            BilTestHarness.CheckBilInvalid("embedded get 结果类型不符", m,
                "get.field.embedded 目标变量");

            // §21.3：embedded set 源类型必须严格等于内层字段类型
            m = S11Module(new SetEmbeddedFieldInstruction(BilOp.Var("b"), BilOp.Var("svc"),
                BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#level@.string")));
            BilTestHarness.CheckBilInvalid("embedded set 源类型不符", m,
                "set.field.embedded 源变量");

            // §21.3：embedded 宿主对象类型必须可赋值到宿主字段 owner
            m = S11Module(new GetEmbeddedFieldInstruction(BilOp.Var("e"), BilOp.Var("lv"),
                BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#level@.string")));
            BilTestHarness.CheckBilInvalid("embedded 宿主对象类型不符", m,
                "宿主对象");

            // §21.8：embedded set 不得写入 const 内层字段
            m = S11Module(
                new SetEmbeddedFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                    BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#tag@.string")));
            var loggedWithConst = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "core.logging::Logged" });
            loggedWithConst.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.logging::Logged#tag@.string",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            BilTestHarness.CheckBilInvalid("embedded set 写 const 内层字段", m,
                "不得被写入");

            // ===== §21.8 init 豁免（对齐 P3 ConstFieldRules，SYNTAX §9.3）=====
            // init 方法（§8.4 init 修饰符标识）体内写实例 const 字段放行
            // （构造期一次性赋值；豁免不限字段宿主==函数宿主，继承的基类
            // 字段同放行——全管线继承形态见正例「解构声明」）；普通方法
            // 写入与 init 内静态写入仍拒
            BilTestHarness.CheckBilValid("init 内写实例 const 字段（init 豁免，正例）",
                ConstWriteModule(ConstWriteTarget.InstanceInInit));
            BilTestHarness.CheckBilInvalid("普通方法写实例 const 字段（不豁免）",
                ConstWriteModule(ConstWriteTarget.InstanceInMethod), "不得被写入");
            BilTestHarness.CheckBilInvalid("init 内写静态 const 字段（静态不豁免）",
                ConstWriteModule(ConstWriteTarget.StaticInInit), "不得被写入");
            BilTestHarness.CheckBilValid("init 内 embedded 写 const 内层字段（init 豁免，正例）",
                S11InitEmbeddedModule());

            // ===== 验证器修复批次 =====
            // S10：同名不同元数类型共存——声明反查键 = 符号 + 泛型元数
            // （Wrap init() 与 Wrap\<T\> init(x: .i32)，两种声明顺序各验
            // 一次：new 两形态按元数各自命中 init，互不遮蔽）
            BilTestHarness.CheckBilValid("同名不同元数共存（plain 先声明，正例）",
                ArityCoexistModule(genericFirst: false));
            BilTestHarness.CheckBilValid("同名不同元数共存（generic 先声明，正例）",
                ArityCoexistModule(genericFirst: true));

            // §21.5/§21.3：跨函数 block 引用的越权块不展开——块内类型错误
            // 只在所属 fn 视角报一次，不经引用点级联（§21.3 类型检查与
            // §21.5 结构检查同走块成员资格过滤）
            m = MinimalModule(out var i32Res, out _);
            var foreignFn = new BilFunction("$foreign()@.void");
            foreignFn.Args.Add(new BilArgDeclaration(".return", ".void"));
            foreignFn.Vars.Add(new BilVarDeclaration(".bool", "fb"));
            foreignFn.Vars.Add(new BilVarDeclaration(".i32", "fi"));
            var foreignBlock = new BilBlock("foreign", BilBlockModifier.Entrypoint);
            var boolResCf = new BilScalarResource("R_CF", BilScalarType.Bool, "true");
            m.Resources.Add(boolResCf);
            foreignBlock.Instructions.Add(new LoadInstruction(i32Res, BilOp.Var("fi")));
            foreignBlock.Instructions.Add(new LoadInstruction(boolResCf, BilOp.Var("fb")));
            foreignBlock.Instructions.Add(new SetVarInstruction(BilOp.Var("fi"), BilOp.Var("fb")));
            foreignBlock.Instructions.Add(new RetInstruction());
            foreignFn.Blocks.Add(foreignBlock);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$foreign()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            m.Functions.Add(foreignFn);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "cf"));
            m.Functions[0].Blocks[0].Instructions.Insert(1,
                new LoadInstruction(boolResCf, BilOp.Var("cf")));
            m.Functions[0].Blocks[0].Instructions.Insert(2, new IfInstruction(
                BilOp.Var("cf"), foreignBlock, null));
            var cascadeErrors = BilVerifier.Verify(m);
            TestHarness.CheckTrue("越权块类型错误不级联（越权一条 + 所属 fn 一条）",
                cascadeErrors.Count == 2
                && cascadeErrors.Count(e => e.Message.Contains("不属于当前函数")) == 1
                && cascadeErrors.Count(e => e.Message.Contains("set.var 两端")) == 1,
                "实际: " + string.Join("; ", cascadeErrors.Select(e => e.ToString())));

            // §21.1：保留名家族（.generic./.vargs./.kwargs.）放行前缀后余部
            // 仍须合法——空余部与含非法字符均拒绝
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".typeid", ".generic."));
            BilTestHarness.CheckBilInvalid("保留名家族空余部（.generic.）", m,
                "非法局部变量名");
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".array<.any>", ".vargs.bad name"));
            BilTestHarness.CheckBilInvalid("保留名家族余部非法字符", m,
                "非法局部变量名");

            // ===== §6.4 类型严格相等（TypesCompatible 收紧为 canonical 全等）=====
            // 手工模块：main(a: sourceType, b: targetType) 参数入口已赋值
            // （DA 免扰），entry 内 set.var b = a 触发两端类型比对

            // 内建标量别名 ↔ canonical 是同一类型的两种拼写（§6.2）——仍兼容
            BilTestHarness.CheckBilValid("内建别名兼容（.i32 ↔ core::i32，正例）",
                TypeCompatModule(".i32", "core::i32"));
            BilTestHarness.CheckBilValid("内建别名兼容（core::String ↔ .string，正例）",
                TypeCompatModule("core::String", ".string"));
            // §6.3：无边界 .typeid ≡ .typeid<.any>
            BilTestHarness.CheckBilValid("无边界 .typeid ≡ .typeid<.any>（正例）",
                TypeCompatModule(".typeid", ".typeid<.any>"));
            // 标准构造头 ↔ canonical 泛型宿主（§6.3）——发射器 .kwargs 契约
            // 形态（.array<.pair<.string, .any>>）与调用点打包形态
            // （.array<core::Pair<.string, .any>>）的混用即此等价
            var corePair = new BilTypeDeclaration("core::Pair", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            corePair.GenericParameters.Add("TKey");
            corePair.GenericParameters.Add("TValue");
            BilTestHarness.CheckBilValid("构造头别名兼容（.pair ↔ core::Pair 嵌套，正例）",
                TypeCompatModule(".array<.pair<.string, .any>>",
                    ".array<core::Pair<.string, .any>>", corePair));

            // 构造类型实参不同不兼容（§6.4：Array\<Dog> ≢ Array\<Animal>）
            BilTestHarness.CheckBilInvalid("构造类型实参不同不兼容",
                TypeCompatModule(".array<.string>", ".array<.i32>"), "set.var 两端");
            // 不同命名空间的同名类型不兼容
            BilTestHarness.CheckBilInvalid("同名不同命名空间不兼容",
                TypeCompatModule("one::Box", "two::Box",
                    new BilTypeDeclaration("one::Box", BilTypeKind.Class,
                        new BilAccessibilityModifier(BilAccessibility.Public)),
                    new BilTypeDeclaration("two::Box", BilTypeKind.Class,
                        new BilAccessibilityModifier(BilAccessibility.Public))),
                "set.var 两端");
            // 嵌套构造逐实参递归全等
            BilTestHarness.CheckBilInvalid("嵌套构造实参不同不兼容",
                TypeCompatModule(".array<.array<.string>>", ".array<.array<.i32>>"),
                "set.var 两端");
            // 同名不同元数不兼容（Wrap ≢ Wrap\<T\>）
            var wrapPlainCompat = new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            var wrapGenericCompat = new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            wrapGenericCompat.GenericParameters.Add("T");
            BilTestHarness.CheckBilInvalid("同名不同元数不兼容（Wrap ≢ Wrap<.i32>）",
                TypeCompatModule("com.example::Wrap", "com.example::Wrap<.i32>",
                    wrapPlainCompat, wrapGenericCompat),
                "set.var 两端");
        }

        // Vec 的索引运算符对（get 返回 .string / set 元素 .string，索引皆 .i32）
        private static BilSimpleMemberDeclaration[] BothIndexOperators()
        {
            return new[]
            {
                IndexOperator("Vec$$getAtIndex(index:.i32)@.string", "getAtIndex"),
                IndexOperator("Vec$$setAtIndex(index:.i32,element:.string)@.void", "setAtIndex"),
            };
        }

        // S11 手工模块（§12.3/§13.3）：core.logging::Logged wrapper（rich，
        // 实例字段 level）+ com.example::Service class（§5.3 wrapper 隐藏
        // 字段）+ com.example::RequestResult enum-struct（Success 自动判别 /
        // Failed 判别值资源 R_FC）+ main(svc, e)（参数入口已赋值，DA 免扰）——
        // 正例与负例共用；body 指令后以 ret $x 收尾
        private static BilModule S11Module(params BilInstruction[] body)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_FC", BilScalarType.I32, "0"));
            module.Resources.Add(new BilScalarResource("R_X", BilScalarType.I32, "0"));

            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.logging::Logged#level@.string",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(logged);

            var service = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                HostWrapperField,
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(service);

            var result = new BilTypeDeclaration("com.example::RequestResult",
                BilTypeKind.EnumStruct, new BilAccessibilityModifier(BilAccessibility.Public));
            result.Members.Add(new BilCaseDeclaration("com.example::RequestResult.Success"));
            result.Members.Add(new BilCaseDeclaration("com.example::RequestResult.Failed",
                new[] { new BilCaseParameter("errorCode", ".i32") },
                discriminantResource: "R_FC"));
            module.LocalSymbols.Add(result);

            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main(svc:com.example::Service,e:com.example::RequestResult)@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction(
                "$main(svc:com.example::Service,e:com.example::RequestResult)@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Args.Add(new BilArgDeclaration("svc", "com.example::Service"));
            main.Args.Add(new BilArgDeclaration("e", "com.example::RequestResult"));
            main.Vars.Add(new BilVarDeclaration(".string", "lv"));
            main.Vars.Add(new BilVarDeclaration(".bool", "b"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[1], BilOp.Var("x")));
            entry.Instructions.AddRange(body);
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // §5.3 wrapper 隐藏字段 canonical：宿主 Service 的 Logged 字段
        private const string HostWrapperField =
            "com.example::Service#.wrapper.core.logging::Logged@core.logging::Logged";

        // S10 同名不同元数共存手工模块（验证器修复批次）：com.example::Wrap
        // （init()）与 com.example::Wrap\<T\>（init(x: .i32)）——external
        // 声明形态聚焦指令侧（免 fn 定义）；genericFirst 控制两声明的登记
        // 顺序；main 内 new 两形态各一次（x 已由 R_0 赋值，实参类型 .i32）
        private static BilModule ArityCoexistModule(bool genericFirst)
        {
            var module = MinimalModule(out _, out var entry);
            var wrapPlain = new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            wrapPlain.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Wrap$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            var wrapGeneric = new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            wrapGeneric.GenericParameters.Add("T");
            wrapGeneric.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Wrap$init(x:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            if (genericFirst)
            {
                module.ExternalSymbols.Add(wrapGeneric);
                module.ExternalSymbols.Add(wrapPlain);
            }
            else
            {
                module.ExternalSymbols.Add(wrapPlain);
                module.ExternalSymbols.Add(wrapGeneric);
            }
            module.Functions[0].Vars.Add(new BilVarDeclaration("com.example::Wrap", "w0"));
            module.Functions[0].Vars.Add(new BilVarDeclaration("com.example::Wrap<.i32>", "w1"));
            entry.Instructions.Insert(1, new NewInstruction(
                BilOp.Type("com.example::Wrap"), BilOp.Var("w0"), new List<BilVariableOperand>()));
            entry.Instructions.Insert(2, new NewInstruction(
                BilOp.Type("com.example::Wrap<.i32>"), BilOp.Var("w1"),
                new List<BilVariableOperand> { BilOp.Var("x") }));
            return module;
        }

        // §6.4 类型严格相等手工模块（TypesCompatible canonical 全等收紧批次）：
        // main(a: sourceType, b: targetType) 两参数入口已赋值（DA 免构造），
        // entry 内 load x 后 set.var b = a 触发两端类型比对，ret x 收尾；
        // extraTypes 登记额外类型声明（core::Pair/one::Box 等使类型引用
        // 可解析，§21.2——构造头免检声明，用户 canonical 类型须登记）
        private static BilModule TypeCompatModule(string sourceType, string targetType,
            params BilTypeDeclaration[] extraTypes)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "0"));
            foreach (var type in extraTypes)
            {
                module.ExternalSymbols.Add(type);
            }
            var symbol = $"$main(a:{sourceType},b:{targetType})@.i32";
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                symbol, new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction(symbol);
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Args.Add(new BilArgDeclaration("a", sourceType));
            main.Args.Add(new BilArgDeclaration("b", targetType));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("x")));
            entry.Instructions.Add(new SetVarInstruction(BilOp.Var("b"), BilOp.Var("a")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // §21.8 init 豁免手工模块：com.example::Entry class（const 实例字段
        // key + const 静态字段 count + init(k)/普通方法 reset(k) 两 fn 定义；
        // 参数入口已赋值、n 先 load 后用，DA 免扰）——writeTarget 选择写入
        // 指令落点：init 体内写实例字段=豁免正例 / 普通方法写实例字段=反例 /
        // init 体内写静态字段=反例（静态不在豁免内，与 P3 对齐）
        private enum ConstWriteTarget { InstanceInInit, InstanceInMethod, StaticInInit }

        private static BilModule ConstWriteModule(ConstWriteTarget writeTarget)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_CW", BilScalarType.I32, "0"));
            var entry = new BilTypeDeclaration("com.example::Entry", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            entry.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Entry#key@.string",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            entry.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticField,
                "com.example::Entry#.static.count@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            entry.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Entry$init(k:.string)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            entry.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Entry$reset(k:.string)@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(entry);

            var init = new BilFunction("com.example::Entry$init(k:.string)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "com.example::Entry"));
            init.Args.Add(new BilArgDeclaration("k", ".string"));
            init.Vars.Add(new BilVarDeclaration(".i32", "n"));
            var initBlock = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initBlock.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("n")));
            if (writeTarget == ConstWriteTarget.InstanceInInit)
            {
                initBlock.Instructions.Add(new SetFieldInstruction(BilOp.Var("k"),
                    BilOp.Var(".this"), BilOp.Field("com.example::Entry#key@.string")));
            }
            if (writeTarget == ConstWriteTarget.StaticInInit)
            {
                initBlock.Instructions.Add(new SetFieldStaticInstruction(BilOp.Var("n"),
                    BilOp.Type("com.example::Entry"),
                    BilOp.Field("com.example::Entry#.static.count@.i32")));
            }
            initBlock.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initBlock);
            module.Functions.Add(init);

            var reset = new BilFunction("com.example::Entry$reset(k:.string)@.void");
            reset.Args.Add(new BilArgDeclaration(".return", ".void"));
            reset.Args.Add(new BilArgDeclaration(".this", "com.example::Entry"));
            reset.Args.Add(new BilArgDeclaration("k", ".string"));
            var resetBlock = new BilBlock("entry", BilBlockModifier.Entrypoint);
            if (writeTarget == ConstWriteTarget.InstanceInMethod)
            {
                resetBlock.Instructions.Add(new SetFieldInstruction(BilOp.Var("k"),
                    BilOp.Var(".this"), BilOp.Field("com.example::Entry#key@.string")));
            }
            resetBlock.Instructions.Add(new RetInstruction());
            reset.Blocks.Add(resetBlock);
            module.Functions.Add(reset);
            return module;
        }

        // §21.8 init 豁免（§13.3 embedded 形态）手工模块：Logged wrapper
        // （const 内层字段 level）+ Service class（§5.3 wrapper 隐藏字段 +
        // init(lv) 声明）+ init fn 定义（体内 set.field.embedded 写 const
        // 内层字段）——embedded 写入与 set.field 同豁免规则
        private static BilModule S11InitEmbeddedModule()
        {
            var module = new BilModule();
            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.logging::Logged#level@.string",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            module.LocalSymbols.Add(logged);

            var service = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                HostWrapperField,
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Service$init(lv:.string)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(service);

            var init = new BilFunction("com.example::Service$init(lv:.string)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "com.example::Service"));
            init.Args.Add(new BilArgDeclaration("lv", ".string"));
            var initBlock = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initBlock.Instructions.Add(new SetEmbeddedFieldInstruction(BilOp.Var("lv"),
                BilOp.Var(".this"),
                BilOp.Field(HostWrapperField), BilOp.Field("core.logging::Logged#level@.string")));
            initBlock.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initBlock);
            module.Functions.Add(init);
            return module;
        }
    }
}
