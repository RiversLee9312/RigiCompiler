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
        // 原编号 258..305 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateDynamicTypesCases() => new (string Label, Action Run)[]
        {
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
            // Map 键判等（equals-or-hash 链，用户裁定）四形态对拍：只断言
            // 行为级结果（count/tryGet），绝不打印具体 hash 数值——VM/native
            // 两宿主哈希数值必然不同
            Case("Map 对象键身份判等不互相覆盖",
                "import core.io.Console\n" +
                "import core.collections.*\n" +
                "pub class Key { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<Key, i32>()\n" +
                "    const k1 = new Key()\n" +
                "    const k2 = new Key()\n" +
                "    m.set(k1, 1)\n" +
                "    m.set(k2, 2)\n" +
                "    if (((m.count == 2L) and ((m.tryGet(k1) if? 0) == 1)) and ((m.tryGet(k2) if? 0) == 2)) { Console.println(\"identity ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("Map String 键内容相同仍合并",
                "import core.io.Console\n" +
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<String, i32>()\n" +
                "    m.set(\"k\", 1)\n" +
                "    m.set(\"k\" + \"\", 2)\n" +
                "    if ((m.count == 1L) and ((m.tryGet(\"k\") if? 0) == 2)) { Console.println(\"merge ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            // review-20260910 #14：Map<String, Any> for-in——MapEnumerator
            // .current 的 `as V`（.nullable<.generic<V>> → V）在 V=Any 时
            // 要求 rigi_sheet_is 的 Any/Object 根规则（Any 不在基类链上）
            Case("Map<String, Any> for-in 迭代",
                "import core.io.Console\n" +
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<String, Any>()\n" +
                "    m.set(\"a\", 1L)\n" +
                "    m.set(\"b\", \"two\")\n" +
                "    var seen = 0L\n" +
                "    for (kv in m) { seen = (seen + 1L) }\n" +
                "    if (seen != 2L) { return 1 }\n" +
                "    Console.println(\"map-any-iter ok\")\n" +
                "    return 0\n" +
                "}\n"),
            Case("Map 自定义 hash 按用户哈希合并",
                "import core.io.Console\n" +
                "import core.collections.*\n" +
                "pub class Badge {\n" +
                "    pub var code: i32\n" +
                "    pub init(v: i32) { code = v }\n" +
                "    pub override func hash(): i64 { return (code as i64) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<Badge, i32>()\n" +
                "    m.set(new Badge(5), 1)\n" +
                "    m.set(new Badge(5), 2)\n" +
                "    m.set(new Badge(6), 3)\n" +
                "    if (((m.count == 2L) and ((m.tryGet(new Badge(5)) if? 0) == 2)) and ((m.tryGet(new Badge(6)) if? 0) == 3)) { Console.println(\"user hash ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            // equals 键对拍（equals-or-hash 链，用户裁定）：键类型声明
            // operator equals（不 override hash）→ 运行期最派生 equals
            // 优先——按字段判等合并/不合并；String 键内容判等回归
            Case("Map 自定义 equals 键按 equals 判等",
                "import core.io.Console\n" +
                "import core.collections.*\n" +
                "pub class Tag {\n" +
                "    pub var id: i32\n" +
                "    pub init(v: i32) { id = v }\n" +
                "    pub operator equals(other: Tag): bool { return (id == other.id) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<Tag, i32>()\n" +
                "    m.set(new Tag(5), 1)\n" +
                "    m.set(new Tag(5), 2)\n" +
                "    m.set(new Tag(6), 3)\n" +
                "    var s = new Map\\<String, i32>()\n" +
                "    s.set(\"k\", 1)\n" +
                "    s.set(\"k\", 9)\n" +
                "    if ((((m.count == 2L) and ((m.tryGet(new Tag(5)) if? 0) == 2)) and ((m.tryGet(new Tag(6)) if? 0) == 3)) and (s.count == 1L)) { Console.println(\"user equals ok\") }\n" +
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
            // ===== 动态 new 隐式默认构造（L7 动态同口径）：全链无 init
            // 声明 + 零实参 → 分配 + 字段零值 + ..init.wrapper 缝合，
            // 不调 init 体（VM TryFindInit argc==0 同口径）=====
            Case("动态 new 零参无显式 init 隐式默认构造",
                "import core.io.Console\n" +
                "pub class Plain {\n" +
                "    pub var x: i32\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Plain)\n" +
                "    var p = new t()\n" +
                "    if (p.x == 0) { Console.println(\"implicit ok\") }\n" +
                "    return p.x\n" +
                "}\n"),
            Case("动态 new 无 init 子类继承有参 init 基类",
                "import core.io.Console\n" +
                "pub open class BaseV {\n" +
                "    pub var b: i32\n" +
                "    pub init(v: i32) { b = v }\n" +
                "}\n" +
                "pub class ChildV : BaseV {\n" +
                "    pub var c: i32\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(ChildV)\n" +
                "    var d = new t()\n" +
                "    if ((d.c == 0) and (d.b == 0)) { Console.println(\"child ok\") }\n" +
                "    return d.c\n" +
                "}\n"),
            Case("动态 new 零参无 init struct",
                "import core.io.Console\n" +
                "pub struct PlainS {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(PlainS)\n" +
                "    var s = new t()\n" +
                "    if ((s.x == 0) and (s.y == 0)) { Console.println(\"plain s ok\") }\n" +
                "    return (s.x + s.y)\n" +
                "}\n"),
            Case("动态 new 零参泛型界无 init 类",
                "import core.io.Console\n" +
                "pub class PlainG {\n" +
                "    pub var x: i32?\n" +
                "}\n" +
                "pub func make\\<T extends PlainG>(): T { return T() }\n" +
                "pub func main(): i32 {\n" +
                "    var p = make\\<PlainG>()\n" +
                "    if (p.x == null) { Console.println(\"generic implicit ok\") }\n" +
                "    return 0\n" +
                "}\n"),
            // 回归对照：字段初始化器经语义合成 init（非隐式路径），
            // 零参动态 new 走显式 init 臂，修复前后都应过
            Case("动态 new 零参字段初始化器合成 init",
                "import core.io.Console\n" +
                "pub class FieldInit {\n" +
                "    pub var x: i32 = 7\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(FieldInit)\n" +
                "    var f = new t()\n" +
                "    if (f.x == 7) { Console.println(\"fieldinit ok\") }\n" +
                "    return f.x\n" +
                "}\n"),
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

        };

    }
}
