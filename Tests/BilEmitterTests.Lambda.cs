using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    public static partial class BilEmitterTests
    {
        private static void TestLambdaEmission()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n");
            CheckNoErrors("无捕获 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("无捕获 lambda 验证器零错误", module);
            TestHarness.CheckTrue("无捕获 lambda 有 synthetic fn declaration",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>()
                    .Any(symbol => symbol.Symbol.Contains(".__lambda.", System.StringComparison.Ordinal)));
            TestHarness.CheckTrue("无捕获 lambda 发射 getid.method",
                text.Contains("getid.method fn("));
            TestHarness.CheckTrue("无捕获 lambda 发射 invoke.indirect",
                text.Contains("invoke.indirect "));
            TestHarness.CheckTrue("methodid canonical 参数/返回签名",
                module.Functions.Any(function => function.Symbol.Contains(".__lambda.",
                    System.StringComparison.Ordinal)
                    && function.Symbol.Contains("x:.i32)@.i32",
                        System.StringComparison.Ordinal)));
        }
    }
}
