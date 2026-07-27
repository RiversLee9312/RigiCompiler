using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// if 表达式解析测试（roadmap #7 表达式模式，SYNTAX.md §7.1）
    ///
    /// 覆盖：
    /// 1. 基本形式 if (cond) { then } else { else }
    /// 2. 嵌套 if 表达式
    /// 3. 跨行书写
    /// 4. if 表达式作为实参
    /// 5. 错误用例：缺 (、缺 else（表达式形式必须有 else）
    /// </summary>
    public class IfExpressionTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 基本形式 =====
        public static void TestBasicIfExpressions()
        {
            Console.WriteLine("=== Testing Basic if Expressions ===");

            TestExpr("var r = if (x > 0) { x } else { opposite(x) }",
                "If(Binary(Sym(x) > Int(0,I32)), Sym(x), Call(Sym(opposite), [Sym(x)]))");
            TestExpr("var r = if (flag) { 1 } else { 2 }",
                "If(Sym(flag), Int(1,I32), Int(2,I32))");

            Console.WriteLine();
        }

        // ===== 2. 嵌套 if =====
        public static void TestNestedIfExpressions()
        {
            Console.WriteLine("=== Testing Nested if Expressions ===");

            TestExpr("var r = if (a) { if (b) { 1 } else { 2 } } else { 3 }",
                "If(Sym(a), If(Sym(b), Int(1,I32), Int(2,I32)), Int(3,I32))");

            Console.WriteLine();
        }

        // ===== 3. 跨行书写 =====
        public static void TestMultiLineIfExpressions()
        {
            Console.WriteLine("=== Testing Multi-line if Expressions ===");

            TestExpr("var r = if (x > 0) {\n    x\n} else {\n    opposite(x)\n}",
                "If(Binary(Sym(x) > Int(0,I32)), Sym(x), Call(Sym(opposite), [Sym(x)]))");

            Console.WriteLine();
        }

        // ===== 4. if 表达式作为实参 =====
        public static void TestIfExpressionAsArgument()
        {
            Console.WriteLine("=== Testing if Expression as Argument ===");

            TestExpr("var v = foo(if (c) { 1 } else { 2 })",
                "Call(Sym(foo), [If(Sym(c), Int(1,I32), Int(2,I32))])");

            Console.WriteLine();
        }

        // ===== 5. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing if Expression Error Cases (expect ParserException) ===");

            // 缺 (
            TestError("var r = if x > 0 { x } else { y }", "缺 (");
            // 缺 else（if 表达式必须包含 else 分支，SYNTAX §7.1）
            TestError("var r = if (x > 0) { x }", "缺 else");
            // 缺 then 分支 }
            TestError("var r = if (x > 0) { x else { y }", "缺 }");

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

            if (ast is RootASTNode root && root.Declarations.Count > 0)
            {
                return root.Declarations[0] as VariableDeclarationASTNode;
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

                string actual = DescribeExpression(decl.Initializer!.Expression);
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
                LiteralExpressionASTNode lit => DescribeExpression(lit.Literal),
                IntLiteralASTNode i => $"Int({i.Value},{i.IntType}{(i.IsHex ? ",hex" : "")})",
                FloatLiteralASTNode f => $"Float({f.Value}{(f.IsFloat ? "f" : "")})",
                StringLiteralASTNode s => $"Str(\"{s.Value}\")",
                BoolLiteralASTNode b => $"Bool({b.Value})",
                NullLiteralASTNode => "Null",
                SymbolReferenceASTNode sref => $"Sym({DescribeSymbol(sref.Symbol.symbol)})",
                UnaryExpressionASTNode u => $"Unary({u.Operator} {DescribeExpression(u.Operand.Expression)})",
                BinaryExpressionASTNode b =>
                    $"Binary({DescribeExpression(b.Left.Expression)} {b.Operator} {DescribeExpression(b.Right.Expression)})",
                GroupExpressionASTNode g => $"Group({DescribeExpression(g.InnerExpression.Expression)})",
                CallExpressionASTNode c =>
                    $"Call({DescribeExpression(c.Callee.Expression)}, [{string.Join(", ", c.Arguments.Select(DescribeArgument))}])",
                MemberAccessASTNode m =>
                    $"Access({DescribeExpression(m.Object.Expression)}, {(m.IsSafeAccess ? "?" : "")}.{m.MemberName})",
                IfExpressionASTNode e =>
                    $"If({DescribeExpression(e.Condition.Expression)}, {DescribeExpression(e.ThenExpression.Expression)}, {DescribeExpression(e.ElseExpression.Expression)})",
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 描述实参（具名时为 name:value）
        private static string DescribeArgument(ArgumentASTNode arg)
        {
            return arg.Name != null
                ? $"{arg.Name}:{DescribeExpression(arg.Value.Expression)}"
                : DescribeExpression(arg.Value.Expression);
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
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  if Expression Tests               ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestBasicIfExpressions();
            TestNestedIfExpressions();
            TestMultiLineIfExpressions();
            TestIfExpressionAsArgument();
            TestErrorCases();

            Console.WriteLine($"=== if Expression Tests Complete: {passCount} passed, {failCount} failed ===\n");

            return failCount;
        }
    }
}
