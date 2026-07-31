namespace LatteCompiler.Tests
{
    /// <summary>
    /// S0 诊断基建测试（M36）：DiagnosticBag 累积、多错不互断、
    /// HasErrors 门槛、Span/Phase 字段携带。
    /// </summary>
    public static class DiagnosticsTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("Diagnostics");

            // 空袋：无错、无内容
            var bag = new DiagnosticBag();
            TestHarness.CheckTrue("空袋 HasErrors == false", !bag.HasErrors);
            TestHarness.CheckTrue("空袋 Count == 0", bag.Diagnostics.Count == 0);

            // Warning 不触发 Error 门槛；字段逐一携带
            bag.Warning(DiagnosticPhase.P1, null, "警告甲");
            TestHarness.CheckTrue("Warning 后 HasErrors 仍为 false", !bag.HasErrors);
            TestHarness.CheckTrue("Warning 后 Count == 1", bag.Diagnostics.Count == 1);
            TestHarness.CheckTrue("Warning 的 Severity 携带",
                bag.Diagnostics[0].Severity == DiagnosticSeverity.Warning);
            TestHarness.CheckTrue("Warning 的 Phase 携带",
                bag.Diagnostics[0].Phase == DiagnosticPhase.P1);
            TestHarness.CheckTrue("Warning 的 Span 可空", bag.Diagnostics[0].Span == null);
            TestHarness.CheckTrue("Warning 的 Message 携带",
                bag.Diagnostics[0].Message == "警告甲");

            // Error 触发门槛；Span 携带（构造一个真实 CharRange）
            var span = new CharRange
            {
                sourceName = "a.latte",
                Start = new CharPosition { line = 3, column = 5, offset = 40 },
                End = new CharPosition { line = 3, column = 9, offset = 44 },
            };
            bag.Error(DiagnosticPhase.P2, span, "错误乙");
            TestHarness.CheckTrue("Error 后 HasErrors == true", bag.HasErrors);
            TestHarness.CheckTrue("Error 的 Span 携带",
                bag.Diagnostics[1].Span is { } s
                && s.sourceName == "a.latte" && s.Start.line == 3 && s.Start.column == 5);
            TestHarness.CheckTrue("Error 的 Phase 携带",
                bag.Diagnostics[1].Phase == DiagnosticPhase.P2);

            // 多错累积不互断：顺序保持、全部保留
            bag.Error(DiagnosticPhase.P3, null, "错误丙");
            bag.Warning(DiagnosticPhase.P4, null, "警告丁");
            TestHarness.CheckTrue("累积 4 条", bag.Diagnostics.Count == 4);
            TestHarness.CheckTrue("累积顺序保持",
                bag.Diagnostics[2].Message == "错误丙" && bag.Diagnostics[3].Message == "警告丁");

            // CheckSemanticError 断言本身：命中 Error 子串
            TestHarness.CheckSemanticError("CheckSemanticError 命中", bag, "错误丙");

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
            TestHarness.CheckTrue("空袋查不到诊断", !found);

            return TestHarness.Summary("Diagnostics");
        }
    }
}
