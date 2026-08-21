using System;
using System.Collections.Generic;
using System.Text;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 压力 fuzz 用例模型：一个用例 = 一组源文件（单文件用例只有一项，
    /// 模块/访问领域为 lib + main 多文件）+ 期望（合法/应报 Error）。
    /// </summary>
    internal sealed class FuzzCase
    {
        public required (string Name, string Source)[] Files { get; init; }
        public required bool ExpectError { get; init; }
        public required string Domain { get; init; }
        public required string Template { get; init; }
    }

    /// <summary>
    /// 压力 fuzz 生成器（P20）：按领域分模块模板化组合，覆盖本轮修复的
    /// bug 领域——a 泛型×可空 / b 继承×wrapper / c 嵌套 struct 写穿 /
    /// d 接口菱形·默认方法·like / e 构造体系 / f shared·async / g 模块·访问。
    ///
    /// 确定性：用例 i 由 (Seed, i) 唯一确定——每用例独立
    /// Random(Seed 派生)，与区间起点无关，任意 from..to 可复现。
    ///
    /// 保守模式（约 1/3 用例）：只产合法程序，保证合法路径（零诊断 ⇒
    /// verifier + VM）占比；其余用例按模块权重混入错误注入（应报 Error
    /// 而非崩溃/挂起/非法 BIL）。
    ///
    /// 模板取自 e2e 语料（Tests/e2e/rigi，已验证的正/负例）的参数化变体：
    /// 随机化名称后缀、字面量、子模板选择与可选成员，保持语法必然合法、
    /// 负例必然触发诊断。
    /// </summary>
    internal static class StressFuzzGenerator
    {
        // 领域权重（百分制）：a/b/c 是本轮修复主战场，权重略高
        private static readonly (string Domain, int Weight)[] DomainWeights =
        {
            ("generic-nullable", 18),
            ("inherit-wrapper", 18),
            ("struct-write", 16),
            ("iface-diamond", 12),
            ("construction", 14),
            ("shared-async", 10),
            ("module-access", 12),
        };

        public static FuzzCase Generate(int caseIndex)
        {
            // 每用例独立种子：区间任意切分/单例复现结果一致
            var rng = new Random(unchecked(
                StressFuzzTests.Seed * 1000003 + caseIndex * 7919 + 17));
            bool conservative = rng.Next(3) == 0;

            int roll = rng.Next(100);
            string domain = DomainWeights[^1].Domain;
            int acc = 0;
            foreach (var (name, weight) in DomainWeights)
            {
                acc += weight;
                if (roll < acc) { domain = name; break; }
            }

            return domain switch
            {
                "generic-nullable" => GenGenericNullable(rng, conservative),
                "inherit-wrapper" => GenInheritWrapper(rng, conservative),
                "struct-write" => GenStructWrite(rng, conservative),
                "iface-diamond" => GenIfaceDiamond(rng, conservative),
                "construction" => GenConstruction(rng, conservative),
                "shared-async" => GenSharedAsync(rng, conservative),
                _ => GenModuleAccess(rng, conservative),
            };
        }

        // ===== 小工具 =====

        private static FuzzCase Ok(string domain, string template, string source) =>
            new()
            {
                Files = new[] { ("main.rg", source) },
                ExpectError = false, Domain = domain, Template = template,
            };

        private static FuzzCase Bad(string domain, string template, string source) =>
            new()
            {
                Files = new[] { ("main.rg", source) },
                ExpectError = true, Domain = domain, Template = template,
            };

        private static FuzzCase OkMulti(string domain, string template,
            string lib, string main) =>
            new()
            {
                Files = new[] { ("lib.rg", lib), ("main.rg", main) },
                ExpectError = false, Domain = domain, Template = template,
            };

        private static FuzzCase BadMulti(string domain, string template,
            string lib, string main) =>
            new()
            {
                Files = new[] { ("lib.rg", lib), ("main.rg", main) },
                ExpectError = true, Domain = domain, Template = template,
            };

        // 合法/注入选择：保守模式恒合法；否则 55% 合法 / 45% 错误注入
        private static bool WantError(Random rng, bool conservative) =>
            !conservative && rng.Next(100) >= 55;

        private static int N(Random rng, int max = 50) => rng.Next(max);

        // ===== a. 泛型×可空（g1/g4/g6/g7/g8/g10 + T? 实参与装箱视图）=====

        private static FuzzCase GenGenericNullable(Random rng, bool conservative)
        {
            string s = rng.Next(1000).ToString();
            if (WantError(rng, conservative))
            {
                switch (rng.Next(4))
                {
                    case 0: // g6 负例：无约束 T() —— 编译期拒绝
                        return Bad("generic-nullable", "unconstrained-T()",
                            $"func make{s}\\<T>(): T {{ return T() }}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    case 1: // g4 负例：非 rich 泛型 struct 持有 Object 字段
                        return Bad("generic-nullable", "nonrich-struct-object",
                            $"class Local{s} {{\n    pub const name: String\n" +
                            "    pub init(_ -> name)\n}\n" +
                            $"struct Wrap{s}\\<T> {{\n    pub var v: T\n" +
                            "    pub init(_ -> v)\n}\n" +
                            "pub func main(): i32 {\n" +
                            $"    const w = new Wrap{s}\\<Local{s}>(new Local{s}(\"x\"))\n" +
                            "    return 0\n}\n");
                    case 2: // 具化 init 实参类型不匹配
                        return Bad("generic-nullable", "ctor-arg-mismatch",
                            $"pub class Holder{s}\\<T> {{\n    pub var v: T\n" +
                            "    pub init(_ -> v)\n}\n" +
                            "pub func main(): i32 {\n" +
                            $"    var h = new Holder{s}\\<i32>(\"s\")\n" +
                            "    return 0\n}\n");
                    default: // 显式泛型实参与实参不匹配
                        return Bad("generic-nullable", "generic-arg-mismatch",
                            $"pub func id{s}\\<T>(x: T): T {{ return x }}\n" +
                            "pub func main(): i32 {\n" +
                            $"    var a = id{s}\\<i32>(\"s\")\n" +
                            "    return 0\n}\n");
                }
            }

            switch (rng.Next(7))
            {
                case 0:
                {   // g8：T → T? 装箱视图返回 + if? 读
                    int v = N(rng);
                    return Ok("generic-nullable", "gp-to-nullable",
                        $"pub func wrapNull{s}\\<T>(x: T): T? {{ return x }}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const v = wrapNull{s}\\<i32>({v})\n" +
                        "    return (v if? 0)\n}\n");
                }
                case 1:
                {   // g10：if?/?. 认 Nullable\<GP\>
                    int fb = N(rng);
                    return Ok("generic-nullable", "gp-nullable-if-safe",
                        $"pub func unwrap{s}\\<T>(x: T?, fallback: T): T " +
                        "{ return (x if? fallback) }\n" +
                        $"pub func nameOf{s}\\<T>(x: T?): String? " +
                        "{ return x?.toString() }\n" +
                        "pub func main(): i32 {\n" +
                        $"    const a = unwrap{s}\\<i32>(null, {fb})\n" +
                        $"    const t = (nameOf{s}\\<i32>({N(rng)}) if? \"null\")\n" +
                        "    return a\n}\n");
                }
                case 2:
                {   // g1：泛型实参内嵌可空 Holder\<i32?\>
                    return Ok("generic-nullable", "nullable-generic-arg",
                        $"pub class Holder{s}\\<T> {{\n    pub var v: T\n" +
                        "    pub init(_ -> v)\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    var h: Holder{s}\\<i32?> = new Holder{s}\\<i32?>(null)\n" +
                        "    return (h.v if? -1)\n}\n");
                }
                case 3:
                {   // g7：构造类型上的静态成员
                    int z = N(rng);
                    int w = N(rng);
                    return Ok("generic-nullable", "constructed-static-members",
                        $"pub class Box{s}\\<T> {{\n    pub var v: T\n" +
                        "    pub init(_ -> v)\n" +
                        "    pub static var zero: T\n" +
                        $"    pub static func wrap(x: T): Box{s}\\<T> " +
                        $"{{ return new Box{s}\\<T>(x) }}\n" +
                        "    pub static func reset(x: T) { zero = x }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    Box{s}\\<i32>.zero = {z}\n" +
                        $"    Box{s}\\<i32>.reset({w})\n" +
                        $"    const b = Box{s}\\<i32>.wrap({N(rng)})\n" +
                        $"    return ((Box{s}\\<i32>.zero + b.v))\n}}\n");
                }
                case 4:
                {   // g6 正例：标量界 T() 零值
                    return Ok("generic-nullable", "t-zero-scalar-bound",
                        $"pub func make{s}\\<T extends i32>(): T {{ return T() }}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const x = make{s}\\<i32>()\n" +
                        "    return (x + 0)\n}\n");
                }
                case 5:
                {   // 嵌套具化 + getAtIndex 返回 T? + if?
                    int v = N(rng);
                    return Ok("generic-nullable", "nested-generic-index",
                        $"pub class Box{s}\\<T> {{\n    pub var item: T\n" +
                        "    pub init(_ -> item)\n" +
                        "    pub operator getAtIndex(index: i32): T? { return item }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    var b = new Box{s}\\<i32>({v})\n" +
                        $"    var nested = new Box{s}\\<Box{s}\\<i32>>(b)\n" +
                        "    const row = nested[0]\n" +
                        "    if (row != null) {\n" +
                        "        return (((row[0] if? 0) + (b[0] if? 0)))\n" +
                        "    }\n    return 0\n}\n");
                }
                default:
                {   // g2：整数字面量直接跟扩展成员访问
                    int v = N(rng);
                    return Ok("generic-nullable", "int-literal-member-access",
                        $"pub ext func i32.twice{s}(): i32 {{ return (this * 2) }}\n" +
                        "pub func main(): i32 {\n" +
                        $"    return {v}.twice{s}()\n}}\n");
                }
            }
        }

        // ===== b. 继承×wrapper（o1/o2/o4/o6/o7 + 字段 override + init 缝合）=====

        private static FuzzCase GenInheritWrapper(Random rng, bool conservative)
        {
            string s = rng.Next(1000).ToString();
            if (WantError(rng, conservative))
            {
                switch (rng.Next(3))
                {
                    case 0: // 字段 override 类型与基类不一致
                        return Bad("inherit-wrapper", "field-override-type-mismatch",
                            $"pub open class Base{s} {{ pub open var hp: i32 = 10 }}\n" +
                            $"pub class Hero{s} : Base{s} " +
                            "{ pub override var hp: String = \"x\" }\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    case 1: // 无基类成员的 override
                        return Bad("inherit-wrapper", "override-without-base",
                            $"pub class C{s} {{ pub override var v: i32 = 1 }}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    default: // static 字段 override（静态无多态）
                        return Bad("inherit-wrapper", "static-override-field",
                            $"pub class C{s} {{ pub static override var v: i32 = 1 }}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                }
            }

            switch (rng.Next(7))
            {
                case 0:
                {   // o1：Method wrapper 经基类/接口静态类型虚派发不丢
                    int a = N(rng, 9) + 1;
                    int b = N(rng, 9) + 1;
                    return Ok("inherit-wrapper", "method-wrapper-virtual",
                        "@WrapperTarget(.Method)\n" +
                        $"pub wrapper Trace{s} {{\n    pub init()\n" +
                        "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                        "        core.io.Console.println(\"trace\")\n" +
                        "        return inner()\n    }\n}\n" +
                        $"pub open class Base{s} {{\n    pub init()\n" +
                        $"    @Trace{s}()\n    pub open func work(): i32 {{ return {a} }}\n}}\n" +
                        $"pub class Child{s} : Base{s} {{\n    pub init()\n" +
                        $"    @Trace{s}()\n    pub override func work(): i32 {{ return {b} }}\n}}\n" +
                        $"pub interface Work{s} {{ func work(): i32\n }}\n" +
                        $"pub class Job{s} implements Work{s} {{\n    pub init()\n" +
                        $"    @Trace{s}()\n    pub override func work(): i32 {{ return {b} }}\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const b: Base{s} = new Child{s}()\n" +
                        "    var r = b.work()\n" +
                        $"    const w: Work{s} = new Job{s}()\n" +
                        "    return (r + w.work())\n}\n");
                }
                case 1:
                {   // o2：子类未 override 的基类方法 wrapper 由继承闭包缝合
                    int a = N(rng, 9) + 1;
                    return Ok("inherit-wrapper", "inherited-method-wrapper",
                        "@WrapperTarget(.Method)\n" +
                        $"pub wrapper Trace{s} {{\n    pub init()\n" +
                        "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                        "        core.io.Console.println(\"trace\")\n" +
                        "        return inner()\n    }\n}\n" +
                        $"pub open class Base{s} {{\n    pub init()\n" +
                        $"    @Trace{s}()\n    pub open func work(): i32 {{ return {a} }}\n}}\n" +
                        $"pub class Child{s} : Base{s} {{\n    pub init()\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    return new Child{s}().work()\n}}\n");
                }
                case 2:
                {   // o7：Entity wrapper get.* 先于一切 init 体安装
                    int hp = N(rng) + 1;
                    return Ok("inherit-wrapper", "base-init-wrapper-installed",
                        "@WrapperTarget(.Entity)\n" +
                        $"pub wrapper Audit{s} {{\n    pub init()\n" +
                        "    operator .proxy.get.*\\<TValue>(symbol: String, " +
                        "value: TValue): TValue {\n" +
                        "        core.io.Console.println(\"audit\")\n" +
                        "        return value\n    }\n}\n" +
                        $"@Audit{s}()\npub open class Base{s} {{\n" +
                        $"    pub var hp: i32 = {hp}\n" +
                        "    pub init() { hp = (this.hp + 1) }\n}\n" +
                        $"@Audit{s}()\npub class Hero{s} : Base{s} {{\n" +
                        "    pub init() { super() }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    return new Hero{s}().hp\n}}\n");
                }
                case 3:
                {   // o6：super(...) 实参为形参类型子类
                    int seed = N(rng);
                    return Ok("inherit-wrapper", "super-subtype-arg",
                        $"pub open class Node{s} {{\n    pub var tag: i32 = 0\n" +
                        $"    pub init(n: Node{s}) {{ tag = (n.tag + 1) }}\n" +
                        "    pub init(seed: i32) { tag = seed }\n}\n" +
                        $"pub class Leaf{s} : Node{s} {{\n" +
                        $"    pub init(prev: Leaf{s}) {{ super(prev) }}\n" +
                        "    pub init(seed: i32) { super(seed) }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const b = new Leaf{s}(new Leaf{s}({seed}))\n" +
                        "    return b.tag\n}\n");
                }
                case 4:
                {   // 字段 override 三级链：存储基类槽、虚派发最高派生初值
                    int x = N(rng, 9) + 1;
                    int y = N(rng, 9) + 1;
                    int z = N(rng, 9) + 1;
                    return Ok("inherit-wrapper", "field-override-chain",
                        $"pub open class Base{s} {{\n    pub open var hp: i32 = {x}\n" +
                        "    pub init()\n}\n" +
                        $"pub open class Hero{s} : Base{s} {{\n" +
                        $"    pub override var hp: i32 = {y}\n    pub init()\n}}\n" +
                        $"pub class Villain{s} : Hero{s} {{\n" +
                        $"    pub override var hp: i32 = {z}\n    pub init()\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const v = new Villain{s}()\n" +
                        $"    const viaBase: Base{s} = v\n" +
                        "    return viaBase.hp\n}\n");
                }
                case 5:
                {   // init 缝合：不调 super 时基类字段初值仍先缝合
                    int a1 = N(rng, 9) + 1;
                    int a2 = N(rng, 9) + 1;
                    return Ok("inherit-wrapper", "init-no-super-field-init",
                        $"pub open class A{s} {{\n    pub var a1: i32 = {a1}\n" +
                        $"    pub var a2: i32 = {a2}\n" +
                        "    pub init() { a2 = (a2 + 100) }\n}\n" +
                        $"pub class B{s} : A{s} {{\n    pub init() {{ }}\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const b = new B{s}()\n" +
                        "    return ((b.a1 + b.a2))\n}\n");
                }
                default:
                {   // o4：裸名调用 override 按最派生槽单候选绑定
                    return Ok("inherit-wrapper", "unqualified-override-call",
                        $"pub open class A{s} {{\n" +
                        "    pub open func tag(): String { return \"A\" }\n}\n" +
                        $"pub open class B{s} : A{s} {{\n" +
                        "    pub override func tag(): String { return \"B\" }\n}\n" +
                        $"pub class C{s} : B{s} {{\n" +
                        "    pub override func tag(): String { return \"C\" }\n" +
                        "    pub func own(): String { return tag() }\n}\n" +
                        $"pub class D{s} : B{s} {{\n" +
                        "    pub func own(): String { return tag() }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const c = new C{s}()\n    const d = new D{s}()\n" +
                        "    if ((c.own() == \"C\")) {\n" +
                        "        if ((d.own() == \"B\")) { return 1 }\n    }\n" +
                        "    return 0\n}\n");
                }
            }
        }

        // ===== c. 嵌套 struct 写穿（s1 四形态 + g9 + s2 布局环负例）=====

        private static FuzzCase GenStructWrite(Random rng, bool conservative)
        {
            string s = rng.Next(1000).ToString();
            if (WantError(rng, conservative))
            {
                switch (rng.Next(3))
                {
                    case 0: // s1 负例：索引结果（T?）不是可写 place
                        return Bad("struct-write", "index-field-write-rejected",
                            $"pub struct Vec{s} {{\n    pub var x: i32\n" +
                            "    pub init(_ -> x)\n}\n" +
                            $"class Bag{s} {{\n    pub var held: Vec{s}\n" +
                            $"    pub init(v: Vec{s}) {{ held = v }}\n" +
                            $"    pub operator getAtIndex(index: i32): Vec{s}? " +
                            "{ return held }\n}\n" +
                            $"func f{s}(b: Bag{s}) {{\n    b[0].x = 9\n}}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    case 1: // s2 负例：布局环（自含/互含二选一）
                        if (rng.Next(2) == 0)
                        {
                            return Bad("struct-write", "layout-cycle-self",
                                $"pub struct Box{s} {{\n    pub var next: Box{s}\n" +
                                "    pub init(_ -> next)\n}\n" +
                                "pub func main(): i32 {\n    return 0\n}\n");
                        }
                        return Bad("struct-write", "layout-cycle-mutual",
                            $"pub struct MutA{s} {{\n    pub var b: MutB{s}\n" +
                            "    pub init(_ -> b)\n}\n" +
                            $"pub struct MutB{s} {{\n    pub var a: MutA{s}\n" +
                            "    pub init(_ -> a)\n}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    default: // 字段链叶写类型不匹配
                        return Bad("struct-write", "chain-leaf-type-mismatch",
                            $"pub struct Vec{s} {{\n    pub var x: i32\n" +
                            "    pub init(_ -> x)\n}\n" +
                            $"pub struct Rect{s} {{\n    pub var origin: Vec{s}\n" +
                            "    pub init(_ -> origin)\n}\n" +
                            "pub func main(): i32 {\n" +
                            $"    var r = new Rect{s}(new Vec{s}(1))\n" +
                            "    r.origin.x = \"s\"\n    return 0\n}\n");
                }
            }

            int a = N(rng, 9) + 1;
            int b = N(rng, 9) + 1;
            switch (rng.Next(4))
            {
                case 0: // s1：字段链写穿 + 整字段替换 + this 链写 + 平写
                    return Ok("struct-write", "field-chain-write",
                        $"pub struct Vec{s} {{\n    pub var x: i32\n    pub var y: i32\n" +
                        "    pub init(_ -> x, _ -> y)\n}\n" +
                        $"pub struct Rect{s} {{\n    pub var origin: Vec{s}\n" +
                        $"    pub var size: Vec{s}\n    pub init(_ -> origin, _ -> size)\n" +
                        "    pub func shiftThis() { origin.x = (origin.x + 1) }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    var r = new Rect{s}(new Vec{s}({a}, {b}), new Vec{s}(3, 4))\n" +
                        $"    r.origin.x = {a}\n" +
                        $"    r.origin = new Vec{s}({b}, 2)\n" +
                        "    r.shiftThis()\n" +
                        $"    var v = new Vec{s}({a}, {b})\n    v.x = {b}\n" +
                        "    return ((r.origin.x + v.x))\n}\n");
                case 1: // s1b：值类型 receiver 方法调用写回四形态
                    return Ok("struct-write", "receiver-method-writeback",
                        $"pub struct Vec{s} {{\n    pub var x: i32\n    pub var y: i32\n" +
                        "    pub init(_ -> x, _ -> y)\n" +
                        "    pub func bumpX() { x = (x + 1) }\n}\n" +
                        $"pub struct Rect{s} {{\n    pub var origin: Vec{s}\n" +
                        $"    pub var size: Vec{s}\n    pub init(_ -> origin, _ -> size)\n" +
                        "    pub func bumpOrigin() { origin.bumpX() }\n" +
                        "    pub func addOrigin() { origin.x += 1 }\n" +
                        "    pub func replaceOrigin() {\n" +
                        "        var o = origin\n        o.x = (o.x + 1)\n        origin = o\n" +
                        "    }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    var r1 = new Rect{s}(new Vec{s}({a}, 0), new Vec{s}(0, 0))\n" +
                        "    r1.origin.bumpX()\n" +
                        $"    var r2 = new Rect{s}(new Vec{s}({a}, 0), new Vec{s}(0, 0))\n" +
                        "    r2.bumpOrigin()\n" +
                        $"    var r3 = new Rect{s}(new Vec{s}({a}, 0), new Vec{s}(0, 0))\n" +
                        "    r3.addOrigin()\n" +
                        $"    var r4 = new Rect{s}(new Vec{s}({a}, 0), new Vec{s}(0, 0))\n" +
                        "    r4.replaceOrigin()\n" +
                        "    return (((r1.origin.x + r2.origin.x) + " +
                        "(r3.origin.x + r4.origin.x)))\n}\n");
                case 2: // 三层嵌套 + 复合赋值 + class 中间边界
                    return Ok("struct-write", "three-level-nested",
                        $"pub struct Deep{s} {{\n    pub var v: i32\n" +
                        "    pub init(_ -> v)\n}\n" +
                        $"pub struct Mid{s} {{\n    pub var leaf: Deep{s}\n" +
                        "    pub init(_ -> leaf)\n}\n" +
                        $"pub class Outer{s} {{\n    pub var mid: Mid{s}\n" +
                        "    pub init(_ -> mid)\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    var o = new Outer{s}(new Mid{s}(new Deep{s}({a})))\n" +
                        $"    o.mid.leaf.v = {b}\n" +
                        "    o.mid.leaf.v += 8\n" +
                        "    return o.mid.leaf.v\n}\n");
                default: // g9：class 内嵌 struct 字段写
                    return Ok("struct-write", "class-embedded-struct",
                        $"pub struct Num{s} {{\n    pub var v: i32\n" +
                        "    pub init(_ -> v)\n}\n" +
                        $"pub class BoxNum{s} {{\n    pub var item: Num{s}\n" +
                        "    pub init(_ -> item)\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    var bn = new BoxNum{s}(new Num{s}({a}))\n" +
                        $"    bn.item.v = {b}\n" +
                        "    return bn.item.v\n}\n");
            }
        }

        // ===== d. 接口菱形/默认方法/like 委托（s3 三形态 + o3）=====

        private static FuzzCase GenIfaceDiamond(Random rng, bool conservative)
        {
            string s = rng.Next(1000).ToString();
            if (WantError(rng, conservative))
            {
                if (rng.Next(2) == 0)
                {   // s3 负例：两接口同签名默认方法冲突未显式 override
                    return Bad("iface-diamond", "conflict-default",
                        $"pub interface A{s} {{\n    func id(): i32\n" +
                        "    func tag(): String { return \"A\" }\n}\n" +
                        $"pub interface B{s} {{\n    func id(): i32\n" +
                        "    func tag(): String { return \"B\" }\n}\n" +
                        $"pub class C{s} implements A{s}, B{s} {{\n    pub init()\n" +
                        "    pub override func id(): i32 { return 1 }\n}\n" +
                        "pub func main(): i32 {\n    return 0\n}\n");
                }
                // 实现类缺接口成员
                return Bad("iface-diamond", "missing-interface-member",
                    $"pub interface Work{s} {{\n    func run(x: i32): i32\n" +
                    "    func tag(): String\n}\n" +
                    $"pub class Impl{s} implements Work{s} {{\n" +
                    "    pub override func run(x: i32): i32 { return (x + 1) }\n}\n" +
                    "pub func main(): i32 {\n    return 0\n}\n");
            }

            switch (rng.Next(4))
            {
                case 0: // s3 正例·真菱形：同一符号默认经两路径继承不算冲突
                    return Ok("iface-diamond", "true-diamond",
                        $"pub interface Base{s} {{ func tag(): String {{ return \"base\" }} }}\n" +
                        $"pub interface A{s} : Base{s} {{ }}\n" +
                        $"pub interface B{s} : Base{s} {{ }}\n" +
                        $"pub class C{s} implements A{s}, B{s} {{\n    pub init()\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const c = new C{s}()\n" +
                        "    if ((c.tag() == \"base\")) { return 1 }\n    return 0\n}\n");
                case 1: // s3 正例·显式 override 解冲突，双视图同派发
                    return Ok("iface-diamond", "explicit-override",
                        $"pub interface A{s} {{\n    func id(): i32\n" +
                        "    func tag(): String { return \"A\" }\n}\n" +
                        $"pub interface B{s} {{\n    func id(): i32\n" +
                        "    func tag(): String { return \"B\" }\n}\n" +
                        $"pub class C{s} implements A{s}, B{s} {{\n    pub init()\n" +
                        "    pub override func id(): i32 { return 1 }\n" +
                        "    pub override func tag(): String { return \"C\" }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    var a: A{s} = new C{s}()\n    var b: B{s} = new C{s}()\n" +
                        "    if ((a.tag() == b.tag())) { return (a.id() + b.id()) }\n" +
                        "    return 0\n}\n");
                case 2: // o3：like 目标字段为接口类型，转发体虚派发
                    return Ok("iface-diamond", "like-interface-field",
                        $"pub interface Work{s} {{ func run(x: i32): i32\n }}\n" +
                        $"pub class Impl{s} implements Work{s} {{\n" +
                        "    pub override func run(x: i32): i32 { return (x + 1) }\n}\n" +
                        $"pub class Via{s} implements Work{s} like sink {{\n" +
                        $"    pub var sink: Work{s} = new Impl{s}()\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const v = new Via{s}()\n" +
                        $"    const w: Work{s} = v\n" +
                        "    return (v.run(3) + w.run(4))\n}\n");
                default: // like 委托：显式 override 优先于转发
                    return Ok("iface-diamond", "like-explicit-first",
                        $"pub interface Fruit{s} {{\n    func taste(): String\n" +
                        "    func color(): String\n}\n" +
                        $"pub class Pear{s} implements Fruit{s} {{\n" +
                        "    pub override func taste(): String { return \"pear\" }\n" +
                        "    pub override func color(): String { return \"green\" }\n}\n" +
                        $"pub class Apple{s} implements Fruit{s} like pear {{\n" +
                        $"    pub var pear: Pear{s} = new Pear{s}()\n" +
                        "    pub override func taste(): String { return \"apple\" }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const a = new Apple{s}()\n" +
                        $"    const f: Fruit{s} = a\n" +
                        "    if ((a.taste() == \"apple\")) {\n" +
                        "        if ((f.color() == \"green\")) { return 1 }\n    }\n" +
                        "    return 0\n}\n");
            }
        }

        // ===== e. 构造体系（init 映射/字段初值/DA 边界/T() 界规则）=====

        private static FuzzCase GenConstruction(Random rng, bool conservative)
        {
            string s = rng.Next(1000).ToString();
            if (WantError(rng, conservative))
            {
                switch (rng.Next(3))
                {
                    case 0: // DA 负例：init 未给字段赋值
                        return Bad("construction", "field-not-assigned",
                            $"pub class Q{s} {{\n    pub var x: i32\n" +
                            "    pub init() { }\n}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    case 1: // DA 负例：局部变量未赋值即用
                        return Bad("construction", "local-unassigned-use",
                            "pub func main(): i32 {\n    var x: i32\n    return x\n}\n");
                    default: // init 映射到不存在的字段
                        return Bad("construction", "init-map-unknown-field",
                            $"pub class R{s} {{\n    pub var x: i32\n" +
                            "    pub init(_ -> nosuch) { x = 1 }\n}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                }
            }

            int a = N(rng, 9) + 1;
            int b = N(rng, 9) + 1;
            switch (rng.Next(4))
            {
                case 0: // n1：全局/静态字段声明初值 main 前执行
                    return Ok("construction", "global-field-init",
                        $"var g{s}: i32 = {a}\n" +
                        $"const cg{s}: i32 = {b}\n" +
                        $"pub class Holder{s} {{\n    pub static var sv: i32 = {a}\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    return (((g{s} + cg{s}) + Holder{s}.sv))\n}}\n");
                case 1: // init 映射 + 默认值 + 重载
                    return Ok("construction", "init-mapping-overload",
                        $"pub class Pt{s} {{\n    pub var x: i32\n    pub var y: i32\n" +
                        $"    pub init(_ -> x = {a}) {{ y = 0 }}\n" +
                        $"    pub init(_ -> x, _ -> y) {{ }}\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    var p = new Pt{s}()\n" +
                        $"    var q = new Pt{s}({a}, {b})\n" +
                        $"    var r = new Pt{s}({b})\n" +
                        "    return (((p.x + q.y) + r.x))\n}\n");
                case 2: // DA 边界：双分支都赋值后可用
                    return Ok("construction", "da-both-branches",
                        "pub func main(): i32 {\n    var x: i32\n" +
                        $"    if (({a} == {a})) {{ x = {a} }} else {{ x = {b} }}\n" +
                        "    return x\n}\n");
                default: // 构造链：init 体内显式赋值 + 字段初值覆盖序
                    return Ok("construction", "init-body-overrides-initializer",
                        $"pub class Cfg{s} {{\n    pub var v: i32 = {a}\n" +
                        $"    pub var w: i32 = {b}\n" +
                        $"    pub init(step: i32) {{ v = (v + step) }}\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const c = new Cfg{s}({a})\n" +
                        "    return ((c.v + c.w))\n}\n");
            }
        }

        // ===== f. shared/async（a1 一致性 + a2 传染矩阵 + 闸门）=====

        private static FuzzCase GenSharedAsync(Random rng, bool conservative)
        {
            string s = rng.Next(1000).ToString();
            if (WantError(rng, conservative))
            {
                switch (rng.Next(4))
                {
                    case 0: // a2：非 shared class 实现 shared 接口
                        return Bad("shared-async", "contagion-implements",
                            $"pub shared interface Worker{s} {{ func run(x: i32): i32\n }}\n" +
                            $"pub class W{s} implements Worker{s} {{\n" +
                            "    pub override func run(x: i32): i32 { return (x + 1) }\n}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    case 1: // a2：派生接口继承 shared 基接口未标 shared
                        return Bad("shared-async", "contagion-inherit",
                            $"pub shared interface IBase{s} {{ }}\n" +
                            $"pub interface IChild{s} : IBase{s} {{ }}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    case 2: // a2：非 shared 接口声明 async 成员
                        return Bad("shared-async", "nonshared-interface-async",
                            $"pub interface Worker{s} {{\n    async func run(x: i32): i32\n}}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                    default: // a1：async override 满足 sync 接口成员（类型洞）
                        return Bad("shared-async", "async-override-mismatch",
                            $"pub interface Worker{s} {{\n    func run(x: i32): i32\n}}\n" +
                            $"pub shared class W{s} implements Worker{s} {{\n    pub init()\n" +
                            "    pub async override func run(x: i32): i32 { return (x + 1) }\n}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                }
            }

            switch (rng.Next(3))
            {
                case 0: // a2 正例：shared 接口 async 成员经接口 await
                    return Ok("shared-async", "shared-interface-await",
                        $"pub shared interface Worker{s} {{\n    async func run(x: i32): i32\n}}\n" +
                        $"pub shared class W{s} implements Worker{s} {{\n    pub init()\n" +
                        "    pub async override func run(x: i32): i32 { return (x + 1) }\n}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const w: Worker{s} = new W{s}()\n" +
                        "    const r = await w.run(10)\n    return r\n}\n");
                case 1: // async 基础：await 取值 + 重复 await 已完成 Task
                    {
                        int v = N(rng);
                        return Ok("shared-async", "async-await-result",
                            $"async func add{s}(n: i32): i32 {{ return (n + 1) }}\n" +
                            "pub func main(): i32 {\n" +
                            $"    var t = add{s}({v})\n" +
                            "    var x = await t\n    var y = await t\n" +
                            "    return (x + y)\n}\n");
                    }
                default: // 传染链全标 shared 正例（声明级，无 await）
                    return Ok("shared-async", "shared-chain-all-marked",
                        $"pub shared interface IA{s} {{ }}\n" +
                        $"pub shared interface IB{s} : IA{s} {{ }}\n" +
                        $"pub shared class Impl{s} implements IB{s} {{\n    pub init()\n}}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const i = new Impl{s}()\n    return 0\n}}\n");
            }
        }

        // ===== g. 模块/访问（s4 具名导入 / s5 签名泄漏 / 推断路径）=====

        private static FuzzCase GenModuleAccess(Random rng, bool conservative)
        {
            string s = rng.Next(1000).ToString();
            string libNs = $"scene{s}.geom";
            string lib =
                $"namespace {libNs}\n" +
                $"pub class Vec{s} {{\n    pub var x: i32\n    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n}\n" +
                $"pub const boost{s}: i32 = {N(rng, 9) + 1}\n" +
                $"pub func pick{s}(v: Vec{s}): String {{ return \"vec\" }}\n" +
                $"pub func pick{s}(x: i32, y: i32): String {{ return \"xy\" }}\n";

            if (WantError(rng, conservative))
            {
                switch (rng.Next(3))
                {
                    case 0: // s5：私有类型经 pub 返回值泄漏（声明点 + 使用点）
                        return BadMulti("module-access", "signature-leak",
                            $"class Hidden{s} {{\n    pub init()\n" +
                            "    pub func n(): i32 { return 1 }\n}\n" +
                            $"pub func make{s}(): Hidden{s} {{ return new Hidden{s}() }}\n",
                            "pub func main(): i32 {\n" +
                            $"    const h = make{s}()\n    return h.n()\n}}\n");
                    case 1: // 双具名同名导入歧义
                        return BadMulti("module-access", "ambiguous-import",
                            lib +
                            $"namespace other{s}\n" +
                            $"pub func pick{s}(x: i32, y: i32): String {{ return \"o\" }}\n",
                            $"import {libNs}.pick{s}\n" +
                            $"import other{s}.pick{s}\n" +
                            "pub func main(): i32 {\n" +
                            $"    const t = pick{s}(1, 2)\n    return 0\n}}\n");
                    default: // 具名导入不存在的成员
                        return BadMulti("module-access", "import-unknown-member",
                            lib,
                            $"import {libNs}.nosuch{s}\n" +
                            "pub func main(): i32 {\n    return 0\n}\n");
                }
            }

            switch (rng.Next(3))
            {
                case 0: // s4：具名导入函数重载全导入 + 全局 const
                    return OkMulti("module-access", "named-import", lib,
                        $"import {libNs}.pick{s}\n" +
                        $"import {libNs}.Vec{s}\n" +
                        $"import {libNs}.boost{s}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const v = new Vec{s}(3, 4)\n" +
                        $"    const t1 = pick{s}(v)\n" +
                        $"    const t2 = pick{s}(1, 2)\n" +
                        $"    if ((t1 == \"vec\")) {{ return boost{s} }}\n" +
                        "    return 0\n}\n");
                case 1: // 通配导入 + 推断局部
                    return OkMulti("module-access", "wildcard-import-infer", lib,
                        $"import {libNs}.*\n" +
                        "pub func main(): i32 {\n" +
                        $"    var v = new Vec{s}(1, 2)\n" +
                        $"    var t = pick{s}(v)\n" +
                        $"    var k = boost{s}\n" +
                        "    if ((t == \"vec\")) { return k }\n    return 0\n}\n");
                default: // 本地同名遮蔽具名导入
                    return OkMulti("module-access", "local-shadows-import", lib,
                        $"import {libNs}.pick{s}\n" +
                        $"pub func pick{s}(): String {{ return \"local\" }}\n" +
                        "pub func main(): i32 {\n" +
                        $"    const t = pick{s}()\n" +
                        "    if ((t == \"local\")) { return 1 }\n    return 0\n}\n");
            }
        }
    }
}
