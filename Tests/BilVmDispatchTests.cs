using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// BIL VM 统一方法派发（逻辑 TypeSheet/vtable+iMap，RUNTIME §6–§9）
    /// 执行断言：带体 open 方法多态、三级继承链中间/末端 override、
    /// 接口派发（无体成员/默认方法/override 默认）、callable 协议（$$call
    /// 经实际类型 sheet）、fn(..super)（override 体 + init 体）、open getter
    /// 多态。harness 同 BilVmTests：源码 → 编译 → BIL → 运行。
    /// </summary>
    public static partial class BilVmDispatchTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("BilVmDispatch");

            TestOpenMethodVirtualDispatch();
            TestThreeLevelChainAndSuper();
            TestInterfaceDispatch();
            TestCallableProtocolDispatch();
            TestOpenGetterVirtualDispatch();
            TestNativeToStringOverrideYields();
            TestNativeToStringMultiLevelChain();
            TestPrimitiveInterpolationRegression();
            TestExplicitToStringDispatch();
            TestGenericBaseOverrideDispatch();
            TestGenericBaseInheritedMethod();
            TestGenericForwardingOverride();
            TestGenericInterfaceDispatch();
            TestGenericMultiLevelChain();
            TestGenericNestedArgumentDispatch();
            TestInitOverloadAssignability();
            TestInitRejectsUnrelatedType();
            TestSubclassWrapperInterceptsInherited();
            TestSubclassWrapperWildcardInherited();
            TestStackedWrappersOnInherited();
            TestBaseAndSubclassWrapperStack();
            TestSpecificBeatsWildcardOnInherited();
            TestPolymorphicInheritedWrapper();
            TestWrapperPlaceVoidRuns();
            TestOverrideStillIntercepted();

            return TestHarness.Summary("BilVmDispatch");
        }

        // 非泛型类继承构造泛型基类：D : B\<i32> 里 override m(x: i32) 必须
        // 按 extends 实参代入后匹配基类槽 m(x: T)——经基类静态符号的派发
        // 命中 D 的实现（代入前基类槽 key 是 m(x:#0)，D 的 key 是
        // m(x:.i32)，不匹配 → D 被追加为新槽，基类符号派发仍到基类实现）
        private static void TestGenericBaseOverrideDispatch()
        {
            var result = Run(
                "pub open class B\\<T> {\n" +
                "    pub init() {}\n" +
                "    pub open func m(x: T): T { return x }\n" +
                "}\n" +
                "pub class D : B\\<i32> {\n" +
                "    pub init() { super() }\n" +
                "    pub override func m(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: B\\<i32> = new D()\n" +
                "    var direct: B\\<i32> = new B\\<i32>()\n" +
                "    return ((b.m(1) * 10) + direct.m(1))\n" +
                "}\n");
            CheckOk("泛型基类 override 派发", result);
            CheckI32("经基类静态类型命中 D override", result, 21);
        }

        // 未 override 的泛型基类方法在 D 实例上调用：方法体内 .generic 占位
        // 沿 extends 链解析为 i32（id 原样返回入参即证明 T 解析正确）
        private static void TestGenericBaseInheritedMethod()
        {
            var result = Run(
                "pub open class B\\<T> {\n" +
                "    pub init() {}\n" +
                "    pub open func id(x: T): T { return x }\n" +
                "}\n" +
                "pub class D : B\\<i32> {\n" +
                "    pub init() { super() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: B\\<i32> = new D()\n" +
                "    return b.id(42)\n" +
                "}\n");
            CheckOk("泛型基类继承方法", result);
            CheckI32("T 解析为 i32", result, 42);
        }

        // 转发形态回归：D\<T2> : B\<T2> 里 override m(x: T2)——代入后归一化
        // 仍是 m(x:#0)，与基类槽同 key 替换（代入改动不得把碰巧正确的转发
        // 场景改坏）。stdout 鉴别派发到 D 的实现
        private static void TestGenericForwardingOverride()
        {
            var result = Run(
                "pub open class B\\<T> {\n" +
                "    pub init() {}\n" +
                "    pub open func m(x: T): T {\n" +
                "        core.io.Console.println(\"base\")\n" +
                "        return x\n" +
                "    }\n" +
                "}\n" +
                "pub class D\\<T2> : B\\<T2> {\n" +
                "    pub init() { super() }\n" +
                "    pub override func m(x: T2): T2 {\n" +
                "        core.io.Console.println(\"derived\")\n" +
                "        return x\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: B\\<i32> = new D\\<i32>()\n" +
                "    return b.m(42)\n" +
                "}\n");
            CheckOk("转发形态 override", result);
            TestHarness.Check("经基类静态类型命中 D\\<T2> override", result.Stdout,
                "derived\n");
            CheckI32("返回值", result, 42);
        }

        // 泛型接口：C : IFace\<i32> 的接口段槽按 implements 实参代入后与
        // 类侧方法同 key 直中——同名同参数个数干扰项（pick(String)，声明在
        // 前）在场时，名字兼容回退会误中它，只有代入后的精确匹配才正确
        private static void TestGenericInterfaceDispatch()
        {
            var result = Run(
                "pub interface IFace\\<T> {\n" +
                "    func pick(x: T): T\n" +
                "}\n" +
                "pub class C implements IFace\\<i32> {\n" +
                "    pub init() {}\n" +
                "    pub func pick(x: String): String { return x }\n" +
                "    pub override func pick(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f: IFace\\<i32> = new C()\n" +
                "    var c = new C()\n" +
                "    return ((f.pick(1) * 10) + c.pick(2))\n" +
                "}\n");
            CheckOk("泛型接口派发", result);
            CheckI32("接口静态符号派发命中 i32 实现", result, 23);
        }

        // 多级混合链：E : D\<i32> ← D\<X> : B\<X>——每级克隆各按直接
        // extends 实参代入（T→X 再 X→.i32），E 的 override m(x: i32) 命中
        // 根源 B 槽；未 override 的 id 沿链保持基类实现且 T 解析为 i32
        private static void TestGenericMultiLevelChain()
        {
            var result = Run(
                "pub open class B\\<T> {\n" +
                "    pub init() {}\n" +
                "    pub open func m(x: T): T { return x }\n" +
                "    pub open func id(x: T): T { return x }\n" +
                "}\n" +
                "pub open class D\\<X> : B\\<X> {\n" +
                "    pub init() { super() }\n" +
                "}\n" +
                "pub class E : D\\<i32> {\n" +
                "    pub init() { super() }\n" +
                "    pub override func m(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d: D\\<i32> = new E()\n" +
                "    var b: B\\<i32> = new E()\n" +
                "    return ((d.m(1) * 100) + ((b.id(2) * 10) + d.id(3)))\n" +
                "}\n");
            CheckOk("多级泛型链", result);
            // E.m(1)=2 → 200；B.id(2)=2 → 20；D.id(3)=3 → 合计 223
            CheckI32("链末端 override + 中间层代入传递", result, 223);
        }

        // 嵌套组合实参：D : B\<Array\<i32>> 的 override 按嵌套实参代入匹配
        private static void TestGenericNestedArgumentDispatch()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub open class B\\<T> {\n" +
                "    pub init() {}\n" +
                "    pub open func size(x: T): i32 { return 0 }\n" +
                "}\n" +
                "pub class D : B\\<Array\\<i32>> {\n" +
                "    pub init() { super() }\n" +
                "    pub override func size(x: Array\\<i32>): i32 { return 1 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: B\\<Array\\<i32>> = new D()\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    return b.size(a)\n" +
                "}\n");
            CheckOk("嵌套实参 override 派发", result);
            CheckI32("经基类静态类型命中 D override", result, 1);
        }

        // 核心：带体 open 方法经基类类型变量调用必须命中 derived override
        //（旧 FindVirtualFunction 有体直返 base fn，无多态）
        private static void TestOpenMethodVirtualDispatch()
        {
            var result = Run(
                "pub open class Base {\n" +
                "    pub init() {}\n" +
                "    pub open func greet(name: String): String { return \"base:\" + name }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub init() {}\n" +
                "    pub override func greet(name: String): String { return \"derived:\" + name }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: Base = new Derived()\n" +
                "    core.io.Console.println(b.greet(\"x\"))\n" +
                "    var direct = new Base()\n" +
                "    core.io.Console.println(direct.greet(\"x\"))\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("open 方法多态", result);
            TestHarness.Check("基类引用命中 derived override", result.Stdout,
                "derived:x\n" +
                "base:x\n");
        }

        // 三级继承链：中间（B）与末端（C）override；super 逐级解析到直接基类
        // 版本（C.m → B.m → A.m）；init 体内 super(...) 先跑基类 init
        private static void TestThreeLevelChainAndSuper()
        {
            var result = Run(
                "pub open class A {\n" +
                "    pub var tag: i32\n" +
                "    pub init() { tag = 1 }\n" +
                "    pub open func m(): i32 { return tag }\n" +
                "    pub open func n(): i32 { return 100 }\n" +
                "}\n" +
                "pub open class B : A {\n" +
                "    pub init() {\n" +
                "        super()\n" +
                "        tag = 2\n" +
                "    }\n" +
                "    pub override func m(): i32 { return (super() + 10) }\n" +
                "    pub override func n(): i32 { return 200 }\n" +
                "}\n" +
                "pub class C : B {\n" +
                "    pub init() { super() }\n" +
                "    pub override func m(): i32 { return (super() * 100) }\n" +
                "    pub override func n(): i32 { return (super() + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a: A = new C()\n" +
                "    var mid: A = new B()\n" +
                "    return ((a.m() * 10) + (a.n() + mid.m()))\n" +
                "}\n");
            CheckOk("三级链 + super", result);
            // C.m = (A.m(tag=2) + 10) * 100 = 1200；C.n = B.n + 1 = 201；
            // B.m = A.m(tag=2) + 10 = 12 → 1200*10 + 201 + 12 = 12213
            CheckI32("末端 override + super 逐级", result, 12213);

            // init super 未跑时 tag 应为 1——对照：无 super 的 B 变体
            var noSuper = Run(
                "pub open class A {\n" +
                "    pub var tag: i32\n" +
                "    pub init() { tag = 1 }\n" +
                "}\n" +
                "pub class B : A {\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new B()\n" +
                "    return b.tag\n" +
                "}\n");
            CheckOk("无 super init", noSuper);
            CheckI32("未调 super 时基类 init 不跑", noSuper, 0);
        }

        // 接口派发：无体成员 → 类实现；默认方法 → 默认体；类 override 默认 →
        // 类实现；默认方法体内调用其他接口成员按 receiver 实际类型虚派发
        private static void TestInterfaceDispatch()
        {
            var result = Run(
                "pub interface Shape {\n" +
                "    pub func area(): i32\n" +
                "    pub func describe(): String { return \"area=${area()}\" }\n" +
                "}\n" +
                "pub class Sq implements Shape {\n" +
                "    pub var s: i32\n" +
                "    pub init(n: i32) { s = n }\n" +
                "    pub override func area(): i32 { return (s * s) }\n" +
                "}\n" +
                "pub class Lbl implements Shape {\n" +
                "    pub var label: String\n" +
                "    pub init() { label = \"L\" }\n" +
                "    pub override func area(): i32 { return 7 }\n" +
                "    pub override func describe(): String { return label }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var sq: Shape = new Sq(3)\n" +
                "    var lbl: Shape = new Lbl()\n" +
                "    core.io.Console.println(sq.describe())\n" +
                "    core.io.Console.println(lbl.describe())\n" +
                "    return (sq.area() + lbl.area())\n" +
                "}\n");
            CheckOk("接口派发", result);
            // sq.describe() 走接口默认体，体内 area() 虚派发到 Sq.area = 9；
            // lbl.describe() 被类 override → L
            TestHarness.Check("默认体 + override 默认", result.Stdout,
                "area=9\n" +
                "L\n");
            CheckI32("无体成员 → 类实现", result, 16);
        }

        // callable 协议（§15.3）：lambda 赋给 core::Func 类型变量后调用；
        // 用户类 operator call 经对象调用——均按 receiver 实际类型虚派发 $$call
        private static void TestCallableProtocolDispatch()
        {
            var lambda = Run(
                "pub func main(): i32 {\n" +
                "    var f: Func\\<i32, i32> = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return f(41)\n" +
                "}\n");
            CheckOk("lambda 经 Func 变量调用", lambda);
            CheckI32("f(41)", lambda, 42);

            var user = Run(
                "pub class Adder {\n" +
                "    pub var base: i32\n" +
                "    pub init(b: i32) { base = b }\n" +
                "    pub operator call(x: i32): i32 { return (base + x) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var adder = new Adder(40)\n" +
                "    return adder(2)\n" +
                "}\n");
            CheckOk("用户 operator call", user);
            CheckI32("adder(2)", user, 42);
        }

        // open getter 经基类引用访问命中 derived getter（getter/setter 是
        // 独立多态单元，SYNTAX §9.2.1/§9.4.1）
        private static void TestOpenGetterVirtualDispatch()
        {
            var result = Run(
                "pub open class Base {\n" +
                "    pub var v: i32 {\n" +
                "        pub open get(value: _) { return value }\n" +
                "    }\n" +
                "    pub init()\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub var v: i32 {\n" +
                "        pub override get(value: _) { return 99 }\n" +
                "    }\n" +
                "    pub init() { super() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: Base = new Derived()\n" +
                "    var base: Base = new Base()\n" +
                "    return ((b.v * 10) + base.v)\n" +
                "}\n");
            CheckOk("open getter 多态", result);
            CheckI32("derived getter=99、base getter=0", result, 990);
        }

        // native hook 给 override 让位（RUNTIME §7 自然推论）：hook 只钩
        // Any/Object 上的默认 toString 本体——静态类型 Any/Object 的调用
        // （含字符串插值）在 receiver 实际类型 override 后必须执行 override
        // 文本；未 override 时仍是 canonical 默认文本（类型名，不回归）
        private static void TestNativeToStringOverrideYields()
        {
            var result = Run(
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "    pub override func toString(): String { return \"P(${x})\" }\n" +
                "}\n" +
                "pub class Plain {\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a: Any = new Point(7)\n" +
                "    core.io.Console.println(\"${a}\")\n" +
                "    var o: Object = new Point(8)\n" +
                "    core.io.Console.println(\"${o}\")\n" +
                "    var p: Any = new Plain()\n" +
                "    core.io.Console.println(\"${p}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("native toString 派发感知", result);
            TestHarness.Check("override 经 Any/Object 静态类型命中 + 默认不回归", result.Stdout,
                "P(7)\n" +
                "P(8)\n" +
                "Plain\n");
        }

        // 多级继承：中间类 override、叶子不 override → 叶子经 Any 静态类型
        // 走中间类版本；全链不 override → canonical 默认文本
        private static void TestNativeToStringMultiLevelChain()
        {
            var result = Run(
                "pub open class A {\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub open class B : A {\n" +
                "    pub init() { super() }\n" +
                "    pub override func toString(): String { return \"B!\" }\n" +
                "}\n" +
                "pub class C : B {\n" +
                "    pub init() { super() }\n" +
                "}\n" +
                "pub class D : A {\n" +
                "    pub init() { super() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var leaf: Any = new C()\n" +
                "    core.io.Console.println(\"${leaf}\")\n" +
                "    var plain: Any = new D()\n" +
                "    core.io.Console.println(\"${plain}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("native toString 多级继承", result);
            TestHarness.Check("叶子走中间类 override、未 override 走默认", result.Stdout,
                "B!\n" +
                "D\n");
        }

        // 基元插值不回归：i32 等内建精确类型 receiver 无 sheet，仍走 hook
        private static void TestPrimitiveInterpolationRegression()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 41\n" +
                "    var flag = true\n" +
                "    core.io.Console.println(\"n=${n} flag=${flag}\")\n" +
                "    return (n + 1)\n" +
                "}\n");
            CheckOk("基元插值回归", result);
            TestHarness.Check("i32/bool 标准文本", result.Stdout, "n=41 flag=true\n");
            CheckI32("返回值", result, 42);
        }

        // 显式 toString 端到端（SYNTAX §3.8 修订后）：Object 静态类型的
        // o.toString() 经 override 遮蔽唯一解析（Any/Object 双候选歧义消除）；
        // Any 静态类型同理；未 override 走合成默认体（canonical 名），已
        // override 经虚派发执行用户实现；i32 显式接收者走 Any 承诺 +
        // 装箱 cast + any_to_string hook 全链路
        private static void TestExplicitToStringDispatch()
        {
            var result = Run(
                "pub class Point {\n" +
                "    pub init() {}\n" +
                "    pub override func toString(): String { return \"PT\" }\n" +
                "}\n" +
                "pub class Plain {\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var o: Object = new Point()\n" +
                "    core.io.Console.println(o.toString())\n" +
                "    var a: Any = new Point()\n" +
                "    core.io.Console.println(a.toString())\n" +
                "    var p: Object = new Plain()\n" +
                "    core.io.Console.println(p.toString())\n" +
                "    var n: i32 = 41\n" +
                "    core.io.Console.println(n.toString())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("显式 toString 派发", result);
            TestHarness.Check("Object/Any 静态类型 + 基元接收者", result.Stdout,
                "PT\n" +
                "PT\n" +
                "Plain\n" +
                "41\n");
        }

        private static BilVmResult Run(string source)
        {
            try
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
                TestHarness.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                        d => $"{d.Phase}: {d.Message}")));
                if (unit.Diagnostics.HasErrors)
                {
                    return new BilVmResult("", "", null,
                        new VmException("编译失败，跳过 VM"));
                }
                return BilVm.Run(module);
            }
            catch (Exception exception)
            {
                TestHarness.CheckTrue("全管线无诊断", false, exception.ToString());
                return new BilVmResult("", "", null,
                    new VmException(exception.Message, inner: exception));
            }
        }

        private static void CheckOk(string label, BilVmResult result)
        {
            TestHarness.CheckTrue(label + " 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
        }

        private static void CheckI32(string label, BilVmResult result, int expected)
        {
            TestHarness.CheckTrue(label,
                result.ReturnValue is VmI32 n && n.Value == expected,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }
    }
}
