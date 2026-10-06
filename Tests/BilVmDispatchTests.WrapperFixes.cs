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
            CaseAssertions.Check("proxied + 改写", result.Stdout, "proxied\nr=11\n");
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
            CaseAssertions.Check("wild 日志", result.Stdout, "wild\n");
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
            CaseAssertions.Check("outer→inner 序", result.Stdout, "outer\ninner\n");
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
            CaseAssertions.Check("Logged 外 Extra 内", result.Stdout, "logged\nextra\n");
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
            CaseAssertions.Check("ping=specific other=wild", result.Stdout, "specific\nwild\n");
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
            CaseAssertions.Check("实际类型 Child 的 wrapper", result.Stdout, "via-child\n");
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
            CaseAssertions.Check("bump + ok", result.Stdout, "bump INFO\nok\n");
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
            CaseAssertions.Check("wrap 日志", result.Stdout, "wrap\n");
            CheckI32("override 体 101", result, 101);
        }

        // bug O1：虚/接口派发丢 Method wrapper——override 已按 §14.9 重复
        // 声明 @Trace，但经基类静态类型调用时 invoke 带静态符号 Base$work，
        // 实例上只有实现侧键 Child$work；修复 = 收集 wrapper 链前先按
        // receiver 实际类型虚派发取实现槽符号（RUNTIME §7 + §14.9）
        private static void TestMethodWrapperViaBaseStaticType()
        {
            var result = Run(
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
                "pub func main(): i32 {\n" +
                "    const b: Base = new Child()\n" +
                "    return b.work()\n" +
                "}\n");
            CheckOk("bug O1 基类静态类型 + 子类 @Trace override", result);
            CaseAssertions.Check("经基类引用也有 trace", result.Stdout, "trace\n");
            CheckI32("override 体 2", result, 2);
        }

        // bug O1 同构：接口静态类型调用（Work$work → Job$work）
        private static void TestMethodWrapperViaInterfaceStaticType()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "pub interface Work {\n" +
                "    func work(): i32\n" +
                "}\n" +
                "pub class Job implements Work {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 3 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const w: Work = new Job()\n" +
                "    return w.work()\n" +
                "}\n");
            CheckOk("bug O1 接口静态类型 + 实现 @Trace", result);
            CaseAssertions.Check("经接口引用也有 trace", result.Stdout, "trace\n");
            CheckI32("实现体 3", result, 3);
        }

        // bug O1 多层链：经中间层静态类型引用末端实例，末端 override 的
        // wrapper 仍命中（Mid$work → Leaf$work）
        private static void TestMethodWrapperViaMidChainStaticType()
        {
            var result = Run(
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
                "    const m: Mid = new Leaf()\n" +
                "    return m.work()\n" +
                "}\n");
            CheckOk("bug O1 多层链中间层静态类型", result);
            CaseAssertions.Check("末端 override 的 trace", result.Stdout, "trace\n");
            CheckI32("末端体 3", result, 3);
        }

        // bug W3：虚派发下 Method wrapper wildcard 的 .name 必须是实际执行
        // 的实现槽符号（Child$work），不是调用点静态符号（Base$work）。
        private static void TestMethodWrapperWildcardNameViaBaseStaticType()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(.name)\n" +
                "        return inner(.name, args)\n" +
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
                "pub func main(): i32 {\n" +
                "    const b: Base = new Child()\n" +
                "    return b.work()\n" +
                "}\n");
            CheckOk("虚调用 .name = 实现槽 Child$work", result);
            CaseAssertions.Check(".name 为 Child$work 而非 Base$work",
                result.Stdout, "Child$work()@.i32\n");
            CheckI32("override 体 2", result, 2);
        }

        // bug W3 同构：接口静态类型调用，.name = Job$work 而非 Work$work
        private static void TestMethodWrapperWildcardNameViaInterface()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(.name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub interface Work {\n" +
                "    func work(): i32\n" +
                "}\n" +
                "pub class Job implements Work {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 3 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const w: Work = new Job()\n" +
                "    return w.work()\n" +
                "}\n");
            CheckOk("接口调用 .name = 实现槽 Job$work", result);
            CaseAssertions.Check(".name 为 Job$work 而非 Work$work",
                result.Stdout, "Job$work()@.i32\n");
            CheckI32("实现体 3", result, 3);
        }

        // 非虚回归：无继承时 .name 仍是声明侧完整 BIL 方法符号
        private static void TestMethodWrapperWildcardNameNonVirtual()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(.name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Service().fetch(41)\n" +
                "}\n");
            CheckOk("非虚 .name = 声明符号", result);
            CaseAssertions.Check(".name 为 Service$fetch",
                result.Stdout, "Service$fetch(x:.i32)@.i32\n");
            CheckI32("fetch 42", result, 42);
        }

        // 未 override：实际执行的仍是基类实现，.name = Base$work 而非 Child$work
        private static void TestMethodWrapperWildcardNameInheritedNoOverride()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(.name)\n" +
                "        return inner(.name, args)\n" +
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
                "    const b: Base = new Child()\n" +
                "    return b.work()\n" +
                "}\n");
            CheckOk("未 override .name = 基类实现槽", result);
            CaseAssertions.Check(".name 为 Base$work（实际执行体）",
                result.Stdout, "Base$work()@.i32\n");
            CheckI32("基类体 1", result, 1);
        }

        // Entity wildcard 的 symbol 与 Method wrapper .name 同口径：
        // 经基类静态类型调用 override 时为 Child$ping，不是 Base$ping
        private static void TestEntityWildcardSymbolViaBaseStaticType()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub open func ping(): i32 { return 1 }\n" +
                "}\n" +
                "@Logged()\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    pub override func ping(): i32 { return 2 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b: Base = new Child()\n" +
                "    return b.ping()\n" +
                "}\n");
            CheckOk("Entity wildcard symbol = 实现槽 Child$ping", result);
            CaseAssertions.Check("symbol 为 Child$ping 而非 Base$ping",
                result.Stdout, "Child$ping()@.i32\n");
            CheckI32("override 体 2", result, 2);
        }

        // 回归：override 未声明 wrapper 时经基类静态类型调用不绕链，
        // 虚派发仍到 override 体（修复不得给无 wrapper 方法凭空造链）
        private static void TestNoWrapperViaBaseStaticTypeRegression()
        {
            var result = Run(
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    pub override func work(): i32 { return 2 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b: Base = new Child()\n" +
                "    return b.work()\n" +
                "}\n");
            CheckOk("无 wrapper 经基类静态类型回归", result);
            CaseAssertions.Check("无 trace", result.Stdout, "");
            CheckI32("override 体 2", result, 2);
        }

        // super 路径保持绕过全部 wrapper（ResolveSuper 直接压帧、非虚）：
        // override 体内 super() 不得再触发基类方法键上的 wrapper
        private static void TestSuperBypassesMethodWrapper()
        {
            var result = Run(
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
                "}\n");
            CheckOk("super 绕过 Method wrapper", result);
            CaseAssertions.Check("仅外层一次 trace", result.Stdout, "trace\n");
            CheckI32("super 1 + 10", result, 11);
        }

        // bug O2（§9.7 修订）：子类未 override 的基类方法，其 Method
        // wrapper 由实际类型的 ..init.wrapper 经继承闭包缝合安装——
        // new Child().work() 有 trace
        private static void TestInheritedMethodWrapperInstalledOnChild()
        {
            var result = Run(
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
                "    return new Child().work()\n" +
                "}\n");
            CheckOk("bug O2 子类实例装基类方法 wrapper", result);
            CaseAssertions.Check("未 override 也 trace", result.Stdout, "trace\n");
            CheckI32("基类体 1", result, 1);
        }

        // bug O7（§9.7/§14.2 修订）：..init.wrapper 先于一切 init 体执行
        // 且覆盖继承闭包——基类 init 体读取字段时，（重申到子类的）Entity
        // wrapper 已安装，读命中 .proxy.get.* 链
        private static void TestBaseInitReadsThroughInheritedEntityWrapper()
        {
            var result = Run(
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
                "}\n");
            CheckOk("bug O7 基类 init 读命中继承 wrapper", result);
            CaseAssertions.Check("init 读 + 主调读各 audit 一次",
                result.Stdout, "audit\naudit\n");
            CheckI32("字段初值 10 先于 init 体（10+1=11）", result, 11);
        }

        // bug O7 完整形态（P17 收尾）：Entity wrapper 同时带 .proxy.*/get.*/set.*
        // 时，构造期 setter 调用被 .proxy.* 拦截后经 inner 重路由为 Set 链——
        // 链起自方法调用（FieldSymbol 原本为空），链末须从访问器符号反查字段
        // 并解胖值包取写入值；getter 调用重路由为 Get 链，链末调 getter 本体
        //（绕过派发链防再入成环）
        private static void TestAccessorCallRerouteThroughEntityWildcard()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"[Audit] ${symbol}\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"[Audit] ${symbol}\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"[Audit] ${symbol}\")\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub open class Entity {\n" +
                "    pub var name: String = \"Ada\"\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { }\n" +
                "    } = 10\n" +
                "    pub init() {\n" +
                "        core.io.Console.println(\"Entity.init ${name} hp=${this.hp}\")\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Hero : Entity {\n" +
                "    pub init() {\n" +
                "        super()\n" +
                "        core.io.Console.println(\"Hero.init\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const h = new Hero()\n" +
                "    core.io.Console.println(\"ok ${h.name}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("访问器调用经实体 wildcard 重路由不崩", result);
            CaseAssertions.Check("完整输出（初值经 setter、init 读经 getter+get 链）",
                result.Stdout,
                "[Audit] Entity$..init.field.name()@.void\n" +
                "[Audit] Entity$..init.field.hp()@.void\n" +
                "[Audit] Entity$.set.hp@.i32\n" +
                "[Audit] Entity#name@.string\n" +
                "[Audit] Entity$.get.hp@.i32\n" +
                "[Audit] Entity#hp@.i32\n" +
                "Entity.init Ada hp=10\n" +
                "Hero.init\n" +
                "[Audit] Entity#name@.string\n" +
                "ok Ada\n");
            CheckI32("main 返回 0（hp=10 见 stdout）", result, 0);
        }
    }
}
