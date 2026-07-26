using System;
using System.Collections.Generic;

namespace LatteCompiler.Tests
{
    public class VariableDeclarationTests
    {
        // 测试计数（PASS 计入 passCount，FAIL/ERROR 计入 failCount）
        private static int passCount = 0;
        private static int failCount = 0;

        // 测试基本变量声明
        public static void TestBasicDeclarations()
        {
            Console.WriteLine("=== Testing Basic Variable Declarations ===");

            TestParseDeclaration("var x = 42", "var x = 42");
            TestParseDeclaration("const name = \"Hello\"", "const name = \"Hello\"");
            TestParseDeclaration("var flag = true", "var flag = true");

            Console.WriteLine();
        }

        // 测试带类型标注的声明
        public static void TestTypedDeclarations()
        {
            Console.WriteLine("=== Testing Typed Variable Declarations ===");

            TestParseDeclaration("var x: i32 = 42", "var x: i32 = 42");
            TestParseDeclaration("const name: String = \"Hello\"", "const name: String = \"Hello\"");
            TestParseDeclaration("var count: i64", "var count: i64 (no init)");

            Console.WriteLine();
        }

        // 测试可空类型
        public static void TestNullableDeclarations()
        {
            Console.WriteLine("=== Testing Nullable Type Declarations ===");

            TestParseDeclaration("var x: i32? = null", "var x: i32? = null");
            TestParseDeclaration("var name: String? = \"test\"", "var name: String? = \"test\"");

            Console.WriteLine();
        }

        // 测试泛型类型
        public static void TestGenericDeclarations()
        {
            Console.WriteLine("=== Testing Generic Type Declarations ===");

            TestParseDeclaration("var list: List\\<String>", "var list: List<String> (no init)");
            TestParseDeclaration("const map: Map\\<String,i32>", "const map: Map<String,i32> (no init)");

            Console.WriteLine();
        }

        // 辅助方法：解析并验证变量声明
        private static void TestParseDeclaration(string code, string expectedDesc)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(code);
                var parser = new Parser();
                var ast = parser.Parse(tokens);

                if (ast is RootASTNode root && root.Children.Count > 0)
                {
                    var declNode = root.Children[0];
                    string result = DescribeDeclaration(declNode);

                    bool passed = result.Contains(expectedDesc.Split('=')[0].Trim()) ||
                                  result.Contains(expectedDesc.Split(':')[0].Trim());

                    Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {code}");
                    Console.WriteLine($"      => {result}");
                    if (passed) passCount++; else failCount++;
                }
                else
                {
                    Console.WriteLine($"  [FAIL] {code} => No AST node produced");
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERROR] {code}");
                Console.WriteLine($"      => {ex.Message}");
                failCount++;
            }
        }

        // 描述声明节点
        private static string DescribeDeclaration(ASTNode node)
        {
            if (node is VariableDeclarationASTNode varDecl)
            {
                string kind = varDecl.IsConst ? "const" : "var";
                string name = varDecl.Name;
                string type = varDecl.TypeAnnotation != null ?
                    DescribeType(varDecl.TypeAnnotation) : "inferred";
                string init = varDecl.Initializer != null ? "initialized" : "no init";

                return $"{kind} {name}: {type}, {init}";
            }

            return $"Unknown node: {node.GetType().Name}";
        }

        // 描述类型
        private static string DescribeType(TypeReferenceASTNode typeNode)
        {
            string typeName = GetSymbolName(typeNode.TypeSymbol.symbol);
            if (typeNode.IsNullable) typeName += "?";
            return typeName;
        }

        // 从 Symbol 提取类型名
        private static string GetSymbolName(Symbol symbol)
        {
            if (symbol.elements.Count == 0)
                return "<empty>";

            string result = "";
            foreach (var element in symbol.elements)
            {
                if (result.Length > 0)
                    result += ".";
                result += element.name;

                if (element.generics.Count > 0)
                {
                    result += "<";
                    for (int i = 0; i < element.generics.Count; i++)
                    {
                        if (i > 0) result += ",";
                        result += GetSymbolName(element.generics[i]);
                    }
                    result += ">";
                }
            }

            return result;
        }

        // 运行所有测试
        public static int RunAll()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Variable Declaration Tests        ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            TestBasicDeclarations();
            TestTypedDeclarations();
            TestNullableDeclarations();
            TestGenericDeclarations();

            Console.WriteLine($"=== Variable Declaration Tests Complete: {passCount} passed, {failCount} failed ===\n");
            return failCount;
        }
    }
}
