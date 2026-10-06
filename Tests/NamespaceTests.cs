using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// namespace 声明解析测试（SYNTAX §15.1，P5 收尾）
    ///
    /// 覆盖：
    /// 1. 基本形态（单段/多段路径）
    /// 2. 与 import / 顶层声明组合（模块系统完整文件头）
    /// 3. 错误用例（空路径、路径后多余 token）
    ///
    /// 驱动方式：CompilerTestTools.ParseRoot 完整入口（namespace 是顶层声明）。
    /// </summary>
    public class NamespaceTests
    {
        // ===== 1. 基本形态 =====
        public static void TestBasicNamespace()
        {
            CompilerTestTools.Section("Basic Namespace");

            TestNamespace("namespace com.example.myapp", "namespace com.example.myapp");
            TestNamespace("namespace core", "namespace core");

            CompilerTestTools.Blank();
        }

        // ===== 2. 与 import / 顶层声明组合 =====
        public static void TestNamespaceCombinations()
        {
            CompilerTestTools.Section("Namespace Combinations");

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

            CompilerTestTools.Blank();
        }

        // ===== 3. 错误用例 =====
        public static void TestNamespaceErrors()
        {
            CompilerTestTools.Section("Namespace Errors");

            CaseAssertions.CheckParseError("namespace（缺少命名空间路径）",
                () => CompilerTestTools.ParseRoot("namespace"),
                "requires a namespace path");
            CaseAssertions.CheckParseError("namespace com.example extra（路径后多余 token）",
                () => CompilerTestTools.ParseRoot("namespace com.example extra"),
                "Unexpected token in namespace declaration");
            CaseAssertions.CheckParseError("namespace com.{example}（路径中不允许 {} 列表）",
                () => CompilerTestTools.ParseRoot("namespace com.{example}"),
                "Unexpected token in namespace declaration");

            CompilerTestTools.Blank();
        }

        // ===== 辅助方法 =====

        // 全管线解析并比对顶层 AST 描述串
        private static void TestNamespace(string source, string expected)
        {
            try
            {
                var root = CompilerTestTools.ParseRoot(source);
                CaseAssertions.Check(source.Replace("\n", "\\n"), AstDescribe.Root(root), expected);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"{source.Replace("\n", "\\n")} => 意外异常", false, ex.Message);
            }
        }

        // ===== 入口 =====


        internal static TestSuiteData Spec { get; } = new("Namespace",
        [
            (nameof(TestBasicNamespace), TestBasicNamespace),
            (nameof(TestNamespaceCombinations), TestNamespaceCombinations),
            (nameof(TestNamespaceErrors), TestNamespaceErrors),
        ], sectionTitle: "Namespace");
    }
}
