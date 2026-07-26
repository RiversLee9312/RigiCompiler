using System;
using System.Collections.Generic;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// import 语句解析测试（SYNTAX §15.2，P5 ImportParserLayer 重建）
    ///
    /// 覆盖：
    /// 1. 单个导入（单段/多段路径）
    /// 2. 全部导入（.*）
    /// 3. 多个导入（.{A, B} 共享前缀，展开为独立完整路径）
    /// 4. 错误用例（空路径、空列表、尾随逗号、* 后多余 token、{} 无前缀）
    ///
    /// 驱动方式：parser.Parse(tokens) 完整入口（import 是顶层语句）。
    /// </summary>
    public class ImportTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 单个导入 =====
        public static void TestSingleImport()
        {
            Console.WriteLine("=== Testing Single Import ===");

            TestImport("import core.collections.List", "import core.collections.List");
            TestImport("import core.String", "import core.String");
            TestImport("import a.b.c.d.E", "import a.b.c.d.E");

            Console.WriteLine();
        }

        // ===== 2. 全部导入 =====
        public static void TestImportAll()
        {
            Console.WriteLine("=== Testing Import All ===");

            TestImport("import core.collections.*", "import core.collections.*");
            TestImport("import core.*", "import core.*");

            Console.WriteLine();
        }

        // ===== 3. 多个导入（共享前缀，展开为独立完整路径）=====
        public static void TestMultiImport()
        {
            Console.WriteLine("=== Testing Multi Import ===");

            TestImport("import core.collections.{List, Map}",
                "import core.collections.List; import core.collections.Map");
            TestImport("import a.b.{X, Y, Z}",
                "import a.b.X; import a.b.Y; import a.b.Z");
            TestImport("import core.{List}", "import core.List");

            Console.WriteLine();
        }

        // ===== 4. 错误用例 =====
        public static void TestImportErrors()
        {
            Console.WriteLine("=== Testing Import Errors ===");

            TestError("import", "缺少导入路径");
            TestError("import core.collections.{}", "空导入列表");
            TestError("import core.collections.{List,}", "尾随逗号");
            TestError("import core.*.Foo", "* 后多余 token");
            TestError("import core.{A} extra", "导入列表后多余 token");
            TestError("import {List}", "{} 缺少前缀路径");

            Console.WriteLine();
        }

        // ===== 辅助方法 =====

        private static void TestImport(string source, string expected)
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
            ImportASTNode i => string.Join("; ", i.importedSymbols.ConvertAll(FormatItem)),
            _ => $"<{node.GetType().Name}>"
        };

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
            Console.WriteLine("║  Import Tests (SYNTAX §15.2, P5)                     ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestSingleImport();
            TestImportAll();
            TestMultiImport();
            TestImportErrors();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }
}
