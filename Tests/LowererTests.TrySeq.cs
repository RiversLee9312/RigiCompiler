using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler.Tests
{
    // LowererTests try/seq 组：try-catch-finally 降级、seq 语句/表达式
    // 双形态、Stage B route 展开（return@ 标记 → route local + break /
    // dispatcher relay）。

    public static partial class LowererTests
    {
        // ===== try 降级（S7e，BIL §16.7）=====
        private static void TestTryLowering()
        {
            // 有名 catch：体头合成「变量 = cast slot」，slot 为合成 .sN
            var (unit, bound, lowered) = LowerUnitWithStdlib(
                "class MyException : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
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
            CaseAssertions.Check("有名 catch 合成 cast", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [e: MyException, .s0: Exception?, .b0: .breakid], " +
                "[Try([Throw(New(MyException, []))], " +
                "[Catch(e, MyException, [Assign(Local(e,MyException), " +
                "Cast(Local(.s0,Exception?), MyException, MyException)); " +
                "CallStmt(handle, [Local(e,MyException)])])], .s0, .b0)])");
            var tryStmt = (LoweredTryStatement)BodyOf(lowered, "f").Body.Statements[0];
            CaseAssertions.CheckTrue("slot 类型 Nullable<Exception>",
                tryStmt.ExceptionSlot.Type!.Name == "Nullable");
            // 合成 cast 赋值的 Origin 指 BoundCatchClause
            var boundTry = (BoundTryStatement)((BoundBlock)
                bound.Single(b => b.Method.Name == "f").Body).Statements[0];
            CaseAssertions.CheckTrue("合成 cast Origin 指 catch 子句",
                ReferenceEquals(tryStmt.Catches[0].Body.Statements[0].Origin,
                    boundTry.Catches[0]));

            // finally(e)：slot 即 finally 变量（不合成 .sN）
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
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
            CaseAssertions.Check("finally 变量即 slot", LoweredDescribe.Body(BodyOf(lowered2, "h")),
                "Body(h, [e: Exception?, .b0: .breakid], [Try([CallStmt(log, [])], [], " +
                "Finally([CallStmt(log, [])]), e, .b0)])");

            // _: 无变量 catch——体头无合成 cast
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
                "class MyException : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
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
            CaseAssertions.Check("_: catch 无合成 cast", LoweredDescribe.Body(BodyOf(lowered3, "g")),
                "Body(g, [.s0: Exception?, .b0: .breakid], [Try([CallStmt(log, [])], " +
                "[Catch(MyException, [CallStmt(log, [])])], .s0, .b0)])");
        }

        // ===== seq 降级（S7e，BIL §3.4 call 化）=====
        private static void TestSeqLowering()
        {
            // 语句形态恒等降级
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func s() {\n" +
                "    seq {\n" +
                "        var x = 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（seq 语句）", unit);
            CaseAssertions.Check("seq 语句降级", LoweredDescribe.Body(BodyOf(lowered, "s")),
                "Body(s, [x: i32, .b0: .breakid], [Seq([Decl(x, i32, = Int(1,i32))], .b0)])");

            // volatile 语句形态
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "func work() {\n" +
                "}\n" +
                "func s2() {\n" +
                "    volatile seq {\n" +
                "        work()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（volatile seq）", unit2);
            CaseAssertions.Check("volatile seq 降级", LoweredDescribe.Body(BodyOf(lowered2, "s2")),
                "Body(s2, [.b0: .breakid], [SeqVolatile([CallStmt(work, [])], .b0)])");

            // 表达式形态脱糖：合成结果局部 + 前置 seq 块（值块降级写结果局部）
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
                "func se(): i32 {\n" +
                "    return seq { return@_ 42 }\n" +
                "}\n");
            CheckNoErrors("无诊断（seq 表达式）", unit3);
            CaseAssertions.Check("seq 表达式脱糖", LoweredDescribe.Body(BodyOf(lowered3, "se")),
                "Body(se, [.s0: i32, .b0: .breakid], [Seq([Assign(Local(.s0,i32), Int(42,i32))], .b0); Return(Local(.s0,i32))])");

            // volatile 表达式形态
            var (unit4, _, lowered4) = LowerUnitWithStdlib(
                "func sv(): i32 {\n" +
                "    return volatile seq { 1 }\n" +
                "}\n");
            CheckNoErrors("无诊断（volatile seq 表达式）", unit4);
            CaseAssertions.Check("volatile seq 表达式脱糖",
                LoweredDescribe.Body(BodyOf(lowered4, "sv")),
                "Body(sv, [.s0: i32, .b0: .breakid], [SeqVolatile([Assign(Local(.s0,i32), Int(1,i32))], .b0); " +
                "Return(Local(.s0,i32))])");

            // return@语句seq（M61；Stage B route 形态）：跨 region exit
            // （if → seq）写 route 局部后 break if region，if 后
            // dispatcher relay（route==1 → break seq region），
            // 其后语句 x = 99 静死保留原位（break 后不可达）
            var (unit5, _, lowered5) = LowerUnitWithStdlib(
                "func f(x: i32): i32 {\n" +
                "    seq named outer {\n" +
                "        x = 1\n" +
                "        if (x > 0) { return@outer }\n" +
                "        x = 99\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（return@语句seq 降级）", unit5);
            CaseAssertions.Check("return@outer route 展开（x=99 静死保留）",
                LoweredDescribe.Body(BodyOf(lowered5, "f")),
                "Body(f, [.b0: .breakid, .b1: .breakid, .s0: i32, .b2: .breakid], " +
                "[Seq([Assign(Param(x,i32), Int(1,i32)); Assign(Local(.s0,i32), Const(0,i32)); " +
                "If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(.s0,i32), Const(1,i32)); Break(.b1)], .b1); " +
                "If(Binary(CmpEq, Local(.s0,i32), Const(1,i32), bool), [Break(.b0)], .b2); " +
                "Assign(Param(x,i32), Int(99,i32))], .b0); Return(Param(x,i32))])");

            // 嵌套无名 seq 传播（M61；Stage B）：内层 exit 目标外层——
            // 内层 seq 与外层各一条 route/dispatcher 链 relay；
            // x = 5 / x = 9 均为静死保留
            var (unit6, _, lowered6) = LowerUnitWithStdlib(
                "func g(x: i32): i32 {\n" +
                "    seq named outer {\n" +
                "        seq {\n" +
                "            if (x > 0) { return@outer }\n" +
                "            x = 5\n" +
                "        }\n" +
                "        x = 9\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（嵌套 seq exit 传播）", unit6);
            CaseAssertions.Check("exit 穿透无名内层 seq（两级 relay）",
                LoweredDescribe.Body(BodyOf(lowered6, "g")),
                "Body(g, [.b0: .breakid, .b1: .breakid, .b2: .breakid, .s0: i32, .s1: i32, " +
                ".b3: .breakid, .b4: .breakid], " +
                "[Seq([Assign(Local(.s1,i32), Const(0,i32)); " +
                "Seq([Assign(Local(.s0,i32), Const(0,i32)); " +
                "If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(.s0,i32), Const(1,i32)); Break(.b2)], .b2); " +
                "If(Binary(CmpEq, Local(.s0,i32), Const(1,i32), bool), " +
                "[Assign(Local(.s1,i32), Const(1,i32)); Break(.b1)], .b3); " +
                "Assign(Param(x,i32), Int(5,i32))], .b1); " +
                "If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), [Break(.b0)], .b4); " +
                "Assign(Param(x,i32), Int(9,i32))], .b0); Return(Param(x,i32))])");
        }

        // ===== using 降级（S10：初始化顺序 + nested try/finally）=====
        private static void TestUsingLowering()
        {
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "class Resource implements core.IDisposable {\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "func acquire(): Resource { return new Resource() }\n" +
                "func use(r: Resource) { }\n" +
                "func single() {\n" +
                "    seq using(const r = acquire()) { use(r) }\n" +
                "}\n");
            CheckNoErrors("using 单资源降级无诊断", unit);
            CaseAssertions.Check("using 单资源 nested try/finally",
                LoweredDescribe.Body(BodyOf(lowered, "single")),
                "Body(single, [r: Resource, .b0: .breakid, .s0: Exception?, .b1: .breakid], " +
                "[Seq([Decl(r, Resource, = Call(acquire, [], Resource)); " +
                "Try([[CallStmt(use, [Local(r,Resource)])]], [], " +
                "Finally([InstCallStmt(dispose, Local(r,Resource), [])]), .s0, .b1)], .b0)])");

            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "class Resource2 implements core.IDisposable {\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "func first(): Resource2 { return new Resource2() }\n" +
                "func second(a: Resource2): Resource2 { return a }\n" +
                "func use2(a: Resource2, b: Resource2) { }\n" +
                "func many() {\n" +
                "    seq using(const a = first()) using(var b: Resource2 = second(a)) { use2(a, b) }\n" +
                "}\n");
            CheckNoErrors("using 多资源降级无诊断", unit2);
            var many = (LoweredSeqBlock)BodyOf(lowered2, "many").Body.Statements.Single();
            var outer = (LoweredTryStatement)many.Body.Statements[1];
            var innerBody = (LoweredBlock)outer.TryBlock.Statements[0];
            var inner = (LoweredTryStatement)innerBody.Statements[1];
            CaseAssertions.CheckTrue("using 多资源初始化源码顺序",
                ((LoweredLocalDeclarationStatement)many.Body.Statements[0]).Local.Name == "a" &&
                ((LoweredLocalDeclarationStatement)innerBody.Statements[0]).Local.Name == "b");
            CaseAssertions.CheckTrue("using 多资源 dispose 逆序",
                ((LoweredCallStatement)inner.FinallyBlock!.Statements[0]).Method.Name == "dispose" &&
                ((LoweredValueReferenceExpression)((LoweredCallStatement)
                    inner.FinallyBlock.Statements[0]).Receiver!).Symbol.Name == "b" &&
                ((LoweredCallStatement)outer.FinallyBlock!.Statements[0]).Method.Name == "dispose" &&
                ((LoweredValueReferenceExpression)((LoweredCallStatement)
                    outer.FinallyBlock.Statements[0]).Receiver!).Symbol.Name == "a");
            CaseAssertions.CheckTrue("using 多资源 try 嵌套",
                inner.TryBlock.Statements.Count == 1 &&
                inner.TryBlock.Statements[0] is LoweredBlock);

            var (unit3, _, lowered3) = LowerUnitWithStdlib(
                "class ExprResource implements core.IDisposable { pub override func dispose() { } }\n" +
                "func acquireExpr(): ExprResource { return new ExprResource() }\n" +
                "func exprUsing(): ExprResource { return seq using(const a = acquireExpr()) " +
                "using(var b: ExprResource = a) { return@_ b } }\n");
            CheckNoErrors("表达式 using 降级无诊断", unit3);
            var exprBody = BodyOf(lowered3, "exprUsing");
            var exprSeq = (LoweredSeqBlock)exprBody.Body.Statements[0];
            // Stage B route 形态：return@_ 穿两层 using try/finally——
            // 每层 try region 各一条 route 局部（进入前初始化 0）+
            // region 后 dispatcher relay；dispose finally 在 relay 前执行
            var exprOuter = (LoweredTryStatement)exprSeq.Body.Statements[2];
            var exprInnerBody = (LoweredBlock)exprOuter.TryBlock.Statements[0];
            var exprInner = (LoweredTryStatement)exprInnerBody.Statements[2];
            CaseAssertions.Check("表达式 using 完整 Lowered 形状", LoweredDescribe.Body(exprBody),
                "Body(exprUsing, [a: ExprResource, b: ExprResource, .s0: ExprResource, .b0: .breakid, " +
                ".s1: Exception?, .b1: .breakid, .s2: Exception?, .b2: .breakid, .s3: i32, .s4: i32, " +
                ".b3: .breakid, .b4: .breakid], " +
                "[Seq([Decl(a, ExprResource, = Call(acquireExpr, [], ExprResource)); " +
                "Assign(Local(.s4,i32), Const(0,i32)); " +
                "Try([[Decl(b, ExprResource, = Local(a,ExprResource)); " +
                "Assign(Local(.s3,i32), Const(0,i32)); " +
                "Try([[Assign(Local(.s0,ExprResource), Local(b,ExprResource)); " +
                "Assign(Local(.s3,i32), Const(1,i32)); Break(.b1)]], [], " +
                "Finally([InstCallStmt(dispose, Local(b,ExprResource), [])]), .s1, .b1); " +
                "If(Binary(CmpEq, Local(.s3,i32), Const(1,i32), bool), " +
                "[Assign(Local(.s4,i32), Const(1,i32)); Break(.b2)], .b3)]], [], " +
                "Finally([InstCallStmt(dispose, Local(a,ExprResource), [])]), .s2, .b2); " +
                "If(Binary(CmpEq, Local(.s4,i32), Const(1,i32), bool), [Break(.b0)], .b4)], .b0); " +
                "Return(Local(.s0,ExprResource))])");
            CaseAssertions.CheckTrue("表达式 using 结果局部来自值块",
                exprInner.TryBlock.Statements[0] is LoweredBlock
                && exprSeq.Body.Statements.Count == 4
                && exprBody.Body.Statements[1] is LoweredReturnStatement);
            CaseAssertions.CheckTrue("表达式 using dispose 逆序",
                ((LoweredValueReferenceExpression)((LoweredCallStatement)
                    exprInner.FinallyBlock!.Statements[0]).Receiver!).Symbol.Name == "b" &&
                ((LoweredValueReferenceExpression)((LoweredCallStatement)
                    exprOuter.FinallyBlock!.Statements[0]).Receiver!).Symbol.Name == "a");
        }

        // ===== Stage B route 展开（StructuredExitRouting）=====
        // 旧 continuation 编织（S7e）用例全部改写为 route 形态快照；
        // 原「try-finally 部分终止 P4 拦截」负例转为正例（新机制天然支持）
        private static void TestTryWeaving()
        {
            // seq 透明：内层 seq 不注册（无名），exit 目标外层值块——
            // if → 内层 seq → 外层 seq 两级 relay
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func f(c: bool): i32 {\n" +
                "    return seq {\n" +
                "        seq {\n" +
                "            if (c) { return@_ 1 }\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（seq route）", unit);
            var described = LoweredDescribe.Body(BodyOf(lowered, "f"));
            CaseAssertions.Check("seq 两级 relay", described, "Body(f, [.s0: i32, .b0: .breakid, .b1: .breakid, .b2: .breakid, .s1: i32, .s2: i32, .b3: .breakid, .b4: .breakid], [Seq([Assign(Local(.s2,i32), Const(0,i32)); Seq([Assign(Local(.s1,i32), Const(0,i32)); If(Param(c,bool), [Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b2)], .b2); If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), [Assign(Local(.s2,i32), Const(1,i32)); Break(.b1)], .b3)], .b1); If(Binary(CmpEq, Local(.s2,i32), Const(1,i32), bool), [Break(.b0)], .b4); Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            CaseAssertions.CheckTrue("routing 后无 StructuredExit 残留",
                !described.Contains("StructuredExit"), described);

            // try 无 finally：exit 穿 try region（if → try → seq 两级 relay）
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "func g(c: bool): i32 {\n" +
                "    return seq {\n" +
                "        try {\n" +
                "            if (c) { return@_ 1 }\n" +
                "        } catch (_: core.Exception) {\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（try route）", unit2);
            var described2 = LoweredDescribe.Body(BodyOf(lowered2, "g"));
            CaseAssertions.Check("try 无 finally route", described2, "Body(g, [.s0: i32, .b0: .breakid, .s1: Exception?, .b1: .breakid, .b2: .breakid, .s2: i32, .s3: i32, .b3: .breakid, .b4: .breakid], [Seq([Assign(Local(.s3,i32), Const(0,i32)); Try([Assign(Local(.s2,i32), Const(0,i32)); If(Param(c,bool), [Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s2,i32), Const(1,i32)); Break(.b1)], .b1); If(Binary(CmpEq, Local(.s2,i32), Const(1,i32), bool), [Assign(Local(.s3,i32), Const(1,i32)); Break(.b2)], .b3)], [Catch(Exception, [])], .s1, .b2); If(Binary(CmpEq, Local(.s3,i32), Const(1,i32), bool), [Break(.b0)], .b4); Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            CaseAssertions.CheckTrue("routing 后无 StructuredExit 残留（try）",
                !described2.Contains("StructuredExit"), described2);

            // finally 自身含 return@（终止覆盖）：finally 与 try 同 region，
            // route 局部覆写——其后 return@_ 2 静死保留
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
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
            CheckNoErrors("无诊断（finally 覆盖）", unit3);
            CaseAssertions.Check("finally 内 return@ route",
                LoweredDescribe.Body(BodyOf(lowered3, "h")), "Body(h, [dummy: i32, f: Exception?, .s0: i32, .b0: .breakid, .b1: .breakid, .s1: i32, .b2: .breakid], [Seq([Decl(dummy, i32, = Int(0,i32)); Assign(Local(.s1,i32), Const(0,i32)); Try([Assign(Local(dummy,i32), Int(1,i32))], [], Finally([Assign(Local(.s0,i32), Int(3,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b1)]), f, .b1); If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), [Break(.b0)], .b2); Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");

            // 原 S7e 拦截负例转正（Stage B 新机制天然支持）：
            // 有 finally + 部分分支终止 + continuation 非空 → 正常降级
            var (unit4, _, lowered4) = LowerUnitWithStdlib(
                "func ok(c: bool): i32 {\n" +
                "    return seq {\n" +
                "        try {\n" +
                "            if (c) { return@_ 1 }\n" +
                "        } finally(f) {\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（try-finally 部分终止转正）", unit4);
            var described4 = LoweredDescribe.Body(BodyOf(lowered4, "ok"));
            CaseAssertions.Check("try-finally 部分终止 route（原拦截转正）", described4,
                "Body(ok, [f: Exception?, .s0: i32, .b0: .breakid, .b1: .breakid, .b2: .breakid, .s1: i32, .s2: i32, .b3: .breakid, .b4: .breakid], [Seq([Assign(Local(.s2,i32), Const(0,i32)); Try([Assign(Local(.s1,i32), Const(0,i32)); If(Param(c,bool), [Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b1)], .b1); If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), [Assign(Local(.s2,i32), Const(1,i32)); Break(.b2)], .b3)], [], Finally([]), f, .b2); If(Binary(CmpEq, Local(.s2,i32), Const(1,i32), bool), [Break(.b0)], .b4); Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            CaseAssertions.CheckTrue("routing 后无 StructuredExit 残留（try-finally）",
                !described4.Contains("StructuredExit"), described4);

            // 原「using 外层 continuation 已知边界」转正：外层值块 exit
            // 穿越 using 的 try/finally——route relay 经两层 try region
            var (unit5, _, lowered5) = LowerUnitWithStdlib(
                "class BoundaryResource implements core.IDisposable { pub override func dispose() { } }\n" +
                "func acquireBoundary(): BoundaryResource { return new BoundaryResource() }\n" +
                "func boundary(c: bool): i32 { return seq { " +
                "seq using(const r = acquireBoundary()) { if (c) { return@_ 1 } } " +
                "return@_ 2 } }\n");
            CheckNoErrors("无诊断（using 边界转正）", unit5);
            var described5 = LoweredDescribe.Body(BodyOf(lowered5, "boundary"));
            CaseAssertions.Check("using 外层 exit route（原拦截转正）", described5, "Body(boundary, [r: BoundaryResource, .s0: i32, .b0: .breakid, .b1: .breakid, .b2: .breakid, .s1: Exception?, .b3: .breakid, .s2: i32, .s3: i32, .s4: i32, .b4: .breakid, .b5: .breakid, .b6: .breakid], [Seq([Assign(Local(.s4,i32), Const(0,i32)); Seq([Decl(r, BoundaryResource, = Call(acquireBoundary, [], BoundaryResource)); Assign(Local(.s3,i32), Const(0,i32)); Try([[Assign(Local(.s2,i32), Const(0,i32)); If(Param(c,bool), [Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s2,i32), Const(1,i32)); Break(.b2)], .b2); If(Binary(CmpEq, Local(.s2,i32), Const(1,i32), bool), [Assign(Local(.s3,i32), Const(1,i32)); Break(.b3)], .b4)]], [], Finally([InstCallStmt(dispose, Local(r,BoundaryResource), [])]), .s1, .b3); If(Binary(CmpEq, Local(.s3,i32), Const(1,i32), bool), [Assign(Local(.s4,i32), Const(1,i32)); Break(.b1)], .b5)], .b1); If(Binary(CmpEq, Local(.s4,i32), Const(1,i32), bool), [Break(.b0)], .b6); Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            CaseAssertions.CheckTrue("routing 后无 StructuredExit 残留（using）",
                !described5.Contains("StructuredExit"), described5);
        }
        // ===== Stage B：StructuredExitRouting 形态专项 =====
        // （嵌套 return@外层的 route 初始化 + dispatcher + relay 形态见
        // ControlFlow.TestValueBlockIfTransform 第一例）
        private static void TestStructuredExitRoutingForms()
        {
            // 简单值块早退（同 region）：写结果局部；exit 处 region 尾位
            // （落尾与 break 同落点）→ 冗余 break 省略，无 route local、
            // 无 dispatcher
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func f(c: bool): i32 {\n" +
                "    return if (c) { return@_ 1 } else { return@_ 2 }\n" +
                "}\n");
            CheckNoErrors("无诊断（同 region 早退）", unit);
            var described = LoweredDescribe.Body(BodyOf(lowered, "f"));
            CaseAssertions.Check("同 region 早退（无 route/dispatcher）", described,
                "Body(f, [.s0: i32, .b0: .breakid], [If(Param(c,bool), " +
                "[Assign(Local(.s0,i32), Int(1,i32))], " +
                "[Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            CaseAssertions.CheckTrue("无 route 局部（无 i32 合成常量）",
                !described.Contains("Const(0,i32)") && !described.Contains("Const(1,i32)"),
                described);
            CaseAssertions.CheckTrue("routing 后无 StructuredExit 残留（同 region）",
                !described.Contains("StructuredExit"), described);

            // dispatcher else-if 链：同一 region（if(a)）内两个不同外层
            // 目标的 exit——return@_ 1（外层 seq 表达式值块）与
            // return@inner（内层 named seq）各登一条 route，region 收尾
            // 生成两分支 else-if 链
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "func h(a: bool, b: bool): i32 {\n" +
                "    return seq {\n" +
                "        seq named inner {\n" +
                "            if (a) {\n" +
                "                if (b) { return@_ 1 }\n" +
                "                return@inner\n" +
                "            }\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（dispatcher 链）", unit2);
            var described2 = LoweredDescribe.Body(BodyOf(lowered2, "h"));
            CaseAssertions.Check("dispatcher else-if 链（两目标）", described2,
                "Body(h, [.s0: i32, .b0: .breakid, .b1: .breakid, .b2: .breakid, .b3: .breakid, " +
                ".s1: i32, .s2: i32, .s3: i32, .b4: .breakid, .b5: .breakid, .b6: .breakid, " +
                ".b7: .breakid], " +
                "[Seq([Assign(Local(.s3,i32), Const(0,i32)); " +
                "Seq([Assign(Local(.s2,i32), Const(0,i32)); " +
                "If(Param(a,bool), " +
                "[Assign(Local(.s1,i32), Const(0,i32)); " +
                "If(Param(b,bool), [Assign(Local(.s0,i32), Int(1,i32)); " +
                "Assign(Local(.s1,i32), Const(1,i32)); Break(.b2)], .b2); " +
                "If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), " +
                "[Assign(Local(.s2,i32), Const(1,i32)); Break(.b3)], .b4); " +
                "Assign(Local(.s2,i32), Const(2,i32)); Break(.b3)], .b3); " +
                "If(Binary(CmpEq, Local(.s2,i32), Const(1,i32), bool), " +
                "[Assign(Local(.s3,i32), Const(1,i32)); Break(.b1)], " +
                "[If(Binary(CmpEq, Local(.s2,i32), Const(2,i32), bool), [Break(.b1)], .b5)], .b6)], .b1); " +
                "If(Binary(CmpEq, Local(.s3,i32), Const(1,i32), bool), [Break(.b0)], .b7); " +
                "Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            CaseAssertions.CheckTrue("routing 后无 StructuredExit 残留（链）",
                !described2.Contains("StructuredExit"), described2);
        }

        // ===== 同 region 尾位 exit 省略冗余 break（tail-position 分析）=====
        private static void TestSameRegionTailExitElision()
        {
            // 多语句值块尾 return@_：exit 处 region 尾位（落尾与 break
            // 同落点）→ 只写结果局部、省略冗余 break
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func f(c: bool): i32 {\n" +
                "    return if (c) {\n" +
                "        const x = 1\n" +
                "        return@_ x\n" +
                "    } else {\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（多语句值块尾 exit）", unit);
            var described = LoweredDescribe.Body(BodyOf(lowered, "f"));
            CaseAssertions.Check("多语句值块尾 return@_ 省略 break", described,
                "Body(f, [x: i32, .s0: i32, .b0: .breakid], " +
                "[If(Param(c,bool), " +
                "[Decl(x, i32, = Int(1,i32)); Assign(Local(.s0,i32), Local(x,i32))], " +
                "[Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            CaseAssertions.CheckTrue("routing 后无 StructuredExit 残留（尾位）",
                !described.Contains("StructuredExit"), described);

            // 非尾位 same-region exit（白盒）：ordinary lowering 对值块
            // /named seq 的同层 exit 已提前截断其后语句（源码层构造不出
            // 非尾位 same-region exit），故直接构造 LoweredTree 调
            // pass——exit 后随语句时 break 不省略、后随语句静死截断
            var (unit2, bound2, lowered2) = LowerUnitWithStdlib(
                "func donor(c: bool): i32 {\n" +
                "    return if (c) { 1 } else { 2 }\n" +
                "}\n");
            CheckNoErrors("无诊断（白盒 donor）", unit2);
            var donorBound = bound2.Single(b => b.Method.Name == "donor");
            var donorIf = (BoundIfExpression)
                ((BoundReturnStatement)donorBound.Body.Statements[0]).Value!;
            var donorLowered = BodyOf(lowered2, "donor");
            var whiteCtx = new LowerContext(donorBound.Method);
            var whiteEnv = new LowerEnvironment(unit2);
            var whiteTarget = donorIf.TrueBranch;
            var whiteResult = donorLowered.Locals[0];
            var whiteBreakId = donorLowered.Locals[1];
            whiteCtx.ExitTargets.Register(whiteTarget, whiteResult, whiteBreakId);
            var whiteExit = new LoweredStructuredExit(whiteTarget, whiteTarget,
                new LoweredConstantExpression(whiteTarget, 1, unit2.Symbols.Bootstrap.Int32));
            var whiteDead = new LoweredAssignmentStatement(whiteTarget,
                SynthLocalFactory.ReferenceTo(whiteTarget, whiteResult),
                new LoweredConstantExpression(whiteTarget, 2, unit2.Symbols.Bootstrap.Int32));
            var whiteRegion = new LoweredIfStatement(whiteTarget,
                new LoweredConstantExpression(whiteTarget, false, unit2.Symbols.Bootstrap.Bool),
                new LoweredBlock(whiteTarget,
                    new List<LoweredStatement> { whiteExit, whiteDead }),
                null, whiteBreakId);
            var whiteRouted = StructuredExitRouting.Run(
                new LoweredBlock(whiteTarget, new List<LoweredStatement> { whiteRegion }),
                whiteCtx, whiteEnv);
            CaseAssertions.Check("非尾位 same-region exit 保留 break（后随语句截断）",
                LoweredDescribe.Block(whiteRouted!),
                "[If(Const(False,bool), " +
                "[Assign(Local(.s0,i32), Const(1,i32)); Break(.b0)], .b0)]");

            // finally 内尾位 return@ 仍发 break（finally 例外——必须以
            // abrupt completion 覆盖 SavedCompletion；递归进
            // FinallyBlock 时强制 isTail=false）
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
                "func h(): i32 {\n" +
                "    return seq {\n" +
                "        try {\n" +
                "            return@_ 1\n" +
                "        } finally(f) {\n" +
                "            return@_ 3\n" +
                "        }\n" +
                "        return@_ 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（finally 尾位 exit）", unit3);
            var described3 = LoweredDescribe.Body(BodyOf(lowered3, "h"));
            CaseAssertions.Check("finally 内尾位 return@ 仍发 break", described3,
                "Body(h, [f: Exception?, .s0: i32, .b0: .breakid, .b1: .breakid, " +
                ".s1: i32, .b2: .breakid], " +
                "[Seq([Assign(Local(.s1,i32), Const(0,i32)); " +
                "Try([Assign(Local(.s0,i32), Int(1,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b1)], " +
                "[], " +
                "Finally([Assign(Local(.s0,i32), Int(3,i32)); Assign(Local(.s1,i32), Const(1,i32)); Break(.b1)]), " +
                "f, .b1); " +
                "If(Binary(CmpEq, Local(.s1,i32), Const(1,i32), bool), [Break(.b0)], .b2); " +
                "Assign(Local(.s0,i32), Int(2,i32))], .b0); Return(Local(.s0,i32))])");
            CaseAssertions.CheckTrue("routing 后无 StructuredExit 残留（finally）",
                !described3.Contains("StructuredExit"), described3);
        }
    }
}
