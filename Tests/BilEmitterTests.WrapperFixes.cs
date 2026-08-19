namespace RigiCompiler.Tests
{
    // Wrapper 簇发射：§14.5 语句位 void 经 wrapper place 发 invoke.noret。
    public static partial class BilEmitterTests
    {
        private static void TestWrapperPlaceVoidCallEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func bump() { }\n" +
                "}\n" +
                "@Logged(\"INFO\")\n" +
                "pub class Svc { pub init() }\n" +
                "pub func f(s: Svc) {\n" +
                "    s:Logged.bump()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（wrapper place void）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（wrapper place void）", module);
            BilTestHarness.CheckFnShape("语句位 void：get.wrapper + invoke.noret",
                module, "$f(s:Svc)@.void",
                ".vars { Logged .s0, Logged .t0 }\n" +
                "get.wrapper $s type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "invoke.noret fn(Logged$bump()@.void) [$.s0]\n" +
                "ret\n");
        }
    }
}
