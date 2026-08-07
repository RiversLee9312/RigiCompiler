namespace LatteCompiler.Tests
{
    public static partial class BinderTests
    {
        private static void TestLambdaBinding()
        {
            TestHarness.Section("P3 Lambda Binding (S13 Slice A)");
            var (unit, bodies) = BindUnit(
                "func f(p: i32): i32 {\n" +
                "    const c = 1\n" +
                "    var v = 2\n" +
                "    var fn = func{(x: i32): i32 -> (x + p)}\n" +
                "    var block = func{(x: i32): i32 -> { var localValue = x\n" +
                "        return@_ localValue }}\n" +
                "    return v\n" +
                "}\n");
            CheckNoErrors("普通 lambda 单表达式/块体无诊断", unit);
            TestHarness.CheckTrue("captured lambda 保持 P3 无诊断",
                unit.Diagnostics.Diagnostics.All(d => d.Phase != DiagnosticPhase.P4));
            var statements = BodyOf(bodies, "f").Body.Statements;
            var first = ((BoundLocalDeclarationStatement)statements[2]).Initializer;
            var second = ((BoundLocalDeclarationStatement)statements[3]).Initializer;
            TestHarness.CheckTrue("lambda 类型为匿名 callable 且不入用户类型图",
                first is BoundLambdaExpression { Type: LambdaTypeSymbol }
                && !unit.Symbols.GlobalNamespace.Types.Any(t => t is LambdaTypeSymbol));
            TestHarness.CheckTrue("参数/单表达式捕获按符号身份记录",
                first is BoundLambdaExpression expression
                && expression.Parameters.Count == 1
                && expression.ReturnType == unit.Symbols.Bootstrap.Int32
                && expression.CapturedSymbols.Count == 1
                && expression.CapturedSymbols.Any(s => s.Name == "p"));
            TestHarness.CheckTrue("块体绑定并排除体内声明捕获",
                second is BoundLambdaExpression { BlockBody: not null } block
                && block.CapturedSymbols.Count == 0);

            var flow = BindUnit(
                "func f() {\n" +
                "    var v = 1\n" +
                "    var fn = func{(): i32 -> (v + 1)}\n" +
                "}\n");
            CheckNoErrors("lambda 继承外层 DA 且局部状态不外泄", flow.Unit);

            var nested = BindUnit(
                "func f(p: i32) {\n" +
                "    var outer = func{(): i32 -> { var local = p\n" +
                "        var nestedFn = func{(): i32 -> { return@_ (p + local) }}\n" +
                "        return@_ local }}\n" +
                "}\n");
            CheckNoErrors("嵌套 lambda 传递捕获", nested.Unit);
            var outer = (BoundLambdaExpression)BodyOf(nested.Bodies, "f").Body.Statements
                .OfType<BoundLocalDeclarationStatement>()
                .Single(statement => statement.Local.Name == "outer").Initializer!;
            TestHarness.CheckTrue("嵌套捕获向外层按符号传递",
                outer.CapturedSymbols.Count == 2
                && outer.CapturedSymbols.Any(s => s.Name == "p")
                && outer.CapturedSymbols.Any(s => s.Name == "local"));

            var badName = BindUnit(
                "func f() { var fn = func{(x: i32): i32 -> missing} }\n");
            TestHarness.CheckSemanticError("lambda 未定义名", badName.Unit.Diagnostics,
                "Undefined");

            var badReturn = BindUnit(
                "func f() { var fn = func{(x: i32): String -> x} }\n");
            TestHarness.CheckSemanticError("lambda 返回类型错误", badReturn.Unit.Diagnostics,
                "Lambda result");

            var implicitBlock = BindUnit(
                "func f() { var fn = func{(): i32 -> { 42 }} }\n");
            TestHarness.CheckSemanticError("lambda 块体禁止隐式返回", implicitBlock.Unit.Diagnostics,
                "explicitly return@");
        }
    }
}
