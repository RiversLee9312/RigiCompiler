using System;
using System.Collections.Generic;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 全量测试入口（test --all）：自动运行全部测试套件，
    /// 输出通过/失败总数与失败套件名；任意失败返回非零退出码。
    /// </summary>
    public static class TestRunner
    {
        // 套件注册表：名字 + 无参入口 + 可选带参入口（未实现则忽略 --suite-args）
        private static readonly (string Name, Func<int> Run, Func<IReadOnlyList<string>, int>? RunWithArgs)[] Suites =
        {
            ("Literal", LiteralParserTests.RunAll, null),
            ("TypeReference", TypeReferenceParserTests.RunAll, null),
            ("VariableDeclaration", VariableDeclarationTests.RunAll, null),
            ("Expression", ExpressionParserTests.RunAll, null),
            ("GenericParsing", GenericParsingTests.RunAll, null),
            ("GenericParameters", GenericParametersTests.RunAll, null),
            ("ParameterList", ParameterListTests.RunAll, null),
            ("Lambda", LambdaExpressionTests.RunAll, null),
            ("IfExpression", IfExpressionTests.RunAll, null),
            ("SwitchExpression", SwitchExpressionTests.RunAll, null),
            ("TypeOf", TypeOfExpressionTests.RunAll, null),
            ("CodeBlock", CodeBlockTests.RunAll, null),
            ("Loop", LoopTests.RunAll, null),
            ("TryCatchFinally", TryCatchFinallyTests.RunAll, null),
            ("SeqBlock", SeqBlockTests.RunAll, null),
            ("Throw", ThrowStatementTests.RunAll, null),
            ("CoroutineOps", CoroutineOpsTests.RunAll, null),
            ("TypeDeclaration", TypeDeclarationTests.RunAll, null),
            ("PropertyAccessor", PropertyAccessorTests.RunAll, null),
            ("Import", ImportTests.RunAll, null),
            ("Namespace", NamespaceTests.RunAll, null),
            ("TokenDisposition", TokenDispositionTests.RunAll, null),
            ("ASTIntegrityValidator", ASTIntegrityValidatorTests.RunAll, null),
            ("LexerFuzz", LexerFuzzTests.RunAll, null),
            ("Logger", LoggerTests.RunAll, null),
            ("AstJsonlSerializer", AstJsonlSerializerTests.RunAll, null),
            ("CommandLineParser", CommandLineParserTests.RunAll, null),
            ("Path", PathParserLayerTests.RunAll, null),
            ("ArgumentList", ArgumentListParserLayerTests.RunAll, null),
            ("MultilineString", MultilineStringTests.RunAll, null),
            ("Diagnostics", DiagnosticsTests.RunAll, null),
            ("SymbolGraph", SymbolGraphTests.RunAll, null),
            ("CanonicalSymbolPrinter", CanonicalSymbolPrinterTests.RunAll, null),
            ("BilWriter", BilWriterTests.RunAll, null),
            ("BilVerifier", BilVerifierTests.RunAll, null),
            ("DeclarationCollector", DeclarationCollectorTests.RunAll, null),
            ("DeclarationResolver", DeclarationResolverTests.RunAll, null),
            ("Binder", BinderTests.RunAll, null),
            ("StdlibSources", StdlibSourcesTests.RunAll, null),
            ("BilEmitter", BilEmitterTests.RunAll, null),
            ("Lowerer", LowererTests.RunAll, null),
            ("SmartCast", SmartCastTests.RunAll, null),
            ("SemanticsFuzz", SemanticsFuzzTests.RunAll, SemanticsFuzzTests.RunWithArgs),
            ("StressFuzz", StressFuzzTests.RunAll, StressFuzzTests.RunWithArgs),
            ("DispatchExplainer", DispatchExplainerTests.RunAll, DispatchExplainerTests.RunWithArgs),
            ("BilVm", BilVmTests.RunAll, BilVmTests.RunWithArgs),
            ("BilVmDispatch", BilVmDispatchTests.RunAll, BilVmDispatchTests.RunWithArgs),
            ("BilVmWakeup", BilVmWakeupTests.RunAll, BilVmWakeupTests.RunWithArgs),
            ("VmPrimitive", VmPrimitiveTests.RunAll, VmPrimitiveTests.RunWithArgs),
            ("BilReader", BilReaderTests.RunAll, BilReaderTests.RunWithArgs),
            ("BilVmStress", BilVmStressTests.RunAll, BilVmStressTests.RunWithArgs),
            ("EscapingSeqExpr", EscapingSeqExprTests.RunAll, EscapingSeqExprTests.RunWithArgs),
            ("EscapingValueBlock", EscapingValueBlockTests.RunAll, EscapingValueBlockTests.RunWithArgs),
            ("SeqRouteHint", SeqRouteHintTests.RunAll, SeqRouteHintTests.RunWithArgs),
            ("EscapingSeqPosition", EscapingSeqPositionTests.RunAll, EscapingSeqPositionTests.RunWithArgs),
            ("E2e", E2eCorpusTests.RunAll, E2eCorpusTests.RunWithArgs),
            ("Middleware", MiddlewareTests.RunAll, MiddlewareTests.RunWithArgs),
            ("NativeE2E", NativeE2ETests.RunAll, NativeE2ETests.RunWithArgs),
            ("BilVmTask", BilVmTaskTests.RunAll, BilVmTaskTests.RunWithArgs),
        };

        // 套件数量（对外编号 1..SuiteCount，即注册表顺序）
        public static int SuiteCount => Suites.Length;

        // --spawned 状态：由 TestCommand 在执行前设置；fuzz 套件据此判断
        // 自己是否已是被派生的子进程（是则无论区间多大都在进程内跑完，禁止再 spawn）
        public static bool IsSpawned { get; set; }

        // 按注册名找套件编号（1 起）；找不到返回 -1。并行 fuzz 用名字寻址，
        // 避免硬编码套件号在注册表顺序调整后失效。
        public static int GetSuiteNumber(string name)
        {
            for (int i = 0; i < Suites.Length; i++)
            {
                if (Suites[i].Name == name) return i + 1;
            }
            return -1;
        }

        // 打印测试选项菜单（test 裸用 / test --run 不带编号时）
        public static void PrintMenu()
        {
            Console.WriteLine("可用测试套件（用 test --run <编号...> 运行，编号可多个、按顺序执行）：");
            for (int i = 0; i < Suites.Length; i++)
            {
                Console.WriteLine($"{i + 1}. {Suites[i].Name}");
            }
            Console.WriteLine("可选: test --run <编号> --suite-args <值...> 传参给支持的套件（未实现则忽略）");
        }

        // 按编号（1 起）运行单个套件，返回失败用例数；编号越界由调用方校验。
        // 带 --suite-args 时优先走 RunWithArgs；未实现则忽略参数。
        public static int RunSuite(int number, IReadOnlyList<string>? args = null)
        {
            var suite = Suites[number - 1];
            if (args is { Count: > 0 } && suite.RunWithArgs != null)
                return suite.RunWithArgs(args);
            if (args is { Count: > 0 })
                Console.WriteLine($"  （套件 {suite.Name} 不接受 --suite-args，已忽略）");
            return suite.Run();
        }

        // 按编号依次运行多个套件，返回失败用例总数
        public static int RunSuites(IReadOnlyList<int> numbers, IReadOnlyList<string>? args = null)
        {
            int totalFail = 0;
            foreach (var n in numbers)
            {
                totalFail += RunSuite(n, args);
            }
            return totalFail;
        }

        // 运行全部套件，返回失败用例总数（0 = 全部通过）
        public static int RunAllSuites(IReadOnlyList<string>? args = null)
        {
            int totalFail = 0;
            var failedSuites = new List<string>();
            foreach (var suite in Suites)
            {
                int fail = args is { Count: > 0 } && suite.RunWithArgs != null
                    ? suite.RunWithArgs(args)
                    : suite.Run();
                totalFail += fail;
                if (fail > 0) failedSuites.Add($"{suite.Name}({fail})");
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
