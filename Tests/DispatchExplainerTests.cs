using System;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S11f 派发链诊断工具测试（RUNTIME §15）：DispatchExplainer 报告黄金 +
    /// CLI --explain-dispatch 标志注册/互斥。数据源 = S11a/S11e 符号产物。
    /// </summary>
    public static class DispatchExplainerTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestEmptyReport();
            TestSpecificChain();
            TestWildcardChain();
            TestMultiLayerOrder();
            TestDowngradeRoute();
            TestCliFlag();
            return TestHarness.Summary("DispatchExplainer");
        }

        // ① 无 wrapper → 明示空报告
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

        // ② Entity wrapper specific 命中链
        private static void TestSpecificChain()
        {
            TestHarness.Section("Specific chain");
            var unit = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init(level: String = \"INFO\")\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n");
            CheckNoErrors("specific 链无诊断", unit);
            TestHarness.Check("specific 单环黄金黄金",
                "type Service\n" +
                "  applied: Logged@Logged<Service>\n" +
                "  member Service$doSomething(arg:.i32)@.string\n" +
                "    [0] specific .proxy.doSomething Service$.proxy.0.doSomething(arg:.i32)@.string\n" +
                "    wrapped Service$.wrapped.doSomething(arg:.i32)@.string\n",
                DispatchExplainer.Explain(unit));
        }

        // ③ wildcard 命中链
        private static void TestWildcardChain()
        {
            TestHarness.Section("Wildcard chain");
            var unit = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub func other(): i32 { return 0 }\n" +
                "}\n");
            CheckNoErrors("wildcard 链无诊断", unit);
            // wildcard 同时合成 member 链与 downgrade 路由
            TestHarness.Check("wildcard 单环 + 降级路由黄金",
                "type Service\n" +
                "  applied: Audited@Audited\n" +
                "  member Service$other()@.i32\n" +
                "    [0] wildcard .proxy.* Service$.proxy.0.other()@.i32\n" +
                "    wrapped Service$.wrapped.other()@.i32\n" +
                "  downgrade\n" +
                "    router Service$call???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n" +
                "    [0] Service$.proxy.0.???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n" +
                "    end core::Any$call???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n",
                DispatchExplainer.Explain(unit));
        }

        // ④ 多层 wrapper outer→inner 顺序
        private static void TestMultiLayerOrder()
        {
            TestHarness.Section("Multi-layer outer→inner");
            var unit = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init(level: String = \"INFO\")\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Logged\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n");
            CheckNoErrors("双层链无诊断", unit);
            TestHarness.Check("双层 outer→inner（specific Logged → wildcard Audited）",
                "type Service\n" +
                "  applied: Logged@Logged<Service> Audited@Audited\n" +
                "  member Service$doSomething(arg:.i32)@.string\n" +
                "    [0] specific .proxy.doSomething Service$.proxy.0.doSomething(arg:.i32)@.string\n" +
                "    [1] wildcard .proxy.* Service$.proxy.1.doSomething(arg:.i32)@.string\n" +
                "    wrapped Service$.wrapped.doSomething(arg:.i32)@.string\n" +
                "  downgrade\n" +
                "    router Service$call???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n" +
                "    [0] Service$.proxy.0.???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n" +
                "    end core::Any$call???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n",
                DispatchExplainer.Explain(unit));
        }

        // ⑤ 降级路由（带 .proxy.* 的类型 → router + 链 + Any.call???）
        private static void TestDowngradeRoute()
        {
            TestHarness.Section("Downgrade route");
            // specific-only：有 member 链、无降级路由
            var unitNo = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n");
            CheckNoErrors("specific-only 无诊断", unitNo);
            var reportNo = DispatchExplainer.Explain(unitNo);
            TestHarness.CheckTrue("specific-only 报告无 downgrade 段",
                !reportNo.Contains("downgrade"));

            // 双 .proxy.* 应用 → 双环降级
            var unit = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper A {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper B {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@A\n" +
                "@B\n" +
                "pub class Host { }\n");
            CheckNoErrors("双环降级无诊断", unit);
            TestHarness.Check("双环降级路由黄金（无被拦截成员）",
                "type Host\n" +
                "  applied: A@A B@B\n" +
                "  downgrade\n" +
                "    router Host$call???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n" +
                "    [0] Host$.proxy.0.???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n" +
                "    [1] Host$.proxy.1.???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n" +
                "    end core::Any$call???(symbol:.string,namedArgs:.array<.any>,unnamedArgs:.array<.any>)@.any\n",
                DispatchExplainer.Explain(unit));
        }

        // ⑥ CLI 级解析：标志注册 + 互斥
        private static void TestCliFlag()
        {
            TestHarness.Section("CLI --explain-dispatch");
            var compile = CommandLineRegistry.Commands.First(c => c.Mask.Name == "compile");
            var subs = compile.SubCommands.Select(s => s.Mask.Name).ToList();
            TestHarness.CheckTrue("--explain-dispatch 已注册",
                subs.Contains("--explain-dispatch"));
            var opt = compile.SubCommands.Single(s => s.Mask.Name == "--explain-dispatch");
            TestHarness.CheckTrue("--explain-dispatch 无参标志",
                opt.Mask.MinArgs == 0 && opt.Mask.MaxArgs == 0);
            TestHarness.CheckTrue("互斥引用存在",
                opt.Mask.MutuallyExclusive.Contains("--parse-only")
                && opt.Mask.MutuallyExclusive.Contains("--emit-bil")
                && opt.Mask.MutuallyExclusive.Contains("--sema-only"));

            CheckParseOk("compile --file a --explain-dispatch",
                new[] { "compile", "--file", "a", "--explain-dispatch" },
                r => r.Has("--explain-dispatch") && r.Get("--explain-dispatch")!.Count == 0);
            CheckParseError("explain-dispatch 与 parse-only 互斥",
                new[] { "compile", "--file", "a", "--explain-dispatch", "--parse-only" }, "互斥");
            CheckParseError("explain-dispatch 与 emit-bil 互斥",
                new[] { "compile", "--file", "a", "--explain-dispatch", "--emit-bil", "o" }, "互斥");
            CheckParseError("explain-dispatch 与 sema-only 互斥",
                new[] { "compile", "--file", "a", "--explain-dispatch", "--sema-only" }, "互斥");
            CheckParseError("emit-bil 与 explain-dispatch 互斥（反向）",
                new[] { "compile", "--file", "a", "--emit-bil", "o", "--explain-dispatch" }, "互斥");
        }

        // ===== 驱动 / 断言 =====

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

        private static void CheckParseOk(string label, string[] args, Func<CommandLineParseResult, bool> predicate)
        {
            if (!CommandLineParser.TryParse(args, out var result, out var error))
            {
                TestHarness.CheckTrue(label + " (unexpected: " + error + ")", false);
                return;
            }
            TestHarness.CheckTrue(label, predicate(result!));
        }

        private static void CheckParseError(string label, string[] args, string messagePart)
        {
            if (CommandLineParser.TryParse(args, out _, out var error))
            {
                TestHarness.CheckTrue(label + " (expected error)", false);
                return;
            }
            TestHarness.CheckTrue(label, error != null && error.Contains(messagePart),
                "got: " + error);
        }
    }
}
