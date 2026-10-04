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
        // 原编号 53..96 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateObjectTypesCases() => new (string Label, Action Run)[]
        {
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
            // review-20260910 #12：全局字段自定义 setter（钳制）——访问器
            // 是 owner==null 的顶层方法，native 的 FindAccessor 曾只按字段
            // 宿主类型沿基类链扫（命名空间宿主查不到类型），写入直写
            // backing 绕过 setter；setter 体内 backing 直访（#..value@
            // 伪字段）不得递归自调
            Case("全局字段自定义 setter 钳制写入",
                "import core.io.Console\n" +
                "pub var missionPhase: i64 {\n" +
                "    pub get(value: _) { return value }\n" +
                "    pub set(value: _) {\n" +
                "        if (value < 0L) { value = 0L }\n" +
                "        if (value > 9L) { value = 9L }\n" +
                "    }\n" +
                "} = 0L\n" +
                "pub func main(): i32 {\n" +
                "    missionPhase = 42L\n" +
                "    const high = missionPhase\n" +
                "    missionPhase = -3L\n" +
                "    const low = missionPhase\n" +
                "    if (high == 9L) { Console.println(\"clamp high ok\") }\n" +
                "    if (low == 0L) { Console.println(\"clamp low ok\") }\n" +
                "    return if ((high == 9L) and (low == 0L)) { 9 } else { 1 }\n" +
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

        };

    }
}
