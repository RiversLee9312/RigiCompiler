using System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Gate;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

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

        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        private static ParallelSuiteRunner.SuiteSpec Spec => new(
            "Middleware", Cases, sectionTitle: "Middleware");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestGateRejectsParseError", TestGateRejectsParseError),
            ("TestGateRejectsVerifierError", TestGateRejectsVerifierError),
            ("TestGateRejectsBoolBitwise", TestGateRejectsBoolBitwise),
            ("TestGateAcceptsValidModule", TestGateAcceptsValidModule),
            ("TestSymbolTable", TestSymbolTable),
            ("TestMirConstruction", TestMirConstruction),
            ("TestMirControlFlow", TestMirControlFlow),
            ("TestBinding", TestBinding),
            ("TestObjectEmission", TestObjectEmission),
            ("TestLlGoldenAnchors", TestLlGoldenAnchors),
            ("TestNullResourceEmission", TestNullResourceEmission),
            ("TestDivGuardEmission", TestDivGuardEmission),
            ("TestLayoutPlans", TestLayoutPlans),
            ("TestTypeSheetEmission", TestTypeSheetEmission),
            ("TestTypeCheckEmission", TestTypeCheckEmission),
            ("TestObjectPathEmission", TestObjectPathEmission),
            ("TestValuePathEmission", TestValuePathEmission),
            ("TestStaticEmission", TestStaticEmission),
            ("TestArrayPathEmission", TestArrayPathEmission),
            ("TestSpanPathEmission", TestSpanPathEmission),
            ("TestInvokeIndirect", TestInvokeIndirect),
            ("TestNativeFfiAbi", TestNativeFfiAbi),
            ("TestBoxAnyEmission", TestBoxAnyEmission),
            ("TestInterfaceDefaultMethods", TestInterfaceDefaultMethods),
            ("TestConstructedTypes", TestConstructedTypes),
            ("TestConstructedDispatch", TestConstructedDispatch),
            ("TestVargsKwargs", TestVargsKwargs),
            ("TestNotSupported", TestNotSupported),
            ("TestNativeCli", TestNativeCli),
            ("TestRcInjection", TestRcInjection),
            ("TestRefMapMw7a", TestRefMapMw7a),
        };

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

        // §11.4 收紧：bool 的 bin.and 属类型非法，Gate 门禁必须拒绝
        private const string BoolBitwiseBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_T = bool true,\n" +
            "    R_Zero = i32 0\n" +
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
            "        .bool a,\n" +
            "        .bool b,\n" +
            "        .bool r,\n" +
            "        .i32 x\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_T) $a\n" +
            "        load res(R_T) $b\n" +
            "        bin.and $a $b $r\n" +
            "        load res(R_Zero) $x\n" +
            "        ret $x\n" +
            "    }\n" +
            "}\n";

        private static void TestGateRejectsBoolBitwise()
        {
            var result = BilGate.Accept(BoolBitwiseBil, "boolbit.bil");
            TestHarness.CheckTrue("bool bin.and 被门禁拒绝", !result.IsAccepted);
            TestHarness.CheckTrue("拒绝消息含 opcode 与类型",
                result.Errors.Any(e => e.Contains("bin.and") && e.Contains(".bool")),
                result.Errors.FirstOrDefault() ?? "");
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
        // 本节直调 MirBuilder.Build：断言未改写的原始 MIR（访问器/数组
        // 降级归 Passes/，由 CreateDefault 管线用例覆盖）

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
        // 直调 MirBuilder.Build：CFG 形状与访问器/数组降级无关

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

            // string 比较 → 比较面（六种比较同一面，次序判定归 Emit）
            TestHarness.CheckTrue("string cmp.eq → StringCompareBinding",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpEq,
                    ".string", "core::String", ".bool")
                    is StringCompareBinding);
            TestHarness.CheckTrue("string cmp.lt → StringCompareBinding",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpLt,
                    ".string", ".string", ".bool")
                    is StringCompareBinding);

            // 窄宽度整数：与 i32 同族绑定（LLVM 指令同宽两侧天然满足）
            TestHarness.CheckTrue("i8 + → IntAdd",
                ImplBinder.BindBinary(Bil.BilBinaryOp.Add,
                    ".i8", ".i8", ".i8")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntAdd });
            TestHarness.CheckTrue("u16 >> → ShiftRightUnsigned",
                ImplBinder.BindBinary(Bil.BilBinaryOp.ShiftRight,
                    ".u16", ".u16", ".u16")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.ShiftRightUnsigned });
            TestHarness.CheckTrue("i16 >> → ShiftRightSigned",
                ImplBinder.BindBinary(Bil.BilBinaryOp.ShiftRight,
                    ".i16", ".i16", ".i16")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.ShiftRightSigned });
            TestHarness.CheckTrue("u8 < → IntCmpULt",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpLt,
                    ".u8", ".u8", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntCmpULt });

            // char 比较按 UTF-16 码元无符号序（VM 同口径）；char 无算术/一元
            TestHarness.CheckTrue("char == → IntCmpEq",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpEq,
                    ".char", ".char", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntCmpEq });
            TestHarness.CheckTrue("char < → IntCmpULt",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpLt,
                    ".char", ".char", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntCmpULt });
            var charArith = false;
            try
            {
                ImplBinder.BindBinary(Bil.BilBinaryOp.Add, ".char", ".char", ".char");
            }
            catch (MwNotSupportedException)
            {
                charArith = true;
            }
            TestHarness.CheckTrue("char 算术受控拒绝（VM 同口径）", charArith);
            var charUnary = false;
            try
            {
                ImplBinder.BindUnary(Bil.BilUnaryOp.BinNot, ".char", ".char");
            }
            catch (MwNotSupportedException)
            {
                charUnary = true;
            }
            TestHarness.CheckTrue("char 一元受控拒绝（VM 同口径）", charUnary);

            // .nullable<T>：eq/ne → 胖引用恒等（null 双段零天然成立）；
            // 排序比较不适用（受控拒绝）
            TestHarness.CheckTrue("nullable cmp.eq → RefCmpEq",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpEq,
                    ".nullable<.string>", ".nullable<.string>", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.RefCmpEq });
            TestHarness.CheckTrue("nullable cmp.ne → RefCmpNe",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpNe,
                    ".nullable<.i32>", "core::Nullable<core::i32>", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.RefCmpNe });
            var nullableOrder = false;
            try
            {
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpLt,
                    ".nullable<.string>", ".nullable<.string>", ".bool");
            }
            catch (MwNotSupportedException)
            {
                nullableOrder = true;
            }
            TestHarness.CheckTrue("nullable 排序比较受控拒绝", nullableOrder);

            // bool 位运算不绑定（§11.4 收紧：内建位运算仅整数族；此类
            // BIL 已过不了 Gate，此处为纵深防御断言）
            var boolBitwise = 0;
            foreach (var bitOp in new[]
            {
                Bil.BilBinaryOp.BinAnd, Bil.BilBinaryOp.BinOr, Bil.BilBinaryOp.BinXor,
            })
            {
                try
                {
                    ImplBinder.BindBinary(bitOp, ".bool", ".bool", ".bool");
                }
                catch (MwNotSupportedException)
                {
                    boolBitwise++;
                }
            }
            TestHarness.CheckTrue("bool bin.and/or/xor 受控拒绝", boolBitwise == 3);

            // 实例方法派发细分（VM 同口径）：class → 虚调用；interface →
            // iMap 派发；init → 直调
            var (_, _, classText) = BilTestHarness.EmitBilUnit(
                "pub interface Named { func name(): String }\n" +
                "pub open class Base { pub init() { } pub open func who(): i32 { return 1 } }\n" +
                "pub class Derived : Base implements Named {\n" +
                "    pub init() { }\n" +
                "    pub override func who(): i32 { return 2 }\n" +
                "    pub override func name(): String { return \"d\" }\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            var classGate = BilGate.Accept(classText, "bindclass.bil");
            TestHarness.CheckTrue("派发用例门禁放行", classGate.IsAccepted,
                string.Join("; ", classGate.Errors));
            var classContext = new MwContext(classGate.Module!);
            TestHarness.CheckTrue("class 实例方法 → VirtualCallBinding",
                ImplBinder.BindCall(classContext.Symbols.FindMember("Base$who()@.i32")!)
                    is VirtualCallBinding);
            TestHarness.CheckTrue("override 方法 → VirtualCallBinding",
                ImplBinder.BindCall(classContext.Symbols.FindMember("Derived$who()@.i32")!)
                    is VirtualCallBinding);
            TestHarness.CheckTrue("interface 方法 → InterfaceCallBinding",
                ImplBinder.BindCall(classContext.Symbols.FindMember("Named$name()@.string")!)
                    is InterfaceCallBinding);
            TestHarness.CheckTrue("init → DirectCallBinding",
                ImplBinder.BindCall(classContext.Symbols.FindMember("Derived$init()@.void")!)
                    is DirectCallBinding);

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
        // 直调 MirBuilder.Build 后发射：与访问器/数组降级无关

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
                ll.Contains("@str.R_Hello = internal constant { i32, i32, [7 x i8] } { i32 -1, i32 0, [7 x i8] c\"Hello, \""), ll);
            TestHarness.CheckTrue("字面量 data 指针 = 块+8",
                ll.Contains("getelementptr inbounds (i8, ptr @str.R_Hello, i64 8)"), ll);
            TestHarness.CheckTrue("String 槽零初始化",
                ll.Contains("store { ptr, i64 } zeroinitializer"), ll);
            TestHarness.CheckTrue("rigi_globals_cleanup 已发射",
                ll.Contains("define void @rigi_globals_cleanup()"), ll);
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

        // ===== null 资源发射（MW2）=====

        private static void TestNullResourceEmission()
        {
            // 真实前端路径：var s: String? = null + == null 检查
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s: String? = null\n" +
                "    if (s == null) { Console.println(\"null\") }\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "null.bil");
            TestHarness.CheckTrue("null 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);
            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            // null = 胖引用双段零（RUNTIME §3：Nullable 是 Object 子类）
            TestHarness.CheckTrue("null 资源 → 胖引用零常量",
                ll.Contains("{ i64, i64 } zeroinitializer"), ll);
            // nullable == → 胖引用双段 extractvalue 各自 icmp 取与
            TestHarness.CheckTrue("nullable == → 双段恒等比较",
                ll.Contains("extractvalue { i64, i64 }"), ll);
        }

        // ===== 除零 guard 发射（MW2 占位语义，策略注入点默认 abort 实现）=====

        private static void TestDivGuardEmission()
        {
            // 真实前端路径：i32 除法 → divisor==0 条件分支 → abort 面 →
            // unreachable；有符号窄宽度 MIN/-1 回绕的取负选择
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 0\n" +
                "    return (x / z)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "div.bil");
            TestHarness.CheckTrue("除零用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);
            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            TestHarness.CheckTrue("abort 面已登记声明",
                ll.Contains("declare void @rigi_abort_divided_by_zero()"), ll);
            TestHarness.CheckTrue("guard 调 abort 面",
                ll.Contains("call void @rigi_abort_divided_by_zero()"), ll);
            TestHarness.CheckTrue("abort 块 unreachable 收尾",
                ll.Contains("unreachable"), ll);
            TestHarness.CheckTrue("有符号 MIN/-1 回绕取负选择",
                ll.Contains("sdiv.wrap"), ll);
        }

        // ===== 布局引擎与 TypeSheet 发射（MW4 批 1）=====

        // 布局用例源（真实前端路径；类/继承/接口/enum/struct/引用字段/
        // rich 内嵌全覆盖；main 不实例化，避开尚未支持的 new 指令）
        private const string LayoutSource =
            "pub interface Named {\n" +
            "    func name(): String\n" +
            "}\n" +
            "pub open class Animal {\n" +
            "    pub var legs: i32\n" +
            "    pub init(l: i32) { legs = l }\n" +
            "    pub open func speak(): String { return \"...\" }\n" +
            "    pub func legCount(): i32 { return legs }\n" +
            "}\n" +
            "pub class Dog : Animal implements Named {\n" +
            "    pub var good: bool\n" +
            "    pub init(l: i32, g: bool) { legs = l\n" +
            "        good = g }\n" +
            "    pub override func speak(): String { return \"woof\" }\n" +
            "    pub override func name(): String { return \"dog\" }\n" +
            "}\n" +
            "pub enum struct Direction {\n" +
            "    pub const degrees: i32\n" +
            "    pub init(_ -> degrees)\n" +
            "}[\n" +
            "    North(0),\n" +
            "    South(180),\n" +
            "    East(90),\n" +
            "    West(270)\n" +
            "]\n" +
            "pub struct Point {\n" +
            "    pub var x: i32\n" +
            "    pub var y: i64\n" +
            "    pub var tag: String\n" +
            "}\n" +
            "pub class Link {\n" +
            "    pub var a: i32 = 0\n" +
            "    pub var p: Link? = null\n" +
            "    pub var b: i64 = 0L\n" +
            "    pub var q: Link? = null\n" +
            "}\n" +
            "pub rich struct Handle {\n" +
            "    pub var target: Link? = null\n" +
            "}\n" +
            "pub class Holder {\n" +
            "    pub var a: i32 = 0\n" +
            "    pub var h: Handle\n" +
            "    pub init(v: Handle) { h = v }\n" +
            "}\n" +
            "pub func main(): i32 {\n" +
            "    return 0\n" +
            "}\n";

        private static RigiCompiler.Middleware.Layout.LayoutPlanTable BuildLayout(string source)
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(source);
            var gate = BilGate.Accept(text, "layout.bil");
            TestHarness.CheckTrue("布局用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            return RigiCompiler.Middleware.Layout.LayoutEngine.Build(
                new MwContext(gate.Module!).Symbols);
        }

        private static RigiCompiler.Middleware.Layout.FieldPlan? FieldOf(
            RigiCompiler.Middleware.Layout.TypeLayoutPlan plan, string namePart)
        {
            foreach (var field in plan.Fields)
            {
                if (field.Symbol.Contains(namePart))
                {
                    return field;
                }
            }
            return null;
        }

        private static void TestLayoutPlans()
        {
            var layout = BuildLayout(LayoutSource);

            // struct：自然对齐 + String 内联 16B（进 refMap kind1）
            var point = layout.Find("Point");
            TestHarness.CheckTrue("Point 已布局", point != null);
            TestHarness.CheckTrue("Point 种类", point!.Kind ==
                RigiCompiler.Middleware.Layout.TypeLayoutKind.Struct);
            TestHarness.CheckTrue("Point 字段偏移",
                FieldOf(point, "#x@")!.Offset == 0
                && FieldOf(point, "#y@")!.Offset == 8
                && FieldOf(point, "#tag@")!.Offset == 16);
            TestHarness.CheckTrue("Point 尺寸/对齐",
                point.Size == 32 && point.Alignment == 16);
            TestHarness.CheckTrue("Point refMap 含 String 槽 kind1",
                point.RefMap.Length == 1
                && FieldOf(point, "#tag@")!.IsStringSlot
                && point.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 1));

            // class：对象头 16 起排、vtable 本类槽
            var animal = layout.Find("Animal");
            TestHarness.CheckTrue("Animal 已布局", animal != null);
            TestHarness.CheckTrue("Animal 字段偏移", FieldOf(animal!, "#legs@")!.Offset == 16);
            TestHarness.CheckTrue("Animal 尺寸/对齐",
                animal!.Size == 32 && animal.Alignment == 16);
            TestHarness.CheckTrue("Animal vtable 本类槽",
                animal.VTableSlots.Count == 2
                && animal.VTableSlots[0] == "Animal$speak()@.string"
                && animal.VTableSlots[1] == "Animal$legCount()@.i32");

            // 继承：基类字段在前（本类字段从基类 Size 续排）、override
            // 复用基槽、interface 实现段独立追加（条目指针与自有槽重复，
            // §7 接口派发模型）+ iMap
            var dog = layout.Find("Dog");
            TestHarness.CheckTrue("Dog 已布局", dog != null);
            TestHarness.CheckTrue("Dog 基类计划", ReferenceEquals(dog!.BasePlan, animal));
            TestHarness.CheckTrue("Dog 字段（继承在前）",
                dog.Fields.Count == 2
                && FieldOf(dog, "#legs@")!.Offset == 16
                && FieldOf(dog, "#good@")!.Offset == 32);
            TestHarness.CheckTrue("Dog 尺寸", dog.Size == 48);
            TestHarness.CheckTrue("Dog override 复用基槽",
                dog.VTableSlots.Count == 4
                && dog.VTableSlots[0] == "Dog$speak()@.string"
                && dog.VTableSlots[1] == "Animal$legCount()@.i32"
                && dog.VTableSlots[2] == "Dog$name()@.string"
                && dog.VTableSlots[3] == "Dog$name()@.string");
            TestHarness.CheckTrue("Dog iMap 段 base offset",
                dog.IMap.Count == 1 && dog.IMap[0].InterfaceType == "Named"
                && dog.IMap[0].BaseOffset == 3);

            // interface 空壳计划（批 2：iMap 键地址 + 接口内槽序表；无实例布局）
            var named = layout.Find("Named");
            TestHarness.CheckTrue("interface 空壳计划",
                named != null && named.Kind ==
                    RigiCompiler.Middleware.Layout.TypeLayoutKind.Interface
                && named.Size == 0 && named.Fields.Count == 0
                && named.VTableSlots.Count == 1
                && named.VTableSlots[0] == "Named$name()@.string");

            // enum：偏移 0 恒 u32 判别，实例字段续排；auto 判别值按声明序
            var direction = layout.Find("Direction");
            TestHarness.CheckTrue("Direction 已布局", direction != null);
            TestHarness.CheckTrue("Direction 种类", direction!.Kind ==
                RigiCompiler.Middleware.Layout.TypeLayoutKind.Enum);
            TestHarness.CheckTrue("Direction 字段偏移/尺寸",
                FieldOf(direction, "#degrees@")!.Offset == 4
                && direction.Size == 8 && direction.Alignment == 4);
            TestHarness.CheckTrue("Direction 判别值表",
                direction.EnumCases.Count == 4
                && direction.EnumCases[0].Discriminant == 0
                && direction.EnumCases[1].Discriminant == 1
                && direction.EnumCases[2].Discriminant == 2
                && direction.EnumCases[3].Discriminant == 3
                && direction.EnumCases[0].Case.Canonical == "Direction.North()");

            // 引用字段：16B 槽 16B 对齐、refMap 跳数（槽粒度）
            var link = layout.Find("Link");
            TestHarness.CheckTrue("Link 字段偏移",
                FieldOf(link!, "#a@")!.Offset == 16
                && FieldOf(link!, "#p@")!.Offset == 32
                && FieldOf(link!, "#b@")!.Offset == 48
                && FieldOf(link!, "#q@")!.Offset == 64);
            TestHarness.CheckTrue("Link 尺寸", link!.Size == 80);
            TestHarness.CheckTrue("Link refMap 跳数",
                link.RefMap.Length == 2 && link.RefMap[0] == 1 && link.RefMap[1] == 1);

            // rich struct：refMap 自身携带（值类型扫描起点 0）
            var handle = layout.Find("Handle");
            TestHarness.CheckTrue("Handle rich 标记",
                handle != null && (handle.TypeFlags
                    & RigiCompiler.Middleware.Layout.TypeLayoutPlan.FlagRich) != 0);
            TestHarness.CheckTrue("Handle refMap",
                handle!.RefMap.Length == 1 && handle.RefMap[0] == 0);

            // 内嵌 rich 值类型字段：引用按偏移折算拼入外层 refMap
            var holder = layout.Find("Holder");
            TestHarness.CheckTrue("Holder 字段偏移",
                FieldOf(holder!, "#a@")!.Offset == 16
                && FieldOf(holder!, "#h@")!.Offset == 32);
            TestHarness.CheckTrue("Holder 尺寸", holder!.Size == 48);
            TestHarness.CheckTrue("Holder refMap（rich 内嵌折算）",
                holder.RefMap.Length == 1 && holder.RefMap[0] == 1);
        }

        private static void TestTypeSheetEmission()
        {
            // 真实前端路径 + 全管线（MirBuild + Layout）：Node 的
            // TypeSheet/vtable/refMap 全局锚点
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub class Node {\n" +
                "    pub var value: i32\n" +
                "    pub var next: Node?\n" +
                "    pub init(v: i32) { value = v }\n" +
                "    pub func get(): i32 { return value }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "sheet.bil");
            TestHarness.CheckTrue("TypeSheet 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            TestHarness.CheckTrue("管线挂载布局", context.Layout != null);
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            // §6 结构：typeInfoId 指向 TypeInfo、baseTypeId null、typeSize 48
            //（头 16 + i32@16 + 引用槽@32）、vTableSize 1（init 不进表）
            TestHarness.CheckTrue("TypeSheet 全局锚点",
                ll.Contains("@typesheet.Node = internal constant { ptr, ptr, i32, i32, i32, ptr, i32, ptr, i32, ptr } " +
                    "{ ptr @typeinfo.Node, ptr null, i32 48, i32 0, i32 1, ptr @typesheet.vtable.Node, " +
                    "i32 0, ptr null, i32 1, ptr @typesheet.refmap.Node }"), ll);
            TestHarness.CheckTrue("TypeInfo 回指 sheet",
                ll.Contains("@typeinfo.Node =") && ll.Contains("ptr @typesheet.Node"), ll);
            TestHarness.CheckTrue("TypeInfo typeInfoId 非 null",
                !ll.Contains("@typesheet.Node = internal constant { ptr, ptr, i32, i32, i32, ptr, i32, ptr, i32, ptr } " +
                    "{ ptr null,"), ll);
            // vtable 条目标 null（get 不可达未进 MIR；MW5 可达性扩编兜底）
            TestHarness.CheckTrue("vtable 全局锚点",
                ll.Contains("@typesheet.vtable.Node = internal constant [1 x ptr] zeroinitializer"), ll);
            // refMap：next 槽@32 → 跳数 (32-16)/16 = 1（kind0）
            TestHarness.CheckTrue("refMap 全局锚点",
                ll.Contains("@typesheet.refmap.Node = internal constant [1 x i16] [i16 1]"), ll);
            TestHarness.CheckTrue("String 内建 sheet 含 kind1 refMap",
                ll.Contains("@\"typesheet.refmap.core::String\" = internal constant [1 x i16] [i16 16384]"), ll);
            TestHarness.CheckTrue("String 内建 sheet flags = INLINE|STRING",
                ll.Contains("i32 16, i32 40") && ll.Contains("typesheet.core::String"), ll);
            TestHarness.CheckTrue("Array 内建 sheet 前缀 32 + FlagArray",
                ll.Contains("i32 32, i32 16") && ll.Contains("typesheet.core::Array"), ll);
        }

        // ===== is/supers/with + TypeInfo（MW5 c3）=====

        private static void TestTypeCheckEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub open class Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub class Dog : Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub interface Named {\n" +
                "    func name(): String\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mark {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Mark\n" +
                "pub class Tagged implements Named {\n" +
                "    pub init() { }\n" +
                "    pub override func name(): String { return \"t\" }\n" +
                "}\n" +
                "pub func isDog(a: Animal): bool { return a is Dog }\n" +
                "pub func isNamed(t: Tagged): bool { return t is Named }\n" +
                "pub func supersDog(a: Animal): bool { return a supers Dog }\n" +
                "pub func withMark(t: Tagged): bool { return t with Mark }\n" +
                "pub func isIndirect\\<T>(a: Animal): bool { return a is T }\n" +
                "pub func main(): i32 {\n" +
                "    var d: Animal = new Dog()\n" +
                "    var t = new Tagged()\n" +
                "    if (isDog(d)) { }\n" +
                "    if (isNamed(t)) { }\n" +
                "    if (supersDog(d)) { }\n" +
                "    if (withMark(t)) { }\n" +
                "    if (isIndirect\\<Dog>(d)) { }\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "typeck.bil");
            TestHarness.CheckTrue("type.check 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var allInsts = context.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 type.is",
                allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.Is && !c.IsIndirect));
            TestHarness.CheckTrue("MIR 含 type.supers",
                allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.Supers && !c.IsIndirect));
            TestHarness.CheckTrue("MIR 含 type.with",
                allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.With && !c.IsIndirect));
            TestHarness.CheckTrue("MIR 含 type.is.indirect",
                allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.Is && c.IsIndirect));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("TypeInfo 全局名字符串",
                ll.Contains("@typeinfo.Tagged") && ll.Contains("ptr @typesheet.Tagged"), ll);
            TestHarness.CheckTrue("TypeInfo wrappers 数组",
                ll.Contains("typeinfo.wrappers.Tagged") && ll.Contains("@typesheet.Mark"), ll);
            TestHarness.CheckTrue("TypeInfo ifaceClosure",
                ll.Contains("typeinfo.ifaces.Tagged") && ll.Contains("@typesheet.Named"), ll);
            TestHarness.CheckTrue("TypeSheet typeInfoId 非 null",
                ll.Contains("ptr @typeinfo.Tagged") && !ll.Contains(
                    "@typesheet.Tagged = internal constant { ptr, ptr, i32, i32, i32, ptr, i32, ptr, i32, ptr } { ptr null,"),
                ll);
            TestHarness.CheckTrue("helper type.is",
                ll.Contains("call i32 @rigi_type_is("), ll);
            TestHarness.CheckTrue("helper type.supers",
                ll.Contains("call i32 @rigi_type_supers("), ll);
            TestHarness.CheckTrue("helper type.with",
                ll.Contains("call i32 @rigi_type_with("), ll);
            TestHarness.CheckTrue("helper type.is.indirect",
                ll.Contains("call i32 @rigi_type_is_indirect("), ll);
        }

        // ===== 对象路径发射（MW4 批 2：new/字段/虚派发/.this 形态）=====

        private static void TestObjectPathEmission()
        {
            // 真实前端路径 + 全管线：基类槽装派生实例的虚调用
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func who(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub init() { }\n" +
                "    pub override func who(): i32 { return 2 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: Base = new Derived()\n" +
                "    return (b.who())\n" +
                "}\n");
            var gate = BilGate.Accept(text, "obj.bil");
            TestHarness.CheckTrue("对象用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;

            // .this 隐藏首参落参数表首位（胖引用形态）
            var who = mir.Functions.First(f => f.Symbol.Canonical.Contains("Derived$who"));
            TestHarness.CheckTrue(".this 隐藏首参",
                who.Parameters.Count == 1 && who.Parameters[0].Name == ".this");

            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            // new → rigi_alloc(@typesheet.X)
            TestHarness.CheckTrue("new 调 rigi_alloc",
                ll.Contains("call ptr @rigi_alloc(ptr @typesheet.Derived)"), ll);
            // 虚调用 → rigi_vtable_entry 查槽
            TestHarness.CheckTrue("虚调用经 rigi_vtable_entry",
                ll.Contains("call ptr @rigi_vtable_entry(ptr"), ll);
            // 可达性扩编：Derived 的 vtable 条目非 null（override 实现进
            // 可达闭包，批 1 的 null 槽消灭）
            TestHarness.CheckTrue("vtable 条目非 null（可达性扩编）",
                ll.Contains("@typesheet.vtable.Derived = internal constant [1 x ptr] " +
                    "[ptr @\"Derived$who()@.i32\"]"), ll);
            // 实例方法签名：首参胖引用 {i64,i64}
            TestHarness.CheckTrue("实例方法首参胖引用",
                ll.Contains("define internal i32 @\"Derived$who()@.i32\"({ i64, i64 }"), ll);
        }

        // ===== 值类型路径发射（MW4 批 3：内联槽/判别/memcpy/ABI 形态）=====

        private static void TestValuePathEmission()
        {
            // 真实前端路径 + 全管线：struct 构造/方法与 enum case/判别
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "    pub func sum(): i32 { return (x + y) }\n" +
                "}\n" +
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    pub init(_ -> degrees)\n" +
                "}[\n" +
                "    North(0) -> 7,\n" +
                "    East(90) -> 42\n" +
                "]\n" +
                "pub func makePoint(a: i32, b: i32): Point { return new Point(a, b) }\n" +
                "pub func main(): i32 {\n" +
                "    var p = makePoint(1, 2)\n" +
                "    var s = p.sum()\n" +
                "    var d = Direction.East\n" +
                "    if (d is .East) { return s }\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "value.bil");
            TestHarness.CheckTrue("值类型用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;

            // MIR 形态：new.case / type.is.case / new type(V)
            var allInsts = mir.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 MirNewValue", allInsts.OfType<MirNewValue>().Any());
            TestHarness.CheckTrue("MIR 含 MirNewCase", allInsts.OfType<MirNewCase>().Any());
            TestHarness.CheckTrue("MIR 含 MirIsCase", allInsts.OfType<MirIsCase>().Any());

            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            // 值类型局部 = 计划尺寸内联 alloca（Point = 2×i32 = 8B）
            TestHarness.CheckTrue("值类型内联 alloca", ll.Contains("alloca [8 x i8]"), ll);
            // new 值类型：整槽清零（VM ZeroOf）
            TestHarness.CheckTrue("值类型清零 memset",
                ll.Contains("call void @llvm.memset.p0.i64"), ll);
            // 值语义深拷贝 memcpy
            TestHarness.CheckTrue("值语义 memcpy",
                ll.Contains("call void @llvm.memcpy.p0.p0.i64"), ll);
            // enum 判别：u32 @ 偏移 0 写判别常量（East -> 42）
            TestHarness.CheckTrue("判别 u32 写", ll.Contains("store i32 42, ptr"), ll);
            // type.is.case：读判别 + icmp eq 判别常量
            TestHarness.CheckTrue("判别比较", ll.Contains("icmp eq i32 %case.disc, 42"), ll);
            // 值类型返回 ABI：隐藏 out 首参 + void 返回
            TestHarness.CheckTrue("值返回隐藏 out 首参",
                ll.Contains("define internal void @\"$makePoint(a:.i32,b:.i32)@Point\"(ptr"), ll);
            // 值类型 .this：首参传指针（alloca 地址别名）
            TestHarness.CheckTrue("值类型 .this 传指针",
                ll.Contains("define internal i32 @\"Point$sum()@.i32\"(ptr"), ll);
        }

        // ===== 静态字段与 rigi_entry stub（MW4 批 4）=====

        private static void TestStaticEmission()
        {
            // 真实前端路径 + 全管线：全局字段 + class static + 初值缝合
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub var gCounter: i32 = 41\n" +
                "pub class Config {\n" +
                "    pub static var level: i32 = 3\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    gCounter = (gCounter + Config.level)\n" +
                "    return gCounter\n" +
                "}\n");
            var gate = BilGate.Accept(text, "static.bil");
            TestHarness.CheckTrue("静态用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;

            // MIR 形态：静态读写指令；..globals.init 恒可达（不在 invoke 闭包内）
            var allInsts = mir.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 MirGetStatic", allInsts.OfType<MirGetStatic>().Any());
            TestHarness.CheckTrue("MIR 含 MirSetStatic", allInsts.OfType<MirSetStatic>().Any());
            TestHarness.CheckTrue("..globals.init 恒可达",
                mir.Functions.Any(f => f.Symbol.Canonical == "$..globals.init()@.void"));

            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            // 静态槽全局（canonical 键、零值初始化、internal 链接）
            TestHarness.CheckTrue("全局字段槽锚点",
                ll.Contains("@\"static.#gCounter@.i32\" = internal global i32 0"), ll);
            TestHarness.CheckTrue("class static 槽锚点",
                ll.Contains("@\"static.Config#.static.level@.i32\" = internal global i32 0"), ll);
            // 入口 stub：..globals.init → main 调用序
            TestHarness.CheckTrue("rigi_entry stub 锚点",
                ll.Contains("define i32 @rigi_entry()"), ll);
            TestHarness.CheckTrue("stub 先调 ..globals.init 再调 main",
                ll.Contains("call void @\"$..globals.init()@.void\"()")
                && ll.IndexOf("call void @\"$..globals.init()@.void\"()",
                    System.StringComparison.Ordinal)
                < ll.IndexOf("call i32 @\"$main()@.i32\"()", System.StringComparison.Ordinal), ll);
            // 用户 main 以 canonical 名发射（internal 链接）
            TestHarness.CheckTrue("main 以 canonical 名发射",
                ll.Contains("define internal i32 @\"$main()@.i32\"()"), ll);
            TestHarness.CheckTrue("静态用例发射 rigi_globals_cleanup",
                ll.Contains("define void @rigi_globals_cleanup()"), ll);
        }

        // ===== 数组路径（MW4：alloc_array / get.array / set.array / length / raw）=====

        private static void TestArrayPathEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 7\n" +
                "    var x = a[0] if? 0\n" +
                "    var y = a[9] if? -1\n" +
                "    return ((x + y) + a.length)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "arr.bil");
            TestHarness.CheckTrue("数组用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;
            var allInsts = mir.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 getid.type", allInsts.OfType<MirGetTypeId>().Any());
            TestHarness.CheckTrue("MIR 含 get.array", allInsts.OfType<MirGetArray>().Any());
            TestHarness.CheckTrue("MIR 含 set.array", allInsts.OfType<MirSetArray>().Any());
            TestHarness.CheckTrue("MIR 含 Nullable 拆箱", allInsts.OfType<MirUnwrapNullable>().Any());
            TestHarness.CheckTrue("MIR 含 Array.length",
                allInsts.OfType<MirGetField>().Any(f =>
                    RigiCompiler.Middleware.Layout.TypeLayout.IsLengthField(f.FieldSymbol)));

            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("alloc_array 面",
                ll.Contains("call ptr @rigi_alloc_array(ptr"), ll);
            TestHarness.CheckTrue("内建 i32 TypeSheet",
                ll.Contains("@\"typesheet.core::i32\""), ll);
            TestHarness.CheckTrue("共享 Array TypeSheet",
                ll.Contains("@\"typesheet.core::Array\""), ll);
            TestHarness.CheckTrue("越界读得 null 分支",
                ll.Contains("arr.get.oob"), ll);

            const string rawBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawarr\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Data = raw.hex x2FF2331C,\n" +
                "    R_Zero = i32 0\n" +
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
                "        .array<.u8> d,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Data) $d\n" +
                "        load res(R_Zero) $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            var rawGate = BilGate.Accept(rawBil, "rawarr.bil");
            TestHarness.CheckTrue("raw→array<u8> 门禁放行", rawGate.IsAccepted,
                string.Join("; ", rawGate.Errors));
            var rawContext = new MwContext(rawGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(rawContext);
            using var rawModule = ModuleBuilder.Build(rawContext, rawContext.Mir!);
            var rawLl = rawModule.PrintToString();
            TestHarness.CheckTrue("raw 字节常量",
                rawLl.Contains("@raw.R_Data") && rawLl.Contains("c\"/\\F23\\1C\""), rawLl);
            TestHarness.CheckTrue("raw 走 alloc_array",
                rawLl.Contains("call ptr @rigi_alloc_array(ptr"), rawLl);
        }

        // ===== Span 路径（MW7b：span_alloc / 具化 sheet / stride 访问）=====

        private static void TestSpanPathEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<i32>(3)\n" +
                "    a[0] = 7\n" +
                "    var x = a[0] if? 0\n" +
                "    return (x + a.length)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "span.bil");
            TestHarness.CheckTrue("Span 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;
            var allInsts = mir.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 get.array（Span）",
                allInsts.OfType<MirGetArray>().Any(g =>
                    TypeLayout.IsSpan(g.CollectionType)));
            TestHarness.CheckTrue("MIR 含 set.array（Span）",
                allInsts.OfType<MirSetArray>().Any(s =>
                    TypeLayout.IsSpan(s.CollectionType)));
            TestHarness.CheckTrue("MIR 含 Span.length",
                allInsts.OfType<MirGetField>().Any(f =>
                    TypeLayout.IsLengthField(f.FieldSymbol)));
            var spanPlan = context.Layout!.Find("core::Span<core::i32>");
            TestHarness.CheckTrue("具化 Span<i32> 计划入表", spanPlan != null);
            TestHarness.CheckTrue("Span 计划 FlagArray 且无 FlagShared",
                spanPlan != null
                && (spanPlan.TypeFlags & TypeLayoutPlan.FlagArray) != 0
                && (spanPlan.TypeFlags & TypeLayoutPlan.FlagShared) == 0);

            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("span_alloc 编组形状",
                ll.Contains("call ptr @rigi_span_alloc(ptr"), ll);
            TestHarness.CheckTrue("具化 Span<i32> TypeSheet",
                ll.Contains("typesheet.core::Span$core::i32$"), ll);
            TestHarness.CheckTrue("Span sheet FlagArray（size 32 + flags 16）",
                SheetHasFlags(ll, "typesheet.core::Span$core::i32$", 32, 16), ll);
            TestHarness.CheckTrue("i32 stride 常量 4",
                ll.Contains("mul i64") && ll.Contains(", 4"), ll);

            var sharedLl = EmitLlFromSource(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = sharedSpanOf\\<i32>(1)\n" +
                "    return a.length\n" +
                "}\n",
                "span.shared.bil");
            TestHarness.CheckTrue("具化 SharedSpan<i32> TypeSheet",
                sharedLl.Contains("typesheet.core::SharedSpan$core::i32$"), sharedLl);
            TestHarness.CheckTrue("SharedSpan sheet FlagArray|FlagShared（flags 18）",
                SheetHasFlags(sharedLl, "typesheet.core::SharedSpan$core::i32$", 32, 18),
                sharedLl);

            var strLl = EmitLlFromSource(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<String>(2)\n" +
                "    a[0] = \"x\"\n" +
                "    return a.length\n" +
                "}\n",
                "span.str.bil");
            TestHarness.CheckTrue("String 元素 stride 常量 16",
                strLl.Contains("mul i64") && strLl.Contains(", 16"), strLl);
        }

        private static bool SheetHasFlags(string ll, string sheetNeedle, int size, int flags)
        {
            var idx = ll.IndexOf(sheetNeedle, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }
            var slice = ll.Substring(idx, Math.Min(400, ll.Length - idx));
            return slice.Contains($"i32 {size}, i32 {flags}");
        }

        // ===== invoke.indirect（§15.3 callable 协议）=====

        private static void TestInvokeIndirect()
        {
            // MIR 直译：lambda 经变量调用 → MirInvokeIndirect 字段齐全
            var (_, _, lambdaText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n");
            var lambdaGate = BilGate.Accept(lambdaText, "ind.lambda.bil");
            TestHarness.CheckTrue("lambda 间接调用门禁放行", lambdaGate.IsAccepted,
                string.Join("; ", lambdaGate.Errors));
            var lambdaContext = new MwContext(lambdaGate.Module!);
            var lambdaMir = MirBuilder.Build(lambdaContext);
            var lambdaInst = lambdaMir.Functions
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                .OfType<MirInvokeIndirect>().FirstOrDefault();
            TestHarness.CheckTrue("MIR 含 MirInvokeIndirect", lambdaInst != null);
            TestHarness.CheckTrue("有结果槽", lambdaInst is { Result: not null });
            TestHarness.CheckTrue("实参不含 receiver", lambdaInst is { Args.Count: 1 });
            TestHarness.CheckTrue("静态类型已附带",
                lambdaInst != null && lambdaInst.CallTargetType.Canonical.Length > 0);

            // noret：Action 语句调用 Result=null
            var (_, _, actionText) = BilTestHarness.EmitBilUnit(
                "pub func sink(v: i32) { }\n" +
                "pub func main(): i32 {\n" +
                "    var act = func{() -> { sink(1) }}\n" +
                "    act()\n" +
                "    return 0\n" +
                "}\n");
            var actionGate = BilGate.Accept(actionText, "ind.action.bil");
            TestHarness.CheckTrue("Action noret 门禁放行", actionGate.IsAccepted,
                string.Join("; ", actionGate.Errors));
            var actionMir = MirBuilder.Build(new MwContext(actionGate.Module!));
            var noret = actionMir.Functions
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                .OfType<MirInvokeIndirect>().FirstOrDefault();
            TestHarness.CheckTrue("noret Result=null", noret is { Result: null });

            // 绑定分流：用户类 operator call → IndirectCallBinding.CallOperator
            var (_, _, userText) = BilTestHarness.EmitBilUnit(
                "pub class Doubler {\n" +
                "    pub init() { }\n" +
                "    pub operator call(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Doubler()\n" +
                "    return d(21)\n" +
                "}\n");
            var userGate = BilGate.Accept(userText, "ind.user.bil");
            TestHarness.CheckTrue("用户 operator call 门禁放行", userGate.IsAccepted,
                string.Join("; ", userGate.Errors));
            var userContext = new MwContext(userGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(userContext);
            var userMir = userContext.Mir!;
            var userInst = userMir.Functions
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                .OfType<MirInvokeIndirect>().First();
            var binding = ImplBinder.BindIndirectCall(userContext.Symbols,
                userInst.CallTargetType.Canonical, new[] { "core::i32" }, "core::i32",
                userContext.Module.Functions);
            TestHarness.CheckTrue("绑定为 IndirectCallBinding", binding is IndirectCallBinding);
            TestHarness.CheckTrue("CallOperator 是 Doubler$$call",
                binding.CallOperator.Canonical.Contains("Doubler$$call")
                && binding.CallOperator.Canonical.Contains(".i32"));

            using var userModule = ModuleBuilder.Build(userContext, userMir);
            var ll = userModule.PrintToString();
            TestHarness.CheckTrue("间接调用经 rigi_vtable_entry",
                ll.Contains("call ptr @rigi_vtable_entry(ptr"), ll);
            TestHarness.CheckTrue("虚槽常量为 i32 0",
                ll.Contains("i32 0") && ll.Contains("rigi_vtable_entry"), ll);

            // 泛型 $$call：typeid 前缀平铺在实参前部（VM FindCallTarget 尚未
            // 吃此前缀，E2E 对拍降级；Middleware 绑定/发射覆盖）
            var (_, _, genText) = BilTestHarness.EmitBilUnit(
                "pub class Mapper {\n" +
                "    pub init() { }\n" +
                "    pub operator call\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Mapper()\n" +
                "    return f\\<i32>(42)\n" +
                "}\n");
            var genGate = BilGate.Accept(genText, "ind.generic.bil");
            TestHarness.CheckTrue("泛型 $$call 门禁放行", genGate.IsAccepted,
                string.Join("; ", genGate.Errors));
            var genContext = new MwContext(genGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(genContext);
            var genInst = genContext.Mir!.Functions
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                .OfType<MirInvokeIndirect>().FirstOrDefault();
            TestHarness.CheckTrue("泛型 $$call MIR 含间接调用", genInst != null);
            TestHarness.CheckTrue("泛型 $$call 实参含 typeid 前缀",
                genInst is { Args.Count: 2 });
            var mainFn = genContext.Mir!.Functions.First(f => f.IsEntrypoint);
            var genArgTypes = new List<string>();
            foreach (var arg in genInst!.Args)
            {
                genArgTypes.Add(mainFn.FindLocal(((MirLocalOperand)arg).Name).Type.Canonical);
            }
            var genBind = ImplBinder.BindIndirectCall(genContext.Symbols,
                genInst.CallTargetType.Canonical, genArgTypes,
                mainFn.FindLocal(genInst.Result!).Type.Canonical,
                genContext.Module.Functions);
            TestHarness.CheckTrue("泛型 $$call 绑定命中 Mapper$$call",
                genBind.CallOperator.Canonical.Contains("Mapper$$call"));
            using var genModule = ModuleBuilder.Build(genContext, genContext.Mir!);
            TestHarness.CheckTrue("泛型 $$call 发射 vtable 入口",
                genModule.PrintToString().Contains("rigi_vtable_entry"));
        }

        // ===== native FFI ABI（MW5 切片 b：String/胖引用 out 首参、Any 拒绝、typeid）=====

        private static void TestNativeFfiAbi()
        {
            var stringLl = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"i64_to_string\")\n" +
                "native func i64_to_string(value: i64): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = i64_to_string(42L)\n" +
                "    return 0\n" +
                "}\n", "ffi.string.bil");
            TestHarness.CheckTrue("String 返回 fn 类型：void + rigi_string* 首参",
                stringLl.Contains("declare void @rigi_i64_to_string(ptr, i64)"), stringLl);
            TestHarness.CheckTrue("String 返回调用点 alloca rigi_string",
                stringLl.Contains("alloca { ptr, i64 }"), stringLl);

            var u64Ll = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"u64_to_string\")\n" +
                "native func u64_to_string(value: u64): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = u64_to_string(1UL)\n" +
                "    return 0\n" +
                "}\n", "ffi.u64.bil");
            TestHarness.CheckTrue("u64 面 fn 类型：void + rigi_string* 首参 + i64",
                u64Ll.Contains("declare void @rigi_u64_to_string(ptr, i64)"), u64Ll);

            var anyToStrLl = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = any_to_string((42 as Any))\n" +
                "    return 0\n" +
                "}\n", "ffi.any_to_string.bil");
            TestHarness.CheckTrue("any_to_string 面：String out 首参 + Any 槽指针",
                anyToStrLl.Contains("declare void @rigi_any_to_string(ptr, ptr)"), anyToStrLl);
            TestHarness.CheckTrue("any_to_string 调用形状",
                anyToStrLl.Contains("call void @rigi_any_to_string(ptr"), anyToStrLl);

            var f32Ll = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"f32_to_string\")\n" +
                "native func f32_to_string(value: float): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = f32_to_string(1.0f)\n" +
                "    return 0\n" +
                "}\n", "ffi.f32.bil");
            TestHarness.CheckTrue("f32 面 fn 类型：void + rigi_string* 首参 + float",
                f32Ll.Contains("declare void @rigi_f32_to_string(ptr, float)"), f32Ll);

            var refLl = EmitLlFromSource(
                "pub class User {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"make_user\")\n" +
                "native func make_user(): User\n" +
                "pub func main(): i32 {\n" +
                "    var u = make_user()\n" +
                "    return 0\n" +
                "}\n", "ffi.ref.bil");
            TestHarness.CheckTrue("用户引用返回 fn 类型：void + 槽指针首参",
                refLl.Contains("declare void @rigi_make_user(ptr)"), refLl);
            TestHarness.CheckTrue("用户引用返回调用点 alloca 16B 对齐槽",
                refLl.Contains("alloca { i64, i64 }") && refLl.Contains("align 16"), refLl);

            var anyLl = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"box_any\")\n" +
                "native func box_any(value: Any): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = box_any((1 as Any))\n" +
                "    return 0\n" +
                "}\n", "ffi.any.bil");
            TestHarness.CheckTrue("Any 参数 fn 类型：String out 首参 + Any 槽指针",
                anyLl.Contains("declare void @rigi_box_any(ptr, ptr)"), anyLl);

            var anyRetLl = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"make_any\")\n" +
                "native func make_any(): Any\n" +
                "pub func main(): i32 {\n" +
                "    var a = make_any()\n" +
                "    return 0\n" +
                "}\n", "ffi.anyret.bil");
            TestHarness.CheckTrue("Any 返回 fn 类型：void + 槽指针首参",
                anyRetLl.Contains("declare void @rigi_make_any(ptr)"), anyRetLl);
            TestHarness.CheckTrue("Any 返回调用点 alloca 16B 对齐槽",
                anyRetLl.Contains("alloca { i64, i64 }") && anyRetLl.Contains("align 16"),
                anyRetLl);

            ExpectMwNotSupportedFromSource(
                "@NativeLibrary(\"libc\")\n" +
                "@NativeSymbol(\"abs\")\n" +
                "native func abs(x: i32): i32\n" +
                "pub func main(): i32 {\n" +
                "    return abs(-1)\n" +
                "}\n",
                "MW1 不支持 native 库: libc",
                "非 rigi_rt 库拒绝消息");

            var typeIdLl = EmitLlFromSource(
                "pub class Holder {\n" +
                "    pub init() { }\n" +
                "    pub func tag\\<T>(n: i32): i32 { return n }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Holder()\n" +
                "    return h.tag\\<i32>(1)\n" +
                "}\n", "ffi.typeid.bil");
            TestHarness.CheckTrue("typeid ABI：.this 胖引用 → typeid ptr → 普通 i32（§7.2）",
                typeIdLl.Contains(
                    "define internal i32 @\"Holder$tag(n:.i32)@.i32\"({ i64, i64 } %0, ptr %1, i32 %2)"),
                typeIdLl);
            TestHarness.CheckTrue("typeid LLVM 表示 = TypeSheet 指针（getid.type 物化）",
                typeIdLl.Contains("store ptr @\"typesheet.core::i32\""), typeIdLl);
        }

        // ===== Any/Box 物化（MW5 切片 c1：MirBoxAny/MirUnboxAny + tag 编码）=====

        private static void TestBoxAnyEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var a = x as Any\n" +
                "    var y = a as i32\n" +
                "    var s = \"hi\"\n" +
                "    var b = s as Any\n" +
                "    var t = b as String\n" +
                "    if (t == \"hi\") { return y }\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "box.any.bil");
            TestHarness.CheckTrue("Box Any 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var allInsts = context.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 MirBoxAny", allInsts.OfType<MirBoxAny>().Any());
            TestHarness.CheckTrue("MIR 含 MirUnboxAny", allInsts.OfType<MirUnboxAny>().Any());

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("tag0 pack：or + insertvalue",
                ll.Contains("or i64") && ll.Contains("insertvalue { i64, i64 }"), ll);
            TestHarness.CheckTrue("tag1：rigi_malloc + memcpy",
                ll.Contains("call ptr @rigi_malloc(i32")
                && ll.Contains("call void @llvm.memcpy.p0.p0.i64"), ll);
            TestHarness.CheckTrue("unbox 类型检查（lshr tag + and sheet）",
                ll.Contains("lshr i64") && ll.Contains("and i64"), ll);
            TestHarness.CheckTrue("unbox 调 rigi_abort_invalid_cast",
                ll.Contains("call void @rigi_abort_invalid_cast(ptr")
                && ll.Contains("typesheet."), ll);
            TestHarness.CheckTrue("abort 面已声明",
                ll.Contains("declare void @rigi_abort_invalid_cast(ptr)"), ll);

            ExpectMwNotSupportedFromSource(
                "pub func main(): i32 {\n" +
                "    var x = 1\n" +
                "    var y = x as i64\n" +
                "    return 0\n" +
                "}\n",
                "cast 数值/String 转换随 §12 转换批",
                "数值转换拒绝消息");
            ExpectMwNotSupportedFromSource(
                "pub struct A {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub struct B {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new A(1)\n" +
                "    var b = a as B\n" +
                "    return 0\n" +
                "}\n",
                "cast 值类型转换随 MW4 批 3",
                "struct 值转换拒绝消息");
        }

        private static string EmitLlFromSource(string source, string label)
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(source);
            var gate = BilGate.Accept(text, label);
            TestHarness.CheckTrue(label + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var module = ModuleBuilder.Build(context, context.Mir!);
            return module.PrintToString();
        }

        private static void ExpectMwNotSupportedFromSource(string source, string needle, string label)
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(source);
            var gate = BilGate.Accept(text, label);
            TestHarness.CheckTrue(label + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var caught = false;
            var message = "";
            try
            {
                var context = new MwContext(gate.Module!);
                RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
                using var module = ModuleBuilder.Build(context, context.Mir!);
            }
            catch (MwNotSupportedException ex)
            {
                caught = ex.Message.Contains(needle);
                message = ex.Message;
            }
            TestHarness.CheckTrue(label, caught, message);
        }

        // ===== 接口默认方法布局（iMap 槽指向接口 fn）=====

        private static void TestInterfaceDefaultMethods()
        {
            var ll = EmitLlFromSource(
                "pub interface Shape {\n" +
                "    pub func area(): i32\n" +
                "    pub func describe(): String { return \"d\" }\n" +
                "}\n" +
                "pub class Sq implements Shape {\n" +
                "    pub var s: i32\n" +
                "    pub init(n: i32) { s = n }\n" +
                "    pub override func area(): i32 { return (s * s) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var sh: Shape = new Sq(3)\n" +
                "    return sh.area()\n" +
                "}\n",
                "iface.default.bil");
            TestHarness.CheckTrue("默认方法未抛未实现",
                !ll.Contains("MW4 接口方法未实现"), ll);
            TestHarness.CheckTrue("vtable 含接口默认方法",
                ll.Contains("Shape$describe") || ll.Contains("@\"Shape$describe"), ll);

            var genLl = EmitLlFromSource(
                "pub interface IBox\\<T> {\n" +
                "    func get(): T\n" +
                "    func tag(): i32 { return 7 }\n" +
                "}\n" +
                "pub class Box3\\<T> implements IBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub override func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: IBox\\<i32> = new Box3\\<i32>(41)\n" +
                "    return b.tag()\n" +
                "}\n",
                "iface.generic.default.bil");
            TestHarness.CheckTrue("泛型接口默认方法未抛未实现",
                !genLl.Contains("MW4 接口方法未实现"), genLl);
            TestHarness.CheckTrue("泛型接口默认方法入表",
                genLl.Contains("IBox$tag") || genLl.Contains("@\"IBox$tag"), genLl);
        }

        // ===== 构造类型具化（MW5 c2-a）=====

        private static void TestConstructedTypes()
        {
            // 收集闭包：嵌套构造 + 环保护（A<T>:B<T>:A<T> 手写 BIL，不经门禁）
            var cycleBil =
                "BIL \"1.1\"\n\nMetadata {\n}\n\nResources {\n}\n\n" +
                "LocalSymbols {\n" +
                "    .type Box = class generic(T) pub {\n" +
                "        .field Box#v@.generic<$.generic.T> pub var\n" +
                "        .method Box$init(v:.generic<$.generic.T>)@.void pub init\n" +
                "    }\n" +
                "    .type A = class generic(T)\n" +
                "        extends B<.generic<$.generic.T>>\n" +
                "        pub {\n" +
                "        .method A$init()@.void pub init\n" +
                "    }\n" +
                "    .type B = class generic(T)\n" +
                "        extends A<.generic<$.generic.T>>\n" +
                "        pub {\n" +
                "        .method B$init()@.void pub init\n" +
                "    }\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n\nExternalSymbols {\n}\n\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        Box<.i32> b,\n" +
                "        Box<Box<.i32>> n,\n" +
                "        A<.i32> a\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        ret\n" +
                "    }\n" +
                "}\n";
            var cycleModule = BilReader.Read(cycleBil);
            var cycleCtx = new MwContext(cycleModule);
            var collected = ConstructedTypeCollector.Collect(cycleCtx);
            TestHarness.CheckTrue("收集含 Box<i32>",
                collected.Contains("Box<core::i32>"));
            TestHarness.CheckTrue("收集含嵌套 Box<Box<i32>>",
                collected.Contains("Box<Box<core::i32>>"));
            TestHarness.CheckTrue("收集含环上 A<i32>/B<i32>",
                collected.Contains("A<core::i32>") && collected.Contains("B<core::i32>"));
            TestHarness.CheckTrue("环保护不重复入表",
                collected.Count(c => c == "A<core::i32>") == 1);

            // 具化计划：隐藏 typeid + 胖值槽 + vtable=模板 fn
            var (_, _, srcText) = BilTestHarness.EmitBilUnit(
                "pub class Box2\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box2\\<i32>(7)\n" +
                "    return b.get()\n" +
                "}\n");
            var gate = BilGate.Accept(srcText, "c2a.bil");
            TestHarness.CheckTrue("构造类型源门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var closed = ConstructedTypeCollector.Collect(context);
            TestHarness.CheckTrue("前端路径收集 Box2<i32>",
                closed.Any(c => c.Contains("Box2") && c.Contains("i32")));
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var plan = context.Layout!.Find("Box2<core::i32>");
            TestHarness.CheckTrue("构造计划已入表", plan != null);
            TestHarness.CheckTrue("隐藏 typeid 槽@16",
                plan!.HiddenTypeIdSlots.Count == 1
                && plan.HiddenTypeIdSlots[0].ParamName == "T"
                && plan.HiddenTypeIdSlots[0].Offset == 16);
            var vField = FieldOf(plan, "#v@");
            TestHarness.CheckTrue("字段复用模板 canonical 且为胖值槽",
                vField != null && vField.IsReferenceSlot && vField.Offset == 32
                && vField.Symbol.StartsWith("Box2#"));
            TestHarness.CheckTrue("Size/refMap（头+typeid+胖槽）",
                plan.Size == 48 && plan.RefMap.Length == 1 && plan.RefMap[0] == 1);
            TestHarness.CheckTrue("vtable 槽=模板 fn",
                plan.VTableSlots.Any(s => s.Contains("Box2$get(")));

            using var llvm = ModuleBuilder.Build(context, context.Mir!);
            var ll = llvm.PrintToString();
            TestHarness.CheckTrue("构造 sheet 全局（转义名）",
                ll.Contains("typesheet.Box2$core::i32$"), ll);
            TestHarness.CheckTrue("tag2 pack 指向构造 sheet",
                ll.Contains("typesheet.Box2$core::i32$")
                && (ll.Contains("shl i64 2, 56") || ll.Contains("shl i64 2, i64 56")
                    || ll.Contains("or i64")), ll);
            TestHarness.CheckTrue("new 站 typeid 常量 store",
                ll.Contains("typesheet.core::i32") && ll.Contains("store i64"), ll);
            TestHarness.CheckTrue("prologue 从隐藏字段 load 类级 typeid",
                ll.Contains("tid.bits") || ll.Contains("load i64"), ll);

            ExpectMwNotSupportedFromSource(
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var w = new Wrap\\<i32>(1)\n" +
                "    return w.v\n" +
                "}\n",
                "泛型值类型构造",
                "泛型 struct 构造拒绝消息");
        }

        // ===== 构造接口 iMap + VirtualSlotOf 精确化（MW5 c2-b）=====

        private static void TestConstructedDispatch()
        {
            var (_, _, ifaceSrc) = BilTestHarness.EmitBilUnit(
                "pub interface IBox\\<T> {\n" +
                "    func get(): T\n" +
                "    func tag(): i32\n" +
                "}\n" +
                "pub class Box3\\<T> implements IBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub override func get(): T { return this.v }\n" +
                "    pub override func tag(): i32 { return 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: IBox\\<i32> = new Box3\\<i32>(1)\n" +
                "    return b.tag()\n" +
                "}\n");
            var ifaceGate = BilGate.Accept(ifaceSrc, "c2b.imap.bil");
            TestHarness.CheckTrue("构造接口源门禁放行", ifaceGate.IsAccepted,
                string.Join("; ", ifaceGate.Errors));
            var ifaceCtx = new MwContext(ifaceGate.Module!);
            var collected = ConstructedTypeCollector.Collect(ifaceCtx);
            TestHarness.CheckTrue("收集含 IBox<i32>",
                collected.Any(c => c.Contains("IBox") && c.Contains("i32")));
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(ifaceCtx);
            var boxPlan = ifaceCtx.Layout!.Find("Box3<core::i32>");
            TestHarness.CheckTrue("Box3<i32> 计划已入表", boxPlan != null);
            TestHarness.CheckTrue("构造接口 iMap 段键与槽基址",
                boxPlan!.IMap.Count >= 1
                && boxPlan.IMap.Any(e => e.InterfaceType == "IBox<core::i32>"
                    && e.BaseOffset == 2),
                boxPlan == null ? "" : string.Join(",", boxPlan.IMap));
            TestHarness.CheckTrue("iMap 槽序复用本类 get/tag",
                boxPlan!.VTableSlots.Count == 4
                && boxPlan.VTableSlots[2] == boxPlan.VTableSlots[0]
                && boxPlan.VTableSlots[3] == boxPlan.VTableSlots[1]);
            var ifacePlan = ifaceCtx.Layout.Find("IBox<core::i32>");
            TestHarness.CheckTrue("IBox<i32> 空壳 sheet 计划",
                ifacePlan != null
                && ifacePlan.Kind == TypeLayoutKind.Interface
                && ifacePlan.VTableSlots.Count == 2);
            using var ifaceLlvm = ModuleBuilder.Build(ifaceCtx, ifaceCtx.Mir!);
            var ifaceLl = ifaceLlvm.PrintToString();
            TestHarness.CheckTrue("构造接口 sheet 全局",
                ifaceLl.Contains("typesheet.IBox$core::i32$"), ifaceLl);
            TestHarness.CheckTrue("imap 引用具化接口 sheet",
                ifaceLl.Contains("typesheet.imap.Box3$core::i32$")
                && ifaceLl.Contains("typesheet.IBox$core::i32$"), ifaceLl);

            var (_, _, virtSrc) = BilTestHarness.EmitBilUnit(
                "pub open class PairV\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub open func foo(): i32 { return 1 }\n" +
                "    pub open func bar(): i32 { return 2 }\n" +
                "}\n" +
                "pub class PairD\\<T> : PairV\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub override func foo(): i32 { return 10 }\n" +
                "    pub override func bar(): i32 { return 20 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var x: PairV\\<i32> = new PairD\\<i32>()\n" +
                "    return (x.foo() + x.bar())\n" +
                "}\n");
            var virtGate = BilGate.Accept(virtSrc, "c2b.virt.bil");
            TestHarness.CheckTrue("双虚泛型类门禁放行", virtGate.IsAccepted,
                string.Join("; ", virtGate.Errors));
            var virtCtx = new MwContext(virtGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(virtCtx);
            var hostPlan = virtCtx.Layout!.Find("PairV<core::i32>");
            TestHarness.CheckTrue("PairV<i32> 构造计划双虚槽",
                hostPlan != null && hostPlan.VTableSlots.Count >= 2
                && hostPlan.VTableSlots[0].Contains("$foo(")
                && hostPlan.VTableSlots[1].Contains("$bar("),
                hostPlan == null ? "" : string.Join(",", hostPlan.VTableSlots));
            var derivedPlan = virtCtx.Layout.Find("PairD<core::i32>");
            TestHarness.CheckTrue("PairD<i32> override 复用基槽",
                derivedPlan != null && derivedPlan.VTableSlots.Count >= 2
                && derivedPlan.VTableSlots[0].Contains("PairD$foo(")
                && derivedPlan.VTableSlots[1].Contains("PairD$bar("));
            using var virtLlvm = ModuleBuilder.Build(virtCtx, virtCtx.Mir!);
            var virtLl = virtLlvm.PrintToString();
            TestHarness.CheckTrue("VirtualSlotOf 命中槽 0 与 1",
                virtLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && virtLl.Contains("i32 0") && virtLl.Contains("i32 1"), virtLl);

            var opLl = EmitLlFromSource(
                "pub class Doubler {\n" +
                "    pub init() { }\n" +
                "    pub operator call(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Doubler()\n" +
                "    return d(21)\n" +
                "}\n", "c2b.op.bil");
            TestHarness.CheckTrue("operator call 虚槽仍为 0",
                opLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && opLl.Contains("i32 0"), opLl);
            var lambdaLl = EmitLlFromSource(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n", "c2b.lambda.bil");
            TestHarness.CheckTrue("lambda 间接调用虚槽仍为 0",
                lambdaLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && lambdaLl.Contains("i32 0"), lambdaLl);
        }

        // ===== vargs/kwargs 包（MW5 d）：签名放行 + 直译按位 =====

        private static void TestVargsKwargs()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "func sum(nums: i32...): i32 { return nums.length }\n" +
                "func show(opts: named String...): i32 { return opts.length }\n" +
                "func collect\\<TArgs...>(values: TArgs...): i32 { return values.length }\n" +
                "pub func main(): i32 {\n" +
                "    return ((sum(1, 2) + show(a = \"x\")) + collect(1, \"s\"))\n" +
                "}\n");
            var gate = BilGate.Accept(text, "vargs.bil");
            TestHarness.CheckTrue("包签名用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);

            var sum = mir.Functions.Single(f => f.Symbol.Canonical == "$sum()@.i32");
            TestHarness.CheckTrue("vargs 登记为普通参数",
                sum.Parameters.Count == 1 && sum.Parameters[0].Name == ".vargs.nums");
            TestHarness.CheckTrue("vargs 槽类型 = Array<Any>",
                TypeLayout.IsArray(sum.Parameters[0].Type)
                && sum.Parameters[0].Type.Canonical.Contains("Any"));

            var show = mir.Functions.Single(f => f.Symbol.Canonical == "$show()@.i32");
            TestHarness.CheckTrue("kwargs 登记为普通参数",
                show.Parameters.Count == 1 && show.Parameters[0].Name == ".kwargs.opts");
            TestHarness.CheckTrue("kwargs 槽类型 = Array<Pair>",
                TypeLayout.IsArray(show.Parameters[0].Type)
                && show.Parameters[0].Type.Canonical.Contains("Pair"));

            var collect = mir.Functions.Single(f => f.Symbol.Canonical == "$collect()@.i32");
            TestHarness.CheckTrue("泛型包+值包按 §7.2 序",
                collect.Parameters.Count == 2
                && collect.Parameters[0].Name == ".generic.TArgs"
                && collect.Parameters[1].Name == ".vargs.values");
            TestHarness.CheckTrue("泛型位置包 TypeRef = array（非 typeid）",
                TypeLayout.IsArray(collect.Parameters[0].Type)
                && !TypeLayout.IsTypeId(collect.Parameters[0].Type));
            TestHarness.CheckTrue("值包 TypeRef = Array<Any>",
                TypeLayout.IsArray(collect.Parameters[1].Type));

            var main = mir.Functions.Single(f => f.IsEntrypoint);
            var sumCall = main.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                .First(c => c.Target.Canonical == "$sum()@.i32");
            TestHarness.CheckTrue("调用点包为单实参", sumCall.Args.Count == 1);
            TestHarness.CheckTrue("调用点实参类型 = 包类型",
                sumCall.Args[0] is MirLocalOperand packArg
                && TypeLayout.IsArray(main.FindLocal(packArg.Name).Type));

            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("Any TypeSheet 供 Pair<,Any> 具化",
                ll.Contains("@\"typesheet.core::Any\""), ll);
            TestHarness.CheckTrue("sum LLVM 形参 = 胖引用",
                ll.Contains("define internal i32 @\"$sum()@.i32\"({ i64, i64 }"), ll);
            TestHarness.CheckTrue("show LLVM 形参 = 胖引用",
                ll.Contains("define internal i32 @\"$show()@.i32\"({ i64, i64 }"), ll);
            TestHarness.CheckTrue("collect LLVM 两包均胖引用",
                ll.Contains("define internal i32 @\"$collect()@.i32\"({ i64, i64 }")
                && ll.Contains("{ i64, i64 } %0, { i64, i64 } %1)"), ll);

            // 包转发：前端 take(nums) 会再打包；手改 invoke 整包转发后直译
            var (_, fwdModule, _) = BilTestHarness.EmitBilUnit(
                "func take(nums: i32...): i32 { return nums.length }\n" +
                "func wrap(nums: i32...): i32 { return take(nums) }\n" +
                "pub func main(): i32 { return wrap(1, 2, 3) }\n");
            RewriteWrapForward(fwdModule);
            var fwdText = BilWriter.Write(fwdModule);
            var fwdGate = BilGate.Accept(fwdText, "fwd.bil");
            TestHarness.CheckTrue("整包转发门禁放行", fwdGate.IsAccepted,
                string.Join("; ", fwdGate.Errors));
            var fwdCtx = new MwContext(fwdGate.Module!);
            var fwdMir = MirBuilder.Build(fwdCtx);
            var wrap = fwdMir.Functions.Single(f => f.Symbol.Canonical == "$wrap()@.i32");
            var fwdCall = wrap.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                .Single(c => c.Target.Canonical == "$take()@.i32");
            TestHarness.CheckTrue("整包转发实参 = $.vargs.nums",
                fwdCall.Args.Count == 1
                && fwdCall.Args[0] is MirLocalOperand { Name: ".vargs.nums" });
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(fwdCtx);
            using var fwdLl = ModuleBuilder.Build(fwdCtx, fwdCtx.Mir!);
            TestHarness.CheckTrue("整包转发 LLVM 发射",
                fwdLl.PrintToString().Contains("@\"$take()@.i32\""),
                fwdLl.PrintToString());
        }

        private static void RewriteWrapForward(BilModule module)
        {
            var wrap = module.Functions.Single(f => f.Symbol == "$wrap()@.i32");
            var entry = wrap.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            var invoke = entry.Instructions.OfType<InvokeInstruction>().Single();
            entry.Instructions.Clear();
            entry.Instructions.Add(new InvokeInstruction(invoke.Method, invoke.Target,
                new[] { new BilVariableOperand(".vargs.nums") }));
            entry.Instructions.Add(new RetInstruction(invoke.Target));
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

            // raw.hex/raw.bin：§19.3 未定 load 目标类型（array/Span 布局
            // 知识）→ 物化随 MW4 定稿；门禁放行（verifier 对 raw 跳过严格
            // 匹配），资源发射受控拒绝
            const string rawBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawtest\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Data = raw.hex x2FF2331C,\n" +
                "    R_Zero = i32 0\n" +
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
                "        .string d,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Data) $d\n" +
                "        load res(R_Zero) $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            var rawGate = BilGate.Accept(rawBil, "raw.bil");
            TestHarness.CheckTrue("raw 用例门禁放行", rawGate.IsAccepted,
                string.Join("; ", rawGate.Errors));
            var rawCaught = false;
            try
            {
                var rawContext = new MwContext(rawGate.Module!);
                var rawMir = MirBuilder.Build(rawContext);
                using var rawModule = ModuleBuilder.Build(rawContext, rawMir);
            }
            catch (MwNotSupportedException ex)
            {
                rawCaught = ex.Message.Contains("array<u8>");
            }
            TestHarness.CheckTrue("raw 资源受控拒绝（非 .array<u8> 目标）", rawCaught);

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

        // ===== RcInjection（MW7a）=====

        private static MwContext PipelineFromSource(string source, string file)
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(source);
            var gate = BilGate.Accept(text, file);
            TestHarness.CheckTrue(file + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors.Take(3)));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            return context;
        }

        private static MirFunction FnOf(MwContext context, string needle)
        {
            var found = context.Mir!.Functions.FirstOrDefault(f => f.Symbol.Canonical.Contains(needle));
            TestHarness.CheckTrue("找到函数 " + needle, found != null,
                string.Join(", ", context.Mir.Functions.Select(f => f.Symbol.Canonical)));
            return found!;
        }

        private static void TestRcInjection()
        {
            // ① 入口参数 acquire 序列
            var ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func take(n: Node): i32 { return n.x }\n" +
                "pub func main(): i32 {\n" +
                "    return take(new Node(1))\n" +
                "}\n",
                "rc.param.bil");
            var take = FnOf(ctx, "$take(");
            TestHarness.CheckTrue("① take 入口首条是参数 acquire",
                take.Blocks[0].Instructions.Count > 0
                && take.Blocks[0].Instructions[0] is MirAcquireSlot acq
                && acq.Local == take.Parameters[0].Name,
                take.Blocks[0].Instructions.FirstOrDefault()?.GetType().Name ?? "empty");

            // ② CopyLocal 三段式与 dst==src 删除
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func alias(n: Node): Node {\n" +
                "    var m = n\n" +
                "    return m\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var n = alias(new Node(1))\n" +
                "    return n.x\n" +
                "}\n",
                "rc.copy.bil");
            var alias = FnOf(ctx, "$alias(");
            var copies = alias.Blocks.SelectMany(b => b.Instructions.Select((inst, i) => (b, i, inst)))
                .Where(t => t.inst is MirCopyLocal)
                .ToList();
            TestHarness.CheckTrue("② 存在托管 CopyLocal", copies.Count > 0);
            foreach (var (block, i, inst) in copies)
            {
                var copy = (MirCopyLocal)inst;
                if (!TypeLayout.IsManagedSlot(ctx, alias.FindLocal(copy.Target).Type))
                {
                    continue;
                }
                TestHarness.CheckTrue("② 三段式前 Release",
                    i > 0 && block.Instructions[i - 1] is MirReleaseSlot rel
                    && rel.Local == copy.Target);
                TestHarness.CheckTrue("② 三段式后 Acquire",
                    i + 1 < block.Instructions.Count
                    && block.Instructions[i + 1] is MirAcquireSlot a
                    && a.Local == copy.Target);
            }

            // dst==src 删除：手写 BIL set.var $n $n
            // 用最小手写模块覆盖自赋值删除
            const string SelfCopyBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"selfcopy\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Zero = i32 0,\n" +
                "    R_Null = null type(Node)\n" +
                "}\n" +
                "\n" +
                "LocalSymbols {\n" +
                "    .type Node = class pub {\n" +
                "        .field Node#x@.i32 pub var\n" +
                "        .method Node$init(v:.i32)@.void pub\n" +
                "    }\n" +
                "    .method $id(n:Node)@.void pub\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n" +
                "\n" +
                "ExternalSymbols {\n" +
                "}\n" +
                "\n" +
                "fn($id(n:Node)@.void) {\n" +
                "    .args {\n" +
                "        .return = .void,\n" +
                "        n = Node\n" +
                "    }\n" +
                "    .vars {\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        set.var $n $n\n" +
                "        ret\n" +
                "    }\n" +
                "}\n" +
                "\n" +
                "fn(Node$init(v:.i32)@.void) {\n" +
                "    .args {\n" +
                "        .return = .void,\n" +
                "        .this = Node,\n" +
                "        v = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        ret\n" +
                "    }\n" +
                "}\n" +
                "\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        Node n,\n" +
                "        .i32 z\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Null) $n\n" +
                "        invoke.noret fn($id(n:Node)@.void) [$n]\n" +
                "        load res(R_Zero) $z\n" +
                "        ret $z\n" +
                "    }\n" +
                "}\n";
            var selfGate = BilGate.Accept(SelfCopyBil, "selfcopy.bil");
            TestHarness.CheckTrue("② 自赋值模块门禁", selfGate.IsAccepted,
                string.Join("; ", selfGate.Errors.Take(3)));
            if (selfGate.IsAccepted)
            {
                var selfCtx = new MwContext(selfGate.Module!);
                RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(selfCtx);
                var idFn = FnOf(selfCtx, "$id(");
                TestHarness.CheckTrue("② dst==src CopyLocal 已删除",
                    !idFn.Blocks.SelectMany(b => b.Instructions).OfType<MirCopyLocal>().Any());
            }

            // ③ 产出类前置 release
            ctx = PipelineFromSource(
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s = \"a\" + \"b\"\n" +
                "    Console.println(s)\n" +
                "    return 0\n" +
                "}\n",
                "rc.prod.bil");
            var main = ctx.Mir!.Functions.First(f => f.IsEntrypoint);
            var concat = main.Blocks.SelectMany(b => b.Instructions)
                .Select((inst, i) => (inst, i))
                .FirstOrDefault(t => t.inst is MirBinaryIntrinsic);
            TestHarness.CheckTrue("③ 含 string concat", concat.inst != null);
            if (concat.inst is MirBinaryIntrinsic bin)
            {
                var block = main.Blocks.First(b => b.Instructions.Contains(bin));
                var idx = block.Instructions.ToList().IndexOf(bin);
                TestHarness.CheckTrue("③ concat 前是 ReleaseSlot",
                    idx > 0 && block.Instructions[idx - 1] is MirReleaseSlot pre
                    && pre.Local == bin.Target);
            }

            // ④ ret 块 release 全覆盖 + $mw.ret 合成
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func wrap(n: Node): Node { return n }\n" +
                "pub func main(): i32 {\n" +
                "    var n = wrap(new Node(1))\n" +
                "    return n.x\n" +
                "}\n",
                "rc.ret.bil");
            var wrap = FnOf(ctx, "$wrap(");
            TestHarness.CheckTrue("④ $mw.ret 已登记",
                wrap.Locals.Any(l => l.Name == RigiCompiler.Middleware.Passes.RcInjectionPass.RetLocalName));
            var retBlock = wrap.Blocks.First(b => b.Terminator is MirRet);
            TestHarness.CheckTrue("④ ret 操作数是 $mw.ret",
                retBlock.Terminator is MirRet { Value: MirLocalOperand op }
                && op.Name == RigiCompiler.Middleware.Passes.RcInjectionPass.RetLocalName);
            TestHarness.CheckTrue("④ ret 块末尾是 ReleaseSlot",
                retBlock.Instructions.Count > 0
                && retBlock.Instructions[^1] is MirReleaseSlot);
            TestHarness.CheckTrue("④ 出口 release 不含 $mw.ret",
                !retBlock.Instructions.TakeLast(1).OfType<MirReleaseSlot>()
                    .Any(r => r.Local == RigiCompiler.Middleware.Passes.RcInjectionPass.RetLocalName)
                || wrap.Parameters.All(p => p.Name != RigiCompiler.Middleware.Passes.RcInjectionPass.RetLocalName));

            // ⑤ class .this 管理、值类型 .this 豁免
            ctx = PipelineFromSource(
                "pub class C {\n" +
                "    pub var x: i32\n" +
                "    pub init() { x = 0 }\n" +
                "    pub func get(): i32 { return x }\n" +
                "}\n" +
                "pub struct S {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "    pub func get(): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new C()\n" +
                "    var s = new S(1)\n" +
                "    return c.get() + s.get()\n" +
                "}\n",
                "rc.this.bil");
            var classGet = FnOf(ctx, "C$get");
            var structGet = FnOf(ctx, "S$get");
            TestHarness.CheckTrue("⑤ class .this 入口 acquire",
                classGet.Blocks[0].Instructions.OfType<MirAcquireSlot>()
                    .Any(a => a.Local == ".this"));
            TestHarness.CheckTrue("⑤ 值类型 .this 豁免 acquire",
                !structGet.Blocks[0].Instructions.OfType<MirAcquireSlot>()
                    .Any(a => a.Local == ".this"));
            TestHarness.CheckTrue("⑤ 值类型 .this 豁免 release",
                !structGet.Blocks.SelectMany(b => b.Instructions).OfType<MirReleaseSlot>()
                    .Any(r => r.Local == ".this"));

            // ⑥ void 函数也有局部 release
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func drop(n: Node) { var m = n }\n" +
                "pub func main(): i32 {\n" +
                "    drop(new Node(1))\n" +
                "    return 0\n" +
                "}\n",
                "rc.void.bil");
            var drop = FnOf(ctx, "$drop(");
            var dropRet = drop.Blocks.First(b => b.Terminator is MirRet);
            TestHarness.CheckTrue("⑥ void 函数 ret 块含 ReleaseSlot",
                dropRet.Instructions.OfType<MirReleaseSlot>().Any());

            // .ll 黄金：含 ref 拷贝的函数出现 acquire/release 配对
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func copy(n: Node): Node { return n }\n" +
                "pub func main(): i32 {\n" +
                "    var n = copy(new Node(1))\n" +
                "    return n.x\n" +
                "}\n",
                "rc.ll.bil");
            using var module = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue(".ll 含 rigi_ref_acquire",
                ll.Contains("call i64 @rigi_ref_acquire("), ll);
            TestHarness.CheckTrue(".ll 含 rigi_ref_release",
                ll.Contains("call void @rigi_ref_release("), ll);
        }

        // MW7a 边界：enum String payload / 嵌套 rich 折算序 / 非 rich 含 String 也产 refMap
        private static void TestRefMapMw7a()
        {
            var layout = BuildLayout(
                "pub class Node {\n" +
                "    pub var x: i32 = 0\n" +
                "}\n" +
                "pub enum struct Note {\n" +
                "    pub const text: String\n" +
                "    pub init(_ -> text)\n" +
                "}[\n" +
                "    A(\"a\"),\n" +
                "    B(text = _)\n" +
                "]\n" +
                "pub rich struct Inner {\n" +
                "    pub var name: String\n" +
                "    pub var node: Node?\n" +
                "}\n" +
                "pub rich struct Outer {\n" +
                "    pub var kid: Inner\n" +
                "    pub var tag: String\n" +
                "}\n" +
                "pub struct Tagged {\n" +
                "    pub var n: i32\n" +
                "    pub var label: String\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");

            var note = layout.Find("Note");
            TestHarness.CheckTrue("enum String payload 产 kind1",
                note != null
                && note.RefMap.Length == 1
                && FieldOf(note, "#text@")!.Offset == 16
                && note.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 1),
                note == null ? "missing" : string.Join(",", note.RefMap));

            var inner = layout.Find("Inner");
            var outer = layout.Find("Outer");
            TestHarness.CheckTrue("嵌套 rich 外层 refMap 序（kind1,kind0,kind1）",
                inner != null && outer != null
                && inner.RefMap.Length == 2
                && inner.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 0)
                && inner.RefMap[1] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindFatRef, 0)
                && outer.RefMap.Length == 3
                && outer.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 0)
                && outer.RefMap[1] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindFatRef, 0)
                && outer.RefMap[2] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 0),
                outer == null ? "missing" : string.Join(",", outer.RefMap));

            var tagged = layout.Find("Tagged");
            TestHarness.CheckTrue("非 rich 含 String 也产 refMap kind1",
                tagged != null
                && (tagged.TypeFlags & TypeLayoutPlan.FlagRich) == 0
                && tagged.RefMap.Length == 1
                && FieldOf(tagged, "#label@")!.IsStringSlot
                && FieldOf(tagged, "#label@")!.Offset == 16
                && tagged.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 1),
                tagged == null ? "missing" : string.Join(",", tagged.RefMap));
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
