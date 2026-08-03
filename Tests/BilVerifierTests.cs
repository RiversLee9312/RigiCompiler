using System.Collections.Generic;
using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// BIL 验证器（BilVerifier，M58）测试：
    /// 正例——中端全管线产出（覆盖 M44–M54 各发射特性）必须验证器零错误；
    /// 负例——手工构造/改造非法模块，按 §20 规则逐类断言命中。
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

        private static void NegativeCases()
        {
            // 基线：最小手工模块本身必须合法（负例均在其上改造）
            BilTestHarness.CheckBilValid("最小手工模块（基线）", MinimalModule(out _, out _));

            // §20.1：版本号
            var m = MinimalModule(out _, out _);
            m.BilVersion = "2.0";
            BilTestHarness.CheckBilInvalid("版本号不受支持", m, "不支持的 BIL 版本");

            // §20.1：保留名声明为局部变量
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i32", ".this"));
            BilTestHarness.CheckBilInvalid("保留名作局部变量", m, "保留名");

            // §20.1：.void 局部变量
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".void", "v"));
            BilTestHarness.CheckBilInvalid(".void 局部变量", m, ".void");

            // §20.5：无 entrypoint
            m = MinimalModule(out _, out var entryBlock);
            entryBlock.Instructions.Clear();
            m.Functions[0].Blocks[0] = new BilBlock("entry");
            m.Functions[0].Blocks[0].Instructions.Add(new RetInstruction());
            BilTestHarness.CheckBilInvalid("无 entrypoint block", m, "恰有一个 entrypoint");

            // §20.5：双 entrypoint
            m = MinimalModule(out _, out _);
            var second = new BilBlock("second", BilBlockModifier.Entrypoint);
            second.Instructions.Add(new RetInstruction());
            m.Functions[0].Blocks.Add(second);
            BilTestHarness.CheckBilInvalid("双 entrypoint block", m, "恰有一个 entrypoint");

            // §20.5：entry 落尾
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.RemoveAt(entryBlock.Instructions.Count - 1);
            BilTestHarness.CheckBilInvalid("entry 落尾", m, "不得以落尾结束");

            // §20.5：跨函数 block 引用（if 引用了别的函数的块）
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

            // §20.2：未声明变量
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("y")));
            BilTestHarness.CheckBilInvalid("未声明变量", m, "未声明的变量");

            // §20.4：读前未赋值
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.RemoveAt(0);   // 去掉 load，直接 ret $x
            BilTestHarness.CheckBilInvalid("读前未赋值", m, "在赋值前被读取");

            // §20.3：set.var 两端类型不等
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "b"));
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("b")));
            BilTestHarness.CheckBilInvalid("set.var 类型不等", m, "set.var 两端");

            // §20.3：cmp 结果非 .bool
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.Insert(1, new BinaryIntrinsicInstruction(
                BilBinaryOp.CmpEq, BilOp.Var("x"), BilOp.Var("x"), BilOp.Var("x")));
            BilTestHarness.CheckBilInvalid("cmp 结果非 bool", m, "cmp 结果");

            // §20.6：breakid 被普通读写
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "bk"));
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("bk")));
            BilTestHarness.CheckBilInvalid("breakid 普通读写", m, "不得被普通读写");

            // §20.5：非 void 裸 ret
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions[entryBlock.Instructions.Count - 1] = new RetInstruction();
            BilTestHarness.CheckBilInvalid("非 void 裸 ret", m, "不得裸 ret");

            // §20.3：invoke 实参类型不符
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

            // §20.2：native 方法不得有 fn 定义
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

            // §20.2：声明关键字与符号 .static. 标记不一致
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::App$.static.run()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            BilTestHarness.CheckBilInvalid("static 标记不一致", m, "不一致");

            // §20.8：class 带 rich
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Rich", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich)));
            BilTestHarness.CheckBilInvalid("class 带 rich", m, "rich 仅适用");

            // §20.8：wrapper 未带 rich
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public)));
            BilTestHarness.CheckBilInvalid("wrapper 未带 rich", m, "必须显式带 rich");

            // §20.2：资源不属于本模块
            m = MinimalModule(out _, out entryBlock);
            var orphan = new BilScalarResource("R_Orphan", BilScalarType.I32, "1");
            entryBlock.Instructions.Insert(1, new LoadInstruction(orphan, BilOp.Var("x")));
            BilTestHarness.CheckBilInvalid("资源不属于本模块", m, "不属于本模块");

            // §20.2：类型符号不可解析
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration("com.example::Missing", "ghost"));
            entryBlock.Instructions.Insert(1, new NewInstruction(
                BilOp.Type("com.example::Missing"), BilOp.Var("ghost"),
                new List<BilVariableOperand>()));
            BilTestHarness.CheckBilInvalid("类型符号不可解析", m, "不可解析");

            // §20.3：if 条件非 .bool
            m = MinimalModule(out _, out entryBlock);
            var thenBlock = new BilBlock("then");
            m.Functions[0].Blocks.Add(thenBlock);
            entryBlock.Instructions.Insert(1, new IfInstruction(BilOp.Var("x"), thenBlock, null));
            BilTestHarness.CheckBilInvalid("if 条件非 bool", m, "if 条件");

            // §20.3：ret 返回值类型不符
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "bb"));
            entryBlock.Instructions[entryBlock.Instructions.Count - 1] =
                new RetInstruction(BilOp.Var("bb"));
            BilTestHarness.CheckBilInvalid("ret 类型不符", m, "ret 返回值");

            // §20.5：continue 引用 switch token
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

            // §20.4：if 分支合并后读取（then 内赋值的变量，合并后视为未赋值）
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

            // §20.4：judge 未写 condition
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
        }
    }
}
