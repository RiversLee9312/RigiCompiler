using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 泛型解析与 > 系列运算符组合测试
    ///
    /// 背景：
    /// - Lexer 不再合并 > 系列 notation（>>、>=、>>>），
    ///   使嵌套泛型的连续闭合符 >> 成为两个独立 > token；
    /// - 比较/移位运算符（>=、>>、>>>）由 ExpressionParserLayer 在
    ///   运算符状态下重新组合相邻的 >、= token 得到。
    ///
    /// 覆盖：
    /// 1. 嵌套泛型类型引用（两层、三层、可空组合）
    /// 2. > 系列运算符的重新组合（>=、>>、>>>）
    /// 3. 回归：单 >、<=、== 不受影响
    /// 4. 错误用例：移位与比较混用未加括号
    /// </summary>
    public class GenericParsingTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 嵌套泛型类型引用 =====
        public static void TestNestedGenericTypes()
        {
            Console.WriteLine("=== Testing Nested Generic Type References ===");

            // 单层回归
            TestType("var a: List\\<String>", "List<String>");
            // 两层嵌套：末尾 >> 是两个独立闭合符
            TestType("var b: List\\<Map\\<String, i32>>", "List<Map<String,i32>>");
            // 三层嵌套
            TestType("var c: List\\<List\\<List\\<i32>>>", "List<List<List<i32>>>");
            // 嵌套 + 可空
            TestType("var d: List\\<Map\\<String, i32>>?", "List<Map<String,i32>>?");
            // 嵌套泛型 + 初始化表达式
            TestDecl("var e: List\\<List\\<i32>> = null", "List<List<i32>>", "Null");

            Console.WriteLine();
        }

        // ===== 1.5 泛型与小于号的无歧义共存 =====
        public static void TestGenericVsLessThan()
        {
            Console.WriteLine("=== Testing \\< Generics vs < Less-Than ===");

            // < 现在只是小于号（在 \\< 语法下不再歧义）
            TestExpr("var lt = a < b", "Binary(Sym(a) < Sym(b))");
            // 泛型路径引用（不带调用）
            TestExpr("var gp = Span.alloc\\<f32>", "Sym(Span.alloc<f32>)");
            // 同一行内泛型与小于号共存
            TestExpr("var cmp = x < y", "Binary(Sym(x) < Sym(y))");

            Console.WriteLine();
        }

        // ===== 2. > 系列运算符组合 =====
        public static void TestCombinedOperators()
        {
            Console.WriteLine("=== Testing Combined > Operators ===");

            // >= 由 > 和 = 组合
            TestExpr("var r = a >= b", "Binary(Sym(a) >= Sym(b))");
            // >> 由两个 > 组合（括号内）
            TestExpr("var s = (a >> 2)", "Group(Binary(Sym(a) >> Int(2,I32)))");
            // >>> 由三个 > 组合
            TestExpr("var t = (a >>> 2)", "Group(Binary(Sym(a) >>> Int(2,I32)))");

            Console.WriteLine();
        }

        // ===== 3. 回归：其他比较运算符不受影响 =====
        public static void TestRegressionOperators()
        {
            Console.WriteLine("=== Testing Operator Regression ===");

            TestExpr("var g = a > b", "Binary(Sym(a) > Sym(b))");
            TestExpr("var l = a <= b", "Binary(Sym(a) <= Sym(b))");
            TestExpr("var e = a == b", "Binary(Sym(a) == Sym(b))");

            Console.WriteLine();
        }

        // ===== 4. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Error Cases (expect ParserException) ===");

            // 移位与比较混用，违反"无运算符优先级"
            TestError("var x = (a >> b >= c)", "移位与比较混用未加括号");
            // 非法运算符组合
            TestError("var y = (a >== b)", "非法运算符组合 >==");
            // 旧语法：裸 < 不再是泛型开启符（\\< 才是）
            TestError("var old: List<String>", "旧泛型语法 List<String> 已废弃");
            // \ 后必须紧跟 <
            TestError("var w = a \\ b", "\\ 后缺少 <");

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

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

        // 校验类型标注描述串
        private static void TestType(string code, string expectedType)
        {
            try
            {
                var decl = ParseVarDecl(code);
                if (decl == null)
                {
                    Fail(code, "no variable declaration node produced");
                    return;
                }

                string? actual = decl.TypeAnnotation != null
                    ? DescribeType(decl.TypeAnnotation) : null;
                if (actual == expectedType)
                {
                    Pass(code, actual!);
                }
                else
                {
                    Fail(code, $"expected type {expectedType}, got {actual ?? "<null>"}");
                }
            }
            catch (Exception ex)
            {
                Fail(code, $"unexpected exception: {ex.Message}");
            }
        }

        // 校验初始化表达式描述串
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

        // 同时校验类型标注与初始化表达式
        private static void TestDecl(string code, string expectedType, string expectedInit)
        {
            try
            {
                var decl = ParseVarDecl(code);
                if (decl == null)
                {
                    Fail(code, "no variable declaration node produced");
                    return;
                }

                string? type = decl.TypeAnnotation != null
                    ? DescribeType(decl.TypeAnnotation) : null;
                string? init = decl.Initializer != null
                    ? DescribeExpression(decl.Initializer!.Expression) : null;

                if (type == expectedType && init == expectedInit)
                {
                    Pass(code, $"{type} = {init}");
                }
                else
                {
                    Fail(code, $"expected [{expectedType} = {expectedInit}], " +
                               $"got [{type ?? "<null>"} = {init ?? "<null>"}]");
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
                _ => $"<{node.GetType().Name}>"
            };
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

        // ===== 入口 =====
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Generic Parsing Tests             ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestNestedGenericTypes();
            TestGenericVsLessThan();
            TestCombinedOperators();
            TestRegressionOperators();
            TestErrorCases();

            Console.WriteLine($"=== Generic Parsing Tests Complete: {passCount} passed, {failCount} failed ===\n");

            return failCount;
        }
    }
}
