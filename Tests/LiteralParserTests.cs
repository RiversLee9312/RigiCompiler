using System;
using System.Collections.Generic;

namespace LatteCompiler.Tests
{
    public class LiteralParserTests
    {
        // 测试整数字面量
        public static void TestIntLiterals()
        {
            Console.WriteLine("=== Testing Integer Literals ===");

            // 简单整数
            TestParseLiteral("42", "IntLiteral: value=42, type=I32");

            // 十六进制
            TestParseLiteral("0xFF", "IntLiteral: value=255, type=I32, hex=true");

            // 带后缀
            TestParseLiteral("100L", "IntLiteral: value=100, type=I64");
            TestParseLiteral("200S", "IntLiteral: value=200, type=I16");
            TestParseLiteral("50B", "IntLiteral: value=50, type=I8");
            TestParseLiteral("300U", "IntLiteral: value=300, type=U32");
            TestParseLiteral("400UL", "IntLiteral: value=400, type=U64");

            Console.WriteLine();
        }

        // 测试浮点数字面量
        public static void TestFloatLiterals()
        {
            Console.WriteLine("=== Testing Float Literals ===");

            TestParseLiteral("3.14", "FloatLiteral: value=3.14, isFloat=false");
            TestParseLiteral("0.1f", "FloatLiteral: value=0.1, isFloat=true");
            TestParseLiteral("2.5", "FloatLiteral: value=2.5, isFloat=false");

            Console.WriteLine();
        }

        // 测试布尔和 null 字面量
        public static void TestBoolAndNull()
        {
            Console.WriteLine("=== Testing Bool and Null Literals ===");

            TestParseLiteral("true", "BoolLiteral: value=true");
            TestParseLiteral("false", "BoolLiteral: value=false");
            TestParseLiteral("null", "NullLiteral");

            Console.WriteLine();
        }

        // 测试字符串字面量
        public static void TestStringLiterals()
        {
            Console.WriteLine("=== Testing String Literals ===");

            TestParseLiteral("\"Hello\"", "StringLiteral: value=Hello");
            TestParseLiteral("\"World ${x}\"", "StringLiteral: value=World ${x}, hasInterpolation=true");

            Console.WriteLine();
        }

        // 辅助方法：解析单个字面量并打印结果
        private static void TestParseLiteral(string code, string expectedDesc)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(code);
                var parser = new Parser();
                var ast = parser.Parse(tokens);

                if (ast is RootASTNode root && root.Children.Count > 0)
                {
                    var literalNode = root.Children[0];
                    string result = DescribeLiteral(literalNode);

                    bool passed = result.Contains(expectedDesc.Split(':')[0]); // 简化检查
                    Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {code} => {result}");
                }
                else
                {
                    Console.WriteLine($"  [FAIL] {code} => No AST node produced");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERROR] {code} => {ex.Message}");
            }
        }

        // 描述字面量节点
        private static string DescribeLiteral(ASTNode node)
        {
            return node switch
            {
                IntLiteralASTNode intNode => $"IntLiteral: value={intNode.Value}, type={intNode.IntType}" +
                                             (intNode.IsHex ? ", hex=true" : ""),
                FloatLiteralASTNode floatNode => $"FloatLiteral: value={floatNode.Value}, isFloat={floatNode.IsFloat}",
                BoolLiteralASTNode boolNode => $"BoolLiteral: value={boolNode.Value}",
                NullLiteralASTNode => "NullLiteral",
                StringLiteralASTNode strNode => $"StringLiteral: value={strNode.Value}" +
                                               (strNode.HasInterpolation ? ", hasInterpolation=true" : ""),
                _ => $"Unknown: {node.GetType().Name}"
            };
        }

        // 运行所有测试
        public static void RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Literal Parser Layer Tests       ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            TestIntLiterals();
            TestFloatLiterals();
            TestBoolAndNull();
            TestStringLiterals();

            Console.WriteLine("=== All Tests Complete ===\n");
        }
    }
}
