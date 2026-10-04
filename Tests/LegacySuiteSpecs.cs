namespace RigiCompiler.Tests;

// 明确的方法组引用兼容 NativeAOT；目录与执行共享 provider，禁止反射发现私有方法。
internal static class LegacySuiteSpecs
{
    internal static IReadOnlyList<string>? GroupLabels(string suite, string group) => suite switch
    {
        "Binder" => BinderTests.GroupLabels(group),
        "BilEmitter" => BilEmitterTests.GroupLabels(group),
        "CommandLineParser" => CommandLineParserTests.GroupLabels,
        _ => null,
    };

    // 每个动作进入自己的计数生命周期；异常前的真实断言也必须进入 worker 结果。
    internal static ParallelSuiteRunner.SuiteSpec Counted(string name,
        IReadOnlyList<(string Label, Action Run)> cases, Action reset, Func<(int Passed, int Failed)> counts)
        => new(name, cases.Select(c => (c.Label, (Action)(() =>
        {
            reset();
            try { c.Run(); }
            finally
            {
                var result = counts();
                TestHarness.AddCounts(result.Passed, result.Failed);
            }
        }))).ToArray(), sectionTitle: name);

    internal static ParallelSuiteRunner.SuiteSpec? Find(string name) => name switch
    {
        "Literal" => LiteralParserTests.Spec,
        "TypeReference" => TypeReferenceParserTests.Spec,
        "VariableDeclaration" => VariableDeclarationTests.Spec,
        "Expression" => ExpressionParserTests.Spec,
        "GenericParsing" => GenericParsingTests.Spec,
        "GenericParameters" => GenericParametersTests.Spec,
        "ParameterList" => ParameterListTests.Spec,
        "Lambda" => LambdaExpressionTests.Spec,
        "IfExpression" => IfExpressionTests.Spec,
        "SwitchExpression" => SwitchExpressionTests.Spec,
        "TypeOf" => TypeOfExpressionTests.Spec,
        "CodeBlock" => CodeBlockTests.Spec,
        "Loop" => LoopTests.Spec,
        "TryCatchFinally" => TryCatchFinallyTests.Spec,
        "SeqBlock" => SeqBlockTests.Spec,
        "Throw" => ThrowStatementTests.Spec,
        "CoroutineOps" => CoroutineOpsTests.Spec,
        "TypeDeclaration" => TypeDeclarationTests.Spec,
        "PropertyAccessor" => PropertyAccessorTests.Spec,
        "Import" => ImportTests.Spec,
        "Namespace" => NamespaceTests.Spec,
        "ASTIntegrityValidator" => ASTIntegrityValidatorTests.Spec,
        "AstJsonlSerializer" => AstJsonlSerializerTests.Spec,
        "Path" => PathParserLayerTests.Spec,
        "ArgumentList" => ArgumentListParserLayerTests.Spec,
        "MultilineString" => MultilineStringTests.Spec,
        "DeclarationCollector" => DeclarationCollectorTests.Spec,
        "DeclarationResolver" => DeclarationResolverTests.Spec,
        "Binder" => BinderTests.Spec,
        "StdlibSources" => StdlibSourcesTests.Spec,
        "BilEmitter" => BilEmitterTests.Spec,
        "Lowerer" => LowererTests.Spec,
        "SmartCast" => SmartCastTests.Spec,
        "CompilerParallel" => CompilerParallelTests.Spec,
        "TokenDisposition" => TokenDispositionTests.Spec,
        "Logger" => LoggerTests.Spec,
        "CommandLineParser" => CommandLineParserTests.Spec,
        "LexerFuzz" => LexerFuzzTests.Spec,
        "BilWriter" => BilWriterTests.Spec,
        "SymbolGraph" => SymbolGraphTests.Spec,
        "CanonicalSymbolPrinter" => CanonicalSymbolPrinterTests.Spec,
        "Diagnostics" => DiagnosticsTests.Spec,
        "NativeE2EArgs" => NativeE2EArgsParseTests.Spec,
        "BilVerifier" => BilVerifierTests.Spec,
        "PerformanceMetrics" => PerformanceMetricsTests.Spec,
        _ => null,
    };
}
