using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public static partial class NativeE2ETests
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
                // 与 NativeCommand 同一 libuv 解析（预热同一份内容哈希缓存，
                // 否则并行用例会各自重建带 RIGI_HAS_LIBUV 的 bitcode）
                RigiRtBuilder.EnsureBitcode(clang, out _, LibuvResolver.Resolve(null));
            }
        }

        private static (string Label, Action Run) Case(string label, string source) =>
            (label, () => RunCase(label, source));

        private static (string Label, Action Run) BilCase(string label, string bil) =>
            (label, () => RunBilCase(label, bil));

        // L6：非 rigi_rt 库 FFI 的 native-only 用例（VM 无对应 hook，§22.5
        // 表外拒绝是定稿行为，不做 VM 对拍）——cSource 现场 clang -c 出
        // 目标文件，经 native --link 链入，断言 stdout/退出码字面量
        private static (string Label, Action Run) NativeOnlyCase(string label,
            string source, string cSource, string expectedStdout, int expectedExit,
            IReadOnlyDictionary<string, string>? env = null) =>
            (label, () => RunNativeOnlyCase(label, source, cSource,
                expectedStdout, expectedExit, env));

        private static (string Label, Action Run) FailCase(string label, string source, string needle) =>
            (label, () => RunFailCase(label, source, needle, null));

        // MW9b-G：native stderr 关键字可与 VM 消息关键字不同（reporter
        // 新格式「{类型全名}: {message}」全名前缀 VM 消息没有）
        private static (string Label, Action Run) FailCase(string label, string source,
            string needle, string nativeNeedle) =>
            (label, () => RunFailCase(label, source, needle, nativeNeedle));

        // MW12b §25.2：native stderr 断言形态——VM 参照对拍 stdout + 退出
        // 码一致（VM 侧暂无 undisposed 事件通道，stdout 不受影响），额外
        // 断言 native stderr 含/不含 needle
        private static (string Label, Action Run) NativeErrCase(string label, string source,
            string needle, bool needlePresent = true) =>
            (label, () => RunNativeErrCase(label, source, needle, needlePresent));

        // MW12c：per-case env——env 与 MemtrackEnv 合并（MEMTRACK 恒在，
        // 泄漏即 exit 1 的判定口径不可关），per-case 同名键覆盖
        private static (string Label, Action Run) EnvCase(string label, string source,
            IReadOnlyDictionary<string, string> env)
        {
            var merged = new Dictionary<string, string>(MemtrackEnv);
            foreach (var pair in env)
            {
                merged[pair.Key] = pair.Value;
            }
            return (label, () => RunCase(label, source, merged));
        }

        // 注：声明须在 Cases 之前（静态初始化按文本序，EnvCase 合并要用）
        private static readonly Dictionary<string, string> MemtrackEnv =
            new() { ["RIGI_RT_MEMTRACK"] = "1" };

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
            // 传递父接口：IC implements IA，C implements IC，经 IC 静态类型
            // 调 IA 方法。native iMap 须含 IA 条目，否则 imap lookup failed
            Case("imap 传递父接口 override 经 IC 调 IA",
                "pub interface IA {\n" +
                "    pub func f(): i32\n" +
                "}\n" +
                "pub interface IC implements IA {\n" +
                "}\n" +
                "pub class C implements IC {\n" +
                "    pub init()\n" +
                "    pub override func f(): i32 { return 42 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const ic: IC = new C()\n" +
                "    var a = ic.f()\n" +
                "    if ((a != 42)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            Case("imap 传递父接口默认方法经 IC 调 IA",
                "pub interface IA {\n" +
                "    pub func f(): i32 { return 7 }\n" +
                "}\n" +
                "pub interface IC implements IA {\n" +
                "}\n" +
                "pub class C implements IC {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const ic: IC = new C()\n" +
                "    var a = ic.f()\n" +
                "    if ((a != 7)) { return 1 }\n" +
                "    return 42\n" +
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
            Case("lambda 赋值后经变量调用",
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n"),
            Case("Action noret 间接调用",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var act = func{() -> { Console.println(\"act\") }}\n" +
                "    act()\n" +
                "    return 0\n" +
                "}\n"),
            Case("用户类自定义 operator call",
                "pub class Doubler {\n" +
                "    pub init() { }\n" +
                "    pub operator call(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Doubler()\n" +
                "    return d(21)\n" +
                "}\n"),
            Case("泛型 $$call Mapper",
                "pub class Mapper {\n" +
                "    pub init() { }\n" +
                "    pub operator call\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Mapper()\n" +
                "    return f\\<i32>(42)\n" +
                "}\n"),
            Case("Func 多态两次间接调用",
                "pub func main(): i32 {\n" +
                "    var f: Func\\<i32, i32> = func{(x: i32): i32 -> (x + 1)}\n" +
                "    var a = f(10)\n" +
                "    f = func{(x: i32): i32 -> (x * 2)}\n" +
                "    var b = f(10)\n" +
                "    return (a + b)\n" +
                "}\n"),
            Case("Any 标量 round trip",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var ai = 42 as Any\n" +
                "    var ni = ai as i32\n" +
                "    var au = 7UL as Any\n" +
                "    var nu = au as u64\n" +
                "    var af = 1.5 as Any\n" +
                "    var nf = af as double\n" +
                "    var ab = true as Any\n" +
                "    var nb = ab as bool\n" +
                "    var ac = 'A' as Any\n" +
                "    var nc = ac as char\n" +
                "    if (ni == 42) { Console.println(\"i32 ok\") }\n" +
                "    if (nu == 7UL) { Console.println(\"u64 ok\") }\n" +
                "    if (nf == 1.5) { Console.println(\"f64 ok\") }\n" +
                "    if (nb) { Console.println(\"bool ok\") }\n" +
                "    if (nc == 'A') { Console.println(\"char ok\") }\n" +
                "    return ni\n" +
                "}\n"),
            Case("Any string round trip",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var boxed = \"hello\" as Any\n" +
                "    var s = boxed as String\n" +
                "    Console.println(s)\n" +
                "    return 0\n" +
                "}\n"),
            Case("Any 大 struct round trip",
                "import core.io.Console\n" +
                "pub struct Pair {\n" +
                "    pub var a: i64\n" +
                "    pub var b: i64\n" +
                "    pub init(_ -> a, _ -> b)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var boxed = (new Pair(11L, 22L) as Any)\n" +
                "    var p = boxed as Pair\n" +
                "    if (p.a == 11L) { Console.println(\"a ok\") }\n" +
                "    if (p.b == 22L) { Console.println(\"b ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("Any 小 struct tag0 round trip",
                "import core.io.Console\n" +
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var boxed = (new Point(3, 4) as Any)\n" +
                "    var p = boxed as Point\n" +
                "    if (p.x == 3) { Console.println(\"x ok\") }\n" +
                "    if (p.y == 4) { Console.println(\"y ok\") }\n" +
                "    return ((p.x * 10) + p.y)\n" +
                "}\n"),
            Case("Any enum round trip",
                "import core.io.Console\n" +
                "pub enum struct Color {\n" +
                "    pub const code: i32\n" +
                "    pub init(_ -> code)\n" +
                "}[\n" +
                "    Red(1),\n" +
                "    Blue(2)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    var boxed = (Color.Red as Any)\n" +
                "    var e = boxed as Color\n" +
                "    if (e is .Red) { Console.println(\"enum ok\") }\n" +
                "    return e.code\n" +
                "}\n"),
            Case("Any 经函数参数 identity",
                "import core.io.Console\n" +
                "pub func identity(a: Any): Any { return a }\n" +
                "pub func main(): i32 {\n" +
                "    var boxed = identity((41 as Any))\n" +
                "    var x = boxed as i32\n" +
                "    if (x == 41) { Console.println(\"identity ok\") }\n" +
                "    return x\n" +
                "}\n"),
            // MW9b-G：native 由 abort 占位改抛真 CastException，类型名
            // 取 canonical 形态（core::Any/core::String）；关键字取两侧
            // 消息公共前缀
            FailCase("Any 拆箱类型不符",
                "pub func main(): i32 {\n" +
                "    var a = 42 as Any\n" +
                "    var s = a as String\n" +
                "    return 0\n" +
                "}\n", "无法将"),
            Case("Any toString 标量族",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(any_to_string((42 as Any)))\n" +
                "    Console.println(any_to_string((-7 as Any)))\n" +
                "    Console.println(any_to_string((18446744073709551615UL as Any)))\n" +
                "    Console.println(any_to_string((1.5 as Any)))\n" +
                "    Console.println(any_to_string((1.0f as Any)))\n" +
                "    Console.println(any_to_string((true as Any)))\n" +
                "    Console.println(any_to_string(('A' as Any)))\n" +
                "    Console.println(any_to_string((\"hi\" as Any)))\n" +
                "    return 0\n" +
                "}\n"),
            Case("Any toString 大 struct",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub struct Pair {\n" +
                "    pub var a: i64\n" +
                "    pub var b: i64\n" +
                "    pub init(_ -> a, _ -> b)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(any_to_string((new Pair(11L, 22L) as Any)))\n" +
                "    return 0\n" +
                "}\n"),
            Case("Any toString 对象默认",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub class Plain {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a: Any = new Plain()\n" +
                "    Console.println(any_to_string(a))\n" +
                "    return 0\n" +
                "}\n"),
            Case("toString override 具体类型",
                "import core.io.Console\n" +
                "pub class Point {\n" +
                "    pub init() { }\n" +
                "    pub override func toString(): String { return \"PT\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Point()\n" +
                "    Console.println(p.toString())\n" +
                "    return 0\n" +
                "}\n"),
            Case("泛型类字段与方法（i32）",
                "pub class Box2\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box2\\<i32>(7)\n" +
                "    if (b.v == 7) { b.v = 8 }\n" +
                "    return b.get()\n" +
                "}\n"),
            Case("泛型类字段与方法（string）",
                "import core.io.Console\n" +
                "pub class Box2\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box2\\<String>(\"ok\")\n" +
                "    Console.println(b.get())\n" +
                "    return 0\n" +
                "}\n"),
            Case("Box2<i32> 与 Box2<string> 共存",
                "import core.io.Console\n" +
                "pub class Box2\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var ni = new Box2\\<i32>(41)\n" +
                "    var ns = new Box2\\<String>(\"x\")\n" +
                "    Console.println(ns.get())\n" +
                "    return ni.get()\n" +
                "}\n"),
            Case("stdlib Pair 使用",
                "pub func main(): i32 {\n" +
                "    var p = new Pair\\<i32, i32>(6, 7)\n" +
                "    return (p.key + p.value)\n" +
                "}\n"),
            Case("嵌套构造 Box2<Box2<i32>>",
                "pub class Box2\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func wrap\\<T>(x: T): Box2\\<T> { return new Box2\\<T>(x) }\n" +
                "pub func main(): i32 {\n" +
                "    var first = wrap\\<i32>(9)\n" +
                "    var nest = wrap\\<Box2\\<i32> >(first)\n" +
                "    var mid = nest.get()\n" +
                "    return mid.get()\n" +
                "}\n"),
            Case("泛型方法 typeid 转发链",
                "pub func id\\<T>(x: T): T { return x }\n" +
                "pub func pass\\<T>(x: T): T { return id\\<T>(x) }\n" +
                "pub func main(): i32 {\n" +
                "    return pass\\<i32>(42)\n" +
                "}\n"),
            // ===== G1：泛型值类型构造（VM↔native 对拍）=====
            Case("泛型 struct 构造与字段方法（i32）",
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var w = new Wrap\\<i32>(7)\n" +
                "    if (w.v == 7) { w.v = 8 }\n" +
                "    return w.get()\n" +
                "}\n"),
            Case("泛型 struct 构造与字段方法（string）",
                "import core.io.Console\n" +
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var w = new Wrap\\<String>(\"ok\")\n" +
                "    Console.println(w.get())\n" +
                "    return 0\n" +
                "}\n"),
            Case("泛型 struct 开放构造（方法内 new 同型 T）",
                "pub struct WPair\\<T> {\n" +
                "    pub var a: T\n" +
                "    pub var b: T\n" +
                "    pub init(_ -> a, _ -> b)\n" +
                "    pub func swap(): WPair\\<T> { return new WPair\\<T>(this.b, this.a) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new WPair\\<i32>(1, 2)\n" +
                "    var q = p.swap()\n" +
                "    return (q.a * 10) + q.b\n" +
                "}\n"),
            Case("泛型 struct 经函数参数传递",
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "func pass(w: Wrap\\<i32>): i32 { return w.get() }\n" +
                "pub func main(): i32 {\n" +
                "    var w = new Wrap\\<i32>(9)\n" +
                "    return pass(w)\n" +
                "}\n"),
            Case("泛型 struct 类级 typeid 直通（is T / T() 标量界）",
                "pub struct Wrap\\<T extends i32> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub func get(): T { return this.v }\n" +
                "    pub func holds(x: Any): bool { return x is T }\n" +
                "    pub func makeDefault(): T { return T() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var w = new Wrap\\<i32>(7)\n" +
                "    var r = 0\n" +
                "    if (w.holds(7 as Any)) { r = 10 }\n" +
                "    if (w.holds(\"s\" as Any)) { r = 99 }\n" +
                "    var d = w.makeDefault()\n" +
                "    return (r + w.get()) + d\n" +
                "}\n"),
            Case("泛型 struct 装箱 Any 与拆回",
                "import core.io.Console\n" +
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var w = new Wrap\\<i32>(41)\n" +
                "    var a = w as Any\n" +
                "    var back = a as Wrap\\<i32>\n" +
                "    if (back.v == 41) { Console.println(\"box ok\") }\n" +
                "    return back.v\n" +
                "}\n"),
            Case("泛型 struct 动态构造（typeOf 来源）",
                "import core.io.Console\n" +
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var seed = new Wrap\\<i32>(0)\n" +
                "    var t = typeOf(seed)\n" +
                "    var w = new t(6)\n" +
                "    if (w.v == 6) { Console.println(\"dyn ok\") }\n" +
                "    return w.v\n" +
                "}\n"),
            Case("构造 struct 字段内嵌（struct 持构造 struct）",
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub struct Outer {\n" +
                "    pub var w: Wrap\\<i32>\n" +
                "    pub init(_ -> w)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var o = new Outer(new Wrap\\<i32>(5))\n" +
                "    o.w.v = 6\n" +
                "    return o.w.v\n" +
                "}\n"),
            Case("泛型 struct 用户运算符直调",
                "pub struct WPair\\<T> {\n" +
                "    pub var tag: i32\n" +
                "    pub var a: T\n" +
                "    pub var b: T\n" +
                "    pub init(_ -> tag, _ -> a, _ -> b)\n" +
                "    pub operator plus(other: WPair\\<T>): WPair\\<T> {\n" +
                "        return new WPair\\<T>((this.tag + other.tag), this.a, other.b)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new WPair\\<i32>(1, 10, 20) + new WPair\\<i32>(2, 30, 40)\n" +
                // POSIX 退出码 8 位截断：判定用小面额累加（0..255 口径）
                "    var acc = 0\n" +
                "    if (p.tag == 3) { acc = acc + 1 }\n" +
                "    if (p.a == 10) { acc = acc + 2 }\n" +
                "    if (p.b == 40) { acc = acc + 4 }\n" +
                "    return acc\n" +
                "}\n"),
            Case("泛型 struct 静态成员裸名访问（不经构造类型）",
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub static func tag(): i32 { return 3 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var w = new Wrap\\<i32>(1)\n" +
                "    return w.v + Wrap.tag()\n" +
                "}\n"),
            // ===== G4：泛型占位操作数运算的运行期派发（VM↔native 对拍）=====
            Case("占位运算：接口界 plus 派发（class 实参）",
                "pub interface Addable {\n" +
                "    operator plus(another: Addable): Addable\n" +
                "}\n" +
                "pub class Num implements Addable {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "    pub operator plus(another: Addable): Addable {\n" +
                "        return new Num(this.n + ((another as Num).n))\n" +
                "    }\n" +
                "}\n" +
                "func add\\<T extends Addable>(a: T, b: T): Addable {\n" +
                "    return a + b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const r = add\\<Num>(new Num(1), new Num(2))\n" +
                "    return ((r as Num).n)\n" +
                "}\n"),
            Case("占位运算：equals/!= 与最派生实现",
                "pub open class Base {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "    pub operator equals(other: Base): bool { return this.n == other.n }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub var extra: i32\n" +
                "    pub init(_ -> n, _ -> extra)\n" +
                "    pub operator equals(other: Base): bool { return false }\n" +
                "}\n" +
                "func eq2\\<T extends Base>(a: T, b: T): bool { return a == b }\n" +
                "func ne2\\<T extends Base>(a: T, b: T): bool { return a != b }\n" +
                "pub func main(): i32 {\n" +
                // POSIX 退出码 8 位截断：权重取小面额（0..255 口径）
                "    var acc = 0\n" +
                "    if (eq2\\<Base>(new Base(1), new Base(1))) { acc = acc + 1 }\n" +
                "    if (ne2\\<Base>(new Base(1), new Base(2))) { acc = acc + 2 }\n" +
                "    if (eq2\\<Derived>(new Derived(1, 2), new Derived(1, 2))) { acc = acc + 4 }\n" +
                "    if (ne2\\<Derived>(new Derived(1, 2), new Derived(1, 2))) { acc = acc + 8 }\n" +
                "    return acc\n" +
                "}\n"),
            Case("占位运算：compareTo 排序三态映射",
                "pub interface Ranked {\n" +
                "    operator compareTo(other: Ranked): core.ComparisonResult\n" +
                "}\n" +
                "pub class Score implements Ranked {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "    pub operator compareTo(other: Ranked): core.ComparisonResult {\n" +
                "        const d = this.n - ((other as Score).n)\n" +
                "        if (d < 0) { return .LesserThanAnother }\n" +
                "        if (d > 0) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "}\n" +
                "func lt\\<T extends Ranked>(a: T, b: T): bool { return a < b }\n" +
                "func ge\\<T extends Ranked>(a: T, b: T): bool { return a >= b }\n" +
                "pub func main(): i32 {\n" +
                "    var acc = 0\n" +
                "    if (lt\\<Score>(new Score(1), new Score(2))) { acc = acc + 1 }\n" +
                "    if (ge\\<Score>(new Score(2), new Score(2))) { acc = acc + 2 }\n" +
                "    if (lt\\<Score>(new Score(3), new Score(2))) { acc = acc + 100 }\n" +
                "    return acc\n" +
                "}\n"),
            Case("占位运算：一元 opposite（struct 界直调）",
                "pub struct VNum {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "    pub operator opposite(): VNum { return new VNum(0 - this.n) }\n" +
                "}\n" +
                "func neg\\<T extends VNum>(a: T): VNum { return -a }\n" +
                "pub func main(): i32 {\n" +
                "    const r = neg\\<VNum>(new VNum(5))\n" +
                // POSIX 退出码 8 位截断：显式判定转正（-5 在 Linux 会被截成 251）
                "    if (r.n == -5) { return 5 }\n" +
                "    return 1\n" +
                "}\n"),
            Case("占位运算：struct 界 plus（值类型接收者拆箱直调）",
                "pub struct VNum {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "    pub operator plus(other: VNum): VNum {\n" +
                "        return new VNum(this.n + other.n)\n" +
                "    }\n" +
                "}\n" +
                "func add\\<T extends VNum>(a: T, b: T): VNum { return a + b }\n" +
                "pub func main(): i32 {\n" +
                "    const r = add\\<VNum>(new VNum(3), new VNum(4))\n" +
                "    return r.n\n" +
                "}\n"),
            // 缺陷 1 回归（B-2 遗留）：值类型方法 + 泛型占位构造接收者
            // ——place 链物化的接收者槽保持占位类型，native 调用点拆箱
            // 适配（修复前胖值位模式直传当内联值指针用，读出垃圾）
            // 缺陷 3 回归（MW12 遗留 pre-existing 0xC0000409）：
            // RangeI32 for-loop——abstract 基类声明接口方法的 abstract
            // override，具体子类实现；native vtable 接口实现段别名槽须
            // 随 override 一并替换（修复前滞留抽象基员 null 槽，接口
            // 派发经 baseTypeId 链命中段基址调空槽）
            Case("RangeI32 for-loop（抽象基类 + 构造接口派发）",
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    for (i in 0 to 4) {\n" +
                "        sum = (sum + i)\n" +
                "    }\n" +
                "    if ((sum != 6)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 缺陷 3 根因最小形态（非泛型）：abstract override + 子类
            // 实现 + 接口派发
            Case("接口派发：abstract override 别名槽随子类覆盖",
                "pub interface IE {\n" +
                "    func m(): i32\n" +
                "}\n" +
                "pub abstract class AbsN implements IE {\n" +
                "    pub abstract override func m(): i32\n" +
                "}\n" +
                "pub class CN : AbsN {\n" +
                "    pub override func m(): i32 { return 42 }\n" +
                "}\n" +
                "func callIt(e: IE): i32 { return e.m() }\n" +
                "pub func main(): i32 {\n" +
                "    if ((callIt(new CN()) != 42)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 缺陷 3 根因最小形态（泛型抽象基类 + 构造接口，RangeEnumerator
            // 同构）
            Case("接口派发：泛型抽象基类 abstract override 子类覆盖",
                "pub interface IE\\<T> {\n" +
                "    func m(): i32\n" +
                "}\n" +
                "pub abstract class AbsG\\<T> implements IE\\<T> {\n" +
                "    pub abstract override func m(): i32\n" +
                "}\n" +
                "pub class CG : AbsG\\<i32> {\n" +
                "    pub override func m(): i32 { return 42 }\n" +
                "}\n" +
                "func callIt(e: IE\\<i32>): i32 { return e.m() }\n" +
                "pub func main(): i32 {\n" +
                "    if ((callIt(new CG()) != 42)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 缺陷 2 回归（L7 遗留）：全链无 init 声明的零参 new —
            // native 构造面不再报「new/super 无匹配 init」（VM
            // TryFindInit「无 init 声明 + 零实参仍构造」同口径）
            Case("无 init 子类：零参构造（全链无 init）",
                "pub open class Base {\n" +
                "}\n" +
                "pub class Sub : Base {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Sub()\n" +
                "    return 42\n" +
                "}\n"),
            // 缺陷 2 回归：多层继承全链无 init
            Case("无 init 子类：三层继承链零参构造",
                "pub open class A {\n" +
                "}\n" +
                "pub open class B : A {\n" +
                "}\n" +
                "pub class C : B {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new C()\n" +
                "    return 42\n" +
                "}\n"),
            // 缺陷 2 回归：基类仅有参 init + 字段带声明初始值——子类
            // 隐式默认构造不调基类 init 体，字段初值由 ..init.wrapper
            // 缝合（§9.3/§9.7）
            Case("无 init 子类：基类仅有参 init 字段初值缝合",
                "pub open class Base {\n" +
                "    pub var x: i32 = 5\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub class Sub : Base {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Sub()\n" +
                "    if ((s.x != 5)) { return 1 }\n" +
                "    var t = new Base(9)\n" +
                "    if ((t.x != 9)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            Case("占位接收者：值类型方法经泛型宿主字段链直调",
                "pub struct VNum {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub func doubled(): i32 { return (this.v + this.v) }\n" +
                "}\n" +
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "func useIt\\<T extends VNum>(b: Box\\<T>): i32 { return b.item.doubled() }\n" +
                "pub func main(): i32 {\n" +
                "    const r = useIt\\<VNum>(new Box\\<VNum>(new VNum(21)))\n" +
                "    if ((r != 42)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 缺陷 1 回归：变异方法经占位接收者——callee 对 this 的修
            // 改须重装箱写回占位槽，再经写回链落进宿主字段（VM 原地
            // 生效语义）
            Case("占位接收者：值类型变异方法写回",
                "pub struct VNum {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub func bump() { this.v = (this.v + 1) }\n" +
                "}\n" +
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "func useIt\\<T extends VNum>(b: Box\\<T>) { b.item.bump() }\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<VNum>(new VNum(21))\n" +
                "    useIt\\<VNum>(b)\n" +
                "    if ((b.item.v != 22)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 缺陷 1 回归：占位值类型宿主上的字段读写（.generic.* 槽
            // 装的内联值类型盒——修复前胖值 payload 直当对象指针寻
            // 址，tag0 内联盒必 AV）
            Case("占位接收者：值类型宿主字段读写",
                "pub struct VNum {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "func readIt\\<T extends VNum>(b: Box\\<T>): i32 {\n" +
                "    var x = b.item\n" +
                "    return x.v\n" +
                "}\n" +
                "func writeIt\\<T extends VNum>(b: Box\\<T>) { b.item.v = 9 }\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<VNum>(new VNum(21))\n" +
                "    const r = readIt\\<VNum>(b)\n" +
                "    if ((r != 21)) { return 1 }\n" +
                "    writeIt\\<VNum>(b)\n" +
                "    if ((b.item.v != 9)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // 落空负例（BIL 级）：界承诺的 operator 在实际类型上缺失——
            // VM 抛「没有用户 operator plus：Plain」；native 沿候选链全
            // 落空抛 core.NoSuchMethodException（同为未捕获出口 exit 1）
            ("占位运算落空：无 operator（BIL 级）",
                () => RunBilFailCase("占位运算落空：无 operator（BIL 级）",
                    BuildGenericOpMissBil(), "没有用户 operator", "NoSuchMethodException")),
            // G1 enum 半边：frontend S11 不发射泛型 enum case，BIL 级
            // 手写对拍（VM NewCase 经 ResolveTypeRef 具体化同语义）
            BilCase("泛型 enum case 构造（BIL 级对拍）",
                "BIL \"1.1\"\n\nMetadata {\n}\n\nResources {\n    R_0 = i32 7\n    R_1 = i32 1\n}\n\n" +
                "LocalSymbols {\n" +
                "    .type Choice = enum-struct generic(T) pub {\n" +
                "        .field Choice#tag@.i32 pub var\n" +
                "        .field Choice#payload@.generic<$.generic.T> pub var\n" +
                "        .method Choice$init(tag:.i32,payload:.generic<$.generic.T>)@.void pub init\n" +
                "        .case Choice.Some(tag:.i32,payload:.generic<$.generic.T>) discriminant auto\n" +
                "    }\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n\nExternalSymbols {\n}\n\n" +
                "fn(Choice$init(tag:.i32,payload:.generic<$.generic.T>)@.void) {\n" +
                "    .args {\n" +
                "        .return = .void,\n" +
                "        .this = Choice,\n" +
                "        .generic.T = .typeid,\n" +
                "        tag = .i32,\n" +
                "        payload = .generic<$.generic.T>\n" +
                "    }\n" +
                "    .vars {\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        set.field $tag $.this field(Choice#tag@.i32)\n" +
                "        set.field $payload $.this field(Choice#payload@.generic<$.generic.T>)\n" +
                "        ret\n" +
                "    }\n" +
                "}\n\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        Choice<.i32> c,\n" +
                "        .i32 .t0,\n" +
                "        .i32 .t1,\n" +
                "        .i32 .t2,\n" +
                "        .i32 .t3\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_0) $.t0\n" +
                "        load res(R_1) $.t1\n" +
                "        new.case type(Choice<.i32>) case(Choice.Some) $c [$.t1, $.t0]\n" +
                "        get.field $c $.t2 field(Choice#tag@.i32)\n" +
                "        get.field $c $.t3 field(Choice#payload@.generic<$.generic.T>)\n" +
                "        add $.t2 $.t3 $.t0\n" +
                "        ret $.t0\n" +
                "    }\n" +
                "}\n"),
            Case("泛型类经构造基类多虚派发",
                "pub open class PairV\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub open func foo(): i32 { return 1 }\n" +
                "    pub open func bar(): i32 { return 2 }\n" +
                "}\n" +
                "pub class PairD\\<T> : PairV\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub override func foo(): i32 { return 10 }\n" +
                "    pub override func bar(): i32 { return 20 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var x: PairV\\<i32> = new PairD\\<i32>()\n" +
                "    return (x.foo() + x.bar())\n" +
                "}\n"),
            Case("泛型类实现泛型接口",
                "pub interface IBox\\<T> {\n" +
                "    func get(): T\n" +
                "    func tag(): i32\n" +
                "    func extra(): i32\n" +
                "}\n" +
                "pub class Box3\\<T> implements IBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub override func get(): T { return this.v }\n" +
                "    pub override func tag(): i32 { return 7 }\n" +
                "    pub override func extra(): i32 { return 2 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Box3\\<i32>(41)\n" +
                "    var b: IBox\\<i32> = c\n" +
                // 返回值须 <256：linux 进程退出码 8-bit 截断，对拍断言的是
                // 跨平台可观察一致（4172 在 linux 只剩 76）
                "    if (((c.get() * 100) + ((b.tag() * 10) + b.extra())) == 4172) { return 42 }\n" +
                "    return 0\n" +
                "}\n"),
            Case("is 类继承命中/不命中",
                "import core.io.Console\n" +
                "pub open class Animal { pub init() { } }\n" +
                "pub class Dog : Animal { pub init() { } }\n" +
                "pub class Cat : Animal { pub init() { } }\n" +
                "pub func main(): i32 {\n" +
                "    var d: Animal = new Dog()\n" +
                "    var c: Animal = new Cat()\n" +
                "    if (d is Dog) { Console.println(\"dog hit\") }\n" +
                "    if (d is Animal) { Console.println(\"animal hit\") }\n" +
                "    if (d is Cat) { Console.println(\"BAD cat\") }\n" +
                "    if (c is Cat) { Console.println(\"cat hit\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("is 接口判定",
                "import core.io.Console\n" +
                "pub interface Named { func name(): String }\n" +
                "pub class Dog implements Named {\n" +
                "    pub init() { }\n" +
                "    pub override func name(): String { return \"d\" }\n" +
                "}\n" +
                "pub class Plain { pub init() { } }\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Dog()\n" +
                "    var p = new Plain()\n" +
                "    if (d is Named) { Console.println(\"iface hit\") }\n" +
                "    if (p is Named) { Console.println(\"BAD iface\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("is 对 Any 装箱值",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a: Any = 42\n" +
                "    if (a is i32) { Console.println(\"i32 hit\") }\n" +
                "    if (a is String) { Console.println(\"BAD str\") }\n" +
                "    var s: Any = \"hi\"\n" +
                "    if (s is String) { Console.println(\"str hit\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("is.indirect 泛型体内",
                "import core.io.Console\n" +
                "pub open class Animal { pub init() { } }\n" +
                "pub class Dog : Animal { pub init() { } }\n" +
                "pub func check\\<T>(x: Animal): bool { return x is T }\n" +
                "pub func main(): i32 {\n" +
                "    var d: Animal = new Dog()\n" +
                "    if (check\\<Dog>(d)) { Console.println(\"gen dog\") }\n" +
                "    if (check\\<Animal>(d)) { Console.println(\"gen animal\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("supers 类继承逆变",
                "import core.io.Console\n" +
                "pub open class Animal { pub init() { } }\n" +
                "pub class Dog : Animal { pub init() { } }\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Animal()\n" +
                "    var d = new Dog()\n" +
                "    if (a supers Dog) { Console.println(\"animal supers dog\") }\n" +
                "    if (d supers Animal) { Console.println(\"BAD dog supers animal\") }\n" +
                "    if (a supers Animal) { Console.println(\"animal supers animal\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("is/supers 多 implements 接口闭包",
                "import core.io.Console\n" +
                "pub interface IA { }\n" +
                "pub interface IB { }\n" +
                "pub interface IC implements IA, IB { }\n" +
                "pub class C implements IC { pub init() }\n" +
                "pub class Plain { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var c = new C()\n" +
                "    var p = new Plain()\n" +
                "    if (c is IA) { Console.println(\"c is IA\") }\n" +
                "    if (c is IB) { Console.println(\"c is IB\") }\n" +
                "    if (c is IC) { Console.println(\"c is IC\") }\n" +
                "    if (p is IA) { Console.println(\"BAD p is IA\") }\n" +
                "    var ia: IA = c\n" +
                "    if (ia supers C) { Console.println(\"ia supers C\") }\n" +
                "    if (ia supers Plain) { Console.println(\"BAD ia supers Plain\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("接口默认方法未 override",
                "import core.io.Console\n" +
                "pub interface Shape {\n" +
                "    pub func area(): i32\n" +
                "    pub func describe(): String { return \"default\" }\n" +
                "}\n" +
                "pub class Sq implements Shape {\n" +
                "    pub var s: i32\n" +
                "    pub init(n: i32) { s = n }\n" +
                "    pub override func area(): i32 { return (s * s) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var sh: Shape = new Sq(3)\n" +
                "    Console.println(sh.describe())\n" +
                "    return sh.area()\n" +
                "}\n"),
            Case("泛型接口默认方法",
                "import core.io.Console\n" +
                "pub interface IBox\\<T> {\n" +
                "    func get(): T\n" +
                "    func tag(): i32 { return 7 }\n" +
                "}\n" +
                "pub class Box3\\<T> implements IBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub override func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Box3\\<i32>(41)\n" +
                "    var b: IBox\\<i32> = c\n" +
                "    if (b.tag() == 7) { Console.println(\"tag 7\") }\n" +
                // 返回值须 <256：linux 进程退出码 8-bit 截断，对拍断言的是
                // 跨平台可观察一致（4107 在 linux 只剩 11）
                "    if (((c.get() * 100) + b.tag()) == 4107) { return 42 }\n" +
                "    return 0\n" +
                "}\n"),
            Case("接口默认方法类 override",
                "import core.io.Console\n" +
                "pub interface Shape {\n" +
                "    pub func area(): i32\n" +
                "    pub func describe(): String { return \"default\" }\n" +
                "}\n" +
                "pub class Lbl implements Shape {\n" +
                "    pub var label: String\n" +
                "    pub init() { label = \"L\" }\n" +
                "    pub override func area(): i32 { return 7 }\n" +
                "    pub override func describe(): String { return label }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var sh: Shape = new Lbl()\n" +
                "    Console.println(sh.describe())\n" +
                "    return sh.area()\n" +
                "}\n"),
            Case("with wrapper 修饰",
                "import core.io.Console\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mark {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Mark\n" +
                "pub class Tagged { pub init() }\n" +
                "pub class Plain { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var t = new Tagged()\n" +
                "    var p = new Plain()\n" +
                "    if (t with Mark) { Console.println(\"with hit\") }\n" +
                "    if (p with Mark) { Console.println(\"BAD with\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("wrapper Entity 字段读写",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: i32\n" +
                "    pub init() { level = 7 }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    if (s:Logged.level != 7) { return 1 }\n" +
                "    s:Logged.level = 42\n" +
                "    return s:Logged.level\n" +
                "}\n"),
            Case("wrapper Entity 方法调用",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: i32\n" +
                "    pub init() { level = 11 }\n" +
                "    pub func dump(): i32 { return level }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s:Logged.dump()\n" +
                "}\n"),
            Case("wrapper Entity specific proxy（self + inner）",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): i32 {\n" +
                "        var host = self\n" +
                "        return (inner(arg) + 1)\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): i32 { return arg }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.doSomething(5)\n" +
                "}\n"),
            // MW10 刀3c：proxy 环 receiver 原地访问宿主隐藏槽（§14.5）——
            // wrapper 可变状态跨调用持久（VM 为基准；用例①转录自
            // BilVmTests.TestEntityProxyStatePersists）
            Case("wrapper Entity specific proxy 状态跨调用持久",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(10)\n" +
                "    var b = s.fetch(10)\n" +
                "    if (((a == 11) and (b == 12))) { return 1 } else { return 0 }\n" +
                "}\n"),
            // Entity specific .proxy.get.<名>：TField 占位源赋给 Any，烘焙
            // extraSubst 后静态 .i32 → .any 须装箱（与 Method inner 经 Any
            // 同根因）
            Case("wrapper Entity specific get 经 Any 往返",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.hp\\<TField>(value: TField): TField {\n" +
                "        var a: Any = value\n" +
                "        return (a as TField)\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub class Hero {\n" +
                "    pub var hp: i32\n" +
                "    pub init() { hp = 41 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    var a = h.hp\n" +
                "    if ((a != 41)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 遗3 用例①：基类 @W 有状态 wrapper，子类按 §14.9 重申覆盖，
            // 子类实例调基类未 override 方法——VM HiddenEntityKey 仅含
            // wrapper TypeRef（重申覆盖同一键），native 物理槽同归首次
            // 声明（最基类）偏移，计数跨调用递增。init 置 100 起计以区分
            // 「读到未安装的零槽」（零槽原地写回会伪造递增假象）：分歧前
            // 环读基类零槽得 11/12，对拍必败
            Case("wrapper Entity 重申覆盖状态跨调用持久（子类走基类链）",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 100 }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Child()\n" +
                "    var a = c.fetch(10)\n" +
                "    var b = c.fetch(10)\n" +
                "    if (((a == 111) and (b == 112))) { return 42 } else { return 0 }\n" +
                "}\n"),
            // 遗3 用例②（回归）：三级重申链——Leaf 实例经 Mid 静态类型
            // 调 Base 未 override 方法，环读/安装同归 Base 槽（下探越过
            // 无自有槽的 Mid）；Base 实例直调同槽不回归
            Case("wrapper Entity 三级重申链经中间基类读槽",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 100 }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "@Counting\n" +
                "pub open class Mid : Base {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Leaf : Mid {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m: Mid = new Leaf()\n" +
                "    var a = m.fetch(10)\n" +
                "    var b = m.fetch(10)\n" +
                "    var s = new Base()\n" +
                "    var c = s.fetch(10)\n" +
                "    var d = s.fetch(10)\n" +
                "    if ((((a == 111) and (b == 112)) and ((c == 111) and (d == 112)))) {\n" +
                "        return 42\n" +
                "    } else { return 0 }\n" +
                "}\n"),
            // 遗3 用例③（加压 o2）：Method wrapper 槽钥匙归方法声明类
            // （VM HiddenMethodKey 含方法符号天然唯一）——子类实例经基类
            // 静态类型调继承方法两次，wrapper 状态跨调用持久（刀6 既有
            // 归一口径的加压锁死）
            Case("Method wrapper 继承方法槽归一状态持久（基类静态类型）",
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
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    @Counted\n" +
                "    pub open func work(x: i32): i32 { return (x * 10) }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b: Base = new Child()\n" +
                "    var a = b.work(1)\n" +
                "    var c = b.work(1)\n" +
                "    if (((a == 11) and (c == 12))) { return 42 } else { return 0 }\n" +
                "}\n"),
            // 刀3c 用例②：字段-Value wrapper 链环内状态持久（.proxy.set
            // 计数，二次写读到 1，三次写读到 2）
            Case("wrapper Value 链环内状态跨调用持久",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper SetCount {\n" +
                "    pub var sets: i32\n" +
                "    pub init() { sets = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        sets = (sets + 1)\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @SetCount\n" +
                "    pub var hp: i32\n" +
                "    pub init() { hp = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 10\n" +
                "    var one = h.hp:SetCount.sets\n" +
                "    h.hp = 20\n" +
                "    var two = h.hp:SetCount.sets\n" +
                "    if ((((one == 1) and (two == 2)) and (h.hp == 20))) {\n" +
                "        return 1\n" +
                "    } else { return 0 }\n" +
                "}\n"),
            // 刀3c 用例③：wildcard 环状态持久（每次拦截累加，读回 2）
            Case("wrapper Entity wildcard 环状态跨调用持久",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WCount {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        calls = (calls + 1)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WCount\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(1)\n" +
                "    var b = s.ping(2)\n" +
                "    if ((((a == 1) and (b == 2)) and (s:WCount.calls == 2))) {\n" +
                "        return 1\n" +
                "    } else { return 0 }\n" +
                "}\n"),
            // MW10 刀3a：wildcard Entity 方法 proxy 链（VM↔native 对拍，
            // 用例转录/改写自 BilVmTests wrapper 段）
            // ===== MW10 刀6：Method wrapper（.proxy.call）链 =====
            // ① 虚/接口/中间层静态类型调用全命中（转录
            // Tests/e2e/rigi/o1_method_wrapper_virtual_dispatch.rg——
            // trampoline 在实现槽 fn，vtable/iMap 派发自然命中）
            Case("Method wrapper 虚/接口派发命中实现槽",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 2 }\n" +
                "}\n" +
                "pub interface Work {\n" +
                "    func work(): i32\n" +
                "}\n" +
                "pub class Job implements Work {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 3 }\n" +
                "}\n" +
                "pub open class Mid : Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 2 }\n" +
                "}\n" +
                "pub class Leaf : Mid {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 3 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b: Base = new Child()\n" +
                "    core.io.Console.println(\"${b.work()}\")\n" +
                "    const w: Work = new Job()\n" +
                "    core.io.Console.println(\"${w.work()}\")\n" +
                "    const m: Mid = new Leaf()\n" +
                "    core.io.Console.println(\"${m.work()}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ② 继承方法 wrapper（转录 o2_inherited_method_wrapper.rg——
            // 子类 ..init.wrapper 闭包缝合安装，槽钥匙归声明类）
            Case("Method wrapper 继承方法命中（子类不 override）",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"${new Child().work()}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ③a specific 双层 outer→inner 序（转录 BilVmTests
            // TestMethodWrapperDoubleLayerOrder）
            Case("Method wrapper specific 双层 outer→inner 序",
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
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @A\n" +
                "    @B\n" +
                "    pub func fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "        return x\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(42)\n" +
                "}\n"),
            // ③b specific 环绕 + inner 改参/改返回值（转录
            // TestMethodWrapperCallSpecificSurrounds）
            Case("Method wrapper specific 环绕改参",
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
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "        return (x * 2)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(21)\n" +
                "}\n"),
            // 占位源 inner 结果经 extraSubst 落 i32 后静态 cast → .any：
            // CastLowering 留下 MirCast（非 BoxAny），native 须装箱而非
            // EmitFail（CastException: .i32 → .any）
            Case("Method wrapper specific inner 经 Any 往返",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        var a: Any = inner(x)\n" +
                "        return (a as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed()\n" +
                "    pub func fetch(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(21)\n" +
                "    if ((a != 42)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // ④ wildcard .proxy.call：.name = 实现槽 canonical（转录
            // TestMethodWrapperWildcardInnerFullShape）
            Case("Method wrapper wildcard .name = 实现槽 canonical",
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
                "}\n"),
            // ④b wildcard 在双层链中间（specific 外环打包进 wildcard
            // 内环；转录改写自 TestWildcardInnerMiddleOfWrapperChain 的
            // Method 面）
            Case("Method wrapper specific→wildcard 混合链",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper WOut {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"out\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper WIn {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(\"in:\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @WOut\n" +
                "    @WIn\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n"),
            // ⑤ 静态方法经 companion（转录
            // TestMethodWrapperStaticViaCompanion；companion 实例 fn 被
            // trampoline，静态壳调它自然命中）
            Case("Method wrapper 静态方法经 companion",
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub class Calc {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub static func total(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return Calc.total(41)\n" +
                "}\n"),
            // ⑥ 全局函数经 ..globals.host（转录
            // TestGlobalMethodWrapperEndToEnd）
            Case("Method wrapper 全局函数经 globals.host",
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "@Trace()\n" +
                "pub func heavy(): i32 { return 21 }\n" +
                "pub func main(): i32 {\n" +
                "    var r = heavy()\n" +
                "    return (r * 2)\n" +
                "}\n"),
            // ⑦ lambda 头 Method wrapper 经 invoke.indirect（转录
            // TestLambdaMethodWrapperEndToEnd 的环绕例 + wildcard 例的
            // .name 合成符号）
            Case("Method wrapper lambda 经 invoke.indirect",
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
                "}\n"),
            Case("Method wrapper lambda wildcard .name 合成符号",
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
                "}\n"),
            // ⑧ 环内 wrapper 状态跨调用持久（转录
            // TestMethodWrapperStatePersists——刀3c 槽地址原地访问语义）
            Case("Method wrapper 环内状态跨调用持久",
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
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Counted\n" +
                "    pub func fetch(x: i32): i32 { return (x * 10) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(1)\n" +
                "    var b = s.fetch(1)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 11)) { return 1 }\n" +
                "    if ((b != 12)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ⑧b 具名包乱序还原（遗留12 任务①，对齐 VM
            // UnboxNamedArgs）：wildcard 环模板调换包内 Pair 次序后经
            // inner 转发，链末按名还原——a/b 不得错位（VM 按名读取同
            // 口径；按位还原会得到 (b - a) = -7）
            Case("Method wrapper wildcard 具名包乱序按名还原",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Swap {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        var p0 = (args[0] if? new Pair\\<String, Any>(\"\", (0 as Any)))\n" +
                "        var p1 = (args[1] if? new Pair\\<String, Any>(\"\", (0 as Any)))\n" +
                "        args[0] = p1\n" +
                "        args[1] = p0\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Swap\n" +
                "    pub func sub(a: i32, b: i32): i32 { return (a - b) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.sub(10, 3)\n" +
                "}\n"),
            // ⑧c super 绕过 Method wrapper 链（遗留12 任务②，转录
            // TestSuperBypassesMethodWrapper）：override 体内 super()
            // 直落基类原始实现，不触发基类方法键上的 wrapper——仅外层
            // 一次 trace（VM ResolveSuper 直接压帧同口径）
            Case("super 绕过 Method wrapper 链",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return (super() + 10) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Child().work()\n" +
                "}\n"),
            // ⑧d super 绕过 Entity wrapper 链（同口径）：基类 Entity
            // wrapper 的 .proxy.work 不得经 super 触发
            Case("super 绕过 Entity wrapper 链",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.work(): i32 {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "@Trace()\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "@Trace()\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    pub override func work(): i32 { return (super() + 10) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Child().work()\n" +
                "}\n"),
            // ===== MW10 刀6b：Method wrapper wildcard 改写 .name
            // 重路由（VM ResolveInner/RerouteWildcardInner 同口径）=====
            // ⑨e 基本重路由命中：proxy 体覆写 .name 形参（前端/验证器
            // 放行——保留首参操作数名恒等、值可改写），hit/miss 分派
            // miss 进 $.mw.mwr router → other 原名 fn（未被烘焙）；VM
            // 侧帧符号改写后链末 InvokeResolved 同落点
            Case("Method wrapper wildcard 改写 .name 重路由命中",
                "import core.io.Console\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        .name = \"Service$other(x:.i32)@.i32\"\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "    pub func other(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var r = s.fetch(1)\n" +
                "    Console.println(\"r=\" + r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ⑨f miss 双侧口径：改写目标不存在——VM 链末
            // InvokeResolved 抛基础设施级 VmException「找不到 fn 定义」
            // （用户不可捕获）；native router 全不中抛
            // core.NoSuchMethodException「未路由的降级请求：」（Entity
            // router 同文案）——双侧 exit 1、stdout 一致，各自关键字
            FailCase("Method wrapper wildcard 改写 .name 未路由",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        .name = \"Service$ghost(x:.i32)@.i32\"\n" +
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
                "    return s.fetch(1)\n" +
                "}\n", "找不到 fn 定义", "未路由的降级请求"),
            // ⑨g 多层环组合：外层 wildcard 改写 .name、内层 wildcard
            // 原样透传（fetch 与 other 同装 WIn——VM 内层环按改写后
            // 符号取槽，无状态 wrapper 双端可观察一致）；改写经透传
            // 路径进内层环，环序 out→in、内层看到改写后符号、落点
            // other 原始体
            Case("Method wrapper 双层 wildcard 改写 .name 剩余环序",
                "import core.io.Console\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper WOut {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        Console.println(\"out:\" + .name)\n" +
                "        .name = \"Service$other(x:.i32)@.i32\"\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper WIn {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        Console.println(\"in:\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @WOut\n" +
                "    @WIn\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "    @WIn\n" +
                "    pub func other(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var r = s.fetch(1)\n" +
                "    Console.println(\"r=\" + r.toString())\n" +
                "    var r2 = s.other(2)\n" +
                "    Console.println(\"r2=\" + r2.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ⑨h 落点绕过目标自身 wrapper 链（VM FinishChain 链末
            // InvokeResolved 同口径）：other 自挂 Trace（specific），
            // 重路由落点 = other 的 $.mwrapped. 最深层原始体——fetch(1)
            // 经 Rer 改写后只有 "rer" 一次打印（无 "trace"）；直调
            // other(2) 正常触发 Trace
            Case("Method wrapper 改写 .name 落点绕过目标自身链",
                "import core.io.Console\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Rer {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        Console.println(\"rer\")\n" +
                "        .name = \"Service$other(x:.i32)@.i32\"\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        Console.println(\"trace\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Rer\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "    @Trace\n" +
                "    pub func other(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var r = s.fetch(1)\n" +
                "    Console.println(\"r=\" + r.toString())\n" +
                "    var r2 = s.other(2)\n" +
                "    Console.println(\"r2=\" + r2.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW10 刀5：singleton 运行时（VM 为基准）=====
            // ① 基本语义：两次 new 同一实例、状态共享（VM §8.7）
            Case("singleton 基本语义（两次 new 同一实例）",
                "pub shared singleton class S {\n" +
                "    pub var v: i32\n" +
                "    pub init() { v = 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new S()\n" +
                "    var b = new S()\n" +
                "    b.v = (a.v + 1)\n" +
                "    return new S().v\n" +
                "}\n"),
            // ② 急切初始化：用户 singleton init 副作用先于 main 首句；
            // 从不触达的 singleton 同样急切初始化
            Case("singleton 急切初始化先于 main",
                "import core.io.Console\n" +
                "pub shared singleton class Boot {\n" +
                "    pub var answer: i32\n" +
                "    pub init() {\n" +
                "        answer = 42\n" +
                "        Console.println(\"boot init\")\n" +
                "    }\n" +
                "}\n" +
                "pub shared singleton class Idle {\n" +
                "    pub init() { Console.println(\"idle init\") }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"main first\")\n" +
                "    return new Boot().answer\n" +
                "}\n"),
            // ②b companion 急切初始化：静态 wrapped 字段的 cell/wrapper
            // 构造副作用先于 main（companion 不被显式 new）
            Case("companion 急切初始化（cell wrapper 构造先于 main）",
                "import core.io.Console\n" +
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper Trace {\n" +
                "    pub init() { Console.println(\"wrapper init\") }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @Trace\n" +
                "    pub static var level: i32 = 5\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"main first\")\n" +
                "    return Holder.level\n" +
                "}\n"),
            // ③ init 互访递归触发：A 的 init 用 B → B 恰好构造一次
            Case("singleton init 互访递归触发",
                "import core.io.Console\n" +
                "pub shared singleton class A {\n" +
                "    pub var b: i32\n" +
                "    pub init() {\n" +
                "        Console.println(\"A init\")\n" +
                "        b = (new B().value + 1)\n" +
                "    }\n" +
                "}\n" +
                "pub shared singleton class B {\n" +
                "    pub var value: i32\n" +
                "    pub init() {\n" +
                "        Console.println(\"B init\")\n" +
                "        value = 41\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"main first\")\n" +
                "    return new A().b\n" +
                "}\n"),
            // ④ 构造环：急切初始化期互引 → 异常（自定义 runner：VM 侧
            // 该异常是基础设施级 VmException，BilVm.Run 直接抛出，不能走
            // RunFailCase 的 BilVmResult.Exception 通道）
            ("singleton 构造环抛异常（急切初始化期）", () => RunSingletonCycleCase()),
            // ⑤ 静态字段 Value wrapper：companion cell 读写经 proxy 链
            //（playground/mw10_probe.rg 的 Holder.level 形态）
            Case("静态字段 Value wrapper（companion cell 链）",
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var floor: i32\n" +
                "    pub init(_ -> floor)\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp(0)\n" +
                "    pub static var level: i32 = 5\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Holder.level = (Holder.level + 1)\n" +
                "    return Holder.level\n" +
                "}\n"),
            // ⑥ 全局字段 Value wrapper：cell 即 singleton（带 wrapper 实参，
            // 实参在 cell ..init.wrapper 体内求值）
            Case("全局字段 Value wrapper（cell 单例）",
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init(_ -> min, _ -> max)\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "@SClamp(40, 2)\n" +
                "var h: i32 = 40\n" +
                "pub func main(): i32 {\n" +
                "    h = (h + 1)\n" +
                "    return h\n" +
                "}\n"),
            Case("wrapper Entity wildcard 拦截改返与原样转发",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        if (symbol == \"Service$zap(x:.i32)@.i32\") {\n" +
                "            return (99 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "    pub func zap(x: i32): i32 { return x }\n" +
                "    pub func poke() { core.io.Console.println(\"poke\") }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.poke()\n" +
                "    var a = s.ping(41)\n" +
                "    var b = s.zap(1)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 42)) { return 1 }\n" +
                "    if ((b != 99)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            Case("wrapper Entity 同层 specific 压 wildcard",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mix {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 {\n" +
                "        core.io.Console.println(\"specific\")\n" +
                "        return (inner(x) + 1)\n" +
                "    }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"wild\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Mix\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return x }\n" +
                "    pub func pong(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(1)\n" +
                "    var b = s.pong(1)\n" +
                "    return ((a * 10) + b)\n" +
                "}\n"),
            Case("wrapper Entity 双层 specific→wildcard 顺序",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WOuter {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 {\n" +
                "        core.io.Console.println(\"outer\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WInner {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"inner\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
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
                "}\n"),
            Case("wrapper Entity 双层 wildcard→wildcard 顺序",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"A\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"B\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n"),
            Case("wrapper Entity wildcard 改写 symbol 重路由",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Service$ping(x:.i32)@.i32\") {\n" +
                "            symbol = \"Service$pong(x:.i32)@.i32\"\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "        if (symbol == \"Service$zap(x:.i32)@.i32\") {\n" +
                "            return (99 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "    pub func pong(x: i32): i32 { return (x * 10) }\n" +
                "    pub func zap(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(5)\n" +
                "    var b = s.zap(1)\n" +
                "    var c = s.pong(2)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 50)) { return 1 }\n" +
                "    if ((b != 99)) { return 2 }\n" +
                "    if ((c != 20)) { return 3 }\n" +
                "    return 42\n" +
                "}\n"),
            // 遗6：泛型宿主成员经 wildcard 的完整烘焙（VM↔native 对拍。
            // VM 语义：方法级 typeid 隐藏实参随值实参同装箱进 unnamed
            // 位置包（声明序居值参前），环末解包恢复作隐藏形参调原始
            // 泛型体；.generic.TUnnamedArgs 类型包恒空不承载 typeid）
            // ①+② 泛型方法经 wildcard 被拦截（打印 symbol + inner 原样
            // 转发、T 推断正确）+ 返回 T 的装箱往返值正确
            Case("wrapper Entity wildcard 泛型方法拦截原样转发",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tracer {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Tracer\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func pick\\<T>(x: T): T { return x }\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.pick\\<i32>(41)\n" +
                "    var b = s.ping(1)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 41)) { return 1 }\n" +
                "    if ((b != 2)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ②b 泛型调用的 unnamed 包 = [typeid, 值]（proxy 可观察包长
            // 并拦截改返；非泛型 ping 包长 1 原样转发）
            Case("wrapper Entity wildcard 泛型方法包首 typeid 可观察",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tracer3 {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (unnamedArgs.length == 2) {\n" +
                "            return (7 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Tracer3\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func pick\\<T>(x: T): T { return x }\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.pick\\<i32>(41)\n" +
                "    var b = s.ping(1)\n" +
                "    if (a == 7) {\n" +
                "        core.io.Console.println(\"pack2-intercepted\")\n" +
                "    } else {\n" +
                "        core.io.Console.println(\"pack-other\")\n" +
                "    }\n" +
                "    if (b == 2) {\n" +
                "        core.io.Console.println(\"ping-ok\")\n" +
                "    }\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 7)) { return 1 }\n" +
                "    if ((b != 2)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ③+⑤ 多泛型参数 + 混合值参 + 显式泛型实参形态
            //（s.mix\<i32, String\>(...)）：unnamed 包 = [T typeid,
            // U typeid, 值...]，终态逐槽恢复
            Case("wrapper Entity wildcard 多泛型参数混合值参",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tracer4 {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Tracer4\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func mix\\<T, U>(x: T, y: U, n: i32): T { return x }\n" +
                "    pub func size\\<T, U>(x: T, y: U): U { return y }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.mix\\<i32, String>(41, \"hej\", 1)\n" +
                "    var n = s.size\\<i32, i32>(1, 5)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 41)) { return 1 }\n" +
                "    if ((n != 5)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ④ 泛型方法经 inner 改写 symbol 重路由到另一泛型方法
            //（包形状一致 [typeid, 值]；router 分支解包恢复 typeid 调
            // gank 的 $.wrapped. 体——gank 把 x 当 i32 加 100 再 as T，
            // 41 → 141；重路由到非泛型方法 VM 侧实参个数不匹配抛错，
            // 不进对拍）
            Case("wrapper Entity wildcard 泛型方法改写 symbol 重路由泛型",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Rerouter {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Service$pick(x:.generic<$.generic.T>)" +
                "@.generic<$.generic.T>\") {\n" +
                "            symbol = \"Service$gank(x:.generic<$.generic.T>)" +
                "@.generic<$.generic.T>\"\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Rerouter\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func pick\\<T>(x: T): T { return x }\n" +
                "    pub func gank\\<T>(x: T): T { return ((((x as i32) + 100)) as T) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.pick\\<i32>(41)\n" +
                "    return a\n" +
                "}\n"),
            // 类级泛型宿主边界：类级 typeid 不进包（调用约定剔除，
            // 被调方从 .this 隐藏字段自取）；trampoline 打包与终态
            // 实参拼装均跳过类级槽（VM PushFrame 重注入同口径）
            Case("wrapper Entity wildcard 类级泛型宿主成员",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tracer2 {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Tracer2\n" +
                "pub class Box\\<T> {\n" +
                "    pub var value: T\n" +
                "    pub init(v: T) { value = v }\n" +
                "    pub func get(): T { return value }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>(7)\n" +
                "    var a = b.get()\n" +
                "    return a\n" +
                "}\n"),
            // MW10 刀4：call??? 降级全链（VM↔native 对拍，用例转录/参照
            // BilVmTests TestDowngradeCallWildcard /
            // TestEntityWildcardMethodProxyBothDirections）
            // ① 未声明方法命中 .proxy.*（proxy 改返 99）+ 已声明 ping
            // 原样转发经环到原始体；返回 .any 的 cast 拆箱由前端既有
            // cast 承担（(s.fetchUserById(42) as i32) 顺带覆盖）
            Case("wrapper call??? 未声明命中 proxy 改返与原样转发",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        if (symbol == \"Service$fetchUserById(.i32)@.any\") {\n" +
                "            return (99 as TReturn)\n" +
                "        } else {\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(41)\n" +
                "    var b = (s.fetchUserById(42) as i32)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 42)) { return 1 }\n" +
                "    if ((b != 99)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ② call??? 进环后经 router 重路由命中另一已声明成员的剩余
            // 环（外层 wildcard 改写 symbol 原样 inner，内层 specific
            // .proxy.ping 环接管返回；VM RerouteWildcardInner 同口径）
            Case("wrapper call??? 重路由命中已声明成员剩余环",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Service$fetchUserById(.i32)@.any\") {\n" +
                "            symbol = \"Service$ping(x:.i32)@.i32\"\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return (s.fetchUserById(42) as i32)\n" +
                "}\n"),
            // ③ 未路由抛 NoSuchMethodException 且可被 try/catch 捕获
            //（消息口径对齐 VM「未路由的降级请求：」+ symbol）
            Case("wrapper call??? 未路由抛 NoSuchMethod 可 catch",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var service = new Service()\n" +
                "    try {\n" +
                "        service.fetchUserById(42)\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // ④ 子类实例的降级调用命中基类 wrapper（继承闭包：Child
            // 重申 @W，dispatch 深度序先命中 Child entry 环；继承方法
            // ping 经基类烘焙链同被拦截——转录 BilVmDispatchTests
            // WrapperFixes 子类拦截形态）
            Case("wrapper call??? 子类实例命中基类 wrapper",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"hit:\" + symbol)\n" +
                "        return (99 as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Child()\n" +
                "    var a = c.ping(1)\n" +
                "    var b = (c.fetchUserById(42) as i32)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 99)) { return 1 }\n" +
                "    if ((b != 99)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            Case("wrapper 字段-Value 读写",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 3 }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    if (h.hp:Clamped.min != 3) { return 1 }\n" +
                "    h.hp:Clamped.min = 9\n" +
                "    return h.hp:Clamped.min\n" +
                "}\n"),
            // MW10 刀2：字段-Value wrapper get/set 链（VM↔native 对拍，
            // 用例转录自 BilVmTests wrapper 段）
            Case("wrapper Value 局部双层链序（A.set→B.set→B.get→A.get）",
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
                "}\n"),
            Case("wrapper Value 局部 Clamped 写夹取",
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
                "    if (((a == 100) and (b == 0))) { return 0 }\n" +
                "    return 1\n" +
                "}\n"),
            Case("wrapper Value get-only 修饰 const 局部可读",
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
                "}\n"),
            Case("wrapper Value 实例字段读写",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var max: i32\n" +
                "    pub init() { max = 100 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        if ((v > max)) { v = max }\n" +
                "        inner((v as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 200\n" +
                "    return h.hp\n" +
                "}\n"),
            Case("wrapper Value 写序 wrapper.set→user.set",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"wrapper.set\")\n" +
                "        var v = (value as i32)\n" +
                "        inner(((v + 10) as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Shift()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            core.io.Console.println(\"user.set\")\n" +
                "            value = value * 2\n" +
                "        }\n" +
                "    } = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 5\n" +
                "    return h.hp\n" +
                "}\n"),
            Case("wrapper Value 读序 user.get→wrapper.get",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"wrapper.get\")\n" +
                "        var v = (value as i32)\n" +
                "        return ((v + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Shift()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) {\n" +
                "            core.io.Console.println(\"user.get\")\n" +
                "            return value * 2\n" +
                "        }\n" +
                "        pub set(value: _) { }\n" +
                "    } = 10\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n"),
            Case("wrapper Value init 写豁免经用户 setter",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @W()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { if (value > 100) { value = 100 } }\n" +
                "    } = 150\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n"),
            // MW10 刀3b：Entity 字段 get/set proxy 链（VM↔native 对拍，
            // 用例转录自 BilVmTests/BilVmStressTests wrapper 段与
            // Tests/e2e/rigi/o7_base_init_wrapper_installed.rg）
            Case("wrapper Entity o7 基类 init 读命中 get.*",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"audit\")\n" +
                "        return value\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub open class Base {\n" +
                "    pub var hp: i32 = 10\n" +
                "    pub init() { hp = (this.hp + 1) }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Hero : Base {\n" +
                "    pub init() { super() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n"),
            // specific get/set 各绕一层（写 s、读 g 打印序 + 字段值生效）。
            // 注：BilVmTests TestEntityGetterSetterProxyCounts 的计数器形态
            // 依赖 wrapper 实例状态持久——native 隐藏槽是内联值拷贝 ABI
            //（get.wrapper 拷贝出槽，环内写 .this 字段不落回），方法面
            // 刀1 起同病，属既有缺口非本刀引入；此处以打印序对拍同等语义
            Case("wrapper Entity specific get/set 各绕一层",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Echo {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {\n" +
                "        core.io.Console.println(\"g\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.name\\<TField>(value: TField) {\n" +
                "        core.io.Console.println(\"s\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@Echo\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    var n = s.name\n" +
                "    if (n == \"b\") { return 1 } else { return 0 }\n" +
                "}\n"),
            Case("wrapper Entity wildcard get.* 收符号变值",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"get:\" + symbol)\n" +
                "        return value\n" +
                "    }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var n = s.name\n" +
                "    return 0\n" +
                "}\n"),
            Case("wrapper Entity wildcard set.* 落原始写",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"set:\" + symbol)\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    if (s.name == \"b\") { return 1 } else { return 0 }\n" +
                "}\n"),
            // MW10 遗留⑧回归：访问器调用被 .proxy.* 拦截重路由为 Set 链
            //（胖值 ABI），wildcard set 环 inner 改写 symbol 后链末落带用户
            // setter 的字段——修前 VM 未拆 VmAny 装箱，setter 内 == 抛
            //「没有用户 operator equals：.any」；修后双端一致落 setter 写
            // backing（改写只影响后续环查找，链末写目标双端同为原字段 hp）
            Case("wrapper set 环 inner 改写 symbol 重路由落用户 setter",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#hp2@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            if (value == 10) { core.io.Console.println(\"hp setter sees 10\") }\n" +
                "        }\n" +
                "    } = 10\n" +
                "    pub var hp2: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            if (value == 10) { core.io.Console.println(\"hp2 setter sees 10\") }\n" +
                "        }\n" +
                "    } = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    core.io.Console.println(\"hp=${e.hp} hp2=${e.hp2}\")\n" +
                "    return 0\n" +
                "}\n"),
            // MW10 遗留④：wildcard set 环 inner 改写 symbol 到另一无 proxy
            // 字段——剩余层无环可进，VM RerouteWildcardInner 只改 MemberName、
            // FieldSymbol 恒为原字段，链末落**原字段**的 setter/backing
            Case("wrapper set 环 inner 改写 symbol 到无 proxy 字段落原字段终态",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#mp@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            if (value == 7) { core.io.Console.println(\"hp setter 7\") }\n" +
                "        }\n" +
                "    } = 0\n" +
                "    pub var mp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            if (value == 7) { core.io.Console.println(\"mp setter 7\") }\n" +
                "        }\n" +
                "    } = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    return 0\n" +
                "}\n"),
            // MW10 遗留④：改写到有 specific set proxy 的字段——剩余层命中
            // .proxy.set.mp 进新字段环（打印 WB.mp）；环 inner 到底后 VM
            // 链末仍写原字段 hp（FieldSymbol 不随改写变）
            Case("wrapper set 环 inner 改写 symbol 进他字段 specific 环",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#mp@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.mp\\<TField>(value: TField) {\n" +
                "        core.io.Console.println(\"WB.mp\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub var mp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    e.mp = 9\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    return 0\n" +
                "}\n"),
            // MW10 遗留④：三层 set 环，中间层（WB，layer 1）改写 hp→mp，
            // 重路由自 layer 2 起命中 WC 的 .proxy.set.mp（层序正确：
            // WA identity → WB 改写 → WC.mp 接管），链末落原字段 hp
            Case("wrapper set 环内层改写 symbol 剩余层序",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"WA\")\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"WB\")\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#mp@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WC {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.mp\\<TField>(value: TField) {\n" +
                "        core.io.Console.println(\"WC.mp\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "@WC\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub var mp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    e.mp = 9\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    return 0\n" +
                "}\n"),
            // MW10 遗留④：改写到可解析但不存在的字段名——VM 按
            // FieldSimpleName 查环无果（剩余层无 wildcard），链末落
            // 原字段终态（不抛异常；FieldSymbol 恒为原字段）
            Case("wrapper set 环 inner 改写 symbol 未知名落原字段终态",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#ghost@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    core.io.Console.println(\"hp=${e.hp}\")\n" +
                "    return 0\n" +
                "}\n"),
            Case("wrapper Entity 双层字段链写序 outer→inner",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WO {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"O.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"O.set\")\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WI {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"I.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"I.set\")\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WO\n" +
                "@WI\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    var n = s.name\n" +
                "    if (n == \"b\") { return 1 } else { return 0 }\n" +
                "}\n"),
            // ===== 遗1：用户运算符 native 分派（VM 为基准逐类对拍） =====
            Case("用户运算符 算术/比较/一元/复合赋值",
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "    pub operator minus(another: Vec): Vec { return new Vec((x - another.x)) }\n" +
                "    pub operator equals(another: Vec): bool { return (x == another.x) }\n" +
                "    pub operator compareTo(another: Vec): ComparisonResult {\n" +
                "        if ((x < another.x)) { return .LesserThanAnother }\n" +
                "        if ((x > another.x)) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "    pub operator opposite(): Vec { return new Vec((0 - x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var n = 0\n" +
                "    if (((a + b).x == 3)) { n = (n + 1) }\n" +
                "    if (((b - a).x == 1)) { n = (n + 2) }\n" +
                "    if ((a == new Vec(1))) { n = (n + 4) }\n" +
                "    if ((a != b)) { n = (n + 8) }\n" +
                "    if ((a < b)) { n = (n + 16) }\n" +
                "    if ((a <= new Vec(1))) { n = (n + 32) }\n" +
                "    if ((b > a)) { n = (n + 64) }\n" +
                "    if ((b >= b)) { n = (n + 128) }\n" +
                "    if (((-a).x == (0 - 1))) { n = (n + 256) }\n" +
                "    var c = new Vec(10)\n" +
                "    c += b\n" +
                "    if ((c.x == 12)) { n = (n + 512) }\n" +
                "    if ((b < a)) { n = (n + 1024) }\n" +
                "    core.io.Console.println(n.toString())\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：位标累计值整体比对
                "    if ((n != 1023)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 混合类型操作数（重载按右操作数形参可赋匹配）+ 继承下探
            //（子类未定义运算符时沿 extends 链命中基类实现）
            Case("用户运算符 混合类型操作数与继承下探",
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "    pub operator plus(n: i32): Vec { return new Vec((x + n)) }\n" +
                "}\n" +
                "open class Animal {\n" +
                "    pub var legs: i32\n" +
                "    pub init(_ -> legs) { }\n" +
                "    pub operator equals(other: Animal): bool { return (legs == other.legs) }\n" +
                "}\n" +
                "class Dog : Animal {\n" +
                "    pub init(n: i32) { super(n) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var m = a + 10\n" +
                "    var n = 0\n" +
                "    if ((m.x == 11)) { n = (n + 1) }\n" +
                "    var v = a + new Vec(2)\n" +
                "    if ((v.x == 3)) { n = (n + 2) }\n" +
                "    var d1: Animal = new Dog(4)\n" +
                "    var d2: Animal = new Dog(4)\n" +
                "    var d3: Animal = new Dog(3)\n" +
                "    if ((d1 == d2)) { n = (n + 4) }\n" +
                "    if ((d1 != d3)) { n = (n + 8) }\n" +
                "    var e1 = new Dog(4)\n" +
                "    var e2 = new Dog(4)\n" +
                "    if ((e1 == e2)) { n = (n + 16) }\n" +
                "    core.io.Console.println(n.toString())\n" +
                "    return n\n" +
                "}\n"),
            // shadow 派发：operator 不可 override 但可同名再定义（静默
            // hiding）；VM 按左操作数实际类型沿派生链命中最具体实现，
            // native 经 vtable 槽覆盖对齐（Derived$$equals → false → 0）
            Case("用户运算符 shadow 实际类型派发",
                "open class Base {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator equals(other: Base): bool { return (x == other.x) }\n" +
                "}\n" +
                "class Derived : Base {\n" +
                "    pub init(n: i32) { super(n) }\n" +
                "    pub operator equals(other: Base): bool { return false }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a: Base = new Derived(1)\n" +
                "    var b: Base = new Base(1)\n" +
                "    var r = if ((a == b)) { return@_ 1 } else { return@_ 0 }\n" +
                "    core.io.Console.println(r.toString())\n" +
                "    return r\n" +
                "}\n"),
            // 遗1 刀3a 首次行为对拍：Entity wrapper 的 specific
            // .proxy.opr.plus 拦截 + inner 落原始体 + 状态跨调用持久
            //（转录 BilVmTests.TestEntityProxyStatePersists 形状到运算符）
            Case("wrapper 运算符 specific proxy 链",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var hits: i32\n" +
                "    pub init() { hits = 0 }\n" +
                "    operator .proxy.opr.plus(another: Vec): Vec {\n" +
                "        hits = (hits + 1)\n" +
                "        var r = inner(another)\n" +
                "        return new Vec((r.x + 100))\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    var d = a + b\n" +
                "    var n = ((c.x + a:Counting.hits))\n" +
                "    core.io.Console.println(n.toString())\n" +
                "    return n\n" +
                "}\n"),
            // wildcard .proxy.opr.* 拦截（转录 BilVmTests:1828
            // WrappedVecOperatorProxyModule 的源码级形态）：proxy 直接
            // 返回 99，原始 plus 不执行
            Case("wrapper 运算符 wildcard proxy 拦截",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String,\n" +
                "        namedArgs: named TNamedArgs...,\n" +
                "        unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return (new Vec(99) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    core.io.Console.println(c.x.toString())\n" +
                "    return c.x\n" +
                "}\n"),
            Case("位置值包 0/1/3 实参",
                "import core.io.Console\n" +
                "func sum(nums: i32...): i32 {\n" +
                "    var total = 0\n" +
                "    var i = 0\n" +
                "    while (i < nums.length) {\n" +
                "        total = total + (nums[i] if? 0)\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    return total\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    if (sum() == 0) { Console.println(\"empty\") }\n" +
                "    if (sum(7) == 7) { Console.println(\"one\") }\n" +
                "    if (sum(1, 2, 3) == 6) { Console.println(\"three\") }\n" +
                "    return 0\n" +
                "}\n"),
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
            Case("动态 new Type<T> 参数来源",
                "import core.io.Console\n" +
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func make(tid: Type\\<Point>, v: i32): Point { return new tid(v) }\n" +
                "pub func main(): i32 {\n" +
                "    var p = make(typeOf(Point), 8)\n" +
                "    if (p.x == 8) { Console.println(\"typeparam ok\") }\n" +
                "    return p.x\n" +
                "}\n"),
            Case("动态 new 占位静态实参",
                "import core.io.Console\n" +
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "}\n" +
                "pub class Factory\\<T> {\n" +
                "    pub func make\\<U extends Box\\<T>>(x: T): U { return U(x) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Factory\\<i32>().make\\<Box\\<i32>>(11)\n" +
                "    if (b.v == 11) { Console.println(\"ph arg ok\") }\n" +
                "    return b.v\n" +
                "}\n"),
            Case("动态 new 泛型构造目标 Box<i32>",
                "import core.io.Console\n" +
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "}\n" +
                "pub func make\\<T extends Box\\<i32>>(x: i32): T { return T(x) }\n" +
                "pub func main(): i32 {\n" +
                "    var b = make\\<Box\\<i32>>(13)\n" +
                "    if (b.v == 13) { Console.println(\"box tid ok\") }\n" +
                "    return b.v\n" +
                "}\n"),
            Case("动态 new 派生 typeid 命中派生 init",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 1 }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub init() { n = 2 }\n" +
                "}\n" +
                "pub func make\\<T extends Base>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    var d = make\\<Derived>()\n" +
                "    if (d.n == 2) { Console.println(\"derived ok\") }\n" +
                "    return d.n\n" +
                "}\n"),
            FailCase("动态 new 无匹配 init",
                "pub class OnlyI32 {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(OnlyI32)\n" +
                "    var o = new t(true)\n" +
                "    return 0\n" +
                "}\n", "不匹配任何 init"),
            Case("动态 new String 实参 T(v)",
                "import core.io.Console\n" +
                "pub class Named {\n" +
                "    pub var s: String\n" +
                "    pub init(s: String) { this.s = s }\n" +
                "}\n" +
                "pub func make\\<T extends Named>(s: String): T { return T(s) }\n" +
                "pub func main(): i32 {\n" +
                "    var n = make\\<Named>(\"hi\" + \"!\")\n" +
                "    if (n.s == \"hi!\") { Console.println(\"str arg ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("动态 new String 实参 typeOf",
                "import core.io.Console\n" +
                "pub class Named {\n" +
                "    pub var s: String\n" +
                "    pub init(s: String) { this.s = s }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Named)\n" +
                "    var n = new t(\"hi\" + \"!\")\n" +
                "    if (n.s == \"hi!\") { Console.println(\"str typeof ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("动态 new rich struct 实参",
                "import core.io.Console\n" +
                "pub struct Pair2 {\n" +
                "    pub var a: String\n" +
                "    pub var b: String\n" +
                "    pub init(_ -> a, _ -> b)\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    pub var p: Pair2\n" +
                "    pub init(p: Pair2) { this.p = p }\n" +
                "}\n" +
                "pub func make\\<T extends Holder>(p: Pair2): T { return T(p) }\n" +
                "pub func main(): i32 {\n" +
                "    var h = make\\<Holder>(new Pair2(\"aa\", \"bb\"))\n" +
                "    if ((h.p.a == \"aa\") and (h.p.b == \"bb\")) { Console.println(\"rich ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("动态 new 多实参混合",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub class Mix {\n" +
                "    pub var n: i32\n" +
                "    pub var s: String\n" +
                "    pub var p: Node\n" +
                "    pub init(n: i32, s: String, p: Node) {\n" +
                "        this.n = n\n" +
                "        this.s = s\n" +
                "        this.p = p\n" +
                "    }\n" +
                "}\n" +
                "pub func make\\<T extends Mix>(n: i32, s: String, p: Node): T {\n" +
                "    return T(n, s, p)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = make\\<Mix>(7, \"hi\" + \"!\", new Node(3))\n" +
                "    if (m.n == 7) {\n" +
                "        if (m.s == \"hi!\") {\n" +
                "            if (m.p.x == 3) { Console.println(\"mix ok\") }\n" +
                "        }\n" +
                "    }\n" +
                "    return m.n\n" +
                "}\n"),
            FailCase("动态 new abstract 目标",
                "pub abstract class Abs {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Abs)\n" +
                "    var a = new t()\n" +
                "    return 0\n" +
                "}\n", "目标不可构造"),
            Case("动态 new Pair typeOf 值",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var sample = new Pair\\<String, i32>(\"a\", 1)\n" +
                "    var t = typeOf(sample)\n" +
                "    var p = new t(\"b\", 2)\n" +
                "    if (p.key == \"b\") {\n" +
                "        if (p.value == 2) { Console.println(\"pair ok\") }\n" +
                "    }\n" +
                "    return p.value\n" +
                "}\n"),
            Case("动态 new RuntimeException getMessage",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var sample = new RuntimeException(\"x\")\n" +
                "    var t = typeOf(sample)\n" +
                "    var e = new t(\"hello\")\n" +
                "    if (e.getMessage() == \"hello\") { Console.println(\"exn ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            FailCase("动态 new 抽象 Exception",
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Exception)\n" +
                "    var a = new t()\n" +
                "    return 0\n" +
                "}\n", "目标不可构造"),
            Case("动态 new struct 目标字段落位",
                "import core.io.Console\n" +
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(a: i32, b: i32) { x = a\n" +
                "        y = b }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Vec)\n" +
                "    var s = new t(3, 4)\n" +
                "    if ((s.x == 3) and (s.y == 4)) { Console.println(\"struct ok\") }\n" +
                "    return (s.x + s.y)\n" +
                "}\n"),
            Case("动态 new 泛型界 struct Pair2",
                "import core.io.Console\n" +
                "pub struct Pair2 {\n" +
                "    pub var a: i32\n" +
                "    pub var b: i32\n" +
                "    pub init(_ -> a, _ -> b)\n" +
                "}\n" +
                "pub func make\\<T extends Pair2>(x: i32, y: i32): T { return T(x, y) }\n" +
                "pub func main(): i32 {\n" +
                "    var p = make\\<Pair2>(5, 6)\n" +
                "    if ((p.a == 5) and (p.b == 6)) { Console.println(\"pair2 ok\") }\n" +
                "    return (p.a + p.b)\n" +
                "}\n"),
            Case("动态 new 带实参 struct init",
                "import core.io.Console\n" +
                "pub struct Box {\n" +
                "    pub var n: i32\n" +
                "    pub init(v: i32) { n = v }\n" +
                "}\n" +
                "pub func fromType(tid: Type\\<Box>, v: i32): Box { return new tid(v) }\n" +
                "pub func main(): i32 {\n" +
                "    var b = fromType(typeOf(Box), 11)\n" +
                "    if (b.n == 11) { Console.println(\"sret ok\") }\n" +
                "    return b.n\n" +
                "}\n"),
            FailCase("动态 new enum 目标",
                "pub enum struct Color {}[Red, Green]\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Color)\n" +
                "    var e = new t()\n" +
                "    return 0\n" +
                "}\n", "目标不可构造"),
            FailCase("动态 new struct 无匹配 init",
                "pub struct OnlyI32 {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(OnlyI32)\n" +
                "    var o = new t(true)\n" +
                "    return 0\n" +
                "}\n", "不匹配任何 init"),
            Case("标量界零参 T()",
                "import core.io.Console\n" +
                "pub func make\\<T extends i32>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    var x = make\\<i32>()\n" +
                "    if (x == 0) { Console.println(\"i32 zero\") }\n" +
                "    return x\n" +
                "}\n"),
            Case("标量目标零参 new typeValue()",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(42)\n" +
                "    var x: i32 = new t()\n" +
                "    if (x == 0) { Console.println(\"tv zero\") }\n" +
                "    return x\n" +
                "}\n"),
            Case("String 界零参 T()",
                "import core.io.Console\n" +
                "pub func make\\<T extends String>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    var s = make\\<String>()\n" +
                "    if (s == \"\") { Console.println(\"str zero\") }\n" +
                "    return 0\n" +
                "}\n"),
            FailCase("标量目标带实参抛 NoSuchMethodException",
                "pub func main(): i32 {\n" +
                "    var t = typeOf(42)\n" +
                "    var x: i32 = new t(1)\n" +
                "    return x\n" +
                "}\n", "不匹配任何 init"),
            FailCase("动态 new 零参无匹配 init",
                "pub class OnlyI32 {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(OnlyI32)\n" +
                "    var o = new t()\n" +
                "    return 0\n" +
                "}\n", "不匹配任何 init"),
            Case("数值 cast 宽化窄化",
                "pub func main(): i32 {\n" +
                "    var a: i32 = 1000\n" +
                "    var b: i64 = (a as i64)\n" +
                "    var c: i16 = (a as i16)\n" +
                "    var d: u8 = (42 as u8)\n" +
                "    var e: double = (a as double)\n" +
                "    var f: i32 = ((e as i32) + (c as i32))\n" +
                // 返回值须 <256：linux 进程退出码 8-bit 截断（3042 在 linux 只剩 226）
                "    if (((f + (d as i32)) + (b as i32)) == 3042) { return 42 }\n" +
                "    return 0\n" +
                "}\n"),
            Case("数值 cast 符号截断",
                "pub func main(): i32 {\n" +
                "    var n: i32 = -1\n" +
                "    var u = n as u32\n" +
                "    var back = u as i32\n" +
                "    var w = (300 as i8) as i32\n" +
                "    if (back == -1) { return w }\n" +
                "    return 0\n" +
                "}\n"),
            Case("浮点 cast 截断与 NaN",
                "pub func main(): i32 {\n" +
                "    var z = 0.0\n" +
                "    var nan = z / z\n" +
                "    var n = nan as i32\n" +
                "    var t = (1.9 as i32)\n" +
                "    var neg = ((0.0 - 1.9) as i32)\n" +
                "    return (((n * 100) + (t * 10)) + (0 - neg))\n" +
                "}\n"),
            Case("浮点 cast 溢出口径",
                "pub func main(): i32 {\n" +
                "    var big = 1e20\n" +
                "    var hi = big as i32\n" +
                "    var lo = ((0.0 - big) as i32)\n" +
                "    if ((hi == 2147483647) and (lo < 0)) { return 1 }\n" +
                "    return 0\n" +
                "}\n"),
            Case("占位 cast 命中",
                "import core.io.Console\n" +
                "pub func conv\\<T>(x: Any): T { return x as T }\n" +
                "pub func main(): i32 {\n" +
                "    var n = conv\\<i32>(42 as Any)\n" +
                "    if (n == 42) { Console.println(\"ph hit\") }\n" +
                "    return n\n" +
                "}\n"),
            Case("占位 as? 命中与 null",
                "import core.io.Console\n" +
                "pub open class Animal { pub init() { } }\n" +
                "pub class Dog : Animal { pub init() { } }\n" +
                "pub func safe\\<T>(a: Animal): T { return a as? T }\n" +
                "pub func main(): i32 {\n" +
                "    var d: Animal = new Dog()\n" +
                "    var hit = safe\\<Dog>(d)\n" +
                "    var miss = safe\\<Dog>(new Animal())\n" +
                "    if (hit != null) { Console.println(\"as? hit\") }\n" +
                "    if (miss == null) { Console.println(\"as? null\") }\n" +
                "    return 0\n" +
                "}\n"),
            FailCase("占位 cast 失败抛 CastException",
                "pub func conv\\<T>(x: Any): T { return x as T }\n" +
                "pub func main(): i32 {\n" +
                "    const ignored = conv\\<String>(42 as Any)\n" +
                "    return 0\n" +
                "}\n", "无法将"),
            Case("struct 恒等 cast",
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Point(3, 4)\n" +
                "    var b = a as Point\n" +
                "    return ((b.x * 10) + b.y)\n" +
                "}\n"),
            FailCase("struct 非恒等抛 CastException",
                "pub struct A {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub struct B {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new A(1)\n" +
                "    var b = a as B\n" +
                "    return 0\n" +
                "}\n", "转换为"),
            Case("String 恒等 cast",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s = \"hi\" as String\n" +
                "    Console.println(s)\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW9a 异常机制对拍：同一份 Rigi 源喂 VM 与 native，
            // 比 stdout + 退出码（RIGI_RT_MEMTRACK=1 零泄漏口径）。
            // 只覆盖「捕获」型路径——未捕获的顶层 stderr 格式 VM/native
            // 对齐属 MW9b，此处不对拍 =====
            Case("try/catch 捕获打印 getMessage",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.RuntimeException(\"boom\")\n" +
                "        return 0\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            Case("catch 顺序：子类先命中、基类兜底",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.IOException(\"io\")\n" +
                "    } catch (e: core.IOException) {\n" +
                "        Console.println(\"sub:\" + e.getMessage())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"base\")\n" +
                "    }\n" +
                "    try {\n" +
                "        throw new core.RuntimeException(\"rt\")\n" +
                "    } catch (_: core.IOException) {\n" +
                "        Console.println(\"no\")\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"base:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 5\n" +
                "}\n"),
            Case("基类 catch 捕获子类异常（is 协变）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.IOException(\"io\")\n" +
                "        return 0\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 4\n" +
                "    }\n" +
                "}\n"),
            Case("catch 未命中传播到外层 try",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.CastException(\"cast\")\n" +
                "        } catch (_: core.IOException) {\n" +
                "            Console.println(\"no\")\n" +
                "        }\n" +
                "        Console.println(\"unreachable\")\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"outer:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 2\n" +
                "}\n"),
            Case("finally 在正常路径执行",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        Console.println(\"try\")\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin\")\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("finally 在异常路径执行后外层捕获",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.RuntimeException(\"x\")\n" +
                "        } catch (_: core.IOException) {\n" +
                "            Console.println(\"no\")\n" +
                "        } finally(_) {\n" +
                "            Console.println(\"fin\")\n" +
                "        }\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 8\n" +
                "}\n"),
            Case("finally(e) 槽语义：无异常 null、异常非空",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        Console.println(\"t1\")\n" +
                "    } finally(e) {\n" +
                "        if (e == null) { Console.println(\"null\") }\n" +
                "        else { Console.println(\"exc\") }\n" +
                "    }\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.RuntimeException(\"boom\")\n" +
                "        } finally(e) {\n" +
                "            if (e == null) { Console.println(\"null2\") }\n" +
                "            else { Console.println(\"exc:\" + e.getMessage()) }\n" +
                "        }\n" +
                "    } catch (_: core.Exception) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "    return 3\n" +
                "}\n"),
            Case("catch 内 rethrow 被外层捕获",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.IOException(\"inner\")\n" +
                "        } catch (e: core.IOException) {\n" +
                "            Console.println(\"c1:\" + e.getMessage())\n" +
                "            throw new core.RuntimeException(\"re\")\n" +
                "        }\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"c2:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 6\n" +
                "}\n"),
            Case("break/continue 穿越 finally",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        i = (i + 1)\n" +
                "        try {\n" +
                "            if (i == 2) { break }\n" +
                "            continue\n" +
                "        } finally(_) {\n" +
                "            Console.println(\"f\")\n" +
                "        }\n" +
                "        Console.println(\"tail\")\n" +
                "    }\n" +
                "    return i\n" +
                "}\n"),
            Case("return 穿越 finally（退出码 42）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 42\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n"),
            Case("throw 跨函数传播捕获",
                "import core.io.Console\n" +
                "pub func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        boom()\n" +
                "        return 0\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            Case("嵌套 try：内层未命中、外层类型命中",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.IOException(\"io\")\n" +
                "        } catch (_: core.CastException) {\n" +
                "            Console.println(\"no\")\n" +
                "        }\n" +
                "        return 0\n" +
                "    } catch (e: core.IOException) {\n" +
                "        Console.println(\"io:\" + e.getMessage())\n" +
                "        return 5\n" +
                "    }\n" +
                "}\n"),
            Case("自定义异常类多态 getMessage",
                "import core.io.Console\n" +
                "pub open class MyException : core.RuntimeException {\n" +
                "    pub init(text: String) { message = text }\n" +
                "    pub override func getMessage(): String { return \"custom:\" + message }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new MyException(\"boom\")\n" +
                "        return 0\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 9\n" +
                "    }\n" +
                "}\n"),
            // ===== MW9b-G：native 守卫点抛真异常（与 VM 同型同消息，
            // 可被 try/catch 捕获）对拍——除零/cast/拆箱/new.indirect/
            // 数组·Span 越界写六守卫 + 未捕获 reporter 新格式 =====
            Case("try/catch 捕获整数除零",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 0\n" +
                "    try {\n" +
                "        return (x / z)\n" +
                "    } catch (e: core.DividedByZeroException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获 as 拆箱失败",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = 42 as Any\n" +
                "    try {\n" +
                "        var s = a as String\n" +
                "        Console.println(s)\n" +
                "        return 0\n" +
                "    } catch (e: core.CastException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 8\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获占位 cast 失败",
                "import core.io.Console\n" +
                "pub func conv\\<T>(x: Any): T { return x as T }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var s = conv\\<String>(42 as Any)\n" +
                "        Console.println(s)\n" +
                "        return 0\n" +
                "    } catch (e: core.CastException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 6\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获 new.indirect 无匹配 init",
                "import core.io.Console\n" +
                "pub class OnlyI32 {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var t = typeOf(OnlyI32)\n" +
                "        var o = new t(true)\n" +
                "        return 0\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 4\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获数组写越界",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    try {\n" +
                "        a[5] = 1\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获 Span 写越界",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s = spanOf\\<i32>(3)\n" +
                "    try {\n" +
                "        s[(0 - 1)] = 1\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 8\n" +
                "    }\n" +
                "}\n"),
            // 未捕获：两侧同为「{类型全名}: {message}」+ exit 1；
            // nativeNeedle 断 reporter 新格式全名前缀
            FailCase("用户 throw 未捕获顶层格式",
                "pub func main(): i32 {\n" +
                "    throw new core.RuntimeException(\"boom\")\n" +
                "}\n", "boom", "core::RuntimeException: boom"),
            FailCase("除零未捕获顶层格式",
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 0\n" +
                "    return (x / z)\n" +
                "}\n", "整数除以零", "core::DividedByZeroException: 整数除以零"),
            FailCase("数组越界写未捕获顶层格式",
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[9] = 2\n" +
                "    return 0\n" +
                "}\n", "数组下标越界", "core::OutOfBoundException: 数组下标越界：9（长度 3）"),
            // ===== MW11a 棒3 协程对拍（VM 母本移植；B-1 起 main 与
            // 其同步调用链可直接挂起——本组保留「await 收进 async run()
            // 体内」写法作回归，main 直接 await 形态见下方 B-1 组；
            // stdout 打印只放 await/join 之后的数据依赖确定位置，不断言
            // 并发交错序）=====
            // ① fork/join 取值（VM TestForkJoinAndFireAndForget join 母本）：
            // 三路 spawn → run 内 await → 求和打印 9
            Case("协程 fork/join 取值",
                "import core.io.Console\n" +
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "async func run() {\n" +
                "    var a = add(1)\n" +
                "    var b = add(2)\n" +
                "    var c = add(3)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    var z = await c\n" +
                "    Console.println(((x + y) + z).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ② await 已完成 Task + 二次 await 同结果（VM
            // TestAwaitExceptionAndCompleted twice 母本）：打印 10
            Case("协程二次 await 同结果",
                "import core.io.Console\n" +
                "async func quick(): i32 { return 5 }\n" +
                "async func run() {\n" +
                "    var t = quick()\n" +
                "    var a = await t\n" +
                "    var b = await t\n" +
                "    Console.println((a + b).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ③ async 抛异常 → await 点重抛 → try/catch 捕获打印（VM
            // TestAwaitExceptionAndCompleted caught 母本）
            Case("协程 await 异常重抛捕获",
                "import core.io.Console\n" +
                "async func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "async func run() {\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        Console.println(\"no\")\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ④ fire-and-forget：spawn 后不 await，main return 后协程仍
            // 被 drain 等待执行完成，其打印出现在 stdout
            Case("协程 fire-and-forget drain 等待",
                "import core.io.Console\n" +
                "async func bg() {\n" +
                "    yield\n" +
                "    Console.println(\"bg-done\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    bg()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑤ 未观察失败（FailCase）：fire-and-forget 抛异常 → main
            // 正常返回 0 后 drain 到终态 → 进程 exit 1 + stderr
            // 「{类型全名}: {message}」（VM 侧查消息关键字，native 侧查
            // reporter 全名前缀格式）
            FailCase("协程未观察失败顶层格式",
                "async func ghost() {\n" +
                "    throw new core.RuntimeException(\"bg\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    ghost()\n" +
                "    return 0\n" +
                "}\n", "bg", "core::RuntimeException: bg"),
            // ⑥ 裸 yield：两协程各 yield 后完成 join，断言最终求和值
            //（交错序不断言）
            Case("协程裸 yield join",
                "import core.io.Console\n" +
                "async func step(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n\n" +
                "}\n" +
                "async func run() {\n" +
                "    var a = step(20)\n" +
                "    var b = step(22)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    Console.println((x + y).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑦ await 穿越 try/finally 双路径（VM
            // TestAwaitThroughTryFinally 母本）：正常返回与异常路径
            // finally 均执行（单协程内顺序确定）
            Case("协程 await 穿越 try/finally 双路径",
                "import core.io.Console\n" +
                "async func pause(): i32 {\n" +
                "    yield\n" +
                "    return 9\n" +
                "}\n" +
                "async func boom(): i32 {\n" +
                "    yield\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "async func run() {\n" +
                "    try {\n" +
                "        var n = await pause()\n" +
                "        Console.println(\"ok:\" + n.toString())\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin1\")\n" +
                "    }\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        Console.println(\"no\")\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin2\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== B-1 栈式跨界（SYNTAX §11：main 以及由它同步调用
            // 的普通 fn 可以直接 await/yield）：非 async 挂起点全链
            // 支持，对拍 VM =====
            // B-1① main 直接 await（await 目标真挂起——yield 强制跨
            // 执行段）：stdout 42 + 退出码 42
            Case("栈式跨界 main 直接 await",
                "import core.io.Console\n" +
                "async func add(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = add(41)\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return r\n" +
                "}\n"),
            // B-1② main → 单层同步 fn（内含 await）：形参/局部跨
            // 挂起保存（acc 在 await 后仍须正确），stdout 45
            Case("栈式跨界 同步 fn 内 await 参数局部保持",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func compute(a: i32, b: i32): i32 {\n" +
                "    var acc = a + b\n" +
                "    var t = slow(acc)\n" +
                "    var r = await t\n" +
                "    return r + acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = compute(10, 5)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-1③ 终态快路径：await 已完成 Task 不挂起（无 yield）
            Case("栈式跨界 main await 已完成 Task",
                "import core.io.Console\n" +
                "async func quick(): i32 { return 5 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = quick()\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return r\n" +
                "}\n"),
            // B-1④ 异常跨链：tainted callee 恢复后抛出 → FAILED 沿
            // 调用点原 ExcTarget 进 main 的 try 派发垫捕获
            Case("栈式跨界 同步 fn 恢复后抛出被 main 捕获",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func risky(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    if (r > 100) {\n" +
                "        throw new core.RuntimeException(\"too-big\")\n" +
                "    }\n" +
                "    return r\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return risky(60)\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            // B-1⑤ main 失败链路（FailCase）：tainted callee 未捕获
            // 异常 → main Task FAILED → settle 重抛 → 顶层 reporter
            FailCase("栈式跨界 main 失败顶层格式",
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func boom(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    throw new core.RuntimeException(\"deep\")\n" +
                "    return r\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return boom(3)\n" +
                "}\n", "deep", "core::RuntimeException: deep"),
            // ===== B-2 全组合收口 =====
            // B-2① async fn 体直调 tainted 普通 fn：整条栈同步挂起，
            // 结果是裸返回值（不是 Task——栈式语义），求和 62
            Case("栈式跨界 async 调 tainted 裸返回",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func work(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    return r + 1\n" +
                "}\n" +
                "async func run(): i32 {\n" +
                "    var x = work(10)\n" +
                "    var y = work(20)\n" +
                "    return x + y\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2② Task.State 投影可观察性：async 调用方的栈悬在
            // tainted 链内时其 Task 投影 Suspended（prop 臂 markSuspended
            // 与 VM 对齐）；观察协程 yield 让步后读 state
            Case("栈式跨界 async 调用方 Task.State 投影",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func tag(s: TaskState): String {\n" +
                "    if (s is .Created) { return \"created\" }\n" +
                "    if (s is .Runnable) { return \"runnable\" }\n" +
                "    if (s is .Suspended) { return \"suspended\" }\n" +
                "    if (s is .Completed) { return \"completed\" }\n" +
                "    if (s is .Failed) { return \"failed\" }\n" +
                "    return \"cancelled\"\n" +
                "}\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func work(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    return r + 1\n" +
                "}\n" +
                "async func run(): i32 {\n" +
                "    return work(10)\n" +
                "}\n" +
                "async func watch(t: Task\\<i32>) {\n" +
                "    yield\n" +
                "    Console.println(\"mid:\" + tag(t.state))\n" +
                "    var r = await t\n" +
                "    Console.println(\"end:\" + tag(t.state))\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    var w = watch(t)\n" +
                "    await w\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2③ 三层嵌套 tainted 链（main→level1→level2→level3→
            // await）：每级调用点都是调用方的挂起点，acc 跨层保持 14
            Case("栈式跨界 三层嵌套挂起链",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n + 1\n" +
                "}\n" +
                "func level3(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    return r + 1\n" +
                "}\n" +
                "func level2(n: i32): i32 {\n" +
                "    var acc = level3(n) + 1\n" +
                "    return acc\n" +
                "}\n" +
                "func level1(n: i32): i32 {\n" +
                "    var acc = level2(n) + 1\n" +
                "    return acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = level1(10)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2④ 直接递归 tainted fn：每层 yield 挂起整链，恢复沿
            // 链逐层下钻（down(5)=15）
            Case("栈式跨界 直接递归逐层挂起",
                "import core.io.Console\n" +
                "func down(n: i32): i32 {\n" +
                "    if (n <= 0) {\n" +
                "        return 0\n" +
                "    }\n" +
                "    yield\n" +
                "    return n + down(n - 1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = down(5)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑤ 互递归 tainted fn（isOdd/isEven 经 yield 互挂）：
            // taint 不动点闭包覆盖环
            Case("栈式跨界 互递归挂起",
                "import core.io.Console\n" +
                "func isOdd(n: i32): bool {\n" +
                "    if (n <= 0) {\n" +
                "        return false\n" +
                "    }\n" +
                "    yield\n" +
                "    return isEven(n - 1)\n" +
                "}\n" +
                "func isEven(n: i32): bool {\n" +
                "    if (n <= 0) {\n" +
                "        return true\n" +
                "    }\n" +
                "    yield\n" +
                "    return isOdd(n - 1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = isOdd(7)\n" +
                "    var b = isEven(8)\n" +
                "    Console.println(a.toString() + b.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑥ 异常跨多层链 + try/finally：tainted 三层（top→
            // mid(try/finally)→deep(await 后抛)）——FAILED 沿链上传，
            // 每层传播垫配平释放，finally 在逐层展开时执行
            Case("栈式跨界 异常多层传播 try/finally",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func deep(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    if (r > 10) {\n" +
                "        throw new core.RuntimeException(\"deep-\" + r.toString())\n" +
                "    }\n" +
                "    return r\n" +
                "}\n" +
                "func mid(n: i32): i32 {\n" +
                "    try {\n" +
                "        return deep(n)\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin-mid\")\n" +
                "    }\n" +
                "}\n" +
                "func top(n: i32): i32 {\n" +
                "    var v = mid(n)\n" +
                "    return v + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var r = top(8)\n" +
                "        Console.println(r.toString())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑦ using 穿越 tainted fn 挂起点：seq using 的 dispose
            // 在恢复后续行时正常执行（RcInjection 托管槽跨挂起配平）
            Case("栈式跨界 using 穿越挂起点",
                "import core.io.Console\n" +
                "pub shared class Res implements core.IDisposable {\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "    pub override func dispose() {\n" +
                "        Console.println(\"dispose \" + tag.toString())\n" +
                "    }\n" +
                "}\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n + 1\n" +
                "}\n" +
                "func useIt(n: i32): i32 {\n" +
                "    seq using(const r = new Res(n)) {\n" +
                "        var t = slow(n)\n" +
                "        var v = await t\n" +
                "        return v * 10\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = useIt(3)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑧ void main 含挂起点：settle 恒 0（无 result 解包），
            // stdout 次序对齐 VM
            Case("栈式跨界 void main 裸 yield",
                "import core.io.Console\n" +
                "pub func main() {\n" +
                "    yield\n" +
                "    Console.println(\"void-main\")\n" +
                "}\n"),
            // B-2⑨ yield Alarm（EventAlarm/sleep）在 tainted 普通 fn：
            // B-2 起放开（plain 恢复闸无 TaskState 投影），sleep 唤醒
            // 后续行 42
            Case("栈式跨界 普通 fn yield sleep",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func nap(n: i32): i32 {\n" +
                "    yield sleep(1)\n" +
                "    return n + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = nap(41)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑩ yield PollingAlarm 在 tainted 普通 fn：arm 协程
            // 裸 yield 后翻牌——覆盖未就绪→退避（poll_schedule 再挂）
            // →就绪路径（plain 恢复闸无投影跳过）；探测节奏跨端不必
            // 一致（§19.2 退避非语言语义），只断言恢复事实
            Case("栈式跨界 普通 fn yield PollingAlarm 退避翻牌",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Flip : PollingAlarm {\n" +
                "    pub var ready: bool = false\n" +
                "    pub override func isReady(): bool { return ready }\n" +
                "}\n" +
                "async func arm(f: Flip) {\n" +
                "    yield\n" +
                "    f.ready = true\n" +
                "}\n" +
                "func waitFlip(f: Flip) {\n" +
                "    yield f\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flip()\n" +
                "    arm(f)\n" +
                "    waitFlip(f)\n" +
                "    Console.println(\"recovered\")\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑪ isReady 抛异常经 plain 链上传（B-2 plain 失败尾：
            // probe ret -1 pending 保持置位 → 恢复闸 ret FAILED →
            // 调用点 FAILED 臂取走重抛 → main try/catch 捕获；对齐
            // VM 帧栈逐层展开口径）
            Case("栈式跨界 普通 fn isReady 异常沿链捕获",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Boom : PollingAlarm {\n" +
                "    pub init() { }\n" +
                "    pub override func isReady(): bool {\n" +
                "        throw new core.RuntimeException(\"probe\")\n" +
                "    }\n" +
                "}\n" +
                "func waitBoom(b: Boom) {\n" +
                "    yield b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Boom()\n" +
                "    try {\n" +
                "        waitBoom(b)\n" +
                "        Console.println(\"miss\")\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑫ Mutex 竞争挂起跨 tainted 链：普通 fn await
            // m.acquire()（enter 在 async acquire 体内——priv enter
            // 用户不可直调，语言级 plain fn 无法直含 Mutex.enter）；
            // 双 worker 竞争同锁，结果配平 14（memtrack 零泄漏）
            Case("栈式跨界 Mutex 竞争跨链配平",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func critical(m: Mutex, n: i32): i32 {\n" +
                "    var g = await m.acquire()\n" +
                "    yield\n" +
                "    var r = n * 2\n" +
                "    m.release(g)\n" +
                "    return r\n" +
                "}\n" +
                "async func worker(m: Mutex, n: i32): i32 {\n" +
                "    return critical(m, n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Mutex()\n" +
                "    var a = worker(m, 3)\n" +
                "    var b = worker(m, 4)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    Console.println((x + y).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑬ 虚调用链挂起点·混合闭包：Base.step 含 yield
            //（tainted），Derived.step 普通——调用点动态分流：Base
            // 实例走协议（7），Derived 实例落原虚调用（51）
            Case("栈式跨界 虚调用混合闭包动态分流",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Base, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = drive(new Base(), 5)\n" +
                "    var b = drive(new Derived(), 5)\n" +
                "    Console.println((a.toString() + \",\") + b.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑭ 虚调用链挂起点·全 tainted 闭包：两个实现臂都走
            // 协议（最深派生优先 type.is 分流，各实现 frame 独立）
            Case("栈式跨界 虚调用全 tainted 闭包",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Base, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = drive(new Base(), 5)\n" +
                "    var b = drive(new Derived(), 5)\n" +
                "    Console.println((a.toString() + \",\") + b.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑮ interface 调用链挂起点：iMap 闭包分流（Slow
            // tainted 走协议 7，Fast 普通落原调用 51）
            Case("栈式跨界 interface 调用动态分流",
                "import core.io.Console\n" +
                "pub interface IStepper {\n" +
                "    func step(n: i32): i32\n" +
                "}\n" +
                "pub class Slow implements IStepper {\n" +
                "    pub init() { }\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Fast implements IStepper {\n" +
                "    pub init() { }\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(s: IStepper, n: i32): i32 {\n" +
                "    return s.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = drive(new Slow(), 5)\n" +
                "    var b = drive(new Fast(), 5)\n" +
                "    Console.println((a.toString() + \",\") + b.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑯ super 调用含挂起点的基类实现：super 恒直调（静态
            // 唯一目标），同直调协议；override 体内两次 super 调用
            Case("栈式跨界 super 调用基类挂起实现",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        var a = super(n)\n" +
                "        var b = super(n)\n" +
                "        return a + b\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Derived()\n" +
                "    var r = d.step(10)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑰ 异常经虚派发链上传：Base.step（tainted 臂）恢复
            // 后抛出 → FAILED 沿链 → main try/catch 捕获；Derived
            // 普通臂不受影响
            Case("栈式跨界 虚调用异常沿链捕获",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        if (n > 10) {\n" +
                "            throw new core.RuntimeException(\"base-\" + n.toString())\n" +
                "        }\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Base, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var r = drive(new Base(), 20)\n" +
                "        Console.println(r.toString())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    var ok = drive(new Derived(), 5)\n" +
                "    Console.println(ok.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑱ async 调用方经虚派发链挂起：run 直调 tainted
            // drive（虚点），整条栈同步挂起，裸返回求和 58
            Case("栈式跨界 async 经虚链同步挂起",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Base, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "async func run(): i32 {\n" +
                "    var x = drive(new Base(), 5)\n" +
                "    var y = drive(new Derived(), 5)\n" +
                "    return x + y\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑲ 含挂起点的 init：构造点分配与 init 下钻分离
            //（合成空 init 分配 + init.wrapper 原位缝合 + init frame
            // .this = 新建对象），恢复后字段写入可见 42
            Case("栈式跨界 init 内挂起",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) {\n" +
                "        yield\n" +
                "        v = x * 2\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var n = new Node(21)\n" +
                "    Console.println(n.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑳ 字段初始值缝合 + init 挂起：init.wrapper 在挂起
            // init 之前原位跑完（v=5 先落，init 恢复后 v+x），跨
            // tainted 工厂 fn 两实例 15,25
            Case("栈式跨界 init 挂起字段初始值缝合",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var v: i32 = 5\n" +
                "    pub init(x: i32) {\n" +
                "        yield\n" +
                "        v = v + x\n" +
                "    }\n" +
                "}\n" +
                "func make(x: i32): Node {\n" +
                "    var n = new Node(x)\n" +
                "    return n\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = make(10)\n" +
                "    var b = make(20)\n" +
                "    Console.println((a.v.toString() + \",\") + b.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2㉑ init 恢复后抛异常经构造链 FAILED 上传：tainted
            // 工厂 fn 内的构造点未捕获 → 沿调用点 FAILED 臂 → main
            // try/catch 捕获（caught:big-20 + 3）
            Case("栈式跨界 init 异常沿链捕获",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) {\n" +
                "        yield\n" +
                "        if (x > 10) {\n" +
                "            throw new core.RuntimeException(\"big-\" + x.toString())\n" +
                "        }\n" +
                "        v = x\n" +
                "    }\n" +
                "}\n" +
                "func make(x: i32): Node {\n" +
                "    var n = new Node(x)\n" +
                "    return n\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a = make(20)\n" +
                "        Console.println(a.v.toString())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    var ok = make(3)\n" +
                "    Console.println(ok.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2㉒ class 运算符 intrinsic 派发含挂起点：单类闭包
            // 臂走协议（operator plus 内 yield），求值 42
            Case("栈式跨界 运算符派发挂起",
                "import core.io.Console\n" +
                "pub class Acc {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "    pub operator plus(other: Acc): Acc {\n" +
                "        yield\n" +
                "        return new Acc(v + other.v)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Acc(10)\n" +
                "    var b = new Acc(32)\n" +
                "    var c = a + b\n" +
                "    Console.println(c.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2㉓ 泛型值类型方法的类级 typeid 落参合成（§7.2 隐
            // 藏参数）：宿主闭合构造 PairBox<i32> → MirGetTypeId 常
            // 量 typeid 落 callee frame，恢复后读字段 42
            Case("栈式跨界 泛型值类型方法隐藏参数",
                "import core.io.Console\n" +
                "pub struct PairBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(x: T) { v = x }\n" +
                "    pub func get(): T {\n" +
                "        yield\n" +
                "        return v\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new PairBox\\<i32>(40)\n" +
                "    var r = b.get()\n" +
                "    Console.println((r + 2).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2㉔ using dispose 可挂起（BIL §17.3）：dispose 内
            // yield——正常 return 路径与异常展开路径的 dispose 均经
            // 虚派发臂协议化挂起/恢复，次序与 VM 对齐（dispose 先于
            // 续行/捕获打印）
            Case("栈式跨界 using dispose 可挂起双路径",
                "import core.io.Console\n" +
                "pub shared class Res implements core.IDisposable {\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "    pub override func dispose() {\n" +
                "        yield\n" +
                "        Console.println(\"dispose \" + tag.toString())\n" +
                "    }\n" +
                "}\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n + 1\n" +
                "}\n" +
                "func useIt(n: i32): i32 {\n" +
                "    seq using(const r = new Res(n)) {\n" +
                "        var t = slow(n)\n" +
                "        var v = await t\n" +
                "        return v * 10\n" +
                "    }\n" +
                "}\n" +
                "func boomIt(n: i32): i32 {\n" +
                "    seq using(const r = new Res(n + 100)) {\n" +
                "        var t = slow(n)\n" +
                "        var v = await t\n" +
                "        throw new core.RuntimeException(\"mid-\" + v.toString())\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = useIt(3)\n" +
                "    Console.println(a.toString())\n" +
                "    try {\n" +
                "        var b = boomIt(4)\n" +
                "        Console.println(b.toString())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // R2-a㉕ 泛型宿主虚派发链挂起点：臂条件扩展为「模板空
            // 壳 + 模块内全部闭合构造 sheet」OR 链（泛型实例头是构
            // 造 sheet，其基链不含模板空壳）；类级 typeid 落参从接
            // 收者实例隐藏字段运行期读取（静态构造形态被接收者
            // cast 剥成裸模板）——Box<i32> 模板臂走协议 7、
            // DerivedBox<i32> 普通臂落原虚调用 51、IntBox（非泛型
            // 派生自 Box<i32>）经构造基链命中 106
            Case("栈式跨界 泛型宿主虚派发动态分流",
                "import core.io.Console\n" +
                "pub open class Box\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class DerivedBox\\<T> : Box\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "pub class IntBox : Box\\<i32> {\n" +
                "    pub init() { }\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 100\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Box\\<i32>, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = drive(new Box\\<i32>(), 5)\n" +
                "    var b = drive(new DerivedBox\\<i32>(), 5)\n" +
                "    var c = drive(new IntBox(), 5)\n" +
                "    Console.println((((a.toString() + \",\") + b.toString()) + \",\") + c.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-a㉖ 泛型宿主挂起实现使用 T（类级 typeid 运行期读
            // 取实证）：id(v: T): T 的 .generic.T 落参取自接收者实
            // 例隐藏 typeid 字段——i32 与 String 两种构造各经模板
            // 臂协议恢复后正确拆箱/装箱
            Case("栈式跨界 泛型宿主挂起实现运行期 typeid",
                "import core.io.Console\n" +
                "pub open class Box\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub open func id(v: T): T {\n" +
                "        yield\n" +
                "        return v\n" +
                "    }\n" +
                "}\n" +
                "func driveId(b: Box\\<i32>, v: i32): i32 {\n" +
                "    return b.id(v) + 1\n" +
                "}\n" +
                "func driveStr(b: Box\\<String>, v: String): String {\n" +
                "    return b.id(v) + \"!\"\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(driveId(new Box\\<i32>(), 41).toString())\n" +
                "    Console.println(driveStr(new Box\\<String>(), \"ok\"))\n" +
                "    return 0\n" +
                "}\n"),
            // R2-b㉗ invoke.indirect 挂起点（callable 协议 $$call
            // 闭包动态分流，同虚派发臂机制）：含 yield 的 lambda 经
            // fn 值变量调用走 tainted 臂协议 42
            Case("栈式跨界 lambda 间接调用挂起",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{(x: i32): i32 -> {\n" +
                "        yield\n" +
                "        return@_ x + 1\n" +
                "    }}\n" +
                "    var r = f(41)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-b㉘ invoke.indirect 混合闭包：Func<i32,i32> 形参
            // 站点——tainted lambda（slow）走协议臂 7，普通 lambda
            //（fast）落默认臂原 invoke.indirect 51；async lambda
            // 回归（Task 通道不受影响）41
            Case("栈式跨界 间接调用混合闭包动态分流",
                "import core.io.Console\n" +
                "func apply(f: Func\\<i32, i32>, x: i32): i32 {\n" +
                "    return f(x) + 1\n" +
                "}\n" +
                "async func run() {\n" +
                "    var slow = func{(x: i32): i32 -> {\n" +
                "        yield\n" +
                "        return@_ x + 1\n" +
                "    }}\n" +
                "    var fast = func{(x: i32): i32 -> (x * 10)}\n" +
                "    var a = apply(slow, 5)\n" +
                "    var b = apply(fast, 5)\n" +
                "    Console.println((a.toString() + \",\") + b.toString())\n" +
                "    const g = func{async (): i32 -> 41}\n" +
                "    const n = await g()\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // R2-b㉙ 捕获 lambda 挂起：捕获 delta 的两个闭包实例
            // 各自建 frame 下钻（callee frame 含 .capture.this 借用
            // 字段），结果 15,105
            Case("栈式跨界 捕获 lambda 间接调用挂起",
                "import core.io.Console\n" +
                "func makeAdder(delta: i32): Func\\<i32, i32> {\n" +
                "    return func{(x: i32): i32 -> {\n" +
                "        yield\n" +
                "        return@_ x + delta\n" +
                "    }}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var add10 = makeAdder(10)\n" +
                "    var add100 = makeAdder(100)\n" +
                "    Console.println((add10(5).toString() + \",\") + add100(5).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-c㉚ new.indirect × tainted class init（分发点本身
            // 成为调用方挂起点）：精确 sheet 臂（IsTypeId ∧ 派生排
            // 除）命中 → 空 init 分配 + init frame 下钻，恢复后 41
            Case("栈式跨界 new.indirect init 内挂起",
                "import core.io.Console\n" +
                "pub class Slow {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        x = v\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Slow)\n" +
                "    var o = new t(41)\n" +
                "    Console.println(o.x.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-c㉛ new.indirect 混合目标矩阵：tainted init 臂（41）
            // / 默认臂普通 init（82）/ 派生类自声明 init 经派生排除
            // 落默认臂（141）/ 无匹配 init NoSuchMethod（nomatch）/
            // tainted init 恢复后抛出沿构造异常边被同 fn 捕获
            Case("栈式跨界 new.indirect 混合目标矩阵",
                "import core.io.Console\n" +
                "pub open class Slow {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        x = v\n" +
                "    }\n" +
                "}\n" +
                "pub class Fast {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v * 2 }\n" +
                "}\n" +
                "pub class Derived : Slow {\n" +
                "    pub init(v: i32) { x = v + 100 }\n" +
                "}\n" +
                "pub class NoInit : Slow {\n" +
                "}\n" +
                "pub class Boom {\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        throw new core.RuntimeException(\"boom-\" + v.toString())\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t1 = typeOf(Slow)\n" +
                "    var o1 = new t1(41)\n" +
                "    Console.println(o1.x.toString())\n" +
                "    var t2 = typeOf(Fast)\n" +
                "    var o2 = new t2(41)\n" +
                "    Console.println(o2.x.toString())\n" +
                "    var t3 = typeOf(Derived)\n" +
                "    var o3 = new t3(41)\n" +
                "    Console.println(o3.x.toString())\n" +
                "    try {\n" +
                "        var t4 = typeOf(NoInit)\n" +
                "        var o4 = new t4(41)\n" +
                "        Console.println(o4.x.toString())\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        Console.println(\"nomatch\")\n" +
                "    }\n" +
                "    try {\n" +
                "        var t5 = typeOf(Boom)\n" +
                "        var o5 = new t5(7)\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // R2-c㉜ new.indirect 在 async 调用方（Tasked 协议）+
            // 泛型宿主构造 sheet 臂（类级 typeid 按臂构造形态常量
            // 合成）——9 / 42
            Case("栈式跨界 new.indirect async 调用方与泛型宿主",
                "import core.io.Console\n" +
                "pub class Slow {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        x = v\n" +
                "    }\n" +
                "}\n" +
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(x: T) {\n" +
                "        yield\n" +
                "        v = x\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var t = typeOf(Slow)\n" +
                "    var o = new t(9)\n" +
                "    Console.println(o.x.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    var proto = new Box\\<i32>(1)\n" +
                "    var tb = typeOf(proto)\n" +
                "    var b: Box\\<i32> = new tb(42)\n" +
                "    Console.println(b.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-d㉝ 同 fn try 内构造抛出双端对齐：MirNewObject
            // 补 ExcTarget 异常边（历史「pending 推迟到下一检查点」
            // 形态消除）——① 同步 init 抛出立即沿 try 边捕获
            //（caught-sync）；② 挂起 init 恢复后 FAILED 沿构造异
            // 常边进本 fn 捕获（caught-fin），finally 次序对齐 VM
            Case("栈式跨界 try 内构造抛出双端对齐",
                "import core.io.Console\n" +
                "pub class SyncBoom {\n" +
                "    pub init(v: i32) {\n" +
                "        throw new core.RuntimeException(\"sync-\" + v.toString())\n" +
                "    }\n" +
                "}\n" +
                "pub class Boom {\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        throw new core.RuntimeException(\"boom-\" + v.toString())\n" +
                "    }\n" +
                "}\n" +
                "func makeSync(): i32 {\n" +
                "    try {\n" +
                "        var o = new SyncBoom(3)\n" +
                "        return 1\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught-sync\")\n" +
                "        return 2\n" +
                "    }\n" +
                "}\n" +
                "func makeFinally(): i32 {\n" +
                "    try {\n" +
                "        var o = new Boom(9)\n" +
                "        return 1\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught-fin\")\n" +
                "        return 2\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"finally\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(makeSync().toString())\n" +
                "    Console.println(makeFinally().toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊱ 泛型接口方法 iMap 派发：默认体的 fn 定义携带 §7.2
            // 方法级 typeid 隐藏参数，thunk fn 类型必须以 fn 体为准
            //（合成签名会漏 typeid 参数，调用约定错配）。① 无挂起点
            //（默认体路径，r=42）
            Case("泛型接口方法 iMap 派发（默认体）",
                "import core.io.Console\n" +
                "pub interface IMapper {\n" +
                "    func map\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub class IntBox implements IMapper {\n" +
                "    pub init() {}\n" +
                "}\n" +
                "func runIt(m: IMapper): i32 {\n" +
                "    var r = m.map\\<i32>(42)\n" +
                "    return (r as i32)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m: IMapper = new IntBox()\n" +
                "    Console.println(\"r=\" + runIt(m).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ② 无挂起点（类 override 覆盖默认体，r=43）
            Case("泛型接口方法 iMap 派发（override）",
                "import core.io.Console\n" +
                "pub interface IMapper {\n" +
                "    func map\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub class IntBox implements IMapper {\n" +
                "    pub init() {}\n" +
                "    pub override func map\\<T>(x: T): T { return (((x as i32) + 1) as T) }\n" +
                "}\n" +
                "func runIt(m: IMapper): i32 {\n" +
                "    var r = m.map\\<i32>(42)\n" +
                "    return (r as i32)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m: IMapper = new IntBox()\n" +
                "    Console.println(\"r=\" + runIt(m).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ③ 含挂起点：async 调用方内 iMap 派发泛型接口方法
            //（统一切分路径同 emitter，调用约定一致）
            Case("泛型接口方法 iMap 派发（含挂起点）",
                "import core.io.Console\n" +
                "pub interface IMapper {\n" +
                "    func map\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub class IntBox implements IMapper {\n" +
                "    pub init() {}\n" +
                "}\n" +
                "async func one(): i32 { return 1 }\n" +
                "async func runIt(): i32 {\n" +
                "    var m: IMapper = new IntBox()\n" +
                "    var t = one()\n" +
                "    var k = await t\n" +
                "    var r = m.map\\<i32>((41 + k))\n" +
                "    Console.println(\"r=\" + (r as i32).toString())\n" +
                "    return 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = runIt()\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊲ MirNewValue 补 ExcTarget（同 R2-d MirNewObject
            // 口径）：值类型 init 同步抛出沿本 fn try 异常边捕获
            //（修复前 pending 推迟致异常逃逸出 fn，native exit=1
            // 而 VM 于 fn 内捕获）
            Case("值类型 init 同步抛出同 fn try 捕获",
                "import core.io.Console\n" +
                "pub struct VBoom {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) {\n" +
                "        throw new core.RuntimeException(\"vboom\")\n" +
                "    }\n" +
                "}\n" +
                "func make(): i32 {\n" +
                "    try {\n" +
                "        var o = new VBoom(3)\n" +
                "        return 1\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "        return 2\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(make().toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊳ open struct 继承链：占位接收者子类型盒拆箱（R1
            // 边界清偿）——ValueTypeLayout 补 struct 继承布局（基类
            // 字段前缀/尺寸/refMap/basePlan 链），拆箱守卫改 rigi_type_is
            // 协变链，写回原地补丁保身份（42/still-child 双端一致）
            Case("占位接收者：open struct 子类型盒变异写回",
                "import core.io.Console\n" +
                "pub open rich struct Base {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "    pub func bump() { v = (v + 1) }\n" +
                "}\n" +
                "pub rich struct Child : Base {\n" +
                "    pub init(x: i32) { super(x) }\n" +
                "}\n" +
                "pub class Box2\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "func useIt\\<T extends Base>(b: Box2\\<T>) { b.item.bump() }\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box2\\<Child>(new Child(41))\n" +
                "    useIt\\<Child>(b)\n" +
                "    Console.println(b.item.v.toString())\n" +
                "    if (b.item is Child) {\n" +
                "        Console.println(\"still-child\")\n" +
                "    } else {\n" +
                "        Console.println(\"sliced\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊴ 子类型盒 + 变异方法抛异常：异常路径不写回（VM copy
            // 语义——部分变异不可见）且身份保留（caught/41/still-child）
            Case("占位接收者：子类型盒异常路径不写回",
                "import core.io.Console\n" +
                "pub open rich struct Base {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "    pub func boom() {\n" +
                "        v = 99\n" +
                "        throw new core.RuntimeException(\"boom\")\n" +
                "    }\n" +
                "}\n" +
                "pub rich struct Child : Base {\n" +
                "    pub init(x: i32) { super(x) }\n" +
                "}\n" +
                "pub class Box2\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "func useIt\\<T extends Base>(b: Box2\\<T>) {\n" +
                "    try {\n" +
                "        b.item.boom()\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box2\\<Child>(new Child(41))\n" +
                "    useIt\\<Child>(b)\n" +
                "    Console.println(b.item.v.toString())\n" +
                "    if (b.item is Child) {\n" +
                "        Console.println(\"still-child\")\n" +
                "    } else {\n" +
                "        Console.println(\"sliced\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊵ 值类型沿 open struct 继承链向上转换：cast 不再无条件
            // 抛 CastException（此前 EmitFail 判死），前缀切片数据对齐
            // VM（42）。已知残留差：经具体类型航点（Base 槽）后运行期
            // 身份不保留（native 内联值 ABI 槽按静态类型定尺寸；VM 值
            // 自我描述恒保身份）——故此用例只断言数据路径
            Case("open struct 值类型向上转换数据路径",
                "import core.io.Console\n" +
                "pub open rich struct Base {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "    pub func bump() { v = (v + 1) }\n" +
                "}\n" +
                "pub rich struct Child : Base {\n" +
                "    pub init(x: i32) { super(x) }\n" +
                "}\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var x: T\n" +
                "    pub init(v: T) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Holder\\<Base>(new Child(41))\n" +
                "    h.x.bump()\n" +
                "    Console.println(h.x.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ⑧ async 无挂起点 fn（统一切分后直跑到底）
            Case("协程无挂起点 async fn",
                "import core.io.Console\n" +
                "async func straight(): i32 { return 7 }\n" +
                "async func run() {\n" +
                "    var t = straight()\n" +
                "    var n = await t\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑨ 泛型类 async 方法（类级 typeid 捕获进 frame）+ 泛型
            // async fn（方法级 typeid 跨挂起）
            Case("协程泛型类 async 方法 + 泛型 async fn",
                "import core.io.Console\n" +
                "shared class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(x: T) { v = x }\n" +
                "    pub async func get(): T {\n" +
                "        yield\n" +
                "        return v\n" +
                "    }\n" +
                "}\n" +
                "async func one(): i32 { return 1 }\n" +
                "async func echo\\<T>(x: T): T {\n" +
                "    var t = one()\n" +
                "    await t\n" +
                "    return x\n" +
                "}\n" +
                "async func run() {\n" +
                "    var b = new Box\\<String>(\"boxed\")\n" +
                "    var g = b.get()\n" +
                "    var s = await g\n" +
                "    var e = echo\\<String>(s + \"!\")\n" +
                "    var r = await e\n" +
                "    Console.println(r)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑩ 协程树（VM TestCoroutineStressForkJoin tree(7)=128
            // 母本）：断言最终求和值
            Case("协程树 tree(7)=128",
                "import core.io.Console\n" +
                "async func tree(n: i32): i32 {\n" +
                "    if (n <= 0) {\n" +
                "        return 1\n" +
                "    }\n" +
                "    var left = tree(n - 1)\n" +
                "    var right = tree(n - 1)\n" +
                "    var a = await left\n" +
                "    var b = await right\n" +
                "    return a + b\n" +
                "}\n" +
                "async func run() {\n" +
                "    var root = tree(7)\n" +
                "    var n = await root\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW11b 棒3：yield Alarm 对拍（VM 基准已逐例实测；
            // 计时断言全部避免墙钟——sleep 毫秒只作唤醒源，断言落
            // stdout 次序/最终值；RIGI_RT_MEMTRACK=1 零泄漏口径）=====
            // ⑪ yield sleep 基础 + fork/join 求和 42（VM
            // BilVmWakeupTests sleep(1) 端到端母本）
            Case("协程 yield sleep fork/join 42",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func nap(n: i32): i32 {\n" +
                "    yield sleep(1)\n" +
                "    return n + 1\n" +
                "}\n" +
                "async func run() {\n" +
                "    var a = nap(19)\n" +
                "    var b = nap(20)\n" +
                "    var c = nap(0)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    var z = await c\n" +
                "    Console.println(((x + y) + z).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑫ sleep(0) 立即触发仍结束执行段（对齐 VM Arm(<=0) →
            // Publish 语义）：立即触发 = 不死锁不挂起即恢复完成；跨
            // 协程交错次序在 VM ThreadPool 下不确定，打印只放同协程
            // 程序序（a1→a2）与 await 数据依赖（done 在终态后）确定处
            Case("协程 sleep(0) 立即触发仍完成",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func a(): i32 {\n" +
                "    Console.println(\"a1\")\n" +
                "    yield sleep(0)\n" +
                "    Console.println(\"a2\")\n" +
                "    return 3\n" +
                "}\n" +
                "async func run() {\n" +
                "    var t = a()\n" +
                "    var n = await t\n" +
                "    Console.println(\"done \" + n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑬ 双协程不同毫秒 sleep 的完成次序（10ms vs 300ms 给足
            // 余量；断言次序串，无墙钟断言）
            Case("协程双毫秒 sleep 完成次序",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func slow() {\n" +
                "    yield sleep(300)\n" +
                "    Console.println(\"slow\")\n" +
                "}\n" +
                "async func fast() {\n" +
                "    yield sleep(10)\n" +
                "    Console.println(\"fast\")\n" +
                "}\n" +
                "async func run() {\n" +
                "    slow()\n" +
                "    fast()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑭ PollingAlarm 翻牌（VM BilVmTests Flip 母本）两形态：
            // (a) 先翻牌后 yield——双端首探即中，isReady 探测计数恰 1
            //     锁死（次数语义）；(b) arm 协程裸 yield 后翻牌——覆盖
            //     未就绪→退避→就绪路径；探测节奏（每执行段末一轮 vs VM
            //     恢复点探测）是 §19.2 实现选择（VM 文档明言退避非语言
            //     语义），跨端次数不必一致，本形态只断言恢复事实
            Case("协程 PollingAlarm 首探即中",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Flip : PollingAlarm {\n" +
                "    pub var ready: bool = false\n" +
                "    pub var probes: i32 = 0\n" +
                "    pub override func isReady(): bool {\n" +
                "        probes = probes + 1\n" +
                "        return ready\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var f = new Flip()\n" +
                "    f.ready = true\n" +
                "    yield f\n" +
                "    Console.println(\"polled \" + f.probes.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 PollingAlarm 退避翻牌",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Flip : PollingAlarm {\n" +
                "    pub var ready: bool = false\n" +
                "    pub override func isReady(): bool { return ready }\n" +
                "}\n" +
                "async func arm(f: Flip) {\n" +
                "    yield\n" +
                "    f.ready = true\n" +
                "}\n" +
                "async func run() {\n" +
                "    var f = new Flip()\n" +
                "    arm(f)\n" +
                "    yield f\n" +
                "    Console.println(\"polled\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== L8：用户 EventAlarm 直继子类默认底座（§19.3）=====
            // 无时钟底座的用户子类经 ensureHandle 懒建手动事件粘滞
            // 底座，protected signal() 为事件源触发入口；VM/native
            // 同源码对拍（断言落最终结果，不锁跨协程交错次序）
            // ⑭c signal 唤醒双 waiter：两 waiter 挂同一 Gate，opener
            //     sleep 后 open() 触发——waiter 登记与 signal 的先后
            //     序由粘滞兜底（先触发后登记也不丢）；和 13 次序无关
            Case("协程 用户EventAlarm signal 唤醒双 waiter",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Gate : EventAlarm {\n" +
                "    pub func fire() { signal() }\n" +
                "}\n" +
                "async func waiter(g: Gate, n: i32): i32 {\n" +
                "    yield\n" +
                "    yield g\n" +
                "    return n + 1\n" +
                "}\n" +
                "async func opener(g: Gate) {\n" +
                "    yield sleep(30)\n" +
                "    g.fire()\n" +
                "}\n" +
                "async func run() {\n" +
                "    var g = new Gate()\n" +
                "    var a = waiter(g, 1)\n" +
                "    var b = waiter(g, 10)\n" +
                "    opener(g)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    Console.println((x + y).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑭d 先 signal 后 yield 不丢（粘滞）：signal 时无 waiter
            //     → 恒置已触发；迟到 yield 立即具备重新发布条件（仍
            //     结束当前执行段）
            Case("协程 用户EventAlarm 先signal后yield不丢",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Gate : EventAlarm {\n" +
                "    pub func fire() { signal() }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var g = new Gate()\n" +
                "    g.fire()\n" +
                "    yield g\n" +
                "    Console.println(\"not lost\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑭e 重复 signal 幂等 + 二次 yield 仍粘滞（§19.3：实例
            //     保持已触发状态，重复触发幂等）
            Case("协程 用户EventAlarm 重复signal幂等粘滞",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Gate : EventAlarm {\n" +
                "    pub func fire() { signal() }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var g = new Gate()\n" +
                "    g.fire()\n" +
                "    g.fire()\n" +
                "    yield g\n" +
                "    g.fire()\n" +
                "    yield g\n" +
                "    Console.println(\"idempotent sticky\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑮ using 清理穿越 alarm yield（ASYNC §8 验收项）：正常
            // 路径 return 与异常路径 throw 的 dispose 均执行且次序正确
            //（dispose 先于 Task 终态 → 先于 await 续行打印）
            Case("协程 using 清理穿越 alarm yield 双路径",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Res implements core.IDisposable {\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "    pub override func dispose() {\n" +
                "        Console.println(\"dispose \" + tag.toString())\n" +
                "    }\n" +
                "}\n" +
                "async func work(): i32 {\n" +
                "    seq using(const r = new Res(1)) {\n" +
                "        yield sleep(1)\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n" +
                "async func boom(): i32 {\n" +
                "    seq using(const r = new Res(2)) {\n" +
                "        yield sleep(1)\n" +
                "        throw new core.RuntimeException(\"x\")\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var t = work()\n" +
                "    var n = await t\n" +
                "    Console.println(n.toString())\n" +
                "    var b = boom()\n" +
                "    try {\n" +
                "        await b\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑯ isReady 抛异常 = yield 点失败（§19.2 第 6 条）：probe
            // ret -1（pending 保持置位）→ drain 取走 → Task FAILED →
            // await 点重抛捕获
            Case("协程 isReady 抛异常 await 重抛捕获",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Boom : PollingAlarm {\n" +
                "    pub init() { }\n" +
                "    pub override func isReady(): bool {\n" +
                "        throw new core.RuntimeException(\"probe\")\n" +
                "    }\n" +
                "}\n" +
                "async func waitIt() {\n" +
                "    var b = new Boom()\n" +
                "    yield b\n" +
                "}\n" +
                "async func run() {\n" +
                "    var t = waitIt()\n" +
                "    try {\n" +
                "        await t\n" +
                "        Console.println(\"miss\")\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW11c 棒5a 阶段4：跨 Executor 对拍（VM
            // BilVmTaskTests.TestCrossExecutorCombination 母本）。包进
            // async run()（B-1 起 main 可直接 await，此处保留回归
            // 写法）。先 Main 冷 Task（无懒起 Worker），再 Compute/IO
            // 懒起；join 回 Main；memtrack 零泄漏 =====
            Case("协程冷 Task Main join",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        return@_ 41\n" +
                "    } })\n" +
                "    t.run()\n" +
                "    const n = await t\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 ComputeExecutor 冷 Task join",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        return@_ 41\n" +
                "    } })\n" +
                "    t.run(new ComputeExecutor())\n" +
                "    const n = await t\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程跨 Executor Compute+IO join",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const c = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        return@_ 40\n" +
                "    } })\n" +
                "    c.run(new ComputeExecutor())\n" +
                "    const io = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        yield sleep(20)\n" +
                "        return@_ 2\n" +
                "    } })\n" +
                "    io.run(new IOExecutor())\n" +
                "    const a = await c\n" +
                "    const b = await io\n" +
                "                    Console.println((a + b).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 语言级 Mutex（§19.6）：VM 方法 hook / native CoroutineSplit
            // 改写 enter + Rigi release 真体。互斥：临界区内 yield 仍持
            // 锁，竞争者不得进入——否则日志交织成 "[a[b..." 形态
            Case("协程 Mutex 临界区不交织",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Log { pub var order: String = \"\" }\n" +
                "async func critical(m: Mutex, log: Log, tag: String) {\n" +
                "    const l = await m.acquire()\n" +
                "    log.order = (log.order + (\"[\" + tag))\n" +
                "    yield sleep(20)\n" +
                "    log.order = (log.order + (tag + \"]\"))\n" +
                "    m.release(l)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const m = new Mutex()\n" +
                "    const log = new Log()\n" +
                "    const a = critical(m, log, \"a\")\n" +
                "    const b = critical(m, log, \"b\")\n" +
                "    await a\n" +
                "    await b\n" +
                "    Console.println(log.order)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 Mutex 他锁令牌释放抛异常",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const m = new Mutex()\n" +
                "    const n = new Mutex()\n" +
                "    const l = await m.acquire()\n" +
                "    try {\n" +
                "        n.release(l)\n" +
                "        Console.println(\"no\")\n" +
                "    } catch (_: core.IllegalStateException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "    m.release(l)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // invoke.indirect async $$call：AsyncFunc 经变量调用产 Task，
            // 再 await。Mutex.runSynchronously 同通道（vtable 闭包也会
            // 把这条路径收进任何 `new Mutex()` 的 native 模块）
            Case("协程 await async lambda",
                "import core.io.Console\n" +
                "async func run() {\n" +
                "    const f = func{async (): i32 -> 41}\n" +
                "    const n = await f()\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 Mutex.runSynchronously 取值",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const m = new Mutex()\n" +
                "    const n = await m.runSynchronously\\<i32>(func{async (): i32 -> 7})\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程不透明 body 冷 Task",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "func wrapI(body: core.AsyncFunc\\<i32>): Task\\<i32> {\n" +
                "    return new Task\\<i32>(body)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const t = wrap(func{async () -> { Console.println(\"opaque\") }})\n" +
                "    await t\n" +
                "    const n = await wrapI(func{async (): i32 -> 9})\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 冷 Task body 的 $$call 沿 extends 链继承（Sub 自身无
            // $$call）：VM 拍平 sheet 解析；native 工厂/bindColdBody
            // 链同语义（MW11c 棒5a 残余面收口）
            Case("协程冷 Task body 继承 $$call",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared abstract class Act : core.AsyncAction {\n" +
                "    pub override async operator call() {\n" +
                "        Console.println(\"inherited\")\n" +
                "    }\n" +
                "}\n" +
                "pub shared class Sub : Act {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub shared abstract class ActI : core.AsyncFunc\\<i32> {\n" +
                "    pub override async operator call(): i32 {\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n" +
                "pub shared class SubI : ActI {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const t = new Task(new Sub())\n" +
                "    await t\n" +
                "    const u = wrap(new Sub())\n" +
                "    await u\n" +
                "    const n = await new Task\\<i32>(new SubI())\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 CoroutineLocal withValue/get",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const id = new CoroutineLocal\\<String>()\n" +
                "    const def = new CoroutineLocal\\<String>(\"def\")\n" +
                "    Console.println((id.get() == null).toString())\n" +
                "    Console.println(def.get() as String)\n" +
                "    await id.withValue(\"hi\", func{async () -> {\n" +
                "        Console.println(id.get() as String)\n" +
                "        await id.withValue(\"nest\", func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }})\n" +
                "        Console.println(id.get() as String)\n" +
                "    }})\n" +
                "    Console.println((id.get() == null).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 CoroutineLocal spawn 继承",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const id = new CoroutineLocal\\<String>()\n" +
                "    await id.withValue(\"x\", func{async () -> {\n" +
                "        const spawned = func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }}\n" +
                "        await spawned()\n" +
                "        const t = new Task(func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }})\n" +
                "        await t\n" +
                "        const u = wrap(func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }})\n" +
                "        await u\n" +
                "    }})\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("序列化平铺标量深复制",
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Mix {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub var f: double = 0.0\n" +
                "    pub var b: bool = false\n" +
                "    pub var c: char = 'x'\n" +
                "    pub var s: String = \"\"\n" +
                "    pub init(_ -> n, _ -> f, _ -> b, _ -> c, _ -> s)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Mix(1, 2.5, true, 'a', \"hi\")\n" +
                "    var copy = deepCopy\\<Mix>(src)\n" +
                "    src.n = 9\n" +
                "    src.s = \"bye\"\n" +
                "    if (((((copy.n == 1) and (copy.f == 2.5)) and (copy.b == true)) and (copy.c == 'a')) and (copy.s == \"hi\")) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("序列化嵌套对象图独立副本",
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Inner {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "@Serializable\n" +
                "pub class Outer {\n" +
                "    pub var a: Inner\n" +
                "    pub var b: Inner\n" +
                "    pub init(x: Inner) { a = x\n        b = x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var kid = new Inner(7)\n" +
                "    var src = new Outer(kid)\n" +
                "    var copy = deepCopy\\<Outer>(src)\n" +
                "    copy.a.n = 3\n" +
                "    if (((src.a.n == 7) and (copy.b.n == 7)) and (copy.a.n == 3)) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("序列化集合字段快照不变量",
                "import core.serialization.*\n" +
                "import core.collections.*\n" +
                "@Serializable\n" +
                "pub class Box {\n" +
                "    pub var nums: Array\\<i32>\n" +
                "    pub var names: List\\<String> = new List\\<String>()\n" +
                "    pub var ages: Map\\<String, i32> = new Map\\<String, i32>()\n" +
                "    pub init(_ -> nums)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var nums = arrayOfElements\\<i32>(1, 2)\n" +
                "    var src = new Box(nums)\n" +
                "    src.names.add(\"a\")\n" +
                "    src.ages.set(\"k\", 4)\n" +
                "    var copy = deepCopy\\<Box>(src)\n" +
                "    src.nums[0] = 99\n" +
                "    src.names.add(\"b\")\n" +
                "    src.ages.set(\"k\", 5)\n" +
                "    const cn = copy.nums[0]\n" +
                "    const cl = copy.names.getAtIndex(0L)\n" +
                "    const cm = copy.ages.tryGet(\"k\")\n" +
                "    if (((((cn if? 0) == 1) and ((cl if? \"\") == \"a\")) and ((cm if? 0) == 4)) and (copy.names.length == 1L)) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("序列化 Temporary 往返新 resume",
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Host {\n" +
                "    pub var n: i32 = 0\n" +
                "    @Temporary((func{ (): i32 -> {\n" +
                "        return@_ (n * 2)\n" +
                "    }} as core.Func\\<i32>))\n" +
                "    pub var derived: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Host(5)\n" +
                "    var copy = deepCopy\\<Host>(src)\n" +
                "    src.n = 9\n" +
                "    src.derived = 1\n" +
                "    if (((copy.n == 5) and (copy.derived == 10)) and (src.derived == 1)) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("序列化 with Serializable 泛型 clone",
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Marked {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "func clone\\<T with Serializable>(x: T): T {\n" +
                "    return deepCopy\\<T>(x)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Marked(11)\n" +
                "    var copy = clone\\<Marked>(src)\n" +
                "    src.n = 0\n" +
                "    if (copy.n == 11) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            // @SerializationBase 隐含 @Serializable：base-only 类 deepCopy 对拍
            Case("序列化 @SerializationBase 隐含 Serializable",
                "namespace core.serialization\n" +
                "@SerializationBase\n" +
                "pub class BaseOnly {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub var s: String = \"\"\n" +
                "    pub init(_ -> n, _ -> s)\n" +
                "}\n" +
                "@EntryPoint\n" +
                "pub func main(): i32 {\n" +
                "    var src = new BaseOnly(7, \"hi\")\n" +
                "    var copy = deepCopy\\<BaseOnly>(src)\n" +
                "    src.n = 9\n" +
                "    src.s = \"bye\"\n" +
                "    if ((copy.n == 7) and (copy.s == \"hi\")) {\n" +
                "        if ((src.n == 9) and (src.s == \"bye\")) {\n" +
                "            return 42\n" +
                "        }\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            // ===== MW11d-C MessageQueue 传输层对拍（冒烟）=====：
            // create/post/next/EOS/release 全链；深复制经 Parcel 往返
            // （post 完成后改源对象不影响已入队消息）；负载 shared class
            // + @Serializable（async 边界共享安全 §4.5 + 可复制 §3.1）
            Case("MessageQueue 冒烟：post/next/EOS/release 全链",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "@Serializable\n" +
                "pub shared class Greeting {\n" +
                "    pub var code: i32\n" +
                "    pub var text: String\n" +
                "    pub init(_ -> code, _ -> text)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Greeting>()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Greeting>(owner, QueueHandleType.Sender)\n" +
                "    const reader = MessageQueue.add_queue_handle\\<Greeting>(owner, QueueHandleType.Reader)\n" +
                "    const live = new Greeting(7, \"hello\")\n" +
                "    await MessageQueue.post(sender, live)\n" +
                "    live.code = 0\n" +
                "    live.text = \"mutated\"\n" +
                "    var item = await MessageQueue.next(reader)\n" +
                "    if (item.isEos) { Console.println(\"FAIL eos\") }\n" +
                "    var g = (item.item as Greeting)\n" +
                "    Console.println(g.code.toString())\n" +
                "    Console.println(g.text)\n" +
                "    await MessageQueue.post(sender, new Greeting(8, \"a\"))\n" +
                "    await MessageQueue.post(sender, new Greeting(9, \"b\"))\n" +
                "    item = await MessageQueue.next(reader)\n" +
                "    g = (item.item as Greeting)\n" +
                "    Console.println(g.code.toString())\n" +
                "    item = await MessageQueue.next(reader)\n" +
                "    g = (item.item as Greeting)\n" +
                "    Console.println(g.code.toString())\n" +
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    item = await MessageQueue.next(reader)\n" +
                "    if (item.isEos) { Console.println(\"EOS\") }\n" +
                "    MessageQueue.release_queue_handle(reader)\n" +
                "    Console.println(\"smoke ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW11d-Cb MessageQueue 对拍补齐（§28 电池）=====：
            // 与 Tests/BilVmTests.Messaging.cs 同款 Rigi 源；VM 为参考
            // 实现，native 须逐字节同 stdout/退出码
            // §11/§12 broadcast：两 reader 独立 cursor 见全部新消息、读速
            // 互不影响、新 reader 从队尾起、释放一个 reader 不影响兄弟
            Case("MessageQueue 对拍：broadcast 双 reader 独立 cursor",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                "    const ra = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    const rb = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    await MessageQueue.post(sender, new Msg(2))\n" +
                "    const a1 = await MessageQueue.next(ra)\n" +
                "    const a2 = await MessageQueue.next(ra)\n" +
                "    Console.println(((a1.item as Msg).n).toString())\n" +
                "    Console.println(((a2.item as Msg).n).toString())\n" +
                "    const b1 = await MessageQueue.next(rb)\n" +
                "    Console.println(((b1.item as Msg).n).toString())\n" +
                "    const rc = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    await MessageQueue.post(sender, new Msg(3))\n" +
                "    const c1 = await MessageQueue.next(rc)\n" +
                "    Console.println(((c1.item as Msg).n).toString())\n" +
                "    MessageQueue.release_queue_handle(rb)\n" +
                "    await MessageQueue.post(sender, new Msg(4))\n" +
                "    const a3 = await MessageQueue.next(ra)\n" +
                "    Console.println(((a3.item as Msg).n).toString())\n" +
                "    const c2 = await MessageQueue.next(rc)\n" +
                "    Console.println(((c2.item as Msg).n).toString())\n" +
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    MessageQueue.release_queue_handle(ra)\n" +
                "    MessageQueue.release_queue_handle(rc)\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // §9.2/§25 EOS 状态机：冷 Task next 挂起（Owner 活+0 Sender
            // 不 EOS）→ post 唤醒；Owner 去+Sender 活不 EOS；双去 sealed
            // 可 drain 后 EOS；sealed 后派生 Sender 拒绝（不可复活）
            Case("MessageQueue 对拍：EOS 状态机（挂起唤醒/sealed/drain/复活拒绝）",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "import core.messaging.QueueItem\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const reader = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                // 冷 Task（§18.4）：直接调 async 函数是热 Task，须
                // new Task(func{async ...}) 才能 run()
                "    const pending = new Task\\<QueueItem\\<Msg>>(func{async (): QueueItem\\<Msg> -> {\n" +
                "        return@_ (await MessageQueue.next\\<Msg>(reader))\n" +
                "    } })\n" +
                "    pending.run()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    const woken = await pending\n" +
                "    if (woken.isEos) {\n" +
                "        Console.println(\"FAIL early eos\")\n" +
                "    } else {\n" +
                "        Console.println((\"woken \" + ((woken.item as Msg).n).toString()))\n" +
                "    }\n" +
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    await MessageQueue.post(sender, new Msg(2))\n" +
                "    await MessageQueue.post(sender, new Msg(3))\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    const d1 = await MessageQueue.next(reader)\n" +
                "    Console.println(((d1.item as Msg).n).toString())\n" +
                "    const d2 = await MessageQueue.next(reader)\n" +
                "    Console.println(((d2.item as Msg).n).toString())\n" +
                "    const tail = await MessageQueue.next(reader)\n" +
                "    if (tail.isEos) { Console.println(\"EOS\") }\n" +
                "    try {\n" +
                "        const bad = MessageQueue.add_queue_handle\\<Msg>(reader, QueueHandleType.Sender)\n" +
                "        Console.println(\"FAIL revive\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    MessageQueue.release_queue_handle(reader)\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // §17 接受点（无 reader 也完成）+ §18 单 sender 顺序 +
            // §24 单 outstanding next 违规捕获。t1.run() 后裸 yield
            // 一圈：让 t1 内层 next 先 enter 入眠，t2 的 enter 才违规
            Case("MessageQueue 对拍：接受点+顺序+outstanding 违规捕获",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "import core.messaging.QueueItem\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    Console.println(\"post accepted no reader\")\n" +
                "    const reader = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    await MessageQueue.post(sender, new Msg(2))\n" +
                "    await MessageQueue.post(sender, new Msg(3))\n" +
                "    const i1 = await MessageQueue.next(reader)\n" +
                "    const i2 = await MessageQueue.next(reader)\n" +
                "    const i3 = await MessageQueue.next(reader)\n" +
                "    Console.println((((i1.item as Msg).n).toString() + ((i2.item as Msg).n).toString()) + ((i3.item as Msg).n).toString())\n" +
                "    const t1 = new Task\\<QueueItem\\<Msg>>(func{async (): QueueItem\\<Msg> -> {\n" +
                "        return@_ (await MessageQueue.next\\<Msg>(reader))\n" +
                "    } })\n" +
                "    t1.run()\n" +
                "    yield\n" +
                "    const t2 = MessageQueue.next(reader)\n" +
                "    try {\n" +
                "        const bad = await t2\n" +
                "        Console.println(\"FAIL outstanding\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    const eos = await t1\n" +
                "    if (eos.isEos) { Console.println(\"EOS\") }\n" +
                "    MessageQueue.release_queue_handle(reader)\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 违规直抛（不捕获）：Reader 上 post → IllegalStateException
            // 穿透 await 到 run 协程顶层（VM 未观察失败；native 顶层
            // reporter + exit 1）
            FailCase("MessageQueue 违规：Reader 上 post 直抛",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const reader = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    await MessageQueue.post(reader, new Msg(1))\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n",
                "该句柄不能 post"),
            // 重复释放：墓碑诊断直抛（同步路径，main 内直接触发）
            FailCase("MessageQueue 违规：重复释放句柄直抛",
                "import core.serialization.Serializable\n" +
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    return 0\n" +
                "}\n",
                "句柄重复释放"),
            // ===== MW11d-D Reader/Receiver/Messenger 高层 API 对拍 =====：
            // 与 Tests/BilVmTests.Messaging.cs 同款源；VM 参考，native
            // 逐字节同 stdout/退出码
            // Messenger 端到端 happy path：receiver 懒建 + listener 收
            // 消息 + 深复制快照（send 后改源对象）+ dispose → EOS 停泵
            Case("Messenger 对拍：send→receiver listener 深复制快照 + EOS 停泵",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.*\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub shared class Sink {\n" +
                "    pub var got: i32\n" +
                "    pub init() { got = 0 }\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const recv = msgr.receiver\n" +
                "    const sink = new Sink()\n" +
                "    recv.addListener(func{async (m: Msg) -> {\n" +
                "        sink.got = m.n\n" +
                "    } })\n" +
                "    const live = new Msg(7)\n" +
                "    await msgr.send(live)\n" +
                "    live.n = 0\n" +
                "    var spins: i32 = 0\n" +
                "    while ((sink.got != 7) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    Console.println((\"snapshot \" + sink.got.toString()))\n" +
                "    msgr.dispose()\n" +
                "    yield sleep(20)\n" +
                "    recv.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 一条消息多 listener：默认均 IOExecutor（同 lane FIFO →
            // 注册序确定）；removeListener 后只剩后者
            Case("Receiver 对拍：多 listener 注册序派发 + removeListener",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.*\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub shared class Sink {\n" +
                "    pub var a: i32\n" +
                "    pub var b: i32\n" +
                "    pub init() {\n" +
                "        a = 0\n" +
                "        b = 0\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const recv = msgr.receiver\n" +
                "    const sink = new Sink()\n" +
                "    const l1 = func{async (m: Msg) -> {\n" +
                "        sink.a = m.n\n" +
                "        Console.println((\"L1:\" + m.n.toString()))\n" +
                "    } }\n" +
                "    const l2 = func{async (m: Msg) -> {\n" +
                "        sink.b = m.n\n" +
                "        Console.println((\"L2:\" + m.n.toString()))\n" +
                "    } }\n" +
                "    recv.addListener(l1)\n" +
                "    recv.addListener(l2)\n" +
                "    await msgr.send(new Msg(1))\n" +
                "    var spins: i32 = 0\n" +
                "    while (((sink.a != 1) or (sink.b != 1)) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    recv.removeListener(l1)\n" +
                "    await msgr.send(new Msg(2))\n" +
                "    spins = 0\n" +
                "    while ((sink.b != 2) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    if (sink.a == 1) {\n" +
                "        Console.println(\"l1 removed\")\n" +
                "    } else {\n" +
                "        Console.println(\"FAIL l1\")\n" +
                "    }\n" +
                "    msgr.dispose()\n" +
                "    yield sleep(20)\n" +
                "    recv.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 生命周期隔离：Reader.createReceiver 不消费源 cursor；
            // Receiver dispose 不影响其 createReader 出的 Reader
            Case("Receiver 对拍：cursor 独立 + dispose 隔离",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.*\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const ra = msgr.createReader()\n" +
                "    const recvA = ra.createReceiver()\n" +
                "    recvA.addListener(func{async (m: Msg) -> { } })\n" +
                "    recvA.dispose()\n" +
                "    await msgr.send(new Msg(1))\n" +
                "    const a1 = await ra.next()\n" +
                "    Console.println(((a1.item as Msg).n).toString())\n" +
                "    const recvB = msgr.receiver\n" +
                "    const rb = recvB.createReader()\n" +
                "    recvB.dispose()\n" +
                "    await msgr.send(new Msg(2))\n" +
                "    const b1 = await rb.next()\n" +
                "    Console.println(((b1.item as Msg).n).toString())\n" +
                "    msgr.dispose()\n" +
                "    const a2 = await ra.next()\n" +
                "    Console.println(((a2.item as Msg).n).toString())\n" +
                "    const eos = await ra.next()\n" +
                "    if (eos.isEos) { Console.println(\"EOS\") }\n" +
                "    ra.dispose()\n" +
                "    rb.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW12b §25.2：IDisposable 销毁时强制检查 + 全局异常通道 =====
            // 局部 IDisposable 对象未 dispose，作用域结束销毁 → 入队 →
            // drain → 空注册表默认分支 stderr 打印（退出码不变；VM 半场
            // B2 已接，stderr needle 双宿主同文本断言）
            NativeErrCase("mw12b_undisposed_default",
                "import core.io.Console\n" +
                "pub class Res implements core.IDisposable {\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const r = new Res(1)\n" +
                "    Console.println(\"made \" + r.tag.toString())\n" +
                "    return 0\n" +
                "}\n",
                "core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：Res"),
            // 正常 using 清理：dispose 进入即置位 → 无事件（双宿主
            // stderr 均无默认打印），stdout 双宿主对拍一致
            NativeErrCase("mw12b_disposed_ok",
                "import core.io.Console\n" +
                "pub class Res implements core.IDisposable {\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { Console.println(\"disposed\") }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    seq using(const r = new Res()) {\n" +
                "        Console.println(\"in\")\n" +
                "    }\n" +
                "    Console.println(\"out\")\n" +
                "    return 0\n" +
                "}\n",
                "UndisposedResourceException", needlePresent: false),
            // 注册 GlobalExceptionHandler 处理器：drain 逐条调 dispatch →
            // 处理器打印 got:<类型名> 到 stdout。VM 半场 B2 已接事件通道
            // （VM 终结器入队 → Run 收尾 drain → dispatch），双宿主 stdout
            // 全对拍（made 行序先于 got 行——两宿主 drain 都在 main 之后）；
            // 有注册处理器不走默认分支，双宿主 stderr 无默认打印
            NativeErrCase("mw12b_undisposed_handler",
                "import core.io.Console\n" +
                "pub class Res implements core.IDisposable {\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.GlobalExceptionHandler.register(func{ (e: core.Exception) -> {\n" +
                "        if (e is core.UndisposedResourceException) {\n" +
                "            Console.println(\"got:\" +\n" +
                "                (e as core.UndisposedResourceException).resourceType)\n" +
                "        }\n" +
                "    } })\n" +
                "    const r = new Res()\n" +
                "    Console.println(\"made\")\n" +
                "    return 0\n" +
                "}\n",
                "UndisposedResourceException", needlePresent: false),
            // 环中对象未 dispose：局部环 main 结束时成候选（阈值不到不
            // 触发），shutdown 终轮收集兜底 → 晚到事件不经用户处理器，
            // 由 C 侧 atexit flush 默认打印（同一文本）。VM 侧 .NET GC
            // 一轮即收环，事件走 Run 收尾 drain 的默认分支——stderr
            // needle 同文本（事件时机两宿主天然不同，只断言文本）
            NativeErrCase("mw12b_undisposed_in_cycle",
                "import core.io.Console\n" +
                "pub class Node implements core.IDisposable {\n" +
                "    pub var next: Node?\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Node()\n" +
                "    var b = new Node()\n" +
                "    a.next = b\n" +
                "    b.next = a\n" +
                "    Console.println(\"cycled\")\n" +
                "    return 0\n" +
                "}\n",
                "core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：Node"),
            // MW12b VM 半场（mw12b2）：非 main 帧局部 undisposed 对象——
            // 辅助 fn 帧弹出后对象脱根 → 默认 stderr 打印；覆盖「销毁检查
            // 不限于 main 帧」的帧根释放路径，stderr needle 双宿主同文本
            NativeErrCase("mw12b2_undisposed_default_vm",
                "import core.io.Console\n" +
                "pub class Res implements core.IDisposable {\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "func leak() {\n" +
                "    const r = new Res()\n" +
                "    Console.println(\"leaked\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    leak()\n" +
                "    return 0\n" +
                "}\n",
                "core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：Res"),
            // ===== MW12c：macroGC 循环回收与泄漏检查套件 =====
            // 口径：VM 对拍 stdout/退出码 + 产物恒带 RIGI_RT_MEMTRACK=1，
            // memtrack 零泄漏即「环被收掉」。默认阈值（1MiB）下局部环靠
            // shutdown 终轮收集兜底；低阈值例走 EnvCase 触发 mid-run pass
            // 双对象环：函数作用域释放后成纯环，终轮收集兜底
            Case("mw12c_cycle_two",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func makeCycle() {\n" +
                "    var a = new Node()\n" +
                "    var b = new Node()\n" +
                "    a.next = b\n" +
                "    b.next = a\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeCycle()\n" +
                "    Console.println(\"two dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // 自环：单对象 next 指向自身，作用域释放后终轮收集
            Case("mw12c_cycle_self",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func makeCycle() {\n" +
                "    var s = new Node()\n" +
                "    s.next = s\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeCycle()\n" +
                "    Console.println(\"self dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // mid-run 触发：RIGI_RT_GC_THRESHOLD=1024，5000 轮双对象环
            // → 多轮 pass（fence 慢路径真实触发），stdout 对拍 + 零泄漏
            EnvCase("mw12c_cycle_midrun",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var pad: i32\n" +
                "    pub init() { pad = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 5000) {\n" +
                "        var a = new Node()\n" +
                "        var b = new Node()\n" +
                "        a.next = b\n" +
                "        b.next = a\n" +
                "        a.pad = i\n" +
                "        n = (n + 1)\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    if (n == 5000) { Console.println(\"midrun ok\") }\n" +
                "    return 0\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "1024" }),
            // 超长环：10 万节点链表首尾相接——收集器显式 trace 栈防深
            // 递归（退化成递归这里会爆栈），终轮收集后零泄漏
            Case("mw12c_cycle_long_chain",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func makeRing() {\n" +
                "    var head = new Node()\n" +
                "    var prev = head\n" +
                "    var i = 1\n" +
                "    while (i < 100000) {\n" +
                "        var cur = new Node()\n" +
                "        prev.next = cur\n" +
                "        prev = cur\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    prev.next = head\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeRing()\n" +
                "    Console.println(\"ring dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // 环带外部活引用：低阈值 mid-run pass 后继续使用活引用
            // （打印经环边到达的对端字段）→ 存活对象不误收；断开活引用
            // 后环脱根 → 终轮收掉
            EnvCase("mw12c_cycle_live_ref",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "}\n" +
                "func makeCycle(): Node {\n" +
                "    var a = new Node(7)\n" +
                "    var b = new Node(8)\n" +
                "    a.next = b\n" +
                "    b.next = a\n" +
                "    return a\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var keep: Node? = makeCycle()\n" +
                "    var i = 0\n" +
                "    while (i < 3000) {\n" +
                "        var x = new Node(i)\n" +
                "        var y = new Node(i)\n" +
                "        x.next = y\n" +
                "        y.next = x\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    var t = keep?.next?.tag if? -1\n" +
                "    Console.println(t.toString())\n" +
                "    keep = null\n" +
                "    Console.println(\"released\")\n" +
                "    return 0\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "1024" }),
            // 经数组元素成环：a → peers 数组 → b → peers 数组，收集器
            // 数组 32B 前缀元素走查必须命中（漏走=泄漏）
            Case("mw12c_cycle_through_array",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var peers: Array\\<Node>?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func makeCycle() {\n" +
                "    var arr = arrayOf\\<Node>(2)\n" +
                "    var a = new Node()\n" +
                "    var b = new Node()\n" +
                "    arr[0] = a\n" +
                "    arr[1] = b\n" +
                "    a.peers = arr\n" +
                "    b.peers = arr\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeCycle()\n" +
                "    Console.println(\"array cycle dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // 环边经过 tag1 盒：rich struct（含 class 引用字段）装箱成
            // Any 存进另一对象字段——盒内递归走查漏走=泄漏、误走=UAF
            Case("mw12c_cycle_through_box",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var payload: Any?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub rich struct Ref {\n" +
                "    pub var target: Node?\n" +
                "    pub init(_ -> target)\n" +
                "}\n" +
                "func makeCycle() {\n" +
                "    var a = new Node()\n" +
                "    var b = new Node()\n" +
                "    var r = new Ref(a)\n" +
                "    b.payload = (r as Any)\n" +
                "    a.payload = b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeCycle()\n" +
                "    Console.println(\"box cycle dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // 跨协程 shared 环（§4.7）：两个 async 协程各持一端互指，
            // await 完成后读取对端字段（数据依赖确定位置），run 帧弹出
            // 后环脱根 → 终轮收集
            Case("mw12c_cycle_shared_cross_coroutine",
                "import core.io.Console\n" +
                "pub shared class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "}\n" +
                "async func holdA(a: Node, b: Node) {\n" +
                "    a.next = b\n" +
                "    yield\n" +
                "}\n" +
                "async func holdB(a: Node, b: Node) {\n" +
                "    b.next = a\n" +
                "    yield\n" +
                "}\n" +
                "async func run() {\n" +
                "    var a = new Node(1)\n" +
                "    var b = new Node(2)\n" +
                "    var ta = holdA(a, b)\n" +
                "    var tb = holdB(a, b)\n" +
                "    await ta\n" +
                "    await tb\n" +
                "    var x = a.next?.tag if? -1\n" +
                "    Console.println(x.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 混合压力：环 + 普通垃圾 + 字符串 + 数组，低阈值长跑
            // （10 万级），sum 双宿主对拍 + 零泄漏
            EnvCase("mw12c_stress_mixed",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    var sum = 0\n" +
                "    while (i < 100000) {\n" +
                "        var a = new Node(i)\n" +
                "        var b = new Node((i + 1))\n" +
                "        a.next = b\n" +
                "        b.next = a\n" +
                "        var s = (\"n\" + i.toString())\n" +
                "        var arr = arrayOf\\<String>(2)\n" +
                "        arr[0] = s\n" +
                "        arr[1] = \"x\"\n" +
                "        var t = arr[0] if? \"\"\n" +
                "        if (t == s) { sum = (sum + 1) }\n" +
                "        if ((i & 3) == 0) { sum = (sum + a.tag) } else { sum = (sum + b.tag) }\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    Console.println(sum.toString())\n" +
                "    return 0\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "512" }),
            // ===== imap/getAtIndex 回归（Bug1 接口 iMap 恒模板键 /
            // Bug2a 泛型占位数组元素运行时 stride / Bug2b 接口派发
            // 结果拆箱）=====
            // ① for-in over List<i32> 求和（最小 imap 派发路径）
            Case("for-in List<i32> 求和",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(1)\n" +
                "    list.add(2)\n" +
                "    list.add(3)\n" +
                "    var sum: i32 = 0\n" +
                "    for (x in list) {\n" +
                "        sum = (sum + x)\n" +
                "    }\n" +
                "    Console.println(\"sum=${sum}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ② wrapper + for-in（imap_repro 母本）：打印 done 3
            Case("wrapper + for-in List<i32>",
                "import core.collections.*\n" +
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
                "    var list = new List\\<i32>()\n" +
                "    list.add(1)\n" +
                "    list.add(2)\n" +
                "    list.add(3)\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    for (x in list) {\n" +
                "        health = x\n" +
                "    }\n" +
                "    core.io.Console.println(\"done ${health}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ③ while + getAtIndex 同步正确性（imap_a4 母本）：done 3
            Case("while + getAtIndex 同步",
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(1)\n" +
                "    list.add(2)\n" +
                "    list.add(3)\n" +
                "    var health: i32 = 50\n" +
                "    var i: i64 = 0L\n" +
                "    var ic: i64 = list.length\n" +
                "    while ((i < ic)) {\n" +
                "        var x = list.getAtIndex(i)\n" +
                "        health = (x if? 0)\n" +
                "        i = (i + 1L)\n" +
                "    }\n" +
                "    core.io.Console.println(\"done ${health}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ④ async 跨挂起 getAtIndex（imap_repro_b 母本；main 只
            // spawn + return 常量）：ok 7 -5 42 r=1
            Case("async 跨挂起 getAtIndex",
                "import core.collections.*\n" +
                "async func one(): i32 { return 1 }\n" +
                "async func run(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(7)\n" +
                "    list.add(-5)\n" +
                "    list.add(42)\n" +
                "    var t = one()\n" +
                "    var r = await t\n" +
                "    var a = list.getAtIndex(0L)\n" +
                "    var b = list.getAtIndex(1L)\n" +
                "    var c = list.getAtIndex(2L)\n" +
                "    if (((a if? -1) != 7)) { return 11 }\n" +
                "    if (((b if? -1) != -5)) { return 12 }\n" +
                "    if (((c if? -1) != 42)) { return 13 }\n" +
                "    core.io.Console.println(\"ok ${(a if? -1)} ${(b if? -1)} ${(c if? -1)} r=${r}\")\n" +
                "    return 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑤ 元素类型矩阵：i64/bool/String/自定义 class
            // add → getAtIndex 往返
            Case("List 元素类型矩阵往返",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Item {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var li = new List\\<i64>()\n" +
                "    li.add(9000000000L)\n" +
                "    var lb = new List\\<bool>()\n" +
                "    lb.add(true)\n" +
                "    var ls = new List\\<String>()\n" +
                "    ls.add(\"hi\")\n" +
                "    var lc = new List\\<Item>()\n" +
                "    lc.add(new Item(5))\n" +
                "    var n = (li.getAtIndex(0L) if? 0L)\n" +
                "    var b = (lb.getAtIndex(0L) if? false)\n" +
                "    var s = (ls.getAtIndex(0L) if? \"\")\n" +
                "    var it = lc.getAtIndex(0L)\n" +
                "    var iv = (it?.v if? -1)\n" +
                "    Console.println(\"${n} ${b} ${s} ${iv}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ⑥ getAtIndex 越界得 null（if? 兜底分支）
            Case("getAtIndex 越界 null 兜底",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(1)\n" +
                "    var x = (list.getAtIndex(5L) if? -1)\n" +
                "    var y = (list.getAtIndex(0L) if? -1)\n" +
                "    Console.println(\"${x} ${y}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ===== L6：非 rigi_rt native 库 FFI 端到端（RUNTIME §26 库解析
            // 留白定稿：C 符号 = @NativeSymbol 原文，链接输入走 native
            // --link）。VM 侧无此 hook（§22.5 表外拒绝是定稿行为），本组
            // 为 native-only 证明：现场 clang 编最小 C 源出目标文件，链接后
            // 断言 stdout/退出码字面量 =====
            NativeOnlyCase("FFI 用户库标量与 String 入参",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigiffi\")\n" +
                "@NativeSymbol(\"rigiffi_add\")\n" +
                "native func ffiAdd(a: i32, b: i32): i32\n" +
                "@NativeLibrary(\"rigiffi\")\n" +
                "@NativeSymbol(\"rigiffi_mul\")\n" +
                "native func ffiMul(a: i64, b: i64): i64\n" +
                "@NativeLibrary(\"rigiffi\")\n" +
                "@NativeSymbol(\"rigiffi_len\")\n" +
                "native func ffiLen(text: String): i64\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(ffiAdd(20, 22).toString())\n" +
                "    Console.println(ffiMul(6L, 7L).toString())\n" +
                "    Console.println(ffiLen(\"hello\").toString())\n" +
                "    return ffiAdd(1, 2)\n" +
                "}\n",
                "typedef struct { const char *data; long long len; } rigi_string;\n" +
                "int rigiffi_add(int a, int b) { return a + b; }\n" +
                "long long rigiffi_mul(long long a, long long b) { return a * b; }\n" +
                "long long rigiffi_len(const rigi_string *s) { return s->len; }\n",
                "42\n42\n5\n", 3),
            // bool 参数 C 边界 = i8 槽（NativeCallEmitter zext i1）
            NativeOnlyCase("FFI 用户库 bool 与 f64",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigiffi2\")\n" +
                "@NativeSymbol(\"rigiffi_pick\")\n" +
                "native func ffiPick(flag: bool, x: double): double\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(ffiPick(true, 1.5).toString())\n" +
                "    Console.println(ffiPick(false, 1.5).toString())\n" +
                "    return 0\n" +
                "}\n",
                "double rigiffi_pick(signed char flag, double x) { return flag ? x : -x; }\n",
                "1.5\n-1.5\n", 0),
            Case("Place 对象身份与 Cell 复用",
                "class Item { pub var n: i32 = 1 }\n" +
                "pub func main(): i32 {\n" +
                " const x = new Item()\n var n: i32 = 1\n const fixed: i32 = 4\n" +
                " seq using(const a = placeOf x) using(const b = placeOf x) {\n" +
                "  core.io.Console.println((a == b).toString())\n }\n" +
                " seq using(const a = placeOf n) using(const b = placeOf n) {\n" +
                "  n = 9\n core.io.Console.println((a == b).toString())\n" +
                "  core.io.Console.println(n.toString())\n a.dispose()\n" +
                "  core.io.Console.println((a == b).toString())\n }\n" +
                " seq using(const a = placeOf fixed) using(const b = placeOf fixed) {\n" +
                "  core.io.Console.println((a == b).toString())\n }\n return 0\n}"),
            Case("Handle 生命周期与值写回",
                "pub func main(): i32 {\n var n: i32 = 3\n" +
                " unsafe seq using(const p = placeOf n) {\n const h = p.expose()\n" +
                " p.dispose()\n core.io.Console.println(h.load().toString())\n" +
                " const m = h.asMutable()\n m.store(9)\n" +
                " core.io.Console.println(n.toString())\n" +
                " core.io.Console.println(h.load().toString())\n }\n return 0\n}"),
            Case("Handle 对象只读与泛型能力边界", HandleBoundarySource),
            Case("Cell 用户覆写开放封闭接口与重载 ABI", CellSlotSource),
            Case("Handle rich value 与异常所有权", HandleRichSource),
            EnvCase("Handle 隐藏边循环回收", HandleCycleSource,
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            Case("Atomic 值对象异常与安全门面", AtomicSource),
            Case("Atomic 基础初始化与读取", "pub func main(): i32 { unsafe seq { const a = new Atomic\\<i32>(3)\n return a.load() } }") ,
            Case("Atomic 值更新", "pub func main(): i32 { unsafe seq { const a = new Atomic\\<i32>(3)\n a.mutate(func{ (old:i32):i32 -> old + 4 })\n return a.load() } }") ,
            Case("AtomicStruct 安全更新", "pub func main(): i32 { const a = new AtomicStruct\\<i32>(3)\n a.store(7)\n return a.load() }") ,
            Case("Atomic nullable 值与对象往返", AtomicNullableSource),
            Case("同步 callable 开放封闭多参数与Action", CallableSlotSource),
            Case("Nullable 泛型cast与is元素约束", NullableCastSource),
            Case("Atomic 回调挂起与锁竞争", AtomicContentionSource),
            Case("安全Atomic容器工厂方法与快照隔离", AtomicContainersSource),
            Case("安全Atomic数组仅工厂", AtomicContainerProbePrefix + "pub func main():i32 { const a = AtomicArray.fromArray\\<Item>(core.collections.arrayOfElements\\<Item>(new Item(1)))\n return 0 }"),
            Case("安全Atomic数组长度", AtomicContainerProbePrefix + "pub func main():i32 { const a = AtomicArray.fromArray\\<Item>(core.collections.arrayOfElements\\<Item>(new Item(1)))\n return await a.length() }"),
            Case("共享消息序列化往返", AtomicContainerProbePrefix + "pub func main():i32 { const item = new Item(1)\n const p = item:Serializable.toParcel()\n const x = core.serialization.fromParcel\\<Item>(p)\n return x.n }"),
            Case("共享消息泛型深复制", AtomicContainerProbePrefix + "pub func main():i32 { const x = core.serialization.deepCopy\\<Item>(new Item(1))\n return x.n }"),
            Case("普通Map自定义对象键相等", AtomicMapKeySource),
            Case("泛型对象文本覆写可挂起", AtomicMapKeySuspendingSource),
            Case("序列化类自环与异常调用隔离", SerializationGraphCorpus("serialization_graph_class")),
            Case("序列化混合容器图与Temporary", SerializationGraphCorpus("serialization_graph_mixed")),
            Case("序列化值类型开放泛型兼容", SerializationGraphCorpus("serialization_graph_value")),
            Case("闭环动态字符串回收不重入fence", SerializationGraphCorpus("serialization_graph_gc_strings")),
            Case("序列化非法引用与异常后上下文隔离", SerializationGraphCorpus("serialization_graph_errors")),
            Case("纯RigiMQ水位部分compact与全部drain", SerializationGraphCorpus("mq_pure_watermark")),
            Case("纯RigiMQ重复唤醒release与异常解锁", SerializationGraphCorpus("mq_pure_wakeup_release")),
            Case("泛型new隐式typeid跨挂起恢复", SerializationGraphCorpus("generic_new_after_suspend")),
            Case("泛型class序列化闭合对象头", SerializationGraphCorpus("serialization_generic_envelope")),
            Case("双executor候选swap与最后release竞争", MqConcurrentReleaseCorpus()),
            NativeErrCase("普通挂起dispose与同名元数回调布局", SerializationGraphCorpus("mq_dispose_after_suspend"),
                "UndisposedResourceException", needlePresent: false),
            NativeErrCase("Reader跨executor重复dispose幂等", SerializationGraphCorpus("mq_reader_dispose_race"),
                "UndisposedResourceException", needlePresent: false),
            Case("Compute池yield与Polling迁移单执行", SerializationGraphCorpus("compute_pool_resume")),
            Case("Compute池终态与await登记竞争及重复观察", SerializationGraphCorpus("task_terminal_waiter_race")),
            Case("MQ跨段缓存与积压branch及清空后复用", SerializationGraphCorpus("mq_segment_cursor")),
            Case("值块lambda混合return与throw执行finally", SerializationGraphCorpus("lambda_return_throw_finally")),
            NativeErrCase("序列化256节点长环重复引用与独立拷贝", SerializationGraphCorpus("serialization_graph_long_cycle"),
                "UndisposedResourceException", needlePresent: false),
            Case("Place嵌套泛型回调身份", SerializationGraphCorpus("place_nested_callback")),
            Case("泛型Cell构造只读与未调用扩张成员", SerializationGraphCorpus("capability_generic_cells")),
            Case("泛型接口参数返回与具体实现ABI", SerializationGraphCorpus("generic_interface_abi")),
            Case("普通子类闭合泛型基类身份与字段", SerializationGraphCorpus("closed_generic_base_identity")),
            Case("Any对象视图持有与AtomicList引用消息", SerializationGraphCorpus("any_object_view_ownership")),
            Case("闭合Func间接继承与不同元数挂起", SerializationGraphCorpus("closed_callable_suspend")),
            EnvCase("Alarm粘滞重复跨属主与环回收", SerializationGraphCorpus("alarm_lifecycle"),
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            NativeOnlyCase("Alarm原生底座反复创建释放有界", NativeResourceCorpus("alarm_resources"),
                "void alarm_resource_test_marker(void) {}", "alarm-resources-ok\n", 0),
            NativeOnlyCase("已观察失败Task节点与异常资源有界", NativeResourceCorpus("failure_resources"),
                "void failure_resource_test_marker(void) {}", "failure-resources-ok\n", 0,
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            EnvCase("失败Task环的内部资源析构", SerializationGraphCorpus("failure_lifecycle"),
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            Case("同一失败Task多Compute观察者重抛", SerializationGraphCorpus("failure_shared_waiters")),
            NativeOnlyCase("普通容器删除及时释放尾槽", NativeResourceCorpus("collection_remove_resources"),
                "void collection_remove_resource_test_marker(void) {}", "7\ncollection-remove-resources-ok\n", 0),
        };

        // 单用例：源 → 中端全管线 → BIL 文本 → VM 执行 + native 编译执行，
        // 比 stdout（行尾归一）与退出码（main 的 i32 返回）
        // 正向源码必须在执行 VM/native 前通过编译诊断检查；运行期负例
        // 同样要求合法源码。BIL 级刻意坏指令仍走各自专门驱动。
        private static BilModule EmitNativeSource(string source,
            [System.Runtime.CompilerServices.CallerMemberName] string label = "")
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            if (unit.Diagnostics.HasErrors)
                throw new InvalidOperationException(label + "：正向源码必须零 Error；" +
                    string.Join("; ", unit.Diagnostics.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Phase + ": " + d.Message)));
            TestHarness.CheckTrue(label + "：源码编译零 Error", true);
            return module;
        }

        private static void DeleteNativeTestDirectory(string dir)
        {
            // ExternalProcess 已 WaitForExit 并 Dispose；Windows 映像锁可能
            // 短暂延迟释放。仅对本用例目录有界重试，最终错误仍原样抛出。
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(dir, recursive: true); return; }
                catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 5
                    && (ex is UnauthorizedAccessException
                        || (ex is IOException && ((ex.HResult & 0xffff) is 5 or 32 or 33))))
                {
                    System.Threading.Thread.Sleep(20 << attempt);
                }
            }
        }

        private static void RunCase(string label, string source,
            IReadOnlyDictionary<string, string>? env = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
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
                    out var nativeOut, out var nativeErr, environment: env ?? MemtrackEnv);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
                TestHarness.CheckTrue(label + "：退出码一致",
                    runExit == expectedExit, $"native={runExit} vm={expectedExit} stderr={nativeErr}");
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // MW12b §25.2：流程同 RunCase（VM 参照对拍 stdout + 退出码一致，
        // VM 无异常），额外断言 native stderr 含/不含 needle；VM 半场 B2
        // 已接事件通道，VM stderr 同文本一并断言（needle 形态对事件条数/
        // 排序不敏感——多事件场景两宿主顺序天然不同）
        private static void RunNativeErrCase(string label, string source, string needle,
            bool needlePresent)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
                var text = BilWriter.Write(module);

                // VM 侧（行为参考实现）：stdout/退出码即参照，stderr 经
                // Run 收尾 drain 的事件通道产生（与 native 同文本）
                var vm = BilVm.Run(BilReader.Read(text));
                TestHarness.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                var expectedExit = vm.ReturnValue is VmI32 value ? value.Value : 0;
                TestHarness.CheckTrue(
                    label + (needlePresent ? "：VM stderr 含关键字" : "：VM stderr 无事件"),
                    vm.Stderr.Contains(needle) == needlePresent, vm.Stderr);

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
                    out var nativeOut, out var nativeErr, environment: MemtrackEnv);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
                TestHarness.CheckTrue(label + "：退出码一致",
                    runExit == expectedExit, $"native={runExit} vm={expectedExit} stderr={nativeErr}");
                TestHarness.CheckTrue(
                    label + (needlePresent ? "：native stderr 含关键字" : "：native stderr 无事件"),
                    nativeErr.Contains(needle) == needlePresent, nativeErr);
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

        // to_string 面族：手写 BIL 声明 native fn（不走 stdlib），VM hook 与
        // native 产物 stdout 对拍。f64/f32 走 Ryu 最短往返 + .NET 默认呈现。
        private const string ToStringFacesBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"strfmt\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_I0 = i64 0,\n" +
            "    R_Ineg = i64 -42,\n" +
            "    R_F0 = f64 0,\n" +
            "    R_Fneg = f64 -2.5,\n" +
            "    R_F01 = f64 0.1,\n" +
            "    R_F02 = f64 0.2,\n" +
            "    R_F05 = f64 0.5,\n" +
            "    R_F1 = f64 1,\n" +
            "    R_F3 = f64 3,\n" +
            "    R_F4 = f64 4,\n" +
            "    R_F4n = f64 -4,\n" +
            "    R_F10 = f64 10,\n" +
            "    R_F1em5 = f64 1e-5,\n" +
            "    R_F1e15 = f64 1e15,\n" +
            "    R_F1e16 = f64 1e16,\n" +
            "    R_F1e17 = f64 1e17,\n" +
            "    R_F1e20 = f64 1e20,\n" +
            "    R_F1e308 = f64 1e308,\n" +
            "    R_Fmax = f64 1.7976931348623157e308,\n" +
            "    R_Fmin = f64 2.2250738585072014E-308,\n" +
            "    R_Fs1 = f32 1,\n" +
            "    R_Fs3 = f32 3,\n" +
            "    R_True = bool true,\n" +
            "    R_False = bool false,\n" +
            "    R_A = char 'A',\n" +
            "    R_Nl = string \"\\n\",\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $print(text:.string)@.void priv native symbol(\"print\") lib(\"rigi_rt\")\n" +
            "    .method $i64_to_string(value:.i64)@.string priv native symbol(\"i64_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $f32_to_string(value:.f32)@.string priv native symbol(\"f32_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $f64_to_string(value:.f64)@.string priv native symbol(\"f64_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $bool_to_string(value:.bool)@.string priv native symbol(\"bool_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $char_to_string(value:.char)@.string priv native symbol(\"char_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .i64 i,\n" +
            "        .f64 f,\n" +
            "        .f64 x,\n" +
            "        .f64 y,\n" +
            "        .f32 fs,\n" +
            "        .f32 xs,\n" +
            "        .f32 ys,\n" +
            "        .bool b,\n" +
            "        .char c,\n" +
            "        .string s,\n" +
            "        .string nl,\n" +
            "        .i32 r\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_Nl) $nl\n" +
            "        load res(R_I0) $i\n" +
            "        invoke fn($i64_to_string(value:.i64)@.string) $s [$i]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Ineg) $i\n" +
            "        invoke fn($i64_to_string(value:.i64)@.string) $s [$i]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F0) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Fneg) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F01) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F05) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F4) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F4n) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1) $x\n" +
            "        load res(R_F3) $y\n" +
            "        div $x $y $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F01) $x\n" +
            "        load res(R_F02) $y\n" +
            "        add $x $y $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e20) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1em5) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e15) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e16) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e17) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F0) $f\n" +
            "        opposite $f $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e308) $x\n" +
            "        load res(R_F10) $y\n" +
            "        mul $x $y $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Fmax) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Fmin) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Fs1) $xs\n" +
            "        load res(R_Fs3) $ys\n" +
            "        div $xs $ys $fs\n" +
            "        invoke fn($f32_to_string(value:.f32)@.string) $s [$fs]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_True) $b\n" +
            "        invoke fn($bool_to_string(value:.bool)@.string) $s [$b]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_False) $b\n" +
            "        invoke fn($bool_to_string(value:.bool)@.string) $s [$b]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_A) $c\n" +
            "        invoke fn($char_to_string(value:.char)@.string) $s [$c]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Zero) $r\n" +
            "        ret $r\n" +
            "    }\n" +
            "}\n";

        // string 排序比较的手写 BIL（前端 P3 未放行 String 的 < 运算符，
        // MW3：f64 switch 比较链降级的符号零/NaN 口径（BIL 级，VM
        // ValuesEqual 即 C# ==）：-0.0 selector 命中 0 case、+0.0 selector
        // 命中 -0.0 case（符号零相等）；NaN case 标签永不命中（跳过头项
        // 落第二项）。期望退出码 10+20+40=70
        private const string SwitchF64SignZeroBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"swfsign\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_T1 = switch-table<.f64> { 0 },\n" +
            "    R_T2 = switch-table<.f64> { -0.0 },\n" +
            "    R_T3 = switch-table<.f64> { NaN, 2.0 },\n" +
            "    R_NegZero = f64 -0.0,\n" +
            "    R_PosZero = f64 0,\n" +
            "    R_Two = f64 2.0,\n" +
            "    R_0 = i32 0,\n" +
            "    R_10 = i32 10,\n" +
            "    R_20 = i32 20,\n" +
            "    R_40 = i32 40,\n" +
            "    R_1 = i32 1,\n" +
            "    R_2 = i32 2,\n" +
            "    R_4 = i32 4\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .f64 x,\n" +
            "        .i32 acc,\n" +
            "        .i32 .t0,\n" +
            "        .breakid .b0,\n" +
            "        .breakid .b1,\n" +
            "        .breakid .b2\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_0) $acc\n" +
            "        load res(R_NegZero) $x\n" +
            "        switch $x res(R_T1)\n" +
            "            [blk(i0)]\n" +
            "            blk(d0)\n" +
            "            $.b0\n" +
            "        load res(R_PosZero) $x\n" +
            "        switch $x res(R_T2)\n" +
            "            [blk(i1)]\n" +
            "            blk(d1)\n" +
            "            $.b1\n" +
            "        load res(R_Two) $x\n" +
            "        switch $x res(R_T3)\n" +
            "            [blk(i2a), blk(i2b)]\n" +
            "            blk(d2)\n" +
            "            $.b2\n" +
            "        ret $acc\n" +
            "    }\n" +
            "    .block i0 {\n" +
            "        load res(R_10) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block d0 {\n" +
            "        load res(R_1) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block i1 {\n" +
            "        load res(R_20) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block d1 {\n" +
            "        load res(R_2) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block i2a {\n" +
            "        load res(R_4) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block i2b {\n" +
            "        load res(R_40) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block d2 {\n" +
            "        load res(R_4) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "}\n";

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

        // 失败对拍（MW9b-G 起两侧同为真异常未捕获出口）：VM 抛语言级
        // 异常（消息含关键字）、native 由顶层 reporter 打印
        // 「{类型全名}: {message}」——stderr 关键字对齐（nativeNeedle 缺省
        // 同 keyword；新格式全名前缀经 nativeNeedle 单断）、native 退出
        // 码 1 对齐 vm 命令未捕获异常出口、stdout 一致
        private static void RunFailCase(string label, string source, string keyword,
            string? nativeNeedle, IReadOnlyDictionary<string, string>? env = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
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
                    out var nativeOut, out var nativeErr, environment: env ?? MemtrackEnv);
                TestHarness.CheckTrue(label + "：native 退出码 1", runExit == 1,
                    $"exit={runExit}");
                TestHarness.CheckTrue(label + "：native stderr 含关键字",
                    nativeErr.Contains(nativeNeedle ?? keyword), nativeErr);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // MW10 刀5 ④：A↔B init 互引 → 急切初始化期构造环。VM 侧：环
        // 异常是基础设施级 VmException（VmContext.SingletonCycleException），
        // 从 InitializeSingletons 经 BilVm.Run 直接抛出（无 BilVmResult
        // 通道，RunFailCase 不适用）；native：get fn 在途检测抛
        // core::RuntimeException 未捕获 → reporter（"{类型全名}: {message}"）
        // → exit 1。已知分歧（以 VM 为准）：VM 消息带在途栈全链
        //（"A → B → A"），native v1 静态槽形态无在途链对象，只报触发类型
        private static void RunSingletonCycleCase(
            IReadOnlyDictionary<string, string>? env = null)
        {
            const string label = "singleton 构造环抛异常（急切初始化期）";
            var source =
                "pub shared singleton class A {\n" +
                "    pub var b: i32\n" +
                "    pub init() { b = new B().value }\n" +
                "}\n" +
                "pub shared singleton class B {\n" +
                "    pub var value: i32\n" +
                "    pub init() { value = new A().b }\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n";
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
                var text = BilWriter.Write(module);

                // VM 侧：构造环在急切初始化期以 VmException 炸出
                VmException? cycle = null;
                try
                {
                    BilVm.Run(BilReader.Read(text));
                }
                catch (VmException ex)
                {
                    cycle = ex;
                }
                TestHarness.CheckTrue(label + "：VM 抛构造环异常", cycle != null);
                TestHarness.CheckTrue(label + "：VM 消息含循环链前缀",
                    cycle != null && cycle.Message.Contains("singleton 初始化循环依赖"),
                    cycle?.Message ?? "");

                // native 侧：编译链接成功，运行 exit 1 + stderr 同前缀
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
                    out var nativeOut, out var nativeErr, environment: env ?? MemtrackEnv);
                TestHarness.CheckTrue(label + "：native 退出码 1", runExit == 1,
                    $"exit={runExit} stderr={nativeErr}");
                TestHarness.CheckTrue(label + "：native stderr 含循环链前缀",
                    nativeErr.Contains("singleton 初始化循环依赖"), nativeErr);
                TestHarness.CheckTrue(label + "：native stdout 为空（与 VM 一致）",
                    NormalizeNewlines(nativeOut) == "", nativeOut);
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // 前端 take(nums) 会把包再装箱成单元素；手改 invoke 整包转发后对拍
        private static void RunPackForwardCase()
        {
            var module = EmitNativeSource(
                "import core.io.Console\n" +
                "func take(nums: i32...): i32 { return nums.length }\n" +
                "func wrap(nums: i32...): i32 { return take(nums) }\n" +
                "pub func main(): i32 {\n" +
                "    if (wrap(1, 2, 3) == 3) { Console.println(\"fwd\") }\n" +
                "    return 0\n" +
                "}\n");
            var wrap = module.Functions.Single(f => f.Symbol == "$wrap()@.i32");
            var entry = wrap.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            var invoke = entry.Instructions.OfType<InvokeInstruction>().Single();
            entry.Instructions.Clear();
            entry.Instructions.Add(new InvokeInstruction(invoke.Method, invoke.Target,
                new[] { new BilVariableOperand(".vargs.nums") }));
            entry.Instructions.Add(new RetInstruction(invoke.Target));
            RunBilCase("包转发（整包）", BilWriter.Write(module));
        }

        // ===== L1：8 条「VM 支持但前端不发射」指令的 BIL 级对拍 =====

        // cast.indirect / cast.safe.indirect（§12.1/§12.2 动态形态）：
        // 上转/下转命中（rigi_try_cast 视图改写），safe 不命中产 null
        //（type.is 观测为 false）；全程与 VM 同 BIL 对拍
        private static void RunIndirectCastCase()
        {
            RunBilCase("cast.indirect 族（BIL 级）", BilWriter.Write(IndirectCastModule(false)));
        }

        // cast.indirect 不命中：VM CastFailed 与 native EmitCastThrow
        // 同型（core::CastException），退出码 1、stderr 关键字对齐
        private static void RunIndirectCastFailCase()
        {
            RunBilFailCase("cast.indirect 不命中抛 CastException（BIL 级）",
                BilWriter.Write(IndirectCastModule(true)), "无法将", "CastException");
        }

        // Animal/Dog（extends）模块：源码骨架（携 stdlib，异常构造/
        // 顶层 reporter 可达）+ $main 入口块清空后直织间接 cast 序列
        //（前端不发射的形态）。failMode = 以 Animal 实例对 .typeid<Dog>
        // 做强制 cast.indirect（不命中路径）
        // G4 落空负例模块（BIL 级）：合法骨架（Addable + add<T> 携
        // stdlib，异常类型/init 可达）+ 手写 Plain（无 operator）——
        // main 改写为 add<Plain>，运行期候选链全落空
        private static string BuildGenericOpMissBil()
        {
            var module = EmitNativeSource(
                "pub interface Addable {\n" +
                "    operator plus(another: Addable): Addable\n" +
                "}\n" +
                "func add\\<T extends Addable>(a: T, b: T): Addable {\n" +
                "    return a + b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return 0\n" +
                "}\n");
            var plain = new BilTypeDeclaration("Plain", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            plain.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Plain$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(plain);
            var plainInit = new BilFunction("Plain$init()@.void");
            plainInit.Args.Add(new BilArgDeclaration(".return", ".void"));
            plainInit.Args.Add(new BilArgDeclaration(".this", "Plain"));
            var plainInitBody = new BilBlock("entry", BilBlockModifier.Entrypoint);
            plainInitBody.Instructions.Add(new RetInstruction());
            plainInit.Blocks.Add(plainInitBody);
            module.Functions.Add(plainInit);

            module.Resources.Add(new BilScalarResource("R_Z", BilScalarType.I32, "0"));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var entry = main.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            entry.Instructions.Clear();
            main.Vars.Add(new BilVarDeclaration(".typeid", "tid"));
            main.Vars.Add(new BilVarDeclaration("Plain", "p1"));
            main.Vars.Add(new BilVarDeclaration("Plain", "p2"));
            main.Vars.Add(new BilVarDeclaration("Addable", "rr"));
            main.Vars.Add(new BilVarDeclaration(".i32", "rz"));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Plain"),
                BilOp.Var("tid")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Plain"), BilOp.Var("p1"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Plain"), BilOp.Var("p2"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new InvokeInstruction(
                BilOp.Fn("$add(a:.generic<$.generic.T>,b:.generic<$.generic.T>)@Addable"),
                BilOp.Var("rr"),
                new[]
                {
                    BilOp.Var("tid"), BilOp.Var("p1"), BilOp.Var("p2"),
                }));
            entry.Instructions.Add(new LoadInstruction(module.Resources[^1],
                BilOp.Var("rz")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("rz")));
            return BilWriter.Write(module);
        }

        private static BilModule IndirectCastModule(bool failMode)
        {
            var module = EmitNativeSource(
                "pub open class Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub class Dog : Animal {\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return 0\n" +
                "}\n");
            module.Resources.Add(new BilScalarResource("R_C99", BilScalarType.I32, "99"));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var entry = main.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            entry.Instructions.Clear();
            main.Vars.Add(new BilVarDeclaration("Dog", "d"));
            main.Vars.Add(new BilVarDeclaration("Animal", "a"));
            main.Vars.Add(new BilVarDeclaration("Animal", "a2"));
            main.Vars.Add(new BilVarDeclaration("Dog", "back"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Animal>", "ta"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Dog>", "td"));
            main.Vars.Add(new BilVarDeclaration(".nullable<Dog>", "miss"));
            main.Vars.Add(new BilVarDeclaration(".bool", "flag"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "bk"));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Dog"), BilOp.Var("d"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Animal"), BilOp.Var("a2"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Animal"),
                BilOp.Var("ta")));
            // Dog → Animal 上转（运行期 typeid 命中）
            entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("d"), BilOp.Var("a"),
                BilOp.Var("ta"), isSafe: false));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Dog"),
                BilOp.Var("td")));
            if (failMode)
            {
                // Animal 实例 → Dog 强制转换：运行期不命中
                entry.Instructions.Add(new LoadInstruction(module.Resources[^1],
                    BilOp.Var("r")));
                entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a2"),
                    BilOp.Var("back"), BilOp.Var("td"), isSafe: false));
                entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            }
            else
            {
                // Animal（实为 Dog）→ Dog 下转命中，读回字段验证身份
                entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a"),
                    BilOp.Var("back"), BilOp.Var("td"), isSafe: false));
                entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("back"),
                    BilOp.Var("r"), BilOp.Field("Dog#n@.i32")));
                // safe 不命中：Animal 非 Dog → null；type.is 观测 false
                entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a2"),
                    BilOp.Var("miss"), BilOp.Var("td"), isSafe: true));
                entry.Instructions.Add(new DirectTypeCheckInstruction(BilTypeCheckKind.Is,
                    BilOp.Var("miss"), BilOp.Type("Dog"), BilOp.Var("flag")));
                var thenBlock = new BilBlock("cast-then");
                thenBlock.Instructions.Add(new LoadInstruction(module.Resources[^1],
                    BilOp.Var("r")));
                thenBlock.Instructions.Add(new RetInstruction(BilOp.Var("r")));
                main.Blocks.Add(thenBlock);
                entry.Instructions.Add(new IfInstruction(BilOp.Var("flag"), thenBlock,
                    null, BilOp.Var("bk")));
                entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            }
            return module;
        }

        // get.wrapper.indirect（§12.4 动态形态，前端无整体取值路径）：
        // Host wrapped(Wrap) + 有参 ..init.wrapper 安装后，经
        // getid.type 的 typeid 间接取 wrapper 值拷贝，读字段验证
        //（BilVmTests.WrapperHostModule 同构手工模块）
        private static void RunGetWrapperIndirectCase()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_L", BilScalarType.I32, "5"));
            var wrap = new BilTypeDeclaration("Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            wrap.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Wrap#level@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            wrap.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Wrap$init(level:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(wrap);
            var host = new BilTypeDeclaration("Host", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("Wrap"));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Host$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Host$..init.wrapper(level:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            module.LocalSymbols.Add(host);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var wrapInit = new BilFunction("Wrap$init(level:.i32)@.void");
            wrapInit.Args.Add(new BilArgDeclaration(".return", ".void"));
            wrapInit.Args.Add(new BilArgDeclaration(".this", "Wrap"));
            wrapInit.Args.Add(new BilArgDeclaration("level", ".i32"));
            var wrapEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("level"),
                BilOp.Var(".this"), BilOp.Field("Wrap#level@.i32")));
            wrapEntry.Instructions.Add(new RetInstruction());
            wrapInit.Blocks.Add(wrapEntry);
            module.Functions.Add(wrapInit);

            var hostInit = new BilFunction("Host$init()@.void");
            hostInit.Args.Add(new BilArgDeclaration(".return", ".void"));
            hostInit.Args.Add(new BilArgDeclaration(".this", "Host"));
            var hostInitEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            hostInitEntry.Instructions.Add(new RetInstruction());
            hostInit.Blocks.Add(hostInitEntry);
            module.Functions.Add(hostInit);

            var initWrapper = new BilFunction("Host$..init.wrapper(level:.i32)@.void");
            initWrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            initWrapper.Args.Add(new BilArgDeclaration(".this", "Host"));
            initWrapper.Args.Add(new BilArgDeclaration("level", ".i32"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new NewWrapperEntityInstruction(BilOp.Type("Wrap"),
                new[] { BilOp.Var("level") }));
            wrapperEntry.Instructions.Add(new RetInstruction());
            initWrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(initWrapper);

            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "lv"));
            main.Vars.Add(new BilVarDeclaration("Host", "h"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Wrap>", "wid"));
            main.Vars.Add(new BilVarDeclaration("Wrap", "w"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("lv")));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Host"),
                BilOp.Var("h"), new[] { BilOp.Var("lv") },
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Wrap"),
                BilOp.Var("wid")));
            entry.Instructions.Add(new GetWrapperIndirectInstruction(BilOp.Var("h"),
                BilOp.Var("wid"), BilOp.Var("w")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("w"), BilOp.Var("r"),
                BilOp.Field("Wrap#level@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            RunBilCase("get.wrapper.indirect（BIL 级）", BilWriter.Write(module));
        }

        // getid.field + get/set.field.indirect + get/set.field.static.indirect
        //（§12.6/§13.5，前端尚不发射——BilVmTests.IndirectBoxModule 同构
        // 手工模块，返回实例字段与静态字段之和验证双向读写）
        private static void RunFieldIndirectCase()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_7", BilScalarType.I32, "7"));
            var box = new BilTypeDeclaration("Box", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "Box#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticField,
                "Box#.static.tag@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Box$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(box);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var init = new BilFunction("Box$init()@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Box"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "v"));
            main.Vars.Add(new BilVarDeclaration("Box", "obj"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Box>", "tid"));
            main.Vars.Add(new BilVarDeclaration(".fieldid<Box, .i32, instance>", "fid"));
            main.Vars.Add(new BilVarDeclaration(".fieldid<Box, .i32, static>", "sfid"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r2"));
            main.Vars.Add(new BilVarDeclaration(".i32", "sum"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("v")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Box"), BilOp.Var("tid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#n@.i32"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new GetIdFieldInstruction(
                BilOp.Field("Box#.static.tag@.i32"), BilOp.Var("sfid")));
            entry.Instructions.Add(new NewIndirectInstruction(BilOp.Var("tid"),
                BilOp.Var("obj"), Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new SetFieldIndirectInstruction(BilOp.Var("v"),
                BilOp.Var("obj"), BilOp.Var("fid")));
            entry.Instructions.Add(new SetFieldStaticIndirectInstruction(BilOp.Var("v"),
                BilOp.Var("tid"), BilOp.Var("sfid")));
            entry.Instructions.Add(new GetFieldIndirectInstruction(BilOp.Var("obj"),
                BilOp.Var("r"), BilOp.Var("fid")));
            entry.Instructions.Add(new GetFieldStaticIndirectInstruction(BilOp.Var("r2"),
                BilOp.Var("tid"), BilOp.Var("sfid")));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("r"), BilOp.Var("r2"), BilOp.Var("sum")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("sum")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            RunBilCase("getid.field + field.indirect 族（BIL 级）", BilWriter.Write(module));
        }

        // new.wrapped.case（§14.4.2，前端尚不发射）：带参
        // ..init.wrapper 的 enum case 构造——wrapper 实参先行（体内写
        // 静态字段作可观测副作用），case 实参随后进 init。返回
        // v + 静态标记验证双序执行（7 + 3 = 10）
        private static void RunNewWrappedCaseCase()
        {
            RunBilCase("new.wrapped.case（BIL 级）",
                BilWriter.Write(NewWrappedCaseModule(mismatch: false)));
        }

        // new.wrapped.case 的 case 实参不匹配任何 init：VM 运行期抛
        // 「new.case 实参不匹配任何 init」（VM 不过门禁）；native 由
        // BilGate（BilVerifier §14.3/§14.4.2）编译期拒绝——同一非法
        // 模块双侧同拒（消息关键字对齐）
        private static void RunNewWrappedCaseRejectCase()
        {
            var text = BilWriter.Write(NewWrappedCaseModule(mismatch: true));
            var vm = BilVm.Run(BilReader.Read(text));
            TestHarness.CheckTrue("new.wrapped.case init 失配：VM 有异常",
                vm.Exception != null);
            TestHarness.CheckTrue("new.wrapped.case init 失配：VM 消息含关键字",
                vm.Exception != null && vm.Exception.Message.Contains("不匹配任何 init"),
                vm.Exception?.Message ?? "");

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var compiled = RunNative("native", "--file", bilPath,
                    "--out", Path.Combine(dir, "case.exe"));
                TestHarness.CheckTrue("new.wrapped.case init 失配：native 门禁拒绝（退出 1）",
                    compiled.Code == 1, $"code={compiled.Code} err={compiled.Err}");
                TestHarness.CheckTrue("new.wrapped.case init 失配：native 消息含关键字",
                    compiled.Err.Contains("不匹配"), compiled.Err);
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // raw.hex/raw.bin → core::Span<u8>/core::SharedSpan<u8>（§19.3 字节
        // 序列；L5 资源面）：VM 对 raw load 整体无物化语义（VmContext
        // LoadResource 拒绝）——先钉住该取证，native 侧按字节缓冲区语义
        // 物化（span_alloc + 静态字节常量 memcpy）。exit = 4 + 2 = 6
        //（两缓冲区 length 字段之和），全程 native-only 验证
        private static void RunRawBufferSpanCase()
        {
            const string bil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawspan\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Data = raw.hex x2FF2331C,\n" +
                "    R_Bits = raw.bin b0101010101010101\n" +
                "}\n" +
                "\n" +
                "LocalSymbols {\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n" +
                "\n" +
                "ExternalSymbols {\n" +
                "}\n" +
                "\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "\n" +
                "    .vars {\n" +
                "        core::Span<.u8> d,\n" +
                "        core::SharedSpan<.u8> b,\n" +
                "        .i32 n,\n" +
                "        .i32 m,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Data) $d\n" +
                "        load res(R_Bits) $b\n" +
                "        get.field $d $n field(core::Span#length@.i32)\n" +
                "        get.field $b $m field(core::SharedSpan#length@.i32)\n" +
                "        add $n $m $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            // 取证钉住：VM 对 raw 资源 load 拒绝（无物化语义可对照，
            // 故本面只能 native-only 验证，不走 RunBilCase 对拍）
            var vm = BilVm.Run(BilReader.Read(bil));
            TestHarness.CheckTrue("raw → Span：VM 拒绝 raw load（取证）",
                vm.Exception != null
                && vm.Exception.Message.Contains("不支持的标量资源类型"),
                vm.Exception?.Message ?? "");

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, bil, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue("raw → Span：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr, environment: MemtrackEnv);
                TestHarness.CheckTrue("raw → Span：退出码 6（两 length 之和）",
                    runExit == 6, $"exit={runExit} stderr={nativeErr}");
                TestHarness.CheckTrue("raw → Span：stdout 为空", nativeOut.Length == 0,
                    nativeOut);
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // enum E（字段 v + 静态 tag + 有参 ..init.wrapper）：case E.Param
        // 洞实参 x 进 init；wrapper 实参 t 进 ..init.wrapper（写静态
        // tag）。mismatch = case 实参多给一个（不匹配任何 init）
        private static BilModule NewWrappedCaseModule(bool mismatch)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_7", BilScalarType.I32, "7"));
            module.Resources.Add(new BilScalarResource("R_3", BilScalarType.I32, "3"));
            var e = new BilTypeDeclaration("E", BilTypeKind.EnumStruct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "E#v@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticField,
                "E#.static.tag@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "E$init(x:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "E$..init.wrapper(t:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            e.Members.Add(new BilCaseDeclaration("E.Fixed"));
            e.Members.Add(new BilCaseDeclaration("E.Param",
                new[] { new BilCaseParameter("v", ".i32") }));
            module.LocalSymbols.Add(e);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var init = new BilFunction("E$init(x:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "E"));
            init.Args.Add(new BilArgDeclaration("x", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("x"),
                BilOp.Var(".this"), BilOp.Field("E#v@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            var wrapper = new BilFunction("E$..init.wrapper(t:.i32)@.void");
            wrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            wrapper.Args.Add(new BilArgDeclaration(".this", "E"));
            wrapper.Args.Add(new BilArgDeclaration("t", ".i32"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new SetFieldStaticInstruction(BilOp.Var("t"),
                BilOp.Type("E"), BilOp.Field("E#.static.tag@.i32")));
            wrapperEntry.Instructions.Add(new RetInstruction());
            wrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(wrapper);

            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            main.Vars.Add(new BilVarDeclaration(".i32", "t"));
            main.Vars.Add(new BilVarDeclaration("E", "e"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            main.Vars.Add(new BilVarDeclaration(".i32", "s"));
            main.Vars.Add(new BilVarDeclaration(".i32", "sum"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("x")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("t")));
            entry.Instructions.Add(new NewWrappedCaseInstruction(BilOp.Type("E"),
                BilOp.Case("E.Param"), BilOp.Var("e"),
                new[] { BilOp.Var("t") },
                mismatch
                    ? new[] { BilOp.Var("x"), BilOp.Var("t") }
                    : (IReadOnlyList<BilVariableOperand>)new[] { BilOp.Var("x") }));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("e"), BilOp.Var("r"),
                BilOp.Field("E#v@.i32")));
            entry.Instructions.Add(new GetFieldStaticInstruction(BilOp.Var("s"),
                BilOp.Type("E"), BilOp.Field("E#.static.tag@.i32")));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("r"), BilOp.Var("s"), BilOp.Var("sum")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("sum")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }
        // 语言级异常（消息含 keyword）；native 编译链接成功、运行退出码
        // 1、stderr 含 nativeNeedle（缺省同 keyword）、stdout 一致
        private static void RunBilFailCase(string label, string bilText, string keyword,
            string? nativeNeedle = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var vm = BilVm.Run(BilReader.Read(bilText));
                TestHarness.CheckTrue(label + "：VM 有异常", vm.Exception != null);
                TestHarness.CheckTrue(label + "：VM 消息含关键字",
                    vm.Exception != null && vm.Exception.Message.Contains(keyword),
                    vm.Exception?.Message ?? "");

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
                    out var nativeOut, out var nativeErr, environment: MemtrackEnv);
                TestHarness.CheckTrue(label + "：native 退出码 1", runExit == 1,
                    $"exit={runExit}");
                TestHarness.CheckTrue(label + "：native stderr 含关键字",
                    nativeErr.Contains(nativeNeedle ?? keyword), nativeErr);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // typeid 数组（泛型位置包 TArgs 的承载形态 .array<.typeid<.any>>）：
        // 元素 = 8B 内联 sheet 指针（sheet FlagInlineValue/typeSize=8），非 16B
        // 胖槽——回归 stride 双口径（发射 16B/分配 8B）导致的堆越界（linux glibc
        // abort）。前端无 TArgs[i] 语法，BIL 级直驱 get.array + nullable 解包 +
        // 间接 is 观测元素值正确性。
        private static void RunTypeIdArrayGetCase()
        {
            RunBilCase("typeid 数组元素读取（BIL 级）",
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"tidarr\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_0 = i32 0,\n" +
                "    R_1 = i32 1,\n" +
                "    R_42 = i32 42,\n" +
                "    R_V = i64 7\n" +
                "}\n" +
                "\n" +
                "LocalSymbols {\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n" +
                "\n" +
                "ExternalSymbols {\n" +
                "}\n" +
                "\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "\n" +
                "    .vars {\n" +
                "        .breakid .b0,\n" +
                "        .typeid .t0,\n" +
                "        .typeid .t1,\n" +
                "        .array<.typeid<.any>> .t2,\n" +
                "        .i32 .t3,\n" +
                "        .nullable<.typeid<.any>> .t4,\n" +
                "        .typeid<.any> .t5,\n" +
                "        .i64 .t6,\n" +
                "        .bool .t7,\n" +
                "        .i32 .t8,\n" +
                "        .i32 .t9\n" +
                "    }\n" +
                "\n" +
                "    .block entry entrypoint {\n" +
                "        getid.type type(.i32) $.t0\n" +
                "        getid.type type(.i64) $.t1\n" +
                "        new type(.array<.typeid<.any>>) $.t2 [$.t0, $.t1]\n" +
                "        load res(R_1) $.t3\n" +
                "        get.array $.t2 $.t3 $.t4\n" +
                "        cast $.t4 $.t5 type(.typeid<.any>)\n" +
                "        load res(R_V) $.t6\n" +
                "        type.is.indirect $.t6 $.t5 $.t7\n" +
                "        if $.t7 blk(if0-then) none $.b0\n" +
                "        load res(R_0) $.t8\n" +
                "        ret $.t8\n" +
                "    }\n" +
                "\n" +
                "    .block if0-then {\n" +
                "        load res(R_42) $.t9\n" +
                "        ret $.t9\n" +
                "    }\n" +
                "}\n");
        }

        // BIL 级对拍（前端尚未降级的合法内建形态）：手写 BIL 直接驱 VM 与
        // native，比对口径与 RunCase 相同
        private static void RunBilCase(string label, string bilText,
            IReadOnlyDictionary<string, string>? env = null)
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
                    out var nativeOut, out var nativeErr, environment: env ?? MemtrackEnv);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
                TestHarness.CheckTrue(label + "：退出码一致",
                    runExit == expectedExit, $"native={runExit} vm={expectedExit} stderr={nativeErr}");
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // L6：非 rigi_rt 库 FFI native-only 驱动——Rigi 源走全管线出 BIL；
        // cSource 用工具链 clang -c 现场编成目标文件，native --file --out
        // --link 一次编译链接；执行产物断言 stdout（行尾归一）与退出码
        private static void RunNativeOnlyCase(string label, string source,
            string cSource, string expectedStdout, int expectedExit,
            IReadOnlyDictionary<string, string>? env = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
                var text = BilWriter.Write(module);
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));

                // 现场出最小外部库（复用工具链解析，无外部依赖）
                var clang = ToolchainResolver.ResolveClang(null);
                TestHarness.CheckTrue(label + "：clang 可用", clang != null);
                if (clang == null)
                {
                    return;
                }
                var cPath = Path.Combine(dir, "ffi.c");
                var objPath = Path.Combine(dir, "ffi.o");
                File.WriteAllText(cPath, cSource, new UTF8Encoding(false));
                var cExit = ExternalProcess.Run(clang,
                    new[] { cPath, "-c", "-o", objPath }, out _, out var cErr);
                TestHarness.CheckTrue(label + "：C 源编译成功", cExit == 0, cErr);
                if (cExit != 0)
                {
                    return;
                }

                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath,
                    "--out", exePath, "--link", objPath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runEnv = new Dictionary<string, string>(MemtrackEnv);
                if (env != null)
                    foreach (var entry in env) runEnv[entry.Key] = entry.Value;
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr, environment: runEnv);
                TestHarness.Check(label + "：stdout 符合预期",
                    NormalizeNewlines(nativeOut), expectedStdout);
                TestHarness.CheckTrue(label + "：退出码符合预期",
                    runExit == expectedExit, $"native={runExit} 期望={expectedExit} stderr={nativeErr}");
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
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
