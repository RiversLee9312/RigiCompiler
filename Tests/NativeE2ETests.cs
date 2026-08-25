using System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// native 端到端对拍套件（MIDDLEWARE_ARCHITECTURE §10 第 2 条）：同一 BIL
    /// 在 BIL VM（行为参考实现）与 native 产物上的可观察行为一致
    /// （stdout / 退出码）。用例走编译器真实产物（EmitBilUnit 全管线），
    /// 杜绝手编样例漂移。未找到 clang 工具链时整套 skip（不计失败）——
    /// CI 双平台 runner 预装 clang/lld 必跑。
    /// </summary>
    public static class NativeE2ETests
    {
        public static int RunAll()
        {
            return TrySkipEntireSuite() ?? ParallelSuiteRunner.RunAll(Spec);
        }

        public static int RunWithArgs(IReadOnlyList<string> args)
        {
            return TrySkipEntireSuite() ?? ParallelSuiteRunner.RunWithArgs(Spec, args);
        }

        private static int? TrySkipEntireSuite()
        {
            if (ToolchainResolver.ResolveClang(null) != null)
            {
                return null;
            }
            TestHarness.Reset();
            TestHarness.Section("native 对拍（VM vs 原生可执行）");
            Console.WriteLine("  （跳过：未找到 clang 工具链；" +
                "开发机跑 tools/Fetch-LlvmToolchain.ps1 后本套件生效）");
            return TestHarness.Summary("NativeE2E");
        }

        private static ParallelSuiteRunner.SuiteSpec Spec => new(
            "NativeE2E",
            Cases,
            sectionTitle: "native 对拍（VM vs 原生可执行）",
            beforeSpawn: PreheatRigiRt);

        private static void PreheatRigiRt()
        {
            var clang = ToolchainResolver.ResolveClang(null);
            if (clang != null)
            {
                RigiRtBuilder.EnsureBitcode(clang, out _);
            }
        }

        private static (string Label, Action Run) Case(string label, string source) =>
            (label, () => RunCase(label, source));

        private static (string Label, Action Run) BilCase(string label, string bil) =>
            (label, () => RunBilCase(label, bil));

        private static (string Label, Action Run) FailCase(string label, string source, string needle) =>
            (label, () => RunFailCase(label, source, needle));

        private static readonly (string Label, Action Run)[] Cases =
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
            Case("print 无换行原样输出",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    Console.print(\"ab\")\n" +
                "    Console.print(\"cd\")\n" +
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
            Case("computed getter/setter",
                "import core.io.Console\n" +
                "pub class Counter {\n" +
                "    pub var count: i32 { pub get(value: _) { return value } priv set(value: _) { count = value } } = 0\n" +
                "    pub func bump(): i32 { count = (count + 1)\n" +
                "        return count }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter()\n" +
                "    if (c.bump() == 1) { Console.println(\"computed ok\") }\n" +
                "    return (c.bump())\n" +
                "}\n"),
            Case("String 字段读写",
                "import core.io.Console\n" +
                "pub class Bag {\n" +
                "    pub var text: String\n" +
                "    pub init(t: String) { text = t }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag(\"hi\")\n" +
                "    Console.println(b.text)\n" +
                "    return 0\n" +
                "}\n"),
            Case("struct new 与字段读写",
                "import core.io.Console\n" +
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Point(3, 4)\n" +
                "    if (p.x == 3) { Console.println(\"read ok\") }\n" +
                "    p.y = 40\n" +
                "    if (p.y == 40) { Console.println(\"write ok\") }\n" +
                "    return ((p.x * 10) + p.y)\n" +
                "}\n"),
            Case("struct 深拷贝（赋值互不影响）",
                "import core.io.Console\n" +
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1001)\n" +
                "    var b = a\n" +
                "    b.x = 99\n" +
                "    if (a.x == 1001) { Console.println(\"deep copy ok\") }\n" +
                "    if (b.x == 99) { Console.println(\"copy independent\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("struct 方法原地修改（this 别名）",
                "import core.io.Console\n" +
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "    pub func move(dx: i32): i32 { x = (x + dx)\n" +
                "        return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var v = new Vec(1)\n" +
                "    v.move(10)\n" +
                "    if (v.x == 11) { Console.println(\"this alias ok\") }\n" +
                "    return v.x\n" +
                "}\n"),
            Case("struct 参数深拷贝隔离",
                "import core.io.Console\n" +
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub func mutate(v: Vec): i32 { v.x = 99\n" +
                "    return v.x }\n" +
                "pub func main(): i32 {\n" +
                "    var v = new Vec(1)\n" +
                "    mutate(v)\n" +
                "    if (v.x == 1) { Console.println(\"param isolated\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("struct 返回",
                "import core.io.Console\n" +
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub func makeVec(n: i32): Vec {\n" +
                "    return new Vec(n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = makeVec(7)\n" +
                "    if (m.x == 7) { Console.println(\"return ok\") }\n" +
                "    return m.x\n" +
                "}\n"),
            Case("嵌套 struct 链写",
                "import core.io.Console\n" +
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub struct Pair {\n" +
                "    pub var a: Vec\n" +
                "    pub var b: i32\n" +
                "    pub init(_ -> a, _ -> b)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Pair(new Vec(1), 2)\n" +
                "    p.a.x = 5\n" +
                "    if (p.a.x == 5) { Console.println(\"chain ok\") }\n" +
                "    return p.b\n" +
                "}\n"),
            Case("class 内嵌 struct 字段",
                "import core.io.Console\n" +
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub class Box {\n" +
                "    pub var v: Vec\n" +
                "    pub init(w: Vec) { v = w }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box(new Vec(3))\n" +
                "    if (b.v.x == 3) { Console.println(\"embedded ok\") }\n" +
                "    b.v.x = 8\n" +
                "    if (b.v.x == 8) { Console.println(\"embedded write ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("enum 构造与 is .Case",
                "import core.io.Console\n" +
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    pub init(_ -> degrees)\n" +
                "}[\n" +
                "    North(0),\n" +
                "    East(90),\n" +
                "    West(270)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    var d = Direction.East\n" +
                "    if (d is .East) { Console.println(\"is case ok\") }\n" +
                "    return d.degrees\n" +
                "}\n"),
             Case("带洞 enum（Failed(404).errorCode）",
                  "import core.io.Console\n" +
                  "pub enum struct RequestResult {\n" +
                  "    pub const errorCode: i32\n" +
                  "    pub init(_ -> errorCode)\n" +
                  "}[\n" +
                  "    Success(-1),\n" +
                  "    Failed(errorCode = _)\n" +
                  "]\n" +
                  "pub func main(): i32 {\n" +
                  "    var r = RequestResult.Failed(404)\n" +
                  "    if (r.errorCode == 404) { Console.println(\"hole ok\") }\n" +
                  // 返回值须 <256：linux 进程退出码 8-bit 截断（404→148），
                  // 对拍断言的是跨平台可观察一致
                  "    if (r.errorCode == 404) { return 42 }\n" +
                  "    return 0\n" +
                  "}\n"),
            Case("enum 判别恒等（两枚同 case 值）",
                "import core.io.Console\n" +
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    pub init(_ -> degrees)\n" +
                "}[\n" +
                "    North(0),\n" +
                "    East(90),\n" +
                "    West(270)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    var a = Direction.East\n" +
                "    var b = Direction.East\n" +
                "    if (a is .East) { Console.println(\"a east\") }\n" +
                "    if (b is .East) { Console.println(\"b east\") }\n" +
                "    if (b is .West) { Console.println(\"BAD\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("enum switch 表达式（pattern is.case 链）",
                "import core.io.Console\n" +
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    pub init(_ -> degrees)\n" +
                "}[\n" +
                "    North(0),\n" +
                "    East(90),\n" +
                "    West(270)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    var d = Direction.East\n" +
                "    var label = switch (d) {\n" +
                "        (_ is .North) -> { return@_ 0 }\n" +
                "        (_ is .East) -> { return@_ 90 }\n" +
                "        default -> { return@_ -1 }\n" +
                "    }\n" +
                "    if (label == 90) { Console.println(\"enum switch ok\") }\n" +
                "    return label\n" +
                "}\n"),
            Case("static 字段读写与初值（..globals.init）",
                "import core.io.Console\n" +
                "pub var gCounter: i32 = 41\n" +
                "pub var gName: String = \"g\"\n" +
                "pub class Config {\n" +
                "    pub static var level: i32 = 3\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    gCounter = (gCounter + 1)\n" +
                "    if (gCounter == 42) { Console.println(\"static rw ok\") }\n" +
                "    if (Config.level == 3) { Console.println(\"static init ok\") }\n" +
                "    Console.println(gName)\n" +
                "    return gCounter\n" +
                "}\n"),
            Case("多 static 初值声明序",
                "import core.io.Console\n" +
                "pub var a: i32 = 1\n" +
                "pub var b: i32 = 2\n" +
                "pub var c: i32 = 3\n" +
                "pub func main(): i32 {\n" +
                "    if (((a + b) + c) == 6) { Console.println(\"multi init ok\") }\n" +
                "    return (((a * 100) + (b * 10)) + c)\n" +
                "}\n"),
            Case("数组创建/读写/长度",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 10\n" +
                "    a[1] = 20\n" +
                "    a[2] = 12\n" +
                "    if (a.length == 3) { Console.println(\"len ok\") }\n" +
                "    return (((a[0] if? 0) + (a[1] if? 0)) + (a[2] if? 0))\n" +
                "}\n"),
            Case("数组越界读取得 null",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(2)\n" +
                "    a[0] = 7\n" +
                "    var miss = a[5] if? -1\n" +
                "    var neg = a[(0 - 1)] if? -2\n" +
                "    if (miss == -1) { Console.println(\"oob ok\") }\n" +
                "    if (neg == -2) { Console.println(\"neg ok\") }\n" +
                "    return a[0] if? 0\n" +
                "}\n"),
            Case("数组引用元素",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Box {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<Box>(2)\n" +
                "    a[0] = new Box(11)\n" +
                "    var x = a[0]?.n if? 0\n" +
                "    var y = a[1]?.n if? -1\n" +
                "    if (y == -1) { Console.println(\"ref null ok\") }\n" +
                "    return (x + y)\n" +
                "}\n"),
            Case("数组 struct 元素内联",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<Point>(2)\n" +
                "    a[0] = new Point(3, 4)\n" +
                "    var p = a[0] if? new Point(0, 0)\n" +
                "    var miss = a[9] if? new Point(8, 1)\n" +
                "    if ((p.x + p.y) == 7) { Console.println(\"struct elem ok\") }\n" +
                "    return ((p.x + p.y) + (miss.x + miss.y))\n" +
                "}\n"),
            Case("用户索引运算符（get/set.array 降调用）",
                "import core.io.Console\n" +
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
                "}\n"),
            Case("static computed 属性（访问器路径）",
                "import core.io.Console\n" +
                "pub var setCalls: i32 = 0\n" +
                "pub class Config {\n" +
                "    pub static var level: i32 { get(_: _) { return 7 } set(_: _) { setCalls = (setCalls + 1) } }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Config.level = 8\n" +
                "    if (Config.level == 7) { Console.println(\"static computed get ok\") }\n" +
                "    if (setCalls == 1) { Console.println(\"static computed set ok\") }\n" +
                "    return Config.level\n" +
                "}\n"),
        };

        // 单用例：源 → 中端全管线 → BIL 文本 → VM 执行 + native 编译执行，
        // 比 stdout（行尾归一）与退出码（main 的 i32 返回）
        private static void RunCase(string label, string source)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var (_, module, _) = BilTestHarness.EmitBilUnit(source);
                var text = BilWriter.Write(module);

                // VM 侧（行为参考实现）
                var vm = BilVm.Run(BilReader.Read(text));
                TestHarness.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                var expectedExit = vm.ReturnValue is VmI32 value ? value.Value : 0;

                // native 侧：CLI 编译 → 进程执行
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
                TestHarness.CheckTrue(label + "：退出码一致",
                    runExit == expectedExit, $"native={runExit} vm={expectedExit}");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // Windows CRT stdout 文本模式把 \n 翻成 \r\n；比对面统一归一
        private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

        // string 排序比较的手写 BIL（前端 P3 未放行 String 的 < 运算符，
        // §11.5 内建形态合法）：cmp.lt/gt/le 三形态 + native print 面输出。
        // 结构模仿前端产物（if 块 + breakid），VM 侧经 rigi_rt/print hook 执行
        private const string StringOrderBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"strorder\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_A = string \"abc\",\n" +
            "    R_B = string \"abd\",\n" +
            "    R_Lt = string \"str lt ok\\n\",\n" +
            "    R_Gt = string \"str gt ok\\n\",\n" +
            "    R_Le = string \"str le ok\\n\",\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "    .type core.io::Console = class pub {\n" +
            "        .static-method core.io::Console$.static.print(value:.string)@.void priv native symbol(\"print\") lib(\"rigi_rt\")\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .string a,\n" +
            "        .string b,\n" +
            "        .breakid .b0,\n" +
            "        .breakid .b1,\n" +
            "        .breakid .b2,\n" +
            "        .bool .t0,\n" +
            "        .bool .t1,\n" +
            "        .bool .t2,\n" +
            "        .string .t3,\n" +
            "        .string .t4,\n" +
            "        .string .t5,\n" +
            "        .i32 .t6\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_A) $a\n" +
            "        load res(R_B) $b\n" +
            "        cmp.lt $a $b $.t0\n" +
            "        if $.t0 blk(if0-then) none $.b0\n" +
            "        cmp.gt $b $a $.t1\n" +
            "        if $.t1 blk(if1-then) none $.b1\n" +
            "        cmp.le $a $a $.t2\n" +
            "        if $.t2 blk(if2-then) none $.b2\n" +
            "        load res(R_Zero) $.t6\n" +
            "        ret $.t6\n" +
            "    }\n" +
            "    .block if0-then {\n" +
            "        load res(R_Lt) $.t3\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t3]\n" +
            "    }\n" +
            "    .block if1-then {\n" +
            "        load res(R_Gt) $.t4\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t4]\n" +
            "    }\n" +
            "    .block if2-then {\n" +
            "        load res(R_Le) $.t5\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t5]\n" +
            "    }\n" +
            "}\n";

        // nullable 双空互比的手写 BIL（前端 P3 未放行两 nullable 变量互比，
        // §11.5 内建形态合法）：两个 .nullable<.string> 同载 null 资源后
        // cmp.eq 为真、cmp.ne 为假（胖引用双段零恒等）
        private const string NullBothBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"nullboth\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_Null = null type(.string),\n" +
            "    R_Eq = string \"both null eq\\n\",\n" +
            "    R_Bad = string \"BAD\\n\",\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "    .type core.io::Console = class pub {\n" +
            "        .static-method core.io::Console$.static.print(value:.string)@.void priv native symbol(\"print\") lib(\"rigi_rt\")\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .nullable<.string> a,\n" +
            "        .nullable<.string> b,\n" +
            "        .breakid .b0,\n" +
            "        .breakid .b1,\n" +
            "        .bool .t0,\n" +
            "        .bool .t1,\n" +
            "        .string .t2,\n" +
            "        .string .t3,\n" +
            "        .i32 .t4\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_Null) $a\n" +
            "        load res(R_Null) $b\n" +
            "        cmp.eq $a $b $.t0\n" +
            "        if $.t0 blk(if0-then) none $.b0\n" +
            "        cmp.ne $a $b $.t1\n" +
            "        if $.t1 blk(if1-then) none $.b1\n" +
            "        load res(R_Zero) $.t4\n" +
            "        ret $.t4\n" +
            "    }\n" +
            "    .block if0-then {\n" +
            "        load res(R_Eq) $.t2\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t2]\n" +
            "    }\n" +
            "    .block if1-then {\n" +
            "        load res(R_Bad) $.t3\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t3]\n" +
            "    }\n" +
            "}\n";

        // 失败对拍（MW2 占位除零语义）：VM 抛语言级异常（消息含关键字）、
        // native 走 abort 面——stderr 关键字对齐（abort 面文本与 VM 消息
        // 逐字节一致）、native 退出码 1 对齐 vm 命令未捕获异常出口、
        // stdout 一致
        private static void RunFailCase(string label, string source, string keyword)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var (_, module, _) = BilTestHarness.EmitBilUnit(source);
                var text = BilWriter.Write(module);

                // VM 侧：应有未捕获语言级异常，消息含关键字
                var vm = BilVm.Run(BilReader.Read(text));
                TestHarness.CheckTrue(label + "：VM 有异常", vm.Exception != null);
                TestHarness.CheckTrue(label + "：VM 消息含关键字",
                    vm.Exception != null && vm.Exception.Message.Contains(keyword),
                    vm.Exception?.Message ?? "");

                // native 侧：编译链接应成功（guard 是合法 IR），运行退出码 1
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr);
                TestHarness.CheckTrue(label + "：native 退出码 1", runExit == 1,
                    $"exit={runExit}");
                TestHarness.CheckTrue(label + "：native stderr 含关键字",
                    nativeErr.Contains(keyword), nativeErr);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // BIL 级对拍（前端尚未降级的合法内建形态）：手写 BIL 直接驱 VM 与
        // native，比对口径与 RunCase 相同
        private static void RunBilCase(string label, string bilText)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // VM 侧（行为参考实现）
                var vm = BilVm.Run(BilReader.Read(bilText));
                TestHarness.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                var expectedExit = vm.ReturnValue is VmI32 value ? value.Value : 0;

                // native 侧：CLI 编译 → 进程执行
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, bilText, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
                TestHarness.CheckTrue(label + "：退出码一致",
                    runExit == expectedExit, $"native={runExit} vm={expectedExit}");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // 驱动 native COMMAND 端到端，捕获 stdout/stderr（同 MiddlewareTests 模式）
        private static (int Code, string Out, string Err) RunNative(params string[] args)
        {
            if (!CommandLineParser.TryParse(args, out var result, out var error))
            {
                throw new InvalidOperationException($"测试构造的命令行应解析成功: {error}");
            }
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            try
            {
                int code = new NativeCommand().Execute(result!);
                return (code, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        }
    }
}
