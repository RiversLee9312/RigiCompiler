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
    public static class BilVmDispatchTests
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

            return TestHarness.Summary("BilVmDispatch");
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
