using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Middleware;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// Middleware 套件：
    /// - Gate 门禁（BIL §23：解析错误与验证错误的 BIL 必须被拒，错误人类可读）；
    /// - 驻留符号表（类型/成员登记、外部引用、驻留=引用相等）；
    /// - MIR 构造（BIL → CFG 直译形状、MwContext 挂载）；
    /// - 实现绑定（类型驱动操作的唯一实现查询：primitive/运行时面/native/直接调用）；
    /// - LLVM 模块构建与 .o 发射（LLVMSharp 进程内管线、.ll 黄金锚点）；
    /// - 受控失败（合法但超出现阶段的 BIL → MwNotSupportedException，非崩溃）；
    /// - native CLI 端到端（参数校验、拒绝路径、发射落盘、stdout 纯净）。
    /// </summary>
    public static class MiddlewareTests
    {
        // 合法的最小手写模块（符号表用例覆盖类型/成员/外部引用/全局函数）
        private const string MinimalValidBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"symtest\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .type Point = class pub {\n" +
            "        .field Point#x@.i32 pub var\n" +
            "        .field Point#y@.i32 pub var\n" +
            "    }\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "    .type core.String = class pub {\n" +
            "        .method core.String$get_Length()@.i32 pub\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .i32 a\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_Zero) $a\n" +
            "        ret $a\n" +
            "    }\n" +
            "}\n";

        // 验证器必拒的手写模块：ret 引用未声明变量 $missing（§21 违规）
        private const string UndeclaredVarBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        ret $missing\n" +
            "    }\n" +
            "}\n";

        // hello world + 字符串拼接的手写模块（.ll 黄金锚点用例）
        private const string HelloConcatBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"hello\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_Hello = string \"Hello, \",\n" +
            "    R_World = string \"world!\\n\",\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "    .type core.io::Console = class pub {\n" +
            "        .static-method core.io::Console$.static.print(value:.string)@.void priv native symbol(\"print\") lib(\"rigi_rt\")\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .string .t0,\n" +
            "        .string .t1,\n" +
            "        .string .t2,\n" +
            "        .i32 result\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_Hello) $.t0\n" +
            "        load res(R_World) $.t1\n" +
            "        add $.t0 $.t1 $.t2\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t2]\n" +
            "        load res(R_Zero) $result\n" +
            "        ret $result\n" +
            "    }\n" +
            "}\n";

        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("Middleware Gate 门禁");
            TestGateRejectsParseError();
            TestGateRejectsVerifierError();
            TestGateAcceptsValidModule();
            TestHarness.Section("Middleware 驻留符号表");
            TestSymbolTable();
            TestHarness.Section("Middleware MIR 构造");
            TestMirConstruction();
            TestHarness.Section("Middleware MIR 控制流直译");
            TestMirControlFlow();
            TestHarness.Section("Middleware 实现绑定");
            TestBinding();
            TestHarness.Section("Middleware 目标文件发射");
            TestObjectEmission();
            TestHarness.Section("Middleware .ll 黄金锚点");
            TestLlGoldenAnchors();
            TestHarness.Section("Middleware 受控失败");
            TestNotSupported();
            TestHarness.Section("native CLI 端到端");
            TestNativeCli();
            return TestHarness.Summary("Middleware");
        }

        // ===== Gate 门禁 =====

        private static void TestGateRejectsParseError()
        {
            var result = BilGate.Accept("这不是 BIL 文本", "bad.bil");
            TestHarness.CheckTrue("解析垃圾被拒绝", !result.IsAccepted);
            TestHarness.CheckTrue("解析错误带文件名", result.Errors.Count > 0
                && result.Errors[0].Contains("bad.bil"), result.Errors.FirstOrDefault() ?? "");
        }

        private static void TestGateRejectsVerifierError()
        {
            var result = BilGate.Accept(UndeclaredVarBil, "undeclared.bil");
            TestHarness.CheckTrue("验证器违规被拒绝", !result.IsAccepted);
            TestHarness.CheckTrue("验证错误提及违规变量", result.Errors.Count > 0
                && result.Errors[0].Contains("$missing"), result.Errors.FirstOrDefault() ?? "");
        }

        private static void TestGateAcceptsValidModule()
        {
            var result = BilGate.Accept(MinimalValidBil, "ok.bil");
            TestHarness.CheckTrue("合法模块放行", result.IsAccepted,
                string.Join("; ", result.Errors));
            TestHarness.CheckTrue("放行模块非空", result.Module != null);
            TestHarness.CheckTrue("放行模块函数数", result.Module!.Functions.Count == 1);

            // 编译器真实产物过门禁（复用中端全管线驱动，杜绝手编样例漂移）
            var (_, _, emittedText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 { return 0 }\n");
            var emitted = BilGate.Accept(emittedText, "emitted.bil");
            TestHarness.CheckTrue("编译器产物过门禁", emitted.IsAccepted,
                string.Join("; ", emitted.Errors.Take(3)));
        }

        // ===== 驻留符号表 =====

        private static void TestSymbolTable()
        {
            var gate = BilGate.Accept(MinimalValidBil, "ok.bil");
            var context = new MwContext(gate.Module!);
            var symbols = context.Symbols;

            var point = symbols.FindType("Point");
            TestHarness.CheckTrue("类型已登记", point != null);
            TestHarness.CheckTrue("本地类型非外部", point is { IsExternal: false });
            TestHarness.CheckTrue("类型成员数", point!.Members.Count == 2);
            TestHarness.CheckTrue("类型成员 canonical", point.Members[0].Canonical == "Point#x@.i32");

            var memberX = symbols.FindMember("Point#x@.i32");
            TestHarness.CheckTrue("成员已登记", memberX != null);
            TestHarness.CheckTrue("驻留=引用相等（类型表↔成员表同一对象）",
                ReferenceEquals(point.Members[0], memberX));
            TestHarness.CheckTrue("成员 Owner 回指宿主类型",
                ReferenceEquals(memberX!.Owner, point));
            TestHarness.CheckTrue("驻留=引用相等（重复查找同一对象）",
                ReferenceEquals(symbols.FindType("Point"), point));

            var external = symbols.FindType("core.String");
            TestHarness.CheckTrue("外部类型已登记", external is { IsExternal: true });
            TestHarness.CheckTrue("外部类型成员已登记",
                symbols.FindMember("core.String$get_Length()@.i32") is { IsExternal: true });

            var main = symbols.FindMember("$main()@.i32");
            TestHarness.CheckTrue("全局函数已登记", main != null);
            TestHarness.CheckTrue("全局函数 Owner 为 null", main!.Owner == null);
            TestHarness.CheckTrue("GlobalMembers 含全局函数",
                symbols.GlobalMembers.Any(m => m.Canonical == "$main()@.i32"));

            TestHarness.CheckTrue("未登记符号查找为 null",
                symbols.FindType("Nope") == null && symbols.FindMember("Nope$f()@.void") == null);
        }

        // ===== MIR 构造 =====

        private static void TestMirConstruction()
        {
            // 编译器真实产物（println 全链）经文本往返 + 门禁后进 MIR
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"hello\")\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "mir.bil");
            TestHarness.CheckTrue("MIR 输入门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);

            TestHarness.CheckTrue("MwContext 挂载 MIR", ReferenceEquals(context.Mir, mir));
            TestHarness.CheckTrue("MIR 函数数（main + println）", mir.Functions.Count == 2,
                string.Join(", ", mir.Functions.Select(f => f.Symbol.Canonical)));

            var main = mir.Functions.First(f => f.Symbol.Canonical == "$main()@.i32");
            TestHarness.CheckTrue("main 是入口", main.IsEntrypoint);
            TestHarness.CheckTrue("main 返回 .i32", main.ReturnType.Key == "i32");
            TestHarness.CheckTrue("main 单 block", main.Blocks.Count == 1);
            TestHarness.CheckTrue("main 终结符是带值 MirRet",
                main.Blocks[0].Terminator is MirRet { Value: not null });
            TestHarness.CheckTrue("main 首指令是资源物化",
                main.Blocks[0].Instructions[0] is MirLoadResource);
            var printlnCall = main.Blocks[0].Instructions.OfType<MirCall>().FirstOrDefault();
            TestHarness.CheckTrue("main 含调用", printlnCall != null);
            TestHarness.CheckTrue("调用目标是 println 驻留符号",
                printlnCall!.Target.Canonical.Contains("println"));
            TestHarness.CheckTrue("void 调用无结果槽", printlnCall.Result == null);
            TestHarness.CheckTrue("调用目标与符号表同一对象（驻留）",
                ReferenceEquals(printlnCall.Target,
                    context.Symbols.FindMember(printlnCall.Target.Canonical)));

            var println = mir.Functions.First(f => f.Symbol.Canonical.Contains("println"));
            TestHarness.CheckTrue("println 非入口", !println.IsEntrypoint);
            TestHarness.CheckTrue("println 返回 .void", println.ReturnType.IsVoid);
            TestHarness.CheckTrue("println 单 string 参数",
                println.Parameters.Count == 1 && println.Parameters[0].Type.IsString);
            TestHarness.CheckTrue("println 体内含 string 拼接运算",
                println.Blocks[0].Instructions.OfType<MirBinaryIntrinsic>().Any(
                    b => b.Op == Bil.BilBinaryOp.Add && b.LeftType.IsString));
            TestHarness.CheckTrue("局部查找可用", println.FindLocal(println.Parameters[0].Name)
                .Type.IsString);
        }

        // ===== MIR 控制流直译（MW3）=====

        private static void TestMirControlFlow()
        {
            // if/else → CondBranch + 双分支 ret + 不可达汇聚块 unreachable 收尾
            var (_, _, ifText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    if (1 < 2) { return 1 } else { return 2 }\n" +
                "}\n");
            var gate = BilGate.Accept(ifText, "if.bil");
            TestHarness.CheckTrue("if 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);
            var main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("if：entry 终结符是条件跳转",
                main.Blocks[0].Terminator is MirCondBranch);
            var condBranch = (MirCondBranch)main.Blocks[0].Terminator;
            TestHarness.CheckTrue("if：双分支各自 ret",
                main.Blocks.Single(b => b.Id == condBranch.ThenTarget).Terminator is MirRet
                && main.Blocks.Single(b => b.Id == condBranch.ElseTarget).Terminator is MirRet);
            var merge = main.Blocks.Single(
                b => b.Id.StartsWith("mw.if.end.", StringComparison.Ordinal));
            TestHarness.CheckTrue("if：双分支均终结，汇聚块不可达收尾 unreachable",
                merge.Terminator is MirUnreachable);

            // while → entry Br(judge)；judge CondBranch(cond, body, exit)；body Br(judge)
            var (_, _, loopText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    while (x < 3) { x = x + 1 }\n" +
                "    return x\n" +
                "}\n");
            gate = BilGate.Accept(loopText, "loop.bil");
            TestHarness.CheckTrue("while 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            context = new MwContext(gate.Module!);
            mir = MirBuilder.Build(context);
            main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("while：entry 终结符跳 judge",
                main.Blocks[0].Terminator is MirBranch { Target: "loop0-judge" });
            TestHarness.CheckTrue("while：judge 条件跳转（true body / false exit）",
                main.Blocks.Single(b => b.Id == "loop0-judge").Terminator is MirCondBranch cond
                && cond.ThenTarget == "loop0-body"
                && cond.ElseTarget.StartsWith("mw.loop.end.", StringComparison.Ordinal));
            TestHarness.CheckTrue("while：body 落出回 judge（enum 为 none）",
                main.Blocks.Single(b => b.Id == "loop0-body").Terminator
                    is MirBranch { Target: "loop0-judge" });
            TestHarness.CheckTrue("while：exit 块含带值 ret",
                main.Blocks.Single(b => b.Id.StartsWith("mw.loop.end.", StringComparison.Ordinal))
                    .Terminator is MirRet { Value: not null });

            // do-while → loop.rev：entry Br(body)；judge CondBranch(cond, body, exit)
            var (_, _, revText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    do {\n" +
                "        x = x + 1\n" +
                "    } while (x < 3)\n" +
                "    return x\n" +
                "}\n");
            gate = BilGate.Accept(revText, "rev.bil");
            TestHarness.CheckTrue("do-while 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            context = new MwContext(gate.Module!);
            mir = MirBuilder.Build(context);
            main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("do-while：entry 终结符跳 body（先执行）",
                main.Blocks[0].Terminator is MirBranch { Target: "loop0-body" });
            TestHarness.CheckTrue("do-while：judge 条件跳转回 body",
                main.Blocks.Single(b => b.Id == "loop0-judge").Terminator is MirCondBranch rev
                && rev.ThenTarget == "loop0-body"
                && rev.ElseTarget.StartsWith("mw.loop.end.", StringComparison.Ordinal));

            // switch → MirSwitch + item/default 各自成块
            var (_, _, switchText) = BilTestHarness.EmitBilUnit(
                "pub func classify(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (2) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return classify(1)\n" +
                "}\n");
            gate = BilGate.Accept(switchText, "switch.bil");
            TestHarness.CheckTrue("switch 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            context = new MwContext(gate.Module!);
            mir = MirBuilder.Build(context);
            var classify = mir.Functions.Single(f => f.Symbol.Canonical.Contains("classify"));
            TestHarness.CheckTrue("switch：entry 终结符是 MirSwitch",
                classify.Blocks[0].Terminator is MirSwitch);
            var sw = (MirSwitch)classify.Blocks[0].Terminator;
            TestHarness.CheckTrue("switch：两 item + 常量表两元素 + default 目标",
                sw.ItemTargets.Count == 2 && sw.Table.Elements.Count == 2
                && sw.DefaultTarget == "switch0-default");
            TestHarness.CheckTrue("switch：item 块各自 ret",
                classify.Blocks.Single(b => b.Id == sw.ItemTargets[0]).Terminator is MirRet
                && classify.Blocks.Single(b => b.Id == sw.ItemTargets[1]).Terminator is MirRet);

            // break@outer → MirBranch 指向外层 loop 出口；continue → 内层 judge
            var (_, _, breakText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    while (x < 10) named outer {\n" +
                "        while (x < 5) {\n" +
                "            x = x + 1\n" +
                "            if (x == 3) { break@outer }\n" +
                "            continue\n" +
                "        }\n" +
                "        x = x + 2\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            gate = BilGate.Accept(breakText, "break.bil");
            TestHarness.CheckTrue("嵌套标签循环门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            context = new MwContext(gate.Module!);
            mir = MirBuilder.Build(context);
            main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("break@outer 解析到外层出口块（mw.loop.end.0）",
                main.Blocks.Any(b => b.Terminator is MirBranch { Target: "mw.loop.end.0" }));
            TestHarness.CheckTrue("continue 解析到内层 judge（loop1-judge）",
                main.Blocks.Any(b => b.Terminator is MirBranch { Target: "loop1-judge" }));
        }

        // ===== 实现绑定 =====

        private static void TestBinding()
        {
            // string + 是 op 级内建 → 运行时面；别名 core::String 与 .string 同键
            var concat = ImplBinder.BindBinary(Bil.BilBinaryOp.Add,
                ".string", "core::String", ".string");
            TestHarness.CheckTrue("string + → rigi_string_concat 运行时面",
                concat is RuntimeFaceBinding { FaceSymbol: RuntimeFaces.StringConcat });

            TestHarness.CheckTrue("i32 + → IntAdd 指令选择",
                ImplBinder.BindBinary(Bil.BilBinaryOp.Add,
                    ".i32", ".i32", ".i32")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntAdd });
            TestHarness.CheckTrue("u64 / → IntUDiv",
                ImplBinder.BindBinary(Bil.BilBinaryOp.Div,
                    ".u64", ".u64", ".u64")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntUDiv });
            TestHarness.CheckTrue("i64 / → IntSDiv",
                ImplBinder.BindBinary(Bil.BilBinaryOp.Div,
                    ".i64", ".i64", ".i64")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntSDiv });
            TestHarness.CheckTrue("f64 >= → FloatCmpGe",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpGe,
                    ".f64", ".f64", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.FloatCmpGe });
            TestHarness.CheckTrue("bool and → LogicAnd",
                ImplBinder.BindBinary(Bil.BilBinaryOp.And,
                    ".bool", ".bool", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.LogicAnd });
            TestHarness.CheckTrue("i32 取负 → IntNeg",
                ImplBinder.BindUnary(Bil.BilUnaryOp.Opposite,
                    ".i32", ".i32")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntNeg });
            TestHarness.CheckTrue("bool not → LogicNot",
                ImplBinder.BindUnary(Bil.BilUnaryOp.Not,
                    ".bool", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.LogicNot });

            // 不支持组合 → MwNotSupportedException（受控失败，非崩溃）
            var unsupported = false;
            try
            {
                ImplBinder.BindBinary(Bil.BilBinaryOp.Add,
                    ".string", ".i32", ".string");
            }
            catch (MwNotSupportedException)
            {
                unsupported = true;
            }
            TestHarness.CheckTrue("string + i32 受控拒绝", unsupported);

            // 调用绑定：native 声明 → NativeDirectBinding；本地 fn → DirectCallBinding
            var gate = BilGate.Accept(HelloConcatBil, "bind.bil");
            var context = new MwContext(gate.Module!);
            var nativePrint = context.Symbols.FindMember(
                "core.io::Console$.static.print(value:.string)@.void");
            TestHarness.CheckTrue("native print → NativeDirectBinding(rigi_rt, print)",
                ImplBinder.BindCall(nativePrint!) is NativeDirectBinding
                { Library: "rigi_rt", Symbol: "print" });
            var main = context.Symbols.FindMember("$main()@.i32");
            TestHarness.CheckTrue("本地 fn → DirectCallBinding",
                ImplBinder.BindCall(main!) is DirectCallBinding);

            // canonical 签名解析（native 声明无 fn 体，签名从符号文本解析）
            var signature = CanonicalSignature.Parse(
                "core.io::Console$.static.print(value:.string)@.void");
            TestHarness.CheckTrue("签名解析：参数 名:类型",
                signature.Parameters.Count == 1 && signature.Parameters[0].Name == "value"
                && signature.Parameters[0].TypeRef == ".string");
            TestHarness.CheckTrue("签名解析：返回类型", signature.ReturnTypeRef == ".void");
            var nested = CanonicalSignature.Parse("f(m:.map<.string, .i64>, x:.i32)@.void");
            TestHarness.CheckTrue("签名解析：嵌套泛型逗号不分割",
                nested.Parameters.Count == 2
                && nested.Parameters[0].TypeRef == ".map<.string, .i64>");
        }

        // ===== .ll 黄金锚点 =====

        private static void TestLlGoldenAnchors()
        {
            var gate = BilGate.Accept(HelloConcatBil, "golden.bil");
            TestHarness.CheckTrue("黄金用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);
            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            // 黄金快照的 MW1 形态：锚定关键行（全文黄金比对随 .ll 快照基建落地）
            TestHarness.CheckTrue("模块名来自 Metadata", ll.Contains("; ModuleID = 'hello'"), ll);
            TestHarness.CheckTrue("字符串字面量进内部全局",
                ll.Contains("@str.R_Hello = internal constant [7 x i8] c\"Hello, \""), ll);
            TestHarness.CheckTrue("转义换行进字节常量",
                ll.Contains("c\"world!\\0A\""), ll);
            TestHarness.CheckTrue("入口发射为 rigi_entry",
                ll.Contains("define i32 @rigi_entry()"), ll);
            TestHarness.CheckTrue("string + → rigi_string_concat 调用",
                ll.Contains("call void @rigi_string_concat(ptr"), ll);
            TestHarness.CheckTrue("native print → rigi_print 声明",
                ll.Contains("declare void @rigi_print(ptr)"), ll);
            TestHarness.CheckTrue("返回装载 i32 0",
                ll.Contains("ret i32"), ll);
        }

        // ===== 受控失败 =====

        private static void TestNotSupported()
        {
            // 合法 BIL（try 已过门禁）超出现阶段 MIR 面 → MwNotSupportedException
            //（异常机制随 MW9 落地）
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    try { return 1 } catch (e: core.RuntimeException) { return 2 }\n" +
                "}\n");
            var gate = BilGate.Accept(text, "try.bil");
            TestHarness.CheckTrue("try 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var caught = false;
            try
            {
                MirBuilder.Build(new MwContext(gate.Module!));
            }
            catch (MwNotSupportedException)
            {
                caught = true;
            }
            TestHarness.CheckTrue("try MIR 构造受控拒绝（随 MW9）", caught);

            // CLI 路径：受控失败转退出码 2 而非崩溃
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_unsupported_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "try.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var result = RunNative("native", "--file", bilPath,
                    "--emit-obj", Path.Combine(dir, "try.o"));
                TestHarness.CheckTrue("不支持形态 CLI 退出码 2", result.Code == 2);
                TestHarness.CheckTrue("不支持形态错误走 stderr", result.Err.Contains("MW3"),
                    result.Err);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ===== 空模块 .o 发射 =====

        private static void TestObjectEmission()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var gate = BilGate.Accept(MinimalValidBil, "ok.bil");
                var context = new MwContext(gate.Module!);
                var mir = MirBuilder.Build(context);
                using var module = ModuleBuilder.Build(context, mir);
                var objPath = Path.Combine(dir, "symtest.o");

                var ok = ObjectEmitter.TryEmitObject(module, objPath, out var error);
                TestHarness.CheckTrue("空模块 .o 发射成功", ok, error);
                TestHarness.CheckTrue(".o 已落盘且非空",
                    File.Exists(objPath) && new FileInfo(objPath).Length > 0);

                // 目标文件魔数：win-x64 → COFF（前 2 字节 machine 0x8664 小端）；
                // linux-x64 → ELF（0x7F 'E' 'L' 'F'）
                var head = File.ReadAllBytes(objPath).Take(4).ToArray();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    TestHarness.CheckTrue(".o 为 COFF（x64 machine 魔数）",
                        head[0] == 0x64 && head[1] == 0x86,
                        BitConverter.ToString(head));
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    TestHarness.CheckTrue(".o 为 ELF（魔数）",
                        head[0] == 0x7F && head[1] == 0x45 && head[2] == 0x4C && head[3] == 0x46,
                        BitConverter.ToString(head));
                }

                // 失败路径：不存在目录下的输出路径 → false + 可读错误（不抛崩）
                var badPath = Path.Combine(dir, "no_such_dir", "x.o");
                var fail = ObjectEmitter.TryEmitObject(module, badPath, out var failError);
                TestHarness.CheckTrue("不可写路径发射返回 false", !fail);
                TestHarness.CheckTrue("不可写路径错误可读", failError.Length > 0);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ===== native CLI 端到端 =====

        private static void TestNativeCli()
        {
            var missingFile = RunNative("native");
            TestHarness.CheckTrue("native 缺 --file 退出码 2", missingFile.Code == 2);

            var missingOut = RunNative("native", "--file", "x.bil");
            TestHarness.CheckTrue("native 缺 --out 退出码 2", missingOut.Code == 2);

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_cli_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var badPath = Path.Combine(dir, "bad.bil");
                File.WriteAllText(badPath, UndeclaredVarBil, new UTF8Encoding(false));
                var rejected = RunNative("native", "--file", badPath,
                    "--out", Path.Combine(dir, "bad.o"));
                TestHarness.CheckTrue("native 非法 BIL 退出码 1", rejected.Code == 1);
                TestHarness.CheckTrue("native 非法 BIL 错误走 stderr", rejected.Err.Contains("$missing"));
                TestHarness.CheckTrue("native 拒绝后不产 .o",
                    !File.Exists(Path.Combine(dir, "bad.o")));

                var okPath = Path.Combine(dir, "ok.bil");
                File.WriteAllText(okPath, MinimalValidBil, new UTF8Encoding(false));
                var objPath = Path.Combine(dir, "app.o");
                var accepted = RunNative("native", "--file", okPath, "--emit-obj", objPath);
                TestHarness.CheckTrue("native 合法 BIL 退出码 0", accepted.Code == 0, accepted.Err);
                TestHarness.CheckTrue("native 发射 .o 落盘",
                    File.Exists(objPath) && new FileInfo(objPath).Length > 0);
                TestHarness.CheckTrue("native stdout 纯净", accepted.Out.Length == 0,
                    accepted.Out);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // 驱动 native COMMAND 端到端，捕获 stdout/stderr（同 vm 套件模式）
        private static (int Code, string Out, string Err) RunNative(params string[] args)
        {
            if (!CommandLineParser.TryParse(args, out var result, out var error))
            {
                throw new InvalidOperationException($"测试构造的命令行应解析成功: {error}");
            }
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            try
            {
                int code = new NativeCommand().Execute(result!);
                return (code, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        }
    }
}
