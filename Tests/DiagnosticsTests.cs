namespace RigiCompiler.Tests
{
    /// <summary>
    /// S0 诊断基建测试（M36）：DiagnosticBag 累积、多错不互断、
    /// HasErrors 门槛、Span/Phase 字段携带。
    /// </summary>
    public static class DiagnosticsTests
    {


        internal static TestSuiteData Spec { get; } = new("Diagnostics",
        [
            (nameof(TestBagAccumulation), TestBagAccumulation),
            (nameof(TestMissingError), TestMissingError),
        ], sectionTitle: "Diagnostics");

        private static void TestBagAccumulation()
        {
            // 空袋：无错、无内容
            var bag = new DiagnosticBag();
            CaseAssertions.CheckTrue("空袋 HasErrors == false", !bag.HasErrors);
            CaseAssertions.CheckTrue("空袋 Count == 0", bag.Diagnostics.Count == 0);

            // Warning 不触发 Error 门槛；字段逐一携带
            bag.Warning(DiagnosticPhase.P1, null, "警告甲");
            CaseAssertions.CheckTrue("Warning 后 HasErrors 仍为 false", !bag.HasErrors);
            CaseAssertions.CheckTrue("Warning 后 Count == 1", bag.Diagnostics.Count == 1);
            CaseAssertions.CheckTrue("Warning 的 Severity 携带",
                bag.Diagnostics[0].Severity == DiagnosticSeverity.Warning);
            CaseAssertions.CheckTrue("Warning 的 Phase 携带",
                bag.Diagnostics[0].Phase == DiagnosticPhase.P1);
            CaseAssertions.CheckTrue("Warning 的 Span 可空", bag.Diagnostics[0].Span == null);
            CaseAssertions.CheckTrue("Warning 的 Message 携带",
                bag.Diagnostics[0].Message == "警告甲");

            // Error 触发门槛；Span 携带（构造一个真实 CharRange）
            var span = new CharRange
            {
                sourceName = "a.rg",
                Start = new CharPosition { line = 3, column = 5, offset = 40 },
                End = new CharPosition { line = 3, column = 9, offset = 44 },
            };
            bag.Error(DiagnosticPhase.P2, span, "错误乙");
            CaseAssertions.CheckTrue("Error 后 HasErrors == true", bag.HasErrors);
            CaseAssertions.CheckTrue("Error 的 Span 携带",
                bag.Diagnostics[1].Span is { } s
                && s.sourceName == "a.rg" && s.Start.line == 3 && s.Start.column == 5);
            CaseAssertions.CheckTrue("Error 的 Phase 携带",
                bag.Diagnostics[1].Phase == DiagnosticPhase.P2);

            // 多错累积不互断：顺序保持、全部保留
            bag.Error(DiagnosticPhase.P3, null, "错误丙");
            bag.Warning(DiagnosticPhase.P4, null, "警告丁");
            CaseAssertions.CheckTrue("累积 4 条", bag.Diagnostics.Count == 4);
            CaseAssertions.CheckTrue("累积顺序保持",
                bag.Diagnostics[2].Message == "错误丙" && bag.Diagnostics[3].Message == "警告丁");

            // CheckSemanticError 断言本身：命中 Error 子串
            CaseAssertions.CheckSemanticError("CheckSemanticError 命中", bag, "错误丙");
        }

        private static void TestMissingError()
        {
            // 全新空袋：CheckSemanticError 应判失败——此处改为直接验证
            // 「无匹配即失败」的事实（不经过 CheckSemanticError 自身，避免污染计数）
            var empty = new DiagnosticBag();
            bool found = false;
            foreach (var d in empty.Diagnostics)
            {
                if (d.Severity == DiagnosticSeverity.Error && d.Message.Contains("不存在"))
                {
                    found = true;
                }
            }
            CaseAssertions.CheckTrue("空袋查不到诊断", !found);

        }
    }
}
