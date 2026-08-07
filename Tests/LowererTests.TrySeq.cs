using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    // LowererTests try/seq 组：try-catch-finally 降级、seq 语句/表达式
    // 双形态、值块编织扩展（seq 透明 / try 规则 / finally 拦截）。

    public static partial class LowererTests
    {
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

            // return@语句seq（M61）：命中本层消费——then 分支 exit 删除，
            // 其后语句 x = 99 织入 else
            var (unit5, _, lowered5) = LowerUnit(
                "func f(x: i32): i32 {\n" +
                "    seq named outer {\n" +
                "        x = 1\n" +
                "        if (x > 0) { return@outer }\n" +
                "        x = 99\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（return@语句seq 降级）", unit5);
            TestHarness.Check("return@outer 编织（x=99 织入 else）",
                LoweredDescribe.Body(BodyOf(lowered5, "f")),
                "Body(f, [], [Seq([Assign(Param(x,i32), Int(1,i32)); " +
                "If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), [], " +
                "[Assign(Param(x,i32), Int(99,i32))])]); Return(Param(x,i32))])");

            // 嵌套无名 seq 传播（M61）：内层 exit 目标外层——内层截断
            // x = 5 并传播，外层消费并截断 x = 9
            var (unit6, _, lowered6) = LowerUnit(
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
            TestHarness.Check("exit 穿透无名内层 seq（两级截断）",
                LoweredDescribe.Body(BodyOf(lowered6, "g")),
                "Body(g, [], [Seq([Seq([If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), [], " +
                "[Assign(Param(x,i32), Int(5,i32)); Assign(Param(x,i32), Int(9,i32))])])]); " +
                "Return(Param(x,i32))])");
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
            TestHarness.Check("using 单资源 nested try/finally",
                LoweredDescribe.Body(BodyOf(lowered, "single")),
                "Body(single, [r: Resource, .s0: Exception?], [Seq([Decl(r, Resource, = Call(acquire, [], Resource)); " +
                "Try([[CallStmt(use, [Local(r,Resource)])]], [], Finally([InstCallStmt(dispose, Local(r,Resource), [])]), .s0)])])");

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
            TestHarness.CheckTrue("using 多资源初始化源码顺序",
                ((LoweredLocalDeclarationStatement)many.Body.Statements[0]).Local.Name == "a" &&
                ((LoweredLocalDeclarationStatement)innerBody.Statements[0]).Local.Name == "b");
            TestHarness.CheckTrue("using 多资源 dispose 逆序",
                ((LoweredCallStatement)inner.FinallyBlock!.Statements[0]).Method.Name == "dispose" &&
                ((LoweredValueReferenceExpression)((LoweredCallStatement)
                    inner.FinallyBlock.Statements[0]).Receiver!).Symbol.Name == "b" &&
                ((LoweredCallStatement)outer.FinallyBlock!.Statements[0]).Method.Name == "dispose" &&
                ((LoweredValueReferenceExpression)((LoweredCallStatement)
                    outer.FinallyBlock.Statements[0]).Receiver!).Symbol.Name == "a");
            TestHarness.CheckTrue("using 多资源 try 嵌套",
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
            var exprOuter = (LoweredTryStatement)exprSeq.Body.Statements[1];
            var exprInnerBody = (LoweredBlock)exprOuter.TryBlock.Statements[0];
            var exprInner = (LoweredTryStatement)exprInnerBody.Statements[1];
            TestHarness.Check("表达式 using 完整 Lowered 形状", LoweredDescribe.Body(exprBody),
                "Body(exprUsing, [a: ExprResource, b: ExprResource, .s0: ExprResource, .s1: Exception?, .s2: Exception?], " +
                "[Seq([Decl(a, ExprResource, = Call(acquireExpr, [], ExprResource)); " +
                "Try([[Decl(b, ExprResource, = Local(a,ExprResource)); " +
                "Try([[Assign(Local(.s0,ExprResource), Local(b,ExprResource))]], [], " +
                "Finally([InstCallStmt(dispose, Local(b,ExprResource), [])]), .s1)]], [], " +
                "Finally([InstCallStmt(dispose, Local(a,ExprResource), [])]), .s2)]); " +
                "Return(Local(.s0,ExprResource))])");
            TestHarness.CheckTrue("表达式 using 结果局部来自值块",
                exprInner.TryBlock.Statements[0] is LoweredBlock
                && exprSeq.Body.Statements.Count == 2
                && exprBody.Body.Statements[1] is LoweredReturnStatement);
            TestHarness.CheckTrue("表达式 using dispose 逆序",
                ((LoweredValueReferenceExpression)((LoweredCallStatement)
                    exprInner.FinallyBlock!.Statements[0]).Receiver!).Symbol.Name == "b" &&
                ((LoweredValueReferenceExpression)((LoweredCallStatement)
                    exprOuter.FinallyBlock!.Statements[0]).Receiver!).Symbol.Name == "a");
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

            // 已知边界：复杂外层值块 continuation 穿越 using 的 try/finally
            // 仍必须由 S7e P4 拦截，不能因表达式 using 的正常降级而放宽。
            var (unit5, _, lowered5) = LowerUnitWithStdlib(
                "class BoundaryResource implements core.IDisposable { pub override func dispose() { } }\n" +
                "func acquireBoundary(): BoundaryResource { return new BoundaryResource() }\n" +
                "func boundary(c: bool): i32 { return seq { " +
                "seq using(const r = acquireBoundary()) { if (c) { return@_ 1 } } " +
                "return@_ 2 } }\n");
            TestHarness.CheckSemanticError("using 外层 continuation 已知边界拦截", unit5.Diagnostics,
                "P4: value block weaving across try-finally with partial termination " +
                "is not supported yet (S7e)");
            TestHarness.CheckTrue("using 外层 continuation 拦截后跳过函数体",
                !lowered5.Any(b => b.Method.Name == "boundary"));
        }
    }
}
