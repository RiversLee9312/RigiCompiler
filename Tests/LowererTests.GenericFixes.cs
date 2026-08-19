using System.Linq;

namespace RigiCompiler.Tests
{
    // P4a：类级泛型参数作为调用 TypeArguments 透传（发射侧再打进方法帧）。
    public static partial class LowererTests
    {
        private static void TestClassGenericCallTypeArguments()
        {
            var (unit, _, lowered) = LowerUnit(
                "func identity\\<T>(x: T): T { return x }\n" +
                "class Repo\\<TItem> {\n" +
                "    pub func pass(x: TItem): TItem { return identity\\<TItem>(x) }\n" +
                "    pub func mix\\<U>(x: TItem, y: U): U { return identity\\<U>(y) }\n" +
                "}\n");
            CheckNoErrors("无诊断（类泛型作调用实参）", unit);
            var pass = BodyOf(lowered, "pass");
            var passCall = pass.Body.Statements
                .OfType<LoweredReturnStatement>()
                .Select(s => s.Value)
                .OfType<LoweredCallExpression>()
                .Single();
            TestHarness.CheckTrue("pass 调用 TypeArguments 含类级 TItem",
                passCall.TypeArguments.Count == 1
                && passCall.TypeArguments[0] is GenericParameterSymbol { Name: "TItem" });

            var mix = BodyOf(lowered, "mix");
            var mixCall = mix.Body.Statements
                .OfType<LoweredReturnStatement>()
                .Select(s => s.Value)
                .OfType<LoweredCallExpression>()
                .Single();
            TestHarness.CheckTrue("mix 调用 TypeArguments 含方法级 U（不丢类级混用）",
                mixCall.TypeArguments.Count == 1
                && mixCall.TypeArguments[0] is GenericParameterSymbol { Name: "U" });
        }
    }
}
