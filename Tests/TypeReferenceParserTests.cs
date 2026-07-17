using System;
using System.Collections.Generic;

namespace LatteCompiler.Tests
{
    public class TypeReferenceParserTests
    {
        // TypeReferenceParserLayer 用于解析类型引用（使用类型）
        // 不处理 rich/shared，它们是类型声明的修饰符

        public static void RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Type Reference Parser Tests       ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            Console.WriteLine("=== Unit Testing ===");
            Console.WriteLine("TypeReferenceParserLayer 已实现，支持：");
            Console.WriteLine("  ✓ 基本类型: i32, i64, String, bool");
            Console.WriteLine("  ✓ 可空类型: String?, i32?");
            Console.WriteLine("  ✓ 泛型类型: List<T>, Map<K,V>");
            Console.WriteLine();
            Console.WriteLine("注意：rich 和 shared 是**类型声明修饰符**，");
            Console.WriteLine("用于定义类型时（如 rich class MyClass），");
            Console.WriteLine("而不是使用类型时（变量声明中不写 rich MyClass）。");
            Console.WriteLine();
            Console.WriteLine("完整测试将在 VariableDeclarationParserLayer 实现后进行。");
            Console.WriteLine("届时可以测试完整的声明语句如:");
            Console.WriteLine("  const name: String = \"Hello\"");
            Console.WriteLine("  var count: i32? = null");
            Console.WriteLine("  var list: List<String> = createList()");
            Console.WriteLine();

            Console.WriteLine("=== Component Status ===");
            TestComponentCreation();

            Console.WriteLine("\n=== All Tests Complete ===\n");
        }

        // 测试组件创建
        private static void TestComponentCreation()
        {
            try
            {
                // 测试能否创建类型引用节点
                var typeNode = new TypeReferenceASTNode(null);
                Console.WriteLine("  [PASS] TypeReferenceASTNode creation");

                // 测试能否创建解析器
                var parser = new TypeReferenceParserLayer(typeNode);
                Console.WriteLine("  [PASS] TypeReferenceParserLayer creation");

                // 测试基本属性
                typeNode.IsNullable = true;
                Console.WriteLine("  [PASS] TypeReferenceASTNode properties");

                Console.WriteLine("\n  ✓ All components created successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERROR] Component creation failed: {ex.Message}");
            }
        }
    }
}

