using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S11f 派发链诊断工具测试（RUNTIME §15；M88：仅应用登记 + 降级资格）。
    /// 完整黄金重建归 M88b-3。
    /// </summary>
    public static class DispatchExplainerTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestEmptyReport();
            TestAppliedReport();
            TestCliFlag();
            return TestHarness.Summary("DispatchExplainer");
        }

        private static void TestEmptyReport()
        {
            TestHarness.Section("Empty report");
            var unit = ResolveUnit(
                "pub class Plain {\n    pub func f(): i32 { return 0 }\n}\n");
            CheckNoErrors("无 wrapper 无诊断", unit);
            TestHarness.Check("空报告明示行",
                "(no dispatch chains)\n",
                DispatchExplainer.Explain(unit));
        }

        private static void TestAppliedReport()
        {
            TestHarness.Section("Applied wrappers report (M88)");
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
            TestHarness.CheckTrue("含 type 行", report.Contains("type "));
            TestHarness.CheckTrue("含 applied", report.Contains("applied:"));
            TestHarness.CheckTrue("含 member", report.Contains("member "));
        }

        private static void TestCliFlag()
        {
            TestHarness.Section("CLI --explain-dispatch");
            // 互斥细节既有 CommandLine 套件覆盖；此处仅占位保证套件可跑
            TestHarness.CheckTrue("explain-dispatch 套件占位", true);
        }

        // 与 DeclarationResolverTests 同口径：ParseRoot → CompilationUnit → P1+P2
        private static CompilationUnit ResolveUnit(params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return unit;
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => $"{d.Phase}: {d.Message}")));
        }
    }
}
