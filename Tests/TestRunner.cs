using System;
using System.Collections.Generic;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 全量测试入口（test --all）：自动运行全部测试套件，
    /// 输出通过/失败总数与失败套件名；任意失败返回非零退出码。
    /// </summary>
    public static class TestRunner
    {
        // 套件注册表：名字 + 运行入口（返回失败用例数）
        private static readonly (string Name, Func<int> Run)[] Suites =
        {
            ("Literal", LiteralParserTests.RunAll),
            ("TypeReference", TypeReferenceParserTests.RunAll),
            ("VariableDeclaration", VariableDeclarationTests.RunAll),
            ("Expression", ExpressionParserTests.RunAll),
            ("GenericParsing", GenericParsingTests.RunAll),
            ("GenericParameters", GenericParametersTests.RunAll),
            ("ParameterList", ParameterListTests.RunAll),
            ("Lambda", LambdaExpressionTests.RunAll),
            ("IfExpression", IfExpressionTests.RunAll),
            ("SwitchExpression", SwitchExpressionTests.RunAll),
            ("TypeOf", TypeOfExpressionTests.RunAll),
            ("CodeBlock", CodeBlockTests.RunAll),
            ("Loop", LoopTests.RunAll),
            ("TryCatchFinally", TryCatchFinallyTests.RunAll),
            ("SeqBlock", SeqBlockTests.RunAll),
            ("Throw", ThrowStatementTests.RunAll),
            ("CoroutineOps", CoroutineOpsTests.RunAll),
            ("TypeDeclaration", TypeDeclarationTests.RunAll),
            ("PropertyAccessor", PropertyAccessorTests.RunAll),
            ("Import", ImportTests.RunAll),
            ("Namespace", NamespaceTests.RunAll),
            ("TokenDisposition", TokenDispositionTests.RunAll),
            ("ASTIntegrityValidator", ASTIntegrityValidatorTests.RunAll),
            ("LexerFuzz", LexerFuzzTests.RunAll),
            ("Logger", LoggerTests.RunAll),
            ("AstJsonlSerializer", AstJsonlSerializerTests.RunAll),
            ("CommandLineParser", CommandLineParserTests.RunAll),
            ("Path", PathParserLayerTests.RunAll),
            ("ArgumentList", ArgumentListParserLayerTests.RunAll),
            ("MultilineString", MultilineStringTests.RunAll),
            ("Diagnostics", DiagnosticsTests.RunAll),
            ("SymbolGraph", SymbolGraphTests.RunAll),
            ("CanonicalSymbolPrinter", CanonicalSymbolPrinterTests.RunAll),
            ("BilWriter", BilWriterTests.RunAll),
            ("BilVerifier", BilVerifierTests.RunAll),
            ("DeclarationCollector", DeclarationCollectorTests.RunAll),
            ("DeclarationResolver", DeclarationResolverTests.RunAll),
            ("Binder", BinderTests.RunAll),
            ("StdlibSources", StdlibSourcesTests.RunAll),
            ("BilEmitter", BilEmitterTests.RunAll),
            ("Lowerer", LowererTests.RunAll),
            ("SmartCast", SmartCastTests.RunAll),
            ("SemanticsFuzz", SemanticsFuzzTests.RunAll),
        };

        // 套件数量（对外编号 1..SuiteCount，即注册表顺序）
        public static int SuiteCount => Suites.Length;

        // 打印测试选项菜单（test 裸用 / test --run 不带编号时）
        public static void PrintMenu()
        {
            Console.WriteLine("可用测试套件（用 test --run <编号...> 运行，编号可多个、按顺序执行）：");
            for (int i = 0; i < Suites.Length; i++)
            {
                Console.WriteLine($"{i + 1}. {Suites[i].Name}");
            }
        }

        // 按编号（1 起）运行单个套件，返回失败用例数；编号越界由调用方校验
        public static int RunSuite(int number) => Suites[number - 1].Run();

        // 按编号依次运行多个套件，返回失败用例总数
        public static int RunSuites(IReadOnlyList<int> numbers)
        {
            int totalFail = 0;
            foreach (var n in numbers)
            {
                totalFail += RunSuite(n);
            }
            return totalFail;
        }

        // 运行全部套件，返回失败用例总数（0 = 全部通过）
        public static int RunAllSuites()
        {
            int totalFail = 0;
            var failedSuites = new List<string>();
            foreach (var (name, run) in Suites)
            {
                int fail = run();
                totalFail += fail;
                if (fail > 0) failedSuites.Add($"{name}({fail})");
            }
            Console.WriteLine("========================================");
            Console.WriteLine($"Test suites: {Suites.Length}, failed cases total: {totalFail}");
            if (failedSuites.Count > 0)
            {
                Console.WriteLine("Failed suites: " + string.Join(", ", failedSuites));
            }
            else
            {
                Console.WriteLine("ALL TESTS PASSED");
            }
            return totalFail;
        }
    }
}
