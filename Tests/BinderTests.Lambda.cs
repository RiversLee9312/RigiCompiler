namespace RigiCompiler.Tests
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

            // 嵌套 lambda 传递捕获：外层方法符号向外传递；外层 lambda 自身
            // 局部/参数不外传（M112：本层 CellLocal 持有）
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
            TestHarness.CheckTrue("嵌套捕获向外层传递外层方法符号 p（不传自身局部 local）",
                outer.CapturedSymbols.Count == 1
                && outer.CapturedSymbols.Any(s => s.Name == "p")
                && !outer.CapturedSymbols.Any(s => s.Name == "local"));
            var nestedFn = outer.CallBody.Body.Statements
                .OfType<BoundLocalDeclarationStatement>()
                .Single(s => s.Local.Name == "nestedFn").Initializer as BoundLambdaExpression;
            TestHarness.CheckTrue("内层捕获 p 与 local",
                nestedFn != null
                && nestedFn.CapturedSymbols.Count == 2
                && nestedFn.CapturedSymbols.Any(s => s.Name == "p")
                && nestedFn.CapturedSymbols.Any(s => s.Name == "local"));

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

            // M105：值位置括号形态 void 间接调用仍报 no result (void)
            var voidGroupedValue = BindUnitWithStdlib(
                "func sink(v: i32) { }\n" +
                "func f() {\n" +
                "    var act: core.Action = func{() -> { sink(0) }}\n" +
                "    var x = (act)()\n" +
                "}\n");
            TestHarness.CheckSemanticError("值位置 (act)() 仍报 void 不可作值",
                voidGroupedValue.Unit.Diagnostics,
                "Method 'call' has no result (void) and cannot be used as a value");

            // M112：外层方法泛型参数 T 的值可 cell 化（捕获 / 嵌套 / 体内注解）
            var methodGenericCapture = BindUnitWithStdlib(
                "func wrap\\<T>(x: T): T {\n" +
                "    var y: T = x\n" +
                "    var f = func{(): T -> y}\n" +
                "    return f()\n" +
                "}\n");
            CheckNoErrors("方法泛型 T 局部捕获 cell 化无诊断", methodGenericCapture.Unit);
            var captureLambda = (BoundLambdaExpression)BodyOf(methodGenericCapture.Bodies, "wrap")
                .Body.Statements.OfType<BoundLocalDeclarationStatement>()
                .Single(s => s.Local.Name == "f").Initializer!;
            var yLocal = BodyOf(methodGenericCapture.Bodies, "wrap").Locals
                .First(l => l.Name == "y");
            TestHarness.CheckTrue("捕获 y 的 cell 子类共享 generic(T)",
                captureLambda.Closure.Captures.Count == 1
                && yLocal.CellStorage != null
                && yLocal.CellStorage.CellClass.GenericParameters.Count == 1
                && yLocal.CellStorage.CellClass.GenericParameters[0].Name == "T");
            TestHarness.CheckTrue("lambda 隐藏类共享 generic(T)",
                captureLambda.Closure.HiddenClass.GenericParameters.Count == 1
                && captureLambda.Closure.HiddenClass.GenericParameters[0].Name == "T");

            var methodGenericParamCapture = BindUnitWithStdlib(
                "func wrap\\<T>(x: T): T {\n" +
                "    var f = func{(): T -> x}\n" +
                "    return f()\n" +
                "}\n");
            CheckNoErrors("方法泛型 T 参数捕获 cell 化无诊断", methodGenericParamCapture.Unit);

            var methodGenericNested = BindUnitWithStdlib(
                "func wrap\\<T>(x: T): T {\n" +
                "    var f = func{(v: T): T -> {\n" +
                "        var g = func{(): T -> v}\n" +
                "        return@_ g()\n" +
                "    }}\n" +
                "    return f(x)\n" +
                "}\n");
            CheckNoErrors("嵌套 lambda 捕获外层方法泛型 T 参数无诊断", methodGenericNested.Unit);
            var outerF = (BoundLambdaExpression)BodyOf(methodGenericNested.Bodies, "wrap")
                .Body.Statements.OfType<BoundLocalDeclarationStatement>()
                .Single(s => s.Local.Name == "f").Initializer!;
            // 嵌套捕获外层 lambda 自身参数 v：不向外层 init 传递（外层 $$call
            // 以 CellLocal 持有）；外层 CapturedSymbols 空
            TestHarness.CheckTrue("外层不传递自身参数捕获",
                outerF.CapturedSymbols.Count == 0
                && outerF.Closure.Captures.Count == 0);
            var nestedG = outerF.CallBody.Body.Statements
                .OfType<BoundLocalDeclarationStatement>()
                .FirstOrDefault(s => s.Local.Name == "g")?.Initializer as BoundLambdaExpression;
            TestHarness.CheckTrue("内层捕获 v 且 cell 化",
                nestedG != null
                && nestedG.CapturedSymbols.Count == 1
                && nestedG.CapturedSymbols.Any(s => s.Name == "v")
                && nestedG.Closure.Captures.Count == 1
                && nestedG.Closure.Captures[0].Field.FieldType is TypeSymbol nestCell
                && (nestCell.CellStorage != null
                    || nestCell.ConstructedFrom?.CellStorage != null
                    || nestCell.Name.StartsWith("..cell..", StringComparison.Ordinal)
                    || nestCell.ConstructedFrom?.Name.StartsWith("..cell..",
                        StringComparison.Ordinal) == true));
            TestHarness.CheckTrue("嵌套两侧隐藏类均共享 generic(T)",
                outerF.Type is TypeSymbol outerLambdaType
                && (outerLambdaType.ConstructedFrom ?? outerLambdaType).GenericParameters.Count
                    == 1
                && (outerLambdaType.ConstructedFrom ?? outerLambdaType).GenericParameters[0].Name
                    == "T"
                && nestedG!.Type is TypeSymbol nestedType
                && (nestedType.ConstructedFrom ?? nestedType).GenericParameters.Count == 1);

            var methodGenericBodyAnnot = BindUnitWithStdlib(
                "func wrap\\<T>(x: T): T {\n" +
                "    var f = func{(v: T): T -> {\n" +
                "        var y: T = v\n" +
                "        return@_ y\n" +
                "    }}\n" +
                "    return f(x)\n" +
                "}\n");
            CheckNoErrors("lambda 体内注解外层方法泛型 T 可解析", methodGenericBodyAnnot.Unit);

            var methodGenericWrite = BindUnitWithStdlib(
                "func wrap\\<T>(x: T): T {\n" +
                "    var y: T = x\n" +
                "    var act = func{(v: T) -> { y = v }}\n" +
                "    act(x)\n" +
                "    return y\n" +
                "}\n");
            CheckNoErrors("方法泛型 T 局部经 cell 读写无诊断", methodGenericWrite.Unit);
        }
    }
}
