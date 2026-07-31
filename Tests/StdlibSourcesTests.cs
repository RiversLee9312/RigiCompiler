using System;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// stdlib 内嵌源载入测试（M43，Semantic/StdlibSources.cs）：
    /// 断言 stdlib/core/Console.latte 确实经 EmbeddedResource 进入程序集，
    /// 且被当前 Lexer+Parser 完整接受。
    ///
    /// 覆盖：
    /// 1. ParseAll() 返回恰好一棵 RootASTNode（当前 stdlib 仅此一源），
    ///    Root 的 Span.sourceName 为逻辑名映射形 <stdlib>/core/Console.latte
    /// 2. 结构断言：namespace core.io + pub class Console；Console 恰好 3 个
    ///    callable 成员——print/printErr 带 native/static 修饰符、无 Body、
    ///    各带两个注解；println 有 Body、无注解
    /// 3. 整棵 Root 的 AstDescribe 描述串精确比对
    /// </summary>
    public static class StdlibSourcesTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();

            TestCountAndSourceName();
            TestStructure();
            TestDescribe();

            return TestHarness.Summary("StdlibSources");
        }

        // ===== 1. 数量与 sourceName =====
        private static void TestCountAndSourceName()
        {
            TestHarness.Section("ParseAll: Count & SourceName");

            var roots = StdlibSources.ParseAll();
            TestHarness.CheckTrue("ParseAll 返回恰好 1 棵 RootASTNode",
                roots.Count == 1, $"实际 {roots.Count} 棵");
            if (roots.Count == 0) { TestHarness.Blank(); return; }

            TestHarness.Check("Root Span.sourceName",
                roots[0].Span?.sourceName ?? "<null>", "<stdlib>/core/Console.latte");

            TestHarness.Blank();
        }

        // ===== 2. 结构断言 =====
        private static void TestStructure()
        {
            TestHarness.Section("Structure: namespace core.io + class Console");

            var roots = StdlibSources.ParseAll();
            if (roots.Count != 1)
            {
                TestHarness.CheckTrue("ParseAll 返回恰好 1 棵 RootASTNode（结构断言前置）",
                    false, $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots[0];

            TestHarness.CheckTrue("顶层恰好 2 个声明（namespace + class）",
                root.Declarations.Count == 2, $"实际 {root.Declarations.Count}");

            var ns = root.Declarations.Count > 0 ? root.Declarations[0] as NamespaceDeclarationASTNode : null;
            TestHarness.CheckTrue("首声明是 namespace", ns != null,
                root.Declarations.Count > 0 ? root.Declarations[0].GetType().Name : "<none>");
            if (ns != null)
            {
                TestHarness.Check("namespace 路径", AstDescribe.Symbol(ns.Name.symbol), "core.io");
            }

            var cls = root.Declarations.Count > 1 ? root.Declarations[1] as ClassDeclarationASTNode : null;
            TestHarness.CheckTrue("次声明是 class", cls != null,
                root.Declarations.Count > 1 ? root.Declarations[1].GetType().Name : "<none>");
            if (cls == null) { TestHarness.Blank(); return; }

            TestHarness.Check("class 名", cls.ClassName, "Console");
            TestHarness.CheckTrue("class 带 pub 修饰符", cls.Modifiers.Contains(Keywords.PUB));
            TestHarness.CheckTrue("Console 恰好 3 个 callable 成员",
                cls.Members.Count == 3 && cls.Members.All(m => m is CallableDeclarationASTNode),
                $"实际 {cls.Members.Count} 个成员");
            if (cls.Members.Count == 3)
            {
                CheckNativeMethod("print", cls.Members[0]);
                CheckNativeMethod("printErr", cls.Members[1]);
                CheckPrintln(cls.Members[2]);
            }

            TestHarness.Blank();
        }

        // native 无体方法：static + native 修饰符、Body 为 null、恰好两个注解
        private static void CheckNativeMethod(string label, ASTNode member)
        {
            var f = member as CallableDeclarationASTNode;
            TestHarness.CheckTrue($"{label} 是 callable", f != null, member.GetType().Name);
            if (f == null) return;

            TestHarness.Check($"{label} 方法名", f.Name, label);
            TestHarness.CheckTrue($"{label} 带 static 修饰符", f.Modifiers.Contains(Keywords.STATIC));
            TestHarness.CheckTrue($"{label} 带 native 修饰符", f.Modifiers.Contains(Keywords.NATIVE));
            TestHarness.CheckTrue($"{label} 无 Body（native 无体）", f.Body == null);
            TestHarness.CheckTrue($"{label} 恰好 2 个注解",
                f.Annotations.Count == 2, $"实际 {f.Annotations.Count}");
            if (f.Annotations.Count == 2)
            {
                TestHarness.Check($"{label} 注解名",
                    AstDescribe.Symbol(f.Annotations[0].Name.symbol) + ", " +
                    AstDescribe.Symbol(f.Annotations[1].Name.symbol),
                    "NativeLibrary, NativeSymbol");
            }
        }

        // println：Latte 层包装（有 Body、无注解、无 native）
        private static void CheckPrintln(ASTNode member)
        {
            var f = member as CallableDeclarationASTNode;
            TestHarness.CheckTrue("println 是 callable", f != null, member.GetType().Name);
            if (f == null) return;

            TestHarness.Check("println 方法名", f.Name, "println");
            TestHarness.CheckTrue("println 带 static 修饰符", f.Modifiers.Contains(Keywords.STATIC));
            TestHarness.CheckTrue("println 不带 native 修饰符", !f.Modifiers.Contains(Keywords.NATIVE));
            TestHarness.CheckTrue("println 有 Body", f.Body != null);
            TestHarness.CheckTrue("println 无注解", f.Annotations.Count == 0);
        }

        // ===== 3. 描述串精确比对 =====
        private static void TestDescribe()
        {
            TestHarness.Section("AstDescribe Snapshot");

            var roots = StdlibSources.ParseAll();
            if (roots.Count != 1)
            {
                TestHarness.CheckTrue("ParseAll 返回恰好 1 棵 RootASTNode（快照前置）",
                    false, $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }

            TestHarness.Check("Root 描述串", AstDescribe.Root(roots[0]),
                "namespace core.io; pub class Console {" +
                @"@NativeLibrary(Str(""latte_rt"")) @NativeSymbol(Str(""print"")) priv static native func print(text: String), " +
                @"@NativeLibrary(Str(""latte_rt"")) @NativeSymbol(Str(""printErr"")) priv static native func printErr(text: String), " +
                "pub static func println(text: String) {}}");

            TestHarness.Blank();
        }
    }
}
