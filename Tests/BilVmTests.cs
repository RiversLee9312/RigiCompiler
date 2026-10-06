using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S14 BIL VM 执行断言（BIL_VM_DESIGN §8 / §9 切片 1–5）：
    /// 源码 → 全编译管线 → BilModule → VM 运行。V1 标量/invoke；
    /// V2 对象字段、静态字段、struct 深拷贝、数组、enum、访问器顺序、
    /// 实例方法 receiver。V2.5 arrayOf/arrayOfElements 端到端。
    /// V3 indirect/cast/type。V4 控制流全家（if/loop/switch/try/throw）。
    /// V5 协程（eager spawn / await / yield 三形态 / quiescence）。
    /// frontend 不可达形态用直接构造的 BilModule。
    /// </summary>
    public static partial class BilVmTests
    {



        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static TestSuiteData Spec => new(
            "BilVm", Cases, sectionTitle: "BilVm");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestHelloWorld", TestHelloWorld),
            ("TestLocalArithmetic", TestLocalArithmetic),
            ("TestUnaryNegation", TestUnaryNegation),
            ("TestNegativeLiteralFolding", TestNegativeLiteralFolding),
            ("TestInvokeWithResult", TestInvokeWithResult),
            ("TestStringConcat", TestStringConcat),
            ("TestScalarLocals", TestScalarLocals),
            ("TestClassInstanceFields", TestClassInstanceFields),
            ("TestFieldZeroDefault", TestFieldZeroDefault),
            ("TestDefaultConstructorFieldInitializer", TestDefaultConstructorFieldInitializer),
            ("TestExplicitInitFieldInitializer", TestExplicitInitFieldInitializer),
            ("TestAccessorFieldInitializerViaSetter", TestAccessorFieldInitializerViaSetter),
            ("TestDefaultConstructorChaining", TestDefaultConstructorChaining),
            ("TestSeqStatementReturnTransparency", TestSeqStatementReturnTransparency),
            ("TestStaticFields", TestStaticFields),
            ("TestRootNamespaceGlobalField", TestRootNamespaceGlobalField),
            ("TestLikeDelegationForwarding", TestLikeDelegationForwarding),
            ("TestLikeDelegationInterfaceField", TestLikeDelegationInterfaceField),
            ("TestStructDeepCopy", TestStructDeepCopy),
            ("TestArrayIndexOperators", TestArrayIndexOperators),
            ("TestArrayIndexOutOfBoundsNull", TestArrayIndexOutOfBoundsNull),
            ("TestArrayIndexOutOfBoundsWriteThrows", TestArrayIndexOutOfBoundsWriteThrows),
            ("TestCompoundAssignmentIndexSingleRead", TestCompoundAssignmentIndexSingleRead),
            ("TestEnumCasePayload", TestEnumCasePayload),
            ("TestEnumCaseFixedPayload", TestEnumCaseFixedPayload),
            ("TestEnumCaseIdentity", TestEnumCaseIdentity),
            ("TestGetterSetterOrder", TestGetterSetterOrder),
            ("TestInstanceMethodReceiver", TestInstanceMethodReceiver),
            ("TestBuiltinArrayDirectModule", TestBuiltinArrayDirectModule),
            ("TestArrayOfI32", TestArrayOfI32),
            ("TestArrayOfElementsString", TestArrayOfElementsString),
            ("TestWrapperInstallDirectModule", TestWrapperInstallDirectModule),
            ("TestGetSelfDirectModule", TestGetSelfDirectModule),
            ("TestNumericCasts", TestNumericCasts),
            ("TestReferenceCasts", TestReferenceCasts),
            ("TestCastFailureAndSafe", TestCastFailureAndSafe),
            ("TestGenericArityAndArgumentsAreNotCastViews",
                TestGenericArityAndArgumentsAreNotCastViews),
            ("TestAnyBoxUnbox", TestAnyBoxUnbox),
            ("TestTypeIsSupersCase", TestTypeIsSupersCase),
            ("TestTypeWithAndGetId", TestTypeWithAndGetId),
            ("TestLambdaInvokeIndirect", TestLambdaInvokeIndirect),
            ("TestGetWrapperDirectModule", TestGetWrapperDirectModule),
            ("TestIndirectFieldAndNew", TestIndirectFieldAndNew),
            ("TestUserOperatorAdd", TestUserOperatorAdd),
            ("TestUserOperatorSourceDispatch", TestUserOperatorSourceDispatch),
            ("TestDowngradeCallWildcard", TestDowngradeCallWildcard),
            ("TestMethodWrapperWildcardInnerFullShape", TestMethodWrapperWildcardInnerFullShape),
            ("TestLambdaMethodWrapperWildcardInner", TestLambdaMethodWrapperWildcardInner),
            ("TestWildcardInnerMiddleOfWrapperChain", TestWildcardInnerMiddleOfWrapperChain),
            ("TestWrapperValueGetProxyInitArg", TestWrapperValueGetProxyInitArg),
            ("TestValueWrapperClampedMutableVar", TestValueWrapperClampedMutableVar),
            ("TestValueWrapperTwoLayerOrder", TestValueWrapperTwoLayerOrder),
            ("TestValueWrapperGetOnlyProxy", TestValueWrapperGetOnlyProxy),
            ("TestStringInterpolationToString", TestStringInterpolationToString),
            ("TestEntitySpecificMethodProxySurrounds", TestEntitySpecificMethodProxySurrounds),
            ("TestEntityWildcardMethodProxyBothDirections", TestEntityWildcardMethodProxyBothDirections),
            ("TestEntityGetterSetterProxyCounts", TestEntityGetterSetterProxyCounts),
            ("TestEntityOperatorProxyWildcardDirectModule", TestEntityOperatorProxyWildcardDirectModule),
            ("TestEntityProxyStatePersists", TestEntityProxyStatePersists),
            ("TestEntityProxySelfReadsHostField", TestEntityProxySelfReadsHostField),
            ("TestEntityGenericCastUnboundDirectModule", TestEntityGenericCastUnboundDirectModule),
            ("TestMethodWrapperCallSpecificSurrounds", TestMethodWrapperCallSpecificSurrounds),
            ("TestMethodWrapperStaticViaCompanion", TestMethodWrapperStaticViaCompanion),
            ("TestMethodWrapperDoubleLayerOrder", TestMethodWrapperDoubleLayerOrder),
            ("TestMethodWrapperStatePersists", TestMethodWrapperStatePersists),
            ("TestMethodWrapperArgPassThrough", TestMethodWrapperArgPassThrough),
            ("TestEntryPointNamespaceMain", TestEntryPointNamespaceMain),
            ("TestEntryPointStaticMember", TestEntryPointStaticMember),
            ("TestEntryPointMultipleAndExplicitSelection", TestEntryPointMultipleAndExplicitSelection),
            ("TestGlobalMethodWrapperEndToEnd", TestGlobalMethodWrapperEndToEnd),
            ("TestGlobalMethodWrapperDoubleLayerOrder", TestGlobalMethodWrapperDoubleLayerOrder),
            ("TestGlobalMethodWrapperInNamespace", TestGlobalMethodWrapperInNamespace),
            ("TestMethodWrapperGetSelfDirectModule", TestMethodWrapperGetSelfDirectModule),
            ("TestMethodWrapperNotEqualsViaOprEqualsDirectModule", TestMethodWrapperNotEqualsViaOprEqualsDirectModule),
            ("TestExceptionGetMessage", TestExceptionGetMessage),
            ("TestIntegerDivisionByZero", TestIntegerDivisionByZero),
            ("TestIntegerModulo", TestIntegerModulo),
            ("TestLambdaMethodWrapperEndToEnd", TestLambdaMethodWrapperEndToEnd),
            ("TestIfElse", TestIfElse),
            ("TestWhileAndDoWhile", TestWhileAndDoWhile),
            ("TestForRangeAndBreakContinue", TestForRangeAndBreakContinue),
            ("TestForRangeConstraintDispatch", TestForRangeConstraintDispatch),
            ("TestNamedImportGenericType", TestNamedImportGenericType),
            ("TestNamedBreakContinue", TestNamedBreakContinue),
            ("TestSwitchStatementAndExpression", TestSwitchStatementAndExpression),
            ("TestConditionalExpectedTypeMaterialization", TestConditionalExpectedTypeMaterialization),
            ("TestTryCatchFinally", TestTryCatchFinally),
            ("TestThrowAcrossFunction", TestThrowAcrossFunction),
            ("TestRetBreakContinueThroughFinally", TestRetBreakContinueThroughFinally),
            ("TestRegionBreakIdConsumption", TestRegionBreakIdConsumption),
            ("TestStructuredExitRouting", TestStructuredExitRouting),
            ("TestFinallyExceptionSlotSemantics", TestFinallyExceptionSlotSemantics),
            ("TestUsingDisposeOrder", TestUsingDisposeOrder),
            ("TestLoopEnumeratorDirectModule", TestLoopEnumeratorDirectModule),
            ("TestAsyncAwaitResult", TestAsyncAwaitResult),
            ("TestAwaitExceptionAndCompleted", TestAwaitExceptionAndCompleted),
            ("TestForkJoinAndFireAndForget", TestForkJoinAndFireAndForget),
            ("TestYieldForms", TestYieldForms),
            ("TestPollingProbeResumeSemantics", TestPollingProbeResumeSemantics),
            ("TestConcurrentPrintLines", TestConcurrentPrintLines),
            ("TestAwaitThroughTryFinally", TestAwaitThroughTryFinally),
            ("TestCoroutineStressForkJoin", TestCoroutineStressForkJoin),
            ("TestGenericConstructedNewInit", TestGenericConstructedNewInit),
            ("TestDestructuringSuperGenericInit", TestDestructuringSuperGenericInit),
            ("TestClosedGenericParamSymbolEndToEnd", TestClosedGenericParamSymbolEndToEnd),
            ("TestGenericFunctionConstructedParams", TestGenericFunctionConstructedParams),
            ("TestGenericInferenceEndToEnd", TestGenericInferenceEndToEnd),
            ("TestGenericParamConstraintDispatch", TestGenericParamConstraintDispatch),
            ("TestGetIdTypeResolvesNestedGeneric", TestGetIdTypeResolvesNestedGeneric),
            ("TestStringLength", TestStringLength),
            ("TestWrapperOutsideSetterWriteOrder", TestWrapperOutsideSetterWriteOrder),
            ("TestWrapperOutsideGetterReadOrder", TestWrapperOutsideGetterReadOrder),
            ("TestWrapperAccessorInitializerNoCrash", TestWrapperAccessorInitializerNoCrash),
            ("TestSetterBodyMultipleValueAccess", TestSetterBodyMultipleValueAccess),
            ("TestWrapperAccessorLocalOrder", TestWrapperAccessorLocalOrder),
            ("TestGenericIndexOperator", TestGenericIndexOperator),
            ("TestClassGenericParamInMethodFrame", TestClassGenericParamInMethodFrame),
            ("TestTwoGenericArgMapInitMatch", TestTwoGenericArgMapInitMatch),
            ("TestColdTaskCapturedAtomicMapAwait", TestColdTaskCapturedAtomicMapAwait),
            ("TestNestedGenericHostEnumerator", TestNestedGenericHostEnumerator),
            ("TestNestedClassOuterGenericCapture", TestNestedClassOuterGenericCapture),
            ("TestReifiedConstructZeroValue", TestReifiedConstructZeroValue),
            ("TestDynamicNewMustThrowNoMatchingInit", TestDynamicNewMustThrowNoMatchingInit),
            ("TestNestedClassNullableInit", TestNestedClassNullableInit),
            ("TestInitMatchByAssignability", TestInitMatchByAssignability),
            ("TestNullableGenericTypeArgument", TestNullableGenericTypeArgument),
            ("TestConstructedTypeStaticMembers", TestConstructedTypeStaticMembers),
            ("TestGenericNullableEndToEnd", TestGenericNullableEndToEnd),
            ("TestBoundNullableBoxingEndToEnd", TestBoundNullableBoxingEndToEnd),
            ("TestNestedStructFieldChainWrite", TestNestedStructFieldChainWrite),
            ("TestStructReceiverCallWriteback", TestStructReceiverCallWriteback),
            ("TestClassEmbeddedStructFieldWrite", TestClassEmbeddedStructFieldWrite),
            ("TestThreeLevelNestedStructWrite", TestThreeLevelNestedStructWrite),
            ("TestStaticFieldRootChainWrite", TestStaticFieldRootChainWrite),
            ("TestWrappedStaticFieldRootChainWrite", TestWrappedStaticFieldRootChainWrite),
            ("TestTemporaryLazyRestore", TestTemporaryLazyRestore),
            ("TestListAddGetRemoveIterate", TestListAddGetRemoveIterate),
            ("TestMapSetGetRemoveIterate", TestMapSetGetRemoveIterate),
            ("TestMapObjectKeyIdentityNotMerged", TestMapObjectKeyIdentityNotMerged),
            ("TestMapContentKeysStillMerge", TestMapContentKeysStillMerge),
            ("TestMapCustomHashKeySemantics", TestMapCustomHashKeySemantics),
            ("TestMapCustomEqualsKeySemantics", TestMapCustomEqualsKeySemantics),
            ("TestParcelSetGetNestedAbsentNull", TestParcelSetGetNestedAbsentNull),
            ("TestSerializableScalarDeepCopy", TestSerializableScalarDeepCopy),
            ("TestSerializableWithStatefulEntityWrapperDeepCopy", TestSerializableWithStatefulEntityWrapperDeepCopy),
            ("TestSerializableNestedIndependent", TestSerializableNestedIndependent),
            ("TestSerializableCollectionsSnapshot", TestSerializableCollectionsSnapshot),
            ("TestSerializableTemporaryResume", TestSerializableTemporaryResume),
            ("TestSerializableGenericClone", TestSerializableGenericClone),
            ("TestSerializationBaseImpliesSerializable", TestSerializationBaseImpliesSerializable),
            ("TestSerializationDepthBudget", TestSerializationDepthBudget),
            ("TestSerializationModeMismatch", TestSerializationModeMismatch),
            ("TestMessageQueueSmokeSemantics", TestMessageQueueSmokeSemantics),
            ("TestMessageQueueCapabilityMatrix", TestMessageQueueCapabilityMatrix),
            ("TestMessageQueueLifetimeEos", TestMessageQueueLifetimeEos),
            ("TestMessageQueueBroadcast", TestMessageQueueBroadcast),
            ("TestMessageQueueAcceptanceAndOutstanding", TestMessageQueueAcceptanceAndOutstanding),
            ("TestReceiverExecutorRouting", TestReceiverExecutorRouting),
            ("TestReceiverReaderCursorIndependence", TestReceiverReaderCursorIndependence),
            ("TestReceiverDisposeIsolation", TestReceiverDisposeIsolation),
            ("TestReceiverListenerIdentity", TestReceiverListenerIdentity),
            ("TestMessengerDisposeEosStopsPump", TestMessengerDisposeEosStopsPump),
            ("TestReceiverPumpIoLaneFullDelivery", TestReceiverPumpIoLaneFullDelivery),
            ("TestReceiverListenerTimerSuspendResume", TestReceiverListenerTimerSuspendResume),
        };

        private static BilVmResult RunPrepared(BilModule module, string functionSymbol,
            IReadOnlyList<VmValue> arguments)
        {
            var context = new VmContext(module);
            var function = context.FindFunction(functionSymbol);
            CaseAssertions.CheckTrue("预备 fn 存在", function != null, functionSymbol);
            // 直建模块（无 stdlib Dispatcher）走降级通道；否则完整调度链
            if (!context.Dispatch.HasDispatcher)
            {
                var standalone = context.Dispatch.RunStandalone(function!,
                    arguments.ToArray());
                return new BilVmResult(context.Stdout, context.Stderr,
                    standalone.Result, standalone.Failure);
            }
            context.InitializeSingletons();
            context.InvokeGlobalInitializers();
            var coroutine = context.Dispatch.Spawn(function!, arguments, caller: null);
            context.Dispatch.RunMainLoop();
            return new BilVmResult(context.Stdout, context.Stderr, coroutine.Result,
                coroutine.Failure);
        }

        private static BilVmResult Run(string source)
        {
            try
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
                CaseAssertions.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                        d => $"{d.Phase}: {d.Message}")));
                if (unit.Diagnostics.HasErrors)
                {
                    return new BilVmResult("", "", null,
                        new VmException("编译失败，跳过 VM"));
                }
                return BilVm.Run(module);
            }
            catch (Exception exception)
            {
                CaseAssertions.CheckTrue("全管线无诊断", false, exception.ToString());
                return new BilVmResult("", "", null,
                    new VmException(exception.Message, inner: exception));
            }
        }

        private static void CheckOk(string label, BilVmResult result)
        {
            CaseAssertions.CheckTrue(label + " 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
        }

        private static void CheckI32(string label, BilVmResult result, int expected)
        {
            CaseAssertions.CheckTrue(label,
                result.ReturnValue is VmI32 n && n.Value == expected,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        private static void CheckBool(string label, BilVmResult result, bool expected)
        {
            CaseAssertions.CheckTrue(label,
                result.ReturnValue is VmBool flag && flag.Value == expected,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }
    }
}
