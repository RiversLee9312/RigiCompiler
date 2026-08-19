using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    // Wrapper 簇运行期：子类 Entity wrapper 拦截继承方法（§14.2）、
    // 多层叠加序、specific/wildcard 择一、经基类静态类型多态。
    public static partial class BilVmDispatchTests
    {
        private static void TestSubclassWrapperInterceptsInherited()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 {\n" +
                "        core.io.Console.println(\"proxied\")\n" +
                "        return inner((n + 10))\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "}\n" +
                "@Logged()\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    const c = new Child()\n" +
                "    core.io.Console.println(\"r=${c.ping(1)}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("子类 wrapper 拦截继承方法", result);
            TestHarness.Check("proxied + 改写", result.Stdout, "proxied\nr=11\n");
        }

        private static void TestSubclassWrapperWildcardInherited()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"wild\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "}\n" +
                "@Logged()\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    const c = new Child()\n" +
                "    return c.ping(1)\n" +
                "}\n");
            CheckOk("子类 wrapper 通配拦截继承方法", result);
            TestHarness.Check("wild 日志", result.Stdout, "wild\n");
            CheckI32("原样转发", result, 1);
        }

        private static void TestStackedWrappersOnInherited()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Outer {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 {\n" +
                "        core.io.Console.println(\"outer\")\n" +
                "        return inner(n)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper InnerW {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 {\n" +
                "        core.io.Console.println(\"inner\")\n" +
                "        return inner((n + 1))\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "}\n" +
                "@Outer()\n" +
                "@InnerW()\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    return new Child().ping(10)\n" +
                "}\n");
            CheckOk("多层 wrapper 拦截继承方法", result);
            TestHarness.Check("outer→inner 序", result.Stdout, "outer\ninner\n");
            CheckI32("内层改写生效", result, 11);
        }

        private static void TestBaseAndSubclassWrapperStack()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 {\n" +
                "        core.io.Console.println(\"logged\")\n" +
                "        return inner(n)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Extra {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 {\n" +
                "        core.io.Console.println(\"extra\")\n" +
                "        return inner((n + 1))\n" +
                "    }\n" +
                "}\n" +
                "@Logged()\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "}\n" +
                "@Logged()\n" +
                "@Extra()\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    return new Child().ping(3)\n" +
                "}\n");
            CheckOk("基类 wrapper 重申 + 子类追加", result);
            TestHarness.Check("Logged 外 Extra 内", result.Stdout, "logged\nextra\n");
            CheckI32("Extra 改写", result, 4);
        }

        private static void TestSpecificBeatsWildcardOnInherited()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mix {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 {\n" +
                "        core.io.Console.println(\"specific\")\n" +
                "        return inner((n + 1))\n" +
                "    }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"wild\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "    pub func other(): i32 { return 7 }\n" +
                "}\n" +
                "@Mix()\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    const c = new Child()\n" +
                "    return ((c.ping(1) * 10) + c.other())\n" +
                "}\n");
            CheckOk("specific 优先于 wildcard（继承成员）", result);
            TestHarness.Check("ping=specific other=wild", result.Stdout, "specific\nwild\n");
            CheckI32("2*10+7", result, 27);
        }

        private static void TestPolymorphicInheritedWrapper()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 {\n" +
                "        core.io.Console.println(\"via-child\")\n" +
                "        return inner((n + 5))\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "}\n" +
                "@Logged()\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var b: Base = new Child()\n" +
                "    return b.ping(1)\n" +
                "}\n");
            CheckOk("经基类静态类型仍走子类 wrapper", result);
            TestHarness.Check("实际类型 Child 的 wrapper", result.Stdout, "via-child\n");
            CheckI32("改写 6", result, 6);
        }

        private static void TestWrapperPlaceVoidRuns()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func bump() { core.io.Console.println(\"bump ${level}\") }\n" +
                "}\n" +
                "@Logged(\"INFO\")\n" +
                "pub class Svc { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    const s = new Svc()\n" +
                "    s:Logged.bump()\n" +
                "    core.io.Console.println(\"ok\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("wrapper place 语句位 void 运行", result);
            TestHarness.Check("bump + ok", result.Stdout, "bump INFO\nok\n");
        }

        private static void TestOverrideStillIntercepted()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 {\n" +
                "        core.io.Console.println(\"wrap\")\n" +
                "        return inner(n)\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub open func ping(n: i32): i32 { return n }\n" +
                "}\n" +
                "@Logged()\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    pub override func ping(n: i32): i32 { return (n + 100) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Child().ping(1)\n" +
                "}\n");
            CheckOk("子类 override 仍走 wrapper", result);
            TestHarness.Check("wrap 日志", result.Stdout, "wrap\n");
            CheckI32("override 体 101", result, 101);
        }
    }
}
