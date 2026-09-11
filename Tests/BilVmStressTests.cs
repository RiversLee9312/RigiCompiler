using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// BIL VM 高强度行为测试（对 BilVmTests 的补盲）：源码 → 全管线 → VM。
    /// 聚焦 wrapper 生命周期全家（有参 init 透传 / 值 get-set 链 / Entity
    /// 方法·getter·setter·operator / 双层嵌套 / call??? 路由与降级 / proxy
    /// 状态持久 / place 读写 / 复合赋值 / rich struct 宿主）、wrapper × 其他
    /// 特性交叉（String 字段、Array 字段、proxy 体内 throw/try、static 字段、
    /// lambda 捕获 wrapped 局部、泛型宿主）、以及文档行为抽查（enum switch
    /// 表达式、字符串插值各标量、type.with、异常穿越多层 finally、using 逆序）。
    /// </summary>
    public static class BilVmStressTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        private static ParallelSuiteRunner.SuiteSpec Spec => new(
            "BilVmStress", Cases, sectionTitle: "BilVmStress");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestEntityWrapperInitArgsMulti", TestEntityWrapperInitArgsMulti),
            ("TestEntityWrapperInitArgString", TestEntityWrapperInitArgString),
            ("TestEntityWrapperInitArgExpression", TestEntityWrapperInitArgExpression),
            ("TestValueWrapperStringField", TestValueWrapperStringField),
            ("TestValueWrapperCompoundAssignment", TestValueWrapperCompoundAssignment),
            ("TestValueWrapperLocalInitGoesThroughSet", TestValueWrapperLocalInitGoesThroughSet),
            ("TestValueWrapperPlaceWrite", TestValueWrapperPlaceWrite),
            ("TestValueWrapperArrayField", TestValueWrapperArrayField),
            ("TestRichStructEntityWrapper", TestRichStructEntityWrapper),
            ("TestRichStructFieldValueWrapper", TestRichStructFieldValueWrapper),
            ("TestEntityWrapperGenericHost", TestEntityWrapperGenericHost),
            ("TestEntityWrapperTwoLayersWithArgs", TestEntityWrapperTwoLayersWithArgs),
            ("TestProxyBodyThrowPropagates", TestProxyBodyThrowPropagates),
            ("TestProxyBodyTryCatchInternal", TestProxyBodyTryCatchInternal),
            ("TestWrappedLocalCapturedByLambda", TestWrappedLocalCapturedByLambda),
            ("TestEnumSwitchExpression", TestEnumSwitchExpression),
            ("TestStringInterpolationScalars", TestStringInterpolationScalars),
            ("TestThrowThroughNestedFinally", TestThrowThroughNestedFinally),
            ("TestTypeWithCheck", TestTypeWithCheck),
            ("TestEntityWrapperGetterSetterBothOrder", TestEntityWrapperGetterSetterBothOrder),
            ("TestEntityGetterWildcardProxy", TestEntityGetterWildcardProxy),
            ("TestEntitySetterWildcardProxy", TestEntitySetterWildcardProxy),
            ("TestEntityProxySelfSource", TestEntityProxySelfSource),
            ("TestEntityWrapperEnumField", TestEntityWrapperEnumField),
            ("TestEnumExplicitDiscriminant", TestEnumExplicitDiscriminant),
            ("TestGenericInstanceMethodCall", TestGenericInstanceMethodCall),
            ("TestStaticWrappedFieldProxyChain", TestStaticWrappedFieldProxyChain),
            ("TestStaticWrappedFieldWithUserAccessors", TestStaticWrappedFieldWithUserAccessors),
            ("TestStaticMethodAndFieldSharedCompanion", TestStaticMethodAndFieldSharedCompanion),
            ("TestNestedClassStaticWrappedField", TestNestedClassStaticWrappedField),
            ("TestStaticWrappedFieldInitializer", TestStaticWrappedFieldInitializer),
            ("TestGlobalWrappedFieldProxyChain", TestGlobalWrappedFieldProxyChain),
            ("TestGlobalWrappedFieldWithUserAccessors", TestGlobalWrappedFieldWithUserAccessors),
            ("TestGlobalWrappedFieldInitializerBeforeMain", TestGlobalWrappedFieldInitializerBeforeMain),
            ("TestGlobalConstWrappedField", TestGlobalConstWrappedField),
            ("TestSingletonInitOrderIndependence", TestSingletonInitOrderIndependence),
            ("TestSingletonCycleDetection", TestSingletonCycleDetection),
            ("TestCoroutineHandoffRaceRegression", TestCoroutineHandoffRaceRegression),
            ("TestSharedCounterConcurrentTasks", TestSharedCounterConcurrentTasks),
            ("TestMessagePumpDeliversAllMessages", TestMessagePumpDeliversAllMessages),
        };

        // ===== 辅助 =====

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

        private static void CheckBool(string label, BilVmResult result, bool expected)
        {
            TestHarness.CheckTrue(label,
                result.ReturnValue is VmBool flag && flag.Value == expected,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // ===== wrapper 生命周期 =====

        // Entity wrapper 多参数 init 透传（i32 + String + bool）经 ..init.wrapper
        // 参数 → new.wrapped 前缀，验证每个字段都落到 wrapper 实例上
        private static void TestEntityWrapperInitArgsMulti()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tagged {\n" +
                "    pub var id: i32\n" +
                "    pub var label: String\n" +
                "    pub var enabled: bool\n" +
                "    pub init(i: i32, l: String, e: bool) {\n" +
                "        id = i\n" +
                "        label = l\n" +
                "        enabled = e\n" +
                "    }\n" +
                "}\n" +
                "@Tagged(42, \"hi\", true)\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var ok = ((s:Tagged.id == 42) and (s:Tagged.label == \"hi\"))" +
                " and s:Tagged.enabled\n" +
                "    if (ok) { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("Entity 多参 init", result);
            CheckI32("id/label/enabled 全透传", result, 1);
        }

        // String init 实参（字符串字面量）透传
        private static void TestEntityWrapperInitArgString()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Leveled {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Leveled(\"DEBUG\")\n" +
                "pub class Svc { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Svc()\n" +
                "    if (s:Leveled.level == \"DEBUG\") { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("Entity String init 实参", result);
            CheckI32("level == DEBUG", result, 1);
        }

        // 类型级 init 实参为表达式：在 ..init.wrapper 体内求值（构造期）
        private static void TestEntityWrapperInitArgExpression()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Step {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "@Step((2 + 3))\n" +
                "pub class Svc { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Svc()\n" +
                "    var b = new Svc()\n" +
                "    return ((a:Step.n * 10) + b:Step.n)\n" +
                "}\n");
            CheckOk("Entity 表达式 init 实参", result);
            CheckI32("(2+3) 每个 new 都求值为 5 → 55", result, 55);
        }

        // Value wrapper 修饰 String 字段：get/set 绕 proxy 且 String 值正确
        private static void TestValueWrapperStringField()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Bracketed {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((\"[\" + (value as String)) + \"]\") as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        inner((((\"<\" + (value as String)) + \">\") as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Bracketed\n" +
                "    var name: String = \"a\"\n" +
                "    name = \"b\"\n" +
                "    if (name == \"[<b>]\") { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("Value wrapper String 字段", result);
            CheckI32("String get/set 经 proxy", result, 1);
        }

        // 复合赋值（value 本体 +=）：读经 get proxy、写经 set proxy
        private static void TestValueWrapperCompoundAssignment()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Doubler {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        inner((((value as i32) * 2) as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Doubler\n" +
                "    var x: i32 = 1\n" +
                "    x += 2\n" +
                "    x += 3\n" +
                "    return x\n" +
                "}\n");
            CheckOk("Value wrapper 复合赋值", result);
            // init 1 经 set 翻倍 2 → +=2：set(2+2=4) 翻倍 8 → +=3：set(8+3=11) 翻倍 22
            CheckI32("init set*2=2 → (2+2)*2=8 → (8+3)*2=22", result, 22);
        }

        // 局部 Value wrapper 声明初始化器经 proxy.set（不得直接
        // new cell(value) 绕过 set 链）
        private static void TestValueWrapperLocalInitGoesThroughSet()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Log {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"[get] ${value}\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"[set] ${value}\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Log()\n" +
                "    var hp: i32 = 50\n" +
                "    hp = 42\n" +
                "    core.io.Console.println(\"${hp}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("局部 Value wrapper 声明初始化经 set", result);
            TestHarness.Check("初始化+赋值+读取 stdout", result.Stdout,
                "[set] 50\n" +
                "[set] 42\n" +
                "[get] 42\n" +
                "42\n");
        }

        // place 写：wrapped 局部的 wrapper 字段经 set.wrapper.field 原地写
        private static void TestValueWrapperPlaceWrite()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    health:Clamped.min = 7\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            CheckOk("局部 wrapper place 写", result);
            CheckI32("place 写读 wrapper 字段", result, 7);
        }

        // Value wrapper 修饰 Array 字段：get/set 绕 proxy 且 Array 值正确
        private static void TestValueWrapperArrayField()
        {
            var result = Run(
                "import core.collections.*\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Noop {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Noop\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 4\n" +
                "    a[1] = 20\n" +
                "    a[2] = 18\n" +
                "    return (((a[0] if? 0) + (a[1] if? 0)) + (a[2] if? 0))\n" +
                "}\n");
            CheckOk("Value wrapper Array 字段", result);
            CheckI32("Array 内容经 proxy 保留", result, 42);
        }

        // Entity wrapper 修饰 rich struct：方法 proxy 生效
        private static void TestRichStructEntityWrapper()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.peek(): i32 {\n" +
                "        return (inner() + 100)\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub rich struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init() { x = 0 }\n" +
                "    pub func peek(): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var v = new Vec()\n" +
                "    return v.peek()\n" +
                "}\n");
            CheckOk("rich struct Entity wrapper", result);
            CheckI32("proxy.peek 返回 inner()+100", result, 100);
        }

        // rich struct 字段挂 Value wrapper（宿主内嵌）
        private static void TestRichStructFieldValueWrapper()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper PlusOne {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((value as i32) + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub rich struct Box {\n" +
                "    @PlusOne\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    b.n = 40\n" +
                "    return b.n\n" +
                "}\n");
            CheckOk("rich struct 字段 Value wrapper", result);
            CheckI32("字段读经 proxy +1", result, 41);
        }

        // 泛型宿主类上的 Entity wrapper：方法派发经类型参数实例化命中
        private static void TestEntityWrapperGenericHost()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.fetch(id: i32): i32 {\n" +
                "        return (inner(id) + 1)\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Store\\<T> {\n" +
                "    pub var item: T?\n" +
                "    pub init()\n" +
                "    pub func fetch(id: i32): i32 { return id }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Store\\<i32>()\n" +
                "    return s.fetch(41)\n" +
                "}\n");
            CheckOk("泛型宿主 Entity wrapper", result);
            CheckI32("proxy.fetch 返回 id+1", result, 42);
        }

        // 双层 Entity wrapper 各带 init 实参，顺序 outer→inner
        private static void TestEntityWrapperTwoLayersWithArgs()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WOuter {\n" +
                "    pub var tag: String\n" +
                "    pub init(_ -> tag)\n" +
                "    operator .proxy.say(m: String): String {\n" +
                "        core.io.Console.println(\"outer:\" + tag)\n" +
                "        return inner(m)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WInner {\n" +
                "    pub var tag: String\n" +
                "    pub init(_ -> tag)\n" +
                "    operator .proxy.say(m: String): String {\n" +
                "        core.io.Console.println(\"inner:\" + tag)\n" +
                "        return inner(m)\n" +
                "    }\n" +
                "}\n" +
                "@WOuter(\"O\")\n" +
                "@WInner(\"I\")\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func say(m: String): String { return m }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    core.io.Console.println(s.say(\"x\"))\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("双层 Entity wrapper 带实参", result);
            TestHarness.Check("双层 outer→inner 顺序 stdout", result.Stdout,
                "outer:O\n" +
                "inner:I\n" +
                "x\n");
        }

        // proxy 体内 throw：异常沿派发链向上穿到调用方
        private static void TestProxyBodyThrowPropagates()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Guard {\n" +
                "    pub init()\n" +
                "    operator .proxy.fail(): i32 {\n" +
                "        throw new core.RuntimeException(\"guarded\")\n" +
                "    }\n" +
                "}\n" +
                "@Guard\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fail(): i32 { return 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    try {\n" +
                "        s.fail()\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        return 7\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("proxy 体内 throw", result);
            CheckI32("异常穿链且被调用方 catch", result, 7);
        }

        // proxy 体内 try/catch 自理异常：不影响派发链
        private static void TestProxyBodyTryCatchInternal()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Safe {\n" +
                "    pub init()\n" +
                "    operator .proxy.fetch(id: i32): i32 {\n" +
                "        try {\n" +
                "            return inner(id)\n" +
                "        } catch (e: core.RuntimeException) {\n" +
                "            return (-1)\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "@Safe\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(id: i32): i32 {\n" +
                "        throw new core.RuntimeException(\"boom\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(1)\n" +
                "}\n");
            CheckOk("proxy 体内 try/catch 自理", result);
            CheckI32("catch 返回 -1", result, -1);
        }

        // lambda 捕获 wrapped 局部：读取经 cell → get proxy
        //（var + wrapper 需同时实现 .proxy.set——§14.3 只读适用性检查前移
        // 后 get-only 修饰 var 是编译错误；直通 set 保持读取经 get proxy 的
        // 被测语义不变）
        private static void TestWrappedLocalCapturedByLambda()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Inc {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((value as i32) + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Inc\n" +
                "    var x: i32 = 10\n" +
                "    var fn = func{(): i32 -> x}\n" +
                "    return fn()\n" +
                "}\n");
            CheckOk("lambda 捕获 wrapped 局部", result);
            CheckI32("捕获读取经 proxy +1", result, 11);
        }

        // ===== 文档行为抽查 =====

        // enum struct 的 switch 表达式（_ is .Case pattern + default）
        private static void TestEnumSwitchExpression()
        {
            var result = Run(
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    priv init(_ -> degrees)\n" +
                "}[\n" +
                "    North(0), South(180), East(90), West(270)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const d: Direction = .East\n" +
                "    var r = switch(d) {\n" +
                "        (_ is .North) -> { 1 }\n" +
                "        (_ is .East) -> { 2 }\n" +
                "        default -> { 9 }\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("enum switch 表达式", result);
            CheckI32(".East → 2", result, 2);
        }

        // 字符串插值各标量类型经 Any.toString 原生面
        private static void TestStringInterpolationScalars()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var b: bool = true\n" +
                "    var d: double = 1.5\n" +
                "    var f: float = 2.5f\n" +
                "    var c: char = 'Z'\n" +
                "    var l: i64 = (7 as i64)\n" +
                "    var u: u8 = (3 as u8)\n" +
                "    core.io.Console.println(\"b=${b} d=${d} f=${f} c=${c} l=${l} u=${u}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("插值各标量", result);
            TestHarness.Check("插值标量 stdout", result.Stdout,
                "b=true d=1.5 f=2.5 c=Z l=7 u=3\n");
        }

        // 异常穿越多层嵌套 finally：每层 finally 逆序执行
        private static void TestThrowThroughNestedFinally()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.RuntimeException(\"x\")\n" +
                "        } finally(_) {\n" +
                "            core.io.Console.println(\"inner\")\n" +
                "        }\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"outer\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("嵌套 finally 异常未吞",
                result.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("RuntimeException"),
                result.Exception?.ToString() ?? "<null>");
            TestHarness.Check("嵌套 finally 顺序", result.Stdout,
                "inner\n" +
                "outer\n");
        }

        // type.with：运行时判断宿主类型 wrapper 链是否含目标 wrapper
        private static void TestTypeWithCheck()
        {
            var result = Run(
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
                "    var a = (t with Mark)\n" +
                "    var b = (p with Mark)\n" +
                "    if ((a and (not b))) { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("type.with 检查", result);
            CheckI32("Tagged with Mark，Plain 不含", result, 1);
        }

        // Entity 字段同时挂 get/set proxy：读写各绕一次（顺序读 g 写 s）
        private static void TestEntityWrapperGetterSetterBothOrder()
        {
            var result = Run(
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
                "    return 0\n" +
                "}\n");
            CheckOk("Entity get/set proxy 顺序", result);
            TestHarness.Check("写 s、读 g", result.Stdout, "s\ng\n");
        }

        // Entity 字段读命中唯一 wildcard .proxy.get.*（无 specific 时），
        // symbol 为字段 canonical symbol
        private static void TestEntityGetterWildcardProxy()
        {
            var result = Run(
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
                "}\n");
            CheckOk("Entity getter wildcard proxy", result);
            TestHarness.CheckTrue("读命中 .proxy.get.* 且带 symbol",
                result.Stdout.Contains("get:Service#name") == true,
                result.Stdout);
        }

        // Entity 字段写命中唯一 wildcard .proxy.set.*，inner 落原始字段写
        private static void TestEntitySetterWildcardProxy()
        {
            var result = Run(
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
                "}\n");
            CheckOk("Entity setter wildcard proxy", result);
            TestHarness.CheckTrue("写命中 .proxy.set.* 且带 symbol",
                result.Stdout.Contains("set:Service#name") == true,
                result.Stdout);
            CheckI32("原始字段写生效", result, 1);
        }

        // Entity proxy 体内 self：产出宿主实例并经 is 判别（源码路径）
        private static void TestEntityProxySelfSource()
        {
            var result = Run(
                "pub interface IHasId { }\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.peek(): i32 {\n" +
                "        var host = self\n" +
                "        if (host is IHasId) { return 42 } else { return 0 }\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service implements IHasId {\n" +
                "    pub init()\n" +
                "    pub func peek(): i32 { return 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.peek()\n" +
                "}\n");
            CheckOk("Entity proxy self（源码）", result);
            CheckI32("self 产出宿主（is IHasId）", result, 42);
        }

        // Entity wrapper 字段为 enum struct：init 透传 + proxy 体内 is .Case
        private static void TestEntityWrapperEnumField()
        {
            var result = Run(
                "pub enum struct Level { }[Low, High]\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper L {\n" +
                "    pub var level: Level\n" +
                "    pub init(l: Level) { level = l }\n" +
                "    operator .proxy.peek(): i32 {\n" +
                "        if (level is .High) { return 2 } else { return 1 }\n" +
                "    }\n" +
                "}\n" +
                "@L(.High)\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func peek(): i32 { return 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.peek()\n" +
                "}\n");
            CheckOk("Entity wrapper enum 字段", result);
            CheckI32("enum init 透传 + proxy 内 is .Case", result, 2);
        }

        // enum struct 显式判别值 -> 与 is .Case 判别
        private static void TestEnumExplicitDiscriminant()
        {
            var result = Run(
                "pub enum struct Code { }[\n" +
                "    First -> 0,\n" +
                "    Second -> 2,\n" +
                "    Third -> 1\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const a: Code = .Third\n" +
                "    const b: Code = .Second\n" +
                "    var r = 0\n" +
                "    if (a is .Third) { r = (r + 1) }\n" +
                "    if (b is .Second) { r = (r + 1) }\n" +
                "    if ((not (a is .First))) { r = (r + 1) }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("enum 显式判别值 is", result);
            CheckI32("Third/Second/非First 各命中", result, 3);
        }

        // 回归：泛型类实例方法调用（receiver 擦除 cast Store<.i32> → Store）
        private static void TestGenericInstanceMethodCall()
        {
            var result = Run(
                "pub class Store\\<T> {\n" +
                "    pub var item: T?\n" +
                "    pub init()\n" +
                "    pub func fetch(id: i32): i32 { return (id + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Store\\<i32>()\n" +
                "    return s.fetch(41)\n" +
                "}\n");
            CheckOk("泛型实例方法调用", result);
            CheckI32("Store<.i32>.fetch(41)", result, 42);
        }

        // ===== 静态问题统一 companion（§8.7）=====

        // Value wrapper 修饰静态字段：写 21 读回经 proxy 链（此前 cell 未初始化
        // 崩「找不到 fn 定义 core::Cell$setValue」）
        private static void TestStaticWrappedFieldProxyChain()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init() { min = 0\n max = 100 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        if ((v > max)) { v = max }\n" +
                "        if ((v < min)) { v = min }\n" +
                "        inner((v as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Holder.counter = 21\n" +
                "    return Holder.counter\n" +
                "}\n");
            CheckOk("静态 Value wrapper 字段 proxy 链", result);
            CheckI32("写 21 读回 21", result, 21);
        }

        // 静态 wrapped 字段 + 用户 get/set：访问器体进 cell getValue/setValue，
        // wrapper 链外置（写：wrapper.set → user.set；读：user.get → wrapper.get）
        private static void TestStaticWrappedFieldWithUserAccessors()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"wrapper.get\")\n" +
                "        var v = (value as i32)\n" +
                "        return ((v + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"wrapper.set\")\n" +
                "        var v = (value as i32)\n" +
                "        inner(((v + 10) as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @Shift()\n" +
                "    pub static var x: i32 {\n" +
                "        pub get(value: _) {\n" +
                "            core.io.Console.println(\"user.get\")\n" +
                "            return value * 2\n" +
                "        }\n" +
                "        pub set(value: _) {\n" +
                "            core.io.Console.println(\"user.set\")\n" +
                "            value = value * 2\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Holder.x = 5\n" +
                "    return Holder.x\n" +
                "}\n");
            CheckOk("静态 wrapper+访问器外置序", result);
            TestHarness.Check("静态写读打印序", result.Stdout,
                "wrapper.set\nuser.set\nuser.get\nwrapper.get\n");
            CheckI32("静态 5→15→30 读 60→61", result, 61);
        }

        // 同一类一个静态 Method wrapper 方法 + 一个静态 Value wrapper 字段：
        // BIL 只一个 companion，VM 行为都正确
        private static void TestStaticMethodAndFieldSharedCompanion()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub class Calc {\n" +
                "    @SClamp\n" +
                "    pub static var base: i32 = 10\n" +
                "    @Timed\n" +
                "    pub static func total(x: i32): i32 { return (Calc.base + x) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Calc.base = 30\n" +
                "    return Calc.total(12)\n" +
                "}\n");
            TestHarness.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            TestHarness.CheckTrue("一个 companion 两个成员",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Count(t => t.Symbol.EndsWith("..companion")) == 1
                && module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Single(t => t.Symbol.EndsWith("..companion"))
                    .Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(d => d.Symbol.Contains("$total("))
                && module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Single(t => t.Symbol.EndsWith("..companion"))
                    .Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(d => d.Symbol.Contains("#base@")));
            var result = BilVm.Run(module);
            CheckOk("静态 Method + Value wrapper 共享 companion", result);
            CheckI32("Calc.total(12) = 30 + 12", result, 42);
        }

        // 嵌套类的静态 wrapped 字段 → 嵌套类自己的 companion
        private static void TestNestedClassStaticWrappedField()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Outer {\n" +
                "    pub class Inner {\n" +
                "        @SClamp\n" +
                "        pub static var counter: i32 = 0\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Outer.Inner.counter = 7\n" +
                "    return Outer.Inner.counter\n" +
                "}\n");
            CheckOk("嵌套类静态 wrapped 字段", result);
            CheckI32("Outer.Inner.counter 写读", result, 7);
        }

        // 静态字段带初始化表达式：companion init 求值并经 proxy 读回
        private static void TestStaticWrappedFieldInitializer()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var answer: i32 = (40 + 2)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return Holder.answer\n" +
                "}\n");
            CheckOk("静态字段初始化表达式", result);
            CheckI32("40 + 2 经 proxy 读回", result, 42);
        }

        // ===== 全局 wrapped 字段 singleton 化（裁定 1）=====

        // 命名空间级带 Value wrapper 字段：cell 子类即 singleton——写/读经
        // proxy 链、place 读 wrapper 字段、内部计数持久（初值在 cell 单例
        // init 里直接写 value，不经 set proxy）
        private static void TestGlobalWrappedFieldProxyChain()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper Counting {\n" +
                "    pub var gets: i32\n" +
                "    pub var sets: i32\n" +
                "    pub init() { gets = 0\n sets = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        gets = (gets + 1)\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        sets = (sets + 1)\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "var g: i32 = 40\n" +
                "pub func main(): i32 {\n" +
                "    g = 41\n" +
                "    var a = g\n" +
                "    var b = g\n" +
                "    return (((a + b) + g:Counting.gets) + g:Counting.sets)\n" +
                "}\n");
            CheckOk("全局 Value wrapper 字段 proxy 链", result);
            CheckI32("41+41 + gets(2) + sets(1)", result, 85);
        }

        // 全局 wrapped 变量 + 用户 get/set：同静态——cell 接管访问器，wrapper 外置
        private static void TestGlobalWrappedFieldWithUserAccessors()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"wrapper.get\")\n" +
                "        var v = (value as i32)\n" +
                "        return ((v + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"wrapper.set\")\n" +
                "        var v = (value as i32)\n" +
                "        inner(((v + 10) as TValue))\n" +
                "    }\n" +
                "}\n" +
                "@Shift()\n" +
                "pub var x: i32 {\n" +
                "    pub get(value: _) {\n" +
                "        core.io.Console.println(\"user.get\")\n" +
                "        return value * 2\n" +
                "    }\n" +
                "    pub set(value: _) {\n" +
                "        core.io.Console.println(\"user.set\")\n" +
                "        value = value * 2\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    x = 5\n" +
                "    return x\n" +
                "}\n");
            CheckOk("全局 wrapper+访问器外置序", result);
            TestHarness.Check("全局写读打印序", result.Stdout,
                "wrapper.set\nuser.set\nuser.get\nwrapper.get\n");
            CheckI32("全局 5→15→30 读 60→61", result, 61);
        }

        // 全局 wrapped 字段初值表达式在 main 前就绪（main 首句直接读回）
        private static void TestGlobalWrappedFieldInitializerBeforeMain()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "@SClamp\n" +
                "var g: i32 = (40 + 2)\n" +
                "pub func main(): i32 {\n" +
                "    return g\n" +
                "}\n");
            CheckOk("全局 wrapped 字段初值 main 前就绪", result);
            CheckI32("40 + 2 经 proxy 读回", result, 42);
        }

        // 全局 const wrapped 字段：const 语义 = ReadonlyCell 风味（BIL 层无
        // setValue、value 为 const、单例无参 init 内求值初值、无 1 元
        // init(value)——用户裁定的 init 元数规则）；proxy 计数持久（读两次
        // → 计数 2）、初值 main 前就绪（main 首行读即 42）
        private static void TestGlobalConstWrappedField()
        {
            const string source =
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper Counting {\n" +
                "    pub var gets: i32\n" +
                "    pub init() { gets = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        gets = (gets + 1)\n" +
                "        return value\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "const g: i32 = (40 + 2)\n" +
                "pub func main(): i32 {\n" +
                "    var a = g\n" +
                "    var b = g\n" +
                "    return ((a + b) + g:Counting.gets)\n" +
                "}\n";
            var result = Run(source);
            CheckOk("全局 const wrapped 字段 proxy 链", result);
            CheckI32("42+42 + gets(2)", result, 86);

            // const 语义（BIL 层结构断言）：ReadonlyCell 风味——无 setValue、
            // value 为 const、单例无参 init（初值内求值）、无 1 元 init(value)
            var (_, module, _) = BilTestHarness.EmitBilUnit(source);
            var cell = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol.StartsWith("..cell.."));
            TestHarness.CheckTrue("const cell extends .readonly_cell<.i32>",
                cell.ExtendsType == ".readonly_cell<.i32>");
            var members = cell.Members.OfType<BilSimpleMemberDeclaration>().ToList();
            TestHarness.CheckTrue("const cell value 为 const + wrapped(Counting)",
                members.Any(m => m.Symbol.EndsWith("#value@.i32")
                    && m.Modifiers.OfType<BilKeywordModifier>()
                        .Any(k => k.Keyword == BilKeyword.Const)
                    && m.Modifiers.OfType<BilWrappedModifier>()
                        .Any(w => w.WrapperTypeRef == "Counting")));
            TestHarness.CheckTrue("const cell 无 setValue",
                !members.Any(m => m.Symbol.Contains("$setValue")));
            TestHarness.CheckTrue("const cell 单例无参 init、无 1 元 init(value)",
                members.Any(m => m.Symbol.Contains("$init()@")
                    && m.Modifiers.OfType<BilKeywordModifier>()
                        .Any(k => k.Keyword == BilKeyword.Init))
                && !members.Any(m => m.Symbol.Contains("$init(value:")));
            TestHarness.CheckTrue("const cell override getValue",
                members.Any(m => m.Symbol.Contains("$getValue()")
                    && m.Modifiers.OfType<BilKeywordModifier>()
                        .Any(k => k.Keyword == BilKeyword.Override)));
        }

        // ===== singleton 初始化无序性（裁定 2）=====

        // a) 用户 singleton 的 init 读静态 wrapped 字段（触发其 companion 初始化）；
        // b) 静态 wrapped 字段初值引用用户 singleton 成员。两种声明顺序各跑
        // 一遍，结果一致且 init 副作用恰好一次（经静态 wrapped 计数器验证）
        private static void TestSingletonInitOrderIndependence()
        {
            const string wrapper =
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n";

            var a1 = Run(wrapper +
                "pub shared singleton class Boot {\n" +
                "    pub var captured: i32\n" +
                "    pub init() { captured = (Holder.counter * 2) }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = 7\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Boot().captured\n" +
                "}\n");
            CheckOk("a) singleton 先声明", a1);
            CheckI32("Holder.counter*2 = 14", a1, 14);

            var a2 = Run(wrapper +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = 7\n" +
                "}\n" +
                "pub shared singleton class Boot {\n" +
                "    pub var captured: i32\n" +
                "    pub init() { captured = (Holder.counter * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Boot().captured\n" +
                "}\n");
            CheckOk("a) companion 先声明", a2);
            CheckI32("Holder.counter*2 = 14", a2, 14);

            var b1 = Run(wrapper +
                "pub shared singleton class Boot {\n" +
                "    pub var answer: i32\n" +
                "    pub init() {\n" +
                "        answer = 40\n" +
                "        Cnt.total = (Cnt.total + 1)\n" +
                "    }\n" +
                "}\n" +
                "pub class Cnt {\n" +
                "    @SClamp\n" +
                "    pub static var total: i32 = 0\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = (new Boot().answer + 2)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return ((Holder.counter * 1000) + Cnt.total)\n" +
                "}\n");
            CheckOk("b) singleton 先声明", b1);
            CheckI32("42*1000 + init 恰好一次(1)", b1, 42001);

            var b2 = Run(wrapper +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = (new Boot().answer + 2)\n" +
                "}\n" +
                "pub class Cnt {\n" +
                "    @SClamp\n" +
                "    pub static var total: i32 = 0\n" +
                "}\n" +
                "pub shared singleton class Boot {\n" +
                "    pub var answer: i32\n" +
                "    pub init() {\n" +
                "        answer = 40\n" +
                "        Cnt.total = (Cnt.total + 1)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return ((Holder.counter * 1000) + Cnt.total)\n" +
                "}\n");
            CheckOk("b) companion 先声明", b2);
            CheckI32("42*1000 + init 恰好一次(1)", b2, 42001);
        }

        // 循环依赖负例：两 singleton init 互相 new 对方 → VmException 含循环链
        private static void TestSingletonCycleDetection()
        {
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

            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            TestHarness.CheckTrue("循环依赖编译无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            VmException? cycle = null;
            try
            {
                BilVm.Run(module);
            }
            catch (VmException ex)
            {
                cycle = ex;
            }
            TestHarness.CheckTrue("循环依赖抛 VmException", cycle != null);
            TestHarness.CheckTrue("异常信息含循环链（A → B → A）",
                cycle?.Message.Contains("循环依赖") == true
                && cycle?.Message.Contains("→") == true
                && cycle?.Message.Contains("A") == true
                && cycle?.Message.Contains("B") == true,
                cycle?.Message ?? "<null>");
        }

        // ===== 并发 handoff 竞态回归（VmExecutor.Execute 协程锁）=====

        // 定向放大「协程挂起 → 唤醒方重发布 → 旧 worker 复查 Step 循环条件」
        // 的双 worker 并发执行窗口（修复前 BilVm 套件循环跑约 5% 复现
        // 「未初始化变量 $.t1」/ catch 返回 <null>）。两份 module 各只编译
        // 一次，逐轮新建 VM 运行（VmContext 只读 module 建索引，共享安全）：
        // ① async throw + await + try/catch/finally ×200 轮
        // ② 密集 bare yield fork/join ×200 轮
        private static void TestCoroutineHandoffRaceRegression()
        {
            var (boomUnit, boomModule, _) = BilTestHarness.EmitBilUnit(
                "async func boom(): i32 {\n" +
                "    yield\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 2\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckTrue("竞态回归 boom 编译无诊断", !boomUnit.Diagnostics.HasErrors,
                string.Join("; ", boomUnit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var boomFailedRound = -1;
            var boomDetail = "";
            for (var round = 0; round < 200 && boomFailedRound < 0; round++)
            {
                var result = BilVm.Run(boomModule);
                if (result.Exception != null || result.Stdout != "fin\n"
                    || result.ReturnValue is not VmI32 boomValue || boomValue.Value != 2)
                {
                    boomFailedRound = round;
                    boomDetail = (result.Exception?.ToString() ?? "<noex>") + " | stdout="
                        + result.Stdout.Replace("\n", "\\n") + " | ret="
                        + (result.ReturnValue?.ToStandardText() ?? "<null>");
                }
            }
            TestHarness.CheckTrue("await throw+finally 200 轮全绿", boomFailedRound < 0,
                "第 " + boomFailedRound + " 轮失败：" + boomDetail);

            var (yieldUnit, yieldModule, _) = BilTestHarness.EmitBilUnit(
                "async func spin(n: i32): i32 {\n" +
                "    var i = 0\n" +
                "    while (i < n) {\n" +
                "        yield\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return i\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = spin(3)\n" +
                "    var b = spin(3)\n" +
                "    var c = spin(3)\n" +
                "    var d = spin(3)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    var z = await c\n" +
                "    var w = await d\n" +
                "    return (((x + y) + z) + w)\n" +
                "}\n");
            TestHarness.CheckTrue("竞态回归 yield 编译无诊断", !yieldUnit.Diagnostics.HasErrors,
                string.Join("; ", yieldUnit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var yieldFailedRound = -1;
            var yieldDetail = "";
            for (var round = 0; round < 200 && yieldFailedRound < 0; round++)
            {
                var result = BilVm.Run(yieldModule);
                if (result.Exception != null
                    || result.ReturnValue is not VmI32 sum || sum.Value != 12)
                {
                    yieldFailedRound = round;
                    yieldDetail = (result.Exception?.ToString() ?? "<noex>") + " | ret="
                        + (result.ReturnValue?.ToStandardText() ?? "<null>");
                }
            }
            TestHarness.CheckTrue("bare yield fork/join 200 轮全绿", yieldFailedRound < 0,
                "第 " + yieldFailedRound + " 轮失败：" + yieldDetail);
        }

        // ===== #15 回归：共享对象字段并发一致性 + 消息泵全量投递 =====

        // 18 个并发冷 Task（ComputeExecutor）对同一 shared 对象的字段做
        // 自增。共享对象字段存储（VmInstanceSlots）必须对多 Worker 线程
        // 并发读写安全：修复前并发 get/set 会丢失更新甚至损坏槽表，
        // 表现为 hits < 18 或未捕获异常；修复后每轮必须稳定 18。
        private static void TestSharedCounterConcurrentTasks()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "import core.collections.*\n" +
                "import core.coroutine.*\n" +
                "priv shared class Counter : core.AsyncAction {\n" +
                "    pub var hits: i64 = 0L\n" +
                "    pub var started: i64 = 0L\n" +
                "    pub override async operator call() {\n" +
                "        started += 1L\n" +
                "        hits += 1L\n" +
                "    }\n" +
                "}\n" +
                "priv shared class CounterJob : core.AsyncAction {\n" +
                "    priv const target: Counter\n" +
                "    pub init(_ -> target) {}\n" +
                "    pub override async operator call() {\n" +
                "        await target()\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const counter = new Counter()\n" +
                "    const tasks = arrayOf\\<Task>(18)\n" +
                "    var i = 0\n" +
                "    while (i < 18) {\n" +
                "        const t = new Task(new CounterJob(counter))\n" +
                "        t.run(new ComputeExecutor())\n" +
                "        tasks[i] = t\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    i = 0\n" +
                "    while (i < 18) {\n" +
                "        await (tasks[i] as Task)\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    core.io.Console.println(\"started=${counter.started} hits=${counter.hits}\")\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("并发计数编译无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var failedRound = -1;
            var detail = "";
            for (var round = 0; round < 6 && failedRound < 0; round++)
            {
                var result = BilVm.Run(module);
                if (result.Exception != null || result.Stdout != "started=18 hits=18\n")
                {
                    failedRound = round;
                    detail = (result.Exception?.ToString() ?? "<noex>") + " | stdout="
                        + result.Stdout.Replace("\n", "\\n");
                }
            }
            TestHarness.CheckTrue("18 并发 Task 字段计数 6 轮全绿", failedRound < 0,
                "第 " + failedRound + " 轮失败：" + detail);
        }

        // 3 生产者 ×6 send，手工泵循环 reader.next 计数，main await 泵终态
        //（EOS）后校验——不依赖睡眠窗口的确定性投递断言：唤醒协议在任意
        // 调度交错下都必须把 18 条消息全部交给泵，一条不丢。
        private static void TestMessagePumpDeliversAllMessages()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "import core.collections.*\n" +
                "import core.coroutine.*\n" +
                "import core.messaging.*\n" +
                "@core.serialization.Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "priv shared class Stats {\n" +
                "    pub var got: i64 = 0L\n" +
                "}\n" +
                "priv shared class Producer : core.AsyncAction {\n" +
                "    priv const sender: core.messaging.Messenger\\<Msg>\n" +
                "    priv const rounds: i32\n" +
                "    pub init(_ -> sender, _ -> rounds) {}\n" +
                "    pub override async operator call() {\n" +
                "        var i = 0\n" +
                "        while (i < rounds) {\n" +
                "            await sender.send(new Msg(i))\n" +
                "            yield\n" +
                "            i = i + 1\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "priv shared class Pump : core.AsyncAction {\n" +
                "    priv const reader: core.messaging.Reader\\<Msg>\n" +
                "    priv const stats: Stats\n" +
                "    pub init(_ -> reader, _ -> stats) {}\n" +
                "    pub override async operator call() {\n" +
                "        while (true) {\n" +
                "            const item = await reader.next()\n" +
                "            if (item.isEos) { return }\n" +
                "            stats.got += 1L\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const stats = new Stats()\n" +
                "    const sender = new core.messaging.Messenger\\<Msg>()\n" +
                "    const reader = sender.createReader()\n" +
                "    const pump = new Task(new Pump(reader, stats))\n" +
                "    pump.run()\n" +
                "    const producers = arrayOf\\<Task>(3)\n" +
                "    var i = 0\n" +
                "    while (i < 3) {\n" +
                "        const t = new Task(new Producer(sender, 6))\n" +
                "        t.run(new ComputeExecutor())\n" +
                "        producers[i] = t\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    i = 0\n" +
                "    while (i < 3) {\n" +
                "        await (producers[i] as Task)\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    sender.dispose()\n" +
                "    await (pump as Task)\n" +
                "    core.io.Console.println(\"got=${stats.got}\")\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("消息泵编译无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var failedRound = -1;
            var detail = "";
            for (var round = 0; round < 8 && failedRound < 0; round++)
            {
                var result = BilVm.Run(module);
                if (result.Exception != null || result.Stdout != "got=18\n")
                {
                    failedRound = round;
                    detail = (result.Exception?.ToString() ?? "<noex>") + " | stdout="
                        + result.Stdout.Replace("\n", "\\n");
                }
            }
            TestHarness.CheckTrue("消息泵 18 条全量投递 8 轮全绿", failedRound < 0,
                "第 " + failedRound + " 轮失败：" + detail);
        }
    }
}
