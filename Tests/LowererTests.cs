using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S7a P4a 测试：Lowerer 对 P3（S5）全部 Bound 节点的恒等重写覆盖。
    /// 每类节点一个 Lowered 形态断言（LoweredDescribe 快照）+ 结构性断言
    /// （Origin 回指引用相等、字段符号引用相等）+ 未覆盖节点负例
    /// （测试私有 Bound 子类注入 → P4 Error + 跳过该函数体）。
    /// S7b 新增脱糖断言：bool 短路 and/or（BIL §11.3 if + 合成局部展开）、
    /// if 表达式（结果局部 + 值块降级）、复合赋值（前置赋值脱糖）、
    /// 值块 if 转换（后续语句移入 else / 双终止丢弃）。
    /// 驱动仿 BinderTests.BindUnit：全管线 P1–P3 后直接进 Lowerer（不带 stdlib）。
    /// </summary>
    public static class LowererTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("Lowerer");

            TestLocalDeclarations();
            TestAssignment();
            TestExpressionAndCallStatements();
            TestUnaryAndFieldReference();
            TestNew();
            TestOriginChain();
            TestShortCircuit();
            TestIfExpressionLowering();
            TestCompoundAssignmentLowering();
            TestValueBlockIfTransform();
            TestUnsupportedNode();

            return TestHarness.Summary("Lowerer");
        }

        // 多源文件经全管线（Parser → P1 → P2 → P3 → P4a）后取编译单元、
        // bound 函数体（Origin 对照用）与 lowered 函数体列表
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bound,
            IReadOnlyList<LoweredFunctionBody> Lowered) LowerUnit(params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            var bound = Binder.Bind(unit, decls);
            return (unit, bound, Lowerer.Lower(unit, bound));
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
        }

        private static LoweredFunctionBody BodyOf(IReadOnlyList<LoweredFunctionBody> bodies,
            string name)
        {
            return bodies.Single(b => b.Method.Name == name);
        }

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

        // ===== S7b：bool 短路 and/or（BIL §11.3：if + 合成局部展开）=====
        private static void TestShortCircuit()
        {
            var (unit, bound, lowered) = LowerUnit(
                "func f(a: bool, b: bool): bool { return (a and b) }\n" +
                "func o(a: bool, b: bool): bool { return (a or b) }\n");
            CheckNoErrors("无诊断（短路展开）", unit);
            TestHarness.Check("a and b ⇒ if a { s = b } else { s = false }",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: bool], " +
                "[If(Param(a,bool), [Assign(Local(.s0,bool), Param(b,bool))], " +
                "[Assign(Local(.s0,bool), Const(False,bool))]); Return(Local(.s0,bool))])");
            TestHarness.Check("a or b ⇒ if a { s = true } else { s = b }",
                LoweredDescribe.Body(BodyOf(lowered, "o")),
                "Body(o, [.s0: bool], " +
                "[If(Param(a,bool), [Assign(Local(.s0,bool), Const(True,bool))], " +
                "[Assign(Local(.s0,bool), Param(b,bool))]); Return(Local(.s0,bool))])");

            // 结构性事实：合成局部进 Locals（引用相等）；常量节点 Origin
            // 回指 and/or 表达式的 Bound 节点（最近的语法来源约定）
            var loweredBody = BodyOf(lowered, "f");
            var ifStmt = (LoweredIfStatement)loweredBody.Body.Statements[0];
            var constantAssign = (LoweredAssignmentStatement)ifStmt.FalseBlock!.Statements[0];
            var constant = (LoweredConstantExpression)constantAssign.Value;
            var boundAnd = (BoundBinaryExpression)((BoundReturnStatement)
                bound.Single(b => b.Method.Name == "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("合成局部 .s0 引用相等（写入与读取同一符号）",
                ReferenceEquals(((LoweredValueReferenceExpression)constantAssign.Target).Symbol,
                    loweredBody.Locals[0])
                && ReferenceEquals(((LoweredValueReferenceExpression)
                    ((LoweredReturnStatement)loweredBody.Body.Statements[1]).Value!).Symbol,
                    loweredBody.Locals[0]));
            TestHarness.CheckTrue("常量节点 Origin 回指 and/or Bound 节点",
                ReferenceEquals(constant.Origin, boundAnd));
            TestHarness.CheckTrue("常量类型为 bool", constant.Type.Name == "bool"
                && (bool)constant.Value == false);
        }

        // ===== S7b：if 表达式 → 合成结果局部 + 前置 LoweredIfStatement =====
        private static void TestIfExpressionLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if ((x > 0)) { return@_ 1 } else { return@_ 2 }\n" +
                "    return r\n" +
                "}\n" +
                "func g(x: i32): i32 {\n" +
                "    return if (x > 0) { x } else { -x }\n" +
                "}\n");
            CheckNoErrors("无诊断（if 表达式降级）", unit);
            TestHarness.Check("显式 return@ 值块写结果局部",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [r: i32, .s0: i32], " +
                "[If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(.s0,i32), Int(1,i32))], [Assign(Local(.s0,i32), Int(2,i32))]); " +
                "Decl(r, i32, = Local(.s0,i32)); Return(Local(r,i32))])");
            TestHarness.Check("隐式取值值块写结果局部",
                LoweredDescribe.Body(BodyOf(lowered, "g")),
                "Body(g, [.s0: i32], " +
                "[If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(.s0,i32), Param(x,i32))], " +
                "[Assign(Local(.s0,i32), Unary(Opposite, Param(x,i32), i32))]); " +
                "Return(Local(.s0,i32))])");
        }

        // ===== S7b：复合赋值脱糖（前置赋值 + 表达式位 Target 引用）=====
        private static void TestCompoundAssignmentLowering()
        {
            var (unit, bound, lowered) = LowerUnit(
                "func f(a: i32): i32 {\n" +
                "    var x = a\n" +
                "    var y = (x += 1)\n" +
                "    return y\n" +
                "}\n");
            CheckNoErrors("无诊断（复合赋值脱糖）", unit);
            TestHarness.Check("x += 1 ⇒ 前置 x = x + 1，表达式位 x",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [x: i32, y: i32], [Decl(x, i32, = Param(a,i32)); " +
                "Assign(Local(x,i32), Binary(Add, Local(x,i32), Int(1,i32), i32)); " +
                "Decl(y, i32, = Local(x,i32)); Return(Local(y,i32))])");

            // 结构性事实：脱糖赋值/运算的 Origin 回指复合赋值 Bound 节点；
            // 表达式位 Target 引用的 Origin 回指 Bound 侧的 Target 引用节点
            var loweredBody = BodyOf(lowered, "f");
            var assign = (LoweredAssignmentStatement)loweredBody.Body.Statements[1];
            var boundCompound = (BoundCompoundAssignmentExpression)
                ((BoundLocalDeclarationStatement)bound.Single(b => b.Method.Name == "f")
                    .Body.Statements[1]).Initializer!;
            TestHarness.CheckTrue("脱糖赋值与运算 Origin 回指复合赋值 Bound 节点",
                ReferenceEquals(assign.Origin, boundCompound)
                && ReferenceEquals(assign.Value.Origin, boundCompound));
            TestHarness.CheckTrue("表达式位引用 Origin 回指 Bound 侧 Target 引用",
                ReferenceEquals(
                    ((LoweredLocalDeclarationStatement)loweredBody.Body.Statements[2])
                        .Initializer!.Origin, boundCompound.Target));
        }

        // ===== S7b：值块 if 转换（后续语句移入 else / 双终止丢弃）=====
        private static void TestValueBlockIfTransform()
        {
            // 中间 return@（if 单分支终止）→ 后续语句移入新建 else 块
            var (unit, _, lowered) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) {\n" +
                "        if (x > 1) { return@_ 2 }\n" +
                "        return@_ 1\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（值块 if 转换）", unit);
            TestHarness.Check("后续语句移入新建 else 块",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [r: i32, .s0: i32], " +
                "[If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[If(Binary(CmpGt, Param(x,i32), Int(1,i32), bool), " +
                "[Assign(Local(.s0,i32), Int(2,i32))], [Assign(Local(.s0,i32), Int(1,i32))])], " +
                "[Assign(Local(.s0,i32), Int(0,i32))]); " +
                "Decl(r, i32, = Local(.s0,i32)); Return(Local(r,i32))])");

            // 双分支都终止 → if 之后语句全丢弃（不可达死代码）
            var (unit2, _, lowered2) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) {\n" +
                "        if (x > 1) { return@_ 1 } else { return@_ 2 }\n" +
                "        var y = 3\n" +
                "        return@_ y\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（双终止丢弃）", unit2);
            // 丢弃的 Decl(y) 语句不产出指令，但 LocalSymbol 仍在 Locals 全量平铺
            TestHarness.Check("双分支终止后语句全丢弃",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [y: i32, r: i32, .s0: i32], " +
                "[If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[If(Binary(CmpGt, Param(x,i32), Int(1,i32), bool), " +
                "[Assign(Local(.s0,i32), Int(1,i32))], [Assign(Local(.s0,i32), Int(2,i32))])], " +
                "[Assign(Local(.s0,i32), Int(0,i32))]); " +
                "Decl(r, i32, = Local(.s0,i32)); Return(Local(r,i32))])");
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
    }
}
