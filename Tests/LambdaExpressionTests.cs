using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// Lambda 表达式解析测试（roadmap #21，SYNTAX.md §5）
    ///
    /// 覆盖：
    /// 1. 完整形式 func{(params): ReturnType -> body}
    /// 2. 空参/多参/默认参数
    /// 3. 泛型 lambda（泛型形参在形参列表之后）
    /// 4. async lambda
    /// 5. trailing lambda（expr{...} 脱糖为调用）
    /// 6. 跨行书写
    /// 7. 错误用例：缺 :、缺 ->、缺 body、async 后非 func
    /// </summary>
    public class LambdaExpressionTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 基本形式 =====
        public static void TestBasicLambdas()
        {
            Console.WriteLine("=== Testing Basic Lambdas ===");

            TestExpr("var f = func{(x: i32): i32 -> (x + 1)}",
                "Lambda([x:i32]): i32 -> Group(Binary(Sym(x) + Int(1,I32)))");
            TestExpr("var f = func{(): i32 -> 42}",
                "Lambda([]): i32 -> Int(42,I32)");
            TestExpr("var add = func{(x: i32, y: i32): i32 -> (x + y)}",
                "Lambda([x:i32, y:i32]): i32 -> Group(Binary(Sym(x) + Sym(y)))");
            // 默认参数（复用 ParameterListParserLayer）
            TestExpr("var f = func{(x: i32 = 5): i32 -> x}",
                "Lambda([x:i32 = Int(5,I32)]): i32 -> Sym(x)");
            // 可变参数（复用 ParameterListParserLayer）
            TestExpr("var f = func{(numbers: i32...): i32 -> 0}",
                "Lambda([numbers:i32...]): i32 -> Int(0,I32)");

            Console.WriteLine();
        }

        // ===== 2. 泛型 lambda =====
        public static void TestGenericLambdas()
        {
            Console.WriteLine("=== Testing Generic Lambdas ===");

            // SYNTAX §5.1：泛型形参列表在形参列表之后
            TestExpr("var f = func{(width: TSize)\\<TSize extends Size>: TSize -> width}",
                "Lambda([width:TSize])<TSize extends Size>: TSize -> Sym(width)");

            Console.WriteLine();
        }

        // ===== 3. async lambda =====
        public static void TestAsyncLambdas()
        {
            Console.WriteLine("=== Testing Async Lambdas ===");

            TestExpr("var loader = async func{(id: i32): SharedUser -> loadUserNow(id)}",
                "Lambda async([id:i32]): SharedUser -> Call(Sym(loadUserNow), [Sym(id)])");

            Console.WriteLine();
        }

        // ===== 4. trailing lambda =====
        public static void TestTrailingLambdas()
        {
            Console.WriteLine("=== Testing Trailing Lambdas ===");

            // expr{...} 脱糖为以 lambda 为唯一实参的调用
            TestExpr("var r = list.map{(item: String): i32 -> item.length}",
                "Call(Sym(list.map), [Lambda([item:String]): i32 -> Sym(item.length)])");
            // lambda 作为普通实参
            TestExpr("var r = foo(func{(x: i32): i32 -> x})",
                "Call(Sym(foo), [Lambda([x:i32]): i32 -> Sym(x)])");

            Console.WriteLine();
        }

        // ===== 5. 跨行书写 =====
        public static void TestMultiLineLambdas()
        {
            Console.WriteLine("=== Testing Multi-line Lambdas ===");

            TestExpr("var f = func{(x: i32): i32 ->\n    (x + 1)\n}",
                "Lambda([x:i32]): i32 -> Group(Binary(Sym(x) + Int(1,I32)))");

            Console.WriteLine();
        }

        // ===== 6. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Lambda Error Cases (expect ParserException) ===");

            // 缺 :（返回类型标注）
            TestError("var f = func{(x: i32) -> (x + 1)}", "缺 :");
            // 缺 ->
            TestError("var f = func{(x: i32): i32 (x + 1)}", "缺 ->");
            // 缺 body
            TestError("var f = func{(x: i32): i32 -> }", "缺 body");
            // async 后不是 func
            TestError("var f = async x", "async 后必须是 func");
            // 缺 {
            TestError("var f = func(x: i32): i32 -> (x + 1)", "缺 {");

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
                FloatLiteralASTNode f => $"Float({f.Value}{(f.IsFloat ? "f" : "")})",
                StringLiteralASTNode s => $"Str(\"{s.Value}\")",
                BoolLiteralASTNode b => $"Bool({b.Value})",
                NullLiteralASTNode => "Null",
                SymbolReferenceASTNode sref => $"Sym({DescribeSymbol(sref.Symbol.symbol)})",
                UnaryExpressionASTNode u => $"Unary({u.Operator} {DescribeExpression(u.Operand)})",
                BinaryExpressionASTNode b =>
                    $"Binary({DescribeExpression(b.Left)} {b.Operator} {DescribeExpression(b.Right)})",
                GroupExpressionASTNode g => $"Group({DescribeExpression(g.InnerExpression)})",
                CallExpressionASTNode c =>
                    $"Call({DescribeExpression(c.Callee)}, [{string.Join(", ", c.Arguments.Select(DescribeArgument))}])",
                IndexExpressionASTNode ix =>
                    $"Index({DescribeExpression(ix.Object)}, [{string.Join(", ", ix.Indices.Select(DescribeArgument))}])",
                MemberAccessASTNode m =>
                    $"Access({DescribeExpression(m.Object)}, {(m.IsSafeAccess ? "?" : "")}.{m.MemberName}{DescribeGenericArgs(m)})",
                LambdaExpressionASTNode l => DescribeLambda(l),
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 描述 lambda：Lambda [async]([params])[\<generics>]: Ret -> body
        private static string DescribeLambda(LambdaExpressionASTNode l)
        {
            string desc = "Lambda";
            if (l.IsAsync) desc += " async";
            desc += $"([{string.Join(", ", l.Parameters.Parameters.Select(DescribeParameter))}])";
            if (l.GenericParameters != null)
            {
                desc += DescribeGenericParameters(l.GenericParameters);
            }
            desc += $": {DescribeType(l.ReturnType)} -> {DescribeExpression(l.Body)}";
            return desc;
        }

        // 描述形参：name:Type、name:Type...、name:Type = default
        private static string DescribeParameter(ParameterASTNode p)
        {
            string desc = $"{p.Name}:{DescribeType(p.Type)}";
            if (p.IsVariadic) desc += "...";
            if (p.DefaultValue != null) desc += $" = {DescribeExpression(p.DefaultValue)}";
            return desc;
        }

        // 描述泛型形参列表：<params, constraints>
        private static string DescribeGenericParameters(GenericParameterListASTNode list)
        {
            var parts = new List<string>();
            foreach (var p in list.Parameters)
            {
                string part = p.Variance switch
                {
                    GenericVariance.Out => "out ",
                    GenericVariance.In => "in ",
                    _ => ""
                };
                if (p.IsNamedVariadic) part += "named ";
                part += p.Name;
                if (p.IsVariadic || p.IsNamedVariadic) part += "...";
                parts.Add(part);
            }
            foreach (var c in list.Constraints)
            {
                string kind = c.Kind switch
                {
                    GenericConstraintKind.Extends => "extends",
                    GenericConstraintKind.Supers => "supers",
                    _ => "with"
                };
                parts.Add($"{DescribeType(c.Target)} {kind} {DescribeType(c.Bound)}");
            }
            return "<" + string.Join(", ", parts) + ">";
        }

        // 描述实参（具名时为 name:value）
        private static string DescribeArgument(ArgumentASTNode arg)
        {
            return arg.Name != null
                ? $"{arg.Name}:{DescribeExpression(arg.Value)}"
                : DescribeExpression(arg.Value);
        }

        // 描述成员访问上的泛型实参
        private static string DescribeGenericArgs(MemberAccessASTNode m)
        {
            if (m.GenericArguments.Count == 0) return "";
            return "<" + string.Join(",", m.GenericArguments.Select(DescribeType)) + ">";
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
        public static void RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Lambda Expression Tests           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestBasicLambdas();
            TestGenericLambdas();
            TestAsyncLambdas();
            TestTrailingLambdas();
            TestMultiLineLambdas();
            TestErrorCases();

            Console.WriteLine($"=== Lambda Expression Tests Complete: {passCount} passed, {failCount} failed ===\n");
        }
    }
}
