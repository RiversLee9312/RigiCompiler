using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // ValueWrappers 职责；与主文件共享同一类型、字段及生命周期。

        // Method wrapper wildcard 全形状转发：.name 显式传入 inner，VM 消费它
        // 重路由下一环；.name 运行时值 = 完整 BIL 方法符号。
        private static void TestMethodWrapperWildcardInnerFullShape()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(\"name=\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(41)\n" +
                "}\n");
            CheckOk("Method wrapper wildcard 全形状 inner", result);
            CheckI32("普通方法经 wildcard 返回 42", result, 42);
            CaseAssertions.CheckTrue(".name = 完整 BIL 方法符号",
                result.Stdout.Contains("name=Service$fetch(x:.i32)@.i32") == true,
                result.Stdout);
        }

        // lambda 头 Method wrapper wildcard：.name 诚实填 $$call 合成符号。
        private static void TestLambdaMethodWrapperWildcardInner()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(\"name=\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var fn = func{ @Timed (x: i32): i32 -> (x + 1) }\n" +
                "    return fn(41)\n" +
                "}\n");
            CheckOk("lambda Method wrapper wildcard 全形状 inner", result);
            CheckI32("lambda 经 wildcard 返回 42", result, 42);
            CaseAssertions.CheckTrue(".name = $$call 合成符号",
                result.Stdout.Contains("name=..lambda..")
                && result.Stdout.Contains("$$call(x:.i32)@.i32"),
                result.Stdout);
        }

        // 双 Entity wrapper：外层 specific，内层 wildcard。wildcard 环 inner 全形状
        // 透传，VM 按传入 symbol 重路由到链末原始方法。
        private static void TestWildcardInnerMiddleOfWrapperChain()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WOuter {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 { return inner(x) }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WInner {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"inner:\" + symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WOuter\n" +
                "@WInner\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n");
            CheckOk("wildcard 在双 wrapper 链中间", result);
            CheckI32("双链 wildcard 透传返回 42", result, 42);
            CaseAssertions.CheckTrue("wildcard 环收到正确 symbol",
                result.Stdout.Contains("inner:Service$ping(x:.i32)@.i32") == true,
                result.Stdout);
        }

        // Value wrapper 最原始的用户场景回归：init 实参透传 + get proxy
        // 每次先计数、状态原地持久。init 实参 a=10 落到 step；const b 初值
        // 0 三次读取经 .proxy.get 链依次得到 10/20/30；最后 count==3。
        private static void TestWrapperValueGetProxyInitArg()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper WrapperA {\n" +
                "    pub var count: i32\n" +
                "    pub var step: i32\n" +
                "    pub init(s: i32) {\n" +
                "        count = 0\n" +
                "        step = s\n" +
                "    }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        count = (count + 1)\n" +
                "        return (((value as i32) + (count * step)) as TValue)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = 10\n" +
                "    @WrapperA(a)\n" +
                "    const b = 0\n" +
                "    var r1 = b\n" +
                "    var r2 = b\n" +
                "    var r3 = b\n" +
                "    var ok1 = ((b:WrapperA.step == 10) and (r1 == 10))\n" +
                "    var ok2 = (((ok1 and (r2 == 20)) and (r3 == 30))" +
                " and (b:WrapperA.count == 3))\n" +
                "    if (ok2) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    } else {\n" +
                "        core.io.Console.println(\"FAIL\")\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            CheckOk("WrapperA init 实参透传 + get proxy 状态持久", result);
            CaseAssertions.Check("WrapperA stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("WrapperA main 返回 0", result, 0);
        }

        // Value 派发 a)：同时实现 get/set 的 Clamped 风格 wrapper 修饰可变
        // var——写 200 经 set 链夹到 100、写 -20 经 set 链夹到 0（inner 写回）。
        private static void TestValueWrapperClampedMutableVar()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init() {\n" +
                "        min = 0\n" +
                "        max = 100\n" +
                "    }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        if ((v > max)) { v = max }\n" +
                "        if ((v < min)) { v = min }\n" +
                "        inner((v as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    health = 200\n" +
                "    var a = health\n" +
                "    health = -20\n" +
                "    var b = health\n" +
                "    if (((a == 100) and (b == 0))) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    } else {\n" +
                "        core.io.Console.println(\"FAIL\")\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            CheckOk("Clamped 局部 var 写夹取", result);
            CaseAssertions.Check("Clamped stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Clamped main 返回 0", result, 0);
        }

        // Value 派发 b)：同一局部叠两个 Value wrapper——读序内层先
        //（B.get → A.get）、写序外层先（A.set → B.set）。
        private static void TestValueWrapperTwoLayerOrder()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"A.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"A.set\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"B.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"B.set\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 0\n" +
                "    x = 1\n" +
                "    var r = x\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("双层 Value wrapper 读序内层先/写序外层先", result);
            CaseAssertions.Check("双层 Value wrapper 顺序 stdout", result.Stdout,
                "A.set\n" +
                "B.set\n" +
                "A.set\n" +
                "B.set\n" +
                "B.get\n" +
                "A.get\n");
        }

        // Value 派发 c)：只带 get proxy 的 wrapper 修饰只读 const——读取经
        // proxy 生效。（var + get-only wrapper 自 §14.3 只读适用性检查前移
        // 后是 P3 编译错误，不再到 VM——回归用例在 Binder 套件）
        private static void TestValueWrapperGetOnlyProxy()
        {
            var readOnly = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper ReadOnly {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((value as i32) + 1) as TValue)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @ReadOnly\n" +
                "    const y: i32 = 1\n" +
                "    var v = y\n" +
                "    return v\n" +
                "}\n");
            CheckOk("get-only wrapper const 读经 proxy", readOnly);
            CheckI32("const 初值 1 经 get proxy 返回 2", readOnly, 2);
        }

        // String 插值：i32 插值 + 拼接混合；class 实例插值输出类型名。
        private static void TestStringInterpolationToString()
        {
            var result = Run(
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(a: i32, b: i32) {\n" +
                "        x = a\n" +
                "        y = b\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var n = 42\n" +
                "    var p = new Point(1, 2)\n" +
                "    core.io.Console.println((\"n=${n}\") + \"!\")\n" +
                "    core.io.Console.println(\"p=${p}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("String 插值 i32 与 class 实例", result);
            CaseAssertions.Check("插值 stdout", result.Stdout,
                "n=42!\n" +
                "p=Point\n");
            CheckI32("插值 main 返回 0", result, 0);
        }

    }
}
