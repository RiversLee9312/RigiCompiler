using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// switch 表达式解析测试（roadmap #8 表达式模式，SYNTAX.md §7.2）
    ///
    /// 覆盖：
    /// 1. 值匹配分支（不含 _ ）
    /// 2. 模式匹配分支（含 _ ，_ 代表被检查的值）
    /// 3. default 分支
    /// 4. 单行书写
    /// 5. 错误用例：缺 default、缺 ->
    /// </summary>
    public class SwitchExpressionTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 值匹配 =====
        public static void TestValueMatch()
        {
            Console.WriteLine("=== Testing switch Value Match ===");

            TestExpr("var r = switch(expr) {\n" +
                     "    (1) -> { \"one\" }\n" +
                     "    (2) -> { \"two\" }\n" +
                     "    default -> { \"other\" }\n" +
                     "}",
                "Switch(Sym(expr), [Int(1,I32) -> Str(\"one\"), Int(2,I32) -> Str(\"two\")], " +
                "default -> Str(\"other\"))");

            Console.WriteLine();
        }

        // ===== 2. 模式匹配 =====
        public static void TestPatternMatch()
        {
            Console.WriteLine("=== Testing switch Pattern Match ===");

            TestExpr("var r = switch(n) {\n" +
                     "    (_ > 10) -> { \"big\" }\n" +
                     "    (_ == (3 + 4)) -> { \"seven\" }\n" +
                     "    default -> { \"small\" }\n" +
                     "}",
                "Switch(Sym(n), " +
                "[Binary(Sym(_) > Int(10,I32)) -> Str(\"big\"), " +
                "Binary(Sym(_) == Group(Binary(Int(3,I32) + Int(4,I32)))) -> Str(\"seven\")], " +
                "default -> Str(\"small\"))");

            Console.WriteLine();
        }

        // ===== 3. 单行书写 =====
        public static void TestSingleLine()
        {
            Console.WriteLine("=== Testing switch Single-line ===");

            TestExpr("var r = switch(x) { (1) -> { 1 } default -> { 0 } }",
                "Switch(Sym(x), [Int(1,I32) -> Int(1,I32)], default -> Int(0,I32))");

            Console.WriteLine();
        }

        // ===== 4. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing switch Error Cases (expect ParserException) ===");

            // 缺 default（switch 表达式必须包含 default 分支，SYNTAX §7.2）
            TestError("var r = switch(x) { (1) -> { 1 } }", "缺 default");
            // 分支缺 ->
            TestError("var r = switch(x) { (1) { 1 } default -> { 0 } }", "分支缺 ->");
            // 缺 selector 的 (
            TestError("var r = switch x { (1) -> { 1 } default -> { 0 } }", "缺 (");

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
                SwitchExpressionASTNode s => DescribeSwitch(s),
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 描述 switch：Switch(selector, [pattern -> body, ...], default -> body)
        private static string DescribeSwitch(SwitchExpressionASTNode s)
        {
            string cases = string.Join(", ", s.Cases.Select(
                c => $"{DescribeExpression(c.Pattern.Expression)} -> {DescribeExpression(c.Body.Expression)}"));
            string def = s.DefaultBody != null ? DescribeExpression(s.DefaultBody.Expression) : "<none>";
            return $"Switch({DescribeExpression(s.Selector.Expression)}, [{cases}], default -> {def})";
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
            Console.WriteLine("║  switch Expression Tests           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestValueMatch();
            TestPatternMatch();
            TestSingleLine();
            TestErrorCases();

            Console.WriteLine($"=== switch Expression Tests Complete: {passCount} passed, {failCount} failed ===\n");

            return failCount;
        }
    }
}
