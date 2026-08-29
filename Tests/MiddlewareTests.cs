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
using RigiCompiler.Middleware.Passes;
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
            ("TestMirTryExpand", TestMirTryExpand),
            ("TestBinding", TestBinding),
            ("TestObjectEmission", TestObjectEmission),
            ("TestLlGoldenAnchors", TestLlGoldenAnchors),
            ("TestNullResourceEmission", TestNullResourceEmission),
            ("TestDivGuardEmission", TestDivGuardEmission),
            ("TestLayoutPlans", TestLayoutPlans),
            ("TestTypeSheetEmission", TestTypeSheetEmission),
            ("TestTypeCheckEmission", TestTypeCheckEmission),
            ("TestWrapperStorageEmission", TestWrapperStorageEmission),
            ("TestProxyBakingEmission", TestProxyBakingEmission),
            ("TestProxyRingReceiverAddrEmission", TestProxyRingReceiverAddrEmission),
            ("TestValueProxyBakingEmission", TestValueProxyBakingEmission),
            ("TestEntityFieldProxyBakingEmission", TestEntityFieldProxyBakingEmission),
            ("TestEntityFieldRouterBranchEmission", TestEntityFieldRouterBranchEmission),
            ("TestSetRingInnerRerouteDispatchEmission",
                TestSetRingInnerRerouteDispatchEmission),
            ("TestSetRingInnerRerouteLinearWhenNoBranches",
                TestSetRingInnerRerouteLinearWhenNoBranches),
            ("TestWildcardProxyBakingEmission", TestWildcardProxyBakingEmission),
            ("TestGenericWildcardBakingEmission", TestGenericWildcardBakingEmission),
            ("TestMixedSpecificWildcardBakingEmission", TestMixedSpecificWildcardBakingEmission),
            ("TestOperatorAndSameLayerProxyBakingEmission",
                TestOperatorAndSameLayerProxyBakingEmission),
            ("TestUserOperatorDispatchEmission", TestUserOperatorDispatchEmission),
            ("TestWrapperIndexInheritanceClosure", TestWrapperIndexInheritanceClosure),
            ("TestHiddenSlotEntityDedupAcrossHierarchy",
                TestHiddenSlotEntityDedupAcrossHierarchy),
            ("TestCallWildcardLoweringEmission", TestCallWildcardLoweringEmission),
            ("TestSingletonLoweringEmission", TestSingletonLoweringEmission),
            ("TestSingletonEntryStubOrder", TestSingletonEntryStubOrder),
            ("TestMethodProxyBakingEmission", TestMethodProxyBakingEmission),
            ("TestMethodProxyBakingDoubleLayer", TestMethodProxyBakingDoubleLayer),
            ("TestMethodProxyBakingWildcard", TestMethodProxyBakingWildcard),
            ("TestMethodProxyWildcardUnpackByName", TestMethodProxyWildcardUnpackByName),
            ("TestMethodProxyBakingHostForms", TestMethodProxyBakingHostForms),
            ("TestMethodProxyEntityComposition", TestMethodProxyEntityComposition),
            ("TestMethodProxyWildcardRerouteRejects", TestMethodProxyWildcardRerouteRejects),
            ("TestSuperCallBypassesWrapperBaking", TestSuperCallBypassesWrapperBaking),
            ("TestGetTypeIdVarEmission", TestGetTypeIdVarEmission),
            ("TestTypeIdConstructedSheets", TestTypeIdConstructedSheets),
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
            ("TestRcPropagatePad", TestRcPropagatePad),
            ("TestExceptionEmission", TestExceptionEmission),
            ("TestRefMapMw7a", TestRefMapMw7a),
            ("TestDynamicNew", TestDynamicNew),
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
            // MW9b-G：core 异常类型 init 族 + getMessage 恒可达白名单
            //（守卫点抛出是发射期引用），函数集必含 main + println + 白名单族
            TestHarness.CheckTrue("MIR 函数含 main + println",
                mir.Functions.Any(f => f.Symbol.Canonical == "$main()@.i32")
                && mir.Functions.Any(f => f.Symbol.Canonical.Contains("println")),
                string.Join(", ", mir.Functions.Select(f => f.Symbol.Canonical)));
            TestHarness.CheckTrue("MIR 函数含异常白名单（除零 init）",
                mir.Functions.Any(f => f.Symbol.Canonical
                    == "core::DividedByZeroException$init()@.void"),
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

        // ===== MIR try 展开（MW9a 第 B 棒）=====
        // 直调 MirBuilder.Build：try/catch/finally 子图形状（checked-flag
        // 便携模型；ExcTarget==null 的解析与发射归后续棒）

        private static MirFunction BuildMainMir(string source, string fileName)
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(source);
            var gate = BilGate.Accept(text, fileName);
            TestHarness.CheckTrue(fileName + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var mir = MirBuilder.Build(new MwContext(gate.Module!));
            return mir.Functions.Single(f => f.IsEntrypoint);
        }

        private static MirBlock BlockEnding(MirFunction fn, string suffix) =>
            fn.Blocks.Single(b => b.Id.EndsWith(suffix, StringComparison.Ordinal));

        private static string ExcVarOf(MirFunction fn) =>
            fn.Locals.Single(l => l.Type.Canonical.Contains("Nullable")
                && l.Type.Canonical.Contains("Exception")).Name;

        private static void TestMirTryExpand()
        {
            // ----- try/catch：派发垫形状 + is 链表序 + throw 直译 -----
            var main = BuildMainMir(
                "class MyError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "class OtherError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new MyError()\n" +
                "    } catch (m: MyError) { return 1 }\n" +
                "    catch (o: OtherError) { return 2 }\n" +
                "    return 0\n" +
                "}\n", "trycatch.bil");
            var dispatch = BlockEnding(main, ".dispatch");
            TestHarness.CheckTrue("try/catch：派发垫入口 MirTakePending",
                dispatch.Instructions[0] is MirTakePending take0
                && take0.TargetLocal.StartsWith("$mw.exc.", StringComparison.Ordinal));
            var excLocal = ((MirTakePending)dispatch.Instructions[0]).TargetLocal;
            TestHarness.CheckTrue("try/catch：$mw.exc.N 注册为 core::Exception 局部",
                main.FindLocal(excLocal).Type.Canonical == "core::Exception");
            TestHarness.CheckTrue("try/catch：派发垫首条 is 目标表项 0（MyError）",
                dispatch.Instructions[1] is MirTypeCheck check0
                && check0.Kind == MirTypeCheckKind.Is
                && check0.Value is MirLocalOperand { Name: var v0 } && v0 == excLocal
                && check0.TargetTypeRef == "MyError");
            TestHarness.CheckTrue("try/catch：派发垫终结为条件跳转（命中垫/下一链节）",
                dispatch.Terminator is MirCondBranch);
            var chain1 = BlockEnding(main, ".dispatch.1");
            TestHarness.CheckTrue("try/catch：链节 1 的 is 目标表项 1（OtherError，表序）",
                chain1.Instructions[0] is MirTypeCheck check1
                && check1.TargetTypeRef == "OtherError");
            var excVar = ExcVarOf(main);
            var catchPre0 = BlockEnding(main, ".catchpre.0");
            TestHarness.CheckTrue("try/catch：命中前置垫写 EXC_VAR=$mw.exc.N 后进 catch 块",
                catchPre0.Instructions[0] is MirCopyLocal pre0
                && pre0.Source is MirLocalOperand { Name: var s0 } && s0 == excLocal
                && pre0.Target == excVar
                && catchPre0.Terminator is MirBranch { Target: "try0-catch0" });
            var throwBlock = main.Blocks.First(
                b => b.Instructions.OfType<MirThrow>().Any());
            var bodyThrow = throwBlock.Instructions.OfType<MirThrow>().Single();
            TestHarness.CheckTrue("throw 直译：ExcTarget=本 try 派发垫（对象身份）",
                ReferenceEquals(bodyThrow.ExcTarget, dispatch));
            TestHarness.CheckTrue("throw 直译：块终结跳派发垫",
                throwBlock.Terminator is MirBranch br0 && br0.Target == dispatch.Id);
            // 无 finally：未命中直接 MirThrow 外层（无外层 = 传播出函数）
            var miss = BlockEnding(main, ".miss");
            TestHarness.CheckTrue("try/catch 无 finally：未命中块 MirThrow",
                miss.Instructions.OfType<MirThrow>().Count() == 1);
            var missThrow = miss.Instructions.OfType<MirThrow>().Single();
            TestHarness.CheckTrue("try/catch 无 finally：未命中 MirThrow 无外层（null）",
                missThrow.ExcTarget == null
                && missThrow.Exception is MirLocalOperand { Name: var m0 } && m0 == excLocal);
            TestHarness.CheckTrue("try/catch 无 finally：未命中块 MirRetThrow 收尾",
                miss.Terminator is MirRetThrow);

            // ----- try/finally：单块双前置垫 + 路由器 + 逃逸垫 -----
            main = BuildMainMir(
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        log()\n" +
                "    } finally(e) {\n" +
                "        log()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n", "tryfinally.bil");
            excVar = ExcVarOf(main);
            var finPreNormal = BlockEnding(main, ".fin.pre.normal");
            TestHarness.CheckTrue("try/finally：正常前置垫首指令写 EXC_VAR=null",
                finPreNormal.Instructions[0] is MirLoadResource nullLoad
                && nullLoad.Resource is BilNullResource
                && nullLoad.Target == excVar);
            TestHarness.CheckTrue("try/finally：正常前置垫写 comp=0 后进 finally 单块",
                finPreNormal.Instructions[1] is MirLoadResource compLoad0
                && compLoad0.Resource is BilScalarResource { LiteralText: "0" }
                && compLoad0.Target.StartsWith("$mw.comp.", StringComparison.Ordinal)
                && finPreNormal.Terminator is MirBranch { Target: "try0-finally" });
            var finPreExc = BlockEnding(main, ".fin.pre.exc");
            TestHarness.CheckTrue("try/finally：异常前置垫写 EXC_VAR=异常对象",
                finPreExc.Instructions[0] is MirCopyLocal excCopy
                && excCopy.Source is MirLocalOperand { Name: var s1 }
                && s1.StartsWith("$mw.exc.", StringComparison.Ordinal)
                && excCopy.Target == excVar
                && finPreExc.Terminator is MirBranch { Target: "try0-finally" });
            var esc = BlockEnding(main, ".esc");
            TestHarness.CheckTrue("try/finally：逃逸垫 take 后汇入异常前置垫",
                esc.Instructions.Count == 1
                && esc.Instructions[0] is MirTakePending
                && esc.Terminator is MirBranch brEsc && brEsc.Target == finPreExc.Id);
            var router = BlockEnding(main, ".route");
            TestHarness.CheckTrue("try/finally：路由器 MirSwitch（default=after-try）",
                router.Terminator is MirSwitch sw0
                && sw0.Selector is MirLocalOperand { Name: var sel0 }
                && sel0.StartsWith("$mw.comp.", StringComparison.Ordinal)
                && sw0.DefaultTarget.EndsWith(".end", StringComparison.Ordinal));
            var rethrow = BlockEnding(main, ".rethrow");
            TestHarness.CheckTrue("try/finally：路由器 throw 分支 = 重抛块（无外层→出厂）",
                ((MirSwitch)router.Terminator).ItemTargets.Contains(rethrow.Id)
                && rethrow.Instructions[0] is MirThrow { ExcTarget: null }
                && rethrow.Terminator is MirRetThrow);
            var dispatchOnly = BlockEnding(main, ".dispatch");
            TestHarness.CheckTrue("try/finally 无 catch：派发垫 take 后直落异常前置垫",
                dispatchOnly.Instructions.Count == 1
                && dispatchOnly.Instructions[0] is MirTakePending
                && dispatchOnly.Terminator is MirBranch brD && brD.Target == finPreExc.Id);
            var tryBody = main.Blocks.Single(b => b.Id == "try0-body");
            TestHarness.CheckTrue("try/finally：try 体内调用 ExcTarget=派发垫",
                tryBody.Instructions.OfType<MirCall>().Any()
                && tryBody.Instructions.OfType<MirCall>().All(
                    c => c.ExcTarget == dispatchOnly));
            var finBody = main.Blocks.Single(b => b.Id == "try0-finally");
            TestHarness.CheckTrue("try/finally：finally 体内调用 ExcTarget=外层（此处 null）",
                finBody.Instructions.OfType<MirCall>().Any()
                && finBody.Instructions.OfType<MirCall>().All(c => c.ExcTarget == null));

            // ----- return 穿 finally：路由器 ret 分支 -----
            main = BuildMainMir(
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } finally(e) {\n" +
                "        log()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n", "tryret.bil");
            TestHarness.CheckTrue("return 穿 finally：$mw.retv 合成局部（i32）",
                main.Locals.Any(l => l.Name == "$mw.retv" && l.Type.Key == "i32"));
            var retBody = main.Blocks.Single(b => b.Id == "try0-body");
            TestHarness.CheckTrue("return 穿 finally：值先存 $mw.retv 再跳 ret 前置垫",
                retBody.Instructions.Last() is MirCopyLocal retvStore
                && retvStore.Target == "$mw.retv"
                && retBody.Terminator is MirBranch brR
                && brR.Target.Contains(".fin.pre.ret"));
            var retPad = main.Blocks.Single(b => b.Id == ((MirBranch)retBody.Terminator).Target);
            TestHarness.CheckTrue("return 穿 finally：ret 前置垫写 null + comp",
                retPad.Instructions[0] is MirLoadResource { Resource: BilNullResource }
                && retPad.Instructions[1] is MirLoadResource
                    { Resource: BilScalarResource { LiteralText: not "0" } }
                && retPad.Terminator is MirBranch { Target: "try0-finally" });
            var retBlock = BlockEnding(main, ".ret");
            TestHarness.CheckTrue("return 穿 finally：ret 出口块 MirRet($mw.retv)",
                retBlock.Terminator is MirRet
                {
                    Value: MirLocalOperand { Name: "$mw.retv" }
                });
            router = BlockEnding(main, ".route");
            TestHarness.CheckTrue("return 穿 finally：路由器含 ret 分支",
                ((MirSwitch)router.Terminator).ItemTargets.Contains(retBlock.Id));

            // ----- break 穿 finally：路由器分支直落外层 loop 出口 -----
            main = BuildMainMir(
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    while (x < 3) {\n" +
                "        try {\n" +
                "            break\n" +
                "        } finally(e) {\n" +
                "            log()\n" +
                "        }\n" +
                "        x = x + 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n", "trybreak.bil");
            TestHarness.CheckTrue("break 穿 finally：brk 前置垫存在",
                main.Blocks.Any(b => b.Id.Contains(".fin.pre.brk")));
            router = BlockEnding(main, ".route");
            TestHarness.CheckTrue("break 穿 finally：路由器分支落外层 loop 出口",
                ((MirSwitch)router.Terminator).ItemTargets.Any(
                    t => t.StartsWith("mw.loop.end.", StringComparison.Ordinal)));

            // ----- finally 内 throw：直解析覆盖（不经路由器）-----
            main = BuildMainMir(
                "class MyError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        log()\n" +
                "    } finally(e) {\n" +
                "        throw new MyError()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n", "tryfinthrow.bil");
            var finThrowBlock = main.Blocks.Single(b => b.Id == "try0-finally");
            var finThrow = finThrowBlock.Instructions.OfType<MirThrow>().Single();
            TestHarness.CheckTrue("finally 内 throw：直解析（ExcTarget=外层，此处 null）",
                finThrow.ExcTarget == null);
            TestHarness.CheckTrue("finally 内 throw：MirRetThrow 收尾（不经路由器）",
                finThrowBlock.Terminator is MirRetThrow);

            // ----- 嵌套 try：内层未命中进外层派发垫 -----
            main = BuildMainMir(
                "class MyError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "class OtherError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new MyError()\n" +
                "        } catch (o: OtherError) {\n" +
                "            log()\n" +
                "        }\n" +
                "    } catch (m: MyError) {\n" +
                "        log()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n", "trynested.bil");
            var dispatches = main.Blocks
                .Where(b => b.Id.EndsWith(".dispatch", StringComparison.Ordinal))
                .OrderBy(b => b.Id, StringComparer.Ordinal)
                .ToList();
            TestHarness.CheckTrue("嵌套 try：两层派发垫", dispatches.Count == 2);
            var outerDispatch = dispatches[0];
            // 内层未命中 → 外层派发垫；外层未命中 → 出厂（ExcTarget null）
            var missBlocks = main.Blocks
                .Where(b => b.Id.EndsWith(".miss", StringComparison.Ordinal)).ToList();
            TestHarness.CheckTrue("嵌套 try：两层各有未命中块", missBlocks.Count == 2);
            var innerMiss = missBlocks.Single(
                b => b.Instructions.OfType<MirThrow>().Single().ExcTarget != null);
            var outerMiss = missBlocks.Single(
                b => b.Instructions.OfType<MirThrow>().Single().ExcTarget == null);
            TestHarness.CheckTrue("嵌套 try：外层未命中 MirThrow 出厂（MirRetThrow）",
                outerMiss.Terminator is MirRetThrow);
            var innerMissThrow = innerMiss.Instructions.OfType<MirThrow>().Single();
            TestHarness.CheckTrue("嵌套 try：内层未命中 MirThrow→外层派发垫（对象身份）",
                ReferenceEquals(innerMissThrow.ExcTarget, outerDispatch)
                && innerMiss.Terminator is MirBranch brN && brN.Target == outerDispatch.Id);
            var innerCatch = main.Blocks.Single(b => b.Id == "try1-catch0");
            TestHarness.CheckTrue("嵌套 try：内层 catch 体内调用 ExcTarget=外层派发垫",
                innerCatch.Instructions.OfType<MirCall>().Any()
                && innerCatch.Instructions.OfType<MirCall>().All(
                    c => c.ExcTarget == outerDispatch));
            var outerCatch = main.Blocks.Single(b => b.Id == "try0-catch0");
            TestHarness.CheckTrue("嵌套 try：外层 catch 体内调用 ExcTarget=null（出厂）",
                outerCatch.Instructions.OfType<MirCall>().Any()
                && outerCatch.Instructions.OfType<MirCall>().All(c => c.ExcTarget == null));
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
            // MW9a 起发射要求 ExcTarget 已解析（RcInjection 传播垫）：
            // 含 Rigi 直接调用（Console.println 包装 fn）的用例走完整管线
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            // null = 胖引用双段零（RUNTIME §3：Nullable 是 Object 子类）
            TestHarness.CheckTrue("null 资源 → 胖引用零常量",
                ll.Contains("{ i64, i64 } zeroinitializer"), ll);
            // nullable == → 胖引用双段 extractvalue 各自 icmp 取与
            TestHarness.CheckTrue("nullable == → 双段恒等比较",
                ll.Contains("extractvalue { i64, i64 }"), ll);
        }

        // ===== 除零 guard 发射（MW9b-G：抛 DividedByZeroException）=====

        private static void TestDivGuardEmission()
        {
            // 真实前端全管线路径：i32 除法 → divisor==0 条件分支 →
            // rigi_alloc + init() + rigi_exc_raise + br 传播垫；有符号窄
            // 宽度 MIN/-1 回绕的取负选择
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
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            TestHarness.CheckTrue("除零 abort 面已退场",
                !ll.Contains("rigi_abort_divided_by_zero"), ll);
            TestHarness.CheckTrue("guard 构造 DividedByZeroException",
                ll.Contains("call ptr @rigi_alloc(")
                && ll.Contains("DividedByZeroException"), ll);
            TestHarness.CheckTrue("guard 调零参 init",
                ll.Contains("DividedByZeroException$init()"), ll);
            TestHarness.CheckTrue("guard 抛异常走 rigi_exc_raise",
                ll.Contains("call void @rigi_exc_raise(ptr"), ll);
            TestHarness.CheckTrue("有符号 MIN/-1 回绕取负选择",
                ll.Contains("sdiv.wrap"), ll);
            TestHarness.CheckTrue("异常边指向传播垫",
                ll.Contains("mw.propagate"), ll);
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
            TestHarness.CheckTrue("Point vtable 槽 0 为 init 分发器（一元、无 iMap）",
                point.VTableSlots.Count == 1
                && point.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && point.IMap.Count == 0);

            // class：对象头 16 起排、vtable 本类槽
            var animal = layout.Find("Animal");
            TestHarness.CheckTrue("Animal 已布局", animal != null);
            TestHarness.CheckTrue("Animal 字段偏移", FieldOf(animal!, "#legs@")!.Offset == 16);
            TestHarness.CheckTrue("Animal 尺寸/对齐",
                animal!.Size == 32 && animal.Alignment == 16);
            TestHarness.CheckTrue("Animal vtable 本类槽",
                animal.VTableSlots.Count == 3
                && animal.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && animal.VTableSlots[1] == "Animal$speak()@.string"
                && animal.VTableSlots[2] == "Animal$legCount()@.i32");

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
                dog.VTableSlots.Count == 5
                && dog.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && dog.VTableSlots[1] == "Dog$speak()@.string"
                && dog.VTableSlots[2] == "Animal$legCount()@.i32"
                && dog.VTableSlots[3] == "Dog$name()@.string"
                && dog.VTableSlots[4] == "Dog$name()@.string");
            TestHarness.CheckTrue("Dog iMap 段 base offset",
                dog.IMap.Count == 1 && dog.IMap[0].InterfaceType == "Named"
                && dog.IMap[0].BaseOffset == 4);

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
            TestHarness.CheckTrue("enum 不挂 init 分发器槽 0",
                direction.VTableSlots.Count == 0);

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
            //（头 16 + i32@16 + 引用槽@32）、vTableSize 2（槽 0 分发器 + get）
            TestHarness.CheckTrue("TypeSheet 全局锚点",
                ll.Contains("@typesheet.Node = internal constant { ptr, ptr, i32, i32, i32, ptr, i32, ptr, i32, ptr } " +
                    "{ ptr @typeinfo.Node, ptr null, i32 48, i32 0, i32 2, ptr @typesheet.vtable.Node, " +
                    "i32 0, ptr null, i32 1, ptr @typesheet.refmap.Node }"), ll);
            TestHarness.CheckTrue("TypeInfo 回指 sheet",
                ll.Contains("@typeinfo.Node =") && ll.Contains("ptr @typesheet.Node"), ll);
            TestHarness.CheckTrue("TypeInfo typeInfoId 非 null",
                !ll.Contains("@typesheet.Node = internal constant { ptr, ptr, i32, i32, i32, ptr, i32, ptr, i32, ptr } " +
                    "{ ptr null,"), ll);
            // 槽 0 = 分发器；get 不可达仍为 null
            TestHarness.CheckTrue("vtable 全局锚点",
                ll.Contains("@typesheet.vtable.Node = internal constant [2 x ptr] " +
                    "[ptr @mw.init.dispatch.Node, ptr null]"), ll);
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
            var taggedPlan = context.Layout!.Find("Tagged");
            TestHarness.CheckTrue("Tagged Entity 隐藏存储 §5.3",
                taggedPlan != null && taggedPlan.Fields.Any(f =>
                    f.Symbol.Contains("#.wrapper.") && f.Symbol.Contains("Mark@")));
            var markPlan = context.Layout.Find("Mark");
            TestHarness.CheckTrue("Mark wrapper 为内联实例布局",
                markPlan != null
                && markPlan.Kind == RigiCompiler.Middleware.Layout.TypeLayoutKind.Wrapper);

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

        // ===== wrapper 隐藏槽内存路径（MW10）：get/set/new.wrapper.* =====

        private static void TestWrapperStorageEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: i32\n" +
                "    pub init() { level = 7 }\n" +
                "    pub func dump(): i32 { return level }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 3 }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func readEntity(s: Service): i32 {\n" +
                "    return s:Logged.level\n" +
                "}\n" +
                "pub func writeEntity(s: Service) {\n" +
                "    s:Logged.level = 42\n" +
                "}\n" +
                "pub func callEntity(s: Service): i32 {\n" +
                "    return s:Logged.dump()\n" +
                "}\n" +
                "pub func readField(hero: Hero): i32 {\n" +
                "    return hero.hp:Clamped.min\n" +
                "}\n" +
                "pub func writeField(hero: Hero) {\n" +
                "    hero.hp:Clamped.min = 9\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    writeEntity(s)\n" +
                "    var h = new Hero()\n" +
                "    writeField(h)\n" +
                "    var a = readEntity(s)\n" +
                "    var b = readField(h)\n" +
                "    var c = callEntity(s)\n" +
                "    return a\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.store.bil");
            TestHarness.CheckTrue("wrapper 存储用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var allInsts = context.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 get.wrapper", allInsts.OfType<MirGetWrapper>().Any());
            TestHarness.CheckTrue("BIL 含 get.wrapper.field",
                text.Contains("get.wrapper.field"), text);
            TestHarness.CheckTrue("MIR 含 get.wrapper.field",
                allInsts.OfType<MirGetWrapperField>().Any());
            TestHarness.CheckTrue("MIR 含 set.wrapper.field",
                allInsts.OfType<MirSetWrapperField>().Any());
            TestHarness.CheckTrue("MIR 含 new.wrapper.entity",
                allInsts.OfType<MirNewWrapper>().Any(n => n.Kind == MirWrapperInstallKind.Entity));
            TestHarness.CheckTrue("MIR 含 new.wrapper.field",
                allInsts.OfType<MirNewWrapper>().Any(n => n.Kind == MirWrapperInstallKind.Field));
            var index = WrapperApplicationIndex.Build(context.Symbols);
            TestHarness.CheckTrue("应用索引 Entity(Service)=Logged",
                index.EntityWrappers("Service").Contains("Logged"));
            TestHarness.CheckTrue("应用索引字段-Value(Hero#hp)=Clamped",
                index.FieldWrappers("Hero#hp@.i32").Contains("Clamped"));
            TestHarness.CheckTrue("Logged init 可达",
                context.Mir.Functions.Any(f => f.Symbol.Canonical.Contains("Logged$init")));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 模块含 Logged init",
                ll.Contains("Logged$init") || ll.Contains("Logged$init("), ll);
        }

        // ===== specific Entity proxy 烘焙（MW10）：get.self + inner 链接 =====

        private static void TestProxyBakingEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): i32 {\n" +
                "        var host = self\n" +
                "        return (inner(arg) + 1)\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): i32 { return arg }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.doSomething(5)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.proxy.bil");
            TestHarness.CheckTrue("proxy 烘焙用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());
            TestHarness.CheckTrue("特化体含 get.self",
                allInsts.OfType<MirGetSelf>().Any());
            TestHarness.CheckTrue("合成 .wrapped. 原始体",
                functions.Any(f => f.Symbol.Canonical.Contains(
                    ProxyBakeSupport.WrappedInfix)));
            TestHarness.CheckTrue("合成 .bake. 特化体",
                functions.Any(f => f.Symbol.Canonical.Contains(ProxyBakeSupport.BakeInfix)));
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical.Contains("Service$doSomething")
                && !f.Symbol.Canonical.Contains(ProxyBakeSupport.WrappedInfix)
                && !f.Symbol.Canonical.Contains(ProxyBakeSupport.BakeInfix));
            TestHarness.CheckTrue("原名槽是 trampoline（get.wrapper.addr + call）",
                trampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperAddr>()
                    .Any()
                && trampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().Any());

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含原名 doSomething",
                ll.Contains("Service$doSomething"), ll);
            TestHarness.CheckTrue("LLVM 含 .wrapped. 原始体",
                ll.Contains(".wrapped."), ll);
            TestHarness.CheckTrue("LLVM 含 .bake. 特化体",
                ll.Contains(".bake."), ll);
        }

        // ===== proxy 环 receiver 取址形态（MW10 刀3c §14.5 原地访问） =====

        // 环 receiver 一律 get.wrapper[.field].addr（宿主隐藏槽就地地址，
        // 非值拷贝）；别名目标局部不进 acquire/release 序列（RichValue
        // wrapper 带 String 字段强制 managed 分类以覆盖该判别）；place
        // 路径的 get.wrapper/get.wrapper.field 值拷贝语义不回归
        private static void TestProxyRingReceiverAddrEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub var tag: String\n" +
                "    pub init() {\n" +
                "        calls = 0\n" +
                "        tag = \"t\"\n" +
                "    }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Tag {\n" +
                "    pub var label: String\n" +
                "    pub init() { label = \"L\" }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Tag\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(1)\n" +
                "    var b = s.fetch(1)\n" +
                "    var h = new Hero()\n" +
                "    h.name = \"b\"\n" +
                "    var n = h.name\n" +
                "    var e = s:Counting.calls\n" +
                "    var z = h.name:Tag.label\n" +
                "    return (((a + b) + e))\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.addr.bil");
            TestHarness.CheckTrue("环 receiver 取址用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();

            // trampoline 首环 receiver：get.wrapper.addr（无拷贝形态残留）
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical.Contains("Service$fetch")
                && !f.Symbol.Canonical.Contains(ProxyBakeSupport.WrappedInfix)
                && !f.Symbol.Canonical.Contains(ProxyBakeSupport.BakeInfix));
            var trampAddr = trampoline.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirGetWrapperAddr>().ToList();
            TestHarness.CheckTrue("trampoline receiver 为 get.wrapper.addr",
                trampAddr.Any(g => g.WrapperType == "Counting")
                && !trampoline.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapper>().Any());
            // RichValue wrapper（含 String 字段）：别名目标不进 acquire/release
            var trampRc = trampoline.Blocks.SelectMany(b => b.Instructions).ToList();
            foreach (var addr in trampAddr)
            {
                TestHarness.CheckTrue("trampoline 别名目标无 release/acquire（" + addr.Target + "）",
                    !trampRc.OfType<MirReleaseSlot>().Any(r => r.Local == addr.Target)
                    && !trampRc.OfType<MirAcquireSlot>().Any(r => r.Local == addr.Target));
            }

            // Value 链使用点 receiver：get.wrapper.field.addr；别名目标同样免 release
            var main = functions.Single(f => f.Symbol.Canonical.Contains("$main"));
            var mainInsts = main.Blocks.SelectMany(b => b.Instructions).ToList();
            var fieldAddr = mainInsts.OfType<MirGetWrapperFieldAddr>()
                .Where(g => g.WrapperType == "Tag").ToList();
            TestHarness.CheckTrue("Value 链使用点 receiver 为 get.wrapper.field.addr（get+set 各一）",
                fieldAddr.Count == 2);
            foreach (var addr in fieldAddr)
            {
                TestHarness.CheckTrue("使用点别名目标无 release（" + addr.Target + "）",
                    !mainInsts.OfType<MirReleaseSlot>().Any(r => r.Local == addr.Target));
            }

            // place 路径值拷贝语义不回归：s:Counting.calls → get.wrapper、
            // h.name:Tag.label → get.wrapper.field（拷贝形态仍在）
            TestHarness.CheckTrue("place Entity wrapper 读保持 get.wrapper 拷贝",
                allInsts.OfType<MirGetWrapper>().Any(g => g.WrapperType == "Counting"));
            TestHarness.CheckTrue("place 字段-Value wrapper 读保持 get.wrapper.field 拷贝",
                allInsts.OfType<MirGetWrapperField>().Any(g => g.WrapperType == "Tag"));

            // .ll 形状：trampoline 体内无 wrapper 值拷贝 acquire（取址不产生
            // 值拷贝，旧形态此处必有 rigi_value_acquire）
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            var marker = ll.IndexOf("Service$fetch", System.StringComparison.Ordinal);
            TestHarness.CheckTrue("LLVM 含原名 fetch", marker >= 0, ll);
            var tail = ll.IndexOf("\ndefine ", marker, System.StringComparison.Ordinal);
            var slice = tail < 0 ? ll.Substring(marker) : ll.Substring(marker, tail - marker);
            TestHarness.CheckTrue("trampoline 体无 rigi_value_acquire（无值拷贝）",
                !slice.Contains("rigi_value_acquire"), slice);
        }

        // ===== 字段-Value wrapper get/set 链烘焙（MW10 刀2） =====

        // 单字段双 wrapper（@A 外 @B 内）：set 链环间 MirCall 链接、get 链
        // 内→外调用序、init 写豁免、cell getValue/setValue 壳化成链
        private static void TestValueProxyBakingEmission()
        {
            const string wrappers =
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n";
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                wrappers +
                "pub class Hero {\n" +
                "    @A\n" +
                "    @B\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 0\n" +
                "    x = 1\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 5\n" +
                "    var a = x\n" +
                "    var b = h.hp\n" +
                "    return ((a + b))\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.value.bil");
            TestHarness.CheckTrue("Value 链用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("Value 链烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // set 链：环符号存在 + 环间 MirCall 链接（A.set→B.set→终态）
            var aSet = Fn("A$.bake.Hero$hp$.set");
            var bSet = Fn("B$.bake.Hero$hp$.set");
            var terminalSet = Fn("Hero$hp$.wrapped.set");
            TestHarness.CheckTrue("A.set 环内经 get.wrapper.field.addr 取 B 槽地址并 MirCall B.set",
                aSet.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperFieldAddr>()
                    .Any(g => g.WrapperType == "B")
                && calls(aSet).Any(c => c.Target.Canonical == bSet.Symbol.Canonical));
            TestHarness.CheckTrue("B.set 环 MirCall 链末终态",
                calls(bSet).Any(c => c.Target.Canonical == terminalSet.Symbol.Canonical));
            TestHarness.CheckTrue("hp 无用户 setter：终态裸写 backing",
                terminalSet.Blocks.SelectMany(b => b.Instructions).OfType<MirSetField>()
                    .Any(s => s.FieldSymbol == "Hero#hp@.i32"));

            // get 链：使用点（main 读 h.hp）环序 = 终态 → B（内）→ A（外）
            var main = Fn("$main");
            var mainInsts = main.Blocks.SelectMany(b => b.Instructions).ToList();
            var getSeq = mainInsts.Where(inst =>
                inst is MirCall c && (c.Target.Canonical == "Hero$hp$.wrapped.get()@core::i32"
                    || c.Target.Canonical.Contains("$.bake.Hero$hp$.get"))).ToList();
            TestHarness.CheckTrue("get 链使用点三步：终态 → B.get → A.get",
                getSeq.Count == 3
                && getSeq[0] is MirCall c0
                    && c0.Target.Canonical == "Hero$hp$.wrapped.get()@core::i32"
                && getSeq[1] is MirCall c1
                    && c1.Target.Canonical.StartsWith("B$.bake.Hero$hp$.get")
                && getSeq[2] is MirCall c2
                    && c2.Target.Canonical.StartsWith("A$.bake.Hero$hp$.get"),
                string.Join(" | ", getSeq));

            // get 环形参表：隐藏 typeid 形参已剔除（.this + value 二参）
            TestHarness.CheckTrue("get 环形参剔除 .generic.TValue",
                Fn("A$.bake.Hero$hp$.get").Parameters.Count == 2);

            // init 写豁免：..init.field.hp 内 MirSetField 未被改写为链
            var initField = Fn("Hero$..init.field.hp");
            TestHarness.CheckTrue("init 族写豁免（MirSetField 保持裸写）",
                initField.Blocks.SelectMany(b => b.Instructions).OfType<MirSetField>()
                    .Any(s => s.FieldSymbol == "Hero#hp@.i32")
                && !calls(initField).Any(c =>
                    c.Target.Canonical.Contains(ProxyBakeSupport.BakeInfix)));

            // cell：wrapped 局部的 getValue/setValue 壳化成链，使用点
            // invoke core::Cell$getValue/setValue 不动
            var cellGet = Fn("$getValue");
            var cellSet = Fn("$setValue");
            TestHarness.CheckTrue("cell getValue 壳：终态调用 + 环调用",
                calls(cellGet).Any(c => c.Target.Canonical.Contains("$value$.wrapped.get"))
                && calls(cellGet).Count(c =>
                    c.Target.Canonical.Contains("$.bake.")) == 2);
            TestHarness.CheckTrue("cell setValue 壳：get.wrapper.field.addr + 最外环（A）调用",
                cellSet.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperFieldAddr>()
                    .Any(g => g.WrapperType == "A")
                && calls(cellSet).Any(c =>
                    c.Target.Canonical.StartsWith("A$.bake.")));
            TestHarness.CheckTrue("cell 使用点 invoke 不动",
                calls(main).Any(c => c.Target.Canonical.StartsWith("core::Cell$getValue"))
                && calls(main).Any(c => c.Target.Canonical.StartsWith("core::Cell$setValue")));
            // cell 终态 = 原访问器体（裸读写 backing）
            var cellTerminalSet = functions.Single(f =>
                f.Symbol.Canonical.Contains("$value$.wrapped.set"));
            TestHarness.CheckTrue("cell set 终态裸写 backing",
                cellTerminalSet.Blocks.SelectMany(b => b.Instructions).OfType<MirSetField>()
                    .Any(s => s.FieldSymbol.Contains("#value@.i32")));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 Value 链烘焙环",
                ll.Contains(".bake.Hero$hp"), ll);
        }

        // ===== Entity 字段 get/set proxy 链烘焙（MW10 刀3b） =====

        // specific get/set 链形状（环符号、环序、终态、零 MirInnerCall、
        // Entity 隐藏槽 get.wrapper）+ wildcard get.* 的 symbol 资源 =
        // 字段 canonical + W 短路 E + init 读命中/写豁免 + 无 proxy 层
        // 透明跳过与访问器兜底
        private static void TestEntityFieldProxyBakingEmission()
        {
            const string valueWrapper =
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n";
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                valueWrapper +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.name\\<TField>(value: TField) {\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        return value\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Empty {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() {\n" +
                "        name = \"a\"\n" +
                "        var c = name\n" +
                "    }\n" +
                "}\n" +
                "@Audit\n" +
                "pub class S2 {\n" +
                "    pub var title: String\n" +
                "    pub init() { title = \"t\" }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Dual {\n" +
                "    @A\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init()\n" +
                "}\n" +
                "@Empty\n" +
                "pub class Plain {\n" +
                "    pub var raw: i32 = 0\n" +
                "    pub var computed: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { computed = value }\n" +
                "    } = 0\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    var n = s.name\n" +
                "    var s2 = new S2()\n" +
                "    var t = s2.title\n" +
                "    var d = new Dual()\n" +
                "    d.hp = 5\n" +
                "    var h = d.hp\n" +
                "    var p = new Plain()\n" +
                "    p.computed = 3\n" +
                "    var c2 = p.computed\n" +
                "    p.raw = 4\n" +
                "    var r2 = p.raw\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.entityfield.bil");
            TestHarness.CheckTrue("Entity 字段链用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("Entity 字段链烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // specific set 链：环符号 + 终态 + 环内经 get.self 宿主裸写
            var cSet = Fn("Counting$.bake.Service$name$.set");
            var terminalSet = Fn("Service$name$.wrapped.set");
            TestHarness.CheckTrue("Counting.set 环 MirCall 链末终态",
                calls(cSet).Any(c => c.Target.Canonical == terminalSet.Symbol.Canonical));
            TestHarness.CheckTrue("name 无用户 setter：终态裸写 backing",
                terminalSet.Blocks.SelectMany(b => b.Instructions).OfType<MirSetField>()
                    .Any(s => s.FieldSymbol == "Service#name@.string"));

            // specific get 使用点（main 读 s.name）：终态 → get.wrapper
            //（Entity 隐藏槽，非 get.wrapper.field）→ Counting.get 环
            var main = Fn("$main");
            var mainInsts = main.Blocks.SelectMany(b => b.Instructions).ToList();
            var getSeq = mainInsts.Where(inst =>
                inst is MirCall c && (c.Target.Canonical == "Service$name$.wrapped.get()@core::String"
                    || c.Target.Canonical.StartsWith("Counting$.bake.Service$name$.get"))).ToList();
            TestHarness.CheckTrue("Entity get 使用点两步：终态 → Counting.get 环",
                getSeq.Count == 2
                && getSeq[0] is MirCall c0
                    && c0.Target.Canonical == "Service$name$.wrapped.get()@core::String"
                && getSeq[1] is MirCall c1
                    && c1.Target.Canonical.StartsWith("Counting$.bake.Service$name$.get"),
                string.Join(" | ", getSeq));
            TestHarness.CheckTrue("Entity 环 receiver 经 get.wrapper.addr（非字段槽）",
                mainInsts.OfType<MirGetWrapperAddr>().Any(g => g.WrapperType == "Counting")
                && !mainInsts.OfType<MirGetWrapperFieldAddr>()
                    .Any(g => g.WrapperType == "Counting"));

            // specific set 使用点（main 写 s.name）：get.wrapper.addr + 最外环调用
            TestHarness.CheckTrue("Entity set 使用点：get.wrapper.addr + Counting.set 环调用",
                mainInsts.OfType<MirGetWrapperAddr>().Any(g => g.WrapperType == "Counting")
                && calls(main).Any(c =>
                    c.Target.Canonical.StartsWith("Counting$.bake.Service$name$.set")));

            // wildcard get 环：symbol 形参剔除（.this + value 二参）且
            // symbol 资源 = 字段 canonical 全串
            var aGet = Fn("Audit$.bake.S2$title$.get");
            TestHarness.CheckTrue("wildcard get 环形参剔除 symbol（二参）",
                aGet.Parameters.Count == 2);
            TestHarness.CheckTrue("wildcard get 环 symbol 资源 = 字段 canonical",
                aGet.Blocks.SelectMany(b => b.Instructions).OfType<MirLoadResource>().Any()
                && context.Module.Resources.OfType<BilScalarResource>()
                    .Any(r => r.LiteralText.Contains("S2#title@.string")));

            // W 短路 E：Dual.hp 自身 wrapped → 只跑 Value 链，无 Entity 环
            TestHarness.CheckTrue("W 短路 E：hp 走 Value 链且无 Entity 环",
                functions.Any(f => f.Symbol.Canonical.Contains("A$.bake.Dual$hp"))
                && !functions.Any(f =>
                    f.Symbol.Canonical.Contains("Counting$.bake.Dual$hp")));

            // init 读命中/写豁免：Service$init 内 name 裸写（无 set 环调用）
            // 且读走 get 链（终态调用存在）
            var init = functions.Single(f => f.Symbol.Canonical == "Service$init()@.void");
            var initInsts = init.Blocks.SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("init 写豁免（MirSetField 保持裸写）",
                initInsts.OfType<MirSetField>().Any(s => s.FieldSymbol == "Service#name@.string")
                && !calls(init).Any(c =>
                    c.Target.Canonical.Contains("$.bake.Service$name$.set")));
            TestHarness.CheckTrue("init 读命中 get 链（终态 + 环调用）",
                calls(init).Any(c => c.Target.Canonical == "Service$name$.wrapped.get()@core::String")
                && calls(init).Any(c =>
                    c.Target.Canonical.StartsWith("Counting$.bake.Service$name$.get")));

            // 无 proxy 层：有用户访问器 → 访问器调用兜底；无 → 保持裸访
            TestHarness.CheckTrue("无 proxy 层读兜底为 getter 调用",
                calls(main).Any(c => c.Target.Canonical == "Plain$.get.computed@.i32"));
            TestHarness.CheckTrue("无 proxy 层写兜底为 setter 调用",
                calls(main).Any(c => c.Target.Canonical == "Plain$.set.computed@.i32"));
            TestHarness.CheckTrue("无 proxy 层无访问器字段保持裸访",
                mainInsts.OfType<MirSetField>().Any(s => s.FieldSymbol == "Plain#raw@.i32")
                && mainInsts.OfType<MirGetField>().Any(g => g.FieldSymbol == "Plain#raw@.i32"));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 Entity 字段链烘焙环",
                ll.Contains(".bake.Service$name"), ll);
        }

        // ===== router get/set 分支（MW10 刀3b） =====

        // 双层宿主（外 A 内 B，均带 .proxy.*/.proxy.get.*/.proxy.set.*）：
        // router(1) 的 if 链覆盖字段访问器符号（S3$.get|set.title@T），
        // 分支自 fromLayer=1 起进字段 Entity 环链——get 分支调 getter
        // 旁路体（$.wrapped..get.）+B 层 get 环，set 分支调 B 层 set 环
        private static void TestEntityFieldRouterBranchEmission()
        {
            const string wrapper =
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n";
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper A {\n" + wrapper + "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper B {\n" + wrapper + "}\n" +
                "@A\n" +
                "@B\n" +
                "pub class S3 {\n" +
                "    pub var title: String {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { }\n" +
                "    } = \"t\"\n" +
                "    pub init()\n" +
                "    pub func ping(): i32 { return 1 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new S3()\n" +
                "    return s.ping()\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.entityrouter.bil");
            TestHarness.CheckTrue("router 字段分支用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;

            var router = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("S3$.mw.router.1("));
            var routerCalls = router.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirCall>().ToList();
            var resources = context.Module.Resources.OfType<BilScalarResource>()
                .Select(r => r.LiteralText).ToList();
            TestHarness.CheckTrue("router 资源含 get/set 访问器符号",
                resources.Any(r => r.Contains("S3$.get.title@.string"))
                && resources.Any(r => r.Contains("S3$.set.title@.string")));
            TestHarness.CheckTrue("router get 分支调 getter 旁路体",
                routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("S3$.wrapped..get.title@")));
            TestHarness.CheckTrue("router get 分支自 fromLayer 起调 B 层 get 环",
                routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("B$.bake.S3$title$.get")));
            TestHarness.CheckTrue("router set 分支自 fromLayer 起调 B 层 set 环",
                routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("B$.bake.S3$title$.set")));
            TestHarness.CheckTrue("router 无 A 层字段环（layer < fromLayer 不进链）",
                !routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("A$.bake.S3$title$")));
            TestHarness.CheckTrue("访问器不落方法成员分支（无 $.wrapped..set.title 直调外环）",
                !routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("A$.bake.S3$.set.title@")
                    || c.Target.Canonical.StartsWith("A$.bake.S3$.get.title@")));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 router 与字段环",
                ll.Contains(".mw.router.1") && ll.Contains(".bake.S3$title"), ll);
        }

        // MW10 遗留④：wildcard set 环 inner 的跨字段重路由分派形状——
        // WA 层 wildcard 环 inner 改写为「symbol == 原字段 canonical 的
        // fast-path（静态链路）+ 否则调分派辅助 H$.mw.srt.1.<名>」双分支；
        // 分派辅助按 canonical 逐字段比对：specific 分支进他字段环变体
        //（$.setr.，终态仍指原字段），miss 落原字段终态；零 MirInnerCall
        private static void TestSetRingInnerRerouteDispatchEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#mp@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.mp\\<TField>(value: TField) { inner(value) }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub var mp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    return e.hp\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.setreroute.bil");
            TestHarness.CheckTrue("set 重路由分派用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("set 重路由烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var callsOf = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // hp 的 WA 层 wildcard 环：fast/slow CFG 分割（恒等 fast-path
            // 调原字段终态，slow 调分派辅助）
            var ring = Fn("WA$.bake.Entity$hp$.set(");
            var ringCalls = callsOf(ring);
            TestHarness.CheckTrue("hp 环含条件分支（fast-path + 分派双分支）",
                ring.Blocks.Any(b => b.Terminator is MirCondBranch));
            TestHarness.CheckTrue("hp 环 fast-path 直调原字段终态",
                ringCalls.Any(c =>
                    c.Target.Canonical.StartsWith("Entity$hp$.wrapped.set(")));
            TestHarness.CheckTrue("hp 环 slow 路径调分派辅助",
                ringCalls.Any(c =>
                    c.Target.Canonical.StartsWith("Entity$.mw.srt.1.hp(")));

            // 分派辅助：specific 分支（mp canonical 比对）进变体环，
            // miss 落原字段终态
            var dispatch = Fn("Entity$.mw.srt.1.hp(symbol:.string,value:.any)@.void");
            var dispatchCalls = callsOf(dispatch);
            var resources = context.Module.Resources.OfType<BilScalarResource>()
                .Select(r => r.LiteralText).ToList();
            TestHarness.CheckTrue("分派辅助资源含 mp 字段 canonical 比对字面量",
                resources.Any(r => r.Contains("Entity#mp@.i32")));
            TestHarness.CheckTrue("分派辅助 specific 分支进 mp 变体环",
                dispatchCalls.Any(c =>
                    c.Target.Canonical.StartsWith("WB$.bake.Entity$mp$.setr.hp.1(")));
            TestHarness.CheckTrue("分派辅助 miss 落原字段终态",
                dispatchCalls.Any(c =>
                    c.Target.Canonical.StartsWith("Entity$hp$.wrapped.set(")));

            // 变体环：inner 续跑无更多环 → 落原字段终态（VM FieldSymbol
            // 恒为原字段同口径）
            var variant = Fn("WB$.bake.Entity$mp$.setr.hp.1(");
            TestHarness.CheckTrue("mp 变体环 inner 落原字段终态",
                callsOf(variant).Any(c =>
                    c.Target.Canonical.StartsWith("Entity$hp$.wrapped.set(")));

            using var module2 = ModuleBuilder.Build(context, context.Mir!);
            var ll2 = module2.PrintToString();
            TestHarness.CheckTrue("LLVM 含分派辅助与变体环",
                ll2.Contains(".mw.srt.1.hp") && ll2.Contains(".setr.hp.1"), ll2);
        }

        // MW10 遗留④形状守恒：剩余层无任何 set proxy 时分派恒落原字段
        // 终态（与恒等等价），环 inner 保持线性链接不引 CFG 分割、不产
        // 分派辅助
        private static void TestSetRingInnerRerouteLinearWhenNoBranches()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    return e.hp\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.setreroute.linear.bil");
            TestHarness.CheckTrue("线性守恒用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            TestHarness.CheckTrue("线性守恒烘焙后无残留 MirInnerCall",
                !functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                    .OfType<MirInnerCall>().Any());
            var ring = functions.Single(f =>
                f.Symbol.Canonical.Contains("Audit$.bake.Entity$hp$.set("));
            TestHarness.CheckTrue("无分派需求时环保持线性（无 CondBranch）",
                !ring.Blocks.Any(b => b.Terminator is MirCondBranch));
            TestHarness.CheckTrue("无分派需求时不产分派辅助 fn",
                !functions.Any(f => f.Symbol.Canonical.Contains("$.mw.srt.")));
        }

        // ===== wildcard Entity proxy 烘焙（MW10 刀3a）：胖值 ABI 链 =====

        // 单层 wildcard 全链形状：trampoline 打包（symbol 资源 + 空 named
        // 包 + unnamed 装箱包）→ baked 环（动态分派块：symbol 字符串比对
        // 命中解包直进 $.wrapped.，miss 调 router）→ router（if 链覆盖全部
        // 可烘焙成员，miss 抛 NoSuchMethodException）
        private static void TestWildcardProxyBakingEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Service$zap(x:.i32)@.i32\") {\n" +
                "            return (99 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "    pub func zap(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.wildcard.bil");
            TestHarness.CheckTrue("wildcard 烘焙用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("wildcard 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var instsOf = new Func<MirFunction, List<MirInst>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).ToList());
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                instsOf(f).OfType<MirCall>().ToList());

            // 环符号：TReturn 代入成员返回类型；ping/zap 各一环 + $.wrapped. 终态
            var pingRing = Fn("Router$.bake.Service$ping(x:.i32)@core::i32");
            var zapRing = Fn("Router$.bake.Service$zap(x:.i32)@core::i32");
            var pingWrapped = Fn("Service$.wrapped.ping");
            TestHarness.CheckTrue("zap 有 $.wrapped. 终态",
                functions.Any(f => f.Symbol.Canonical.Contains("Service$.wrapped.zap")));

            // trampoline：get.wrapper + symbol 资源 + 两个包数组 + 调环
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$ping(x:.i32)@.i32");
            var trampInsts = instsOf(trampoline);
            TestHarness.CheckTrue("wildcard trampoline 打包（get.wrapper.addr + symbol 资源 + 包数组）",
                trampInsts.OfType<MirGetWrapperAddr>().Any(g => g.WrapperType == "Router")
                && trampInsts.OfType<MirLoadResource>().Any()
                && trampInsts.OfType<MirNewArray>().Count() == 2
                && calls(trampoline).Any(c =>
                    c.Target.Canonical == pingRing.Symbol.Canonical));
            TestHarness.CheckTrue("trampoline unnamed 包逐参装箱 .any",
                trampInsts.OfType<MirBoxAny>().Any());

            // 环形参：.generic 三包已擦除/代入（.this + symbol + 两包）
            TestHarness.CheckTrue("wildcard 环形参擦除 .generic 三包",
                pingRing.Parameters.Count == 4
                && !pingRing.Parameters.Any(p => p.Name.StartsWith(".generic.")));

            // 动态分派块：字符串比对 + 双分支；hit 解包直进 $.wrapped.，
            // miss 调 router 并拆回 .any
            TestHarness.CheckTrue("环内含 symbol 字符串比对",
                instsOf(pingRing).OfType<MirBinaryIntrinsic>().Any(b =>
                    b.Op == BilBinaryOp.CmpEq && b.LeftType.IsString));
            TestHarness.CheckTrue("环内含条件分支（hit/miss）",
                pingRing.Blocks.SelectMany(b => new[] { b.Terminator })
                    .OfType<MirCondBranch>().Any());
            TestHarness.CheckTrue("hit 分支解包直进 $.wrapped.（get.array + unbox）",
                instsOf(pingRing).OfType<MirGetArray>().Any()
                && instsOf(pingRing).OfType<MirUnboxAny>().Any()
                && calls(pingRing).Any(c =>
                    c.Target.Canonical == pingWrapped.Symbol.Canonical));
            var router = Fn("Service$.mw.router.1");
            TestHarness.CheckTrue("miss 分支调 router(H, 2 层界=1)",
                calls(pingRing).Any(c => c.Target.Canonical == router.Symbol.Canonical));

            // zap 环：(99 as TReturn) 的 .generic.TReturn 就地物化 getid.type
            TestHarness.CheckTrue("zap 环物化 .generic.TReturn typeid",
                instsOf(zapRing).OfType<MirGetTypeId>().Any(g =>
                    g.TypeRef.Contains("i32") && g.Target == ".generic.TReturn"));

            // router：if 链覆盖 ping/zap 两成员（fromLayer=1 无环 → 直调
            // $.wrapped. 终态），miss 抛 NoSuchMethodException
            TestHarness.CheckTrue("router 分支直调 $.wrapped. 终态（不回调 trampoline）",
                calls(router).Any(c => c.Target.Canonical == pingWrapped.Symbol.Canonical)
                && calls(router).Any(c => c.Target.Canonical.Contains("Service$.wrapped.zap"))
                && !calls(router).Any(c =>
                    c.Target.Canonical == trampoline.Symbol.Canonical));
            TestHarness.CheckTrue("router miss 抛 NoSuchMethodException",
                instsOf(router).OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical == "core::NoSuchMethodException")
                && instsOf(router).OfType<MirThrow>().Any()
                && router.Blocks.Any(b => b.Terminator is MirRetThrow));
            TestHarness.CheckTrue("router 结果装箱 .any（值类型 box）",
                instsOf(router).OfType<MirBoxAny>().Any());

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 wildcard 环", ll.Contains(".bake.Service$ping"), ll);
            TestHarness.CheckTrue("LLVM 含 router", ll.Contains(".mw.router.1"), ll);
            TestHarness.CheckTrue("LLVM 含 $.wrapped. 原始体", ll.Contains(".wrapped."), ll);
        }

        // 遗6：泛型宿主成员经 wildcard 的烘焙形状——trampoline 把方法级
        // typeid 隐藏形参随值实参同装箱进 unnamed 位置包（声明序居值参
        // 前：MirBoxAny 两次）；环特化 TReturn 擦除 .any（泛型占位返回
        // 无 TypeSheet，环 ABI 以胖值承载）；终态 hit 分支从包首解包
        // typeid（MirGetArray + MirUnboxAny 到 .typeid 槽）再解值参，
        // 调 $.wrapped. 原始泛型体；router 直调分支同形（typeid 随包
        // 透传，无独立类型包渠道）
        private static void TestGenericWildcardBakingEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func pick\\<T>(x: T): T { return x }\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.pick\\<i32>(41)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.generic.wildcard.bil");
            TestHarness.CheckTrue("泛型 wildcard 烘焙用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("泛型 wildcard 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var instsOf = new Func<MirFunction, List<MirInst>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).ToList());
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                instsOf(f).OfType<MirCall>().ToList());

            // 环符号：TReturn 擦除 .any（泛型占位返回无 TypeSheet）
            var pickRing = Fn("Router$.bake.Service$pick");
            TestHarness.CheckTrue("泛型成员环返回擦除 .any",
                pickRing.ReturnType.IsAny);
            var pickWrapped = Fn("Service$.wrapped.pick");

            // trampoline：原名槽保留原签名（含 .generic.T 隐藏形参），
            // 打包 = symbol 资源 + 空 named 包 + unnamed 装箱包——
            // typeid 经 MirBoxAny 装箱居包首，占位值参是胖槽透转
            //（装箱在调用点边界已完成；VM BoxConcreteArgs 全量 VmAny
            // 包装的同构形态），unnamed 包恰 2 元素
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$pick(x:.generic<$.generic.T>)@.generic<$.generic.T>");
            TestHarness.CheckTrue("泛型 trampoline 保留 .generic.T 隐藏形参",
                trampoline.Parameters.Any(p => p.Name == ".generic.T"));
            var trampInsts = instsOf(trampoline);
            TestHarness.CheckTrue("泛型 trampoline 打包（typeid 装箱 + 占位值透转）",
                trampInsts.OfType<MirGetWrapperAddr>().Any()
                && trampInsts.OfType<MirBoxAny>().Any()
                && trampInsts.OfType<MirNewArray>().Any(n =>
                    n.Elements.Count == 2)
                && calls(trampoline).Any(c =>
                    c.Target.Canonical == pickRing.Symbol.Canonical));

            // 终态 hit 分支：包首 MirGetArray + MirUnboxAny 到 .typeid
            // 槽（方法级 typeid 恢复），随后调 $.wrapped. 原始泛型体
            var ringInsts = instsOf(pickRing);
            TestHarness.CheckTrue("泛型终态解包含 typeid 槽",
                ringInsts.OfType<MirUnboxAny>().Any(u =>
                    pickRing.FindLocal(u.Target).Type.Canonical.Contains(".typeid")
                    || pickRing.FindLocal(u.Target).Type.Canonical.Contains("core::Type")));
            TestHarness.CheckTrue("泛型终态直调 $.wrapped. 体",
                calls(pickRing).Any(c =>
                    c.Target.Canonical == pickWrapped.Symbol.Canonical));

            // router：分支直调 $.wrapped. 终态（typeid 随包透传，签名
            // 仍三包无独立类型包渠道），不调 trampoline
            var router = Fn("Service$.mw.router.1");
            TestHarness.CheckTrue("泛型 router 直调 $.wrapped.（包透传）",
                calls(router).Any(c => c.Target.Canonical == pickWrapped.Symbol.Canonical)
                && !calls(router).Any(c =>
                    c.Target.Canonical == trampoline.Symbol.Canonical)
                && router.Parameters.Count == 4);

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含泛型 wildcard 环", ll.Contains(".bake.Service$pick"),
                ll);
        }

        // specific+wildcard 混合链：外层 WOuter specific、内层 WInner
        // wildcard（转录 VM TestWildcardInnerMiddleOfWrapperChain）——
        // specific 环 inner 打包进 wildcard 环；wildcard 环 hit 直进终态；
        // 只合成 router(H,2)
        private static void TestMixedSpecificWildcardBakingEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WOuter {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 { return inner(x) }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WInner {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WOuter\n" +
                "@WInner\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.mixed.bil");
            TestHarness.CheckTrue("混合链用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("混合链烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            var outerRing = Fn("WOuter$.bake.Service$ping");
            var innerRing = Fn("WInner$.bake.Service$ping");
            // specific 环 inner → wildcard 下一环：get.wrapper.addr(WInner) +
            // symbol 资源 + unnamed 装箱包 + 调内层环
            var outerInsts = outerRing.Blocks.SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("specific 环 inner 打包进 wildcard 环",
                outerInsts.OfType<MirGetWrapperAddr>().Any(g => g.WrapperType == "WInner")
                && outerInsts.OfType<MirLoadResource>().Any()
                && outerInsts.OfType<MirNewArray>().Any()
                && outerInsts.OfType<MirBoxAny>().Any()
                && calls(outerRing).Any(c =>
                    c.Target.Canonical == innerRing.Symbol.Canonical));
            // wildcard 环 hit 直进 $.wrapped. 终态
            TestHarness.CheckTrue("wildcard 内环 hit 直进 $.wrapped.",
                calls(innerRing).Any(c =>
                    c.Target.Canonical.Contains("Service$.wrapped.ping")));
            // 首环 specific：trampoline 为既有直传形态（不打包）
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$ping(x:.i32)@.i32");
            TestHarness.CheckTrue("首环 specific：trampoline 直调外环不打包",
                calls(trampoline).Any(c =>
                    c.Target.Canonical == outerRing.Symbol.Canonical)
                && !trampoline.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirNewArray>().Any());
            // wildcard 在第 1 层（inner）：只合成 router(H,2)
            TestHarness.CheckTrue("只合成 router(H,2)",
                functions.Any(f => f.Symbol.Canonical.Contains("Service$.mw.router.2"))
                && !functions.Any(f => f.Symbol.Canonical.Contains("Service$.mw.router.1")));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含混合链双环",
                ll.Contains("WOuter$.bake.") && ll.Contains("WInner$.bake."), ll);
        }

        // 同层 specific 压 wildcard + 运算符 specific/wildcard 烘焙形状
        //（运算符 fn 原名槽换 trampoline；native 调用点经 add 等内建指令
        // 分派属 ImplBinder 既有空白，此处只断言 MIR 烘焙形状）
        private static void TestOperatorAndSameLayerProxyBakingEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mix {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 { return inner(x) }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Mix\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return x }\n" +
                "    pub func pong(x: i32): i32 { return x }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WO {\n" +
                "    pub init()\n" +
                "    operator .proxy.opr.plus(another: VecA): VecA { return inner(another) }\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WW {\n" +
                "    pub init()\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WO\n" +
                "pub class VecA {\n" +
                "    pub init()\n" +
                "    pub operator plus(another: VecA): VecA { return new VecA() }\n" +
                "}\n" +
                "@WW\n" +
                "pub class VecB {\n" +
                "    pub init()\n" +
                "    pub operator plus(another: VecB): VecB { return new VecB() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return ((s.ping(1) + s.pong(2)))\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.opr.bil");
            TestHarness.CheckTrue("运算符烘焙用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("运算符用例烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            // 同层择一：ping 走 specific 环（不打包），pong 走 wildcard 环
            var pingRing = functions.Single(f =>
                f.Symbol.Canonical.Contains("Mix$.bake.Service$ping"));
            TestHarness.CheckTrue("同层 specific 环直传（无打包）",
                !pingRing.Blocks.SelectMany(b => b.Instructions).OfType<MirNewArray>().Any()
                && pingRing.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("Service$.wrapped.ping")));
            TestHarness.CheckTrue("同层 wildcard 环兜底（pong）",
                functions.Any(f => f.Symbol.Canonical.Contains("Mix$.bake.Service$pong")));

            // 运算符 specific：VecA$$plus 原名槽换 trampoline（直传形态）
            var plusATrampoline = functions.Single(f =>
                f.Symbol.Canonical == "VecA$$plus(another:VecA)@VecA");
            TestHarness.CheckTrue("运算符 specific trampoline（get.wrapper.addr + 调环）",
                plusATrampoline.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapperAddr>()
                    .Any(g => g.WrapperType == "WO")
                && plusATrampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("WO$.bake.VecA$$plus")));
            TestHarness.CheckTrue("运算符 specific $.wrapped. 原始体",
                functions.Any(f =>
                    f.Symbol.Canonical.Contains("VecA$.wrapped.$plus")));
            // 运算符 wildcard：VecB$$plus trampoline 打包 + 环动态分派 +
            // router 覆盖 VecB$$plus 分支
            var plusBTrampoline = functions.Single(f =>
                f.Symbol.Canonical == "VecB$$plus(another:VecB)@VecB");
            TestHarness.CheckTrue("运算符 wildcard trampoline 打包",
                plusBTrampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirNewArray>()
                    .Count() == 2
                && plusBTrampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("WW$.bake.VecB$$plus")));
            var oprRouter = functions.Single(f =>
                f.Symbol.Canonical.Contains("VecB$.mw.router.1"));
            TestHarness.CheckTrue("运算符 router 覆盖 $$plus 分支并直调终态",
                oprRouter.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("VecB$.wrapped.$plus")));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含运算符烘焙环", ll.Contains(".bake.VecB$$plus"), ll);
        }

        // ===== 遗1：用户运算符 native 分派（VM FindOperator 口径） =====

        // intrinsic 直译形状：用户类型操作数的 add/cmp/opposite → MirCall
        //（OperatorDispatch，目标 operator fn）；!= = equals + not；
        // </<= = compareTo + ComparisonResult case 判别（VM OrderCompare
        // 同口径）；内建标量运算保持 MirBinaryIntrinsic 原形状
        private static void TestUserOperatorDispatchEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "    pub operator equals(another: Vec): bool { return (x == another.x) }\n" +
                "    pub operator compareTo(another: Vec): ComparisonResult {\n" +
                "        if ((x < another.x)) { return .LesserThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "    pub operator opposite(): Vec { return new Vec((0 - x)) }\n" +
                "}\n" +
                "pub struct Meter {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator plus(another: Meter): Meter { return new Meter((v + another.v)) }\n" +
                "}\n" +
                "pub interface Equatable { pub operator equals(other: Equatable): bool }\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    var d = (a == b)\n" +
                "    var e = (a != b)\n" +
                "    var f = (a < b)\n" +
                "    var g = (a <= b)\n" +
                "    var h = -a\n" +
                "    var m1 = new Meter(1)\n" +
                "    var m2 = new Meter(2)\n" +
                "    var m3 = m1 + m2\n" +
                "    return c.x\n" +
                "}\n");
            var gate = BilGate.Accept(text, "user.opr.bil");
            TestHarness.CheckTrue("用户运算符用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var main = functions.Single(f => f.Symbol.Canonical == "$main()@.i32");
            var insts = main.Blocks.SelectMany(b => b.Instructions).ToList();
            var dispatchCalls = insts.OfType<MirCall>().Where(c => c.OperatorDispatch).ToList();

            TestHarness.Check("运算符直译调用数（Vec plus/equals×2/compareTo×2/opposite + Meter plus）",
                dispatchCalls.Count.ToString(), "7");
            TestHarness.CheckTrue("add → Vec$$plus（OperatorDispatch）",
                dispatchCalls.Any(c => c.Target.Canonical == "Vec$$plus(another:Vec)@Vec"));
            TestHarness.CheckTrue("==/!= → Vec$$equals 两次（== 直存、!= 取反）",
                dispatchCalls.Count(c =>
                    c.Target.Canonical == "Vec$$equals(another:Vec)@.bool") == 2);
            TestHarness.CheckTrue("</<= → Vec$$compareTo 两次",
                dispatchCalls.Count(c =>
                    c.Target.Canonical == "Vec$$compareTo(another:Vec)@core::ComparisonResult") == 2);
            TestHarness.CheckTrue("一元 - → Vec$$opposite",
                dispatchCalls.Any(c => c.Target.Canonical == "Vec$$opposite()@Vec"));

            // != 的 MIR 形状：equals 调用结果槽经 not 取反
            TestHarness.CheckTrue("!= 形状：equals 后跟 not",
                insts.OfType<MirUnaryIntrinsic>().Any(u => u.Op == BilUnaryOp.Not));
            // < 的 MIR 形状：compareTo + is.case(.LesserThanAnother)；
            // <= 多一个 is.case(.Equal) + or 组合
            var cases = insts.OfType<MirIsCase>().Select(
                c => c.Case.Declaration.QualifiedName).ToList();
            TestHarness.CheckTrue("< 形状：is.case LesserThanAnother 命中",
                cases.Contains("core::ComparisonResult.LesserThanAnother"));
            TestHarness.CheckTrue("<= 形状：is.case Equal + or 组合",
                cases.Contains("core::ComparisonResult.Equal")
                && insts.OfType<MirBinaryIntrinsic>().Any(b => b.Op == BilBinaryOp.Or));

            // 内建标量运算保持原形状（Vec$$plus 体内的 i32 add 不走用户派发）
            var plusFn = functions.Single(f =>
                f.Symbol.Canonical == "Vec$$plus(another:Vec)@Vec");
            TestHarness.CheckTrue("内建 i32 add 保持 MirBinaryIntrinsic",
                plusFn.Blocks.SelectMany(b => b.Instructions).OfType<MirBinaryIntrinsic>()
                    .Any(b => b.Op == BilBinaryOp.Add)
                && !plusFn.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.OperatorDispatch));

            // struct 的 add：Meter$$plus 直译（OperatorDispatch；直调分流
            // 归 Binding）
            TestHarness.CheckTrue("struct add → Meter$$plus（OperatorDispatch）",
                insts.OfType<MirCall>().Any(c => c.OperatorDispatch
                    && c.Target.Canonical == "Meter$$plus(another:Meter)@Meter"));

            // 绑定分流：class 运算符 → 虚派发（运行期按实际类型落最派生
            // 实现，VM 口径）；struct → 直调；interface → iMap 派发
            var vecPlus = context.Symbols.FindMember("Vec$$plus(another:Vec)@Vec")!;
            TestHarness.CheckTrue("class 运算符绑定 → VirtualCallBinding",
                ImplBinder.BindOperatorCall(vecPlus) is VirtualCallBinding);
            var meterPlus = context.Symbols.FindMember("Meter$$plus(another:Meter)@Meter")!;
            TestHarness.CheckTrue("struct 运算符绑定 → DirectCallBinding",
                ImplBinder.BindOperatorCall(meterPlus) is DirectCallBinding);
            var ifaceEquals = context.Symbols.FindMember(
                "Equatable$$equals(other:Equatable)@.bool")!;
            TestHarness.CheckTrue("interface 运算符绑定 → InterfaceCallBinding",
                ImplBinder.BindOperatorCall(ifaceEquals) is InterfaceCallBinding);
            // 显式 invoke 运算符保持静态直调（VM ResolveDispatchSymbol
            // 对 operator 原样返回调用点符号的同口径）
            TestHarness.CheckTrue("显式 invoke 运算符保持 DirectCallBinding",
                ImplBinder.BindCall(vecPlus) is DirectCallBinding);
        }

        // ===== wrapper 应用索引继承闭包（MW10 刀4） =====

        // 闭包语义：本类声明序 outer→inner 在前；祖先未重申应用按基→本
        // 追加在后；同定义重申覆盖只装一次（VM CollectEntityWrappers 靠
        // §14.9 重申约束等价的同一口径）
        private static void TestWrapperIndexInheritanceClosure()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged { pub init() }\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Extra { pub init() }\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Third { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "pub open class Base { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "@Third\n" +
                "pub open class Mid : Base { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "@Third\n" +
                "pub class Leaf : Mid { pub init() }\n" +
                "pub func main(): i32 { return 0 }\n");
            var gate = BilGate.Accept(text, "wrapper.index.closure.bil");
            TestHarness.CheckTrue("闭包索引用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var index = WrapperApplicationIndex.Build(context.Symbols);

            // 重申去重：Mid/Leaf 重申 Logged/Extra 后各只装一次，声明序保持
            TestHarness.CheckTrue("Base 闭包=本类声明",
                index.EntityWrappers("Base").SequenceEqual(new[] { "Logged", "Extra" }));
            TestHarness.CheckTrue("Mid 闭包=重申+追加、无重复",
                index.EntityWrappers("Mid").SequenceEqual(
                    new[] { "Logged", "Extra", "Third" }));
            TestHarness.CheckTrue("Leaf 闭包沿链去重不翻倍",
                index.EntityWrappers("Leaf").SequenceEqual(
                    new[] { "Logged", "Extra", "Third" }));

            // 手写 BIL（绕过 §14.9 重申；§21.3 门禁会拦安装侧不一致，
            // 故直读文本不入门禁）：子类声明剔除 wrapped 后，闭包仍并入
            // 基类应用（VM CollectEntityWrappers 只读本类属重申等价偷懒，
            // 此处按 §9.7 安装侧闭包口径购齐）
            var hand = text.Replace(
                "        pub open wrapped(Logged) wrapped(Extra) wrapped(Third) {",
                "        pub open {");
            TestHarness.CheckTrue("探测：BIL 文本确含可剔除的重申段", hand != text);
            var handIndex = WrapperApplicationIndex.Build(
                new MwContext(BilReader.Read(hand)).Symbols);
            TestHarness.CheckTrue("未重申子类闭包并入基类应用（基→本追加）",
                handIndex.EntityWrappers("Mid").SequenceEqual(
                    new[] { "Logged", "Extra" }));
        }

        // ===== Entity 隐藏槽跨层级去重（MW10 遗3） =====

        // VM HiddenEntityKey 仅含 wrapper TypeRef（不含声明类），子类重申
        // 同 ref 覆盖同一隐藏键——native 物理槽同口径：重申不另开槽，
        // 槽恒归首次声明（最基类）偏移，随 basePlan.Fields 原名逐层拷入。
        // 布局形状断言：Base 恰一个 Entity 槽；Mid/Leaf 无本类名前缀槽、
        // 各恰含一枚原名拷入的基类槽且偏移与 Base 一致
        private static void TestHiddenSlotEntityDedupAcrossHierarchy()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var hits: i32\n" +
                "    pub init() { hits = 0 }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Extra { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "pub open class Base { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "pub open class Mid : Base { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "pub class Leaf : Mid { pub init() }\n" +
                "pub func main(): i32 { return 0 }\n");
            var gate = BilGate.Accept(text, "wrapper.slot.dedup.bil");
            TestHarness.CheckTrue("槽去重用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            TestHarness.CheckTrue("管线挂载布局", context.Layout != null);

            var basePlan = context.Layout!.Find("Base");
            var midPlan = context.Layout.Find("Mid");
            var leafPlan = context.Layout.Find("Leaf");
            TestHarness.CheckTrue("三级布局计划齐全",
                basePlan != null && midPlan != null && leafPlan != null);
            if (basePlan == null || midPlan == null || leafPlan == null)
            {
                return;
            }
            const string baseLogged = "Base#.wrapper.Logged@Logged";
            const string baseExtra = "Base#.wrapper.Extra@Extra";
            TestHarness.CheckTrue("Base 含两枚本类槽",
                basePlan.Fields.Count(f => f.Symbol == baseLogged) == 1
                    && basePlan.Fields.Count(f => f.Symbol == baseExtra) == 1);
            TestHarness.CheckTrue("Mid 重申不另开槽（无 Mid# 前缀 wrapper 槽）",
                !midPlan.Fields.Any(f => f.Symbol.StartsWith("Mid#.wrapper.",
                    System.StringComparison.Ordinal)));
            TestHarness.CheckTrue("Leaf 重申不另开槽（无 Leaf# 前缀 wrapper 槽）",
                !leafPlan.Fields.Any(f => f.Symbol.StartsWith("Leaf#.wrapper.",
                    System.StringComparison.Ordinal)));
            TestHarness.CheckTrue("Mid 恰含原名拷入的基类槽各一枚",
                midPlan.Fields.Count(f => f.Symbol == baseLogged) == 1
                    && midPlan.Fields.Count(f => f.Symbol == baseExtra) == 1);
            TestHarness.CheckTrue("Leaf 恰含原名拷入的基类槽各一枚",
                leafPlan.Fields.Count(f => f.Symbol == baseLogged) == 1
                    && leafPlan.Fields.Count(f => f.Symbol == baseExtra) == 1);
            var baseOffset = basePlan.Fields.First(f => f.Symbol == baseLogged).Offset;
            TestHarness.CheckTrue("基类槽偏移跨层级一致",
                midPlan.Fields.First(f => f.Symbol == baseLogged).Offset == baseOffset
                    && leafPlan.Fields.First(f => f.Symbol == baseLogged).Offset
                        == baseOffset);
        }

        // ===== call??? 降级改写（MW10 刀4） =====

        // 全链形状：调用点 invoke core::Any$call??? → $mw.call???.dispatch
        //（候选 if 链按继承深度深→浅、hit 取槽调 entry 首环、miss 抛
        // NoSuchMethodException）；entry 环（TReturn 擦除 .any、.generic
        // 剔除）inner 原地改写——末环 → router(T, 层+1)（无可烘焙成员
        // 宿主亦预建零分支 router）；无残留 MirInnerCall/call??? 调用
        private static void TestCallWildcardLoweringEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Base$fetchUserById(x:.i32)@.any\") {\n" +
                "            return (99 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Child()\n" +
                "    return (c.fetchUserById(42) as i32)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.callwildcard.bil");
            TestHarness.CheckTrue("call??? 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("call??? 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());
            TestHarness.CheckTrue("调用点无残留 core::Any$call??? 调用",
                !allInsts.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.StartsWith("core::Any$call???",
                        System.StringComparison.Ordinal)));

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var instsOf = new Func<MirFunction, List<MirInst>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).ToList());
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                instsOf(f).OfType<MirCall>().ToList());

            // dispatch fn：两候选（Child 深于 Base）if 链 + miss 抛
            var dispatch = Fn("$mw.call???.dispatch");
            var main = functions.Single(f => f.Symbol.Canonical == "$main()@.i32");
            TestHarness.CheckTrue("main 调用点改写为 dispatch",
                calls(main).Any(c => c.Target.Canonical == dispatch.Symbol.Canonical));
            var checks = instsOf(dispatch).OfType<MirTypeCheck>()
                .Where(t => t.Kind == MirTypeCheckKind.Is).ToList();
            TestHarness.CheckTrue("dispatch 含两候选实际类型判定",
                checks.Count == 2);
            TestHarness.CheckTrue("dispatch 候选按深度深→浅（Child 先 Base 后）",
                checks[0].TargetTypeRef == "Child" && checks[1].TargetTypeRef == "Base");
            TestHarness.CheckTrue("dispatch hit 取槽调 entry 首环并返回",
                instsOf(dispatch).OfType<MirGetWrapperAddr>().Any()
                && calls(dispatch).Any(c =>
                    c.Target.Canonical.Contains("$.mw.call???.entry."))
                && dispatch.Blocks.Any(b => b.Terminator is MirRet));
            TestHarness.CheckTrue("dispatch miss 抛 NoSuchMethodException",
                instsOf(dispatch).OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical == "core::NoSuchMethodException")
                && instsOf(dispatch).OfType<MirThrow>().Any()
                && dispatch.Blocks.Any(b => b.Terminator is MirRetThrow));

            // entry 环：Child/Base 各一环（单层 wildcard）；返回 .any、
            // 无 .generic 形参；inner → router(T, 1)
            var childEntry = Fn("Child$.mw.call???.entry.0");
            TestHarness.CheckTrue("entry 环返回擦除 .any", childEntry.ReturnType.IsAny);
            TestHarness.CheckTrue("entry 环形参剔除 .generic",
                !childEntry.Parameters.Any(p => p.Name.StartsWith(".generic.")));
            TestHarness.CheckTrue("entry 环含 .proxy.* 模板体（symbol 比对）",
                instsOf(childEntry).OfType<MirBinaryIntrinsic>().Any(b =>
                    b.Op == BilBinaryOp.CmpEq && b.LeftType.IsString));
            TestHarness.CheckTrue("Child entry 末环 inner → router(Child, 1)",
                calls(childEntry).Any(c =>
                    c.Target.Canonical.Contains("Child$.mw.router.1")));
            var baseEntry = Fn("Base$.mw.call???.entry.0");
            TestHarness.CheckTrue("Base entry 末环 inner → router(Base, 1)",
                calls(baseEntry).Any(c =>
                    c.Target.Canonical.Contains("Base$.mw.router.1")));
            TestHarness.CheckTrue("router 预建覆盖无可烘焙成员宿主（Child 零分支亦建）",
                functions.Any(f => f.Symbol.Canonical.Contains("Child$.mw.router.1")));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 dispatch", ll.Contains(".mw.call???"), ll);
            TestHarness.CheckTrue("LLVM 含 entry 环", ll.Contains(".mw.call???.entry"), ll);
        }

        // ===== singleton 运行时（MW10 刀5）=====

        // get fn 三态/缓存/异常边形状 + new 改写 + Singletons 条目挂载
        private static void TestSingletonLoweringEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub shared singleton class S {\n" +
                "    pub var v: i32\n" +
                "    pub init() { v = 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new S()\n" +
                "    var b = new S()\n" +
                "    return b.v\n" +
                "}\n");
            var gate = BilGate.Accept(text, "singleton.bil");
            TestHarness.CheckTrue("singleton 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var instsOf = new Func<MirFunction, List<MirInst>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).ToList());

            // 合成条目：get fn / state / cache 符号
            TestHarness.CheckTrue("Singletons 条目挂载（S）",
                context.Singletons.Count == 1
                && context.Singletons[0].TypeCanonical == "S"
                && context.Singletons[0].GetFnCanonical == "S$.static.mw.singleton.get()@S"
                && context.Singletons[0].StateFieldSymbol == "S#.mw.singleton.state@.i32"
                && context.Singletons[0].CacheFieldSymbol == "S#.mw.singleton.cache@S");

            // get fn 形状：三态读写 + 带异常边的真构造 + 失败块置回 + 环抛
            var get = Fn("S$.static.mw.singleton.get");
            TestHarness.CheckTrue("get fn 无参、返回单例类型",
                get.Parameters.Count == 0 && get.ReturnType.Canonical == "S");
            var getInsts = instsOf(get);
            TestHarness.CheckTrue("get fn 读三态槽（entry）",
                getInsts.OfType<MirGetStatic>().Any(g =>
                    g.FieldSymbol == "S#.mw.singleton.state@.i32"));
            TestHarness.CheckTrue("get fn 读缓存槽（ready）",
                getInsts.OfType<MirGetStatic>().Any(g =>
                    g.FieldSymbol == "S#.mw.singleton.cache@S"));
            TestHarness.CheckTrue("get fn 三态槽三写（在途/就绪/置回）",
                getInsts.OfType<MirSetStatic>().Count(s =>
                    s.FieldSymbol == "S#.mw.singleton.state@.i32") == 3);
            TestHarness.CheckTrue("get fn 登记缓存槽",
                getInsts.OfType<MirSetStatic>().Any(s =>
                    s.FieldSymbol == "S#.mw.singleton.cache@S"));
            var construct = getInsts.OfType<MirNewObject>().Single(n =>
                n.Type.Canonical == "S");
            TestHarness.CheckTrue("get fn 真构造带异常边（init 抛出置回在途）",
                construct.ExcTarget != null
                && construct.ExcTarget.Id == "mw.sg.fail"
                && construct.Init.Canonical == "S$init()@.void");
            TestHarness.CheckTrue("get fn 环检测抛异常",
                getInsts.OfType<MirThrow>().Any()
                && getInsts.OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical == "core::RuntimeException"));
            TestHarness.CheckTrue("get fn 失败块置回未构造（state=0）",
                get.Blocks.Any(b => b.Id == "mw.sg.fail"
                    && b.Instructions.OfType<MirSetStatic>().Any(s =>
                        s.FieldSymbol == "S#.mw.singleton.state@.i32")));

            // new 改写：main 里两次 new type(S) → 两处 get 调用，无残留构造
            var main = Fn("$main");
            var mainInsts = instsOf(main);
            TestHarness.CheckTrue("main 无残留 MirNewObject(S)",
                !mainInsts.OfType<MirNewObject>().Any(n => n.Type.Canonical == "S"));
            TestHarness.CheckTrue("main 两处 new 均改写为 get 调用",
                mainInsts.OfType<MirCall>().Count(c =>
                    c.Target.Canonical == "S$.static.mw.singleton.get()@S") == 2);

            // LL：合成静态槽 + get fn 发射
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("合成三态槽发射（i32 全局）",
                ll.Contains("@\"static.S#.mw.singleton.state@.i32\" = internal global i32 0"), ll);
            TestHarness.CheckTrue("合成缓存槽发射（胖引用全局）",
                ll.Contains("@\"static.S#.mw.singleton.cache@S\" = internal global { i64, i64 } zeroinitializer"), ll);
            TestHarness.CheckTrue("get fn 发射",
                ll.Contains("@\"S$.static.mw.singleton.get()@S\"()"), ll);
            TestHarness.CheckTrue("缓存槽纳入 rigi_globals_cleanup",
                ll.Contains("define void @rigi_globals_cleanup()"), ll);
        }

        // rigi_entry 急切初始化调用序（VM InitializeSingletons →
        // InvokeGlobalInitializers → main 同口径）：singleton get 族 →
        // ..globals.init → main
        private static void TestSingletonEntryStubOrder()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "var g: i32 = 40\n" +
                "pub shared singleton class S {\n" +
                "    pub var v: i32\n" +
                "    pub init() { v = 7 }\n" +
                "}\n" +
                "pub func main(): i32 { return new S().v }\n");
            var gate = BilGate.Accept(text, "singleton.entry.bil");
            TestHarness.CheckTrue("入口序用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            var stubAt = ll.IndexOf("define i32 @rigi_entry()", StringComparison.Ordinal);
            TestHarness.CheckTrue("rigi_entry stub 存在", stubAt >= 0, ll);
            var stub = ll.Substring(stubAt);
            var getAt = stub.IndexOf("S$.static.mw.singleton.get()@S", StringComparison.Ordinal);
            var globalsAt = stub.IndexOf("$..globals.init()@.void", StringComparison.Ordinal);
            var mainAt = stub.IndexOf("$main()@.i32", StringComparison.Ordinal);
            TestHarness.CheckTrue("急切初始化序：get 族 → globals.init → main",
                getAt > 0 && globalsAt > getAt && mainAt > globalsAt,
                stub.Substring(0, Math.Min(stub.Length, 1200)));
            // 急切初始化丢弃的返回值归还（防泄漏：释放调用紧贴 get 调用）
            TestHarness.CheckTrue("get 返回值即弃即释放",
                stub.Contains("singleton.get") && stub.Contains("rigi_ref_release"), stub);
        }

        // ===== Method wrapper 烘焙（MW10 刀6，§14.4） =====

        // specific 实例方法链：trampoline 在实现槽 fn（
        // MirGetWrapperMethodAddr 取槽 → 调首环），原始体外移
        // $.mwrapped.，环 inner 直调 $.mwrapped.（接收者 = 宿主本体）
        private static void TestMethodProxyBakingEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"before\")\n" +
                "        var r = inner(x)\n" +
                "        return r\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(21)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.method.bil");
            TestHarness.CheckTrue("Method wrapper 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("Method 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // trampoline = 原名槽 fn（Service$fetch）
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$fetch(x:.i32)@.i32");
            var trampInsts = trampoline.Blocks.SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("trampoline 经 get.wrapper.method.addr 取 Timed 槽",
                trampInsts.OfType<MirGetWrapperMethodAddr>().Any(g =>
                    g.MethodSymbol == "Service$fetch(x:.i32)@.i32" && g.WrapperType == "Timed"));
            var ring = Fn("Timed$.bake.Service$fetch");
            TestHarness.CheckTrue("trampoline 调首环（specific 不打包）",
                calls(trampoline).Any(c => c.Target.Canonical == ring.Symbol.Canonical)
                && !trampInsts.OfType<MirNewArray>().Any());
            // 环 inner → $.mwrapped. 终态（receiver = 宿主）
            var raw = Fn("Service$.mwrapped.fetch");
            TestHarness.CheckTrue("环 inner 直调 $.mwrapped. 原始体",
                calls(ring).Any(c => c.Target.Canonical == raw.Symbol.Canonical));
            TestHarness.CheckTrue("$.mwrapped. 保留原始方法体（mul 指令）",
                raw.Blocks.SelectMany(b => b.Instructions).OfType<MirBinaryIntrinsic>()
                    .Any(b => b.Op == RigiCompiler.Bil.BilBinaryOp.Mul));
            TestHarness.CheckTrue("环特化剔除 .generic.TReturn 形参",
                ring.Parameters.Count == 2 && ring.Parameters[0].Name == ".this"
                && ring.Parameters[1].Name == "x");

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 method 环与 $.mwrapped. 体",
                ll.Contains("Timed$.bake.Service$fetch") && ll.Contains("$.mwrapped.fetch"),
                ll);
        }

        // 双层 specific：outer 环 inner → inner 环（receiver 经槽地址），
        // inner 环 → $.mwrapped.（outer→inner 声明序 = 安装序）
        private static void TestMethodProxyBakingDoubleLayer()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @A\n" +
                "    @B\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(42)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.method2.bil");
            TestHarness.CheckTrue("双层 Method wrapper 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            var outer = Fn("A$.bake.Service$fetch");
            var inner = Fn("B$.bake.Service$fetch");
            var raw = Fn("Service$.mwrapped.fetch");
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$fetch(x:.i32)@.i32");
            TestHarness.CheckTrue("trampoline 调最外环 A",
                calls(trampoline).Any(c => c.Target.Canonical == outer.Symbol.Canonical)
                && trampoline.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapperMethodAddr>().Any(g => g.WrapperType == "A"));
            TestHarness.CheckTrue("A 环 inner → B 环（receiver 经 B 槽地址）",
                calls(outer).Any(c => c.Target.Canonical == inner.Symbol.Canonical)
                && outer.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapperMethodAddr>().Any(g => g.WrapperType == "B"));
            TestHarness.CheckTrue("B 环 inner → $.mwrapped. 终态",
                calls(inner).Any(c => c.Target.Canonical == raw.Symbol.Canonical));
        }

        // wildcard 环：trampoline 打包（.name 资源 = 实现槽 canonical +
        // 具名包 Pair 逐项），环 inner 恒等转发解包直进 $.mwrapped.；
        // 环 ABI 返回 .any、trampoline 拆回原返回类型
        private static void TestMethodProxyBakingWildcard()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(.name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(41)\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.methodwc.bil");
            TestHarness.CheckTrue("wildcard Method wrapper 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("wildcard Method 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$fetch(x:.i32)@.i32");
            var trampInsts = trampoline.Blocks.SelectMany(b => b.Instructions).ToList();
            var ring = Fn("Timed$.bake.Service$fetch");
            TestHarness.CheckTrue("wildcard trampoline 打包具名包（Pair 逐项 + 数组）",
                trampInsts.OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical.Contains("Pair"))
                && trampInsts.OfType<MirNewArray>().Any());
            TestHarness.CheckTrue(".name 资源 = 实现槽 canonical",
                context.Module.Resources.OfType<BilScalarResource>().Any(r =>
                    r.LiteralText.Contains("Service$fetch(x:.i32)@.i32")));
            TestHarness.CheckTrue("wildcard trampoline 调首环",
                calls(trampoline).Any(c => c.Target.Canonical == ring.Symbol.Canonical));
            // 环 inner：按名解包（$mw.named.lookup 逐形参查找）→ 调终态，
            // 结果装箱 .any
            var ringInsts = ring.Blocks.SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("wildcard 环 inner 按名解包直进 $.mwrapped.",
                calls(ring).Any(c => c.Target.Canonical.Contains("$mw.named.lookup"))
                && calls(ring).Any(c =>
                    c.Target.Canonical.Contains("Service$.mwrapped.fetch"))
                && ringInsts.OfType<MirBoxAny>().Any());
            // 按名还原合成 fn：单实例、key 内容相等比对（string CmpEq）、
            // 命中取 value、缺名补 null（VM UnboxNamedArgs 同口径）
            var lookup = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("$mw.named.lookup("));
            var lookupInsts = lookup.Blocks.SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("$mw.named.lookup 按 key 内容相等查找",
                lookupInsts.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol == ProxyWildcardAbi.PairKeyFieldSymbol)
                && lookupInsts.OfType<MirBinaryIntrinsic>().Any(b =>
                    b.Op == RigiCompiler.Bil.BilBinaryOp.CmpEq
                    && b.LeftType.IsString)
                && lookupInsts.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol == ProxyWildcardAbi.PairValueFieldSymbol));
            TestHarness.CheckTrue("$mw.named.lookup 缺名补 null（.any 零值胖引用）",
                lookupInsts.OfType<MirLoadResource>().Any(l =>
                    l.Resource is BilNullResource nullRes && nullRes.TypeRef == ".any")
                && lookup.Blocks.Any(b => b.Terminator is MirCondBranch)
                && lookupInsts.OfType<MirGetArray>().Any());

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 wildcard method 环",
                ll.Contains("Timed$.bake.Service$fetch"), ll);
        }

        // 按名还原（遗留12 任务①，对齐 VM UnboxNamedArgs）：乱序/缺名
        // 与位置无关——环 inner 对每具名形参发一次 $mw.named.lookup
        //（名资源逐参物化），合成 fn 模块级单实例（多方法烘焙共享）；
        // 缺名支返 .any 零值胖引用（VM 缺名补 VmNull 同口径）
        private static void TestMethodProxyWildcardUnpackByName()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func add(a: i32, b: i32): i32 { return (a + b) }\n" +
                "    @Timed\n" +
                "    pub func sub(a: i32, b: i32): i32 { return (a - b) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return (s.add(1, 2) + s.sub(3, 4))\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.methodwc.names.bil");
            TestHarness.CheckTrue("按名还原用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // 模块级单实例（两方法烘焙共享同一 lookup）
            var lookups = functions.Where(f =>
                f.Symbol.Canonical.StartsWith("$mw.named.lookup(")).ToList();
            TestHarness.CheckTrue("$mw.named.lookup 模块级单实例", lookups.Count == 1);
            var lookup = lookups[0];

            foreach (var name in new[] { "add", "sub" })
            {
                var ring = functions.Single(f =>
                    f.Symbol.Canonical.Contains("Timed$.bake.Service$" + name));
                var ringInsts = ring.Blocks.SelectMany(b => b.Instructions).ToList();
                var lookupCalls = calls(ring).Where(c =>
                    c.Target.Canonical == lookup.Symbol.Canonical).ToList();
                TestHarness.CheckTrue(name + " 环 inner 逐具名形参一次 lookup",
                    lookupCalls.Count == 2);
                // 每参名资源物化后作 lookup 第二实参（按名而非按位）
                var nameArgs = lookupCalls.Select(c =>
                    ((MirLocalOperand)c.Args[1]).Name).ToList();
                var literals = ringInsts.OfType<MirLoadResource>()
                    .Where(l => nameArgs.Contains(l.Target))
                    .Select(l => ((BilScalarResource)l.Resource).LiteralText).ToList();
                TestHarness.CheckTrue(name + " 环 lookup 名资源 = 形参名（a/b）",
                    literals.Contains("\"a\"") && literals.Contains("\"b\""));
                TestHarness.CheckTrue(name + " 环 inner 直进 $.mwrapped.",
                    calls(ring).Any(c =>
                        c.Target.Canonical.Contains("Service$.mwrapped." + name)));
            }

            // 乱序/缺名由 lookup 按名语义兜底：key 内容相等命中、全包
            // 扫描环、缺名返 null（VM UnboxNamedArgs 同口径）
            var lookupInsts = lookup.Blocks.SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("lookup 读 Pair key 字段",
                lookupInsts.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol == ProxyWildcardAbi.PairKeyFieldSymbol));
            TestHarness.CheckTrue("lookup key 内容相等比对（string CmpEq）",
                lookupInsts.OfType<MirBinaryIntrinsic>().Any(b =>
                    b.Op == RigiCompiler.Bil.BilBinaryOp.CmpEq
                    && b.LeftType.IsString));
            TestHarness.CheckTrue("lookup 全包扫描环（CondBranch + Branch）",
                lookup.Blocks.Any(b => b.Terminator is MirCondBranch)
                && lookup.Blocks.Any(b => b.Terminator is MirBranch));
            TestHarness.CheckTrue("lookup 缺名补 null（VM 同口径）",
                lookupInsts.OfType<MirLoadResource>().Any(l =>
                    l.Resource is BilNullResource nullRes && nullRes.TypeRef == ".any"));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 $mw.named.lookup",
                ll.Contains("mw.named.lookup"), ll);
        }

        // 宿主形态覆盖：静态方法（companion 实例 fn 被 trampoline、静态
        // 壳不动）、全局函数（..globals.host 实例 fn）、lambda（
        // ..lambda..UUID$$call fn）
        private static void TestMethodProxyBakingHostForms()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub class Calc {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub static func total(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "@Timed\n" +
                "pub func heavy(x: i32): i32 { return (x + 2) }\n" +
                "pub func main(): i32 {\n" +
                "    var fn = func{ @Timed (x: i32): i32 -> (x + 3) }\n" +
                "    return ((Calc.total(1) + heavy(1)) + fn(1))\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.methodhosts.bil");
            TestHarness.CheckTrue("宿主形态用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;

            bool IsMethodTrampoline(MirFunction f) =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperMethodAddr>()
                    .Any();

            // 静态：companion 实例 fn 换 trampoline；壳体直调之（自然命中）
            var companionFn = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("Calc...companion$total("));
            TestHarness.CheckTrue("companion 实例 fn 是 method trampoline",
                IsMethodTrampoline(companionFn));
            var shell = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("Calc$.static.total("));
            TestHarness.CheckTrue("静态壳体不是 trampoline（直调 companion fn）",
                !IsMethodTrampoline(shell)
                && shell.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical == companionFn.Symbol.Canonical));
            TestHarness.CheckTrue("companion 原始体外移 $.mwrapped.",
                functions.Any(f => f.Symbol.Canonical.Contains(
                    "Calc...companion$.mwrapped.total")));

            // 全局函数：..globals.host 实例 fn 换 trampoline；$heavy 壳不动
            var hostFn = functions.Single(f =>
                f.Symbol.Canonical == "..globals.host$heavy(x:.i32)@.i32");
            TestHarness.CheckTrue("globals.host 实例 fn 是 method trampoline",
                IsMethodTrampoline(hostFn));
            TestHarness.CheckTrue("globals.host 原始体外移 $.mwrapped.",
                functions.Any(f => f.Symbol.Canonical.Contains(
                    "..globals.host$.mwrapped.heavy")));

            // lambda：$$call fn 换 trampoline（invoke.indirect 经 vtable 命中）
            var callFn = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("..lambda..")
                && f.Symbol.Canonical.Contains("$$call(x:.i32)"));
            TestHarness.CheckTrue("lambda $$call fn 是 method trampoline",
                IsMethodTrampoline(callFn));
            TestHarness.CheckTrue("lambda 原始体外移 $.mwrapped.",
                functions.Any(f => f.Symbol.Canonical.Contains("$.mwrapped.")
                    && f.Symbol.Canonical.Contains("$call(x:.i32)")));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含三形态烘焙环",
                ll.Contains("Timed$.bake.Calc...companion$total")
                && ll.Contains("Timed$.bake...globals.host$heavy")
                && ll.Contains("$$call"), ll);
        }

        // Entity×Method 三层组合：M 原名槽 = Entity trampoline
        //（get.wrapper.addr），$.wrapped. = method trampoline（
        // get.wrapper.method.addr），$.mwrapped. = 最深层原始体
        private static void TestMethodProxyEntityComposition()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Ent {\n" +
                "    pub init()\n" +
                "    operator .proxy.work(): i32 { return inner() }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Met {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn { return inner() }\n" +
                "}\n" +
                "@Ent\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Met\n" +
                "    pub func work(): i32 { return 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.work()\n" +
                "}\n");
            var gate = BilGate.Accept(text, "wrapper.compose.bil");
            TestHarness.CheckTrue("Entity×Method 组合用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("组合烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            var outermost = functions.Single(f =>
                f.Symbol.Canonical == "Service$work()@.i32");
            TestHarness.CheckTrue("原名槽 = Entity trampoline（get.wrapper.addr Ent）",
                outermost.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperAddr>()
                    .Any(g => g.WrapperType == "Ent"));
            var entityRing = functions.Single(f =>
                f.Symbol.Canonical.Contains("Ent$.bake.Service$work"));
            TestHarness.CheckTrue("Entity 环 inner → $.wrapped.（method trampoline）",
                entityRing.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical == "Service$.wrapped.work()@core::i32"));
            var methodLayer = functions.Single(f =>
                f.Symbol.Canonical == "Service$.wrapped.work()@core::i32");
            TestHarness.CheckTrue("$.wrapped. = method trampoline（Met 槽地址 → Met 环）",
                methodLayer.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapperMethodAddr>().Any(g => g.WrapperType == "Met")
                && methodLayer.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("Met$.bake.Service$work")));
            var methodRing = functions.Single(f =>
                f.Symbol.Canonical.Contains("Met$.bake.Service$work"));
            TestHarness.CheckTrue("Method 环 inner → $.mwrapped. 最深层原始体",
                methodRing.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical == "Service$.mwrapped.work()@core::i32"));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含三层组合",
                ll.Contains("Ent$.bake.Service$work")
                && ll.Contains("$.wrapped.work") && ll.Contains("$.mwrapped.work"), ll);
        }

        // wildcard 环改写 .name 重路由：受控拒绝（VM 已支持，后续补）。
        // 前端 + BIL 验证器（§21.3 保留首参恒等）已先行拒绝，本用例绕过
        // 门禁直读 BIL 文本，压 MW 层第二道防线
        private static void TestMethodProxyWildcardRerouteRejects()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        return inner(\"other\", args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(1)\n" +
                "}\n");
            var context = new MwContext(BilReader.Read(text));
            var rejected = false;
            try
            {
                RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            }
            catch (RigiCompiler.Middleware.MwNotSupportedException)
            {
                rejected = true;
            }
            TestHarness.CheckTrue("wildcard 环改写 .name 受控拒绝", rejected);
        }

        // ===== super 绕过 wrapper 链（遗留12 任务②，对齐 VM
        // ResolveSuper 直接压帧） =====

        // MirSuperCall 目标改写：基类方法被 Method wrapper 烘焙 →
        // $.mwrapped.（Entity×Method 组合的最深层）；被 Entity wrapper
        // 烘焙 → $.wrapped.；无 wrapper 烘焙的基类方法保持原名槽不动
        private static void TestSuperCallBypassesWrapperBaking()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Met {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn { return inner() }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Ent {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(): i32 { return inner() }\n" +
                "}\n" +
                "pub open class BaseM {\n" +
                "    pub init()\n" +
                "    @Met\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class ChildM : BaseM {\n" +
                "    pub init()\n" +
                "    pub override func work(): i32 { return (super() + 10) }\n" +
                "}\n" +
                "@Ent\n" +
                "pub open class BaseE {\n" +
                "    pub init()\n" +
                "    pub open func ping(): i32 { return 2 }\n" +
                "}\n" +
                "@Ent\n" +
                "pub class ChildE : BaseE {\n" +
                "    pub init()\n" +
                "    pub override func ping(): i32 { return (super() + 20) }\n" +
                "}\n" +
                "pub open class BaseN {\n" +
                "    pub init()\n" +
                "    pub open func plain(): i32 { return 3 }\n" +
                "}\n" +
                "pub class ChildN : BaseN {\n" +
                "    pub init()\n" +
                "    pub override func plain(): i32 { return (super() + 30) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return ((new ChildM().work() + new ChildE().ping())\n" +
                "        + new ChildN().plain())\n" +
                "}\n");
            var gate = BilGate.Accept(text, "super.bypass.bil");
            TestHarness.CheckTrue("super 绕链用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            // 子类 override 体自身亦可能被烘焙外移（ChildE 带 @Ent），
            // MirSuperCall 全模块扫描按目标归组
            var superTargets = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).OfType<MirSuperCall>()
                .Select(s => s.Target.Canonical).ToList();

            // Method wrapper：super 目标 → $.mwrapped. 最深层原始体
            TestHarness.CheckTrue("super 目标改写为 $.mwrapped.（Method wrapper）",
                superTargets.Contains("BaseM$.mwrapped.work()@core::i32"));
            TestHarness.CheckTrue("$.mwrapped. 原始体 fn 存在",
                functions.Any(f => f.Symbol.Canonical == "BaseM$.mwrapped.work()@core::i32"));

            // Entity wrapper：super 目标 → $.wrapped. 原始体
            TestHarness.CheckTrue("super 目标改写为 $.wrapped.（Entity wrapper）",
                superTargets.Contains("BaseE$.wrapped.ping()@core::i32"));
            TestHarness.CheckTrue("$.wrapped. 原始体 fn 存在",
                functions.Any(f => f.Symbol.Canonical == "BaseE$.wrapped.ping()@core::i32"));

            // 无 wrapper：super 目标保持原名槽不动
            TestHarness.CheckTrue("无 wrapper 的 super 目标不动",
                superTargets.Contains("BaseN$plain()@.i32"));

            // 全模块不再残留指向被烘焙原名槽的 MirSuperCall
            TestHarness.CheckTrue("无残留指向 trampoline 的 MirSuperCall",
                !superTargets.Any(t => t == "BaseM$work()@.i32"
                    || t == "BaseE$ping()@.i32"));

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 super 直调最深层原始体",
                ll.Contains("$.mwrapped.work") && ll.Contains("$.wrapped.ping"), ll);
        }

        // ===== getid.var + typeid 装箱 .any（MW8a）=====
        private static void TestGetTypeIdVarEmission()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub open class Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub class Dog : Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func probe\\<T>(x: T): Type\\<T> { return typeOf(x) }\n" +
                "pub func main(): i32 {\n" +
                "    var n = 42\n" +
                "    var t = typeOf(n)\n" +
                "    var a: Animal = new Dog()\n" +
                "    var td = typeOf(a)\n" +
                "    var boxed: Any = t\n" +
                "    var back = boxed as Type\\<i32>\n" +
                "    var g = probe\\<i32>(7)\n" +
                "    if (n is t) { }\n" +
                "    if (n is back) { }\n" +
                "    if (a is td) { }\n" +
                "    if (7 is g) { }\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "typeof.var.bil");
            TestHarness.CheckTrue("getid.var 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var allInsts = context.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 getid.var", allInsts.OfType<MirGetTypeIdVar>().Any());
            TestHarness.CheckTrue("MIR 含 typeid 装箱 .any", allInsts.OfType<MirBoxAny>().Any());
            TestHarness.CheckTrue("MIR 含 typeid 拆箱", allInsts.OfType<MirUnboxAny>().Any());

            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("getid.var 常量 store 路径",
                ll.Contains("store ptr @\"typesheet.core::i32\""), ll);
            TestHarness.CheckTrue("getid.var rigi_typeof 调用路径",
                ll.Contains("call ptr @rigi_typeof(i64") && ll.Contains("declare ptr @rigi_typeof(i64"),
                ll);
            TestHarness.CheckTrue("构造 .typeid<i32> sheet",
                ll.Contains("typesheet.core::Type$core::i32$"), ll);
            TestHarness.CheckTrue("typeid tag0 装箱",
                ll.Contains("or i64") && ll.Contains("insertvalue { i64, i64 }"), ll);
            TestHarness.CheckTrue(".null 内建 sheet",
                ll.Contains("@\"typesheet..null\"") || ll.Contains("@typesheet..null"), ll);
        }

        // ===== .typeid<X> 构造 sheet 全链（MW8c-1）=====

        private static void TestTypeIdConstructedSheets()
        {
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var t = typeOf(42)\n" +
                "    var boxed: Any = t\n" +
                "    var back = boxed as Type\\<i32>\n" +
                "    if (t is Type\\<i32>) { }\n" +
                "    if (t is Type\\<String>) { }\n" +
                "    return 0\n" +
                "}\n");
            var gate = BilGate.Accept(text, "typeid.constructed.bil");
            TestHarness.CheckTrue("构造 typeid 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            TestHarness.CheckTrue("布局含 Type<i32> 计划",
                context.Layout != null
                && context.Layout.Find("core::Type<core::i32>") != null);
            var i32Plan = context.Layout!.Find("core::Type<core::i32>")!;
            TestHarness.CheckTrue("Type<i32> 值类型 sheet 形态",
                i32Plan.Kind == TypeLayoutKind.Struct
                && i32Plan.Size == 8
                && i32Plan.TypeFlags == TypeLayoutPlan.FlagInlineValue
                && i32Plan.RefMapCount == 0
                && i32Plan.IMap.Count == 0
                && i32Plan.VTableSlots.Count == 0
                && i32Plan.BasePlan == null);
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("构造 Type<i32> TypeSheet 全局",
                ll.Contains("typesheet.core::Type$core::i32$"), ll);
            TestHarness.CheckTrue("构造 Type<String> TypeSheet 全局",
                ll.Contains("typesheet.core::Type$core::String$"), ll);
            TestHarness.CheckTrue("装箱视图用构造键而非擦除 .typeid",
                !ll.Contains("typesheet..typeid") && ll.Contains("typesheet.core::Type$core::i32$"),
                ll);
            TestHarness.CheckTrue(".null sheet 锚点",
                ll.Contains("@\"typesheet..null\"") || ll.Contains("@typesheet..null"), ll);
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
                ll.Contains("@typesheet.vtable.Derived = internal constant [2 x ptr] " +
                    "[ptr @mw.init.dispatch.Derived, ptr @\"Derived$who()@.i32\"]"), ll);
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
            TestHarness.CheckTrue("虚槽常量为 i32 1（槽 0 分发器）",
                ll.Contains("i32 1") && ll.Contains("rigi_vtable_entry"), ll);

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
            // MW9b-G：拆箱不符由 abort 改抛 CastException
            TestHarness.CheckTrue("unbox 不符抛 CastException",
                ll.Contains("call void @rigi_exc_raise(ptr")
                && ll.Contains("CastException$init(fromType:"), ll);
            TestHarness.CheckTrue("invalid_cast abort 面已退场",
                !ll.Contains("rigi_abort_invalid_cast"), ll);

            var numLl = EmitLlFromSource(
                "pub func main(): i32 {\n" +
                "    var x = 1\n" +
                "    var y = x as i64\n" +
                "    return (y as i32)\n" +
                "}\n", "cast.num.bil");
            TestHarness.CheckTrue("数值 widening 发射 sext",
                numLl.Contains("sext i32"), numLl);
            TestHarness.CheckTrue("数值 narrowing 发射 trunc",
                numLl.Contains("trunc i64"), numLl);

            var phLl = EmitLlFromSource(
                "pub func conv\\<T>(x: Any): T { return x as T }\n" +
                "pub func main(): i32 {\n" +
                "    return conv\\<i32>(42 as Any)\n" +
                "}\n", "cast.ph.bil");
            TestHarness.CheckTrue("占位 cast 调 rigi_try_cast",
                phLl.Contains("call i32 @rigi_try_cast("), phLl);
            TestHarness.CheckTrue("占位 cast 失败抛 CastException",
                phLl.Contains("call void @rigi_exc_raise(ptr")
                && phLl.Contains("CastException$init(fromType:"), phLl);

            var stLl = EmitLlFromSource(
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
                "}\n", "cast.struct.bil");
            TestHarness.CheckTrue("struct 非恒等抛 CastException",
                stLl.Contains("call void @rigi_exc_raise(ptr")
                && stLl.Contains("CastException$init(fromType:"), stLl);
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
                    && e.BaseOffset == 3),
                boxPlan == null ? "" : string.Join(",", boxPlan.IMap));
            TestHarness.CheckTrue("iMap 槽序复用本类 get/tag",
                boxPlan!.VTableSlots.Count == 5
                && boxPlan.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && boxPlan.VTableSlots[3] == boxPlan.VTableSlots[1]
                && boxPlan.VTableSlots[4] == boxPlan.VTableSlots[2]);
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
                hostPlan != null && hostPlan.VTableSlots.Count >= 3
                && hostPlan.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && hostPlan.VTableSlots[1].Contains("$foo(")
                && hostPlan.VTableSlots[2].Contains("$bar("),
                hostPlan == null ? "" : string.Join(",", hostPlan.VTableSlots));
            var derivedPlan = virtCtx.Layout.Find("PairD<core::i32>");
            TestHarness.CheckTrue("PairD<i32> override 复用基槽",
                derivedPlan != null && derivedPlan.VTableSlots.Count >= 3
                && derivedPlan.VTableSlots[1].Contains("PairD$foo(")
                && derivedPlan.VTableSlots[2].Contains("PairD$bar("));
            using var virtLlvm = ModuleBuilder.Build(virtCtx, virtCtx.Mir!);
            var virtLl = virtLlvm.PrintToString();
            TestHarness.CheckTrue("VirtualSlotOf 命中槽 1 与 2",
                virtLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && virtLl.Contains("i32 1") && virtLl.Contains("i32 2"), virtLl);

            var opLl = EmitLlFromSource(
                "pub class Doubler {\n" +
                "    pub init() { }\n" +
                "    pub operator call(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Doubler()\n" +
                "    return d(21)\n" +
                "}\n", "c2b.op.bil");
            TestHarness.CheckTrue("operator call 虚槽为 1（槽 0 分发器）",
                opLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && opLl.Contains("i32 1"), opLl);
            var lambdaLl = EmitLlFromSource(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n", "c2b.lambda.bil");
            TestHarness.CheckTrue("lambda 间接调用虚槽为 1（槽 0 分发器）",
                lambdaLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && lambdaLl.Contains("i32 1"), lambdaLl);
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
            // try 已过门禁且 MIR 面随 MW9a 落地：MirBuilder 展开不再受控拒绝
            var (_, _, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    try { return 1 } catch (e: core.RuntimeException) { return 2 }\n" +
                "}\n");
            var gate = BilGate.Accept(text, "try.bil");
            TestHarness.CheckTrue("try 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var tryMir = MirBuilder.Build(new MwContext(gate.Module!));
            TestHarness.CheckTrue("try MIR 构造放行（MW9a）",
                tryMir.Functions.Any(f => f.IsEntrypoint));

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

            // CLI 路径：受控失败转退出码 2 而非崩溃（try 已随 MW9a 落地，
            // 受控失败样本改用 raw.hex 非 .array<u8> 目标）
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_unsupported_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "raw.bil");
                File.WriteAllText(bilPath, rawBil, new UTF8Encoding(false));
                var result = RunNative("native", "--file", bilPath,
                    "--emit-obj", Path.Combine(dir, "raw.o"));
                TestHarness.CheckTrue("不支持形态 CLI 退出码 2", result.Code == 2);
                TestHarness.CheckTrue("不支持形态错误走 stderr", result.Err.Contains("array<u8>"),
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

        // ===== RcInjection 传播垫（MW9a 第 C 棒）=====

        private static void TestRcPropagatePad()
        {
            const string PadId = RigiCompiler.Middleware.Passes.RcInjectionPass.PropagateBlockId;

            // ① 含可抛调用的函数：垫存在、MirRetThrow 收尾、release 序与
            // ret 出口尾部同口径、全部 ExcTarget 解析指向垫（对象身份）
            var ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func wrap(n: Node): i32 { return n.x }\n" +
                "pub func main(): i32 {\n" +
                "    return wrap(new Node(1))\n" +
                "}\n",
                "rc.pad.bil");
            var main = FnOf(ctx, "$main(");
            var pad = main.Blocks.SingleOrDefault(b => b.Id == PadId);
            TestHarness.CheckTrue("① 含可抛调用函数有传播垫", pad != null);
            TestHarness.CheckTrue("① 垫 MirRetThrow 收尾",
                pad != null && pad.Terminator is MirRetThrow);
            TestHarness.CheckTrue("① 垫指令全 ReleaseSlot",
                pad != null && pad.Instructions.Count > 0
                && pad.Instructions.All(i => i is MirReleaseSlot));
            var retBlock = main.Blocks.First(b => b.Terminator is MirRet);
            var retInsts = retBlock.Instructions;
            var retTail = new List<string>();
            for (var i = retInsts.Count - 1; i >= 0 && retInsts[i] is MirReleaseSlot rel; i--)
            {
                retTail.Insert(0, rel.Local);
            }
            TestHarness.CheckTrue("① 垫 release 序与 ret 出口同口径",
                pad != null
                && pad.Instructions.OfType<MirReleaseSlot>().Select(r => r.Local)
                    .SequenceEqual(retTail),
                string.Join(",", retTail));
            TestHarness.CheckTrue("① 可抛指令 ExcTarget 全解析指向垫",
                pad != null && main.Blocks.SelectMany(b => b.Instructions).All(inst =>
                    inst switch
                    {
                        MirCall call => ReferenceEquals(call.ExcTarget, pad),
                        MirSuperCall superCall => ReferenceEquals(superCall.ExcTarget, pad),
                        MirInvokeIndirect invoke => ReferenceEquals(invoke.ExcTarget, pad),
                        MirThrow throwInst => ReferenceEquals(throwInst.ExcTarget, pad),
                        _ => true,
                    }));

            // ② 无可抛/守卫指令函数（纯常量返回，无调用无 throw 无
            // 守卫型指令——MW9b-G 起 get.field/二元运算/set.array 等
            // 守卫指令也带异常边）：无垫
            ctx = PipelineFromSource(
                "pub func seven(): i32 { return 7 }\n" +
                "pub func main(): i32 { return seven() }\n",
                "rc.padfree.bil");
            var seven = FnOf(ctx, "$seven(");
            TestHarness.CheckTrue("② 无可抛/守卫指令函数无传播垫",
                seven.Blocks.All(b => b.Id != PadId));

            // ③ throw 直写出厂：ExcTarget 解析进垫、原 MirRetThrow 终结符
            // 改道 MirBranch(垫)、全函数 MirRetThrow 仅垫一处
            ctx = PipelineFromSource(
                "class MyError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func boom(): i32 {\n" +
                "    throw new MyError()\n" +
                "}\n" +
                "pub func main(): i32 { return boom() }\n",
                "rc.throw.bil");
            var boom = FnOf(ctx, "$boom(");
            var boomPad = boom.Blocks.SingleOrDefault(b => b.Id == PadId);
            TestHarness.CheckTrue("③ throw 出厂函数有传播垫", boomPad != null);
            var throwInst = boom.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirThrow>().Single();
            TestHarness.CheckTrue("③ MirThrow ExcTarget=垫（对象身份）",
                boomPad != null && ReferenceEquals(throwInst.ExcTarget, boomPad));
            var throwBlock = boom.Blocks.Single(b => b.Instructions.Contains(throwInst));
            TestHarness.CheckTrue("③ throw 块终结符改道 MirBranch(垫)",
                throwBlock.Terminator is MirBranch branch && branch.Target == PadId);
            TestHarness.CheckTrue("③ MirRetThrow 仅传播垫一处",
                boom.Blocks.Count(b => b.Terminator is MirRetThrow) == 1);
        }

        // ===== 异常指令发射（MW9a 第 C 棒，.ll 黄金锚点）=====

        private static void TestExceptionEmission()
        {
            var ctx = PipelineFromSource(
                "class MyError : core.RuntimeException {\n" +
                "    pub init(text: String) { message = text }\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "pub func fail(): i32 {\n" +
                "    throw new MyError(\"boom\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try { return fail() } catch (e: core.RuntimeException) { return 1 }\n" +
                "}\n",
                "exc.emit.bil");
            using var module = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll = module.PrintToString();

            TestHarness.CheckTrue("MirTakePending → rigi_exc_take",
                ll.Contains("call ptr @rigi_exc_take()"), ll);
            TestHarness.CheckTrue("MirThrow → rigi_exc_raise",
                ll.Contains("call void @rigi_exc_raise(ptr"), ll);
            TestHarness.CheckTrue("MirRetThrow → ret undef（值返回）",
                ll.Contains("ret i32 undef"), ll);
            TestHarness.CheckTrue("调用后 pending 检查",
                ll.Contains("call ptr @rigi_exc_pending()"), ll);
            TestHarness.CheckTrue("异常边跳传播垫",
                ll.Contains("label %mw.propagate"), ll);
            TestHarness.CheckTrue("正常边落内联继续块",
                ll.Contains("exc.cont"), ll);
            TestHarness.CheckTrue("派发垫胖引用落槽",
                ll.Contains("store { i64, i64 }"), ll);
            TestHarness.CheckTrue("reporter 块已发射",
                ll.Contains("entry.uncaught:"), ll);
            TestHarness.CheckTrue("reporter 取实际类型全名",
                ll.Contains("@rigi_type_name_of("), ll);
            TestHarness.CheckTrue("reporter 虚派发 getMessage",
                ll.Contains("call ptr @rigi_vtable_entry(ptr"), ll);
            TestHarness.CheckTrue("reporter 打印 stderr",
                ll.Contains("call void @rigi_print_err(ptr"), ll);
            TestHarness.CheckTrue("reporter 释放异常胖引用",
                ll.Contains("call void @rigi_ref_release("), ll);
            TestHarness.CheckTrue("reporter 出口 rigi_exc_halt",
                ll.Contains("call void @rigi_exc_halt()"), ll);
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

        // ===== 动态 new（MW8b）=====

        private static void TestDynamicNew()
        {
            var ctx = PipelineFromSource(
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init() { x = 0 }\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func make\\<T extends Point>(): T { return T() }\n" +
                "pub func makeV\\<T extends Point>(v: i32): T { return T(v) }\n" +
                "pub func fromType(tid: Type\\<Point>, v: i32): Point { return new tid(v) }\n" +
                "pub func main(): i32 {\n" +
                "    var a = make\\<Point>()\n" +
                "    var b = makeV\\<Point>(7)\n" +
                "    var c = fromType(typeOf(Point), 3)\n" +
                "    return ((a.x + b.x) + c.x)\n" +
                "}\n",
                "dynnew.bil");
            var allInsts = ctx.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 new.indirect",
                allInsts.OfType<MirNewIndirect>().Any());

            using var module = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("槽 0 为 init 分发器",
                ll.Contains("@typesheet.vtable.Point = internal constant")
                && ll.Contains("ptr @mw.init.dispatch.Point"), ll);
            TestHarness.CheckTrue("分发器 if 链比 argc",
                ll.Contains("define internal ptr @mw.init.dispatch.Point(ptr")
                && ll.Contains("icmp eq i32"), ll);
            TestHarness.CheckTrue("ctor thunk 序列 alloc",
                ll.Contains("define internal { i64, i64 } @\"mw.init.ctor.Point#")
                && ll.Contains("call ptr @rigi_alloc(ptr @typesheet.Point)"), ll);
            TestHarness.CheckTrue("无匹配 init 抛 NoSuchMethodException",
                ll.Contains("call void @rigi_exc_raise(ptr")
                && ll.Contains("NoSuchMethodException$init(typeName:"), ll);
            TestHarness.CheckTrue("调用点读 vTable 字段",
                ll.Contains("getelementptr") && ll.Contains("dynnew"), ll);

            var pairCtx = PipelineFromSource(
                "pub func main(): i32 {\n" +
                "    var sample = new Pair\\<String, i32>(\"a\", 1)\n" +
                "    var t = typeOf(sample)\n" +
                "    var p = new t(\"b\", 2)\n" +
                "    return p.value\n" +
                "}\n",
                "dynnew-pair.bil");
            TestHarness.CheckTrue("Pair typeOf 值 MIR 含 new.indirect",
                pairCtx.Mir!.Functions.SelectMany(f => f.Blocks)
                    .SelectMany(b => b.Instructions).OfType<MirNewIndirect>().Any());

            var exnCtx = PipelineFromSource(
                "pub func main(): i32 {\n" +
                "    var sample = new RuntimeException(\"x\")\n" +
                "    var t = typeOf(sample)\n" +
                "    var e = new t(\"hello\")\n" +
                "    if (e.getMessage() == \"hello\") { return 1 }\n" +
                "    return 0\n" +
                "}\n",
                "dynnew-exn.bil");
            TestHarness.CheckTrue("RuntimeException typeOf 值 MIR 含 new.indirect",
                exnCtx.Mir!.Functions.SelectMany(f => f.Blocks)
                    .SelectMany(b => b.Instructions).OfType<MirNewIndirect>().Any());

            var structCtx = PipelineFromSource(
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func fromType(tid: Type\\<Vec>, v: i32): Vec { return new tid(v) }\n" +
                "pub func main(): i32 {\n" +
                "    var s = fromType(typeOf(Vec), 9)\n" +
                "    return s.x\n" +
                "}\n",
                "dynnew-struct.bil");
            using var structMod = ModuleBuilder.Build(structCtx, structCtx.Mir!);
            var structLl = structMod.PrintToString();
            TestHarness.CheckTrue("struct 槽 0 为 init 分发器",
                structLl.Contains("@typesheet.vtable.Vec = internal constant")
                && structLl.Contains("ptr @mw.init.dispatch.Vec"), structLl);
            TestHarness.CheckTrue("struct ctor thunk 为 sret void",
                structLl.Contains("define internal void @\"mw.init.ctor.Vec#")
                && structLl.Contains("call void @llvm.memset.p0.i64"), structLl);
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
