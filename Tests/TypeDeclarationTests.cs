using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 类型声明解析测试（P3 M13 基础）
    ///
    /// 覆盖：
    /// 1. 简单 class 声明（空 body）
    /// 2. 带修饰符的 class（pub, open, abstract, etc.）
    /// 3. interface 声明
    /// 4. struct 声明
    /// 5. wrapper 声明
    ///
    /// 注：本阶段仅解析声明头部和空 body {}
    ///     成员解析（字段、方法）将在后续实现
    /// </summary>
    public class TypeDeclarationTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 简单 class 声明 =====
        public static void TestSimpleClass()
        {
            Console.WriteLine("=== Testing Simple Class Declaration ===");

            TestDeclaration("class Animal {}",
                "class Animal");

            TestDeclaration("class Dog {}",
                "class Dog");

            Console.WriteLine();
        }

        // ===== 2. 带修饰符的 class =====
        public static void TestClassWithModifiers()
        {
            Console.WriteLine("=== Testing Class with Modifiers ===");

            TestDeclaration("pub class Animal {}",
                "pub class Animal");

            TestDeclaration("pub open class Animal {}",
                "pub open class Animal");

            TestDeclaration("pub abstract class Shape {}",
                "pub abstract class Shape");

            TestDeclaration("pub shared class Session {}",
                "pub shared class Session");

            TestDeclaration("pub singleton class Config {}",
                "pub singleton class Config");

            Console.WriteLine();
        }

        // ===== 3. interface 声明 =====
        public static void TestInterface()
        {
            Console.WriteLine("=== Testing Interface Declaration ===");

            TestDeclaration("interface Drawable {}",
                "interface Drawable");

            TestDeclaration("pub interface Comparable {}",
                "pub interface Comparable");

            Console.WriteLine();
        }

        // ===== 4. struct 声明 =====
        public static void TestStruct()
        {
            Console.WriteLine("=== Testing Struct Declaration ===");

            TestDeclaration("struct Point {}",
                "struct Point");

            TestDeclaration("pub struct Vector2 {}",
                "pub struct Vector2");

            TestDeclaration("pub open struct BaseStruct {}",
                "pub open struct BaseStruct");

            TestDeclaration("pub rich struct Entry {}",
                "pub rich struct Entry");

            TestDeclaration("pub shared rich struct SharedEntry {}",
                "pub shared rich struct SharedEntry");

            Console.WriteLine();
        }

        // ===== 5. wrapper 声明 =====
        public static void TestWrapper()
        {
            Console.WriteLine("=== Testing Wrapper Declaration ===");

            TestDeclaration("wrapper Logged {}",
                "wrapper Logged");

            TestDeclaration("pub wrapper Cached {}",
                "pub wrapper Cached");

            Console.WriteLine();
        }

        // ===== 辅助方法 =====

        private static void TestDeclaration(string source, string expected)
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

        private static string FormatRoot(RootASTNode root)
        {
            if (root.Children.Count == 0)
                return "<empty>";

            var firstChild = root.Children[0];
            if (firstChild is DeclarationASTNode decl && decl.Declaration != null)
            {
                return FormatDeclaration(decl.Declaration);
            }

            return $"<{firstChild.GetType().Name}>";
        }

        private static string FormatDeclaration(ASTNode node)
        {
            return node switch
            {
                ClassDeclarationASTNode c =>
                    $"{string.Join(" ", c.Modifiers)}{(c.Modifiers.Count > 0 ? " " : "")}class {c.ClassName}",
                InterfaceDeclarationASTNode i =>
                    $"{string.Join(" ", i.Modifiers)}{(i.Modifiers.Count > 0 ? " " : "")}interface {i.InterfaceName}",
                StructDeclarationASTNode s =>
                    $"{string.Join(" ", s.Modifiers)}{(s.Modifiers.Count > 0 ? " " : "")}struct {s.StructName}",
                WrapperDeclarationASTNode w =>
                    $"{string.Join(" ", w.Modifiers)}{(w.Modifiers.Count > 0 ? " " : "")}wrapper {w.WrapperName}",
                _ => $"<{node.GetType().Name}>"
            };
        }

        // ===== 入口 =====
        public static void RunAll()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Type Declaration Tests (P3 M13 Basic)               ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            TestSimpleClass();
            TestClassWithModifiers();
            TestInterface();
            TestStruct();
            TestWrapper();

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Total: {passCount + failCount,3} tests | Pass: {passCount,3} | Fail: {failCount,3}            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }
}
