using System.Linq;

namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== 循环（S7c-1：while/do-while 形态、条件 bool、DA 规则、for 拦截）=====
        private static void TestLoops()
        {
            CompilerTestTools.Section("P3 Loops");

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
            CaseAssertions.Check("while 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "Loop(while, Binary(CmpLt, Local(x,i32), Param(a,i32), bool), " +
                "[Assign(Local(x,i32), Binary(Add, Local(x,i32), Int(1,i32), i32))])])");
            CaseAssertions.Check("do-while 绑定形态", BoundDescribe.Body(BodyOf(bodies, "g")),
                "Body(g, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "Loop(do-while, Binary(CmpLt, Local(x,i32), Param(a,i32), bool), " +
                "[Assign(Local(x,i32), Binary(Add, Local(x,i32), Int(1,i32), i32))])])");

            var (unit2, _) = BindUnit("func f(x: i32) { while (x) { } }\n");
            CaseAssertions.CheckSemanticError("while 条件非 bool", unit2.Diagnostics,
                "loop condition must be bool (got 'i32')");

            var (unit3, _) = BindUnit("func f(x: i32) { do { } while (x) }\n");
            CaseAssertions.CheckSemanticError("do-while 条件非 bool", unit3.Diagnostics,
                "loop condition must be bool (got 'i32')");

            // stdlib 缺席（BindUnit 不带 stdlib）：范围循环先报 operator 缺失
            var (unit4, _) = BindUnit("func f() { for (i in 0 to 3) { } }\n");
            CaseAssertions.CheckSemanticError("stdlib 缺席（无 EnumerateInRange operator）",
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
            CaseAssertions.CheckSemanticError("DA：while 后仍报未赋值", unit5.Diagnostics,
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
            CaseAssertions.Check("do-while 赋值生效形态", BoundDescribe.Body(BodyOf(bodies6, "f")),
                "Body(f, [x: i32], [Decl(x, i32); " +
                "Loop(do-while, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Assign(Local(x,i32), Int(1,i32))]); Return(Local(x,i32))])");

            // GuaranteesReturn：循环保守 false（while (true) 特例留口）
            var (unit7, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    while (a > 0) { return 1 }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("循环内 return 不保证返回（保守）", unit7.Diagnostics,
                "must return a value on all code paths");

            // DA：do-while 体内 break 跳过赋值——出环点与体尾取交
            var (unit8, _) = BindUnit(
                "func f(b: bool): i32 {\n" +
                "    var x: i32\n" +
                "    do {\n" +
                "        if (b) { break }\n" +
                "        x = 1\n" +
                "    } while (false)\n" +
                "    return x\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("DA：do-while break 跳过赋值", unit8.Diagnostics,
                "Use of unassigned local variable 'x'");

            // DA：do-while 赋值后再 break——出环点已赋值
            var (unit9, _) = BindUnit(
                "func f(b: bool): i32 {\n" +
                "    var x: i32\n" +
                "    do {\n" +
                "        x = 1\n" +
                "        if (b) { break }\n" +
                "    } while (false)\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("DA：do-while 赋值后 break 计入", unit9);

            // DA：named break 穿透外层，内层体尾不计入外层出口
            var (unit10, _) = BindUnit(
                "func f(b: bool): i32 {\n" +
                "    var x: i32\n" +
                "    do named outer {\n" +
                "        do {\n" +
                "            if (b) { break@outer }\n" +
                "            x = 1\n" +
                "        } while (false)\n" +
                "    } while (false)\n" +
                "    return x\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("DA：named break 跨层不计入内层体尾", unit10.Diagnostics,
                "Use of unassigned local variable 'x'");

            // DA：finally 叠到 break 出环点
            var (unit11, _) = BindUnitWithStdlib(
                "func f(b: bool): i32 {\n" +
                "    var x: i32\n" +
                "    do {\n" +
                "        try {\n" +
                "            if (b) { break }\n" +
                "        } finally(f) {\n" +
                "            x = 1\n" +
                "        }\n" +
                "    } while (false)\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("DA：do-while break 路径叠 finally 赋值", unit11);
        }

        // ===== break/continue（S7c-1：标签栈解析、穿透值块、诊断）=====
        private static void TestLoopControl()
        {
            CompilerTestTools.Section("P3 Loop Control");

            var (unit, bodies) = BindUnit(
                "func f(a: i32) {\n" +
                "    while (a > 0) {\n" +
                "        if (a == 5) { break }\n" +
                "        if (a == 2) { continue }\n" +
                "        a = a - 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（break/continue）", unit);
            CaseAssertions.Check("无标签 break/continue 形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Loop(while, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[If(Binary(CmpEq, Param(a,i32), Int(5,i32), bool), [Break]); " +
                "If(Binary(CmpEq, Param(a,i32), Int(2,i32), bool), [Continue]); " +
                "Assign(Param(a,i32), Binary(Sub, Param(a,i32), Int(1,i32), i32))])])");
            // 结构性事实：无标签命中最内层循环（引用相等）
            var loop = (BoundLoop)BodyOf(bodies, "f").Body.Statements[0];
            var breakStmt = (BoundLoopControl)((BoundIfStatement)
                loop.Body.Statements[0]).TrueBlock.Statements[0];
            CaseAssertions.CheckTrue("break 引用命中目标循环",
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
            CaseAssertions.Check("嵌套标签循环形态", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Loop(while@outer, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Loop(while, Binary(CmpGt, Param(a,i32), Int(1,i32), bool), " +
                "[Break@outer; Continue]); " +
                "Assign(Param(a,i32), Binary(Sub, Param(a,i32), Int(1,i32), i32))])])");
            // 结构性事实：break@outer 命中外层、无标签 continue 命中内层
            var outerLoop = (BoundLoop)BodyOf(bodies2, "f").Body.Statements[0];
            var innerLoop = (BoundLoop)outerLoop.Body.Statements[0];
            CaseAssertions.CheckTrue("break@outer 引用命中外层循环",
                ReferenceEquals(((BoundLoopControl)innerLoop.Body.Statements[0]).Target, outerLoop));
            CaseAssertions.CheckTrue("无标签 continue 引用命中内层循环",
                ReferenceEquals(((BoundLoopControl)innerLoop.Body.Statements[1]).Target, innerLoop));

            var (unit3, _) = BindUnit("func f() { while (true) { break@nope } }\n");
            CaseAssertions.CheckSemanticError("未定义循环标签", unit3.Diagnostics,
                "Undefined loop label: 'nope'");

            var (unit4, _) = BindUnit("func f() { break }\n");
            CaseAssertions.CheckSemanticError("循环外 break", unit4.Diagnostics,
                "'break' outside of a loop");

            var (unit5, _) = BindUnit("func f() { continue }\n");
            CaseAssertions.CheckSemanticError("循环外 continue", unit5.Diagnostics,
                "'continue' outside of a loop");

            // do-while 内 break
            var (unit6, bodies6) = BindUnit(
                "func f(a: i32) {\n" +
                "    do {\n" +
                "        break\n" +
                "    } while (a > 0)\n" +
                "}\n");
            CheckNoErrors("无诊断（do-while 内 break）", unit6);
            CaseAssertions.Check("do-while 内 break 形态", BoundDescribe.Body(BodyOf(bodies6, "f")),
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
            CaseAssertions.Check("值块内 break 穿透形态", BoundDescribe.Body(BodyOf(bodies7, "f")),
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
            CaseAssertions.CheckTrue("值块内 break 引用命中外层循环",
                ReferenceEquals(((BoundLoopControl)
                    ifExpr.TrueBranch.Block.Statements[0]).Target, outerWhile));

            // return@ 值块目标跨循环：StructuredExitRouting 展开，P3 放行
            var (unit8, bodies8) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) {\n" +
                "        while (x > 1) { return@_ 1 }\n" +
                "        return@_ 2\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（return@ 值块目标跨循环）", unit8);
            CaseAssertions.Check("return@ 跨循环绑定形态", BoundDescribe.Body(BodyOf(bodies8, "f")),
                "Body(f, [], [Return(IfExpr(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "ValueBlock(_, i32, [Loop(while, Binary(CmpGt, Param(x,i32), Int(1,i32), bool), " +
                "[ReturnValue(_, Int(1,i32))]); ReturnValue(_, Int(2,i32))]), " +
                "ValueBlock(_, i32, [ReturnValue(_, Int(0,i32))]), i32))])");
            var trueBranch = ((BoundIfExpression)((BoundReturnStatement)
                BodyOf(bodies8, "f").Body.Statements[0]).Value!).TrueBranch;
            var whileInBranch = (BoundLoop)trueBranch.Block.Statements[0];
            CaseAssertions.CheckTrue("循环体内 return@ 命中外层值块",
                ReferenceEquals(((BoundReturnValueStatement)
                    whileInBranch.Body.Statements[0]).Target, trueBranch));

            // do-while 至少一次：体保证产值即可作为值块唯一产值路径
            var (unit9, bodies9) = BindUnit(
                "func g(): i32 {\n" +
                "    return seq {\n" +
                "        do { return@_ 7 } while (false)\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（do-while 体保证产值）", unit9);
            CaseAssertions.Check("do-while 唯一产值路径", BoundDescribe.Body(BodyOf(bodies9, "g")),
                "Body(g, [], [Return(SeqExpr([], ValueBlock(_, i32, " +
                "[Loop(do-while, Bool(False,bool), [ReturnValue(_, Int(7,i32))])])))])");

            // 循环体内 return@ 类型参与外层统一（EnumerateStatements 下钻）
            var (unit11, _) = BindUnit(
                "func k(x: i32): i32 {\n" +
                "    return if (x > 0) {\n" +
                "        while (x > 1) { return@_ \"a\" }\n" +
                "        return@_ 2\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("循环体内 return@ 类型不一致", unit11.Diagnostics,
                "produces different types");

            // while 可零次执行：末语句是 while 仍不保证（保守）
            var (unit10, _) = BindUnit(
                "func h(): i32 {\n" +
                "    return seq {\n" +
                "        while (true) { return@_ 1 }\n" +
                "    }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("while 末语句不保证产值（保守）", unit10.Diagnostics,
                "must explicitly return@ a value");
        }

        // ===== for 双形态（S7c-2：范围循环/for-each 协议，带 stdlib）=====
        private static void TestForLoops()
        {
            CompilerTestTools.Section("P3 For Loops");

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
            CaseAssertions.Check("范围循环绑定形态", BoundDescribe.Body(BodyOf(bodies, "main")),
                "Body(main, [sum: i32, i: i32], [Decl(sum, i32, = Int(0,i32)); " +
                "For(i, InstCall(EnumerateInRange, Int(0,i32), [Int(5,i32)], IEnumerable<i32>), " +
                "[Assign(Local(sum,i32), Binary(Add, Local(sum,i32), Local(i,i32), i32))]); " +
                "Return(Local(sum,i32))])");
            // 结构性事实：Iterable 是 operator 实例调用；循环变量 const i32；
            // 协议三方法挂好（接口方法符号引用）
            var rangeLoop = (BoundLoop)BodyOf(bodies, "main").Body.Statements[1];
            var iterable = rangeLoop.Iterable as BoundInstanceCallExpression;
            CaseAssertions.CheckTrue("Iterable = EnumerateInRange operator 调用",
                iterable != null && iterable.Method.Kind == MethodKind.Operator
                && iterable.Method.Name == "EnumerateInRange");
            CaseAssertions.CheckTrue("循环变量 const i32",
                rangeLoop.LoopVariable != null && rangeLoop.LoopVariable.IsConst
                && ReferenceEquals(rangeLoop.LoopVariable.Type,
                    unit.Symbols.Bootstrap.Int32));
            var collectionsNs = unit.Symbols.GlobalNamespace.ChildNamespaces
                .Single(n => n.Name == "core").ChildNamespaces
                .Single(n => n.Name == "collections");
            var enumerableDef = collectionsNs.Types.Single(t => t.Name == "IEnumerable");
            var enumeratorDef = collectionsNs.Types.Single(t => t.Name == "IEnumerator");
            CaseAssertions.CheckTrue("协议三方法符号引用（接口成员）",
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
            CaseAssertions.Check("for-each 绑定形态", BoundDescribe.Body(BodyOf(bodies2, "main")),
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
            CaseAssertions.CheckSemanticError("迭代源未实现 IEnumerable", unit3.Diagnostics,
                "does not implement core.collections.IEnumerable<T>");

            // 诊断：右操作数无法绑定到左操作数类型的 EnumerateInRange 形参
            var (unit4, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    for (i in 0 to \"s\") { }\n" +
                "    return 0\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("右操作数不匹配 EnumerateInRange 形参",
                unit4.Diagnostics, "No applicable overload of 'EnumerateInRange'");

            // 诊断：循环变量 const 写入
            var (unit5, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    for (i in 0 to 3) {\n" +
                "        i = 5\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("循环变量 const 写入拒绝", unit5.Diagnostics,
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
            CaseAssertions.CheckSemanticError("DA：for 后仍报未赋值", unit6.Diagnostics,
                "Use of unassigned local variable 'x'");

            // 泛型参数经约束：T extends i32 走 i32 的 EnumerateInRange
            var (unit7, bodies7) = BindUnitWithStdlib(
                "pub func sum\\<T extends i32>(a: T, b: T): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckNoErrors("无诊断（T extends i32 范围循环）", unit7);
            CaseAssertions.Check("T extends i32 范围循环绑定形态",
                BoundDescribe.Body(BodyOf(bodies7, "sum")),
                "Body(sum, [total: i32, i: i32], [Decl(total, i32, = Int(0,i32)); " +
                "For(i, InstCall(EnumerateInRange, Param(a,T), [Param(b,T)], IEnumerable<i32>), " +
                "[Assign(Local(total,i32), Binary(Add, Local(total,i32), Local(i,i32), i32))]); " +
                "Return(Local(total,i32))])");

            // 自定义类型经约束：T extends Step，命中 Step 自己的 operator
            var stepHeader =
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n";
            var (unit8, bodies8) = BindUnitWithStdlib(stepHeader +
                "pub func count\\<T extends Step>(a: T, b: T): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckNoErrors("无诊断（T extends Step 范围循环）", unit8);
            CaseAssertions.Check("T extends Step 范围循环绑定形态",
                BoundDescribe.Body(BodyOf(bodies8, "count")),
                "Body(count, [total: i32, i: i32], [Decl(total, i32, = Int(0,i32)); " +
                "For(i, InstCall(EnumerateInRange, Param(a,T), [Param(b,T)], IEnumerable<i32>), " +
                "[Assign(Local(total,i32), Binary(Add, Local(total,i32), Local(i,i32), i32))]); " +
                "Return(Local(total,i32))])");

            // 直接（非泛型）自定义类型：有 operator 正常绑定
            var (unit9, bodies9) = BindUnitWithStdlib(stepHeader +
                "pub func walk(a: Step, b: Step): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckNoErrors("无诊断（自定义类型范围循环）", unit9);
            var customLoop = (BoundLoop)BodyOf(bodies9, "walk").Body.Statements[1];
            var customCall = customLoop.Iterable as BoundInstanceCallExpression;
            CaseAssertions.CheckTrue("自定义类型 Iterable = 本类型 EnumerateInRange",
                customCall != null && customCall.Method.Kind == MethodKind.Operator
                && customCall.Method.Name == "EnumerateInRange"
                && customCall.Method.Owner is TypeSymbol owner && owner.Name == "Step");

            // 负例：有 operator 但返回类型不是 IEnumerable
            var (unit10, _) = BindUnitWithStdlib(
                "pub class BadRet {\n" +
                "    pub operator EnumerateInRange(end: BadRet): i32 { return 0 }\n" +
                "}\n" +
                "pub func main(a: BadRet, b: BadRet) {\n" +
                "    for (i in a to b) { }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("返回类型不是 IEnumerable", unit10.Diagnostics,
                "does not implement core.collections.IEnumerable<T>");

            // 负例：无 operator
            var (unit11, _) = BindUnitWithStdlib(
                "pub class NoOp { }\n" +
                "pub func main(a: NoOp, b: NoOp) {\n" +
                "    for (i in a to b) { }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("自定义类型无 EnumerateInRange", unit11.Diagnostics,
                "has no EnumerateInRange operator");

            // 形参为 i32：两端不同型合法（此前类型洞场景，现因形参匹配而合法）
            var stepToI32Header =
                "pub open class StepI32 {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: i32): " +
                "core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, end)\n" +
                "    }\n" +
                "}\n";
            var (unit12, bodies12) = BindUnitWithStdlib(stepToI32Header +
                "pub func walk(a: StepI32, b: i32): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckNoErrors("无诊断（Step to i32，形参为 i32）", unit12);
            var stepI32Loop = (BoundLoop)BodyOf(bodies12, "walk").Body.Statements[1];
            var stepI32Call = stepI32Loop.Iterable as BoundInstanceCallExpression;
            CaseAssertions.CheckTrue("Step to i32 命中本类型 EnumerateInRange",
                stepI32Call != null && stepI32Call.Method.Name == "EnumerateInRange"
                && stepI32Call.Method.Parameters[0].Type is TypeSymbol pI32
                && pI32.Name == "i32");

            // 形参为 Step：b: i32 不可赋
            var (unit13, _) = BindUnitWithStdlib(stepHeader +
                "pub func walk(a: Step): i32 {\n" +
                "    for (i in a to 5) { }\n" +
                "    return 0\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("Step 形参为 Step 时 b: i32 拒绝",
                unit13.Diagnostics, "No applicable overload of 'EnumerateInRange'");

            // 重载选择：end: i32 / end: Step 各选对
            var overloadHeader =
                "pub open class StepOv {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: i32): " +
                "core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, end)\n" +
                "    }\n" +
                "    pub operator EnumerateInRange(end: StepOv): " +
                "core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n";
            var (unit14, bodies14) = BindUnitWithStdlib(overloadHeader +
                "pub func byI32(a: StepOv): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to 5) { total = (total + i) }\n" +
                "    return total\n" +
                "}\n" +
                "pub func byStep(a: StepOv, b: StepOv): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) { total = (total + i) }\n" +
                "    return total\n" +
                "}\n");
            CheckNoErrors("无诊断（EnumerateInRange 重载选择）", unit14);
            var ovI32 = ((BoundLoop)BodyOf(bodies14, "byI32").Body.Statements[1])
                .Iterable as BoundInstanceCallExpression;
            var ovStep = ((BoundLoop)BodyOf(bodies14, "byStep").Body.Statements[1])
                .Iterable as BoundInstanceCallExpression;
            CaseAssertions.CheckTrue("step to 5 选 end: i32",
                ovI32 != null && ovI32.Method.Parameters[0].Type is TypeSymbol ovP0
                && ovP0.Name == "i32");
            CaseAssertions.CheckTrue("step to otherStep 选 end: StepOv",
                ovStep != null && ovStep.Method.Parameters[0].Type is TypeSymbol ovP1
                && ovP1.Name == "StepOv");

            // 无可匹配重载
            var (unit15, _) = BindUnitWithStdlib(overloadHeader +
                "pub func bad(a: StepOv) {\n" +
                "    for (i in a to \"s\") { }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("无可匹配 EnumerateInRange 重载",
                unit15.Diagnostics, "No applicable overload of 'EnumerateInRange'");

            // 二义：两端接口均可赋且互不更具体
            var (unit16, _) = BindUnitWithStdlib(
                "pub interface ILeft { }\n" +
                "pub interface IRight { }\n" +
                "pub class Both implements ILeft, IRight { }\n" +
                "pub class StepAmb {\n" +
                "    pub operator EnumerateInRange(end: ILeft): " +
                "core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(0, 1)\n" +
                "    }\n" +
                "    pub operator EnumerateInRange(end: IRight): " +
                "core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(0, 1)\n" +
                "    }\n" +
                "}\n" +
                "pub func walk(a: StepAmb, b: Both) {\n" +
                "    for (i in a to b) { }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("EnumerateInRange 重载二义",
                unit16.Diagnostics, "Call to 'EnumerateInRange' is ambiguous");

            // 泛型 operator：约束满足 / 违反 / 推断失败
            var genericOpHeader =
                "pub interface Marker { }\n" +
                "pub class Good implements Marker { }\n" +
                "pub class StepGen {\n" +
                "    pub operator EnumerateInRange\\<U extends Marker>(end: U): " +
                "core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(0, 1)\n" +
                "    }\n" +
                "}\n";
            var (unit17, bodies17) = BindUnitWithStdlib(genericOpHeader +
                "pub func ok(a: StepGen, b: Good) {\n" +
                "    for (i in a to b) { }\n" +
                "}\n");
            CheckNoErrors("无诊断（泛型 EnumerateInRange 约束满足）", unit17);
            var genCall = ((BoundLoop)BodyOf(bodies17, "ok").Body.Statements[0])
                .Iterable as BoundInstanceCallExpression;
            CaseAssertions.CheckTrue("泛型 EnumerateInRange 推断 U=Good",
                genCall != null && genCall.TypeArguments.Count == 1
                && genCall.TypeArguments[0] is TypeSymbol genU
                && genU.Name == "Good");

            var (unit18, _) = BindUnitWithStdlib(genericOpHeader +
                "pub func bad(a: StepGen) {\n" +
                "    for (i in a to 5) { }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("泛型 EnumerateInRange 违反约束",
                unit18.Diagnostics,
                "does not satisfy the 'Extends Marker' constraint");

            var (unit19, _) = BindUnitWithStdlib(
                "pub class StepInf {\n" +
                "    pub operator EnumerateInRange\\<U>(end: Array\\<U>): " +
                "core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(0, 1)\n" +
                "    }\n" +
                "}\n" +
                "pub func bad(a: StepInf) {\n" +
                "    for (i in a to 5) { }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("泛型 EnumerateInRange 推断失败",
                unit19.Diagnostics, "cannot infer type arguments from the given arguments");

            // T extends B，B 的形参为基类型、b 为 T（可赋）
            var (unit20, bodies20) = BindUnitWithStdlib(
                "pub open class BaseBound {\n" +
                "    pub operator EnumerateInRange(end: BaseBound): " +
                "core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(0, 1)\n" +
                "    }\n" +
                "}\n" +
                "pub func walk\\<T extends BaseBound>(a: T, b: T): i32 {\n" +
                "    var n = 0\n" +
                "    for (i in a to b) { n = (n + i) }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（T extends B，形参为 B，b: T 可赋）", unit20);
            CaseAssertions.Check("T extends B 范围循环绑定形态",
                BoundDescribe.Body(BodyOf(bodies20, "walk")),
                "Body(walk, [n: i32, i: i32], [Decl(n, i32, = Int(0,i32)); " +
                "For(i, InstCall(EnumerateInRange, Param(a,T), [Param(b,T)], IEnumerable<i32>), " +
                "[Assign(Local(n,i32), Binary(Add, Local(n,i32), Local(i,i32), i32))]); " +
                "Return(Local(n,i32))])");
        }
    }
}
