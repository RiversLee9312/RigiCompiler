using System;
using System.Collections.Generic;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// namespace 声明解析测试（SYNTAX §15.1，P5 收尾）
    ///
    /// 覆盖：
    /// 1. 基本形态（单段/多段路径）
    /// 2. 与 import / 顶层声明组合（模块系统完整文件头）
    /// 3. 错误用例（空路径、路径后多余 token）
    ///
    /// 驱动方式：parser.Parse(tokens) 完整入口（namespace 是顶层声明）。
    /// </summary>
    public class NamespaceTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 基本形态 =====
        public static void TestBasicNamespace()
        {
            Console.WriteLine("=== Testing Basic Namespace ===");

            TestNamespace("namespace com.example.myapp", "namespace com.example.myapp");
            TestNamespace("namespace core", "namespace core");

            Console.WriteLine();
        }

        // ===== 2. 与 import / 顶层声明组合 =====
        public static void TestNamespaceCombinations()
        {
            Console.WriteLine("=== Testing Namespace Combinations ===");

            TestNamespace(
                "namespace com.example.myapp\n" +
                "import core.collections.List",
                "namespace com.example.myapp; import core.collections.List");
            TestNamespace(
                "namespace com.example.myapp\n" +
                "import core.collections.{List, Map}\n" +
                "pub func main() {}",
                "namespace com.example.myapp; import core.collections.List; " +
                "import core.collections.Map; pub func main() {}");

            Console.WriteLine();
        }

        // ===== 3. 错误用例 =====
        public static void TestNamespaceErrors()
        {
            Console.WriteLine("=== Testing Namespace Errors ===");

            TestError("namespace", "缺少命名空间路径");
            TestError("namespace com.example extra", "路径后多余 token");
            TestError("namespace com.{example}", "路径中不允许 {} 列表");

            Console.WriteLine();
        }

        // ===== 辅助方法 =====

        private static void TestNamespace(string source, string expected)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var parser = new Parser();
                var root = (RootASTNode)parser.Parse(tokens);

                var formatted = FormatRoot(root);
                if (formatted == expected)
                {
                    Console.WriteLine($"PASS: {source}");
                    passCount++;
                }
                else
                {
                    Console.WriteLine($"FAIL: {source}");
                    Console.WriteLine($"  Expected: {expected}");
                    Console.WriteLine($"  Got:      {formatted}");
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL (Exception): {source}");
                Console.WriteLine($"  Expected: {expected}");
                Console.WriteLine($"  Exception: {ex.Message}");
                failCount++;
            }
        }

        private static void TestError(string source, string reason)
        {
            try
            {
                var lexer = new Lexer();
                var tokens = lexer.Tokenize(source);
                var parser = new Parser();
                parser.Parse(tokens);
                Console.WriteLine($"FAIL: {source}");
                Console.WriteLine($"  Expected ParserException ({reason}), but parse succeeded");
                failCount++;
            }
            catch (ParserException)
            {
                Console.WriteLine($"PASS: {source}  (rejected: {reason})");
                passCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {source}");
                Console.WriteLine($"  Expected ParserException ({reason}), got {ex.GetType().Name}: {ex.Message}");
                failCount++;
            }
        }

        private static string FormatRoot(RootASTNode root)
        {
            if (root.Children.Count == 0)
                return "<empty>";

            return string.Join("; ", root.Children.ConvertAll(FormatNode));
        }

        private static string FormatNode(ASTNode node) => node switch
        {
            NamespaceDeclarationASTNode n =>
                "namespace " + string.Join(".", n.Name.symbol.elements.ConvertAll(el => el.name)),
            ImportASTNode i => string.Join("; ", i.importedSymbols.ConvertAll(FormatItem)),
            CallableDeclarationASTNode f => FormatCallable(f),
            _ => $"<{node.GetType().Name}>"
        };

        private static string FormatCallable(CallableDeclarationASTNode f)
        {
            var mods = f.Modifiers.Count == 0 ? "" : string.Join(" ", f.Modifiers) + " ";
            return mods + "func " + f.Name + "()" + (f.Body != null ? " {}" : "");
        }

        private static string FormatItem(ImportItem item)
        {
            var s = "import " + string.Join(".",
                item.symbolNode.symbol.elements.ConvertAll(el => el.name));
            return item.importAll ? s + ".*" : s;
        }

        // ===== 入口 =====
        public static void RunAll()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Namespace Tests (SYNTAX §15.1, P5)                  ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestBasicNamespace();
            TestNamespaceCombinations();
            TestNamespaceErrors();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }
}
