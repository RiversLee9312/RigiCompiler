namespace LatteCompiler.Tests
{
    public static partial class BinderTests
    {
        private static void TestLambdaBinding()
        {
            TestHarness.Section("P3 Lambda Binding（§5.2 对象模型）");
            var (unit, bodies) = BindUnitWithStdlib(
                "func f(p: i32): i32 {\n" +
                "    const c = 1\n" +
                "    var v = 2\n" +
                "    var fn = func{(x: i32): i32 -> (x + p)}\n" +
                "    var block = func{(x: i32): i32 -> { var localValue = x\n" +
                "        return@_ localValue }}\n" +
                "    return v\n" +
                "}\n");
            CheckNoErrors("普通 lambda 单表达式/块体无诊断", unit);
            var statements = BodyOf(bodies, "f").Body.Statements;
            var first = (BoundLambdaExpression)((BoundLocalDeclarationStatement)statements[2])
                .Initializer!;
            var second = (BoundLambdaExpression)((BoundLocalDeclarationStatement)statements[3])
                .Initializer!;

            // 隐藏类身份（SYNTAX §5.2）：..lambda..UUID 命名、同命名空间、
            // 继承 core::Func\<i32, i32\>、不入用户符号图
            TestHarness.CheckTrue("lambda 类型为隐藏类（LambdaClosure 标记 + 不入用户类型图）",
                first.Type is TypeSymbol { LambdaClosure: not null } hidden
                && hidden.Name.StartsWith("..lambda..", StringComparison.Ordinal)
                && !unit.Symbols.GlobalNamespace.Types.Any(t => t.LambdaClosure != null));
            var hiddenClass = (TypeSymbol)first.Type;
            TestHarness.CheckTrue("隐藏类基类 = core::Func<i32, i32>（TRet 在前）",
                hiddenClass.BaseType is { ConstructedFrom: { } baseDefinition }
                && baseDefinition.Name == "Func"
                && hiddenClass.BaseType.TypeArguments!.Count == 2);
            TestHarness.CheckTrue("隐藏类非 shared（普通 lambda）",
                !hiddenClass.IsShared);
            // $$call 运算符：覆写基类 abstract call、参数/返回齐备
            TestHarness.CheckTrue("$$call 覆写运算符齐备",
                first.Closure.Call.Kind == MethodKind.Operator
                && first.Closure.Call.IsOverride
                && first.Closure.Call.Parameters.Count == 1
                && first.Closure.Call.ReturnType == unit.Symbols.Bootstrap.Int32);
            // 捕获按符号身份记录 + 闭包字段（var p → Cell 字段）
            TestHarness.CheckTrue("参数捕获按符号身份记录",
                first.CapturedSymbols.Count == 1
                && first.CapturedSymbols.Any(s => s.Name == "p"));
            TestHarness.CheckTrue("var 捕获 → .capture 字段 + cell 隐藏子类（基类 Cell）",
                first.Closure.Captures.Count == 1
                && first.Closure.Captures[0].Field.Name == ".capture.p"
                && first.Closure.Captures[0].Field.FieldType is TypeSymbol
                {
                    CellStorage: not null,
                    BaseType.ConstructedFrom: { Name: "Cell" }
                });
            TestHarness.CheckTrue("被捕获参数置 CellStorage 标记（Cell 风味）",
                first.CapturedSymbols.OfType<ParameterSymbol>().Single()
                    .CellStorage is { IsReadOnly: false });
            TestHarness.CheckTrue("init 参数 = 捕获序（c0 = cell 类型）",
                first.Closure.Init.Parameters.Count == 1
                && first.Closure.Init.Parameters[0].Name == "c0");
            // 块体排除体内声明捕获（localValue 是 lambda 体内局部）
            TestHarness.CheckTrue("块体绑定并排除体内声明捕获",
                second.CapturedSymbols.Count == 0
                && second.Closure.Captures.Count == 0);

            // const 捕获 → ReadonlyCell
            var constCapture = BindUnitWithStdlib(
                "func f(): i32 {\n" +
                "    const c = 41\n" +
                "    var fn = func{(): i32 -> (c + 1)}\n" +
                "    return fn()\n" +
                "}\n");
            CheckNoErrors("const 捕获无诊断", constCapture.Unit);
            var constLambda = (BoundLambdaExpression)BodyOf(constCapture.Bodies, "f")
                .Body.Statements.OfType<BoundLocalDeclarationStatement>()
                .Single(s => s.Local.Name == "fn").Initializer!;
            TestHarness.CheckTrue("const 捕获 → ReadonlyCell 子类字段 + 符号标记",
                constLambda.Closure.Captures[0].IsReadOnly
                && constLambda.Closure.Captures[0].Field.FieldType is TypeSymbol
                {
                    CellStorage: not null,
                    BaseType.ConstructedFrom: { Name: "ReadonlyCell" }
                }
                && constLambda.CapturedSymbols.OfType<LocalSymbol>().Single()
                    .CellStorage is { IsReadOnly: true });

            // void lambda（省略返回类型）：基类 Action 族
            var voidLambda = BindUnitWithStdlib(
                "func sink(x: i32) { }\n" +
                "func f() {\n" +
                "    var act = func{() -> sink(1)}\n" +
                "}\n");
            var voidAction = (BoundLambdaExpression)BodyOf(voidLambda.Bodies, "f")
                .Body.Statements.OfType<BoundLocalDeclarationStatement>()
                .Single().Initializer!;
            TestHarness.CheckTrue("void lambda 基类 = core::Action（零元数）",
                voidAction.ReturnType == null
                && ((TypeSymbol)voidAction.Type).BaseType is { } actionBase
                && (actionBase.ConstructedFrom ?? actionBase).Name == "Action");

            // 嵌套 lambda 传递捕获
            var nested = BindUnitWithStdlib(
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

            // this 捕获（普通字段，不套 Cell）
            var thisCapture = BindUnitWithStdlib(
                "class Counter { pub var n: i32\n" +
                "    pub func bump() { var fn = func{() -> { n = (n + 1) }} } }\n");
            CheckNoErrors("this 捕获（隐式实例字段访问）无诊断", thisCapture.Unit);
            var thisLambda = (BoundLambdaExpression)BodyOf(thisCapture.Bodies, "bump")
                .Body.Statements.OfType<BoundLocalDeclarationStatement>()
                .Single().Initializer!;
            TestHarness.CheckTrue("this 捕获为普通字段（不套 Cell）",
                thisLambda.Closure.Captures.Count == 1
                && thisLambda.Closure.Captures[0].IsThis
                && thisLambda.Closure.Captures[0].Field.Name == ".capture.this"
                && thisLambda.Closure.Captures[0].Field.FieldType is TypeSymbol
                {
                    Name: "Counter"
                });

            var badName = BindUnitWithStdlib(
                "func f() { var fn = func{(x: i32): i32 -> missing} }\n");
            TestHarness.CheckSemanticError("lambda 未定义名", badName.Unit.Diagnostics,
                "Undefined");

            var badReturn = BindUnitWithStdlib(
                "func f() { var fn = func{(x: i32): String -> x} }\n");
            TestHarness.CheckSemanticError("lambda 返回类型错误", badReturn.Unit.Diagnostics,
                "Lambda result");

            var implicitBlock = BindUnitWithStdlib(
                "func f() { var fn = func{(): i32 -> { 42 }} }\n");
            TestHarness.CheckSemanticError("lambda 块体禁止隐式返回", implicitBlock.Unit.Diagnostics,
                "explicitly return@");
        }
    }
}
