using System;
using System.Collections.Generic;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 全量测试入口（--test-all）：自动运行全部测试套件，
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
        };

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
