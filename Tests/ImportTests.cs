using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// import 语句解析测试（SYNTAX §15.2，P5 ImportParserLayer 重建）
    ///
    /// 覆盖：
    /// 1. 单个导入（单段/多段路径）
    /// 2. 全部导入（.*）
    /// 3. 多个导入（.{A, B} 共享前缀，展开为独立完整路径）
    /// 4. 错误用例（空路径、空列表、尾随逗号、* 后多余 token、{} 无前缀、列表项带路径）
    ///
    /// 驱动方式：CompilerTestTools.ParseRoot 完整入口（import 是顶层语句）。
    /// </summary>
    public class ImportTests
    {
        // ===== 1. 单个导入 =====
        public static void TestSingleImport()
        {
            CompilerTestTools.Section("Single Import");

            TestImport("import core.collections.List", "import core.collections.List");
            TestImport("import core.String", "import core.String");
            TestImport("import a.b.c.d.E", "import a.b.c.d.E");

            CompilerTestTools.Blank();
        }

        // ===== 2. 全部导入 =====
        public static void TestImportAll()
        {
            CompilerTestTools.Section("Import All");

            TestImport("import core.collections.*", "import core.collections.*");
            TestImport("import core.*", "import core.*");

            CompilerTestTools.Blank();
        }

        // ===== 3. 多个导入（共享前缀，展开为独立完整路径）=====
        public static void TestMultiImport()
        {
            CompilerTestTools.Section("Multi Import");

            TestImport("import core.collections.{List, Map}",
                "import core.collections.List; import core.collections.Map");
            TestImport("import a.b.{X, Y, Z}",
                "import a.b.X; import a.b.Y; import a.b.Z");
            TestImport("import core.{List}", "import core.List");

            CompilerTestTools.Blank();
        }

        // ===== 4. 错误用例 =====
        public static void TestImportErrors()
        {
            CompilerTestTools.Section("Import Errors");

            CaseAssertions.CheckParseError("import（缺少导入路径）",
                () => CompilerTestTools.ParseRoot("import"),
                "requires an import path");
            CaseAssertions.CheckParseError("import core.collections.{}（空导入列表）",
                () => CompilerTestTools.ParseRoot("import core.collections.{}"),
                "Import list cannot be empty");
            CaseAssertions.CheckParseError("import core.collections.{List,}（尾随逗号）",
                () => CompilerTestTools.ParseRoot("import core.collections.{List,}"),
                "Trailing comma in import list");
            CaseAssertions.CheckParseError("import core.*.Foo（* 后多余 token）",
                () => CompilerTestTools.ParseRoot("import core.*.Foo"),
                "Unexpected token after import statement");
            CaseAssertions.CheckParseError("import core.{A} extra（导入列表后多余 token）",
                () => CompilerTestTools.ParseRoot("import core.{A} extra"),
                "Unexpected token after import statement");
            CaseAssertions.CheckParseError("import {List}（{} 缺少前缀路径）",
                () => CompilerTestTools.ParseRoot("import {List}"),
                "requires an import path");
            CaseAssertions.CheckParseError("import a.{b.c}（列表项带路径，§15.2 只允许单标识符）",
                () => CompilerTestTools.ParseRoot("import a.{b.c}"),
                "Import list item must be a single identifier");

            CompilerTestTools.Blank();
        }

        // ===== 辅助方法 =====

        // 全管线解析并比对顶层 AST 描述串
        private static void TestImport(string source, string expected)
        {
            try
            {
                var root = CompilerTestTools.ParseRoot(source);
                CaseAssertions.Check(source, AstDescribe.Root(root), expected);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"{source} => 意外异常", false, ex.Message);
            }
        }

        // ===== 入口 =====


        internal static TestSuiteData Spec { get; } = new("Import",
        [
            (nameof(TestSingleImport), TestSingleImport),
            (nameof(TestImportAll), TestImportAll),
            (nameof(TestMultiImport), TestMultiImport),
            (nameof(TestImportErrors), TestImportErrors),
        ], sectionTitle: "Import");
    }
}
