namespace LatteCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== try-catch-finally（S7e，SYNTAX §8）=====
        private static void TestTry()
        {
            TestHarness.Section("P3 Try-Catch-Finally");

            // 基本形态：catch 变量 const，命中即已赋值
            var (unit, bodies) = BindUnit(
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
            CheckNoErrors("try-catch 无诊断", unit);
            TestHarness.Check("try-catch 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [e: MyException], [Try([Throw(New(MyException, []))], " +
                "[Catch(e, MyException, [CallStmt(handle, [Local(e,MyException)])])])])");
            var tryStmt = (BoundTryStatement)((BoundBlock)BodyOf(bodies, "f").Body).Statements[0];
            TestHarness.CheckTrue("catch 变量 const",
                tryStmt.Catches[0].Variable != null && tryStmt.Catches[0].Variable!.IsConst);

            // _: 无变量形态
            var (unit2, bodies2) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func risky() {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func g() {\n" +
                "    try {\n" +
                "        risky()\n" +
                "    } catch (_: MyException) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("_: catch 无诊断", unit2);
            TestHarness.Check("_: catch 形态", BoundDescribe.Body(BodyOf(bodies2, "g")),
                "Body(g, [], [Try([CallStmt(risky, [])], " +
                "[Catch(MyException, [CallStmt(log, [])])])])");
            var try2 = (BoundTryStatement)((BoundBlock)BodyOf(bodies2, "g").Body).Statements[0];
            TestHarness.CheckTrue("_: 无 catch 变量", try2.Catches[0].Variable == null);

            // finally(e)：e 类型 = Nullable<core.Exception>，const
            var (unit3, bodies3) = BindUnit(
                "func risky() {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func h() {\n" +
                "    try {\n" +
                "        risky()\n" +
                "    } finally(e) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("finally(e) 无诊断", unit3);
            TestHarness.Check("finally(e) 形态", BoundDescribe.Body(BodyOf(bodies3, "h")),
                "Body(h, [e: Exception?], [Try([CallStmt(risky, [])], [], " +
                "Finally(e, [CallStmt(log, [])]))])");
            var try3 = (BoundTryStatement)((BoundBlock)BodyOf(bodies3, "h").Body).Statements[0];
            TestHarness.CheckTrue("finally 变量类型 Nullable<Exception> 且 const",
                try3.FinallyVariable != null && try3.FinallyVariable.IsConst
                && try3.FinallyVariable.Type!.Name == "Nullable");

            // 诊断：catch 类型与 Exception 不兼容
            var (unit4, _) = BindUnit(
                "func bad() {\n" +
                "    try {\n" +
                "    } catch (e: i32) {\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("catch 类型不兼容", unit4.Diagnostics,
                "catch type must be compatible with 'Exception' (got 'i32')");

            // 诊断：catch 变量 const 赋值拒绝
            var (unit5, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func cb() {\n" +
                "    try {\n" +
                "    } catch (e: MyException) {\n" +
                "        e = new MyException()\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("catch 变量只读", unit5.Diagnostics,
                "Cannot assign to const 'e'");

            // definite assignment：try/catch 交集——仅 try 赋值不够
            var (unit6, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func da(): i32 {\n" +
                "    var x: i32\n" +
                "    try {\n" +
                "        x = 1\n" +
                "    } catch (_: MyException) {\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("仅 try 赋值报未赋值", unit6.Diagnostics,
                "Use of unassigned local variable 'x'");

            // definite assignment：try 与 catch 都赋值 → 交集成立
            var (unit7, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func da2(): i32 {\n" +
                "    var x: i32\n" +
                "    try {\n" +
                "        x = 1\n" +
                "    } catch (_: MyException) {\n" +
                "        x = 2\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("try/catch 双赋值通过", unit7);

            // definite assignment：无 catch 时 try 直通（异常必穿透）
            var (unit8, _) = BindUnit(
                "func da3(): i32 {\n" +
                "    var x: i32\n" +
                "    try {\n" +
                "        x = 1\n" +
                "    } finally(f) {\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无 catch try 直通", unit8);

            // definite assignment：finally 恒执行并集
            var (unit9, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func da4(): i32 {\n" +
                "    var x: i32\n" +
                "    try {\n" +
                "        x = 1\n" +
                "    } catch (_: MyException) {\n" +
                "    } finally(f) {\n" +
                "        x = 3\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("finally 并集通过", unit9);

            // GuaranteesReturn：finally 终止覆盖所有路径
            var (unit10, _) = BindUnit(
                "func risky() {\n" +
                "}\n" +
                "func gr(): i32 {\n" +
                "    try {\n" +
                "        risky()\n" +
                "    } finally(f) {\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("finally 终止即保证返回", unit10);

            // GuaranteesReturn：try 与全部 catch 都返回
            var (unit11, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func gr2(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } catch (_: MyException) {\n" +
                "        return 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全分支返回通过", unit11);

            // GuaranteesReturn：无 catch 时 try 单块判定（All 真空 true）
            var (unit12, _) = BindUnit(
                "func gr3(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } finally(f) {\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无 catch try 返回通过", unit12);

            // 诊断：try 返回但 catch 不返回
            var (unit13, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func gr4(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } catch (_: MyException) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("catch 不返回报缺失", unit13.Diagnostics,
                "Function 'gr4' must return a value on all code paths");
        }

        // ===== seq（S7e，SYNTAX §10）=====
        private static void TestSeq()
        {
            TestHarness.Section("P3 Seq");

            // 语句形态：块级直通（作用域/assigned 语义同裸块）
            var (unit, bodies) = BindUnit(
                "func s() {\n" +
                "    seq {\n" +
                "        var x = 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("seq 语句无诊断", unit);
            TestHarness.Check("seq 语句形态", BoundDescribe.Body(BodyOf(bodies, "s")),
                "Body(s, [x: i32], [Seq([Decl(x, i32, = Int(1,i32))])])");

            // volatile 语句形态
            var (unit2, bodies2) = BindUnit(
                "func work() {\n" +
                "}\n" +
                "func s2() {\n" +
                "    volatile seq {\n" +
                "        work()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("volatile seq 无诊断", unit2);
            TestHarness.Check("volatile seq 形态", BoundDescribe.Body(BodyOf(bodies2, "s2")),
                "Body(s2, [], [SeqVolatile([CallStmt(work, [])])])");

            // definite assignment 直通：seq 内赋值对外可见
            var (unit3, _) = BindUnit(
                "func sd(): i32 {\n" +
                "    var x: i32\n" +
                "    seq {\n" +
                "        x = 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("seq 赋值直通", unit3);

            // 表达式形态：显式 return@_
            var (unit4, bodies4) = BindUnit(
                "func se(): i32 {\n" +
                "    return seq { return@_ 42 }\n" +
                "}\n");
            CheckNoErrors("seq 表达式无诊断", unit4);
            TestHarness.Check("seq 表达式形态", BoundDescribe.Body(BodyOf(bodies4, "se")),
                "Body(se, [], [Return(SeqExpr(ValueBlock(_, i32, [ReturnValue(_, Int(42,i32))])))])");

            // 表达式形态：隐式取值（单表达式语句）
            var (unit5, bodies5) = BindUnit(
                "func si(): i32 {\n" +
                "    return seq { 42 }\n" +
                "}\n");
            CheckNoErrors("隐式取值无诊断", unit5);
            TestHarness.Check("隐式取值形态", BoundDescribe.Body(BodyOf(bodies5, "si")),
                "Body(si, [], [Return(SeqExpr(ValueBlock(_, i32, implicit, [ExprStmt(Int(42,i32))])))])");

            // 表达式形态：named 标签
            var (unit6, bodies6) = BindUnit(
                "func sn(): i32 {\n" +
                "    return seq named calc { return@calc 7 }\n" +
                "}\n");
            CheckNoErrors("named seq 无诊断", unit6);
            TestHarness.Check("named seq 形态", BoundDescribe.Body(BodyOf(bodies6, "sn")),
                "Body(sn, [], [Return(SeqExpr(ValueBlock(calc, i32, [ReturnValue(calc, Int(7,i32))])))])");

            // 表达式形态：volatile 置位到值块
            var (unit7, bodies7) = BindUnit(
                "func sv(): i32 {\n" +
                "    return volatile seq { 1 }\n" +
                "}\n");
            CheckNoErrors("volatile seq 表达式无诊断", unit7);
            TestHarness.Check("volatile 置位", BoundDescribe.Body(BodyOf(bodies7, "sv")),
                "Body(sv, [], [Return(SeqExpr(ValueBlock(_, i32, implicit, volatile, " +
                "[ExprStmt(Int(1,i32))])))])");

            // 诊断：表达式形态无产值
            var (unit8, _) = BindUnit(
                "func sb(): i32 {\n" +
                "    return seq { var x = 1\nreturn@_ x }\n" +
                "}\n");
            CheckNoErrors("多语句显式 return@ 无诊断", unit8);
            var (unit9, _) = BindUnit(
                "func sb2(): i32 {\n" +
                "    return seq { var x = 1 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("无产值拒绝", unit9.Diagnostics,
                "seq expression must produce a value (at least one path must return@ a value)");

            // 诊断：using 绑定归 S13（语句与表达式形态同拦截）
            var (unit10, _) = BindUnit(
                "func openFile(): i32 {\n" +
                "    return 1\n" +
                "}\n" +
                "func use(f: i32) {\n" +
                "}\n" +
                "func su() {\n" +
                "    seq using(const file = openFile()) { use(file) }\n" +
                "}\n");
            TestHarness.CheckSemanticError("using 拦截", unit10.Diagnostics,
                "P3: using bindings are not supported yet (S13)");

            // 诊断：语句 seq 不压值块栈——return@ 指向它报未定义标签
            var (unit11, _) = BindUnit(
                "func sl(): i32 {\n" +
                "    seq {\n" +
                "        return@_ 1\n" +
                "    }\n" +
                "    return 2\n" +
                "}\n");
            TestHarness.CheckSemanticError("语句 seq 无标签", unit11.Diagnostics,
                "Undefined value block label: '_'");

            // return@ 穿透语句 seq 命中外层值块（末语句为 seq → 体穿透判定）
            var (unit12, bodies12) = BindUnit(
                "func st(): i32 {\n" +
                "    return seq {\n" +
                "        var dummy = 0\n" +
                "        seq {\n" +
                "            return@_ 1\n" +
                "        }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("seq 穿透无诊断", unit12);
            TestHarness.Check("seq 穿透形态", BoundDescribe.Body(BodyOf(bodies12, "st")),
                "Body(st, [dummy: i32], [Return(SeqExpr(ValueBlock(_, i32, [Decl(dummy, i32, = Int(0,i32)); Seq([ReturnValue(_, Int(1,i32))])])))])");

            // return@ 穿透 try 命中外层值块（try 与全部 catch 终止）
            var (unit13, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func tt(): i32 {\n" +
                "    return seq {\n" +
                "        var dummy = 0\n" +
                "        try {\n" +
                "            return@_ 1\n" +
                "        } catch (_: MyException) {\n" +
                "            return@_ 2\n" +
                "        }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("try 穿透无诊断", unit13);

            // return@ 穿透 try——finally 终止覆盖
            var (unit14, _) = BindUnit(
                "func tt2(): i32 {\n" +
                "    return seq {\n" +
                "        var dummy = 0\n" +
                "        try {\n" +
                "            dummy = 1\n" +
                "        } finally(f) {\n" +
                "            return@_ 3\n" +
                "        }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("finally 覆盖穿透无诊断", unit14);
        }

        // ===== return@语句seq（M61，SYNTAX §6.1：提前结束该块，不携带值）=====
        private static void TestSeqExit()
        {
            TestHarness.Section("P3 return@statement-seq (M61)");

            // 正例：命中即 BoundSeqExitStatement；嵌套块内穿透
            var (unit, bodies) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    seq named outer {\n" +
                "        if (x > 0) { return@outer }\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（return@语句seq）", unit);
            TestHarness.Check("SeqExit 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Seq@outer([If(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[SeqExit(@outer)])]); Return(Param(x,i32))])");

            // 负例：语句 seq 目标必须不携带值
            var (unit2, _) = BindUnit(
                "func f() {\n    seq named s {\n        return@s 1\n    }\n}\n");
            TestHarness.CheckSemanticError("语句 seq 不带值", unit2.Diagnostics,
                "return@s cannot carry a value (target is a statement seq)");

            // 负例：隔循环拦截（seq 在循环外，return@ 在循环内）
            var (unit3, _) = BindUnit(
                "func f(x: i32) {\n" +
                "    seq named s {\n" +
                "        while (x > 0) { return@s }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("隔循环拒绝", unit3.Diagnostics,
                "return@s across a loop boundary not supported yet (S7c)");

            // 负例：隔值块拦截（return@seq 在值块内——continuation 无法表达）
            var (unit4, _) = BindUnit(
                "func f(c: bool): i32 {\n" +
                "    seq named s {\n" +
                "        var v = if (c) { return@s } else { return@_ 1 }\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("隔值块拒绝", unit4.Diagnostics,
                "return@s across a value block boundary not supported yet");

            // 未 named 的语句 seq 不作目标（`_` 默认标签值块专属）
            var (unit5, _) = BindUnit(
                "func f() {\n    seq {\n        return@_\n    }\n}\n");
            TestHarness.CheckSemanticError("匿名语句 seq 非目标", unit5.Diagnostics,
                "Undefined value block label: '_'");
        }
    }
}
