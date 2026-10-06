namespace RigiCompiler.Tests;

// 明确的方法组引用兼容 NativeAOT；目录与执行共享 provider，禁止反射发现私有方法。
internal static class StaticTestProviders
{
    internal static IReadOnlyList<string>? GroupLabels(string suite, string group) => suite switch
    {
        "Binder" => BinderTests.GroupLabels(group),
        "BilEmitter" => BilEmitterTests.GroupLabels(group),
        "CommandLineParser" => CommandLineParserTests.GroupLabels,
        _ => null,
    };

    internal static TestSuiteData? Find(string name) => name switch
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
