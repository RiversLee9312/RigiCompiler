using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S5 P3 最小闭环测试（M41）：Binder 的 bound 形态与定型类型断言 + 结构性事实。
    /// 覆盖：字面量定型、局部变量声明（var 推断/显式标注/const）、值引用（局部/参数/
    /// 全局字段）、二元与一元 intrinsic 运算（结果类型维度）、赋值与 definite
    /// assignment 最小版、无重载直接调用（具名实参规范序重排）、宿主类型成员调用
    /// （类内裸名静态/实例拦截/多段路径类容器/遮蔽优先级）、new 构造、
    /// return 与「所有路径显式返回」、块作用域与遮蔽、诊断互不阻断。
    /// S7c-1 增补：while/do-while 绑定形态、循环条件 bool 检查、for 拦截
    /// （S7c-2）、break/continue 标签栈解析（无标签栈顶/named 穿透/循环外与
    /// 未定义标签诊断）、值块内穿透、definite assignment 循环规则、
    /// GuaranteesReturn 循环保守。
    /// S8c 增补：索引访问（getAtIndex/setAtIndex 读写/复合赋值/段索引/
    /// 双重索引/this 索引）、表达式底座路径（分组/调用/new 构造/SafeDot
    /// 调用底座）与负例矩阵（无运算符/多参数/具名不匹配/nullable/类型
    /// 不匹配/void/重载归口/索引非值）。
    /// S8e 增补（BinderTests.Access.cs）：使用点访问控制（§16.1 多文件
    /// pub/priv/protected/internal 矩阵 + priv init 构造拦截 + priv 字段
    /// 读写）、访问器绑定（§9.4.1——读写节点形态不变/访问器体绑定与合成/
    /// 读写存在性与可见性/value 别名/smart cast 不收窄；M107 局部访问器路线 C）、
    /// override 配套（§9.2.1 正例与逐条负例 + new abstract）。
    /// 诊断断言沿用消息子串惯例（CheckSemanticError）；符号比较一律引用相等。
    /// </summary>
    public static partial class BinderTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        internal static ParallelSuiteRunner.SuiteSpec Spec { get; } = new("Binder",
        [
            (nameof(TestLiterals), TestLiterals),
            (nameof(TestLocalDeclarations), TestLocalDeclarations),
            (nameof(TestDeclInitFailurePoisonSilence), TestDeclInitFailurePoisonSilence),
            (nameof(TestValueReferences), TestValueReferences),
            (nameof(TestGlobalFieldInitializerBan), TestGlobalFieldInitializerBan),
            (nameof(TestBinaryOperators), TestBinaryOperators),
            (nameof(TestUserEqualityOperators), TestUserEqualityOperators),
            (nameof(TestUnaryOperators), TestUnaryOperators),
            (nameof(TestAssignments), TestAssignments),
            (nameof(TestCalls), TestCalls),
            (nameof(TestHostTypeMembers), TestHostTypeMembers),
            (nameof(TestNew), TestNew),
            (nameof(TestInitMappingSynthesis), TestInitMappingSynthesis),
            (nameof(TestReturn), TestReturn),
            (nameof(TestScopes), TestScopes),
            (nameof(TestIfStatements), TestIfStatements),
            (nameof(TestIfExpressions), TestIfExpressions),
            (nameof(TestBranchDefiniteAssignment), TestBranchDefiniteAssignment),
            (nameof(TestCompoundAssignments), TestCompoundAssignments),
            (nameof(TestLoops), TestLoops),
            (nameof(TestLoopControl), TestLoopControl),
            (nameof(TestInstanceMembers), TestInstanceMembers),
            (nameof(TestIndexAccess), TestIndexAccess),
            (nameof(TestContainerCallSuffixChain), TestContainerCallSuffixChain),
            (nameof(TestForLoops), TestForLoops),
            (nameof(TestSwitch), TestSwitch),
            (nameof(TestThrow), TestThrow),
            (nameof(TestCast), TestCast),
            (nameof(TestTry), TestTry),
            (nameof(TestSeqStatements), TestSeqStatements),
            (nameof(TestSeqReturnAnalysis), TestSeqReturnAnalysis),
            (nameof(TestSeqExpressions), TestSeqExpressions),
            (nameof(TestSeqExpectedTypes), TestSeqExpectedTypes),
            (nameof(TestSeqUsingStatements), TestSeqUsingStatements),
            (nameof(TestSeqUsingExpressions), TestSeqUsingExpressions),
            (nameof(TestSeqUsingExpressionDiagnostics), TestSeqUsingExpressionDiagnostics),
            (nameof(TestSeqReturnBoundaries), TestSeqReturnBoundaries),
            (nameof(TestUnsafeContexts), TestUnsafeContexts),
            (nameof(TestSeqExit), TestSeqExit),
            (nameof(TestStringInterpolation), TestStringInterpolation),
            (nameof(TestSafeAccess), TestSafeAccess),
            (nameof(TestNullFallback), TestNullFallback),
            (nameof(TestDestructuring), TestDestructuring),
            (nameof(TestTypeCheck), TestTypeCheck),
            (nameof(TestTypeOf), TestTypeOf),
            (nameof(TestDefaultParameters), TestDefaultParameters),
            (nameof(TestOverloadResolution), TestOverloadResolution),
            (nameof(TestAccessControl), TestAccessControl),
            (nameof(TestUseSiteAccessibilityF1), TestUseSiteAccessibilityF1),
            (nameof(TestProbedTypeChecksF2), TestProbedTypeChecksF2),
            (nameof(TestAccessors), TestAccessors),
            (nameof(TestLocalAccessors), TestLocalAccessors),
            (nameof(TestOverride), TestOverride),
            (nameof(TestSuperCalls), TestSuperCalls),
            (nameof(TestDefaultConstructorSynthesis), TestDefaultConstructorSynthesis),
            (nameof(TestConversionOperators), TestConversionOperators),
            (nameof(TestAsyncGates), TestAsyncGates),
            (nameof(TestYieldBinding), TestYieldBinding),
            (nameof(TestAsyncResultTypes), TestAsyncResultTypes),
            (nameof(TestAwaitBinding), TestAwaitBinding),
            (nameof(TestGenericCalls), TestGenericCalls),
            (nameof(TestIndirectGenericCalls), TestIndirectGenericCalls),
            (nameof(TestGenericVarArgs), TestGenericVarArgs),
            (nameof(TestGenericInference), TestGenericInference),
            (nameof(TestOperatorNameCalls), TestOperatorNameCalls),
            (nameof(TestUserOperatorPositions), TestUserOperatorPositions),
            (nameof(TestGenericFunctionBody), TestGenericFunctionBody),
            (nameof(TestGenericParamEffectiveMembers), TestGenericParamEffectiveMembers),
            (nameof(TestNamedGenericImportUsage), TestNamedGenericImportUsage),
            (nameof(TestDynamicNew), TestDynamicNew),
            (nameof(TestInitFieldDa), TestInitFieldDa),
            (nameof(TestGenericBaseClassMemberLookup), TestGenericBaseClassMemberLookup),
            (nameof(TestGenericVarianceAssignability), TestGenericVarianceAssignability),
            (nameof(TestCallFixes), TestCallFixes),
            (nameof(TestAsyncGateVariadicPacks), TestAsyncGateVariadicPacks),
            (nameof(TestGenericBackingAccessors), TestGenericBackingAccessors),
            (nameof(TestPerCandidateConstraints), TestPerCandidateConstraints),
            (nameof(TestAmbiguityWinnersAndPackSyntax), TestAmbiguityWinnersAndPackSyntax),
            (nameof(TestSharedInterfaceContagion), TestSharedInterfaceContagion),
            (nameof(TestInstantiationFillInP3), TestInstantiationFillInP3),
            (nameof(TestGenericBoundSharedSafeDerivation), TestGenericBoundSharedSafeDerivation),
            (nameof(TestThisSelfConstructed), TestThisSelfConstructed),
            (nameof(TestNestedGenericFieldIdentity), TestNestedGenericFieldIdentity),
            (nameof(TestGenericFieldSubstitution), TestGenericFieldSubstitution),
            (nameof(TestForEachConstructedInterface), TestForEachConstructedInterface),
            (nameof(TestCovariantInitUsage), TestCovariantInitUsage),
            (nameof(TestConstructedTypeStaticMembers), TestConstructedTypeStaticMembers),
            (nameof(TestGenericNullableFixes), TestGenericNullableFixes),
            (nameof(TestExplicitSharedGenerics), TestExplicitSharedGenerics),
            (nameof(TestIfMergeNoRevive), TestIfMergeNoRevive),
            (nameof(TestLoopExitNoRevive), TestLoopExitNoRevive),
            (nameof(TestTryNarrowing), TestTryNarrowing),
            (nameof(TestNullLiteralContext), TestNullLiteralContext),
            (nameof(TestValueBlockBareReturn), TestValueBlockBareReturn),
            (nameof(TestNestedValueBlockReturnValue), TestNestedValueBlockReturnValue),
            (nameof(TestInterfaceDefaultOnConcrete), TestInterfaceDefaultOnConcrete),
            (nameof(TestAssignmentLhsSmartCast), TestAssignmentLhsSmartCast),
            (nameof(TestBareFieldCallReceiver), TestBareFieldCallReceiver),
            (nameof(TestUnqualifiedOverrideShadowing), TestUnqualifiedOverrideShadowing),
            (nameof(TestOverrideFixes), TestOverrideFixes),
            (nameof(TestFieldOverrideRules), TestFieldOverrideRules),
            (nameof(TestEnumCases), TestEnumCases),
            (nameof(TestDiagnosticsAccumulation), TestDiagnosticsAccumulation),
            (nameof(TestKwArgsBodyView), TestKwArgsBodyView),
            (nameof(TestWrapperPlaceBinding), TestWrapperPlaceBinding),
            (nameof(TestWrapperPlaceReadOnly), TestWrapperPlaceReadOnly),
            (nameof(TestStaticMethodCompanionBinding), TestStaticMethodCompanionBinding),
            (nameof(TestWrapperInitArgBinding), TestWrapperInitArgBinding),
            (nameof(TestWrapperPlaceErrors), TestWrapperPlaceErrors),
            (nameof(TestValueWrapperGetOnlyLocal), TestValueWrapperGetOnlyLocal),
            (nameof(TestWrapperPlaceLowering), TestWrapperPlaceLowering),
            (nameof(TestProxyBodyBinding), TestProxyBodyBinding),
            (nameof(TestProxyGenericParamTypeRefs), TestProxyGenericParamTypeRefs),
            (nameof(TestInnerCallGenericPackForwarding), TestInnerCallGenericPackForwarding),
            (nameof(TestGenericParamWithWrapperPlace), TestGenericParamWithWrapperPlace),
            (nameof(TestDowngradeBinding), TestDowngradeBinding),
            (nameof(TestWrapperFieldInitializers), TestWrapperFieldInitializers),
            (nameof(TestGenericParameterFieldInitializers), TestGenericParameterFieldInitializers),
            (nameof(TestForwardDefaultConstructionDa), TestForwardDefaultConstructionDa),
            (nameof(TestWrapperDefaultConstructionDa), TestWrapperDefaultConstructionDa),
            (nameof(TestWrapperPlaceVoidStatement), TestWrapperPlaceVoidStatement),
            (nameof(TestGetProxyInnerForbidden), TestGetProxyInnerForbidden),
            (nameof(TestSubclassWrapperInheritedShape), TestSubclassWrapperInheritedShape),
            (nameof(TestMethodWrapperSpecificCallShape), TestMethodWrapperSpecificCallShape),
            (nameof(TestLambdaBinding), TestLambdaBinding),
            (nameof(TestVoidLambdaExpressionBodyStatementSemantics), TestVoidLambdaExpressionBodyStatementSemantics),
            (nameof(TestNamedImportValueConsumption), TestNamedImportValueConsumption),
            (nameof(TestMw11cCoroutineShapes), TestMw11cCoroutineShapes),
            (nameof(TestMw11dSerializationFront), TestMw11dSerializationFront),
            (nameof(TestPlaceOfStorage), TestPlaceOfStorage),
        ], sectionTitle: "Binder", memoryMiB: 2048);

        // 多源文件经全管线（Parser → P1 → P2 → P3）后取编译单元与 bound 函数体列表
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bodies) BindUnit(
            params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return (unit, Binder.Bind(unit, decls));
        }

        // 带 stdlib 的全管线驱动（S7c-2：for 协议与 ext operator 用例需要
        // core.collections 与 .bootstrap 的 EnumerateInRange 注册）
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bodies)
            BindUnitWithStdlib(params string[] sources)
            => BindUnitWithStdlibSources(false, sources);

        // 标准库内部测试显式标记可信 AST，用户源测试不授予此权限。
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bodies)
            BindUnitWithStdlibSources(bool compilerLibraryFixture, params string[] sources)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.AddRange(sources.Select(source => {
                var root = TestHarness.ParseRoot(source);
                root.IsCompilerLibrary = compilerLibraryFixture;
                return root;
            }));
            var unit = new CompilationUnit(roots.ToArray());
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return (unit, Binder.Bind(unit, decls));
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => $"{d.Phase}: {d.Message}")));
        }

        private static BoundFunctionBody BodyOf(IReadOnlyList<BoundFunctionBody> bodies, string name)
        {
            return TestHarness.UniqueNamedBody(bodies, name, b => b.Method);
        }

    }
}
