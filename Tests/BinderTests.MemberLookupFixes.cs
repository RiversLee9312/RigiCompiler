using System.Linq;

namespace RigiCompiler.Tests
{
    // P3 成员查找与流分析修复组：
    // A2/C4 接口默认方法在实现类上可直接调用（SYNTAX §11 / §9.2.1）；
    // A4 赋值 LHS 接收者享受 smart cast（SYNTAX §3.5）；
    // C1 裸字段名在调用接收者位置先按实例成员解析。
    public static partial class BinderTests
    {
        private static void TestMemberLookupFixes()
        {
            TestInterfaceDefaultOnConcrete();
            TestAssignmentLhsSmartCast();
            TestBareFieldCallReceiver();
            TestUnqualifiedOverrideShadowing();
        }

        // ===== A2/C4：接口默认实现隐式继承，具体类上可直接调用 =====
        private static void TestInterfaceDefaultOnConcrete()
        {
            TestHarness.Section("P3 MemberLookupFixes: 接口默认方法");

            var (unit, bodies) = BindUnit(
                "pub interface INamed {\n" +
                "    func getName(): String\n" +
                "    func greet(): String { return this.getName() }\n" +
                "}\n" +
                "pub class Dog implements INamed {\n" +
                "    pub override func getName(): String { return \"Rex\" }\n" +
                "}\n" +
                "pub func viaClass(d: Dog): String { return d.greet() }\n" +
                "pub func viaIface(i: INamed): String { return i.greet() }\n");
            CheckNoErrors("无诊断（具体类调默认方法）", unit);
            TestHarness.CheckTrue("具体类接收者命中接口默认 greet",
                BoundDescribe.Body(BodyOf(bodies, "viaClass")).Contains(
                    "InstCall(greet, Param(d,Dog), [], String)"));
            TestHarness.CheckTrue("接口接收者对照仍合法",
                BoundDescribe.Body(BodyOf(bodies, "viaIface")).Contains(
                    "InstCall(greet, Param(i,INamed), [], String)"));
            var named = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "INamed");
            var viaClass = (BoundReturnStatement)BodyOf(bodies, "viaClass").Body.Statements[0];
            TestHarness.CheckTrue("具体类调用绑定接口默认方法符号",
                ReferenceEquals(((BoundInstanceCallExpression)viaClass.Value!).Method,
                    named.Methods.Single(m => m.Name == "greet")));

            // 子类的子类：InterfaceClosure 沿基类链收 implements
            var (grand, _) = BindUnit(
                "pub interface INamed {\n" +
                "    func greet(): String { return \"hi\" }\n" +
                "}\n" +
                "pub open class Dog implements INamed { }\n" +
                "pub class Puppy : Dog { }\n" +
                "pub func f(p: Puppy): String { return p.greet() }\n");
            CheckNoErrors("无诊断（孙类调默认方法）", grand);

            // 实现类体内裸名调用默认方法
            var (bare, bareBodies) = BindUnit(
                "pub interface INamed {\n" +
                "    func greet(): String { return \"hi\" }\n" +
                "}\n" +
                "pub class Dog implements INamed {\n" +
                "    pub func speak(): String { return greet() }\n" +
                "}\n");
            CheckNoErrors("无诊断（体内裸名默认方法）", bare);
            TestHarness.CheckTrue("裸名 greet 补 this",
                BoundDescribe.Body(BodyOf(bareBodies, "speak")).Contains(
                    "InstCall(greet, This(Dog), [], String)"));

            // 泛型接口默认方法：代入后签名参与重载
            var (generic, genericBodies) = BindUnit(
                "pub interface IBox\\<T> {\n" +
                "    func wrap(x: T): T { return x }\n" +
                "    func tag(): String { return \"box\" }\n" +
                "}\n" +
                "pub class IntBox implements IBox\\<i32> { }\n" +
                "pub func useWrap(b: IntBox): i32 { return b.wrap(7) }\n" +
                "pub func useTag(b: IntBox): String { return b.tag() }\n");
            CheckNoErrors("无诊断（泛型接口默认方法）", generic);
            TestHarness.CheckTrue("wrap 代入 T→i32",
                BoundDescribe.Body(BodyOf(genericBodies, "useWrap")).Contains(
                    "InstCall(wrap, Param(b,IntBox), [Int(7,i32)], i32)"));

            // override 覆盖默认实现后具体类走 override，不再双候选
            var (over, overBodies) = BindUnit(
                "pub interface IBox\\<T> {\n" +
                "    func wrap(x: T): T { return x }\n" +
                "}\n" +
                "pub class IntBox implements IBox\\<i32> {\n" +
                "    pub override func wrap(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func f(b: IntBox): i32 { return b.wrap(1) }\n");
            CheckNoErrors("无诊断（override 覆盖默认）", over);
            var intBox = over.Symbols.GlobalNamespace.Types.Single(t => t.Name == "IntBox");
            var overRet = (BoundReturnStatement)BodyOf(overBodies, "f").Body.Statements[0];
            TestHarness.CheckTrue("命中类上 override 而非接口默认",
                ReferenceEquals(((BoundInstanceCallExpression)overRet.Value!).Method,
                    intBox.Methods.Single(m => m.Name == "wrap")));

            // super 只看直接基类（SYNTAX §9.2.2），到不了接口默认实现
            var (super, _) = BindUnit(
                "pub interface INamed {\n" +
                "    func greet(): String { return \"hi\" }\n" +
                "}\n" +
                "pub class Dog implements INamed {\n" +
                "    pub override func greet(): String { return super() }\n" +
                "}\n");
            TestHarness.CheckSemanticError("super 不能调接口默认方法", super.Diagnostics,
                "direct base type has no method 'greet' for super(...)");

            // 反例：无体接口成员未实现仍报未实现；已实现的才能调
            var (missing, _) = BindUnit(
                "pub interface INamed { func getName(): String }\n" +
                "pub class Dog implements INamed { }\n" +
                "pub func f(d: Dog): String { return d.getName() }\n");
            TestHarness.CheckSemanticError("无体接口成员必须实现", missing.Diagnostics,
                "does not implement abstract member 'getName'");

            // 反例：具体类上不存在的成员仍未定义
            var (undef, _) = BindUnit(
                "pub interface INamed { func greet(): String { return \"hi\" } }\n" +
                "pub class Dog implements INamed { }\n" +
                "pub func f(d: Dog): i32 { return d.missing() }\n");
            TestHarness.CheckSemanticError("未声明成员仍未定义", undef.Diagnostics,
                "Undefined member 'missing' on type 'Dog'");
        }

        // ===== A4：赋值 LHS 接收者与读取同等收窄 =====
        private static void TestAssignmentLhsSmartCast()
        {
            TestHarness.Section("P3 MemberLookupFixes: 赋值 LHS smart cast");

            var (unit, bodies) = BindUnit(
                "pub class Node { pub var v: i32 }\n" +
                "pub func f(t: Node?): i32 {\n" +
                "    if (t == null) { return 0 }\n" +
                "    t.v = 2\n" +
                "    return t.v\n" +
                "}\n");
            CheckNoErrors("无诊断（guard 后写字段）", unit);
            TestHarness.CheckTrue("赋值 LHS 接收者包 SmartCast",
                BoundDescribe.Body(BodyOf(bodies, "f")).Contains(
                    "Assign(InstField(v, SmartCast(Param(t,Node?), Node), i32), Int(2,i32))"));

            // 深路径 a.b.c = v
            var (deep, deepBodies) = BindUnit(
                "pub class Leaf { pub var v: i32 }\n" +
                "pub class Mid { pub var leaf: Leaf }\n" +
                "pub class Outer { pub var mid: Mid }\n" +
                "pub func f(a: Outer?): i32 {\n" +
                "    if (a == null) { return 0 }\n" +
                "    a.mid.leaf.v = 2\n" +
                "    return a.mid.leaf.v\n" +
                "}\n");
            CheckNoErrors("无诊断（深路径赋值 LHS）", deep);
            TestHarness.CheckTrue("深路径头接收者收窄",
                BoundDescribe.Body(BodyOf(deepBodies, "f")).Contains(
                    "InstField(v, InstField(leaf, InstField(mid, SmartCast(Param(a,Outer?), Outer), Mid), Leaf), i32)"));

            // 循环内赋值 LHS：while 条件为真的收窄在体内有效（§3.5）
            var (loop, loopBodies) = BindUnit(
                "pub class Node { pub var v: i32 }\n" +
                "pub func f(t: Node?): i32 {\n" +
                "    while (t != null) {\n" +
                "        t.v = 1\n" +
                "        return t.v\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("无诊断（循环内赋值 LHS）", loop);
            TestHarness.CheckTrue("while 真边赋值接收者收窄",
                BoundDescribe.Body(BodyOf(loopBodies, "f")).Contains(
                    "Assign(InstField(v, SmartCast(Param(t,Node?), Node), i32), Int(1,i32))"));

            // 复合赋值：读路径本已收窄，回归不回退
            var (compound, compoundBodies) = BindUnit(
                "pub class Node { pub var v: i32 }\n" +
                "pub func f(t: Node?): i32 {\n" +
                "    if (t == null) { return 0 }\n" +
                "    t.v += 1\n" +
                "    return t.v\n" +
                "}\n");
            CheckNoErrors("无诊断（复合赋值 LHS）", compound);
            TestHarness.CheckTrue("复合赋值接收者收窄",
                BoundDescribe.Body(BodyOf(compoundBodies, "f")).Contains(
                    "CompoundAssign(Add, InstField(v, SmartCast(Param(t,Node?), Node), i32)"));

            // 赋值 place 本身（t = ...）仍不包 SmartCast
            var (self, selfBodies) = BindUnit(
                "pub class Node { pub var v: i32 }\n" +
                "pub func f(t: Node?): i32 {\n" +
                "    if (t == null) { return 0 }\n" +
                "    t = null\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("无诊断（对自身赋值）", self);
            TestHarness.CheckTrue("对自身赋值目标不包 SmartCast",
                BoundDescribe.Body(BodyOf(selfBodies, "f")).Contains(
                    "Assign(Param(t,Node?), Null(Node?))"));

            // const 字段稳定链：中间可空字段收窄后写叶
            var (constChain, _) = BindUnit(
                "pub class Leaf { pub var v: i32 }\n" +
                "pub class Outer { pub const mid: Leaf? }\n" +
                "pub func f(a: Outer): i32 {\n" +
                "    if (a.mid == null) { return 0 }\n" +
                "    a.mid.v = 2\n" +
                "    return a.mid.v\n" +
                "}\n");
            CheckNoErrors("无诊断（const 链中间收窄后写）", constChain);

            // 反例：无 guard 仍拒绝可空点访问
            var (noGuard, _) = BindUnit(
                "pub class Node { pub var v: i32 }\n" +
                "pub func f(t: Node?) { t.v = 2 }\n");
            TestHarness.CheckSemanticError("无收窄仍拒绝可空写", noGuard.Diagnostics,
                "cannot be accessed on nullable type");

            // 反例：var 字段不可收窄，中间可空仍拒绝
            var (varField, _) = BindUnit(
                "pub class Leaf { pub var v: i32 }\n" +
                "pub class Outer { pub var mid: Leaf? }\n" +
                "pub func f(a: Outer) {\n" +
                "    if (a.mid == null) { return }\n" +
                "    a.mid.v = 2\n" +
                "}\n");
            TestHarness.CheckSemanticError("var 字段不收窄", varField.Diagnostics,
                "cannot be accessed on nullable type");
        }

        // ===== C1：裸字段名在调用接收者位置先按实例成员解析 =====
        private static void TestBareFieldCallReceiver()
        {
            TestHarness.Section("P3 MemberLookupFixes: 裸字段调用接收者");

            var (unit, bodies) = BindUnit(
                "pub class Counter {\n" +
                "    pub func inc(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    pub var c: Counter\n" +
                "    pub func run(): i32 { return c.inc() }\n" +
                "    pub func viaThis(): i32 { return this.c.inc() }\n" +
                "}\n");
            CheckNoErrors("无诊断（裸字段作调用接收者）", unit);
            TestHarness.CheckTrue("c.inc 绑实例调用",
                BoundDescribe.Body(BodyOf(bodies, "run")).Contains(
                    "InstCall(inc, InstField(c, This(Holder), Counter), [], i32)"));
            TestHarness.CheckTrue("this.c.inc 对照",
                BoundDescribe.Body(BodyOf(bodies, "viaThis")).Contains(
                    "InstCall(inc, InstField(c, This(Holder), Counter), [], i32)"));

            // 泛型字段类型
            var (generic, _) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub func get(): T { return item }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    pub var b: Box\\<i32>\n" +
                "    pub func run(): i32 { return b.get() }\n" +
                "}\n");
            CheckNoErrors("无诊断（泛型字段接收者）", generic);

            // 扩展方法挂在字段类型上
            var (ext, _) = BindUnit(
                "pub class Counter { }\n" +
                "pub ext func Counter.twice(): i32 { return 2 }\n" +
                "pub class Holder {\n" +
                "    pub var c: Counter\n" +
                "    pub func run(): i32 { return c.twice() }\n" +
                "}\n");
            CheckNoErrors("无诊断（字段接收者 + 扩展方法）", ext);

            // 同名字段与类型共存：字段优先
            var (same, sameBodies) = BindUnit(
                "pub class Counter {\n" +
                "    pub static func zero(): i32 { return 0 }\n" +
                "    pub func inc(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    pub var Counter: Counter\n" +
                "    pub func run(): i32 { return Counter.inc() }\n" +
                "}\n");
            CheckNoErrors("无诊断（同名字段优先于类型）", same);
            TestHarness.CheckTrue("同名时走字段实例调用",
                BoundDescribe.Body(BodyOf(sameBodies, "run")).Contains(
                    "InstCall(inc, InstField(Counter, This(Holder), Counter), [], i32)"));

            // 对照：无同名字段时类型静态方法仍合法
            var (typeOnly, typeBodies) = BindUnit(
                "pub class Counter {\n" +
                "    pub static func zero(): i32 { return 0 }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    pub func run(): i32 { return Counter.zero() }\n" +
                "}\n");
            CheckNoErrors("无诊断（无同名字段走类型）", typeOnly);
            TestHarness.CheckTrue("无字段时走静态调用",
                BoundDescribe.Body(BodyOf(typeBodies, "run")).Contains(
                    "Call(zero, [], i32)"));

            // 先赋局部再调对照
            var (local, _) = BindUnit(
                "pub class Counter { pub func inc(): i32 { return 1 } }\n" +
                "pub class Holder {\n" +
                "    pub var c: Counter\n" +
                "    pub func run(): i32 {\n" +
                "        const x = c\n" +
                "        return x.inc()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（先赋局部再调）", local);

            // 反例：静态上下文裸实例字段
            var (stat, _) = BindUnit(
                "pub class Counter { pub func inc(): i32 { return 1 } }\n" +
                "pub class Holder {\n" +
                "    pub var c: Counter\n" +
                "    pub static func run(): i32 { return c.inc() }\n" +
                "}\n");
            TestHarness.CheckSemanticError("静态上下文裸实例字段", stat.Diagnostics,
                "instance field 'c' requires a receiver");

            // 反例：未定义字段仍按类型/名失败
            var (missing, _) = BindUnit(
                "pub class Holder {\n" +
                "    pub func run(): i32 { return missing.inc() }\n" +
                "}\n");
            TestHarness.CheckSemanticError("未定义字段接收者", missing.Diagnostics,
                "Unresolved type or namespace: 'missing'");
        }

        // ===== O4：无限定调用 override 方法按最派生槽单候选（与 this.m() 同口径）=====
        private static void TestUnqualifiedOverrideShadowing()
        {
            TestHarness.Section("P3 MemberLookupFixes: 无限定 override 遮蔽");

            // bug O4 本体：裸名 show() 不再歧义，绑定子类 override
            var (unit, bodies) = BindUnit(
                "pub open class Base {\n" +
                "    pub open func show(): String { return \"B\" }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub override func show(): String { return \"C\" }\n" +
                "    pub func own(): String { return show() }\n" +
                "}\n");
            CheckNoErrors("无诊断（裸名调 override 方法）", unit);
            TestHarness.CheckTrue("裸名 show 补 this",
                BoundDescribe.Body(BodyOf(bodies, "own")).Contains(
                    "InstCall(show, This(Child), [], String)"));
            var child = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Child");
            var ownRet = (BoundReturnStatement)BodyOf(bodies, "own").Body.Statements[0];
            TestHarness.CheckTrue("裸名 show 命中最派生槽（Child.show）",
                ReferenceEquals(((BoundInstanceCallExpression)ownRet.Value!).Method,
                    child.Methods.Single(m => m.Name == "show")));

            // 多层继承链 A→B→C：中间层与叶层都 override，裸名命中 C 版
            var (chain, chainBodies) = BindUnit(
                "pub open class A {\n" +
                "    pub open func tag(): String { return \"A\" }\n" +
                "}\n" +
                "pub open class B : A {\n" +
                "    pub override func tag(): String { return \"B\" }\n" +
                "}\n" +
                "pub class C : B {\n" +
                "    pub override func tag(): String { return \"C\" }\n" +
                "    pub func own(): String { return tag() }\n" +
                "}\n");
            CheckNoErrors("无诊断（多层链裸名调用）", chain);
            var cType = chain.Symbols.GlobalNamespace.Types.Single(t => t.Name == "C");
            var chainRet = (BoundReturnStatement)BodyOf(chainBodies, "own").Body.Statements[0];
            TestHarness.CheckTrue("多层链裸名命中 C 版",
                ReferenceEquals(((BoundInstanceCallExpression)chainRet.Value!).Method,
                    cType.Methods.Single(m => m.Name == "tag")));

            // 多层链叶层不 override：裸名命中中间层 B 版（最派生槽）
            var (mid, midBodies) = BindUnit(
                "pub open class A {\n" +
                "    pub open func tag(): String { return \"A\" }\n" +
                "}\n" +
                "pub open class B : A {\n" +
                "    pub override func tag(): String { return \"B\" }\n" +
                "}\n" +
                "pub class C : B {\n" +
                "    pub func own(): String { return tag() }\n" +
                "}\n");
            CheckNoErrors("无诊断（叶层不 override）", mid);
            var bType = mid.Symbols.GlobalNamespace.Types.Single(t => t.Name == "B");
            var midRet = (BoundReturnStatement)BodyOf(midBodies, "own").Body.Statements[0];
            TestHarness.CheckTrue("叶层不 override 时命中 B 版",
                ReferenceEquals(((BoundInstanceCallExpression)midRet.Value!).Method,
                    bType.Methods.Single(m => m.Name == "tag")));

            // 基类 open 方法未被 override：裸名仍命中基类版
            var (noOver, noOverBodies) = BindUnit(
                "pub open class Base {\n" +
                "    pub open func show(): String { return \"B\" }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub func own(): String { return show() }\n" +
                "}\n");
            CheckNoErrors("无诊断（未 override 继承方法）", noOver);
            var baseType = noOver.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Base");
            var noOverRet = (BoundReturnStatement)BodyOf(noOverBodies, "own").Body.Statements[0];
            TestHarness.CheckTrue("未 override 时命中基类版",
                ReferenceEquals(((BoundInstanceCallExpression)noOverRet.Value!).Method,
                    baseType.Methods.Single(m => m.Name == "show")));

            // 重载共存回归：基类 show(i32) 重载与子类 show() 无冲突
            var (ovl, ovlBodies) = BindUnit(
                "pub open class Base {\n" +
                "    pub open func show(): String { return \"B\" }\n" +
                "    pub func show(x: i32): String { return \"Bx\" }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub override func show(): String { return \"C\" }\n" +
                "    pub func own(): String { return show() }\n" +
                "    pub func ownArg(): String { return show(1) }\n" +
                "}\n");
            CheckNoErrors("无诊断（基类重载共存）", ovl);
            TestHarness.CheckTrue("带参重载仍命中基类版本",
                BoundDescribe.Body(BodyOf(ovlBodies, "ownArg")).Contains(
                    "InstCall(show, This(Child), [Int(1,i32)], String)"));

            // 基类 static 同名方法不受遮蔽改动影响
            var (stat, statBodies) = BindUnit(
                "pub open class Base {\n" +
                "    pub static func make(): i32 { return 1 }\n" +
                "    pub open func show(): String { return \"B\" }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub override func show(): String { return \"C\" }\n" +
                "    pub func own(): i32 { return make() }\n" +
                "}\n");
            CheckNoErrors("无诊断（基类静态裸名）", stat);
            TestHarness.CheckTrue("基类静态方法裸名仍可命中",
                BoundDescribe.Body(BodyOf(statBodies, "own")).Contains("Call(make, [], i32)"));
        }
    }
}
