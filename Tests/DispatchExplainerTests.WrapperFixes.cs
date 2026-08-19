namespace RigiCompiler.Tests
{
    // Wrapper 簇：--explain-dispatch 须列出继承成员（§14.2 子类拦截）。
    public static partial class DispatchExplainerTests
    {
        private static void TestInheritedMemberPreview()
        {
            TestHarness.Section("Inherited member dispatch preview");
            var unit = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    operator .proxy.ping(n: i32): i32 { return inner(n) }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "    pub func other(): i32 { return 0 }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Child : Base { }\n");
            CheckNoErrors("子类 wrapper + 继承成员无诊断", unit);
            var report = DispatchExplainer.Explain(unit);
            TestHarness.CheckTrue("Child 报告含继承 ping",
                report.Contains("type Child") && report.Contains("ping"));
            TestHarness.CheckTrue("ping 命中 specific",
                report.Contains("specific") && report.Contains(".proxy.ping"));
            TestHarness.CheckTrue("other 命中 wildcard",
                report.Contains("other") && report.Contains("wildcard"));
        }
    }
}
