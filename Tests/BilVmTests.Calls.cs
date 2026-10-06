using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // Calls 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestLambdaInvokeIndirect()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n");
            CheckOk("lambda invoke.indirect", result);
            CheckI32("fn(41)", result, 42);
        }

        // get.wrapper / get.wrapper.indirect：frontend 无整体取值路径，直接构造。
        private static void TestGetWrapperDirectModule()
        {
            var module = WrapperHostModule();
            var main = module.Functions.First(f => f.Symbol == "$main()@Host");
            main.Vars.Add(new BilVarDeclaration("Wrap", "w"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Wrap>", "wid"));
            main.Vars.Add(new BilVarDeclaration(".i32", "lv3"));
            var entry = main.Blocks[0];
            entry.Instructions.RemoveAt(entry.Instructions.Count - 1);
            entry.Instructions.Add(new GetWrapperInstruction(BilOp.Var("h"),
                BilOp.Type("Wrap"), BilOp.Var("w")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Wrap"), BilOp.Var("wid")));
            entry.Instructions.Add(new GetWrapperIndirectInstruction(BilOp.Var("h"),
                BilOp.Var("wid"), BilOp.Var("w")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("w"), BilOp.Var("lv3"),
                BilOp.Field("Wrap#level@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("lv3")));
            var result = BilVm.Run(module);
            CheckOk("get.wrapper", result);
            CheckI32("wrapper.level", result, 9);
        }

        // frontend 尚不发射 new.indirect / get.field.indirect；直接构造测 VM。
        private static void TestIndirectFieldAndNew()
        {
            var module = IndirectBoxModule();
            var result = BilVm.Run(module);
            CheckOk("indirect 字段/构造", result);
            CheckI32("new.indirect + set/get.field.indirect", result, 7);
        }

        // 直构 BIL：add 指令按精确类型派发用户 plus（§22.3）。
        private static void TestUserOperatorAdd()
        {
            var module = VectorPlusModule();
            var result = BilVm.Run(module);
            CheckOk("用户 operator plus", result);
            CheckI32("Vector2 add.x", result, 4);
        }

        // 源码运算符位置：用户 plus / compareTo 四比较 / 一元 / 复合赋值 /
        // and 两侧求值 / 运算结果参与后续表达式
        private static void TestUserOperatorSourceDispatch()
        {
            var plus = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    return ((c.x + 1))\n" +
                "}\n");
            CheckOk("源码 plus", plus);
            CheckI32("1+2 再 +1 = 4", plus, 4);

            var compare = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator compareTo(another: Vec): ComparisonResult {\n" +
                "        if ((x < another.x)) { return .LesserThanAnother }\n" +
                "        if ((x > another.x)) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = new Vec(1)\n" +
                "    var n = 0\n" +
                "    if ((a < b)) { n = (n + 1) }\n" +
                "    if ((a <= c)) { n = (n + 1) }\n" +
                "    if ((b > a)) { n = (n + 1) }\n" +
                "    if ((c >= a)) { n = (n + 1) }\n" +
                "    if ((b < a)) { n = (n + 10) }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("源码 compareTo", compare);
            CheckI32("< <= > >= 四真一假", compare, 4);

            var unary = Run(
                "class Bits {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator opposite(): Bits { return new Bits((0 - v)) }\n" +
                "    pub operator not(): Bits { return new Bits(if ((v == 0)) { return@_ 1 } else { return@_ 0 }) }\n" +
                "    pub operator bitwiseNot(): Bits { return new Bits(!v) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Bits(3)\n" +
                "    var z = new Bits(0)\n" +
                "    var n = 0\n" +
                "    if (((-a).v == (0 - 3))) { n = (n + 1) }\n" +
                "    if (((not z).v == 1)) { n = (n + 1) }\n" +
                "    if (((!a).v == (!3))) { n = (n + 1) }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("源码一元 opposite/not/bitwiseNot", unary);
            CheckI32("三元均命中", unary, 3);

            var compound = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(10)\n" +
                "    var b = new Vec(5)\n" +
                "    a += b\n" +
                "    return a.x\n" +
                "}\n");
            CheckOk("源码复合赋值 +=", compound);
            CheckI32("10 += 5 → 15", compound, 15);

            var bothSides = Run(
                "class Flag {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n) { }\n" +
                "    pub operator and(other: Flag): Flag { return other }\n" +
                "}\n" +
                "var hits: i32\n" +
                "func bump(): Flag {\n" +
                "    hits = (hits + 1)\n" +
                "    return new Flag(1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    hits = 0\n" +
                "    var a = new Flag(0)\n" +
                "    var b = a and bump()\n" +
                "    return (hits + b.n)\n" +
                "}\n");
            CheckOk("用户 and 两侧求值", bothSides);
            CheckI32("hits=1 且取右侧 n=1", bothSides, 2);
        }

        // §22.5 方法 hook core::Any$call???：烘焙归 Middleware，VM 行为参考
        // 即链末默认实现——未路由抛 core::NoSuchMethodException（RUNTIME §14.2）。
        private static void TestDowngradeCallWildcard()
        {
            const string service =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n";
            var thrown = Run(service +
                "pub func main(): i32 {\n" +
                "    var service = new Service()\n" +
                "    service.fetchUserById(42)\n" +
                "    return 0\n" +
                "}\n");
            CaseAssertions.CheckTrue("降级未路由抛 NoSuchMethodException",
                thrown.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("NoSuchMethodException"),
                thrown.Exception?.ToString() ?? "<null>");
            CaseAssertions.CheckTrue("异常消息带请求 symbol",
                thrown.Exception?.Message.Contains("Service$fetchUserById") == true,
                thrown.Exception?.Message ?? "<null>");
            var caught = Run(service +
                "pub func main(): i32 {\n" +
                "    var service = new Service()\n" +
                "    try {\n" +
                "        service.fetchUserById(42)\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        return 7\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("降级异常可 catch", caught);
            CheckI32("catch 返回 7", caught, 7);
        }

    }
}
