using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // Lambda 职责；与主文件共享同一类型、字段及生命周期。

        // lambda Method wrapper VM 端到端：specific 环绕 + 改返回值；状态
        // 持久；捕获 lambda 共存；双 Method wrapper 顺序；init 实参形态
        //（实参为外层局部，验证透传）。
        private static void TestLambdaMethodWrapperEndToEnd()
        {
            var surround = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"before\")\n" +
                "        var r = inner(x)\n" +
                "        core.io.Console.println(\"after\")\n" +
                "        return (((r as i32) + 1) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{ @Timed (x: i32): i32 -> (x + 1) }\n" +
                "    return f(41)\n" +
                "}\n");
            CheckOk("lambda Method wrapper 环绕", surround);
            CaseAssertions.Check("lambda 环绕 stdout", surround.Stdout,
                "before\n" +
                "after\n");
            CheckI32("lambda 经 wrapper 返回 43", surround, 43);

            var state = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Counted {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        calls = (calls + 1)\n" +
                "        var r = inner(x)\n" +
                "        return (((r as i32) + calls) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{ @Counted (x: i32): i32 -> (x * 10) }\n" +
                "    var a = f(1)\n" +
                "    var b = f(1)\n" +
                "    return ((a * 100) + b)\n" +
                "}\n");
            CheckOk("lambda Method wrapper 状态持久", state);
            CheckI32("lambda 第 1 次 11、第 2 次 12 → 1112", state, 1112);

            var captured = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var base = 40\n" +
                "    var f = func{ @Timed (x: i32): i32 -> (x + base) }\n" +
                "    return f(2)\n" +
                "}\n");
            CheckOk("捕获 lambda + Method wrapper 共存", captured);
            CheckI32("base 40 + x 2 经 wrapper 返回 42", captured, 42);

            var doubleLayer = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"A\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"B\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{ @A @B (x: i32): i32 -> (x + 1) }\n" +
                "    return f(41)\n" +
                "}\n");
            CheckOk("lambda 双 Method wrapper", doubleLayer);
            CaseAssertions.Check("lambda 双 wrapper stdout", doubleLayer.Stdout,
                "A\n" +
                "B\n");
            CheckI32("lambda 双 wrapper 返回 42", doubleLayer, 42);

            var initArg = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Tagged {\n" +
                "    pub var tag: String\n" +
                "    pub init(t: String) { tag = t }\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"tag=\" + tag)\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var tag = \"hi\"\n" +
                "    var f = func{ @Tagged(tag) (x: i32): i32 -> (x + 1) }\n" +
                "    return f(41)\n" +
                "}\n");
            CheckOk("lambda Method wrapper init 实参透传", initArg);
            CaseAssertions.Check("init 实参 stdout", initArg.Stdout, "tag=hi\n");
            CheckI32("init 实参透传后返回 42", initArg, 42);
        }

    }
}
