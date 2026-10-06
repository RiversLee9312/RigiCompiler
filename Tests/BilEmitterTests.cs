using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S6 P4 最小闭环 + S7a 基础发射补齐测试：Lowerer（P4a 恒等重写）+
    /// BilEmitter（P4b 发射）端到端。
    /// M58 起迁移到 BilTestHarness 验证器框架：全管线无诊断（CheckNoErrors）
    /// → CheckBilValid（BilVerifier 零错误）→ CheckFnShape/CheckResShape
    /// 形状黄金（指令序列/操作数/.tN 编号/block id 逐字节锁定；资源名按
    /// 首次出现顺序重编号 res(#k)/#k，消除 stdlib 基线资源偏移脆弱点）。
    /// 覆盖：全管线无诊断、hello world 模块结构断言（唯一源模块名与可信 helper 绑定 /
    /// Resources 计数与标量资源 / LocalSymbols 含 core.io::Console 类型与
    /// println 静态方法声明 / main 与 println 两个 fn 定义 / main 恰一个
    /// entrypoint block）、Origin 调试链
    /// （BilInstruction.Origin → LoweredNode.Origin → BoundNode.Syntax → Span）、
    /// 资源去重；S7a 新增：局部声明 + 初始化器（set.var）、赋值、二元运算
    /// （算术 + 比较）、一元运算、带返回值 invoke、表达式语句（结果丢弃）、
    /// new 构造、§19.1 标量资源全形态（bool/f64/f32/char/null）、static 字段
    /// 读写（get/set.field.static）；负例改为实例方法（P4 Error + 跳过 fn）。
    /// S7b：if 语句/表达式与短路 and/or 的多 block 黄金文本（§16.2 if
    /// 指令、none 操作数、if0-then 形态 block id、分支块落尾不补 ret、
    /// 合成 bool 常量同键去重）。
    /// S7c-1：while/do-while 的 loop/loop.rev 发射（§16.3/§16.4 三 block
    /// 黄金文本与操作数序 cond/body/none/judge/breakid、loop0-body/
    /// loop0-judge 块 id 递增）、break/continue（§16.5 嵌套标签命中外层
    /// breakid）、.vars 的 .breakid 条目（§9.3）。
    /// S7d：常量 switch 的 switch 指令发射（§16.6 操作数序 selector/
    /// res(表)/[blk item 表]/blk(default)/breakid、switch0-item0/
    /// switch0-default 块 id、§19.4 switch-table 单行资源与跨 fn 同表
    /// 去重）、pattern switch 不到 P4b（P4a 已降为 if 链——无 switch
    /// 指令与表资源）、throw（§16.9 单操作数、entry 块 throw 终止不补 ret）。
    /// S8c：索引访问（§13.6 get.array/set.array 发射——读写/复合/链式形态，
    /// 用户 operator 的 §8.4 声明形态断言）。
    /// S8e：访问器声明发射（§8.3 字段形态标记 backing/computed/readable/
    /// writable/compiler-generated + §8.4 getter(FIELD)/setter(FIELD) 字段槽
    /// 驱动声明，类 backing 与全局自动访问器 fn 形状黄金）与
    /// override/abstract 投影（§8.2/§8.4 关键字修饰，abstract 无 fn 定义）。
    /// S11：enum case（§8.5 .case 声明挂类型 Members——洞签名形态 +
    /// discriminant auto/res(R) 显式判别值资源登记；§14.3 new.case 与
    /// §12.3 type.is.case 值发射，含 switch pattern 降级路径；enum 无体
    /// init 经 §9.3 映射赋值体合成照常发射声明 + fn 定义）。
    /// §9.3 增补（BilEmitterTests.Members.cs）：init 参数映射赋值合成
    /// 端到端——无体 init 的 fn 定义（set.field + ret，§21.2 门槛）。
    /// S11 增补（BilEmitterTests.Ext.cs）：ext 收尾端到端样例（M80）——
    /// ext 实例字段读写/实例方法调用/ext 字段 + 访问器（用户类型与内建
    /// String）/ext static 字段常量方法/ext 字段复合赋值。
    /// </summary>
    public static partial class BilEmitterTests
    {
        private const string HelloWorldSource =
            "pub func main(): i32 {\n" +
            "    core.io.Console.println(\"Hello, world!\")\n" +
            "    return 0\n" +
            "}\n";


        internal static TestSuiteData Spec { get; } = new("BilEmitter",
        [
            (nameof(TestUnsafeProjection), TestUnsafeProjection),
            (nameof(TestGoldenOutput), TestGoldenOutput),
            (nameof(TestOriginChain), TestOriginChain),
            (nameof(TestResourceDeduplication), TestResourceDeduplication),
            (nameof(TestLocalDeclarationAndAssignment), TestLocalDeclarationAndAssignment),
            (nameof(TestUnaryAndComparison), TestUnaryAndComparison),
            (nameof(TestUserOperatorEqualsEmission), TestUserOperatorEqualsEmission),
            (nameof(TestUserOperatorPlusEmission), TestUserOperatorPlusEmission),
            (nameof(TestInvokeWithResult), TestInvokeWithResult),
            (nameof(TestNew), TestNew),
            (nameof(TestLiteralResources), TestLiteralResources),
            (nameof(TestStaticFieldReadWrite), TestStaticFieldReadWrite),
            (nameof(TestIfStatementEmission), TestIfStatementEmission),
            (nameof(TestIfExpressionEmission), TestIfExpressionEmission),
            (nameof(TestIfExpressionSubtypeEmission), TestIfExpressionSubtypeEmission),
            (nameof(TestShortCircuitEmission), TestShortCircuitEmission),
            (nameof(TestLoopEmission), TestLoopEmission),
            (nameof(TestInstanceEmission), TestInstanceEmission),
            (nameof(TestGenericParamMemberEmission), TestGenericParamMemberEmission),
            (nameof(TestInitMappingEmission), TestInitMappingEmission),
            (nameof(TestForLoopEmission), TestForLoopEmission),
            (nameof(TestSwitchEmission), TestSwitchEmission),
            (nameof(TestPatternSwitchEmission), TestPatternSwitchEmission),
            (nameof(TestThrowEmission), TestThrowEmission),
            (nameof(TestCastEmission), TestCastEmission),
            (nameof(TestStringInterpolationEmission), TestStringInterpolationEmission),
            (nameof(TestSafeAccessEmission), TestSafeAccessEmission),
            (nameof(TestNullFallbackEmission), TestNullFallbackEmission),
            (nameof(TestDestructuringEmission), TestDestructuringEmission),
            (nameof(TestTryEmission), TestTryEmission),
            (nameof(TestSeqEmission), TestSeqEmission),
            (nameof(TestTypeCheckEmission), TestTypeCheckEmission),
            (nameof(TestTypeOfEmission), TestTypeOfEmission),
            (nameof(TestDynamicNewEmission), TestDynamicNewEmission),
            (nameof(TestIndexEmission), TestIndexEmission),
            (nameof(TestContainerCallSuffixChainEmission), TestContainerCallSuffixChainEmission),
            (nameof(TestAccessorEmission), TestAccessorEmission),
            (nameof(TestLocalAccessorEmission), TestLocalAccessorEmission),
            (nameof(TestOverrideProjection), TestOverrideProjection),
            (nameof(TestSuperEmission), TestSuperEmission),
            (nameof(TestGenericEmission), TestGenericEmission),
            (nameof(TestIndirectGenericEmission), TestIndirectGenericEmission),
            (nameof(TestGenericVarianceEmission), TestGenericVarianceEmission),
            (nameof(TestVarArgsEmission), TestVarArgsEmission),
            (nameof(TestGenericVarArgsEmission), TestGenericVarArgsEmission),
            (nameof(TestExceptionEmission), TestExceptionEmission),
            (nameof(TestDisposableEmission), TestDisposableEmission),
            (nameof(TestUsingEmission), TestUsingEmission),
            (nameof(TestAsyncTaskEmission), TestAsyncTaskEmission),
            (nameof(TestAwaitEmission), TestAwaitEmission),
            (nameof(TestYieldEmission), TestYieldEmission),
            (nameof(TestGenericNullableNullResource), TestGenericNullableNullResource),
            (nameof(TestVarArgsParameterAssignment), TestVarArgsParameterAssignment),
            (nameof(TestNamedPackResultType), TestNamedPackResultType),
            (nameof(TestVarArgsIndexBoxingEmission), TestVarArgsIndexBoxingEmission),
            (nameof(TestExtFieldDeclarationModifier), TestExtFieldDeclarationModifier),
            (nameof(TestExtInstanceFieldAndMethodEmission), TestExtInstanceFieldAndMethodEmission),
            (nameof(TestExtAccessorEmission), TestExtAccessorEmission),
            (nameof(TestExtBuiltinAccessorEmission), TestExtBuiltinAccessorEmission),
            (nameof(TestExtStaticEmission), TestExtStaticEmission),
            (nameof(TestStringLengthFieldEmission), TestStringLengthFieldEmission),
            (nameof(TestExtCompoundAssignmentEmission), TestExtCompoundAssignmentEmission),
            (nameof(TestConstFieldModifierEmission), TestConstFieldModifierEmission),
            (nameof(TestSmartCastCompoundAssignmentEmission), TestSmartCastCompoundAssignmentEmission),
            (nameof(TestEvalOrderGuardEmission), TestEvalOrderGuardEmission),
            (nameof(TestSiblingScopeLocalUniquification), TestSiblingScopeLocalUniquification),
            (nameof(TestCrossFunctionCatchTablePrivate), TestCrossFunctionCatchTablePrivate),
            (nameof(TestSafeAccessVoidCallEmission), TestSafeAccessVoidCallEmission),
            (nameof(TestEnumCaseFixedEmission), TestEnumCaseFixedEmission),
            (nameof(TestEnumCaseFixedPayloadEmission), TestEnumCaseFixedPayloadEmission),
            (nameof(TestEnumCaseParameterizedEmission), TestEnumCaseParameterizedEmission),
            (nameof(TestEnumCaseExplicitDiscriminant), TestEnumCaseExplicitDiscriminant),
            (nameof(TestEnumCaseDiscardedStatementEmission), TestEnumCaseDiscardedStatementEmission),
            (nameof(TestWrapperFieldInitializerEmission), TestWrapperFieldInitializerEmission),
            (nameof(TestGenericParameterFieldInitializers), TestGenericParameterFieldInitializers),
            (nameof(TestForwardDefaultConstructionEmission), TestForwardDefaultConstructionEmission),
            (nameof(TestSerializableImplicitDefaultConstruction), TestSerializableImplicitDefaultConstruction),
            (nameof(TestGenericWrapperFieldInitializerEmission), TestGenericWrapperFieldInitializerEmission),
            (nameof(TestWrapperEntityReadEmission), TestWrapperEntityReadEmission),
            (nameof(TestWrapperEntityWriteEmission), TestWrapperEntityWriteEmission),
            (nameof(TestWrapperEntityCallEmission), TestWrapperEntityCallEmission),
            (nameof(TestWrapperNestedChainEmission), TestWrapperNestedChainEmission),
            (nameof(TestWrapperIndexReadEmission), TestWrapperIndexReadEmission),
            (nameof(TestWrapperFieldValueEmission), TestWrapperFieldValueEmission),
            (nameof(TestWrapperFieldValueSameWrapperTwoFields), TestWrapperFieldValueSameWrapperTwoFields),
            (nameof(TestWrapperCompoundAssignmentEmission), TestWrapperCompoundAssignmentEmission),
            (nameof(TestWrapperPlaceEmissionGates), TestWrapperPlaceEmissionGates),
            (nameof(TestWrapperCellStorageCoverage), TestWrapperCellStorageCoverage),
            (nameof(TestWrapperFieldValueCallAndIndexEmission), TestWrapperFieldValueCallAndIndexEmission),
            (nameof(TestWrapperIndexWriteEmission), TestWrapperIndexWriteEmission),
            (nameof(TestWrapperDeepWriteEmission), TestWrapperDeepWriteEmission),
            (nameof(TestWrapperDeepWriteMixedBoundary), TestWrapperDeepWriteMixedBoundary),
            (nameof(TestWrapperDeepCompoundAssignmentEmission), TestWrapperDeepCompoundAssignmentEmission),
            (nameof(TestWrapperSharedHostFieldStability), TestWrapperSharedHostFieldStability),
            (nameof(TestGenericParamWithWrapperEmission), TestGenericParamWithWrapperEmission),
            (nameof(TestProxyBakingEmission), TestProxyBakingEmission),
            (nameof(TestProxyWildcardBakingEmission), TestProxyWildcardBakingEmission),
            (nameof(TestProxySpecificVariadicEmission), TestProxySpecificVariadicEmission),
            (nameof(TestProxyAccessorBakingEmission), TestProxyAccessorBakingEmission),
            (nameof(TestDowngradeEmissionSingle), TestDowngradeEmissionSingle),
            (nameof(TestDowngradeCastMaterialization), TestDowngradeCastMaterialization),
            (nameof(TestDowngradeStatementPosition), TestDowngradeStatementPosition),
            (nameof(TestDowngradeDoubleChain), TestDowngradeDoubleChain),
            (nameof(TestDowngradeGateNoChain), TestDowngradeGateNoChain),
            (nameof(TestDowngradeViaInterface), TestDowngradeViaInterface),
            (nameof(TestDowngradeExemptionPositions), TestDowngradeExemptionPositions),
            (nameof(TestInitWrapperTypeLevelEmission), TestInitWrapperTypeLevelEmission),
            (nameof(TestInitFieldSynthesisEmission), TestInitFieldSynthesisEmission),
            (nameof(TestInitWrapperCellArgsEmission), TestInitWrapperCellArgsEmission),
            (nameof(TestWrapperPlaceVoidCallEmission), TestWrapperPlaceVoidCallEmission),
            (nameof(TestGlobalWrappedFieldEmission), TestGlobalWrappedFieldEmission),
            (nameof(TestStaticMethodCompanionEmission), TestStaticMethodCompanionEmission),
            (nameof(TestLambdaNoCapture), TestLambdaNoCapture),
            (nameof(TestLambdaVarCapture), TestLambdaVarCapture),
            (nameof(TestLambdaConstCapture), TestLambdaConstCapture),
            (nameof(TestLambdaThisCapture), TestLambdaThisCapture),
            (nameof(TestLambdaNestedCapture), TestLambdaNestedCapture),
            (nameof(TestLambdaVoidAction), TestLambdaVoidAction),
            (nameof(TestLambdaAsync), TestLambdaAsync),
            (nameof(TestLambdaExplicitFuncType), TestLambdaExplicitFuncType),
            (nameof(TestLambdaParamCapturePrologue), TestLambdaParamCapturePrologue),
            (nameof(TestLambdaBlockBody), TestLambdaBlockBody),
            (nameof(TestLambdaThrowBlock), TestLambdaThrowBlock),
            (nameof(TestLambdaCompoundAssignCapture), TestLambdaCompoundAssignCapture),
            (nameof(TestLambdaGenericContext), TestLambdaGenericContext),
            (nameof(TestLambdaMethodGenericCellCapture), TestLambdaMethodGenericCellCapture),
            (nameof(TestLambdaMethodGenericNestedCapture), TestLambdaMethodGenericNestedCapture),
            (nameof(TestLambdaMethodGenericParamCapture), TestLambdaMethodGenericParamCapture),
            (nameof(TestLambdaVoidIndirectCall), TestLambdaVoidIndirectCall),
            (nameof(TestLambdaVoidIndirectCallGrouped), TestLambdaVoidIndirectCallGrouped),
            (nameof(TestLambdaVoidIndirectCallReturned), TestLambdaVoidIndirectCallReturned),
            (nameof(TestLambdaVoidIndirectCallIndexed), TestLambdaVoidIndirectCallIndexed),
            (nameof(TestLambdaForCapture), TestLambdaForCapture),
            (nameof(TestLambdaNestedForCapture), TestLambdaNestedForCapture),
            (nameof(TestLambdaCatchCapture), TestLambdaCatchCapture),
            (nameof(TestLambdaFinallyCapture), TestLambdaFinallyCapture),
            (nameof(TestLambdaUsingCapture), TestLambdaUsingCapture),
            (nameof(TestLambdaMethodWrapperEmission), TestLambdaMethodWrapperEmission),
            (nameof(TestBuiltinToStringEmission), TestBuiltinToStringEmission),
            (nameof(TestUnsupportedNodes), TestUnsupportedNodes),
            (nameof(TestGenericIndexOperatorEmission), TestGenericIndexOperatorEmission),
            (nameof(TestClassGenericParamFrameEmission), TestClassGenericParamFrameEmission),
            (nameof(TestEntryPointAnnotationEmission), TestEntryPointAnnotationEmission),
            (nameof(TestNamespaceSliceEmission), TestNamespaceSliceEmission),
        ], sectionTitle: "BilEmitter", memoryMiB: 2048);

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            CaseAssertions.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
        }
    }
}
