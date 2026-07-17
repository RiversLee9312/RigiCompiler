using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 函数形参列表解析测试（ParameterListParserLayer，roadmap #5）
    ///
    /// 覆盖 SYNTAX.md §4 的形参语法：
    /// 1. 普通参数（含泛型类型）
    /// 2. 默认参数（含表达式默认值）
    /// 3. 位置可变参数 i32...
    /// 4. 具名可变参数 named String...
    /// 5. 混合与空列表
    /// 6. 错误用例：缺类型标注、缺类型、点数不足、缺默认值
    ///
    /// 测试驱动方式：通过 Parser.Parse(tokens, entryLayer) 重载，
    /// 以 ParameterListParserLayer 为起始层独立解析 (...) 片段。
    /// </summary>
    public class ParameterListTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 普通参数 =====
        public static void TestPlainParameters()
        {
            Console.WriteLine("=== Testing Plain Parameters ===");

            TestParse("(a: i32)", "[a: i32]");
            TestParse("(a: i32, b: String)", "[a: i32, b: String]");
            // 泛型类型参数
            TestParse("(a: List\\<i32>)", "[a: List<i32>]");
            // 可空类型参数
            TestParse("(name: String?)", "[name: String?]");

            Console.WriteLine();
        }

        // ===== 2. 默认参数 =====
        public static void TestDefaultParameters()
        {
            Console.WriteLine("=== Testing Default Parameters ===");

            TestParse("(name: String = \"World\")", "[name: String = Str(\"World\")]");
            // 表达式作为默认值
            TestParse("(a: i32 = 1 + 2)", "[a: i32 = Binary(Int(1,I32) + Int(2,I32))]");

            Console.WriteLine();
        }

        // ===== 3. 可变参数 =====
        public static void TestVariadicParameters()
        {
            Console.WriteLine("=== Testing Variadic Parameters ===");

            TestParse("(numbers: i32...)", "[numbers: i32...]");
            TestParse("(options: named String...)", "[options: named String...]");

            Console.WriteLine();
        }

        // ===== 4. 混合与空列表 =====
        public static void TestMixedParameters()
        {
            Console.WriteLine("=== Testing Mixed and Empty Parameters ===");

            TestParse("()", "[]");
            TestParse("(a: i32, b: String = \"x\", rest: named i32...)",
                "[a: i32, b: String = Str(\"x\"), rest: named i32...]");

            Console.WriteLine();
        }

        // ===== 5. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Error Cases (expect ParserException) ===");

            TestError("(a)", "缺少类型标注");
            TestError("(a: )", "缺少类型");
            TestError("(a: i32..)", "可变参数点数不足");
            TestError("(a: i32 = )", "缺少默认值表达式");

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        private static ParameterListASTNode ParseParameterList(string code)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(code);
            var node = new ParameterListASTNode(null);
            var parser = new Parser();
            parser.Parse(tokens, new ParameterListParserLayer(node));
            return node;
        }

        private static void TestParse(string code, string expectedDesc)
        {
            try
            {
                var node = ParseParameterList(code);
                string actual = Describe(node);
                if (actual == expectedDesc)
                {
                    Console.WriteLine($"  [PASS] {code}");
                    Console.WriteLine($"      => {actual}");
                    passCount++;
                }
                else
                {
                    Fail(code, $"expected {expectedDesc}, got {actual}");
                }
            }
            catch (Exception ex)
            {
                Fail(code, $"unexpected exception: {ex.Message}");
            }
        }

        private static void TestError(string code, string reason)
        {
            try
            {
                ParseParameterList(code);
                Fail(code, $"expected ParserException ({reason}), but parse succeeded");
            }
            catch (ParserException)
            {
                Console.WriteLine($"  [PASS] {code}  (rejected: {reason})");
                passCount++;
            }
            catch (Exception ex)
            {
                Fail(code, $"expected ParserException ({reason}), got {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Fail(string code, string message)
        {
            Console.WriteLine($"  [FAIL] {code}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== AST 描述 =====

        private static string Describe(ParameterListASTNode node)
        {
            return "[" + string.Join(", ", node.Parameters.Select(DescribeParameter)) + "]";
        }

        private static string DescribeParameter(ParameterASTNode param)
        {
            string prefix = param.IsNamedVariadic ? "named " : "";
            string suffix = (param.IsVariadic || param.IsNamedVariadic) ? "..." : "";
            string desc = $"{param.Name}: {prefix}{DescribeType(param.Type)}{suffix}";
            if (param.DefaultValue != null)
            {
                desc += $" = {DescribeExpression(param.DefaultValue)}";
            }
            return desc;
        }

        private static string DescribeType(TypeReferenceASTNode typeNode)
        {
            string typeName = DescribeSymbol(typeNode.TypeSymbol.symbol);
            if (typeNode.IsNullable) typeName += "?";
            return typeName;
        }

        private static string DescribeSymbol(Symbol symbol)
        {
            var parts = new List<string>();
            foreach (var element in symbol.elements)
            {
                string part = element.name;
                if (element.generics.Count > 0)
                {
                    part += "<" + string.Join(",", element.generics.Select(g => DescribeSymbol(g))) + ">";
                }
                parts.Add(part);
            }
            return string.Join(".", parts);
        }

        private static string DescribeExpression(ASTNode? node)
        {
            return node switch
            {
                null => "<null>",
                LiteralExpressionASTNode lit => DescribeExpression(lit.LiteralNode),
                IntLiteralASTNode i => $"Int({i.Value},{i.IntType}{(i.IsHex ? ",hex" : "")})",
                FloatLiteralASTNode f => $"Float({f.Value}{(f.IsFloat ? "f" : "")})",
                StringLiteralASTNode s => $"Str(\"{s.Value}\")",
                BoolLiteralASTNode b => $"Bool({b.Value})",
                NullLiteralASTNode => "Null",
                SymbolReferenceASTNode sref => $"Sym({DescribeSymbol(sref.Symbol.symbol)})",
                UnaryExpressionASTNode u => $"Unary({u.Operator} {DescribeExpression(u.Operand)})",
                BinaryExpressionASTNode b =>
                    $"Binary({DescribeExpression(b.Left)} {b.Operator} {DescribeExpression(b.Right)})",
                GroupExpressionASTNode g => $"Group({DescribeExpression(g.InnerExpression)})",
                _ => $"<{node.GetType().Name}>"
            };
        }

        // ===== 入口 =====
        public static void RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Parameter List Tests              ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestPlainParameters();
            TestDefaultParameters();
            TestVariadicParameters();
            TestMixedParameters();
            TestErrorCases();

            Console.WriteLine($"=== Parameter List Tests Complete: {passCount} passed, {failCount} failed ===\n");
        }
    }
}
