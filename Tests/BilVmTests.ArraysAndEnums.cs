using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // ArraysAndEnums 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestStructDeepCopy()
        {
            var result = Run(
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(a: i32, b: i32) {\n" +
                "        x = a\n" +
                "        y = b\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Point(1, 2)\n" +
                "    var q = p\n" +
                "    p.x = 10\n" +
                "    return (q.x + (p.x * 100))\n" +
                "}\n");
            CheckOk("struct 深拷贝", result);
            CheckI32("赋值后互不影响", result, 1001);
        }

        private static void TestArrayIndexOperators()
        {
            var result = Run(
                "pub class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub init() { item = 0 }\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag()\n" +
                "    b[0] = 21\n" +
                "    return b[0] if? 0\n" +
                "}\n");
            CheckOk("数组索引", result);
            CheckI32("get/set.array", result, 21);
        }

        // Q6（§13.2）：内建数组越界读取不 trap，按「读取失败」得 null
        private static void TestArrayIndexOutOfBoundsNull()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(2)\n" +
                "    a[0] = 7\n" +
                "    core.io.Console.println((a[5] if? -1).toString())\n" +
                "    const neg = a[(0 - 1)]\n" +
                "    if (neg == null) {\n" +
                "        core.io.Console.println(\"null\")\n" +
                "    }\n" +
                "    return a[0] if? 0\n" +
                "}\n");
            CheckOk("Q6：越界读取得 null", result);
            CaseAssertions.Check("越界/负下标 stdout", result.Stdout, "-1\nnull\n");
            CheckI32("界内读回", result, 7);
        }

        // MW9b：内建数组/Span 越界**写入**抛可捕获 core.OutOfBoundException
        //（读越界仍按空安全得 null，见上）；未捕获顶层格式对齐 native
        // reporter「{类型全名}: {message}」
        private static void TestArrayIndexOutOfBoundsWriteThrows()
        {
            var caught = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    try {\n" +
                "        a[5] = 1\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("数组越界写被 catch", caught);
            CheckI32("数组越界写 catch 返回 7", caught, 7);
            CaseAssertions.Check("数组越界写 getMessage stdout", caught.Stdout,
                "数组下标越界：5（长度 3）\n");

            var spanCaught = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var s = spanOf\\<i32>(3)\n" +
                "    try {\n" +
                "        s[(0 - 1)] = 1\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 8\n" +
                "    }\n" +
                "}\n");
            CheckOk("Span 越界写被 catch", spanCaught);
            CheckI32("Span 越界写 catch 返回 8", spanCaught, 8);
            CaseAssertions.Check("Span 越界写 getMessage stdout", spanCaught.Stdout,
                "数组下标越界：-1（长度 3）\n");

            var uncaught = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[9] = 2\n" +
                "    return 0\n" +
                "}\n");
            CaseAssertions.CheckTrue("未捕获越界写抛 OutOfBoundException",
                uncaught.Exception?.ExceptionObject is VmObject oobObj
                && oobObj.TypeRef.Contains("OutOfBoundException"),
                uncaught.Exception?.ToString() ?? "<null>");
            CaseAssertions.Check("未捕获越界写顶层格式",
                uncaught.Exception?.Message ?? "",
                "core::OutOfBoundException: 数组下标越界：9（长度 3）");
        }

        // §13.2 单次求值回归：索引写回中 getAtIndex 只读一次
        //（历史 bug：表达式位重读 place 导致 get→set→get 三次调用。
        //  Q6 后 a[i] op= x 读侧为 T? 不再可写，改为显式读改写形态锁定
        //  同一义务）
        private static void TestCompoundAssignmentIndexSingleRead()
        {
            var result = Run(
                "pub class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub var reads: i32\n" +
                "    pub init() { item = 10\nreads = 0 }\n" +
                "    pub operator getAtIndex(index: i32): i32? { reads += 1\nreturn item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag()\n" +
                "    b[0] = ((b[0] if? 0) + 5)\n" +
                "    return (b.reads * 1000) + b.item\n" +
                "}\n");
            CheckOk("索引显式读改写回", result);
            CheckI32("getAtIndex 恰好一次 + 写回值正确", result, 1015);
        }

        private static void TestEnumCasePayload()
        {
            var result = Run(
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(_ -> errorCode)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const failed: RequestResult = .Failed(404)\n" +
                "    return failed.errorCode\n" +
                "}\n");
            CheckOk("enum payload", result);
            CheckI32("Failed(404).errorCode", result, 404);
        }

        // SYNTAX §12.1 回归：固定 case 的声明点固定实参写入载荷
        //（历史 bug：固定实参被丢弃，字段读出零值）
        private static void TestEnumCaseFixedPayload()
        {
            var result = Run(
                "pub enum struct E {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}[\n" +
                "    Fixed(42),\n" +
                "    Param(v = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const f: E = .Fixed\n" +
                "    const p: E = .Param(7)\n" +
                "    return (f.v * 100) + p.v\n" +
                "}\n");
            CheckOk("enum 固定 case payload", result);
            CheckI32("Fixed(42).v = 42、Param(7).v = 7", result, 4207);
            var negative = Run(
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(_ -> errorCode)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const ok: RequestResult = .Success\n" +
                "    return ok.errorCode\n" +
                "}\n");
            CheckOk("enum 固定 case 负值 payload", negative);
            CheckI32("Success(-1).errorCode", negative, -1);
        }

        private static void TestEnumCaseIdentity()
        {
            var ok = Run(
                "pub enum struct Outcome { }[Ok, Failed]\n" +
                "pub func main(): Outcome { return .Ok }\n");
            CheckOk("enum 身份 Ok", ok);
            CaseAssertions.CheckTrue("Ok case 符号",
                ok.ReturnValue is VmEnum e && e.CaseSymbol == "Outcome.Ok",
                ok.ReturnValue?.ToStandardText() ?? "<null>");
            var failed = Run(
                "pub enum struct Outcome { }[Ok, Failed]\n" +
                "pub func main(): Outcome { return .Failed }\n");
            CheckOk("enum 身份 Failed", failed);
            CaseAssertions.CheckTrue("Failed 与 Ok 身份不同",
                failed.ReturnValue is VmEnum f && f.CaseSymbol == "Outcome.Failed"
                && ok.ReturnValue is VmEnum o && !f.SameCase(o),
                failed.ReturnValue?.ToStandardText() ?? "<null>");
        }

        private static void TestGetterSetterOrder()
        {
            var result = Run(
                "pub class Box {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) {\n" +
                "            core.io.Console.println(\"g\")\n" +
                "            return value\n" +
                "        }\n" +
                "        pub set(value: _) {\n" +
                "            core.io.Console.println(\"s\")\n" +
                "        }\n" +
                "    }\n" +
                "    pub init() { value = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    b.value = 3\n" +
                "    return b.value\n" +
                "}\n");
            CheckOk("访问器", result);
            CaseAssertions.Check("getter/setter 调用顺序", result.Stdout, "s\ns\ng\n");
            CheckI32("访问器返回值", result, 3);
        }

        private static void TestInstanceMethodReceiver()
        {
            var result = Run(
                "pub class Counter {\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 0 }\n" +
                "    pub func inc(): i32 {\n" +
                "        n = n + 1\n" +
                "        return n\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter()\n" +
                "    c.inc()\n" +
                "    return c.inc()\n" +
                "}\n");
            CheckOk("实例方法", result);
            CheckI32("this 调用链", result, 2);
        }

    }
}
