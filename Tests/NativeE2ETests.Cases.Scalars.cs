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
        // 原编号 0..52 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateScalarsCases() => new (string Label, Action Run)[]
        {
            Case("hello world",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"Hello, world!\")\n" +
                "    return 0\n" +
                "}\n"),
            Case("字符串拼接",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var greeting = \"Hello, \" + \"rigi\"\n" +
                "    Console.println(greeting + \"!\")\n" +
                "    return 0\n" +
                "}\n"),
            // String.length（core::String#length@.i64）原生直读：字面量
            // 常量槽与局部 ARC 槽两种宿主形态（比较字面量带 L 后缀，i64）
            Case("String.length 字面量宿主",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    if (\"abc\".length == 3L) { Console.println(\"strlen ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("String.length 变量宿主",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s = \"abcd\"\n" +
                "    if (s.length == 4L) { Console.println(\"strlen var ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("String UTF-8 length 与 characterCount",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    if ((\"序列化测试\".length == 15L) and (\"序列化测试\".characterCount == 5L)) { Console.println(\"cjk ok\") }\n" +
                "    if ((\"👨‍👩‍👧\".length == 18L) and (\"👨‍👩‍👧\".characterCount == 5L)) { Console.println(\"family ok\") }\n" +
                "    if ((\"🇨🇳\".length == 8L) and (\"🇨🇳\".characterCount == 2L)) { Console.println(\"flag ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("print 无换行原样输出",
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"print\")\n" +
                "native func rawPrint(text: String)\n" +
                "pub func main(): i32 {\n" +
                "    rawPrint(\"ab\")\n" +
                "    rawPrint(\"cd\")\n" +
                "    return 0\n" +
                "}\n"),
            Case("标量退出码",
                "pub func main(): i32 {\n" +
                "    return (6 * 7)\n" +
                "}\n"),
            Case("if/else 分支",
                "pub func main(): i32 {\n" +
                "    var x = 10\n" +
                "    if (x > 5) { x = 1 } else { x = 2 }\n" +
                "    return x\n" +
                "}\n"),
            Case("while 求和",
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    var i = 1\n" +
                "    while (i <= 10) {\n" +
                "        sum = sum + i\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n"),
            Case("do-while 先执行",
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    do {\n" +
                "        x = x + 1\n" +
                "    } while (x < 5)\n" +
                "    return x\n" +
                "}\n"),
            Case("break/continue",
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    var i = 0\n" +
                "    while (i < 10) {\n" +
                "        i = i + 1\n" +
                "        if (i == 3) { continue }\n" +
                "        if (i > 7) { break }\n" +
                "        sum = sum + i\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n"),
            Case("嵌套标签 break@outer",
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    while (x < 10) named outer {\n" +
                "        while (x < 5) {\n" +
                "            x = x + 1\n" +
                "            if (x == 3) { break@outer }\n" +
                "            continue\n" +
                "        }\n" +
                "        x = x + 2\n" +
                "    }\n" +
                "    return x\n" +
                "}\n"),
            Case("短路求值降 if 块",
                "pub func main(): i32 {\n" +
                "    var a = true\n" +
                "    var b = false\n" +
                "    if ((a and b) or (a and (not b))) { return 7 }\n" +
                "    return 0\n" +
                "}\n"),
            Case("switch 常量表",
                "pub func classify(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 10 }\n" +
                "        (2) -> { return 20 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return classify(2)\n" +
                "}\n"),
            // MW3：f64 selector → 比较链降级（命中两项 + default）
            Case("switch f64 selector 命中与 default",
                "pub func classify(x: double): i32 {\n" +
                "    switch (x) {\n" +
                "        (1.5) -> { return 10 }\n" +
                "        (2.5) -> { return 20 }\n" +
                "        default -> { return 1 }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return ((classify(2.5) + classify(1.5)) + classify(9.0))\n" +
                "}\n"),
            // MW3：f32 selector → 比较链降级（命中 + default）
            Case("switch f32 selector 命中与 default",
                "pub func classify(x: float): i32 {\n" +
                "    switch (x) {\n" +
                "        (0.5f) -> { return 5 }\n" +
                "        (1.5f) -> { return 15 }\n" +
                "        default -> { return 2 }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return classify(1.5f) + classify(0.25f)\n" +
                "}\n"),
            // MW3：String selector → 比较链降级，按值相等（运行时拼接的
            // 字符串命中字面量 case，证实非引用恒等）+ default
            Case("switch String selector 按值命中与 default",
                "pub func classify(s: String): i32 {\n" +
                "    switch (s) {\n" +
                "        (\"hello\") -> { return 10 }\n" +
                "        (\"world\") -> { return 20 }\n" +
                "        default -> { return 3 }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var built = \"wor\" + \"ld\"\n" +
                "    return ((classify(\"hello\") + classify(built)) + classify(\"?\"))\n" +
                "}\n"),
            // MW3：VM 语义边界（C# == 口径）——NaN 与任何 case 不等（落
            // default）；-0.0/+0.0 交叉命中与 NaN case 标签见下方手写
            // BIL 用例「switch f64 符号零与 NaN 标签（BIL 级）」
            Case("switch f64 NaN selector 落 default",
                "pub func main(): i32 {\n" +
                "    var zero = 0.0\n" +
                "    var nan = (zero / zero)\n" +
                "    var nanBranch = switch (nan) {\n" +
                "        (0.0) -> { return@_ 1 }\n" +
                "        (1.0) -> { return@_ 2 }\n" +
                "        default -> { return@_ 3 }\n" +
                "    }\n" +
                "    var hitBranch = switch (zero) {\n" +
                "        (0.0) -> { return@_ 10 }\n" +
                "        default -> { return@_ 20 }\n" +
                "    }\n" +
                "    return (nanBranch + hitBranch)\n" +
                "}\n"),
            BilCase("switch f64 符号零与 NaN 标签（BIL 级）", SwitchF64SignZeroBil),
            Case("pattern switch 降级链（call blk）",
                "pub func main(): i32 {\n" +
                "    var x = 5\n" +
                "    var label = switch (x) {\n" +
                "        (_ > 10) -> { return@_ 1 }\n" +
                "        default -> { return@_ 0 }\n" +
                "    }\n" +
                "    return label\n" +
                "}\n"),
            Case("浮点四则与比较",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var x = 1.5\n" +
                "    var y = 2.25\n" +
                "    if ((x + y) == 3.75) { Console.println(\"f64 add ok\") }\n" +
                "    if ((y - x) == 0.75) { Console.println(\"f64 sub ok\") }\n" +
                "    if ((x * y) == 3.375) { Console.println(\"f64 mul ok\") }\n" +
                "    if ((y / x) == 1.5) { Console.println(\"f64 div ok\") }\n" +
                "    if (x < y) { Console.println(\"f64 lt ok\") }\n" +
                "    var p = 0.5f\n" +
                "    var q = 1.5f\n" +
                "    if ((p + q) == 2.0f) { Console.println(\"f32 add ok\") }\n" +
                "    if (p < q) { Console.println(\"f32 lt ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("位运算与按位取反",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = 60\n" +
                "    var b = 13\n" +
                "    if ((a & b) == 12) { Console.println(\"and ok\") }\n" +
                "    if ((a | b) == 61) { Console.println(\"or ok\") }\n" +
                "    if ((a ^ b) == 49) { Console.println(\"xor ok\") }\n" +
                "    if ((!a) == -61) { Console.println(\"not ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("三种移位",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = 60\n" +
                "    if ((a << 2) == 240) { Console.println(\"shl ok\") }\n" +
                "    if ((a >> 2) == 15) { Console.println(\"shr ok\") }\n" +
                "    var n = -16\n" +
                "    if ((n >> 2) == -4) { Console.println(\"ashr ok\") }\n" +
                "    if ((n >>> 2) == 1073741820) { Console.println(\"lshr ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("移位量按位宽掩码（overshift 对齐 VM）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s = 1B\n" +
                "    if ((s << 9B) == 2B) { Console.println(\"i8 overshift masked\") }\n" +
                "    var w = 1\n" +
                "    if ((w << 33) == 2) { Console.println(\"i32 overshift masked\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("无符号比较与除法",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var u = 4000000000U\n" +
                "    var v = 3000000000U\n" +
                "    if (u > v) { Console.println(\"u cmp ok\") }\n" +
                "    if ((u / 7U) == 571428571U) { Console.println(\"u div ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("窄宽度 i8/u8/i16/u16 运算",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = 100B\n" +
                "    var b = 23B\n" +
                "    if ((a - b) == 77B) { Console.println(\"i8 sub ok\") }\n" +
                "    if ((a + b) == 123B) { Console.println(\"i8 add ok\") }\n" +
                "    var c = 200UB\n" +
                "    var d = 7UB\n" +
                "    if ((c / d) == 28UB) { Console.println(\"u8 div ok\") }\n" +
                "    if (c > d) { Console.println(\"u8 cmp ok\") }\n" +
                "    var e = 1000S\n" +
                "    var f = 300S\n" +
                "    if ((e - f) == 700S) { Console.println(\"i16 sub ok\") }\n" +
                "    var g = 50000US\n" +
                "    var h = 1234US\n" +
                "    if ((g - h) == 48766US) { Console.println(\"u16 sub ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("窄宽度回绕与移位",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = 100B\n" +
                "    if ((a * 2B) == -56B) { Console.println(\"i8 mul wrap ok\") }\n" +
                "    var s = 1B\n" +
                "    if ((s << 3B) == 8B) { Console.println(\"i8 shl ok\") }\n" +
                "    var t = -16B\n" +
                "    if ((t >> 2B) == -4B) { Console.println(\"i8 ashr ok\") }\n" +
                "    if ((t >>> 2B) == 60B) { Console.println(\"i8 lshr ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("char 比较（含高位码元）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var x = 'a'\n" +
                "    var y = 'b'\n" +
                "    if (x < y) { Console.println(\"char lt\") }\n" +
                "    if (x == 'a') { Console.println(\"char eq\") }\n" +
                "    var hi = '￿'\n" +
                "    var lo = '中'\n" +
                "    if (hi > lo) { Console.println(\"char high gt\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("string eq/ne",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = \"abc\"\n" +
                "    var b = \"abd\"\n" +
                "    var c = \"abc\"\n" +
                "    if (a == c) { Console.println(\"str eq ok\") }\n" +
                "    if (a != b) { Console.println(\"str ne ok\") }\n" +
                "    if (a == b) { Console.println(\"BAD\") }\n" +
                "    return 0\n" +
                "}\n"),
            // string 排序比较：前端 P3 暂未放行 String 的 < 运算符（BIL §11.5
            // 内建形态合法，VM 支持），故以手写 BIL 直接对拍
            BilCase("string 排序比较（BIL 内建形态）", StringOrderBil),
            BilCase("to_string 面族（手写 BIL）", ToStringFacesBil),
            Case("null 资源与 nullable 检查",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s: String? = null\n" +
                "    if (s == null) { Console.println(\"ref null ok\") }\n" +
                "    var n: i32? = null\n" +
                "    if (n == null) { Console.println(\"val null ok\") }\n" +
                "    if (n != null) { Console.println(\"BAD\") }\n" +
                "    if (s != null) { Console.println(\"BAD2\") }\n" +
                "    return 0\n" +
                "}\n"),
            // nullable 双空互比：前端 P3 只放行 nullable 与 null 字面量的
            // 比较（两个 nullable 变量互比报 operator 未定义），BIL §11.5
            // 内建形态合法，故以手写 BIL 对拍
            BilCase("nullable 双空引用恒等（BIL 内建形态）", NullBothBil),
            Case("i8/i16/i32 MIN/-1 回绕",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = -128B\n" +
                "    var b = -1B\n" +
                "    if ((a / b) == -128B) { Console.println(\"i8 min/-1 wrap\") }\n" +
                "    var c = -32768S\n" +
                "    var d = -1S\n" +
                "    if ((c / d) == -32768S) { Console.println(\"i16 min/-1 wrap\") }\n" +
                "    var e = -2147483648\n" +
                "    var f = -1\n" +
                "    if ((e / f) == -2147483648) { Console.println(\"i32 min/-1 wrap\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("浮点除零 IEEE（Inf/NaN）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var z = 0.0\n" +
                "    var big = 1.0\n" +
                "    if ((big / z) > 1e308) { Console.println(\"f64 inf\") }\n" +
                "    var nan = (z / z)\n" +
                "    if (nan != nan) { Console.println(\"f64 nan\") }\n" +
                "    var zf = 0.0f\n" +
                "    var bigf = 1.0f\n" +
                "    if ((bigf / zf) > 3e38f) { Console.println(\"f32 inf\") }\n" +
                "    return 0\n" +
                "}\n"),
            FailCase("整数除零",
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 0\n" +
                "    return (x / z)\n" +
                "}\n", "整数除以零"),
            FailCase("无符号除零",
                "pub func main(): i32 {\n" +
                "    var x = 42UL\n" +
                "    var z = 0UL\n" +
                "    if ((x / z) == 0UL) { return 1 }\n" +
                "    return 0\n" +
                "}\n", "整数除以零"),
            FailCase("窄宽度除零",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = 42B\n" +
                "    var z = (1B - 1B)\n" +
                "    if ((a / z) == 1B) { Console.println(\"x\") }\n" +
                "    return 0\n" +
                "}\n", "整数除以零"),
            // ===== mod-3：取模 %（srem/urem/frem + 模零 guard + 用户派发）=====
            Case("整数取模（i32 含负号/i64/u8）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = 17\n" +
                "    var b = 5\n" +
                "    if ((a % b) == 2) { Console.println(\"i32 mod ok\") }\n" +
                "    var c = -17\n" +
                "    if ((c % b) == -2) { Console.println(\"i32 neg mod ok\") }\n" +
                "    var d = 42\n" +
                "    var e = -5\n" +
                "    if ((d % e) == 2) { Console.println(\"i32 neg divisor mod ok\") }\n" +
                "    var big = 17L\n" +
                "    if ((big % 5L) == 2L) { Console.println(\"i64 mod ok\") }\n" +
                "    var u = 200UB\n" +
                "    if ((u % 7UB) == 4UB) { Console.println(\"u8 mod ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("i32 MIN/-1 取模消毒（x % ±1 == 0，不溢出失败）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var m = -2147483648\n" +
                "    var n = -1\n" +
                "    if ((m % n) == 0) { Console.println(\"i32 min/-1 mod\") }\n" +
                "    var p = -9223372036854775808L\n" +
                "    if ((p % 1L) == 0L) { Console.println(\"i64 min mod ±1\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("浮点取模与模零 NaN（IEEE 截断余数）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var x = 7.5\n" +
                "    var z = 2.25\n" +
                "    if ((x % z) == 0.75) { Console.println(\"f64 mod ok\") }\n" +
                "    var zero = 0.0\n" +
                "    var nan = (x % zero)\n" +
                "    if (nan != nan) { Console.println(\"f64 mod nan\") }\n" +
                "    var pf = 7.5f\n" +
                "    var qf = 2.0f\n" +
                "    if ((pf % qf) == 1.5f) { Console.println(\"f32 mod ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("try/catch 捕获整数模零",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 0\n" +
                "    try {\n" +
                "        return (x % z)\n" +
                "    } catch (e: core.DividedByZeroException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            FailCase("无符号模零",
                "pub func main(): i32 {\n" +
                "    var x = 42UL\n" +
                "    var z = 0UL\n" +
                "    if ((x % z) == 0UL) { return 1 }\n" +
                "    return 0\n" +
                "}\n", "整数除以零"),
            Case("用户 operator mod 派发",
                "import core.io.Console\n" +
                "pub class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "    pub operator mod(other: Vec): Vec { return new Vec((x % other.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(17)\n" +
                "    var b = new Vec(5)\n" +
                "    var r = (a % b)\n" +
                "    if (r.x == 2) { Console.println(\"user mod ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("取模复合赋值 %=",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var x = 47\n" +
                "    x %= 5\n" +
                "    if (x == 2) { Console.println(\"i32 %= ok\") }\n" +
                "    var d = 7.5\n" +
                "    d %= 2.0\n" +
                "    if (d == 1.5) { Console.println(\"f64 %= ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            FailCase("i64 MIN/-1 溢出",
                "pub func main(): i32 {\n" +
                "    var e = -9223372036854775808L\n" +
                "    var f = -1L\n" +
                "    if ((e / f) == 0L) { return 1 }\n" +
                "    return 0\n" +
                "}\n", "overflow"),
            Case("class new 与字段读写（含零值）",
                "import core.io.Console\n" +
                "pub class Counter {\n" +
                "    pub var count: i32\n" +
                "    pub init() { count = 0 }\n" +
                "    pub func bump(): i32 { count = (count + 1)\n" +
                "        return count }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter()\n" +
                "    if (c.count == 0) { Console.println(\"zero ok\") }\n" +
                "    c.count = 41\n" +
                "    if (c.count == 41) { Console.println(\"rw ok\") }\n" +
                "    return (c.bump())\n" +
                "}\n"),
            Case("init 参数映射（_ -> x）",
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Point(3, 4)\n" +
                "    return ((p.x * 10) + p.y)\n" +
                "}\n"),
            Case("字段声明初始值与 null 字段（..init.wrapper 缝合）",
                "import core.io.Console\n" +
                "pub class Link {\n" +
                "    pub var a: i32 = 7\n" +
                "    pub var p: Link? = null\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var l = new Link()\n" +
                "    if (l.a == 7) { Console.println(\"init value ok\") }\n" +
                "    if (l.p == null) { Console.println(\"null field ok\") }\n" +
                "    return l.a\n" +
                "}\n"),
            Case("继承字段与 super init/方法",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "    pub open func who(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub init(v: i32) { super(v) }\n" +
                "    pub override func who(): i32 { return (super() + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Derived(41)\n" +
                "    if (d.x == 41) { Console.println(\"super init ok\") }\n" +
                "    return (d.who())\n" +
                "}\n"),
            Case("虚派发（基类槽变量装派生实例）",
                "import core.io.Console\n" +
                "pub open class Animal {\n" +
                "    pub init() { }\n" +
                "    pub open func speak(): String { return \"...\" }\n" +
                "}\n" +
                "pub class Dog : Animal {\n" +
                "    pub init() { }\n" +
                "    pub override func speak(): String { return \"woof\" }\n" +
                "}\n" +
                "pub func describe(a: Animal): i32 {\n" +
                "    Console.println(a.speak())\n" +
                "    return 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Dog()\n" +
                "    var a: Animal = d\n" +
                "    describe(a)\n" +
                "    return 0\n" +
                "}\n"),
            // review-20260910 #11：字段覆写（open var + override var 不同
            // 初值）共享基类槽，初值经合成 ..init.field.<名> 虚派发选最高
            // 派生实现——合成方法无 override 修饰符，native vtable 曾按
            // 新槽追加导致经基类引用读回基类初值
            Case("字段覆写初值三层链经基类引用虚派发",
                "import core.io.Console\n" +
                "pub open class BaseF {\n" +
                "    pub open var hp: i32 = 10\n" +
                "    pub init()\n" +
                "}\n" +
                "pub open class HeroF : BaseF {\n" +
                "    pub override var hp: i32 = 99\n" +
                "    pub init()\n" +
                "}\n" +
                "pub class VillainF : HeroF {\n" +
                "    pub override var hp: i32 = 7\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new BaseF()\n" +
                "    const h = new HeroF()\n" +
                "    const v = new VillainF()\n" +
                "    const viaBase: BaseF = v\n" +
                "    if (b.hp == 10) { Console.println(\"base ok\") }\n" +
                "    if (h.hp == 99) { Console.println(\"hero ok\") }\n" +
                "    if (viaBase.hp == 7) { Console.println(\"viaBase ok\") }\n" +
                "    return viaBase.hp\n" +
                "}\n"),
            Case("interface 派发",
                "import core.io.Console\n" +
                "pub interface Named {\n" +
                "    func name(): String\n" +
                "}\n" +
                "pub class Dog implements Named {\n" +
                "    pub init() { }\n" +
                "    pub override func name(): String { return \"dog\" }\n" +
                "}\n" +
                "pub func callName(n: Named): i32 {\n" +
                "    Console.println(n.name())\n" +
                "    return 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Dog()\n" +
                "    var n: Named = d\n" +
                "    callName(n)\n" +
                "    return 0\n" +
                "}\n"),

        };

    }
}
