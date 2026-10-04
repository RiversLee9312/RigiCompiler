using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S7a P4a 测试：Lowerer 对 P3（S5）全部 Bound 节点的恒等重写覆盖。
    /// 每类节点一个 Lowered 形态断言（LoweredDescribe 快照）+ 结构性断言
    /// （Origin 回指引用相等、字段符号引用相等）+ 未覆盖节点负例
    /// （测试私有 Bound 子类注入 → P4 Error + 跳过该函数体）。
    /// S7b 新增脱糖断言：bool 短路 and/or（BIL §11.3 if + 合成局部展开）、
    /// if 表达式（结果局部 + 值块降级）、复合赋值（前置赋值脱糖）。
    /// S7c-1 新增循环降级断言：Judge 块（条件求值移入、条件内短路展开
    /// 随块走）、合成 bool 条件局部 .sN 与 .breakid 局部 .bN（Type null）、
    /// do-while rev、BoundLoop → BreakId 映射命中（嵌套标签/值块穿透）。
    /// S7e 新增：cast 恒等降级（as/as? + Origin 回指）、try 降级（ExceptionSlot
    /// 复用 finally 变量或合成 .sN、有名 catch 体头合成 cast）、seq 双形态
    /// （语句恒等 / 表达式脱糖结果局部）。
    /// Stage B（return@/Value-Block Structured Exit 重构）：return@ 降级为
    /// LoweredStructuredExit 标记 + StructuredExitRouting route 展开断言
    /// （同 region 直 break / 跨 region route 局部 + dispatcher else-if 链 /
    /// 静死语句原位保留 / 原 S7e try-finally 拦截负例转正），硬不变量——
    /// routing 后 LoweredDescribe 全文不含 "StructuredExit"。
    /// S8c 新增：索引访问恒等降级（读/写/复合三形态共用 LoweredIndexExpression，
    /// 读写指令选择归 P4b）。
    /// 驱动仿 BinderTests.BindUnit：全管线 P1–P3 后直接进 Lowerer（不带 stdlib）。
    /// </summary>
    //
    // 测试组按类别分文件（partial class，仿 Bil/BilVerifier 分文件先例）：
    //   LowererTests.cs             —— RunAll 入口 + 共享驱动/断言 helper
    //   LowererTests.Basics.cs      —— 局部声明/赋值/语句/new/Origin 链/未覆盖节点负例
    //   LowererTests.ControlFlow.cs —— 短路/if 表达式/复合赋值/值块/循环/switch/throw
    //   LowererTests.TrySeq.cs      —— try/seq/值块编织
    //   LowererTests.Values.cs      —— cast/插值/?./if?/解构/is/typeOf
    //   LowererTests.Members.cs     —— 实例成员/索引访问
    //   LowererTests.EnumCases.cs   —— enum case 构造恒等降级/is .Case 槽透传（S11）
    public static partial class LowererTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        internal static ParallelSuiteRunner.SuiteSpec Spec { get; } = new("Lowerer",
        [
            (nameof(TestLocalDeclarations), TestLocalDeclarations),
            (nameof(TestAssignment), TestAssignment),
            (nameof(TestExpressionAndCallStatements), TestExpressionAndCallStatements),
            (nameof(TestUnaryAndFieldReference), TestUnaryAndFieldReference),
            (nameof(TestNew), TestNew),
            (nameof(TestOriginChain), TestOriginChain),
            (nameof(TestShortCircuit), TestShortCircuit),
            (nameof(TestIfExpressionLowering), TestIfExpressionLowering),
            (nameof(TestCompoundAssignmentLowering), TestCompoundAssignmentLowering),
            (nameof(TestValueBlockIfTransform), TestValueBlockIfTransform),
            (nameof(TestLoopLowering), TestLoopLowering),
            (nameof(TestLoopStructuredExitRouting), TestLoopStructuredExitRouting),
            (nameof(TestInstanceLowering), TestInstanceLowering),
            (nameof(TestForLoopLowering), TestForLoopLowering),
            (nameof(TestSwitchLowering), TestSwitchLowering),
            (nameof(TestThrowLowering), TestThrowLowering),
            (nameof(TestThrowLambdaLowering), TestThrowLambdaLowering),
            (nameof(TestElseIfChainTransform), TestElseIfChainTransform),
            (nameof(TestCastLowering), TestCastLowering),
            (nameof(TestTryLowering), TestTryLowering),
            (nameof(TestSeqLowering), TestSeqLowering),
            (nameof(TestUsingLowering), TestUsingLowering),
            (nameof(TestTryWeaving), TestTryWeaving),
            (nameof(TestStructuredExitRoutingForms), TestStructuredExitRoutingForms),
            (nameof(TestSameRegionTailExitElision), TestSameRegionTailExitElision),
            (nameof(TestInterpolationLowering), TestInterpolationLowering),
            (nameof(TestSafeAccessLowering), TestSafeAccessLowering),
            (nameof(TestNullFallbackLowering), TestNullFallbackLowering),
            (nameof(TestDestructuringLowering), TestDestructuringLowering),
            (nameof(TestTypeCheckLowering), TestTypeCheckLowering),
            (nameof(TestTypeOfLowering), TestTypeOfLowering),
            (nameof(TestDynamicNewLowering), TestDynamicNewLowering),
            (nameof(TestIndexLowering), TestIndexLowering),
            (nameof(TestSafeAccessPrefixInThenBlock), TestSafeAccessPrefixInThenBlock),
            (nameof(TestSafeAccessVoidCallStatement), TestSafeAccessVoidCallStatement),
            (nameof(TestCompoundAssignmentIndexMaterialization), TestCompoundAssignmentIndexMaterialization),
            (nameof(TestCompoundAssignmentGetterMaterialization), TestCompoundAssignmentGetterMaterialization),
            (nameof(TestVarArgsParameterType), TestVarArgsParameterType),
            (nameof(TestVarArgsIndexLowering), TestVarArgsIndexLowering),
            (nameof(TestValueChainDeepWrite), TestValueChainDeepWrite),
            (nameof(TestValueChainDeepWriteThisRoot), TestValueChainDeepWriteThisRoot),
            (nameof(TestValueChainCompoundWrite), TestValueChainCompoundWrite),
            (nameof(TestValueReceiverCallWriteback), TestValueReceiverCallWriteback),
            (nameof(TestValueChainStaticRootWrite), TestValueChainStaticRootWrite),
            (nameof(TestValueChainStaticRootCompound), TestValueChainStaticRootCompound),
            (nameof(TestValueChainStaticRootReceiverCall), TestValueChainStaticRootReceiverCall),
            (nameof(TestValueChainStaticRootSingleFieldWrite), TestValueChainStaticRootSingleFieldWrite),
            (nameof(TestValueChainWrappedStaticRootWrite), TestValueChainWrappedStaticRootWrite),
            (nameof(TestValueChainGetterOnlyIntermediateError), TestValueChainGetterOnlyIntermediateError),
            (nameof(TestValueChainConstIntermediateError), TestValueChainConstIntermediateError),
            (nameof(TestIndexResultFieldWriteRejected), TestIndexResultFieldWriteRejected),
            (nameof(TestClassGenericCallTypeArguments), TestClassGenericCallTypeArguments),
            (nameof(TestEnumCaseLowering), TestEnumCaseLowering),
            (nameof(TestAwaitLowering), TestAwaitLowering),
            (nameof(TestYieldLowering), TestYieldLowering),
            (nameof(TestLambdaLowering), TestLambdaLowering),
            (nameof(TestUnsupportedNode), TestUnsupportedNode),
        ], sectionTitle: "Lowerer", memoryMiB: 2048);

        // 多源文件经全管线（Parser → P1 → P2 → P3 → P4a）后取编译单元、
        // bound 函数体（Origin 对照用）与 lowered 函数体列表
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bound,
            IReadOnlyList<LoweredFunctionBody> Lowered) LowerUnit(params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            var bound = Binder.Bind(unit, decls);
            return (unit, bound, Lowerer.Lower(unit, bound));
        }

        // 带 stdlib 的全管线驱动（S7c-2：for 脱糖用例需要 core.collections
        // 协议与 .bootstrap 的 EnumerateInRange 注册）
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bound,
            IReadOnlyList<LoweredFunctionBody> Lowered) LowerUnitWithStdlib(
            params string[] sources)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.AddRange(sources.Select(TestHarness.ParseRoot));
            var unit = new CompilationUnit(roots.ToArray());
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            var bound = Binder.Bind(unit, decls);
            return (unit, bound, Lowerer.Lower(unit, bound));
        }

        private static void TestThrowLambdaLowering()
        {
            var (unit, _, bodies) = LowerUnitWithStdlib("""
                pub func main(): i32 {
                    const fail = func{ ():i32 -> { throw new core.RuntimeException("callback") }}
                    return 0
                }
                """);
            CheckNoErrors("直接 throw lambda 降级无诊断", unit);
            var call = bodies.Single(b => b.Method.Owner?.LambdaClosure is { } closure
                && ReferenceEquals(b.Method, closure.Call)
                && closure.ValueBlock is { ValueType: null });
            TestHarness.CheckTrue("全逃逸 lambda 不读取不存在的结果",
                call.Body.Statements.Count == 1
                && call.Body.Statements[0] is LoweredSeqBlock { Origin: BoundValueBlock { ValueType: null } });
        }
        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
        }

        private static LoweredFunctionBody BodyOf(IReadOnlyList<LoweredFunctionBody> bodies,
            string name)
        {
            return TestHarness.UniqueNamedBody(bodies, name, b => b.Method);
        }
    }
}
