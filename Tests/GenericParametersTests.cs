using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 泛型参数列表解析测试（GenericParametersParserLayer）
    ///
    /// 覆盖 SYNTAX.md §3.6 的泛型声明语法（\<...> 列表）：
    /// 1. 参数声明子句：TElement、多参数、out/in 型变
    /// 2. 约束子句：extends / supers / with
    /// 3. 可变泛型参数：TArgs...、named TValues...、可变 + 约束
    /// 4. wrapper canonical 形态：named TNamedArgs..., TUnnamedArgs..., TReturn
    /// 5. 错误用例：缺 \ 、缺 <、named 缺 ...、点数不足、空列表、复杂类型当参数名
    ///
    /// 测试驱动方式：通过 Parser.Parse(tokens, entryLayer) 重载，
    /// 以 GenericParametersParserLayer 为起始层独立解析 \<...> 片段。
    /// </summary>
    public class GenericParametersTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 参数声明子句 =====
        public static void TestParameters()
        {
            Console.WriteLine("=== Testing Generic Parameter Clauses ===");

            TestParse("\\<TElement>", "Params{TElement} Constraints{}");
            TestParse("\\<TInput, TResult>", "Params{TInput, TResult} Constraints{}");
            TestParse("\\<out TElement>", "Params{out TElement} Constraints{}");
            TestParse("\\<in TElement>", "Params{in TElement} Constraints{}");
            TestParse("\\<out T, in U, V>", "Params{out T, in U, V} Constraints{}");

            Console.WriteLine();
        }

        // ===== 2. 约束子句 =====
        public static void TestConstraints()
        {
            Console.WriteLine("=== Testing Generic Constraint Clauses ===");

            TestParse("\\<TItem extends Comparable>",
                "Params{} Constraints{TItem extends Comparable}");
            TestParse("\\<Serializable supers BaseType>",
                "Params{} Constraints{Serializable supers BaseType}");
            TestParse("\\<TItem extends Comparable, Serializable supers BaseType>",
                "Params{} Constraints{TItem extends Comparable, Serializable supers BaseType}");
            TestParse("\\<TItem with Serializable>",
                "Params{} Constraints{TItem with Serializable}");
            // 型变参数 + 约束
            TestParse("\\<out TElement extends Comparable>",
                "Params{out TElement} Constraints{TElement extends Comparable}");
            // 复杂类型作为约束 Target
            TestParse("\\<List\\<T> extends Collection\\<T>>",
                "Params{} Constraints{List<T> extends Collection<T>}");

            Console.WriteLine();
        }

        // ===== 3. 可变泛型参数 =====
        public static void TestVariadicParameters()
        {
            Console.WriteLine("=== Testing Variadic Generic Parameters ===");

            TestParse("\\<TArgs...>", "Params{TArgs...} Constraints{}");
            TestParse("\\<named TValues...>", "Params{named TValues...} Constraints{}");
            // 可变参数 + with 约束（Target 隐含为前面的参数）
            TestParse("\\<named TValues... with Serializable>",
                "Params{named TValues...} Constraints{TValues with Serializable}");

            Console.WriteLine();
        }

        // ===== 4. wrapper canonical 形态 =====
        public static void TestWrapperCanonicalShape()
        {
            Console.WriteLine("=== Testing Wrapper Canonical Shape ===");

            TestParse("\\<named TNamedArgs..., TUnnamedArgs..., TReturn>",
                "Params{named TNamedArgs..., TUnnamedArgs..., TReturn} Constraints{}");

            Console.WriteLine();
        }

        // ===== 5. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Error Cases (expect ParserException) ===");

            TestError("<TElement>", "缺少 \\ 开启符");
            TestError("\\TElement>", "缺少 <");
            TestError("\\<named TValues>", "named 后缺少 ...");
            TestError("\\<TArgs..>", "可变参数点数不足");
            TestError("\\<>", "空泛型列表");
            TestError("\\<List\\<String>, T>", "复杂类型不能作为参数名");

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        // 以 GenericParametersParserLayer 为起始层独立解析 \<...> 片段
        private static GenericParameterListASTNode ParseGenericParams(string code)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(code);
            var node = new GenericParameterListASTNode(null);
            var parser = new Parser();
            parser.Parse(tokens, new GenericParametersParserLayer(node));
            return node;
        }

        private static void TestParse(string code, string expectedDesc)
        {
            try
            {
                var node = ParseGenericParams(code);
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
                ParseGenericParams(code);
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

        private static string Describe(GenericParameterListASTNode node)
        {
            string parameters = string.Join(", ", node.Parameters.Select(DescribeParameter));
            string constraints = string.Join(", ", node.Constraints.Select(DescribeConstraint));
            return $"Params{{{parameters}}} Constraints{{{constraints}}}";
        }

        private static string DescribeParameter(GenericParameterASTNode param)
        {
            string prefix = param.Variance switch
            {
                GenericVariance.Out => "out ",
                GenericVariance.In => "in ",
                _ => ""
            };
            if (param.IsNamedVariadic) prefix = "named " + prefix;
            string suffix = (param.IsVariadic || param.IsNamedVariadic) ? "..." : "";
            return prefix + param.Name + suffix;
        }

        private static string DescribeConstraint(GenericConstraintASTNode constraint)
        {
            string kind = constraint.Kind switch
            {
                GenericConstraintKind.Extends => "extends",
                GenericConstraintKind.Supers => "supers",
                GenericConstraintKind.With => "with",
                _ => "?"
            };
            return $"{DescribeType(constraint.Target)} {kind} {DescribeType(constraint.Bound)}";
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
            Console.WriteLine("║  Generic Parameters Tests          ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestParameters();
            TestConstraints();
            TestVariadicParameters();
            TestWrapperCanonicalShape();
            TestErrorCases();

            Console.WriteLine($"=== Generic Parameters Tests Complete: {passCount} passed, {failCount} failed ===\n");
        }
    }
}
