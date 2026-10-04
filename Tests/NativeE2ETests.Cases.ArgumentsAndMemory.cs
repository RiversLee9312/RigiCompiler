using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // 原编号 211..257 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateArgumentsAndMemoryCases() => new (string Label, Action Run)[]
        {
            Case("具名值包 named 实参",
                "import core.io.Console\n" +
                "func keys(opts: named Any...): i32 {\n" +
                "    Console.println(opts[0]?.key if? \"\")\n" +
                "    Console.println(opts[1]?.key if? \"\")\n" +
                "    return opts.length\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return keys(name = \"rigi\", city = \"nyc\")\n" +
                "}\n"),
            Case("具名值包 named String...",
                "import core.io.Console\n" +
                "func keys(opts: named String...): i32 {\n" +
                "    Console.println(opts[0]?.key if? \"\")\n" +
                "    Console.println(opts[1]?.key if? \"\")\n" +
                "    return opts.length\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return keys(name = \"rigi\", city = \"nyc\")\n" +
                "}\n"),
            Case("泛型继承合成 super A<T>:B<T>",
                "pub open class B\\<T> {\n" +
                "    pub var v: i32 = 41\n" +
                "}\n" +
                "pub class A\\<T> : B\\<T> {\n" +
                "    pub var w: i32 = 7\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const c = new A\\<i32>()\n" +
                "    return (c.v + c.w)\n" +
                "}\n"),
            Case("泛型位置包 TArgs",
                "import core.io.Console\n" +
                "pub func count\\<TArgs...>(values: TArgs...): i32 {\n" +
                "    return values.length\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    if (count() == 0) { Console.println(\"empty\") }\n" +
                "    if (count(1, 2, 3) == 3) { Console.println(\"three\") }\n" +
                "    return 0\n" +
                "}\n"),
            ("包转发（整包）", RunPackForwardCase),
            ("typeid 数组元素读取（BIL 级）", RunTypeIdArrayGetCase),
            ("cast.indirect 族（BIL 级）", RunIndirectCastCase),
            ("cast.indirect 不命中抛 CastException（BIL 级）", RunIndirectCastFailCase),
            ("get.wrapper.indirect（BIL 级）", RunGetWrapperIndirectCase),
            ("getid.field + field.indirect 族（BIL 级）", RunFieldIndirectCase),
            ("new.wrapped.case（BIL 级）", RunNewWrappedCaseCase),
            ("new.wrapped.case init 失配双侧拒绝（BIL 级）", RunNewWrappedCaseRejectCase),
            ("raw.hex/raw.bin → Span/SharedSpan 字节缓冲区（BIL 级）", RunRawBufferSpanCase),
            Case("kwargs 遍历 Pair 拆箱",
                "import core.io.Console\n" +
                "func show(opts: named Any...): i32 {\n" +
                "    var i = 0\n" +
                "    while (i < opts.length) {\n" +
                "        var p = opts[i] if? new Pair\\<String, Any>(\"\", (\"\" as Any))\n" +
                "        Console.println(p.key)\n" +
                "        Console.println((p.value) as String)\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return show(name = \"rigi\", city = \"nyc\")\n" +
                "}\n"),
            Case("ARC 字符串洪流",
                "import core.io.Console\n" +
                "func wrap(s: String): String { return s + \"!\" }\n" +
                "func chain(s: String): String { return wrap(s + \".\") }\n" +
                "pub func main(): i32 {\n" +
                "    var s = \"x\"\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        s = s + \"y\"\n" +
                "        s = chain(s)\n" +
                "        if ((i & 7) == 7) { s = \"x\" }\n" +
                "        n = n + 1\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 2000) { Console.println(\"flood ok\") }\n" +
                "    Console.println(s)\n" +
                "    return 0\n" +
                "}\n"),
            Case("ARC 字符串数组与字段",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Bag {\n" +
                "    pub var text: String\n" +
                "    pub init(t: String) { text = t }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var bag = new Bag(\"s\")\n" +
                "    var a = arrayOf\\<String>(4)\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        if ((i & 1) == 0) { bag.text = \"even\" } else { bag.text = \"odd\" }\n" +
                "        a[0] = bag.text\n" +
                "        a[1] = a[0] if? \"\"\n" +
                "        var slot = a[1] if? \"\"\n" +
                "        if (slot == bag.text) { n = n + 1 }\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 2000) { Console.println(\"arr ok\") }\n" +
                "    Console.println(bag.text)\n" +
                "    return 0\n" +
                "}\n"),
            Case("ARC Any 装拆箱",
                "import core.io.Console\n" +
                "pub struct Pair {\n" +
                "    pub var a: i64\n" +
                "    pub var b: i64\n" +
                "    pub init(_ -> a, _ -> b)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        var ai = i as Any\n" +
                "        var ni = ai as i32\n" +
                "        var boxedS = \"hello\" as Any\n" +
                "        var ns = boxedS as String\n" +
                "        var ap = (new Pair(11L, 22L) as Any)\n" +
                "        var p = ap as Pair\n" +
                "        if (ni == i) { n = n + 1 }\n" +
                "        if (ns == \"hello\") { n = n + 1 }\n" +
                "        if (p.a == 11L) { n = n + 1 }\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 6000) { Console.println(\"any ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("ARC nullable 数组与字段",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Bag {\n" +
                "    pub var text: String? = null\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32?>(8)\n" +
                "    var bag = new Bag()\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        var slot = i & 7\n" +
                "        var boxed: i32? = slot\n" +
                "        a[slot] = boxed\n" +
                "        var got = a[slot] if? -1\n" +
                "        if (got == slot) { n = n + 1 }\n" +
                "        bag.text = \"tok\"\n" +
                "        if (bag.text != null) { n = n + 1 }\n" +
                "        bag.text = null\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 4000) { Console.println(\"null ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("ARC rich struct 拷贝传递",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub rich struct Handle {\n" +
                "    pub var name: String\n" +
                "    pub var node: Node?\n" +
                "    pub init(_ -> name, _ -> node)\n" +
                "}\n" +
                "func take(h: Handle): Handle { return h }\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<Handle>(2)\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        var h = new Handle(\"nm\", new Node(i))\n" +
                "        var c = h\n" +
                "        var r = take(c)\n" +
                "        a[0] = r\n" +
                "        var got = a[0] if? new Handle(\"\", null)\n" +
                "        if (got.name == r.name) { n = n + 1 }\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 2000) { Console.println(\"rich ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("ARC 嵌套 rich struct",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub rich struct Inner {\n" +
                "    pub var name: String\n" +
                "    pub var node: Node?\n" +
                "    pub init(_ -> name, _ -> node)\n" +
                "}\n" +
                "pub rich struct Outer {\n" +
                "    pub var kid: Inner\n" +
                "    pub var tag: String\n" +
                "    pub init(_ -> kid, _ -> tag)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        var o = new Outer(new Inner(\"in\", new Node(i)), \"t\")\n" +
                "        var c = o\n" +
                "        if (c.kid.name == o.kid.name) { n = n + 1 }\n" +
                "        if (c.tag == \"t\") { n = n + 1 }\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 4000) { Console.println(\"nest ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("ARC 静态字段覆写",
                "import core.io.Console\n" +
                "pub shared class Box {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub var gName: String = \"init\"\n" +
                "pub var gBox: Box? = null\n" +
                "pub class Config {\n" +
                "    pub static var label: String = \"cfg\"\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        if ((i & 1) == 0) { gName = \"ga\" } else { gName = \"gb\" }\n" +
                "        gBox = new Box(i)\n" +
                "        Config.label = gName\n" +
                "        if (gName == Config.label) { n = n + 1 }\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 2000) { Console.println(\"static ok\") }\n" +
                "    Console.println(gName)\n" +
                "    return 0\n" +
                "}\n"),
            Case("ARC shared class 字段",
                "import core.io.Console\n" +
                "pub shared class Cell {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Cell(0)\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        c = new Cell(1)\n" +
                "        c.n = (c.n + 1)\n" +
                "        n = n + c.n\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 4000) { Console.println(\"shared ok\") }\n" +
                "    return c.n\n" +
                "}\n"),
            Case("ARC enum String payload",
                "import core.io.Console\n" +
                "pub enum struct Msg {\n" +
                "    pub const text: String\n" +
                "    pub init(_ -> text)\n" +
                "}[\n" +
                "    Hello(\"hello\"),\n" +
                "    Custom(text = _)\n" +
                "]\n" +
                "func take(m: Msg): Msg { return m }\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        var m = Msg.Custom(\"xy\")\n" +
                "        var c = m\n" +
                "        var r = take(c)\n" +
                "        if (r is .Custom) {\n" +
                "            if (r.text == c.text) { n = n + 1 }\n" +
                "        }\n" +
                "        if (Msg.Hello is .Hello) { n = n + 1 }\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "                    if (n == 4000) { Console.println(\"enum ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("spanOf<i32> 写读求和",
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<i32>(3)\n" +
                "    a[0] = 10\n" +
                "    a[1] = 20\n" +
                "    a[2] = 12\n" +
                "    return (((a[0] if? 0) + (a[1] if? 0)) + (a[2] if? 0))\n" +
                "}\n"),
            Case("spanOf<String> 读写",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<String>(2)\n" +
                "    a[0] = \"Hello, \"\n" +
                "    a[1] = \"world!\"\n" +
                "    Console.println(((a[0] if? \"\") + (a[1] if? \"\")))\n" +
                "    return 0\n" +
                "}\n"),
            Case("spanOf<struct Point> 元素内联",
                "import core.collections.*\n" +
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<Point>(2)\n" +
                "    a[0] = new Point(3, 4)\n" +
                "    var p = a[0] if? new Point(0, 0)\n" +
                "    return (p.x + p.y)\n" +
                "}\n"),
            Case("Span 别名语义",
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<i32>(1)\n" +
                "    a[0] = 1\n" +
                "    var b = a\n" +
                "    b[0] = 42\n" +
                "    return a[0] if? 0\n" +
                "}\n"),
            Case("sharedSpanOf<i32> 写读求和",
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = sharedSpanOf\\<i32>(2)\n" +
                "    a[0] = 40\n" +
                "    a[1] = 2\n" +
                "    return ((a[0] if? 0) + (a[1] if? 0))\n" +
                "}\n"),
            Case("Span 越界读取得 null",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<i32>(2)\n" +
                "    a[0] = 7\n" +
                "    var miss = a[5] if? -1\n" +
                "    var neg = a[(0 - 1)] if? -2\n" +
                "    if (miss == -1) { Console.println(\"oob ok\") }\n" +
                "    if (neg == -2) { Console.println(\"neg ok\") }\n" +
                "    return a[0] if? 0\n" +
                "}\n"),
            FailCase("Span 越界写",
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<i32>(2)\n" +
                "    a[5] = 1\n" +
                "    return 0\n" +
                "}\n", "数组下标越界"),
            Case("Span<rich struct> 写入读出覆写",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub rich struct Rec {\n" +
                "    pub var name: String\n" +
                "    pub var node: Node?\n" +
                "    pub init(_ -> name, _ -> node)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<Rec>(2)\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        a[0] = new Rec(\"nm\", new Node(i))\n" +
                "        a[1] = new Rec(\"xy\", null)\n" +
                "        var got = a[0] if? new Rec(\"\", null)\n" +
                "        if (got.name == \"nm\") { n = n + 1 }\n" +
                "        a[0] = new Rec(\"ov\", new Node(0))\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 2000) { Console.println(\"rich span ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("spanOf 泄漏压力",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    while (i < 10000) {\n" +
                "        var s = spanOf\\<i32>(256)\n" +
                "        s[0] = i\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    Console.println(\"span leak ok\")\n" +
                "    return 0\n" +
                "}\n"),
            Case("Span 装入 Any 再 is/cast",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<i32>(1)\n" +
                "    a[0] = 42\n" +
                "    var boxed = a as Any\n" +
                "    if (boxed is Span\\<i32>) { Console.println(\"is span\") }\n" +
                "    var back = boxed as Span\\<i32>\n" +
                "    return back[0] if? 0\n" +
                "}\n"),
            Case("Span<String> 元素覆写",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<String>(2)\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 2000) {\n" +
                "        if ((i & 1) == 0) { a[0] = \"even\" } else { a[0] = \"odd\" }\n" +
                "        a[1] = a[0] if? \"\"\n" +
                "        if ((a[1] if? \"\") == (a[0] if? \"\")) { n = n + 1 }\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if (n == 2000) { Console.println(\"str span ok\") }\n" +
                "    Console.println(a[0] if? \"\")\n" +
                "    return 0\n" +
                "}\n"),
            Case("typeOf 标量值",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var t = typeOf(x)\n" +
                "    if (x is t) { Console.println(\"i32 hit\") }\n" +
                "    Console.println(any_to_string((t as Any)))\n" +
                "    return 0\n" +
                "}\n"),
            Case("typeOf 对象实际子类",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub open class Animal { pub init() { } }\n" +
                "pub class Dog : Animal { pub init() { } }\n" +
                "pub func main(): i32 {\n" +
                "    var a: Animal = new Dog()\n" +
                "    var t = typeOf(a)\n" +
                "    if (a is t) { Console.println(\"actual hit\") }\n" +
                "    if (a is Dog) { Console.println(\"dog hit\") }\n" +
                "    Console.println(any_to_string((t as Any)))\n" +
                "    return 0\n" +
                "}\n"),
            Case("typeOf Any 装箱值",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub func main(): i32 {\n" +
                "    var a: Any = 42\n" +
                "    var t = typeOf(a)\n" +
                "    if (a is t) { Console.println(\"any hit\") }\n" +
                "    Console.println(any_to_string((t as Any)))\n" +
                "    return 0\n" +
                "}\n"),
            Case("泛型体内 typeOf(T 型值)",
                "import core.io.Console\n" +
                "pub func probe\\<T>(x: T): bool {\n" +
                "    var t = typeOf(x)\n" +
                "    return x is t\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    if (probe\\<i32>(7)) { Console.println(\"gen i32\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("typeid 装箱 Any 拆回",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(42)\n" +
                "    var a: Any = t\n" +
                "    if (a is Type\\<Any>) { Console.println(\"is type\") }\n" +
                "    var back = a as Type\\<i32>\n" +
                "    if (42 is back) { Console.println(\"box back\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("Type 构造边界 is",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(42)\n" +
                "    if (t is Type\\<i32>) { Console.println(\"is i32\") }\n" +
                "    if (t is Type\\<String>) { Console.println(\"is str\") }\n" +
                "    if (t is Type\\<Any>) { Console.println(\"is any\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("Type<i32> 装箱拆回",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(42)\n" +
                "    var a: Any = t\n" +
                "    var back = a as Type\\<i32>\n" +
                "    if (42 is back) { Console.println(\"unbox i32\") }\n" +
                "    return 0\n" +
                "}\n"),
            FailCase("Type<i32> 拆 Type<String>",
                "pub func main(): i32 {\n" +
                "    var t = typeOf(42)\n" +
                "    var a: Any = t\n" +
                "    var bad = a as Type\\<String>\n" +
                "    return 0\n" +
                "}\n", "无法将"),
            Case("typeOf(null) 打印 .null",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub func main(): i32 {\n" +
                "    var x: String? = null\n" +
                "    var t = typeOf(x)\n" +
                "    Console.println(any_to_string((t as Any)))\n" +
                "    return 0\n" +
                "}\n"),
            Case("typeOf 子类与 Type<基类> is",
                "import core.io.Console\n" +
                "pub open class Animal { pub init() { } }\n" +
                "pub class Dog : Animal { pub init() { } }\n" +
                "pub func main(): i32 {\n" +
                "    var a: Animal = new Dog()\n" +
                "    var t = typeOf(a)\n" +
                "    if (t is Type\\<Dog>) { Console.println(\"tid dog\") }\n" +
                "    if (t is Type\\<Animal>) { Console.println(\"tid animal\") }\n" +
                "    if (a is Dog) { Console.println(\"a dog\") }\n" +
                "    if (a is Animal) { Console.println(\"a animal\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("动态 new 零参 T()",
                "import core.io.Console\n" +
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init() { x = 4 }\n" +
                "}\n" +
                "pub func make\\<T extends Point>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    var p = make\\<Point>()\n" +
                "    if (p.x == 4) { Console.println(\"zero ok\") }\n" +
                "    return p.x\n" +
                "}\n"),
            Case("动态 new 带实参 T(args)",
                "import core.io.Console\n" +
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func make\\<T extends Point>(v: i32): T { return T(v) }\n" +
                "pub func main(): i32 {\n" +
                "    var p = make\\<Point>(9)\n" +
                "    if (p.x == 9) { Console.println(\"args ok\") }\n" +
                "    return p.x\n" +
                "}\n"),
            Case("动态 new typeOf 来源",
                "import core.io.Console\n" +
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Point)\n" +
                "    var p = new t(6)\n" +
                "    if (p.x == 6) { Console.println(\"typeof ok\") }\n" +
                "    return p.x\n" +
                "}\n"),

        };

    }
}
