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
    /// S7c-1 新增循环降级断言：Judge 块（条件求值移入、条件内短路展开
    /// 随块走）、合成 bool 条件局部 .sN 与 .breakid 局部 .bN（Type null）、
    /// Enumerator 恒 null、do-while rev、BoundLoop → BreakId 映射命中
    /// （嵌套标签/值块穿透）。
    /// S7e 新增：cast 恒等降级（as/as? + Origin 回指）、try 降级（ExceptionSlot
    /// 复用 finally 变量或合成 .sN、有名 catch 体头合成 cast）、seq 双形态
    /// （语句恒等 / 表达式脱糖结果局部）、值块编织扩展（seq 透明、try 无
    /// finally 同 if 规则、finally 终止覆盖、try-finally 部分终止拦截）。
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
            TestLoopLowering();
            TestInstanceLowering();
            TestForLoopLowering();
            TestSwitchLowering();
            TestThrowLowering();
            TestElseIfChainTransform();
            TestCastLowering();
            TestTryLowering();
            TestSeqLowering();
            TestTryWeaving();
            TestInterpolationLowering();
            TestSafeAccessLowering();
            TestNullFallbackLowering();
            TestDestructuringLowering();
            TestTypeCheckLowering();
            TestTypeOfLowering();
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

        // 带 stdlib 的全管线驱动（S7c-2：for 脱糖用例需要 core.collections
        // 协议与 .bootstrap 的 EnumerateInRange 注册）
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bound,
            IReadOnlyList<LoweredFunctionBody> Lowered) LowerUnitWithStdlib(
            params string[] sources)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.AddRange(sources.Select(TestHarness.ParseRoot));
            var unit = new CompilationUnit(roots.ToArray());
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

        // ===== S7c-1：循环降级（Judge 块/条件局部/Enumerator null/BreakId 映射）=====
        private static void TestLoopLowering()
        {
            // while：条件求值移入 Judge 块（末尾写合成条件局部 .s0）
            var (unit, _, lowered) = LowerUnit(
                "func f(a: i32) {\n" +
                "    var x = 0\n" +
                "    while (x < a) {\n" +
                "        x = x + 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（while 降级）", unit);
            TestHarness.Check("while Lowered 形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [x: i32, .s0: bool, .b0: .breakid], " +
                "[Decl(x, i32, = Int(0,i32)); " +
                "Loop([Assign(Local(.s0,bool), " +
                "Binary(CmpLt, Local(x,i32), Param(a,i32), bool))], .s0, " +
                "[Assign(Local(x,i32), Binary(Add, Local(x,i32), Int(1,i32), i32))], .b0)])");

            // 结构性事实：Condition/BreakId 是合成局部并进 Locals（引用相等）、
            // BreakId.Type 为 null（.breakid 特例）、Enumerator 恒 null
            var whileLoop = (LoweredLoop)BodyOf(lowered, "f").Body.Statements[1];
            var fLocals = BodyOf(lowered, "f").Locals;
            TestHarness.CheckTrue("条件局部 .s0（bool，引用相等）",
                whileLoop.Condition.Name == ".s0" && whileLoop.Condition.Type?.Name == "bool"
                && ReferenceEquals(whileLoop.Condition, fLocals[1]));
            TestHarness.CheckTrue("BreakId 局部 .b0（Type null，引用相等）",
                whileLoop.BreakId.Name == ".b0" && whileLoop.BreakId.Type == null
                && ReferenceEquals(whileLoop.BreakId, fLocals[2]));
            TestHarness.CheckTrue("Enumerator 恒 null（S7c-2 前）",
                whileLoop.Enumerator == null && !whileLoop.IsRev);

            // do-while → loop.rev（IsRev）
            var (unit2, _, lowered2) = LowerUnit(
                "func f(a: i32) {\n" +
                "    do {\n" +
                "        a = a - 1\n" +
                "    } while (a > 0)\n" +
                "}\n");
            CheckNoErrors("无诊断（do-while 降级）", unit2);
            TestHarness.Check("do-while Lowered 形态（rev）",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [.s0: bool, .b0: .breakid], " +
                "[Loop(rev, [Assign(Local(.s0,bool), " +
                "Binary(CmpGt, Param(a,i32), Int(0,i32), bool))], .s0, " +
                "[Assign(Param(a,i32), Binary(Sub, Param(a,i32), Int(1,i32), i32))], .b0)])");

            // 嵌套标签循环：break@outer 映射命中外层 .b0、continue 命中内层 .b1
            var (unit3, _, lowered3) = LowerUnit(
                "func f(a: i32) {\n" +
                "    while (a > 0) named outer {\n" +
                "        while (a > 1) {\n" +
                "            break@outer\n" +
                "            continue\n" +
                "        }\n" +
                "        a = a - 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（嵌套标签循环降级）", unit3);
            TestHarness.Check("嵌套循环 Lowered 形态", LoweredDescribe.Body(BodyOf(lowered3, "f")),
                "Body(f, [.s0: bool, .b0: .breakid, .s1: bool, .b1: .breakid], " +
                "[Loop([Assign(Local(.s0,bool), Binary(CmpGt, Param(a,i32), Int(0,i32), bool))], .s0, " +
                "[Loop([Assign(Local(.s1,bool), Binary(CmpGt, Param(a,i32), Int(1,i32), bool))], .s1, " +
                "[Break(.b0); Continue(.b1)], .b1); " +
                "Assign(Param(a,i32), Binary(Sub, Param(a,i32), Int(1,i32), i32))], .b0)])");
            // 结构性事实：映射引用相等——Break 携外层 BreakId、Continue 携内层
            var outer = (LoweredLoop)BodyOf(lowered3, "f").Body.Statements[0];
            var inner = (LoweredLoop)outer.Body.Statements[0];
            TestHarness.CheckTrue("break@outer 携外层 BreakId（引用相等）",
                ReferenceEquals(((LoweredLoopControl)inner.Body.Statements[0]).BreakId,
                    outer.BreakId));
            TestHarness.CheckTrue("continue 携内层 BreakId（引用相等）",
                ReferenceEquals(((LoweredLoopControl)inner.Body.Statements[1]).BreakId,
                    inner.BreakId));

            // 条件内短路展开：前置语句落在 Judge 块（不污染循环前块）
            var (unit4, _, lowered4) = LowerUnit(
                "func f(a: bool, b: bool) {\n" +
                "    while ((a and b)) {\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（条件内短路）", unit4);
            TestHarness.Check("短路展开进 Judge 块", LoweredDescribe.Body(BodyOf(lowered4, "f")),
                "Body(f, [.s0: bool, .b0: .breakid, .s1: bool], " +
                "[Loop([If(Param(a,bool), [Assign(Local(.s1,bool), Param(b,bool))], " +
                "[Assign(Local(.s1,bool), Const(False,bool))]); " +
                "Assign(Local(.s0,bool), Local(.s1,bool))], .s0, [], .b0)])");

            // 值块内 break 穿透：直接发 BIL 跳转（无展开、无 if 转换介入）
            var (unit5, _, lowered5) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = 0\n" +
                "    while (x > 0) {\n" +
                "        r = if ((x == 5)) { break } else { return@_ 1 }\n" +
                "        x = x - 1\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（值块内 break 穿透降级）", unit5);
            TestHarness.Check("值块内 break 穿透 Lowered 形态",
                LoweredDescribe.Body(BodyOf(lowered5, "f")),
                "Body(f, [r: i32, .s0: bool, .b0: .breakid, .s1: i32], " +
                "[Decl(r, i32, = Int(0,i32)); " +
                "Loop([Assign(Local(.s0,bool), Binary(CmpGt, Param(x,i32), Int(0,i32), bool))], .s0, " +
                "[If(Binary(CmpEq, Param(x,i32), Int(5,i32), bool), [Break(.b0)], " +
                "[Assign(Local(.s1,i32), Int(1,i32))]); " +
                "Assign(Local(r,i32), Local(.s1,i32)); " +
                "Assign(Param(x,i32), Binary(Sub, Param(x,i32), Int(1,i32), i32))], .b0); " +
                "Return(Local(r,i32))])");
        }

        // ===== S7c-2：实例成员恒等降级（this/实例调用/实例字段）=====
        private static void TestInstanceLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub func add(n: i32): i32 { return value + n }\n" +
                "}\n" +
                "func call(c: Counter): i32 { return c.add(2) }\n" +
                "func read(c: Counter): i32 { return c.value }\n");
            CheckNoErrors("无诊断（实例成员降级）", unit);
            TestHarness.Check("裸名实例字段降级（this 隐式）",
                LoweredDescribe.Body(BodyOf(lowered, "add")),
                "Body(add, [], [Return(Binary(Add, " +
                "InstField(value, This(Counter), i32), Param(n,i32), i32))])");
            TestHarness.Check("实例调用降级",
                LoweredDescribe.Body(BodyOf(lowered, "call")),
                "Body(call, [], [Return(InstCall(add, Param(c,Counter), [Int(2,i32)], i32))])");
            TestHarness.Check("实例字段访问降级",
                LoweredDescribe.Body(BodyOf(lowered, "read")),
                "Body(read, [], [Return(InstField(value, Param(c,Counter), i32))])");

            // 结构性事实：InstCall 的 Method 符号引用相等、Type 自带（i32）
            var counterType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Counter");
            var callReturn = (LoweredReturnStatement)BodyOf(lowered, "call").Body.Statements[0];
            var instCall = (LoweredInstanceCallExpression)callReturn.Value!;
            TestHarness.CheckTrue("实例调用方法符号引用相等 + Type 自带",
                ReferenceEquals(instCall.Method,
                    counterType.Methods.Single(m => m.Name == "add"))
                && ReferenceEquals(instCall.Type, unit.Symbols.Bootstrap.Int32));
        }

        // ===== S7c-2：for 脱糖（前置 iterate + LoweredLoop 三件套）=====
        private static void TestForLoopLowering()
        {
            var (unit, bound, lowered) = LowerUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    for (i in 0 to 3) {\n" +
                "        sum = sum + i\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            CheckNoErrors("无诊断（for 脱糖）", unit);
            TestHarness.Check("for 脱糖 Lowered 形态",
                LoweredDescribe.Body(BodyOf(lowered, "main")),
                "Body(main, [sum: i32, i: i32, .s0: IEnumerator<i32>, .s1: bool, .b0: .breakid], " +
                "[Decl(sum, i32, = Int(0,i32)); " +
                "Assign(Local(.s0,IEnumerator<i32>), " +
                "InstCall(iterate, " +
                "InstCall(EnumerateInRange, Int(0,i32), [Int(3,i32)], IEnumerable<i32>), " +
                "[], IEnumerator<i32>)); " +
                "Loop([Assign(Local(.s1,bool), " +
                "InstCall(moveNext, Local(.s0,IEnumerator<i32>), [], bool))], .s1, " +
                "[Assign(Local(i,i32), " +
                "InstCall(current, Local(.s0,IEnumerator<i32>), [], i32)); " +
                "Assign(Local(sum,i32), Binary(Add, Local(sum,i32), Local(i,i32), i32))], .b0); " +
                "Return(Local(sum,i32))])");

            // 结构性事实（三件套引用相等）：前置 iterate 语句在 Loop 之前；
            // Judge/Body 头的协议调用方法符号 = P3 挂在 BoundLoop 上的产物；
            // 条件局部/枚举器局部/breakid 与 Locals 同一实例
            var boundLoop = (BoundLoop)bound.Single(b => b.Method.Name == "main")
                .Body.Statements[1];
            var mainBody = BodyOf(lowered, "main");
            var iterateAssign = (LoweredAssignmentStatement)mainBody.Body.Statements[1];
            var forLoop = (LoweredLoop)mainBody.Body.Statements[2];
            var judgeCall = (LoweredInstanceCallExpression)
                ((LoweredAssignmentStatement)forLoop.Judge.Statements[0]).Value;
            var currentCall = (LoweredInstanceCallExpression)
                ((LoweredAssignmentStatement)forLoop.Body.Statements[0]).Value;
            TestHarness.CheckTrue("前置 iterate 方法符号引用相等",
                ReferenceEquals(((LoweredInstanceCallExpression)iterateAssign.Value).Method,
                    boundLoop.IterateMethod));
            TestHarness.CheckTrue("Judge moveNext / Body 头 current 方法符号引用相等",
                ReferenceEquals(judgeCall.Method, boundLoop.MoveNextMethod)
                && ReferenceEquals(currentCall.Method, boundLoop.CurrentMethod));
            TestHarness.CheckTrue("条件局部/枚举器局部/breakid 引用相等",
                ReferenceEquals(forLoop.Condition, mainBody.Locals[3])
                && ReferenceEquals(
                    ((LoweredValueReferenceExpression)iterateAssign.Target).Symbol,
                    mainBody.Locals[2])
                && ReferenceEquals(forLoop.BreakId, mainBody.Locals[4]));
            var enumeratorLocalType = mainBody.Locals[2].Type!;
            TestHarness.CheckTrue("枚举器局部类型 = IEnumerator<i32> 构造",
                enumeratorLocalType.ConstructedFrom != null
                && enumeratorLocalType.ConstructedFrom.Name == "IEnumerator"
                && ReferenceEquals(enumeratorLocalType.TypeArguments![0],
                    unit.Symbols.Bootstrap.Int32));
        }

        // ===== switch 降级（S7d：全值匹配 → LoweredSwitch；含 pattern → if 链）=====
        private static void TestSwitchLowering()
        {
            // 全值匹配语句形态：LoweredSwitch + 合成 .breakid（无人引用，仅满足形态）
            var (unit, bound, lowered) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (2) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（常量 switch）", unit);
            TestHarness.Check("常量 switch Lowered 形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.b0: .breakid], [Switch(Param(x,i32), " +
                "[Case(Int(1,i32), [Return(Int(1,i32))]); Case(Int(2,i32), [Return(Int(2,i32))])], " +
                "[Return(Int(0,i32))], .b0)])");
            var fBody = BodyOf(lowered, "f");
            var switchStmt = (LoweredSwitch)fBody.Body.Statements[0];
            TestHarness.CheckTrue("Origin 回指引用相等 + BreakId 进 Locals",
                ReferenceEquals(switchStmt.Origin,
                    ((BoundBlock)bound.Single(b => b.Method.Name == "f").Body).Statements[0])
                && ReferenceEquals(switchStmt.BreakId, fBody.Locals[0]));

            // 混合形态：任一 pattern → selector 物化 .sN + 嵌套 if 链（保序）
            var (unit2, _, lowered2) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (_ > 10) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（pattern switch）", unit2);
            var described2 = LoweredDescribe.Body(BodyOf(lowered2, "f"));
            TestHarness.Check("pattern switch if 链降级", described2,
                "Body(f, [.s0: i32], [Assign(Local(.s0,i32), Param(x,i32)); " +
                "[If(Binary(CmpEq, Local(.s0,i32), Int(1,i32), bool), [Return(Int(1,i32))], " +
                "[If(Binary(CmpGt, Local(.s0,i32), Int(10,i32), bool), [Return(Int(2,i32))], " +
                "[Return(Int(0,i32))])])]])");
            TestHarness.CheckTrue("selector 全 switch 只求值一次",
                described2.Split("Param(x").Length - 1 == 1);
            var chainAssign = (LoweredAssignmentStatement)BodyOf(lowered2, "f").Body.Statements[0];
            var selectorTemp = ((LoweredValueReferenceExpression)chainAssign.Target).Symbol;
            var outerIf = (LoweredIfStatement)
                ((LoweredBlock)BodyOf(lowered2, "f").Body.Statements[1]).Statements[0];
            TestHarness.CheckTrue("占位读取与 selector 物化局部引用相等",
                ReferenceEquals(((LoweredValueReferenceExpression)
                    ((LoweredBinaryExpression)outerIf.Condition).Left).Symbol, selectorTemp));

            // 表达式形态：结果局部 + 前置 switch（分支体写结果局部）
            var (unit3, _, lowered3) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 10 }\n" +
                "        default -> { 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（switch 表达式）", unit3);
            TestHarness.Check("switch 表达式结果局部", LoweredDescribe.Body(BodyOf(lowered3, "f")),
                "Body(f, [.s0: i32, .b0: .breakid], " +
                "[Switch(Param(x,i32), [Case(Int(1,i32), [Assign(Local(.s0,i32), Int(10,i32))])], " +
                "[Assign(Local(.s0,i32), Int(0,i32))], .b0); Return(Local(.s0,i32))])");
        }

        // ===== throw 降级（S7d：恒等 + 值块终止口径）=====
        private static void TestThrowLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func f(): i32 {\n" +
                "    throw new MyException()\n" +
                "}\n");
            CheckNoErrors("无诊断（throw）", unit);
            TestHarness.Check("throw 恒等降级", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [Throw(New(MyException, []))])");

            // 值块内 throw：路径终止（其后语句截断，if 转换不编织 continuation）
            var (unit2, _, lowered2) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) {\n" +
                "        throw new core.Exception()\n" +
                "    } else { 0 }\n" +
                "}\n");
            CheckNoErrors("无诊断（值块内 throw）", unit2);
            TestHarness.Check("值块内 throw 终止", LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [.s0: i32], [If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Throw(New(Exception, []))], [Assign(Local(.s0,i32), Int(0,i32))]); " +
                "Return(Local(.s0,i32))])");
        }

        // ===== M46 修复回归：else-if 链混合终止的 continuation 编织 =====
        private static void TestElseIfChainTransform()
        {
            // c2 命中路径产值必须留在结果局部（修复前被后续语句覆盖为 3）
            var (unit, _, lowered) = LowerUnit(
                "func f(c0: bool, c1: bool, c2: bool): i32 {\n" +
                "    var x = 0\n" +
                "    var r = if (c0) {\n" +
                "        if (c1) { return@_ 1 } else if (c2) { return@_ 2 } else { x = 2 }\n" +
                "        x = 3\n" +
                "        return@_ x\n" +
                "    } else { 0 }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（else-if 链编织）", unit);
            TestHarness.Check("else-if 链混合终止编织", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [x: i32, r: i32, .s0: i32], [Decl(x, i32, = Int(0,i32)); " +
                "If(Param(c0,bool), " +
                "[If(Param(c1,bool), [Assign(Local(.s0,i32), Int(1,i32))], " +
                "[If(Param(c2,bool), [Assign(Local(.s0,i32), Int(2,i32))], " +
                "[Assign(Local(x,i32), Int(2,i32)); Assign(Local(x,i32), Int(3,i32)); " +
                "Assign(Local(.s0,i32), Local(x,i32))])])], " +
                "[Assign(Local(.s0,i32), Int(0,i32))]); " +
                "Decl(r, i32, = Local(.s0,i32)); Return(Local(r,i32))])");
        }

        // ===== cast 恒等降级（S7e，BIL §12.1/§12.2）=====
        private static void TestCastLowering()
        {
            var (unit, bound, lowered) = LowerUnit(
                "func f(s: String): String {\n" +
                "    return s as String\n" +
                "}\n" +
                "func g(s: String): String? {\n" +
                "    return s as? String\n" +
                "}\n");
            CheckNoErrors("无诊断（cast）", unit);
            TestHarness.Check("as 降级形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [Return(Cast(Param(s,String), String, String))])");
            TestHarness.Check("as? 降级形态", LoweredDescribe.Body(BodyOf(lowered, "g")),
                "Body(g, [], [Return(SafeCast(Param(s,String), String, String?))])");
            // Origin 回指引用相等（恒等降级）
            var boundCast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)bound.Single(b => b.Method.Name == "f").Body).Statements[0]).Value!;
            var loweredCast = (LoweredCastExpression)((LoweredReturnStatement)
                BodyOf(lowered, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("cast Origin 回指 Bound 节点",
                ReferenceEquals(loweredCast.Origin, boundCast));
        }

        // ===== try 降级（S7e，BIL §16.7）=====
        private static void TestTryLowering()
        {
            // 有名 catch：体头合成「变量 = cast slot」，slot 为合成 .sN
            var (unit, bound, lowered) = LowerUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func handle(e: MyException) {\n" +
                "}\n" +
                "func f() {\n" +
                "    try {\n" +
                "        throw new MyException()\n" +
                "    } catch (e: MyException) {\n" +
                "        handle(e)\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（try-catch）", unit);
            TestHarness.Check("有名 catch 合成 cast", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [e: MyException, .s0: Exception?], " +
                "[Try([Throw(New(MyException, []))], " +
                "[Catch(e, MyException, [Assign(Local(e,MyException), " +
                "Cast(Local(.s0,Exception?), MyException, MyException)); " +
                "CallStmt(handle, [Local(e,MyException)])])], .s0)])");
            var tryStmt = (LoweredTryStatement)BodyOf(lowered, "f").Body.Statements[0];
            TestHarness.CheckTrue("slot 类型 Nullable<Exception>",
                tryStmt.ExceptionSlot.Type!.Name == "Nullable");
            // 合成 cast 赋值的 Origin 指 BoundCatchClause
            var boundTry = (BoundTryStatement)((BoundBlock)
                bound.Single(b => b.Method.Name == "f").Body).Statements[0];
            TestHarness.CheckTrue("合成 cast Origin 指 catch 子句",
                ReferenceEquals(tryStmt.Catches[0].Body.Statements[0].Origin,
                    boundTry.Catches[0]));

            // finally(e)：slot 即 finally 变量（不合成 .sN）
            var (unit2, _, lowered2) = LowerUnit(
                "func log() {\n" +
                "}\n" +
                "func h() {\n" +
                "    try {\n" +
                "        log()\n" +
                "    } finally(e) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（finally(e)）", unit2);
            TestHarness.Check("finally 变量即 slot", LoweredDescribe.Body(BodyOf(lowered2, "h")),
                "Body(h, [e: Exception?], [Try([CallStmt(log, [])], [], " +
                "Finally([CallStmt(log, [])]), e)])");

            // _: 无变量 catch——体头无合成 cast
            var (unit3, _, lowered3) = LowerUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func g() {\n" +
                "    try {\n" +
                "        log()\n" +
                "    } catch (_: MyException) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（_: catch）", unit3);
            TestHarness.Check("_: catch 无合成 cast", LoweredDescribe.Body(BodyOf(lowered3, "g")),
                "Body(g, [.s0: Exception?], [Try([CallStmt(log, [])], " +
                "[Catch(MyException, [CallStmt(log, [])])], .s0)])");
        }

        // ===== seq 降级（S7e，BIL §3.4 call 化）=====
        private static void TestSeqLowering()
        {
            // 语句形态恒等降级
            var (unit, _, lowered) = LowerUnit(
                "func s() {\n" +
                "    seq {\n" +
                "        var x = 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（seq 语句）", unit);
            TestHarness.Check("seq 语句降级", LoweredDescribe.Body(BodyOf(lowered, "s")),
                "Body(s, [x: i32], [Seq([Decl(x, i32, = Int(1,i32))])])");

            // volatile 语句形态
            var (unit2, _, lowered2) = LowerUnit(
                "func work() {\n" +
                "}\n" +
                "func s2() {\n" +
                "    volatile seq {\n" +
                "        work()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（volatile seq）", unit2);
            TestHarness.Check("volatile seq 降级", LoweredDescribe.Body(BodyOf(lowered2, "s2")),
                "Body(s2, [], [SeqVolatile([CallStmt(work, [])])])");

            // 表达式形态脱糖：合成结果局部 + 前置 seq 块（值块降级写结果局部）
            var (unit3, _, lowered3) = LowerUnit(
                "func se(): i32 {\n" +
                "    return seq { return@_ 42 }\n" +
                "}\n");
            CheckNoErrors("无诊断（seq 表达式）", unit3);
            TestHarness.Check("seq 表达式脱糖", LoweredDescribe.Body(BodyOf(lowered3, "se")),
                "Body(se, [.s0: i32], [Seq([Assign(Local(.s0,i32), Int(42,i32))]); " +
                "Return(Local(.s0,i32))])");

            // volatile 表达式形态
            var (unit4, _, lowered4) = LowerUnit(
                "func sv(): i32 {\n" +
                "    return volatile seq { 1 }\n" +
                "}\n");
            CheckNoErrors("无诊断（volatile seq 表达式）", unit4);
            TestHarness.Check("volatile seq 表达式脱糖",
                LoweredDescribe.Body(BodyOf(lowered4, "sv")),
                "Body(sv, [.s0: i32], [SeqVolatile([Assign(Local(.s0,i32), Int(1,i32))]); " +
                "Return(Local(.s0,i32))])");
        }

        // ===== 值块编织扩展（S7e：seq 透明 / try 规则 / finally 拦截）=====
        private static void TestTryWeaving()
        {
            // seq 与 LoweredBlock 同构透明：continuation 织入体内 if 的 else
            var (unit, _, lowered) = LowerUnit(
                "func f(c: bool): i32 {\n" +
                "    return seq {\n" +
                "        seq {\n" +
                "            if (c) { return@_ 1 }\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（seq 编织）", unit);
            TestHarness.Check("seq 透明编织", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: i32], [Seq([Seq([If(Param(c,bool), " +
                "[Assign(Local(.s0,i32), Int(1,i32))], " +
                "[Assign(Local(.s0,i32), Int(2,i32))])])]); Return(Local(.s0,i32))])");

            // try 无 finally：同 if 规则编织（continuation 织入不终止分支末端）
            var (unit2, _, lowered2) = LowerUnit(
                "func g(c: bool): i32 {\n" +
                "    return seq {\n" +
                "        try {\n" +
                "            if (c) { return@_ 1 }\n" +
                "        } catch (_: core.Exception) {\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（try 编织）", unit2);
            TestHarness.Check("try 无 finally 编织", LoweredDescribe.Body(BodyOf(lowered2, "g")),
                "Body(g, [.s0: i32, .s1: Exception?], [Seq([Try(" +
                "[If(Param(c,bool), [Assign(Local(.s0,i32), Int(1,i32))], " +
                "[Assign(Local(.s0,i32), Int(2,i32))])], " +
                "[Catch(Exception, [Assign(Local(.s0,i32), Int(2,i32))])], .s1)]); " +
                "Return(Local(.s0,i32))])");

            // finally 自身终止（值块写入）：continuation 全丢弃
            var (unit3, _, lowered3) = LowerUnit(
                "func h(): i32 {\n" +
                "    return seq {\n" +
                "        var dummy = 0\n" +
                "        try {\n" +
                "            dummy = 1\n" +
                "        } finally(f) {\n" +
                "            return@_ 3\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（finally 终止覆盖）", unit3);
            TestHarness.Check("finally 终止丢弃 continuation",
                LoweredDescribe.Body(BodyOf(lowered3, "h")),
                "Body(h, [dummy: i32, f: Exception?, .s0: i32], [Seq(" +
                "[Decl(dummy, i32, = Int(0,i32)); " +
                "Try([Assign(Local(dummy,i32), Int(1,i32))], [], " +
                "Finally([Assign(Local(.s0,i32), Int(3,i32))]), f)]); " +
                "Return(Local(.s0,i32))])");

            // 拦截：有 finally + 部分分支终止 + continuation 非空 → P4 Error
            var (unit4, _, lowered4) = LowerUnit(
                "func bad(c: bool): i32 {\n" +
                "    return seq {\n" +
                "        try {\n" +
                "            if (c) { return@_ 1 }\n" +
                "        } finally(f) {\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("try-finally 部分终止编织拦截", unit4.Diagnostics,
                "P4: value block weaving across try-finally with partial termination " +
                "is not supported yet (S7e)");
            TestHarness.CheckTrue("拦截后跳过该函数体",
                !lowered4.Any(b => b.Method.Name == "bad"));
        }

        // ===== 字符串插值与子类型 cast 物化（S7f，BIL §6.5）=====
        // 插值经 P3 绑定为 toString/+ 链后恒等降级；值类型 receiver 调
        // Any 承诺的 toString 时物化装箱 cast（§12.1 内建引用视图转换）
        private static void TestInterpolationLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "func f() {\n" +
                "    var n = 42\n" +
                "    var s = \"n=${n}\"\n" +
                "}\n");
            CheckNoErrors("无诊断（插值降级）", unit);
            TestHarness.Check("值类型段 toString 的 receiver 装箱 cast",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [n: i32, s: String], [Decl(n, i32, = Int(42,i32)); " +
                "Decl(s, String, = Binary(Add, Str(\"n=\",String), " +
                "InstCall(toString, Cast(Local(n,i32), Any, Any), [], String), String))])");

            // return 位置的子类型 cast 物化（实现/派生 → 声明类型视图）
            var (unit2, _, lowered2) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): Animal { return d }\n");
            CheckNoErrors("无诊断（return cast 物化）", unit2);
            TestHarness.Check("return 子类型 cast 物化",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [], [Return(Cast(Param(d,Dog), Animal, Animal))])");

            // 局部声明初始化位置的子类型 cast 物化（值类型 → 接口装箱）
            var (unit3, _, lowered3) = LowerUnit(
                "interface Greeter { func greet(): String }\n" +
                "class Bot implements Greeter { pub func greet(): String { return \"hi\" } }\n" +
                "func f() {\n" +
                "    var g: Greeter = new Bot()\n" +
                "}\n");
            CheckNoErrors("无诊断（初始化 cast 物化）", unit3);
            TestHarness.Check("局部初始化子类型 cast 物化",
                LoweredDescribe.Body(BodyOf(lowered3, "f")),
                "Body(f, [g: Greeter], [Decl(g, Greeter, = Cast(New(Bot, []), Greeter, Greeter))])");
        }

        // ===== `?.` 脱糖（S7f，SYNTAX §3.4；BIL §3.4）=====
        private static void TestSafeAccessLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): String? { return u?.name }\n");
            CheckNoErrors("无诊断（?. 降级）", unit);
            // 物化 receiver + null 检查 + 非空分支 unwrap/成员/wrap
            TestHarness.Check("?. 脱糖形态（字段）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: User?, .s1: String?], [" +
                "Assign(Local(.s0,User?), Param(u,User?)); " +
                "Assign(Local(.s1,String?), Const(null,String?)); " +
                "If(Binary(CmpNe, Local(.s0,User?), Const(null,User?), bool), " +
                "[Assign(Local(.s1,String?), " +
                "Cast(InstField(name, Cast(Local(.s0,User?), User, User), String), String?, String?))]); " +
                "Return(Local(.s1,String?))])");

            // 链式：a?.b?.c——内层安全访问的 receiver 是外层占位（unwrap cast 复用）
            var (unit2, _, lowered2) = LowerUnit(
                "class B { pub var c: String\n    pub init(_ -> c) { } }\n" +
                "class A { pub var b: B\n    pub init(_ -> b) { } }\n" +
                "func f(a: A?): String? { return a?.b?.c }\n");
            CheckNoErrors("无诊断（链式 ?. 降级）", unit2);
            TestHarness.Check("链式 ?. 脱糖形态",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [.s0: A?, .s1: B?, .s2: B?, .s3: String?], [" +
                "Assign(Local(.s0,A?), Param(a,A?)); " +
                "Assign(Local(.s1,B?), Const(null,B?)); " +
                "If(Binary(CmpNe, Local(.s0,A?), Const(null,A?), bool), " +
                "[Assign(Local(.s1,B?), " +
                "Cast(InstField(b, Cast(Local(.s0,A?), A, A), B), B?, B?))]); " +
                "Assign(Local(.s2,B?), Local(.s1,B?)); " +
                "Assign(Local(.s3,String?), Const(null,String?)); " +
                "If(Binary(CmpNe, Local(.s2,B?), Const(null,B?), bool), " +
                "[Assign(Local(.s3,String?), " +
                "Cast(InstField(c, Cast(Local(.s2,B?), B, B), String), String?, String?))]); " +
                "Return(Local(.s3,String?))])");
        }

        // ===== if? 脱糖（S7f，SYNTAX §3.4）=====
        private static void TestNullFallbackLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): User { return u if? new User(\"anon\") }\n");
            CheckNoErrors("无诊断（if? 降级）", unit);
            // 非空分支 unwrap，空分支求回退值（延迟求值由 if 结构保证）
            TestHarness.Check("if? 脱糖形态",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: User?, .s1: User], [" +
                "Assign(Local(.s0,User?), Param(u,User?)); " +
                "If(Binary(CmpNe, Local(.s0,User?), Const(null,User?), bool), " +
                "[Assign(Local(.s1,User), Cast(Local(.s0,User?), User, User))], " +
                "[Assign(Local(.s1,User), New(User, init, [Str(\"anon\",String)]))]); " +
                "Return(Local(.s1,User))])");
        }

        // ===== 解构脱糖（S7f，SYNTAX §18；BIL §3.4 精确字段读取）=====
        private static void TestDestructuringLowering()
        {
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "func f() {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "}\n");
            CheckNoErrors("无诊断（解构降级）", unit);
            // pair 物化一次 + 逐字段读取（分量类型 = 构造实参）
            TestHarness.Check("解构脱糖形态",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [k: String, v: i32, .s0: Entry], [" +
                "[Assign(Local(.s0,Entry), New(Entry, init, [Str(\"a\",String), Int(1,i32)])); " +
                "Decl(k, String, = InstField(key, Local(.s0,Entry), String)); " +
                "Decl(v, i32, = InstField(value, Local(.s0,Entry), i32))]])");
        }

        // ===== 类型谓词 is 降级（S8a，恒等降级无脱糖；BIL §12.3 直接对应）=====
        private static void TestTypeCheckLowering()
        {
            // is 静态：恒等降级 + Origin 回指 + 目标类型符号透传
            var (unit, bound, lowered) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): bool { return d is Animal }\n");
            CheckNoErrors("无诊断（is 静态降级）", unit);
            TestHarness.Check("is 静态降级形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [Return(Is(Param(d,Dog), Animal))])");
            var boundIs = (BoundTypeCheckExpression)((BoundReturnStatement)
                bound.Single(b => b.Method.Name == "f").Body.Statements[0]).Value!;
            var loweredIs = (LoweredTypeCheckExpression)((LoweredReturnStatement)
                BodyOf(lowered, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("is Origin 回指 Bound 节点",
                ReferenceEquals(loweredIs.Origin, boundIs));
            TestHarness.CheckTrue("is 目标类型符号透传（静态无 TargetValue）",
                ReferenceEquals(loweredIs.TargetType, boundIs.TargetType)
                && loweredIs.TargetValue == null);

            // is 动态：TargetValue 递归降级（Lowered 新节点，Origin 回指 Bound 值节点）
            var (unit2, bound2, lowered2) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func k(d: Dog): bool {\n" +
                "    var t = typeOf(d)\n" +
                "    return d is t\n" +
                "}\n");
            CheckNoErrors("无诊断（is 动态降级）", unit2);
            TestHarness.Check("is 动态降级形态", LoweredDescribe.Body(BodyOf(lowered2, "k")),
                "Body(k, [t: Type<Dog>], " +
                "[Decl(t, Type<Dog>, = TypeOf(Param(d,Dog), Type<Dog>)); " +
                "Return(Is(Param(d,Dog), dyn Local(t,Type<Dog>)))])");
            var boundDynIs = (BoundTypeCheckExpression)((BoundReturnStatement)
                bound2.Single(b => b.Method.Name == "k").Body.Statements[1]).Value!;
            var loweredDynIs = (LoweredTypeCheckExpression)((LoweredReturnStatement)
                BodyOf(lowered2, "k").Body.Statements[1]).Value!;
            TestHarness.CheckTrue("动态 is Origin 回指 + TargetValue 递归降级",
                ReferenceEquals(loweredDynIs.Origin, boundDynIs)
                && loweredDynIs.TargetValue != null
                && !ReferenceEquals(loweredDynIs.TargetValue, boundDynIs.TargetValue)
                && ReferenceEquals(loweredDynIs.TargetValue!.Origin, boundDynIs.TargetValue!));
        }

        // ===== typeOf 降级（S8a，恒等降级无脱糖；BIL §12.5 直接对应）=====
        private static void TestTypeOfLowering()
        {
            // 值形态：Operand 递归降级 + Origin 回指
            var (unit, bound, lowered) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): Type\\<Dog> { return typeOf(d) }\n");
            CheckNoErrors("无诊断（typeOf 值形态降级）", unit);
            TestHarness.Check("typeOf 值形态降级形态",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [Return(TypeOf(Param(d,Dog), Type<Dog>))])");
            var boundTypeOf = (BoundTypeOfExpression)((BoundReturnStatement)
                bound.Single(b => b.Method.Name == "f").Body.Statements[0]).Value!;
            var loweredTypeOf = (LoweredTypeOfExpression)((LoweredReturnStatement)
                BodyOf(lowered, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("typeOf 值形态 Origin 回指 + Operand 递归降级",
                ReferenceEquals(loweredTypeOf.Origin, boundTypeOf)
                && loweredTypeOf.Operand != null
                && ReferenceEquals(loweredTypeOf.Operand!.Origin, boundTypeOf.Operand!)
                && loweredTypeOf.TargetType == null);

            // 类型形态：TargetType 符号透传
            var (unit2, bound2, lowered2) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func m(): Type\\<Animal> { return typeOf(Animal) }\n");
            CheckNoErrors("无诊断（typeOf 类型形态降级）", unit2);
            TestHarness.Check("typeOf 类型形态降级形态",
                LoweredDescribe.Body(BodyOf(lowered2, "m")),
                "Body(m, [], [Return(TypeOf(type Animal, Type<Animal>))])");
            var boundTypeForm = (BoundTypeOfExpression)((BoundReturnStatement)
                bound2.Single(b => b.Method.Name == "m").Body.Statements[0]).Value!;
            var loweredTypeForm = (LoweredTypeOfExpression)((LoweredReturnStatement)
                BodyOf(lowered2, "m").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("typeOf 类型形态 Origin 回指 + 目标类型符号透传",
                ReferenceEquals(loweredTypeForm.Origin, boundTypeForm)
                && ReferenceEquals(loweredTypeForm.TargetType, boundTypeForm.TargetType)
                && loweredTypeForm.Operand == null);
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
