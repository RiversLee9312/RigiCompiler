using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    // LowererTests 基础组：局部声明/赋值/表达式与调用语句/一元与字段引用/
    // new 构造/Origin 调试链 + 未覆盖节点负例（测试私有 Bound 子类注入）。

    public static partial class LowererTests
    {
        // ===== 局部变量声明（显式标注 / var 推断 / 无初始化器）=====
        private static void TestLocalDeclarations()
        {
            var (unit, _, lowered) = LowerUnit(
                "func f() {\n" +
                "    var x: i32 = 42\n" +
                "    var y = 1 + 2\n" +
                "    var z: i32\n" +
                "}\n");
            CheckNoErrors("无诊断（局部声明）", unit);
            TestHarness.Check("局部声明 Lowered 形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [x: i32, y: i32, z: i32], [Decl(x, i32, = Int(42,i32)); " +
                "Decl(y, i32, = Binary(Add, Int(1,i32), Int(2,i32), i32)); Decl(z, i32)])");
        }

        // ===== 赋值（局部目标）=====
        private static void TestAssignment()
        {
            var (unit, _, lowered) = LowerUnit(
                "func f() {\n" +
                "    var x: i32 = 0\n" +
                "    x = 5\n" +
                "}\n");
            CheckNoErrors("无诊断（赋值）", unit);
            TestHarness.Check("赋值 Lowered 形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "Assign(Local(x,i32), Int(5,i32))])");
        }

        // ===== 表达式语句（带返回值调用结果丢弃）与 void 调用语句 =====
        private static void TestExpressionAndCallStatements()
        {
            var (unit, _, lowered) = LowerUnit(
                "func add(a: i32, b: i32): i32 { return a + b }\n" +
                "func use(v: i32) { }\n" +
                "func f() {\n" +
                "    add(1, 2)\n" +
                "    use(3)\n" +
                "    return\n" +
                "}\n");
            CheckNoErrors("无诊断（表达式/调用语句）", unit);
            TestHarness.Check("带返回值调用", LoweredDescribe.Body(BodyOf(lowered, "add")),
                "Body(add, [], [Return(Binary(Add, Param(a,i32), Param(b,i32), i32))])");
            TestHarness.Check("表达式语句 + void 调用 + 裸 return",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [ExprStmt(Call(add, [Int(1,i32), Int(2,i32)], i32)); " +
                "CallStmt(use, [Int(3,i32)]); Return])");
        }

        // ===== 一元运算与全局/static 字段引用 =====
        private static void TestUnaryAndFieldReference()
        {
            var (unit, _, lowered) = LowerUnit(
                "class Counter { pub static var value: i32 }\n" +
                "func neg(a: i32): i32 { return -a }\n" +
                "func read(): i32 { return Counter.value }\n" +
                "func write() { Counter.value = 42 }\n");
            CheckNoErrors("无诊断（一元/字段引用）", unit);
            TestHarness.Check("一元运算 Lowered 形态", LoweredDescribe.Body(BodyOf(lowered, "neg")),
                "Body(neg, [], [Return(Unary(Opposite, Param(a,i32), i32))])");
            TestHarness.Check("static 字段读取", LoweredDescribe.Body(BodyOf(lowered, "read")),
                "Body(read, [], [Return(Field(value,i32))])");
            TestHarness.Check("static 字段写入", LoweredDescribe.Body(BodyOf(lowered, "write")),
                "Body(write, [], [Assign(Field(value,i32), Int(42,i32))])");

            // 结构性事实：字段符号引用相等（符号图唯一实例）
            var counterType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Counter");
            var fieldSymbol = counterType.Fields.Single(f => f.Name == "value");
            var readReturn = (LoweredReturnStatement)BodyOf(lowered, "read").Body.Statements[0];
            var fieldRef = readReturn.Value as LoweredFieldReferenceExpression;
            TestHarness.CheckTrue("字段符号引用相等",
                fieldRef != null && ReferenceEquals(fieldRef.Field, fieldSymbol));
        }

        // ===== new 构造（零参无 init / 带参匹配 init）=====
        private static void TestNew()
        {
            var (unit, _, lowered) = LowerUnit(
                "class Empty { }\n" +
                "class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "}\n" +
                "func e(): Empty { return new Empty() }\n" +
                "func p(): Point { return new Point(42) }\n");
            CheckNoErrors("无诊断（new）", unit);
            TestHarness.Check("零参构造 Lowered 形态", LoweredDescribe.Body(BodyOf(lowered, "e")),
                "Body(e, [], [Return(New(Empty, []))])");
            TestHarness.Check("带参构造 Lowered 形态", LoweredDescribe.Body(BodyOf(lowered, "p")),
                "Body(p, [], [Return(New(Point, init, [Int(42,i32)]))])");

            // 结构性事实：Init 符号引用相等
            var pointType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Point");
            var pReturn = (LoweredReturnStatement)BodyOf(lowered, "p").Body.Statements[0];
            var newExpr = pReturn.Value as LoweredNewExpression;
            TestHarness.CheckTrue("init 符号引用相等",
                newExpr?.Init != null && ReferenceEquals(newExpr.Init,
                    pointType.Methods.Single(m => m.Kind == MethodKind.Init)));
        }

        // ===== Origin 调试链（LoweredNode.Origin 回指 Bound 节点，引用相等）=====
        private static void TestOriginChain()
        {
            var (unit, bound, lowered) = LowerUnit(
                "func f(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（Origin 链）", unit);
            var boundBody = bound.Single(b => b.Method.Name == "f");
            var loweredBody = BodyOf(lowered, "f");
            TestHarness.CheckTrue("方法符号同一实例",
                ReferenceEquals(boundBody.Method, loweredBody.Method));
            TestHarness.CheckTrue("块 Origin 回指",
                ReferenceEquals(loweredBody.Body.Origin, boundBody.Body));

            var boundDecl = (BoundLocalDeclarationStatement)boundBody.Body.Statements[0];
            var loweredDecl = (LoweredLocalDeclarationStatement)loweredBody.Body.Statements[0];
            TestHarness.CheckTrue("声明语句 Origin 回指",
                ReferenceEquals(loweredDecl.Origin, boundDecl));
            TestHarness.CheckTrue("LocalSymbol 同一实例",
                ReferenceEquals(loweredDecl.Local, boundDecl.Local));

            var boundBinary = (BoundBinaryExpression)boundDecl.Initializer!;
            var loweredBinary = (LoweredBinaryExpression)loweredDecl.Initializer!;
            TestHarness.CheckTrue("二元运算 Origin 回指",
                ReferenceEquals(loweredBinary.Origin, boundBinary));
            TestHarness.CheckTrue("左操作数 Origin 回指",
                ReferenceEquals(loweredBinary.Left.Origin, boundBinary.Left));
            TestHarness.CheckTrue("运算键透传", loweredBinary.Op == boundBinary.Op);
        }

        // ===== 负例：未覆盖节点 → P4 Error + 跳过该函数体 =====
        // （S7a 后 P3 能产出的节点已全部覆盖，源码无法触达 default 分支——
        // 以测试私有 Bound 子类模拟「未来新增但 Lowerer 尚未覆盖」的节点）
        private sealed class FutureBoundStatement : BoundStatement
        {
            public FutureBoundStatement(ASTNode syntax) : base(syntax)
            {
            }
        }

        private static void TestUnsupportedNode()
        {
            var root = TestHarness.ParseRoot("func f() { }\n");
            var unit = new CompilationUnit(root);
            var method = new MethodSymbol("future", MethodKind.Regular);
            var body = new BoundFunctionBody(method, new List<LocalSymbol>(),
                new BoundBlock(root, new List<BoundStatement> { new FutureBoundStatement(root) }));
            var lowered = Lowerer.Lower(unit, new[] { body });
            TestHarness.CheckSemanticError("未覆盖节点报 P4 Error", unit.Diagnostics,
                "not supported by minimal lowering");
            TestHarness.CheckTrue("跳过未覆盖函数体（无 LoweredFunctionBody 产出）",
                lowered.Count == 0);
        }

        // lambda 对象模型（SYNTAX §5.2）：宿主 + init + $$call 三 body；
        // 表达式降为 New(隐藏类)；捕获局部 init 为 cell 构造；读取 getValue。
        private static void TestLambdaLowering()
        {
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func f() { var fn = func{(): i32 -> 42 } }\n");
            TestHarness.CheckTrue("无捕获 lambda P4 降级无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var noCapBodies = LambdaRelatedBodies(lowered, "f");
            TestHarness.CheckTrue("无捕获 lambda 产 3 个 Lowered body（宿主+init+$$call）",
                noCapBodies.Count == 3);
            TestHarness.Check("无捕获宿主 Lowered 形态",
                NormLambda(LoweredDescribe.Body(BodyOf(noCapBodies, "f"))),
                "Body(f, [fn: ..lambda..UUID], [Decl(fn, ..lambda..UUID, = " +
                "New(..lambda..UUID, init, []))])");
            TestHarness.Check("无捕获 $$call Lowered 形态",
                NormLambda(LoweredDescribe.Body(LambdaCallBody(noCapBodies))),
                "Body(call, [], [Return(Int(42,i32))])");
            TestHarness.Check("无捕获 init Lowered 形态",
                NormLambda(LoweredDescribe.Body(LambdaInitBody(noCapBodies))),
                "Body(init, [], [])");

            var captured = LowerUnitWithStdlib(
                "func f(p: i32) { var x = 1\n" +
                "    var fn = func{(): i32 -> (p + x) }\n" +
                "    x = 2 }\n");
            TestHarness.CheckTrue("捕获 lambda P4 降级无诊断", !captured.Unit.Diagnostics.HasErrors,
                string.Join("; ", captured.Unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var capBodies = LambdaRelatedBodies(captured.Lowered, "f");
            TestHarness.CheckTrue("捕获 lambda 产 3 个 Lowered body（宿主+init+$$call）",
                capBodies.Count == 3);
            TestHarness.Check("捕获宿主 Lowered 形态（cell 构造 + CellRef 实参 + setValue）",
                NormLambda(LoweredDescribe.Body(BodyOf(capBodies, "f"))),
                "Body(f, [x: i32, fn: ..lambda..UUID, .c.p: Cell<i32>], [" +
                "Decl(.c.p, Cell<i32>, = New(Cell<i32>, init, [Param(p,i32)])); " +
                "Decl(x, i32, = New(Cell<i32>, init, [Int(1,i32)])); " +
                "Decl(fn, ..lambda..UUID, = New(..lambda..UUID, init, " +
                "[CellRef(.c.p,Cell<i32>), CellRef(x,Cell<i32>)])); " +
                "InstCallStmt(setValue, CellRef(x,Cell<i32>), [Int(2,i32)])])");
            TestHarness.Check("捕获 $$call 读取 getValue",
                NormLambda(LoweredDescribe.Body(LambdaCallBody(capBodies))),
                "Body(call, [], [Return(Binary(Add, " +
                "InstCall(getValue, InstField(.capture.p, This(i32), Cell<i32>), [], i32), " +
                "InstCall(getValue, InstField(.capture.x, This(i32), Cell<i32>), [], i32), " +
                "i32))])");
            TestHarness.Check("捕获 init 写 .capture 字段",
                NormLambda(LoweredDescribe.Body(LambdaInitBody(capBodies))),
                "Body(init, [], [" +
                "Assign(InstField(.capture.p, This(..lambda..UUID), Cell<i32>), Param(c0,Cell<i32>)); " +
                "Assign(InstField(.capture.x, This(..lambda..UUID), Cell<i32>), Param(c1,Cell<i32>))])");
        }

        private static string NormLambda(string text) =>
            BilTestHarness.NormalizeLambdaUuids(text);

        private static List<LoweredFunctionBody> LambdaRelatedBodies(
            IReadOnlyList<LoweredFunctionBody> lowered, string hostName)
        {
            return lowered.Where(b =>
                b.Method.Name == hostName
                || (b.Method.Owner != null
                    && b.Method.Owner.Name.StartsWith("..lambda..",
                        System.StringComparison.Ordinal))).ToList();
        }

        private static LoweredFunctionBody LambdaCallBody(
            IReadOnlyList<LoweredFunctionBody> bodies) =>
            bodies.Single(b => b.Method.Name == "call"
                && b.Method.Owner != null
                && b.Method.Owner.Name.StartsWith("..lambda..",
                    System.StringComparison.Ordinal));

        private static LoweredFunctionBody LambdaInitBody(
            IReadOnlyList<LoweredFunctionBody> bodies) =>
            bodies.Single(b => b.Method.Name == "init"
                && b.Method.Owner != null
                && b.Method.Owner.Name.StartsWith("..lambda..",
                    System.StringComparison.Ordinal));

    }
}
