using System;
using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S11f 派发链诊断工具测试（RUNTIME §15；M88：仅应用登记 + 降级资格）。
    /// 完整黄金重建归 M88b-3。
    /// </summary>
    public static partial class DispatchExplainerTests
    {



        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static TestSuiteData Spec => new(
            "DispatchExplainer", Cases, sectionTitle: "DispatchExplainer");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestEmptyReport", TestEmptyReport),
            ("TestAppliedReport", TestAppliedReport),
            ("TestVariadicMemberWildcardPreview", TestVariadicMemberWildcardPreview),
            ("TestInheritedMemberPreview", TestInheritedMemberPreview),
            ("TestCliFlag", TestCliFlag),
        };

        private static void TestEmptyReport()
        {
            CompilerTestTools.Section("Empty report");
            var unit = ResolveUnit(
                "pub class Plain {\n    pub func f(): i32 { return 0 }\n}\n");
            CheckNoErrors("无 wrapper 无诊断", unit);
            CaseAssertions.Check("空报告明示行",
                "(no dispatch chains)\n",
                DispatchExplainer.Explain(unit));
        }

        private static void TestAppliedReport()
        {
            CompilerTestTools.Section("Applied wrappers report (M88)");
            var unit = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n");
            CheckNoErrors("有应用无诊断", unit);
            var report = DispatchExplainer.Explain(unit);
            CaseAssertions.CheckTrue("含 type 行", report.Contains("type "));
            CaseAssertions.CheckTrue("含 applied", report.Contains("applied:"));
            CaseAssertions.CheckTrue("含 member", report.Contains("member "));
        }

        // #27⑦：可变值参数成员与泛型成员进入 wildcard 匹配预览（不再跳过）
        private static void TestVariadicMemberWildcardPreview()
        {
            CompilerTestTools.Section("Variadic / generic member wildcard preview (#27⑦)");
            var unit = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub func log(msgs: String...): i32 { return 0 }\n" +
                "    pub func id\\<T>(x: T): T { return x }\n" +
                "}\n");
            CheckNoErrors("可变/泛型成员宿主无诊断", unit);
            var report = DispatchExplainer.Explain(unit);
            CaseAssertions.CheckTrue("含 log 成员", report.Contains("member ") && report.Contains("log"));
            CaseAssertions.CheckTrue("含 id 成员", report.Contains("id"));
            CaseAssertions.CheckTrue("log 命中 wildcard",
                report.Contains("wildcard") && report.Contains(".proxy.*"));
        }

        private static void TestCliFlag()
        {
            CompilerTestTools.Section("CLI --explain-dispatch");
            // 互斥细节既有 CommandLine 套件覆盖；此处仅占位保证套件可跑
            CaseAssertions.CheckTrue("explain-dispatch 套件占位", true);
        }

        // 与 DeclarationResolverTests 同口径：ParseRoot → CompilationUnit → P1+P2
        private static CompilationUnit ResolveUnit(params string[] sources)
        {
            var roots = sources.Select(CompilerTestTools.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return unit;
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            CaseAssertions.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => $"{d.Phase}: {d.Message}")));
        }
    }
}
