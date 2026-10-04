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
        // 原编号 97..135 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateGenericsCases() => new (string Label, Action Run)[]
        {
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
                "        .this = Choice<.generic<$.generic.T>>,\n" +
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

        };

    }
}
