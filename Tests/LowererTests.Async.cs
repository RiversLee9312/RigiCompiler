using System.Linq;

namespace RigiCompiler.Tests
{
    public static partial class LowererTests
    {
        private static void TestAwaitLowering()
        {
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "async func load(): String { return \"ok\" }\n" +
                "async func flush() { }\n" +
                "func main() {\n" +
                "    var value = await load()\n" +
                "    await flush()\n" +
                "}\n");
            CheckNoErrors("await P4a 无诊断", unit);
            var statements = BodyOf(lowered, "main").Body.Statements;
            CaseAssertions.CheckTrue("Task<T> 值 await Lowered 节点",
                statements[0] is LoweredLocalDeclarationStatement
                && ((LoweredLocalDeclarationStatement)statements[0]).Initializer
                    is LoweredAwaitExpression { HasResult: true });
            CaseAssertions.CheckTrue("Task 语句 await Lowered 节点",
                statements[1] is LoweredExpressionStatement
                && ((LoweredExpressionStatement)statements[1]).Expression
                    is LoweredAwaitExpression { HasResult: false });
        }

        private static void TestYieldLowering()
        {
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "import core.coroutine.*\n" +
                "func main() {\n" +
                "    yield\n" +
                "    yield sleep(1)\n" +
                "}");
            CheckNoErrors("yield P4a 无诊断", unit);
            var statements = BodyOf(lowered, "main").Body.Statements;
            CaseAssertions.CheckTrue("yield Lowered 裸/Alarm 节点",
                statements[0] is LoweredYieldStatement { Alarm: null }
                && statements[1] is LoweredYieldStatement { Alarm: not null });
        }
    }
}
