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
using RigiCompiler.Middleware.Toolchain;
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
    public static partial class MiddlewareTests
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



        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static TestSuiteData Spec => new(
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
            ("TestModGuardEmission", TestModGuardEmission),
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
            ("TestMethodProxyWildcardReroute", TestMethodProxyWildcardReroute),
            ("TestSuperCallBypassesWrapperBaking", TestSuperCallBypassesWrapperBaking),
            ("TestGetTypeIdVarEmission", TestGetTypeIdVarEmission),
            ("TestTypeIdConstructedSheets", TestTypeIdConstructedSheets),
            ("TestObjectPathEmission", TestObjectPathEmission),
            ("TestValuePathEmission", TestValuePathEmission),
            ("TestStaticEmission", TestStaticEmission),
            ("TestArrayPathEmission", TestArrayPathEmission),
            ("TestSpanPathEmission", TestSpanPathEmission),
            ("TestRawBufferEmission", TestRawBufferEmission),
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
            ("TestCoroutineLowering", TestCoroutineLowering),
            ("TestSyntheticFrameTypePlan", TestSyntheticFrameTypePlan),
            ("TestCoroutineSplit", TestCoroutineSplit),
            ("TestCoroutineSplitRejection", TestCoroutineSplitRejection),
            ("TestCoroutineYieldAlarm", TestCoroutineYieldAlarm),
            ("TestCoroutineEmit", TestCoroutineEmit),
            ("TestExceptionEmission", TestExceptionEmission),
            ("TestRefMapMw7a", TestRefMapMw7a),
            ("TestDynamicNew", TestDynamicNew),
            ("TestCapabilityConstructedCalls", () => TestCapabilityConstructedCalls()),
            ("TestExternalProcessDeadline", TestExternalProcessDeadline),
            ("TestRuntimeFeatureMacroPreamble", TestRuntimeFeatureMacroPreamble),
            ("TestRuntimeTargetIdentity", TestRuntimeTargetIdentity),
            ("TestWrapperGenericInitArgumentAbi", TestWrapperGenericInitArgumentAbi),
            ("TestComp003ArtifactCache", TestComp003ArtifactCache),
            ("TestComp003RuntimeSnapshot", TestComp003RuntimeSnapshot),
            ("TestComp003ObjectIdentity", TestComp003ObjectIdentity),
            ("TestComp003NativeCache", TestComp003NativeCache),
        };

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
            WorkerConsole.SetOut(outWriter);
            WorkerConsole.SetError(errWriter);
            try
            {
                int code = new NativeCommand().Execute(result!);
                return (code, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                WorkerConsole.SetOut(oldOut);
                WorkerConsole.SetError(oldErr);
            }
        }
    }
}
