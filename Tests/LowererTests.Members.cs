using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler.Tests
{
    // LowererTests 成员访问组：实例成员恒等降级（this/实例调用/实例字段）
    // + 索引访问（读/写/复合三形态共用 LoweredIndexExpression）。

    public static partial class LowererTests
    {
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
            CaseAssertions.Check("裸名实例字段降级（this 隐式）",
                LoweredDescribe.Body(BodyOf(lowered, "add")),
                "Body(add, [], [Return(Binary(Add, " +
                "InstField(value, This(Counter), i32), Param(n,i32), i32))])");
            CaseAssertions.Check("实例调用降级",
                LoweredDescribe.Body(BodyOf(lowered, "call")),
                "Body(call, [], [Return(InstCall(add, Param(c,Counter), [Int(2,i32)], i32))])");
            CaseAssertions.Check("实例字段访问降级",
                LoweredDescribe.Body(BodyOf(lowered, "read")),
                "Body(read, [], [Return(InstField(value, Param(c,Counter), i32))])");

            // 结构性事实：InstCall 的 Method 符号引用相等、Type 自带（i32）
            var counterType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Counter");
            var callReturn = (LoweredReturnStatement)BodyOf(lowered, "call").Body.Statements[0];
            var instCall = (LoweredInstanceCallExpression)callReturn.Value!;
            CaseAssertions.CheckTrue("实例调用方法符号引用相等 + Type 自带",
                ReferenceEquals(instCall.Method,
                    counterType.Methods.Single(m => m.Name == "add"))
                && ReferenceEquals(instCall.Type, unit.Symbols.Bootstrap.Int32));
        }

        // ===== S8c：索引访问恒等降级（读/写共用 LoweredIndexExpression）=====
        private static void TestIndexLowering()
        {
            var (unit, bound, lowered) = LowerUnit(
                "class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "func read(b: Bag, i: i32): i32? { return b[i] }\n" +
                "func write(b: Bag) { b[0] = 42 }\n" +
                "func bump(b: Bag) { b[1] = ((b[1] if? 0) + 2) }\n");
            CheckNoErrors("无诊断（索引降级）", unit);
            CaseAssertions.Check("索引读降级（Q6：Type = i32?）",
                LoweredDescribe.Body(BodyOf(lowered, "read")),
                "Body(read, [], [Return(Index(Param(b,Bag), Param(i,i32), i32?))])");
            CaseAssertions.Check("索引写降级",
                LoweredDescribe.Body(BodyOf(lowered, "write")),
                "Body(write, [], [Assign(Index(Param(b,Bag), Int(0,i32), i32), Int(42,i32))])");
            // 索引显式读改写回（Q6 后 a[i] op= 读侧为 T?，由显式形态替代）
            CaseAssertions.Check("索引显式读改写回降级",
                LoweredDescribe.Body(BodyOf(lowered, "bump")),
                "Body(bump, [.s0: i32?, .s1: i32, .b0: .breakid], " +
                "[Assign(Local(.s0,i32?), Index(Param(b,Bag), Int(1,i32), i32?)); " +
                "If(Binary(CmpNe, Local(.s0,i32?), Const(null,i32?), bool), " +
                "[Assign(Local(.s1,i32), Cast(Local(.s0,i32?), i32, i32))], " +
                "[Assign(Local(.s1,i32), Int(0,i32))], .b0); " +
                "Assign(Index(Param(b,Bag), Int(1,i32), i32), " +
                "Binary(Add, Local(.s1,i32), Int(2,i32), i32))])");

            // 结构性事实：Origin 回指引用相等 + Type 透传（读 = getAtIndex
            // 返回类型；写 = setAtIndex 元素形参类型——同型同源此处皆 i32）
            var boundRead = (BoundIndexExpression)((BoundReturnStatement)
                bound.Single(b => b.Method.Name == "read").Body.Statements[0]).Value!;
            var loweredRead = (LoweredIndexExpression)((LoweredReturnStatement)
                BodyOf(lowered, "read").Body.Statements[0]).Value!;
            CaseAssertions.CheckTrue("索引读 Origin 回指 + Type 透传",
                ReferenceEquals(loweredRead.Origin, boundRead)
                && ReferenceEquals(loweredRead.Type, boundRead.Type));
            var boundWrite = (BoundIndexExpression)((BoundAssignmentStatement)
                bound.Single(b => b.Method.Name == "write").Body.Statements[0]).Target;
            var loweredWrite = (LoweredIndexExpression)((LoweredAssignmentStatement)
                BodyOf(lowered, "write").Body.Statements[0]).Target;
            CaseAssertions.CheckTrue("索引写 Origin 回指 + 目标形态共用",
                ReferenceEquals(loweredWrite.Origin, boundWrite)
                && ReferenceEquals(loweredWrite.Type, boundWrite.Type));
        }
    }
}
