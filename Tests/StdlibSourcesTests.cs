using System;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// stdlib 内嵌源载入测试（M43，Semantic/StdlibSources.cs）：
    /// 断言 stdlib/**/*.latte 确实经 EmbeddedResource 进入程序集，
    /// 且被当前 Lexer+Parser 完整接受。
    ///
    /// 覆盖（S7c-2 起三源：.bootstrap.latte / core/Console.latte /
    /// core/collections.latte，按逻辑名 Ordinal 排序）：
    /// 1. ParseAll() 返回恰好三棵 RootASTNode，Span.sourceName 为逻辑名
    ///    映射形（&lt;stdlib&gt;/ 前缀，含点开头文件名的反推）
    /// 2. 结构断言：.bootstrap 顶层恰好 1 个 ext operator callable；
    ///    Console（namespace core.io + pub class + 3 callable 成员，
    ///    native 双注解）；collections（namespace core.collections +
    ///    2 interface + 2 class）
    /// 3. Console 整棵 Root 的 AstDescribe 描述串精确比对
    /// </summary>
    public static class StdlibSourcesTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();

            TestCountAndSourceName();
            TestBootstrapStructure();
            TestConsoleStructure();
            TestCollectionsStructure();
            TestConsoleDescribe();

            return TestHarness.Summary("StdlibSources");
        }

        // ===== 1. 数量与 sourceName =====
        private static void TestCountAndSourceName()
        {
            TestHarness.Section("ParseAll: Count & SourceName");

            var roots = StdlibSources.ParseAll();
            TestHarness.CheckTrue("ParseAll 返回恰好 3 棵 RootASTNode",
                roots.Count == 3, $"实际 {roots.Count} 棵");
            if (roots.Count < 3) { TestHarness.Blank(); return; }

            // 逻辑名 Ordinal 排序：'.'(0x2E) < 'c'；'C'(0x43) < 'c'(0x63)
            TestHarness.Check("sourceName[0]（点开头文件名反推）",
                roots[0].Span?.sourceName ?? "<null>", "<stdlib>/.bootstrap.latte");
            TestHarness.Check("sourceName[1]",
                roots[1].Span?.sourceName ?? "<null>", "<stdlib>/core/Console.latte");
            TestHarness.Check("sourceName[2]",
                roots[2].Span?.sourceName ?? "<null>", "<stdlib>/core/collections.latte");

            TestHarness.Blank();
        }

        // ===== 2a. .bootstrap 结构：恰好 1 个 ext operator callable =====
        private static void TestBootstrapStructure()
        {
            TestHarness.Section("Structure: .bootstrap ext operator");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 1)
            {
                TestHarness.CheckTrue("ParseAll 至少 1 棵（结构断言前置）", false);
                TestHarness.Blank();
                return;
            }
            var root = roots[0];

            TestHarness.CheckTrue("顶层恰好 3 个声明（namespace + ext operator + Pair）",
                root.Declarations.Count == 3, $"实际 {root.Declarations.Count}");
            TestHarness.CheckTrue("首声明是 namespace core",
                root.Declarations.Count > 0
                && root.Declarations[0] is NamespaceDeclarationASTNode,
                root.Declarations.Count > 0 ? root.Declarations[0].GetType().Name : "<none>");
            var fn = root.Declarations.Count > 1
                ? root.Declarations[1] as CallableDeclarationASTNode : null;
            TestHarness.CheckTrue("次声明是 callable（ext operator）", fn != null,
                root.Declarations.Count > 1 ? root.Declarations[1].GetType().Name : "<none>");
            if (fn == null) { TestHarness.Blank(); return; }

            TestHarness.Check("限定名（ext 目标.成员名）", fn.Name, "i32.EnumerateInRange");
            TestHarness.CheckTrue("带 ext 修饰符", fn.Modifiers.Contains(Keywords.EXT));
            TestHarness.CheckTrue("带 pub 修饰符", fn.Modifiers.Contains(Keywords.PUB));
            TestHarness.CheckTrue("Kind 是 Operator", fn.Kind == CallableKind.Operator);
            TestHarness.CheckTrue("有 Body（Latte 自举实现）", fn.Body != null);

            // M52：core.Pair\<TKey, TValue\> 自举声明（SYNTAX §18 解构协议根）
            var pair = root.Declarations.Count > 2
                ? root.Declarations[2] as ClassDeclarationASTNode : null;
            TestHarness.CheckTrue("第三声明是 class（core.Pair）", pair != null,
                root.Declarations.Count > 2 ? root.Declarations[2].GetType().Name : "<none>");
            if (pair != null)
            {
                TestHarness.CheckTrue("Pair 是 open 泛型类",
                    pair.Modifiers.Contains(Keywords.OPEN)
                    && pair.GenericParameters?.Parameters.Count == 2);
            }

            TestHarness.Blank();
        }

        // ===== 2b. Console 结构（namespace core.io + class Console）=====
        private static void TestConsoleStructure()
        {
            TestHarness.Section("Structure: namespace core.io + class Console");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 2)
            {
                TestHarness.CheckTrue("ParseAll 至少 2 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots[1];

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

        // ===== 2c. collections 结构（namespace + 2 interface + 2 class）=====
        private static void TestCollectionsStructure()
        {
            TestHarness.Section("Structure: namespace core.collections");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 3)
            {
                TestHarness.CheckTrue("ParseAll 至少 3 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots[2];

            // 顶层：namespace + IEnumerator/IEnumerable 接口 +
            // RangeEnumeratorI32/RangeI32 类（共 5 个声明）
            TestHarness.CheckTrue("顶层恰好 5 个声明（namespace + 2 interface + 2 class）",
                root.Declarations.Count == 5, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 5) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.collections",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.collections");
            TestHarness.CheckTrue("声明[1] 是 interface IEnumerator",
                root.Declarations[1] is InterfaceDeclarationASTNode iface1
                && iface1.InterfaceName == "IEnumerator");
            TestHarness.CheckTrue("声明[2] 是 interface IEnumerable",
                root.Declarations[2] is InterfaceDeclarationASTNode iface2
                && iface2.InterfaceName == "IEnumerable");
            TestHarness.CheckTrue("声明[3] 是 class RangeEnumeratorI32",
                root.Declarations[3] is ClassDeclarationASTNode cls1
                && cls1.ClassName == "RangeEnumeratorI32");
            TestHarness.CheckTrue("声明[4] 是 class RangeI32",
                root.Declarations[4] is ClassDeclarationASTNode cls2
                && cls2.ClassName == "RangeI32");

            // 接口方法无体（§11）；实现类成员带 override（RangeI32.iterate）
            if (root.Declarations[1] is InterfaceDeclarationASTNode enumerator)
            {
                TestHarness.CheckTrue("IEnumerator 双成员均无 Body（接口无体方法）",
                    enumerator.Members.Count == 2
                    && enumerator.Members.All(m =>
                        m is CallableDeclarationASTNode { Body: null }));
            }
            if (root.Declarations[4] is ClassDeclarationASTNode range)
            {
                var iterate = range.Members.OfType<CallableDeclarationASTNode>()
                    .FirstOrDefault(m => m.Name == "iterate");
                TestHarness.CheckTrue("RangeI32.iterate 带 override 修饰符",
                    iterate != null && iterate.Modifiers.Contains(Keywords.OVERRIDE));
            }

            TestHarness.Blank();
        }

        // ===== 3. Console 描述串精确比对 =====
        private static void TestConsoleDescribe()
        {
            TestHarness.Section("AstDescribe Snapshot (Console)");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 2)
            {
                TestHarness.CheckTrue("ParseAll 至少 2 棵（快照前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }

            TestHarness.Check("Console Root 描述串", AstDescribe.Root(roots[1]),
                "namespace core.io; pub class Console {" +
                @"@NativeLibrary(Str(""latte_rt"")) @NativeSymbol(Str(""print"")) priv static native func print(text: String), " +
                @"@NativeLibrary(Str(""latte_rt"")) @NativeSymbol(Str(""printErr"")) priv static native func printErr(text: String), " +
                "pub static func println(text: String) {}}");

            TestHarness.Blank();
        }
    }
}
