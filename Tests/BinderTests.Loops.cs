using System.Linq;

namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== 循环（S7c-1：while/do-while 形态、条件 bool、DA 规则、for 拦截）=====
        private static void TestLoops()
        {
            TestHarness.Section("P3 Loops");

            var (unit, bodies) = BindUnit(
                "func f(a: i32) {\n" +
                "    var x = 0\n" +
                "    while (x < a) {\n" +
                "        x = x + 1\n" +
                "    }\n" +
                "}\n" +
                "func g(a: i32) {\n" +
                "    var x = 0\n" +
                "    do {\n" +
                "        x = x + 1\n" +
                "    } while (x < a)\n" +
                "}\n");
            CheckNoErrors("无诊断（while/do-while）", unit);
            TestHarness.Check("while 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "Loop(while, Binary(CmpLt, Local(x,i32), Param(a,i32), bool), " +
                "[Assign(Local(x,i32), Binary(Add, Local(x,i32), Int(1,i32), i32))])])");
            TestHarness.Check("do-while 绑定形态", BoundDescribe.Body(BodyOf(bodies, "g")),
                "Body(g, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "Loop(do-while, Binary(CmpLt, Local(x,i32), Param(a,i32), bool), " +
                "[Assign(Local(x,i32), Binary(Add, Local(x,i32), Int(1,i32), i32))])])");

            var (unit2, _) = BindUnit("func f(x: i32) { while (x) { } }\n");
            TestHarness.CheckSemanticError("while 条件非 bool", unit2.Diagnostics,
                "loop condition must be bool (got 'i32')");

            var (unit3, _) = BindUnit("func f(x: i32) { do { } while (x) }\n");
            TestHarness.CheckSemanticError("do-while 条件非 bool", unit3.Diagnostics,
                "loop condition must be bool (got 'i32')");

            // stdlib 缺席（BindUnit 不带 stdlib）：范围循环先报 operator 缺失
            var (unit4, _) = BindUnit("func f() { for (i in 0 to 3) { } }\n");
            TestHarness.CheckSemanticError("stdlib 缺席（无 EnumerateInRange operator）",
                unit4.Diagnostics, "has no EnumerateInRange operator");

            // DA：while 后 = before（体可能零次执行）——循环内赋值不生效
            var (unit5, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    while (a > 0) {\n" +
                "        x = 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("DA：while 后仍报未赋值", unit5.Diagnostics,
                "Use of unassigned local variable 'x'");

            // DA：do-while 后 = 体尾集合（体至少执行一次）
            var (unit6, bodies6) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    do {\n" +
                "        x = 1\n" +
                "    } while (a > 0)\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("DA：do-while 后视为已赋值", unit6);
            TestHarness.Check("do-while 赋值生效形态", BoundDescribe.Body(BodyOf(bodies6, "f")),
                "Body(f, [x: i32], [Decl(x, i32); " +
                "Loop(do-while, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Assign(Local(x,i32), Int(1,i32))]); Return(Local(x,i32))])");

            // GuaranteesReturn：循环保守 false（while (true) 特例留口）
            var (unit7, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    while (a > 0) { return 1 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("循环内 return 不保证返回（保守）", unit7.Diagnostics,
                "must return a value on all code paths");
        }

        // ===== break/continue（S7c-1：标签栈解析、穿透值块、诊断）=====
        private static void TestLoopControl()
        {
            TestHarness.Section("P3 Loop Control");

            var (unit, bodies) = BindUnit(
                "func f(a: i32) {\n" +
                "    while (a > 0) {\n" +
                "        if (a == 5) { break }\n" +
                "        if (a == 2) { continue }\n" +
                "        a = a - 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（break/continue）", unit);
            TestHarness.Check("无标签 break/continue 形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Loop(while, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[If(Binary(CmpEq, Param(a,i32), Int(5,i32), bool), [Break]); " +
                "If(Binary(CmpEq, Param(a,i32), Int(2,i32), bool), [Continue]); " +
                "Assign(Param(a,i32), Binary(Sub, Param(a,i32), Int(1,i32), i32))])])");
            // 结构性事实：无标签命中最内层循环（引用相等）
            var loop = (BoundLoop)BodyOf(bodies, "f").Body.Statements[0];
            var breakStmt = (BoundLoopControl)((BoundIfStatement)
                loop.Body.Statements[0]).TrueBlock.Statements[0];
            TestHarness.CheckTrue("break 引用命中目标循环",
                ReferenceEquals(breakStmt.Target, loop));

            // named 嵌套标签：break@outer 穿透内层循环命中外层
            var (unit2, bodies2) = BindUnit(
                "func f(a: i32) {\n" +
                "    while (a > 0) named outer {\n" +
                "        while (a > 1) {\n" +
                "            break@outer\n" +
                "            continue\n" +
                "        }\n" +
                "        a = a - 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（named 标签）", unit2);
            TestHarness.Check("嵌套标签循环形态", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Loop(while@outer, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Loop(while, Binary(CmpGt, Param(a,i32), Int(1,i32), bool), " +
                "[Break@outer; Continue]); " +
                "Assign(Param(a,i32), Binary(Sub, Param(a,i32), Int(1,i32), i32))])])");
            // 结构性事实：break@outer 命中外层、无标签 continue 命中内层
            var outerLoop = (BoundLoop)BodyOf(bodies2, "f").Body.Statements[0];
            var innerLoop = (BoundLoop)outerLoop.Body.Statements[0];
            TestHarness.CheckTrue("break@outer 引用命中外层循环",
                ReferenceEquals(((BoundLoopControl)innerLoop.Body.Statements[0]).Target, outerLoop));
            TestHarness.CheckTrue("无标签 continue 引用命中内层循环",
                ReferenceEquals(((BoundLoopControl)innerLoop.Body.Statements[1]).Target, innerLoop));

            var (unit3, _) = BindUnit("func f() { while (true) { break@nope } }\n");
            TestHarness.CheckSemanticError("未定义循环标签", unit3.Diagnostics,
                "Undefined loop label: 'nope'");

            var (unit4, _) = BindUnit("func f() { break }\n");
            TestHarness.CheckSemanticError("循环外 break", unit4.Diagnostics,
                "'break' outside of a loop");

            var (unit5, _) = BindUnit("func f() { continue }\n");
            TestHarness.CheckSemanticError("循环外 continue", unit5.Diagnostics,
                "'continue' outside of a loop");

            // do-while 内 break
            var (unit6, bodies6) = BindUnit(
                "func f(a: i32) {\n" +
                "    do {\n" +
                "        break\n" +
                "    } while (a > 0)\n" +
                "}\n");
            CheckNoErrors("无诊断（do-while 内 break）", unit6);
            TestHarness.Check("do-while 内 break 形态", BoundDescribe.Body(BodyOf(bodies6, "f")),
                "Body(f, [], [Loop(do-while, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Break])])");

            // 值块内 break 穿透（GuaranteesValueReturn 视其为路径终止）：
            // 纯穿透分支 + 产值分支的 if 表达式在循环内合法
            var (unit7, bodies7) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = 0\n" +
                "    while (x > 0) {\n" +
                "        r = if ((x == 5)) { break } else { return@_ 1 }\n" +
                "        x = x - 1\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（值块内 break 穿透）", unit7);
            TestHarness.Check("值块内 break 穿透形态", BoundDescribe.Body(BodyOf(bodies7, "f")),
                "Body(f, [r: i32], [Decl(r, i32, = Int(0,i32)); " +
                "Loop(while, Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(r,i32), " +
                "IfExpr(Binary(CmpEq, Param(x,i32), Int(5,i32), bool), " +
                "ValueBlock(_, -, [Break]), " +
                "ValueBlock(_, i32, [ReturnValue(_, Int(1,i32))]), i32)); " +
                "Assign(Param(x,i32), Binary(Sub, Param(x,i32), Int(1,i32), i32))]); " +
                "Return(Local(r,i32))])");
            // 结构性事实：穿透 break 命中值块外的循环
            var outerWhile = (BoundLoop)BodyOf(bodies7, "f").Body.Statements[1];
            var assign = (BoundAssignmentStatement)outerWhile.Body.Statements[0];
            var ifExpr = (BoundIfExpression)assign.Value;
            TestHarness.CheckTrue("值块内 break 引用命中外层循环",
                ReferenceEquals(((BoundLoopControl)
                    ifExpr.TrueBranch.Block.Statements[0]).Target, outerWhile));

            // return@ 隔循环边界拦截（S7c 技术债：脱糖无法表达跳出中间循环）
            var (unit8, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) {\n" +
                "        while (x > 1) { return@_ 1 }\n" +
                "        return@_ 2\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("return@ 隔循环边界拦截", unit8.Diagnostics,
                "across a loop boundary not supported yet (S7c)");
        }

        // ===== for 双形态（S7c-2：范围循环/for-each 协议，带 stdlib）=====
        private static void TestForLoops()
        {
            TestHarness.Section("P3 For Loops");

            // 范围循环：EnumerateInRange operator 调用 + 协议判定
            var (unit, bodies) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    for (i in 0 to 5) {\n" +
                "        sum = sum + i\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            CheckNoErrors("无诊断（范围循环）", unit);
            TestHarness.Check("范围循环绑定形态", BoundDescribe.Body(BodyOf(bodies, "main")),
                "Body(main, [sum: i32, i: i32], [Decl(sum, i32, = Int(0,i32)); " +
                "For(i, InstCall(EnumerateInRange, Int(0,i32), [Int(5,i32)], IEnumerable<i32>), " +
                "[Assign(Local(sum,i32), Binary(Add, Local(sum,i32), Local(i,i32), i32))]); " +
                "Return(Local(sum,i32))])");
            // 结构性事实：Iterable 是 operator 实例调用；循环变量 const i32；
            // 协议三方法挂好（接口方法符号引用）
            var rangeLoop = (BoundLoop)BodyOf(bodies, "main").Body.Statements[1];
            var iterable = rangeLoop.Iterable as BoundInstanceCallExpression;
            TestHarness.CheckTrue("Iterable = EnumerateInRange operator 调用",
                iterable != null && iterable.Method.Kind == MethodKind.Operator
                && iterable.Method.Name == "EnumerateInRange");
            TestHarness.CheckTrue("循环变量 const i32",
                rangeLoop.LoopVariable != null && rangeLoop.LoopVariable.IsConst
                && ReferenceEquals(rangeLoop.LoopVariable.Type,
                    unit.Symbols.Bootstrap.Int32));
            var collectionsNs = unit.Symbols.GlobalNamespace.ChildNamespaces
                .Single(n => n.Name == "core").ChildNamespaces
                .Single(n => n.Name == "collections");
            var enumerableDef = collectionsNs.Types.Single(t => t.Name == "IEnumerable");
            var enumeratorDef = collectionsNs.Types.Single(t => t.Name == "IEnumerator");
            TestHarness.CheckTrue("协议三方法符号引用（接口成员）",
                ReferenceEquals(rangeLoop.IterateMethod,
                    enumerableDef.Methods.Single(m => m.Name == "iterate"))
                && ReferenceEquals(rangeLoop.MoveNextMethod,
                    enumeratorDef.Methods.Single(m => m.Name == "moveNext"))
                && ReferenceEquals(rangeLoop.CurrentMethod,
                    enumeratorDef.Methods.Single(m => m.Name == "current")));

            // for-each：集合类型实现 IEnumerable<i32>（stdlib RangeI32）
            var (unit2, bodies2) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    var r = new core.collections.RangeI32(0, 3)\n" +
                "    for (x in r) {\n" +
                "        sum = sum + x\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            CheckNoErrors("无诊断（for-each）", unit2);
            TestHarness.Check("for-each 绑定形态", BoundDescribe.Body(BodyOf(bodies2, "main")),
                "Body(main, [sum: i32, r: RangeI32, x: i32], [Decl(sum, i32, = Int(0,i32)); " +
                "Decl(r, RangeI32, = New(RangeI32, init, [Int(0,i32), Int(3,i32)])); " +
                "For(x, Local(r,RangeI32), " +
                "[Assign(Local(sum,i32), Binary(Add, Local(sum,i32), Local(x,i32), i32))]); " +
                "Return(Local(sum,i32))])");

            // 诊断：未实现 IEnumerable 的类型
            var (unit3, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    for (x in 42) { }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("迭代源未实现 IEnumerable", unit3.Diagnostics,
                "does not implement core.collections.IEnumerable<T>");

            // 诊断：范围两端类型不一致
            var (unit4, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    for (i in 0 to \"s\") { }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("范围两端类型不一致", unit4.Diagnostics,
                "Range bounds must have the same type (got 'i32' and 'String')");

            // 诊断：循环变量 const 写入
            var (unit5, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    for (i in 0 to 3) {\n" +
                "        i = 5\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("循环变量 const 写入拒绝", unit5.Diagnostics,
                "Cannot assign to const 'i'");

            // DA：for 后 = before（体可能零次执行）
            var (unit6, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    var x: i32\n" +
                "    for (i in 0 to 3) {\n" +
                "        x = 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("DA：for 后仍报未赋值", unit6.Diagnostics,
                "Use of unassigned local variable 'x'");
        }
    }
}
