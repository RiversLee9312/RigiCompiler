using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// typeOf 表达式解析测试（SYNTAX.md §3.7）
    ///
    /// 覆盖：
    /// 1. typeOf(值)
    /// 2. typeOf(调用/成员等复合表达式)
    /// 3. typeOf 结果参与后缀链
    /// 4. 错误用例：缺 (、缺操作数
    /// </summary>
    public class TypeOfExpressionTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 基本形式 =====
        public static void TestBasicTypeOf()
        {
            Console.WriteLine("=== Testing Basic typeOf ===");

            TestExpr("var t = typeOf(box)", "TypeOf(Sym(box))");
            TestExpr("var t = typeOf(12)", "TypeOf(Int(12,I32))");

            Console.WriteLine();
        }

        // ===== 2. 复合操作数 =====
        public static void TestComplexOperands()
        {
            Console.WriteLine("=== Testing typeOf with Complex Operands ===");

            TestExpr("var t = typeOf(foo(1))", "TypeOf(Call(Sym(foo), [Int(1,I32)]))");
            TestExpr("var t = typeOf(obj.field)", "TypeOf(Sym(obj.field))");

            Console.WriteLine();
        }

        // ===== 3. 后缀链 =====
        public static void TestSuffixAfterTypeOf()
        {
            Console.WriteLine("=== Testing Suffix after typeOf ===");

            TestExpr("var t = typeOf(box).name", "Access(TypeOf(Sym(box)), .name)");

            Console.WriteLine();
        }

        // ===== 4. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing typeOf Error Cases (expect ParserException) ===");

            // 缺 (
            TestError("var t = typeOf box", "缺 (");
            // 缺操作数
            TestError("var t = typeOf()", "缺操作数");

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        // 解析一段变量声明代码，返回声明节点
        private static VariableDeclarationASTNode? ParseVarDecl(string code)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(code);
            var parser = new Parser();
            var ast = parser.Parse(tokens);

            if (ast is RootASTNode root && root.Children.Count > 0)
            {
                return root.Children[0] as VariableDeclarationASTNode;
            }
            return null;
        }

        // 结构校验：初始化表达式的描述串必须与期望完全一致
        private static void TestExpr(string code, string expectedDesc)
        {
            try
            {
                var decl = ParseVarDecl(code);
                if (decl == null)
                {
                    Fail(code, "no variable declaration node produced");
                    return;
                }

                string actual = DescribeExpression(decl.Initializer);
                if (actual == expectedDesc)
                {
                    Pass(code, actual);
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

        // 错误校验：解析必须抛出 ParserException
        private static void TestError(string code, string reason)
        {
            try
            {
                ParseVarDecl(code);
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

        private static void Pass(string code, string result)
        {
            Console.WriteLine($"  [PASS] {code}");
            Console.WriteLine($"      => {result}");
            passCount++;
        }

        private static void Fail(string code, string message)
        {
            Console.WriteLine($"  [FAIL] {code}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== AST 描述 =====

        // 把表达式节点描述为紧凑的结构串，用于精确比对
        private static string DescribeExpression(ASTNode? node)
        {
            return node switch
            {
                null => "<null>",
                LiteralExpressionASTNode lit => DescribeExpression(lit.LiteralNode),
                IntLiteralASTNode i => $"Int({i.Value},{i.IntType}{(i.IsHex ? ",hex" : "")})",
                StringLiteralASTNode s => $"Str(\"{s.Value}\")",
                SymbolReferenceASTNode sref => $"Sym({DescribeSymbol(sref.Symbol.symbol)})",
                CallExpressionASTNode c =>
                    $"Call({DescribeExpression(c.Callee)}, [{string.Join(", ", c.Arguments.Select(DescribeArgument))}])",
                MemberAccessASTNode m =>
                    $"Access({DescribeExpression(m.Object)}, {(m.IsSafeAccess ? "?" : "")}.{m.MemberName})",
                TypeOfExpressionASTNode t => $"TypeOf({DescribeExpression(t.Operand)})",
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 描述实参（具名时为 name:value）
        private static string DescribeArgument(ArgumentASTNode arg)
        {
            return arg.Name != null
                ? $"{arg.Name}:{DescribeExpression(arg.Value)}"
                : DescribeExpression(arg.Value);
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

        // ===== 入口 =====
        public static void RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  typeOf Expression Tests           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestBasicTypeOf();
            TestComplexOperands();
            TestSuffixAfterTypeOf();
            TestErrorCases();

            Console.WriteLine($"=== typeOf Expression Tests Complete: {passCount} passed, {failCount} failed ===\n");
        }
    }
}
