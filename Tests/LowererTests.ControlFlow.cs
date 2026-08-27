using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler.Tests
{
    // LowererTests 控制流组：bool 短路/if 表达式/复合赋值/值块 route 展开/
    // 循环降级/for 脱糖/switch/throw/else-if 链 route 展开（Stage B）。

    public static partial class LowererTests
    {
        // ===== S7b：bool 短路 and/or（BIL §11.3：if + 合成局部展开）=====
        private static void TestShortCircuit()
        {
            var (unit, bound, lowered) = LowerUnitWithStdlib(
                "func f(a: bool, b: bool): bool { return (a and b) }\n" +
                "func o(a: bool, b: bool): bool { return (a or b) }\n");
            CheckNoErrors("无诊断（短路展开）", unit);
            TestHarness.Check("a and b ⇒ if a { s = b } else { s = false }",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: bool, .b0: .breakid], " +
                "[If(Param(a,bool), [Assign(Local(.s0,bool), Param(b,bool))], " +
                "[Assign(Local(.s0,bool), Const(False,bool))], .b0); Return(Local(.s0,bool))])");
            TestHarness.Check("a or b ⇒ if a { s = true } else { s = b }",
                LoweredDescribe.Body(BodyOf(lowered, "o")),
                "Body(o, [.s0: bool, .b0: .breakid], " +
                "[If(Param(a,bool), [Assign(Local(.s0,bool), Const(True,bool))], " +
                "[Assign(Local(.s0,bool), Param(b,bool))], .b0); Return(Local(.s0,bool))])");

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
            var (unit, _, lowered) = LowerUnitWithStdlib(
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
                "Body(f, [r: i32, .s0: i32, .b0: .breakid], " +
                "[If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(.s0,i32), Int(1,i32))], " +
                "[Assign(Local(.s0,i32), Int(2,i32))], .b0); " +
                "Decl(r, i32, = Local(.s0,i32)); Return(Local(r,i32))])");
            TestHarness.Check("隐式取值值块写结果局部",
                LoweredDescribe.Body(BodyOf(lowered, "g")),
                "Body(g, [.s0: i32, .b0: .breakid], " +
                "[If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(.s0,i32), Param(x,i32))], " +
                "[Assign(Local(.s0,i32), Unary(Opposite, Param(x,i32), i32))], .b0); " +
                "Return(Local(.s0,i32))])");
        }

        // ===== S7b：复合赋值脱糖（前置赋值 + 表达式位 Target 引用）=====
        private static void TestCompoundAssignmentLowering()
        {
            var (unit, bound, lowered) = LowerUnitWithStdlib(
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

            // M60 单次求值（SYNTAX §13.2 通用规则定稿）：副作用 receiver
            // 物化合成局部——getBox() 求值一次；纯读取目标直通零物化
            //（上例锁定）。Q6 后索引读侧为 T?，a[i] op= 形态由显式读改
            // 写替代（receiver/index 由用户显式物化）
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub init(_ -> item)\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "class Box { pub var f: i32\n    pub init(_ -> f) }\n" +
                "func getBag(): Bag { return new Bag(0) }\n" +
                "func getBox(): Box { return new Box(0) }\n" +
                "func getI(): i32 { return 1 }\n" +
                "func bump(): i32 {\n" +
                "    const bag = getBag()\n" +
                "    const i = getI()\n" +
                "    bag[i] = ((bag[i] if? 0) + 2)\n" +
                "    return bag[i] if? 0\n" +
                "}\n" +
                "func bumpField(): i32 { var z = (getBox().f += 3)\nreturn z }\n");
            CheckNoErrors("无诊断（复合赋值单次求值）", unit2);
            TestHarness.Check("索引显式读改写回（Q6）",
                LoweredDescribe.Body(BodyOf(lowered2, "bump")),
                "Body(bump, [bag: Bag, i: i32, .s0: i32?, .s1: i32, .b0: .breakid, " +
                ".s2: i32?, .s3: i32, .b1: .breakid], " +
                "[Decl(bag, Bag, = Call(getBag, [], Bag)); " +
                "Decl(i, i32, = Call(getI, [], i32)); " +
                "Assign(Local(.s0,i32?), Index(Local(bag,Bag), Local(i,i32), i32?)); " +
                "If(Binary(CmpNe, Local(.s0,i32?), Const(null,i32?), bool), " +
                "[Assign(Local(.s1,i32), Cast(Local(.s0,i32?), i32, i32))], " +
                "[Assign(Local(.s1,i32), Int(0,i32))], .b0); " +
                "Assign(Index(Local(bag,Bag), Local(i,i32), i32), " +
                "Binary(Add, Local(.s1,i32), Int(2,i32), i32)); " +
                "Assign(Local(.s2,i32?), Index(Local(bag,Bag), Local(i,i32), i32?)); " +
                "If(Binary(CmpNe, Local(.s2,i32?), Const(null,i32?), bool), " +
                "[Assign(Local(.s3,i32), Cast(Local(.s2,i32?), i32, i32))], " +
                "[Assign(Local(.s3,i32), Int(0,i32))], .b1); " +
                "Return(Local(.s3,i32))])");
            TestHarness.Check("字段复合物化（receiver 一次）",
                LoweredDescribe.Body(BodyOf(lowered2, "bumpField")),
                "Body(bumpField, [z: i32, .s0: Box], " +
                "[Assign(Local(.s0,Box), Call(getBox, [], Box)); " +
                "Assign(InstField(f, Local(.s0,Box), i32), " +
                "Binary(Add, InstField(f, Local(.s0,Box), i32), Int(3,i32), i32)); " +
                "Decl(z, i32, = InstField(f, Local(.s0,Box), i32)); " +
                "Return(Local(z,i32))])");
        }

        // ===== Stage B：值块 return@ 的 route 展开（StructuredExitRouting）=====
        private static void TestValueBlockIfTransform()
        {
            // 中间 return@（跨 region：嵌套 if → 外层 if 表达式 region）→
            // route 局部 + dispatcher relay，其后语句静死保留原位
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) {\n" +
                "        if (x > 1) { return@_ 2 }\n" +
                "        return@_ 1\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（值块 route 展开）", unit);
            TestHarness.Check("跨 region return@（route + dispatcher）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [r: i32, .s0: i32, .b0: .breakid, .b1: .breakid, .s1: i32, .b2: .breakid], " +
                "[If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(.s1,i32), Const(0,i32)); " +
                "If(Binary(CmpGt, Param(x,i32), Int(1,i32), bool), " +
                "[Assign(Local(.s0,i32), Int(2,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b1)], .b1); " +
                "If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), [Break(.b0)], .b2); " +
                "Assign(Local(.s0,i32), Int(1,i32))], " +
                "[Assign(Local(.s0,i32), Int(0,i32))], .b0); " +
                "Decl(r, i32, = Local(.s0,i32)); Return(Local(r,i32))])");

            // 双分支都产标记 → if 之后语句为静死代码（routing 不移动
            // continuation——region 收尾展开后自然不可达，原地保留）
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
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
            CheckNoErrors("无诊断（双终止静死保留）", unit2);
            TestHarness.Check("双分支终止后语句静死保留",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [y: i32, r: i32, .s0: i32, .b0: .breakid, .b1: .breakid, .s1: i32, .b2: .breakid], " +
                "[If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(.s1,i32), Const(0,i32)); " +
                "If(Binary(CmpGt, Param(x,i32), Int(1,i32), bool), " +
                "[Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b1)], " +
                "[Assign(Local(.s0,i32), Int(2,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b1)], .b1); " +
                "If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), [Break(.b0)], .b2); " +
                "Decl(y, i32, = Int(3,i32)); Assign(Local(.s0,i32), Local(y,i32))], " +
                "[Assign(Local(.s0,i32), Int(0,i32))], .b0); " +
                "Decl(r, i32, = Local(.s0,i32)); Return(Local(r,i32))])");
        }

        // ===== S7c-1：循环降级（Judge 块/条件局部/BreakId 映射）=====
        private static void TestLoopLowering()
        {
            // while：条件求值移入 Judge 块（末尾写合成条件局部 .s0）
            var (unit, _, lowered) = LowerUnitWithStdlib(
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
            // BreakId.Type 为 null（.breakid 特例）
            var whileLoop = (LoweredLoop)BodyOf(lowered, "f").Body.Statements[1];
            var fLocals = BodyOf(lowered, "f").Locals;
            TestHarness.CheckTrue("条件局部 .s0（bool，引用相等）",
                whileLoop.Condition.Name == ".s0" && whileLoop.Condition.Type?.Name == "bool"
                && ReferenceEquals(whileLoop.Condition, fLocals[1]));
            TestHarness.CheckTrue("BreakId 局部 .b0（Type null，引用相等）",
                whileLoop.BreakId.Name == ".b0" && whileLoop.BreakId.Type == null
                && ReferenceEquals(whileLoop.BreakId, fLocals[2]));
            TestHarness.CheckTrue("while 非 rev", !whileLoop.IsRev);

            // do-while → loop.rev（IsRev）
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
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
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
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
                ReferenceEquals(((LoweredBreakStatement)inner.Body.Statements[0]).BreakId,
                    outer.BreakId));
            TestHarness.CheckTrue("continue 携内层 BreakId（引用相等）",
                ReferenceEquals(((LoweredContinueStatement)inner.Body.Statements[1]).BreakId,
                    inner.BreakId));

            // 条件内短路展开：前置语句落在 Judge 块（不污染循环前块）
            var (unit4, _, lowered4) = LowerUnitWithStdlib(
                "func f(a: bool, b: bool) {\n" +
                "    while ((a and b)) {\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（条件内短路）", unit4);
            TestHarness.Check("短路展开进 Judge 块", LoweredDescribe.Body(BodyOf(lowered4, "f")),
                "Body(f, [.s0: bool, .b0: .breakid, .s1: bool, .b1: .breakid], " +
                "[Loop([If(Param(a,bool), [Assign(Local(.s1,bool), Param(b,bool))], " +
                "[Assign(Local(.s1,bool), Const(False,bool))], .b1); " +
                "Assign(Local(.s0,bool), Local(.s1,bool))], .s0, [], .b0)])");

            // 值块内 break 穿透：直接发 BIL 跳转（无展开、无 routing
            // 介入）；else 分支的 return@_ 同 region 尾位展开（只写结果、
            // 省略冗余 break）
            var (unit5, _, lowered5) = LowerUnitWithStdlib(
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
                "Body(f, [r: i32, .s0: bool, .b0: .breakid, .s1: i32, .b1: .breakid], " +
                "[Decl(r, i32, = Int(0,i32)); " +
                "Loop([Assign(Local(.s0,bool), Binary(CmpGt, Param(x,i32), Int(0,i32), bool))], .s0, " +
                "[If(Binary(CmpEq, Param(x,i32), Int(5,i32), bool), [Break(.b0)], " +
                "[Assign(Local(.s1,i32), Int(1,i32))], .b1); " +
                "Assign(Local(r,i32), Local(.s1,i32)); " +
                "Assign(Param(x,i32), Binary(Sub, Param(x,i32), Int(1,i32), i32))], .b0); " +
                "Return(Local(r,i32))])");
        }

        // ===== 循环 region 的 return@ route + dispatcher =====
        private static void TestLoopStructuredExitRouting()
        {
            // while 体 return@_ 外层 seq：loop region 写 route + break，
            // 后随 dispatcher relay 出 seq
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func f(x: i32): i32 {\n" +
                "    return seq {\n" +
                "        while (x > 1) { return@_ 1 }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（循环 region route）", unit);
            var described = LoweredDescribe.Body(BodyOf(lowered, "f"));
            TestHarness.Check("while 体 return@ → loop route + dispatcher", described,
                "Body(f, [.s0: i32, .b0: .breakid, .s1: bool, .b1: .breakid, .s2: i32, .b2: .breakid], " +
                "[Seq([Assign(Local(.s2,i32), Const(0,i32)); " +
                "Loop([Assign(Local(.s1,bool), Binary(CmpGt, Param(x,i32), Int(1,i32), bool))], .s1, " +
                "[Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s2,i32), Const(1,i32)); Break(.b1)], .b1); " +
                "If(Binary(CmpEq, Local(.s2,i32), Const(1,i32), bool), [Break(.b0)], .b2); " +
                "Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            TestHarness.CheckTrue("routing 后无 StructuredExit 残留（loop）",
                !described.Contains("StructuredExit"), described);
            var seqBlock = (LoweredSeqBlock)BodyOf(lowered, "f").Body.Statements[0];
            var loopDispatcher = seqBlock.Body.Statements.OfType<LoweredIfStatement>()
                .Single(s => s.SeqRouteHintRoute != null);
            TestHarness.CheckTrue("loop dispatcher 打了 seq-route hint",
                loopDispatcher.SeqRouteHintRoute != null
                && loopDispatcher.SeqRouteHintRoute.Name == ".s2");

            // 嵌套循环：内层 return@ 穿两层 loop relay
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "func g(): i32 {\n" +
                "    return seq {\n" +
                "        while (true) {\n" +
                "            while (true) { return@_ 21 }\n" +
                "            return@_ 0\n" +
                "        }\n" +
                "        return@_ 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（嵌套循环 relay）", unit2);
            var described2 = LoweredDescribe.Body(BodyOf(lowered2, "g"));
            TestHarness.Check("嵌套循环两级 route + dispatcher", described2,
                "Body(g, [.s0: i32, .b0: .breakid, .s1: bool, .b1: .breakid, .s2: bool, .b2: .breakid, " +
                ".s3: i32, .s4: i32, .b3: .breakid, .b4: .breakid], " +
                "[Seq([Assign(Local(.s4,i32), Const(0,i32)); " +
                "Loop([Assign(Local(.s1,bool), Bool(True,bool))], .s1, " +
                "[Assign(Local(.s3,i32), Const(0,i32)); " +
                "Loop([Assign(Local(.s2,bool), Bool(True,bool))], .s2, " +
                "[Assign(Local(.s0,i32), Int(21,i32)); Assign(Local(.s3,i32), Const(1,i32)); Break(.b2)], .b2); " +
                "If(Binary(CmpEq, Local(.s3,i32), Const(1,i32), bool), " +
                "[Assign(Local(.s4,i32), Const(1,i32)); Break(.b1)], .b3); " +
                "Assign(Local(.s0,i32), Int(0,i32)); Assign(Local(.s4,i32), Const(1,i32)); Break(.b1)], .b1); " +
                "If(Binary(CmpEq, Local(.s4,i32), Const(1,i32), bool), [Break(.b0)], .b4); " +
                "Assign(Local(.s0,i32), Int(1,i32))], .b0); Return(Local(.s0,i32))])");
            TestHarness.CheckTrue("routing 后无 StructuredExit 残留（嵌套 loop）",
                !described2.Contains("StructuredExit"), described2);

            // break 不写 route：dispatcher fall-through 落到循环后 return@
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
                "func h(x: i32): i32 {\n" +
                "    return seq {\n" +
                "        while (x > 0) {\n" +
                "            if (x == 1) { break }\n" +
                "            return@_ 1\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（break 与 return@ 共存）", unit3);
            var described3 = LoweredDescribe.Body(BodyOf(lowered3, "h"));
            TestHarness.Check("break 不写 route、return@ 写 route", described3,
                "Body(h, [.s0: i32, .b0: .breakid, .s1: bool, .b1: .breakid, .b2: .breakid, .s2: i32, .b3: .breakid], " +
                "[Seq([Assign(Local(.s2,i32), Const(0,i32)); " +
                "Loop([Assign(Local(.s1,bool), Binary(CmpGt, Param(x,i32), Int(0,i32), bool))], .s1, " +
                "[If(Binary(CmpEq, Param(x,i32), Int(1,i32), bool), [Break(.b1)], .b2); " +
                "Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s2,i32), Const(1,i32)); Break(.b1)], .b1); " +
                "If(Binary(CmpEq, Local(.s2,i32), Const(1,i32), bool), [Break(.b0)], .b3); " +
                "Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            TestHarness.CheckTrue("routing 后无 StructuredExit 残留（break 共存）",
                !described3.Contains("StructuredExit"), described3);
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
                enumeratorLocalType is TypeSymbol enumeratorType
                && enumeratorType.ConstructedFrom != null
                && enumeratorType.ConstructedFrom.Name == "IEnumerator"
                && ReferenceEquals(enumeratorType.TypeArguments![0],
                    unit.Symbols.Bootstrap.Int32));
        }

        // ===== switch 降级（S7d：全值匹配 → LoweredSwitch；含 pattern → if 链）=====
        private static void TestSwitchLowering()
        {
            // 全值匹配语句形态：LoweredSwitch + 合成 .breakid（无人引用，仅满足形态）
            var (unit, bound, lowered) = LowerUnitWithStdlib(
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
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
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
                "Body(f, [.s0: i32, .b0: .breakid, .b1: .breakid], " +
                "[Assign(Local(.s0,i32), Param(x,i32)); " +
                "[If(Binary(CmpEq, Local(.s0,i32), Int(1,i32), bool), [Return(Int(1,i32))], " +
                "[If(Binary(CmpGt, Local(.s0,i32), Int(10,i32), bool), [Return(Int(2,i32))], " +
                "[Return(Int(0,i32))], .b0)], .b1)]])");
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
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
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
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "class MyException : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func f(): i32 {\n" +
                "    throw new MyException()\n" +
                "}\n");
            CheckNoErrors("无诊断（throw）", unit);
            TestHarness.Check("throw 恒等降级", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [Throw(New(MyException, []))])");

            // 值块内 throw：路径终止（其后语句截断，if 转换不编织 continuation）
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "class MyException : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) {\n" +
                "        throw new MyException()\n" +
                "    } else { 0 }\n" +
                "}\n");
            CheckNoErrors("无诊断（值块内 throw）", unit2);
            TestHarness.Check("值块内 throw 终止", LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [.s0: i32, .b0: .breakid], [If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Throw(New(MyException, []))], [Assign(Local(.s0,i32), Int(0,i32))], .b0); " +
                "Return(Local(.s0,i32))])");

            // throw 同块其后语句：静态不可达，StructuredExitRouting 截断
            // 不发射（Rigi 无 goto/label，死代码不存在被跳入复活的可能）
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
                "class MyException : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func g(): i32 { return 1 }\n" +
                "func f() {\n" +
                "    throw new MyException()\n" +
                "    var dead = g()\n" +
                "}\n");
            CheckNoErrors("无诊断（throw 后死代码）", unit3);
            // 语句被截断；dead 的「声明」是 P3 产物（Locals 归 P3 所有），
            // 仅作为从未赋值的局部留在 .vars——与未使用的普通声明同处置
            TestHarness.Check("throw 后同块语句截断", LoweredDescribe.Body(BodyOf(lowered3, "f")),
                "Body(f, [dead: i32], [Throw(New(MyException, []))])");
        }

        // ===== M46 回归（Stage B 形态）：else-if 链混合终止的 route 展开 =====
        private static void TestElseIfChainTransform()
        {
            // c2 命中路径产值必须留在结果局部（M46 修复前被后续语句覆盖
            // 为 3）；Stage B：跨 region return@ 经 route 局部 + dispatcher
            // relay 跳出，其后 x = 3 / return@_ x 为静死代码原位保留
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func f(c0: bool, c1: bool, c2: bool): i32 {\n" +
                "    var x = 0\n" +
                "    var r = if (c0) {\n" +
                "        if (c1) { return@_ 1 } else if (c2) { return@_ 2 } else { x = 2 }\n" +
                "        x = 3\n" +
                "        return@_ x\n" +
                "    } else { 0 }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（else-if 链 route 展开）", unit);
            TestHarness.Check("else-if 链混合终止 route 展开",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [x: i32, r: i32, .s0: i32, .b0: .breakid, .b1: .breakid, .b2: .breakid, " +
                ".s1: i32, .s2: i32, .b3: .breakid, .b4: .breakid], " +
                "[Decl(x, i32, = Int(0,i32)); " +
                "If(Param(c0,bool), " +
                "[Assign(Local(.s1,i32), Const(0,i32)); " +
                "If(Param(c1,bool), " +
                "[Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b2)], " +
                "[Assign(Local(.s2,i32), Const(0,i32)); " +
                "If(Param(c2,bool), " +
                "[Assign(Local(.s0,i32), Int(2,i32)); Assign(Local(.s2,i32), Const(1,i32)); Break(.b1)], " +
                "[Assign(Local(x,i32), Int(2,i32))], .b1); " +
                "If(Binary(CmpEq, Local(.s2,i32), Const(1,i32), bool), " +
                "[Assign(Local(.s1,i32), Const(1,i32)); Break(.b2)], .b3)], .b2); " +
                "If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), [Break(.b0)], .b4); " +
                "Assign(Local(x,i32), Int(3,i32)); Assign(Local(.s0,i32), Local(x,i32))], " +
                "[Assign(Local(.s0,i32), Int(0,i32))], .b0); " +
                "Decl(r, i32, = Local(.s0,i32)); Return(Local(r,i32))])");
        }
    }
}
