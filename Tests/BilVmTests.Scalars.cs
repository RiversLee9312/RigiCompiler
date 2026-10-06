using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // Scalars 职责；与主文件共享同一类型、字段及生命周期。

        internal static void TestHelloWorld()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"Hello, world!\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("hello", result);
            CaseAssertions.Check("hello stdout", result.Stdout, "Hello, world!\n");
            CheckI32("hello 返回值", result, 0);
        }

        private static void TestLocalArithmetic()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    x = x * 3\n" +
                "    return x\n" +
                "}\n");
            CheckOk("算术", result);
            CheckI32("1+2 再 *3", result, 9);
        }

        private static void TestUnaryNegation()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 5\n" +
                "    var n: i32 = -a\n" +
                "    var b: bool = n == 5\n" +
                "    return n\n" +
                "}\n");
            CheckOk("一元", result);
            CheckI32("-5", result, -5);
        }

        // 负号折叠端到端（SYNTAX §3.3）：负号并入整数字面量，各符号类型
        // 下界可书写并经 BIL 标量资源文本（负文本）装载运行
        private static void TestNegativeLiteralFolding()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    return -2147483648\n" +
                "}\n");
            CheckOk("i32 下界折叠", result);
            CheckI32("return -2147483648", result, -2147483648);

            var i8 = Run(
                "pub func main(): i32 {\n" +
                "    const a: i8 = -128B\n" +
                "    if (a == -128B) { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("i8 下界折叠", i8);
            CheckI32("-128B 变量初始化与比较", i8, 1);
        }

        private static void TestInvokeWithResult()
        {
            var result = Run(
                "pub func double(a: i32): i32 { return a * 2 }\n" +
                "pub func main(): i32 {\n" +
                "    double(5)\n" +
                "    return double(21)\n" +
                "}\n");
            CheckOk("invoke", result);
            CheckI32("double(21)", result, 42);
        }

        private static void TestStringConcat()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"Hello, \" + \"world!\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("字符串拼接", result);
            CaseAssertions.Check("拼接 stdout", result.Stdout, "Hello, world!\n");
            CheckI32("拼接返回值", result, 0);
        }

        private static void TestScalarLocals()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var b: bool = true\n" +
                "    var d: double = 0.5\n" +
                "    var f: float = 0.1f\n" +
                "    var c: char = 'A'\n" +
                "    var n: i32 = 10\n" +
                "    n = n + 22\n" +
                "    return n\n" +
                "}\n");
            CheckOk("标量局部", result);
            CheckI32("标量局部返回值", result, 32);
        }

    }
}
