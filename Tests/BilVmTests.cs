using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S14 BIL VM 执行断言（BIL_VM_DESIGN §8 / §9 切片 1–5）：
    /// 源码 → 全编译管线 → BilModule → VM 运行。V1 标量/invoke；
    /// V2 对象字段、静态字段、struct 深拷贝、数组、enum、访问器顺序、
    /// 实例方法 receiver。V2.5 arrayOf/arrayOfElements 端到端。
    /// V3 indirect/cast/type。V4 控制流全家（if/loop/switch/try/throw）。
    /// V5 协程（eager spawn / await / yield 三形态 / quiescence）。
    /// frontend 不可达形态用直接构造的 BilModule。
    /// </summary>
    public static partial class BilVmTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        private static ParallelSuiteRunner.SuiteSpec Spec => new(
            "BilVm", Cases, sectionTitle: "BilVm");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestHelloWorld", TestHelloWorld),
            ("TestLocalArithmetic", TestLocalArithmetic),
            ("TestUnaryNegation", TestUnaryNegation),
            ("TestNegativeLiteralFolding", TestNegativeLiteralFolding),
            ("TestInvokeWithResult", TestInvokeWithResult),
            ("TestStringConcat", TestStringConcat),
            ("TestScalarLocals", TestScalarLocals),
            ("TestClassInstanceFields", TestClassInstanceFields),
            ("TestFieldZeroDefault", TestFieldZeroDefault),
            ("TestDefaultConstructorFieldInitializer", TestDefaultConstructorFieldInitializer),
            ("TestExplicitInitFieldInitializer", TestExplicitInitFieldInitializer),
            ("TestAccessorFieldInitializerViaSetter", TestAccessorFieldInitializerViaSetter),
            ("TestDefaultConstructorChaining", TestDefaultConstructorChaining),
            ("TestSeqStatementReturnTransparency", TestSeqStatementReturnTransparency),
            ("TestStaticFields", TestStaticFields),
            ("TestRootNamespaceGlobalField", TestRootNamespaceGlobalField),
            ("TestLikeDelegationForwarding", TestLikeDelegationForwarding),
            ("TestLikeDelegationInterfaceField", TestLikeDelegationInterfaceField),
            ("TestStructDeepCopy", TestStructDeepCopy),
            ("TestArrayIndexOperators", TestArrayIndexOperators),
            ("TestArrayIndexOutOfBoundsNull", TestArrayIndexOutOfBoundsNull),
            ("TestArrayIndexOutOfBoundsWriteThrows", TestArrayIndexOutOfBoundsWriteThrows),
            ("TestCompoundAssignmentIndexSingleRead", TestCompoundAssignmentIndexSingleRead),
            ("TestEnumCasePayload", TestEnumCasePayload),
            ("TestEnumCaseFixedPayload", TestEnumCaseFixedPayload),
            ("TestEnumCaseIdentity", TestEnumCaseIdentity),
            ("TestGetterSetterOrder", TestGetterSetterOrder),
            ("TestInstanceMethodReceiver", TestInstanceMethodReceiver),
            ("TestBuiltinArrayDirectModule", TestBuiltinArrayDirectModule),
            ("TestArrayOfI32", TestArrayOfI32),
            ("TestArrayOfElementsString", TestArrayOfElementsString),
            ("TestWrapperInstallDirectModule", TestWrapperInstallDirectModule),
            ("TestGetSelfDirectModule", TestGetSelfDirectModule),
            ("TestNumericCasts", TestNumericCasts),
            ("TestReferenceCasts", TestReferenceCasts),
            ("TestCastFailureAndSafe", TestCastFailureAndSafe),
            ("TestAnyBoxUnbox", TestAnyBoxUnbox),
            ("TestTypeIsSupersCase", TestTypeIsSupersCase),
            ("TestTypeWithAndGetId", TestTypeWithAndGetId),
            ("TestLambdaInvokeIndirect", TestLambdaInvokeIndirect),
            ("TestGetWrapperDirectModule", TestGetWrapperDirectModule),
            ("TestIndirectFieldAndNew", TestIndirectFieldAndNew),
            ("TestUserOperatorAdd", TestUserOperatorAdd),
            ("TestUserOperatorSourceDispatch", TestUserOperatorSourceDispatch),
            ("TestDowngradeCallWildcard", TestDowngradeCallWildcard),
            ("TestMethodWrapperWildcardInnerFullShape", TestMethodWrapperWildcardInnerFullShape),
            ("TestLambdaMethodWrapperWildcardInner", TestLambdaMethodWrapperWildcardInner),
            ("TestWildcardInnerMiddleOfWrapperChain", TestWildcardInnerMiddleOfWrapperChain),
            ("TestWrapperValueGetProxyInitArg", TestWrapperValueGetProxyInitArg),
            ("TestValueWrapperClampedMutableVar", TestValueWrapperClampedMutableVar),
            ("TestValueWrapperTwoLayerOrder", TestValueWrapperTwoLayerOrder),
            ("TestValueWrapperGetOnlyProxy", TestValueWrapperGetOnlyProxy),
            ("TestStringInterpolationToString", TestStringInterpolationToString),
            ("TestEntitySpecificMethodProxySurrounds", TestEntitySpecificMethodProxySurrounds),
            ("TestEntityWildcardMethodProxyBothDirections", TestEntityWildcardMethodProxyBothDirections),
            ("TestEntityGetterSetterProxyCounts", TestEntityGetterSetterProxyCounts),
            ("TestEntityOperatorProxyWildcardDirectModule", TestEntityOperatorProxyWildcardDirectModule),
            ("TestEntityProxyStatePersists", TestEntityProxyStatePersists),
            ("TestEntityProxySelfReadsHostField", TestEntityProxySelfReadsHostField),
            ("TestEntityGenericCastUnboundDirectModule", TestEntityGenericCastUnboundDirectModule),
            ("TestMethodWrapperCallSpecificSurrounds", TestMethodWrapperCallSpecificSurrounds),
            ("TestMethodWrapperStaticViaCompanion", TestMethodWrapperStaticViaCompanion),
            ("TestMethodWrapperDoubleLayerOrder", TestMethodWrapperDoubleLayerOrder),
            ("TestMethodWrapperStatePersists", TestMethodWrapperStatePersists),
            ("TestMethodWrapperArgPassThrough", TestMethodWrapperArgPassThrough),
            ("TestEntryPointNamespaceMain", TestEntryPointNamespaceMain),
            ("TestEntryPointStaticMember", TestEntryPointStaticMember),
            ("TestEntryPointMultipleAndExplicitSelection", TestEntryPointMultipleAndExplicitSelection),
            ("TestGlobalMethodWrapperEndToEnd", TestGlobalMethodWrapperEndToEnd),
            ("TestGlobalMethodWrapperDoubleLayerOrder", TestGlobalMethodWrapperDoubleLayerOrder),
            ("TestGlobalMethodWrapperInNamespace", TestGlobalMethodWrapperInNamespace),
            ("TestMethodWrapperGetSelfDirectModule", TestMethodWrapperGetSelfDirectModule),
            ("TestMethodWrapperNotEqualsViaOprEqualsDirectModule", TestMethodWrapperNotEqualsViaOprEqualsDirectModule),
            ("TestExceptionGetMessage", TestExceptionGetMessage),
            ("TestIntegerDivisionByZero", TestIntegerDivisionByZero),
            ("TestLambdaMethodWrapperEndToEnd", TestLambdaMethodWrapperEndToEnd),
            ("TestIfElse", TestIfElse),
            ("TestWhileAndDoWhile", TestWhileAndDoWhile),
            ("TestForRangeAndBreakContinue", TestForRangeAndBreakContinue),
            ("TestForRangeConstraintDispatch", TestForRangeConstraintDispatch),
            ("TestNamedImportGenericType", TestNamedImportGenericType),
            ("TestNamedBreakContinue", TestNamedBreakContinue),
            ("TestSwitchStatementAndExpression", TestSwitchStatementAndExpression),
            ("TestConditionalExpectedTypeMaterialization", TestConditionalExpectedTypeMaterialization),
            ("TestTryCatchFinally", TestTryCatchFinally),
            ("TestThrowAcrossFunction", TestThrowAcrossFunction),
            ("TestRetBreakContinueThroughFinally", TestRetBreakContinueThroughFinally),
            ("TestRegionBreakIdConsumption", TestRegionBreakIdConsumption),
            ("TestStructuredExitRouting", TestStructuredExitRouting),
            ("TestFinallyExceptionSlotSemantics", TestFinallyExceptionSlotSemantics),
            ("TestUsingDisposeOrder", TestUsingDisposeOrder),
            ("TestLoopEnumeratorDirectModule", TestLoopEnumeratorDirectModule),
            ("TestAsyncAwaitResult", TestAsyncAwaitResult),
            ("TestAwaitExceptionAndCompleted", TestAwaitExceptionAndCompleted),
            ("TestForkJoinAndFireAndForget", TestForkJoinAndFireAndForget),
            ("TestYieldForms", TestYieldForms),
            ("TestConcurrentPrintLines", TestConcurrentPrintLines),
            ("TestAwaitThroughTryFinally", TestAwaitThroughTryFinally),
            ("TestCoroutineStressForkJoin", TestCoroutineStressForkJoin),
            ("TestGenericConstructedNewInit", TestGenericConstructedNewInit),
            ("TestDestructuringSuperGenericInit", TestDestructuringSuperGenericInit),
            ("TestClosedGenericParamSymbolEndToEnd", TestClosedGenericParamSymbolEndToEnd),
            ("TestGenericFunctionConstructedParams", TestGenericFunctionConstructedParams),
            ("TestGenericInferenceEndToEnd", TestGenericInferenceEndToEnd),
            ("TestGenericParamConstraintDispatch", TestGenericParamConstraintDispatch),
            ("TestGetIdTypeResolvesNestedGeneric", TestGetIdTypeResolvesNestedGeneric),
            ("TestStringLength", TestStringLength),
            ("TestWrapperOutsideSetterWriteOrder", TestWrapperOutsideSetterWriteOrder),
            ("TestWrapperOutsideGetterReadOrder", TestWrapperOutsideGetterReadOrder),
            ("TestWrapperAccessorInitializerNoCrash", TestWrapperAccessorInitializerNoCrash),
            ("TestSetterBodyMultipleValueAccess", TestSetterBodyMultipleValueAccess),
            ("TestWrapperAccessorLocalOrder", TestWrapperAccessorLocalOrder),
            ("TestGenericIndexOperator", TestGenericIndexOperator),
            ("TestClassGenericParamInMethodFrame", TestClassGenericParamInMethodFrame),
            ("TestReifiedConstructZeroValue", TestReifiedConstructZeroValue),
            ("TestDynamicNewMustThrowNoMatchingInit", TestDynamicNewMustThrowNoMatchingInit),
            ("TestNestedClassNullableInit", TestNestedClassNullableInit),
            ("TestInitMatchByAssignability", TestInitMatchByAssignability),
            ("TestNullableGenericTypeArgument", TestNullableGenericTypeArgument),
            ("TestConstructedTypeStaticMembers", TestConstructedTypeStaticMembers),
            ("TestGenericNullableEndToEnd", TestGenericNullableEndToEnd),
            ("TestBoundNullableBoxingEndToEnd", TestBoundNullableBoxingEndToEnd),
            ("TestNestedStructFieldChainWrite", TestNestedStructFieldChainWrite),
            ("TestStructReceiverCallWriteback", TestStructReceiverCallWriteback),
            ("TestClassEmbeddedStructFieldWrite", TestClassEmbeddedStructFieldWrite),
            ("TestThreeLevelNestedStructWrite", TestThreeLevelNestedStructWrite),
            ("TestStaticFieldRootChainWrite", TestStaticFieldRootChainWrite),
            ("TestWrappedStaticFieldRootChainWrite", TestWrappedStaticFieldRootChainWrite),
        };

        private static void TestHelloWorld()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"Hello, world!\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("hello", result);
            TestHarness.Check("hello stdout", result.Stdout, "Hello, world!\n");
            CheckI32("hello 返回值", result, 0);
        }

        private static void TestLocalArithmetic()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    x = x * 3\n" +
                "    return x\n" +
                "}\n");
            CheckOk("算术", result);
            CheckI32("1+2 再 *3", result, 9);
        }

        private static void TestUnaryNegation()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 5\n" +
                "    var n: i32 = -a\n" +
                "    var b: bool = n == 5\n" +
                "    return n\n" +
                "}\n");
            CheckOk("一元", result);
            CheckI32("-5", result, -5);
        }

        // 负号折叠端到端（SYNTAX §3.3）：负号并入整数字面量，各符号类型
        // 下界可书写并经 BIL 标量资源文本（负文本）装载运行
        private static void TestNegativeLiteralFolding()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    return -2147483648\n" +
                "}\n");
            CheckOk("i32 下界折叠", result);
            CheckI32("return -2147483648", result, -2147483648);

            var i8 = Run(
                "pub func main(): i32 {\n" +
                "    const a: i8 = -128B\n" +
                "    if (a == -128B) { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("i8 下界折叠", i8);
            CheckI32("-128B 变量初始化与比较", i8, 1);
        }

        private static void TestInvokeWithResult()
        {
            var result = Run(
                "pub func double(a: i32): i32 { return a * 2 }\n" +
                "pub func main(): i32 {\n" +
                "    double(5)\n" +
                "    return double(21)\n" +
                "}\n");
            CheckOk("invoke", result);
            CheckI32("double(21)", result, 42);
        }

        private static void TestStringConcat()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"Hello, \" + \"world!\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("字符串拼接", result);
            TestHarness.Check("拼接 stdout", result.Stdout, "Hello, world!\n");
            CheckI32("拼接返回值", result, 0);
        }

        private static void TestScalarLocals()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var b: bool = true\n" +
                "    var d: double = 0.5\n" +
                "    var f: float = 0.1f\n" +
                "    var c: char = 'A'\n" +
                "    var n: i32 = 10\n" +
                "    n = n + 22\n" +
                "    return n\n" +
                "}\n");
            CheckOk("标量局部", result);
            CheckI32("标量局部返回值", result, 32);
        }

        private static void TestClassInstanceFields()
        {
            var result = Run(
                "pub class Box {\n" +
                "    pub var n: i32\n" +
                "    pub init(v: i32) { n = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box(7)\n" +
                "    b.n = b.n + 1\n" +
                "    return b.n\n" +
                "}\n");
            CheckOk("实例字段", result);
            CheckI32("7+1", result, 8);
        }

        private static void TestFieldZeroDefault()
        {
            // P18/S2（§9.3 DA）：非空字段必须有声明初始值或 init 赋值——
            // 声明初始值即落地值（VM 零填充仅为 BIL 层分配细节，前端不再
            // 产生「无初始值裸字段」形态）
            var result = Run(
                "pub class Box { pub var n: i32 = 0 }\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    return b.n\n" +
                "}\n");
            CheckOk("字段零值", result);
            CheckI32("声明初始值 0 落地", result, 0);
        }

        // SYNTAX §9.3 回归：默认构造应用声明处字段初始化器（含泛型类）
        //（历史 bug：初始化器被丢弃，字段读出零值）
        private static void TestDefaultConstructorFieldInitializer()
        {
            var result = Run(
                "pub class Plain {\n" +
                "    pub var count: i32 = 41\n" +
                "    pub var other: i32 = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const p = new Plain()\n" +
                "    return (p.count * 100) + p.other\n" +
                "}\n");
            CheckOk("默认构造字段初始化器", result);
            CheckI32("带初始化器取初始化器", result, 4100);
            var generic = Run(
                "class Box\\<T> {\n" +
                "    pub var item: i32 = 5\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>()\n" +
                "    return b.item\n" +
                "}\n");
            CheckOk("泛型类默认构造字段初始化器", generic);
            CheckI32("new Box<i32>().item = 5", generic, 5);
        }

        // SYNTAX §9.3 回归：显式 init（无体 / 空体）同样应用声明处
        // 实例字段初始化器（历史 bug：仅默认构造合成路径拼初始化器）
        private static void TestExplicitInitFieldInitializer()
        {
            var plain = Run(
                "pub class Plain {\n" +
                "    pub var n: i32 = 100\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Plain().n\n" +
                "}\n");
            CheckOk("无体显式 init 字段初始化器", plain);
            CheckI32("new Plain().n = 100", plain, 100);

            var withProp = Run(
                "pub class WithProp {\n" +
                "    pub var n: i32 {\n" +
                "        pub get\n" +
                "        pub set\n" +
                "    } = 100\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new WithProp().n\n" +
                "}\n");
            CheckOk("自动访问器 + 显式 init 初始化器", withProp);
            CheckI32("new WithProp().n = 100", withProp, 100);

            var withBacking = Run(
                "pub class WithBackingProp {\n" +
                "    pub var n: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { }\n" +
                "    } = 100\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new WithBackingProp().n\n" +
                "}\n");
            CheckOk("backing 访问器 + 显式 init 初始化器", withBacking);
            CheckI32("new WithBackingProp().n = 100", withBacking, 100);

            var multi = Run(
                "pub class Multi {\n" +
                "    pub var a: i32 = 1\n" +
                "    pub var b: i32 = 2\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m = new Multi()\n" +
                "    return m.a + m.b\n" +
                "}\n");
            CheckOk("空体显式 init 多字段初始化器", multi);
            CheckI32("a+b=3", multi, 3);

            var clamped = Run(
                "pub class Meter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { if (value > 100) { value = 100 } }\n" +
                "    } = 150\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Meter().value\n" +
                "}\n");
            CheckOk("显式 init 初始化器经 setter 钳制", clamped);
            CheckI32("setter 钳制 150→100", clamped, 100);

            var mapped = Run(
                "pub class M {\n" +
                "    pub var x: i32 = 10\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> y)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m = new M(7)\n" +
                "    return (m.x * 100) + m.y\n" +
                "}\n");
            CheckOk("显式 init 初始化器 + 参数映射", mapped);
            CheckI32("x=10 y=7", mapped, 1007);
        }

        // SYNTAX §9.4.1 回归：带自定义访问器的实例字段，声明处初始化器
        // 经 setter 应用（BIL set.field 本就强制走 setter——backing 钳制
        // 自初始化起生效；计算形态同经 setter）
        private static void TestAccessorFieldInitializerViaSetter()
        {
            var backing = Run(
                "pub class Meter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { if (value > 100) { value = 100 } }\n" +
                "    } = 150\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Meter().value\n" +
                "}\n");
            CheckOk("backing 访问器初始化器经 setter", backing);
            CheckI32("setter 钳制 150→100", backing, 100);

            var computed = Run(
                "pub class Therm {\n" +
                // P18/S2（§9.3 DA）：底层字段给哨兵初始值（声明序先于
                // display 的 setter 写入，终值不变）
                "    pub var celsius: i32 = 0\n" +
                "    pub var display: i32 {\n" +
                "        pub get(_: _) { return celsius }\n" +
                "        pub set(_: _) { celsius = value }\n" +
                "    } = 33\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Therm().celsius\n" +
                "}\n");
            CheckOk("计算访问器初始化器经 setter", computed);
            CheckI32("计算形态 setter 写底层字段", computed, 33);
        }

        // SYNTAX §9.3 回归：默认构造链式——隐式/合成零参构造先完成基类
        // 初始化再应用本类字段初始化器（A→B→C 逐环；无本类初始化器的
        // 派生类同样合成；泛型基类 super 照 extends 代入转发）
        private static void TestDefaultConstructorChaining()
        {
            var chain = Run(
                "pub open class A { pub var x: i32 = 41 }\n" +
                "pub open class B : A { pub var y: i32 = 7 }\n" +
                "pub class C : B { pub var z: i32 = 9 }\n" +
                "pub func main(): i32 {\n" +
                "    const c = new C()\n" +
                "    return ((c.x * 100) + (c.y * 10)) + c.z\n" +
                "}\n");
            CheckOk("A→B→C 链式默认构造", chain);
            CheckI32("x=41 y=7 z=9 全部生效", chain, 4179);

            var derivedOnly = Run(
                "pub open class B { pub var y: i32 = 7 }\n" +
                "pub class C : B { }\n" +
                "pub func main(): i32 {\n" +
                "    return new C().y\n" +
                "}\n");
            CheckOk("无本类初始化器的派生合成", derivedOnly);
            CheckI32("基类初始化经合成 super 生效", derivedOnly, 7);

            var generic = Run(
                "pub open class B\\<T> { pub var v: i32 = 41 }\n" +
                "pub class C\\<T> : B\\<T> { pub var w: i32 = 7 }\n" +
                "pub func main(): i32 {\n" +
                "    const c = new C\\<i32>()\n" +
                "    return (c.v * 100) + c.w\n" +
                "}\n");
            CheckOk("泛型基类链式默认构造", generic);
            CheckI32("泛型基类 super 照 extends 代入", generic, 4107);
        }

        // 返回保证分析透视语句位置 seq 块（含嵌套；裸 return 直达外层
        // 函数，BIL §9.4 call blk 终止判定同口径透视）
        private static void TestSeqStatementReturnTransparency()
        {
            var single = Run(
                "pub func main(): i32 {\n" +
                "    seq { return 7 }\n" +
                "}\n");
            CheckOk("seq 末位 return 透视", single);
            CheckI32("seq { return 7 }", single, 7);

            var nested = Run(
                "pub func main(): i32 {\n" +
                "    seq { seq { return 7 } }\n" +
                "}\n");
            CheckOk("嵌套 seq return 透视", nested);
            CheckI32("seq { seq { return 7 } }", nested, 7);
        }

        private static void TestStaticFields()
        {
            var result = Run(
                "pub class Counter { pub static var value: i32 }\n" +
                "pub func main(): i32 {\n" +
                "    Counter.value = 42\n" +
                "    return Counter.value\n" +
                "}\n");
            CheckOk("静态字段", result);
            CheckI32("静态字段 42", result, 42);
        }

        // 回归（历史 bug：根命名空间全局字段 P4 报 "has no owner to
        // project"）：无 namespace 声明文件的顶层 var 读写端到端——宿主
        // 投影为空形态 type()，验证器归一放行，VM 按字段符号寻址
        private static void TestRootNamespaceGlobalField()
        {
            var result = Run(
                "var counter: i32 = 0\n" +
                "pub func main(): i32 {\n" +
                "    counter = (counter + 1)\n" +
                "    counter = (counter + 1)\n" +
                "    return counter\n" +
                "}\n");
            CheckOk("根命名空间全局字段", result);
            CheckI32("两次自增后读出 2", result, 2);
        }

        // like 委托（SYNTAX §9.6）端到端：委托字段类型提供同签名实现的
        // 接口成员由合成转发方法承担；显式实现优先于委托；接口类型接收者
        // 虚调用同样命中（含显式与转发两路）
        private static void TestLikeDelegationForwarding()
        {
            var result = Run(
                "import core.io.Console\n" +
                "pub interface Fruit {\n" +
                "    func taste(): String\n" +
                "    func color(): String\n" +
                "}\n" +
                "pub class Pear implements Fruit {\n" +
                "    pub override func taste(): String { return \"pear-ish\" }\n" +
                "    pub override func color(): String { return \"green\" }\n" +
                "}\n" +
                "pub class Apple implements Fruit like pear {\n" +
                "    pub var pear: Pear = new Pear()\n" +
                "    pub override func taste(): String { return \"apple-ish\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const a = new Apple()\n" +
                "    Console.println(a.taste())\n" +
                "    Console.println(a.color())\n" +
                "    const f: Fruit = a\n" +
                "    Console.println(f.taste())\n" +
                "    Console.println(f.color())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("like 委托转发", result);
            TestHarness.Check("显式优先 + 委托转发 + 接口虚调用",
                result.Stdout, "apple-ish\ngreen\napple-ish\ngreen\n");
        }

        // bug O3：like 目标字段为接口类型——转发体调接口方法，运行时对
        // 字段值虚派发。含：基本委托运行正确、显式 override 优先于转发、
        // 字段接口默认方法（HasBody）作委托目标
        private static void TestLikeDelegationInterfaceField()
        {
            var result = Run(
                "import core.io.Console\n" +
                "pub interface Work { func run(x: i32): i32\n }\n" +
                "pub class Impl implements Work {\n" +
                "    pub override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub class ViaIface implements Work like sink {\n" +
                "    pub var sink: Work = new Impl()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new ViaIface()\n" +
                "    Console.println(b.run(3).toString())\n" +
                "    const w: Work = b\n" +
                "    Console.println(w.run(4).toString())\n" +
                "    return b.run(3)\n" +
                "}\n");
            CheckOk("接口字段 like 委托转发", result);
            TestHarness.Check("接口字段委托运行输出",
                result.Stdout, "4\n5\n");
            CheckI32("接口字段委托返回值", result, 4);

            // 显式 override 优先于接口字段 like 转发
            var explicitFirst = Run(
                "import core.io.Console\n" +
                "pub interface Work {\n" +
                "    func run(x: i32): i32\n" +
                "    func tag(): String\n" +
                "}\n" +
                "pub class Impl implements Work {\n" +
                "    pub override func run(x: i32): i32 { return (x + 1) }\n" +
                "    pub override func tag(): String { return \"impl\" }\n" +
                "}\n" +
                "pub class ViaIface implements Work like sink {\n" +
                "    pub var sink: Work = new Impl()\n" +
                "    pub override func run(x: i32): i32 { return (x - 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new ViaIface()\n" +
                "    Console.println(b.run(10).toString())\n" +
                "    Console.println(b.tag())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("显式 override 优先于接口字段转发", explicitFirst);
            TestHarness.Check("显式优先输出",
                explicitFirst.Stdout, "9\nimpl\n");

            // 字段接口的默认方法（HasBody）作委托目标：虚派发到默认实现
            var defaultMethod = Run(
                "import core.io.Console\n" +
                "pub interface Sink {\n" +
                "    func greet(): String { return \"hi\" }\n" +
                "}\n" +
                "pub interface Greeter { func greet(): String\n }\n" +
                "pub class Impl implements Sink { }\n" +
                "pub class ViaDefault implements Greeter like sink {\n" +
                "    pub var sink: Sink = new Impl()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const v = new ViaDefault()\n" +
                "    Console.println(v.greet())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("接口默认方法作委托目标", defaultMethod);
            TestHarness.Check("默认方法转发输出", defaultMethod.Stdout, "hi\n");
        }

        private static void TestStructDeepCopy()
        {
            var result = Run(
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(a: i32, b: i32) {\n" +
                "        x = a\n" +
                "        y = b\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Point(1, 2)\n" +
                "    var q = p\n" +
                "    p.x = 10\n" +
                "    return (q.x + (p.x * 100))\n" +
                "}\n");
            CheckOk("struct 深拷贝", result);
            CheckI32("赋值后互不影响", result, 1001);
        }

        private static void TestArrayIndexOperators()
        {
            var result = Run(
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
                "}\n");
            CheckOk("数组索引", result);
            CheckI32("get/set.array", result, 21);
        }

        // Q6（§13.2）：内建数组越界读取不 trap，按「读取失败」得 null
        private static void TestArrayIndexOutOfBoundsNull()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(2)\n" +
                "    a[0] = 7\n" +
                "    core.io.Console.println((a[5] if? -1).toString())\n" +
                "    const neg = a[(0 - 1)]\n" +
                "    if (neg == null) {\n" +
                "        core.io.Console.println(\"null\")\n" +
                "    }\n" +
                "    return a[0] if? 0\n" +
                "}\n");
            CheckOk("Q6：越界读取得 null", result);
            TestHarness.Check("越界/负下标 stdout", result.Stdout, "-1\nnull\n");
            CheckI32("界内读回", result, 7);
        }

        // MW9b：内建数组/Span 越界**写入**抛可捕获 core.OutOfBoundException
        //（读越界仍按空安全得 null，见上）；未捕获顶层格式对齐 native
        // reporter「{类型全名}: {message}」
        private static void TestArrayIndexOutOfBoundsWriteThrows()
        {
            var caught = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    try {\n" +
                "        a[5] = 1\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("数组越界写被 catch", caught);
            CheckI32("数组越界写 catch 返回 7", caught, 7);
            TestHarness.Check("数组越界写 getMessage stdout", caught.Stdout,
                "数组下标越界：5（长度 3）\n");

            var spanCaught = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var s = spanOf\\<i32>(3)\n" +
                "    try {\n" +
                "        s[(0 - 1)] = 1\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 8\n" +
                "    }\n" +
                "}\n");
            CheckOk("Span 越界写被 catch", spanCaught);
            CheckI32("Span 越界写 catch 返回 8", spanCaught, 8);
            TestHarness.Check("Span 越界写 getMessage stdout", spanCaught.Stdout,
                "数组下标越界：-1（长度 3）\n");

            var uncaught = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[9] = 2\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("未捕获越界写抛 OutOfBoundException",
                uncaught.Exception?.ExceptionObject is VmObject oobObj
                && oobObj.TypeRef.Contains("OutOfBoundException"),
                uncaught.Exception?.ToString() ?? "<null>");
            TestHarness.Check("未捕获越界写顶层格式",
                uncaught.Exception?.Message ?? "",
                "core::OutOfBoundException: 数组下标越界：9（长度 3）");
        }

        // §13.2 单次求值回归：索引写回中 getAtIndex 只读一次
        //（历史 bug：表达式位重读 place 导致 get→set→get 三次调用。
        //  Q6 后 a[i] op= x 读侧为 T? 不再可写，改为显式读改写形态锁定
        //  同一义务）
        private static void TestCompoundAssignmentIndexSingleRead()
        {
            var result = Run(
                "pub class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub var reads: i32\n" +
                "    pub init() { item = 10\nreads = 0 }\n" +
                "    pub operator getAtIndex(index: i32): i32? { reads += 1\nreturn item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag()\n" +
                "    b[0] = ((b[0] if? 0) + 5)\n" +
                "    return (b.reads * 1000) + b.item\n" +
                "}\n");
            CheckOk("索引显式读改写回", result);
            CheckI32("getAtIndex 恰好一次 + 写回值正确", result, 1015);
        }

        private static void TestEnumCasePayload()
        {
            var result = Run(
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(_ -> errorCode)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const failed: RequestResult = .Failed(404)\n" +
                "    return failed.errorCode\n" +
                "}\n");
            CheckOk("enum payload", result);
            CheckI32("Failed(404).errorCode", result, 404);
        }

        // SYNTAX §12.1 回归：固定 case 的声明点固定实参写入载荷
        //（历史 bug：固定实参被丢弃，字段读出零值）
        private static void TestEnumCaseFixedPayload()
        {
            var result = Run(
                "pub enum struct E {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}[\n" +
                "    Fixed(42),\n" +
                "    Param(v = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const f: E = .Fixed\n" +
                "    const p: E = .Param(7)\n" +
                "    return (f.v * 100) + p.v\n" +
                "}\n");
            CheckOk("enum 固定 case payload", result);
            CheckI32("Fixed(42).v = 42、Param(7).v = 7", result, 4207);
            var negative = Run(
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(_ -> errorCode)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const ok: RequestResult = .Success\n" +
                "    return ok.errorCode\n" +
                "}\n");
            CheckOk("enum 固定 case 负值 payload", negative);
            CheckI32("Success(-1).errorCode", negative, -1);
        }

        private static void TestEnumCaseIdentity()
        {
            var ok = Run(
                "pub enum struct Outcome { }[Ok, Failed]\n" +
                "pub func main(): Outcome { return .Ok }\n");
            CheckOk("enum 身份 Ok", ok);
            TestHarness.CheckTrue("Ok case 符号",
                ok.ReturnValue is VmEnum e && e.CaseSymbol == "Outcome.Ok",
                ok.ReturnValue?.ToStandardText() ?? "<null>");
            var failed = Run(
                "pub enum struct Outcome { }[Ok, Failed]\n" +
                "pub func main(): Outcome { return .Failed }\n");
            CheckOk("enum 身份 Failed", failed);
            TestHarness.CheckTrue("Failed 与 Ok 身份不同",
                failed.ReturnValue is VmEnum f && f.CaseSymbol == "Outcome.Failed"
                && ok.ReturnValue is VmEnum o && !f.SameCase(o),
                failed.ReturnValue?.ToStandardText() ?? "<null>");
        }

        private static void TestGetterSetterOrder()
        {
            var result = Run(
                "pub class Box {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) {\n" +
                "            core.io.Console.println(\"g\")\n" +
                "            return value\n" +
                "        }\n" +
                "        pub set(value: _) {\n" +
                "            core.io.Console.println(\"s\")\n" +
                "        }\n" +
                "    }\n" +
                "    pub init() { value = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    b.value = 3\n" +
                "    return b.value\n" +
                "}\n");
            CheckOk("访问器", result);
            TestHarness.Check("getter/setter 调用顺序", result.Stdout, "s\ns\ng\n");
            CheckI32("访问器返回值", result, 3);
        }

        private static void TestInstanceMethodReceiver()
        {
            var result = Run(
                "pub class Counter {\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 0 }\n" +
                "    pub func inc(): i32 {\n" +
                "        n = n + 1\n" +
                "        return n\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter()\n" +
                "    c.inc()\n" +
                "    return c.inc()\n" +
                "}\n");
            CheckOk("实例方法", result);
            CheckI32("this 调用链", result, 2);
        }

        // V2.5：拆除「单 i32 = 长度」特权。new .array [2] 是 1 元数组
        //（元素为 2），不再分配长度 2 的零数组。源码 `new Array<T>(n)`
        // 由 P3 拒绝（Array 无 init）。
        private static void TestBuiltinArrayDirectModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_N", BilScalarType.I32, "2"));
            module.Resources.Add(new BilScalarResource("R_Z", BilScalarType.I32, "0"));
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "n"));
            main.Vars.Add(new BilVarDeclaration(".i32", "z"));
            main.Vars.Add(new BilVarDeclaration(".array<.i32>", "a"));
            // Q6：get.array 结果 = .nullable<.i32>（索引读取恒可空）
            main.Vars.Add(new BilVarDeclaration(".nullable<.i32>", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("n")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("z")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type(".array<.i32>"),
                BilOp.Var("a"), new[] { BilOp.Var("n") }));
            entry.Instructions.Add(new GetArrayInstruction(BilOp.Var("a"), BilOp.Var("z"),
                BilOp.Var("x")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            var result = BilVm.Run(module);
            CheckOk("new .array 不再把单 i32 当长度", result);
            TestHarness.CheckTrue("元素是 2 不是零（长度特权已拆除；Q6 包 Nullable）",
                result.ReturnValue is VmNullable { HasValue: true, Value: VmI32 { Value: 2 } });

            var (unit, _, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var a = new Array\\<i32>(3)\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("源码 new Array<T>(n) 无 init 报错",
                unit.Diagnostics.HasErrors
                && unit.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("no constructor", StringComparison.OrdinalIgnoreCase)
                    || d.Message.Contains("has no constructor")),
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
        }

        private static void TestArrayOfI32()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 10\n" +
                "    a[1] = 20\n" +
                "    a[2] = 12\n" +
                "    return (((a[0] if? 0) + (a[1] if? 0)) + (a[2] if? 0))\n" +
                "}\n");
            CheckOk("arrayOf<i32>", result);
            CheckI32("arrayOf 内容 10+20+12", result, 42);
        }

        private static void TestArrayOfElementsString()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOfElements\\<String>(\"Hello, \", \"world!\")\n" +
                "    core.io.Console.println(((a[0] if? \"\") + (a[1] if? \"\")))\n" +
                "    return a.length\n" +
                "}\n");
            CheckOk("arrayOfElements<String>", result);
            TestHarness.Check("arrayOfElements stdout", result.Stdout, "Hello, world!\n");
            CheckI32("arrayOfElements.length", result, 2);
        }

        // new.wrapped / new.wrapper.entity 安装语义：frontend 对无参
        // ..init.wrapper 走普通 new（§14.4 互斥），有参形态此处直接构造。
        private static void TestWrapperInstallDirectModule()
        {
            var module = WrapperHostModule();
            var result = BilVm.Run(module);
            CheckOk("wrapper 安装", result);
            TestHarness.CheckTrue("返回宿主", result.ReturnValue is VmObject host
                && host.TypeRef == "Host"
                && host.TryReadHidden(VmContext.HiddenEntityKey("Wrap"), out var stored)
                && stored is VmObject wrapper
                && wrapper.Host == host
                && wrapper.TryReadField("Wrap#level@.i32", out var level)
                && level is VmI32 n && n.Value == 9,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // get.self 仅 proxy 模板体内合法；proxy 派发属 V3，此处直接把
        // 已安装 Host 的 wrapper 作为 .this 压入模板 fn。
        private static void TestGetSelfDirectModule()
        {
            var module = GetSelfModule();
            var host = new VmObject("Host", valueType: false);
            host.WriteField("Host#n@.i32", new VmI32(9));
            var wrapper = new VmObject("Wrap", valueType: true) { Host = host };
            var result = RunPrepared(module, "Wrap$.proxy.read()@.i32", new VmValue[] { wrapper });
            CheckOk("get.self", result);
            CheckI32("self.n", result, 9);
        }

        private static BilModule WrapperHostModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_L", BilScalarType.I32, "5"));
            module.Resources.Add(new BilScalarResource("R_L2", BilScalarType.I32, "9"));
            var wrap = new BilTypeDeclaration("Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            wrap.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Wrap#level@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            wrap.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Wrap$init(level:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(wrap);
            var host = new BilTypeDeclaration("Host", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("Wrap"));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Host$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Host$..init.wrapper(level:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            module.LocalSymbols.Add(host);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@Host",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var wrapInit = new BilFunction("Wrap$init(level:.i32)@.void");
            wrapInit.Args.Add(new BilArgDeclaration(".return", ".void"));
            wrapInit.Args.Add(new BilArgDeclaration(".this", "Wrap"));
            wrapInit.Args.Add(new BilArgDeclaration("level", ".i32"));
            var wrapEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("level"),
                BilOp.Var(".this"), BilOp.Field("Wrap#level@.i32")));
            wrapEntry.Instructions.Add(new RetInstruction());
            wrapInit.Blocks.Add(wrapEntry);
            module.Functions.Add(wrapInit);

            var hostInit = new BilFunction("Host$init()@.void");
            hostInit.Args.Add(new BilArgDeclaration(".return", ".void"));
            hostInit.Args.Add(new BilArgDeclaration(".this", "Host"));
            var hostInitEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            hostInitEntry.Instructions.Add(new RetInstruction());
            hostInit.Blocks.Add(hostInitEntry);
            module.Functions.Add(hostInit);

            var initWrapper = new BilFunction("Host$..init.wrapper(level:.i32)@.void");
            initWrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            initWrapper.Args.Add(new BilArgDeclaration(".this", "Host"));
            initWrapper.Args.Add(new BilArgDeclaration("level", ".i32"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new NewWrapperEntityInstruction(BilOp.Type("Wrap"),
                new[] { BilOp.Var("level") }));
            wrapperEntry.Instructions.Add(new RetInstruction());
            initWrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(initWrapper);

            var main = new BilFunction("$main()@Host");
            main.Args.Add(new BilArgDeclaration(".return", "Host"));
            main.Vars.Add(new BilVarDeclaration(".i32", "lv"));
            main.Vars.Add(new BilVarDeclaration(".i32", "lv2"));
            main.Vars.Add(new BilVarDeclaration("Host", "h"));
            var mainEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            mainEntry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("lv")));
            mainEntry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("lv2")));
            mainEntry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Host"),
                BilOp.Var("h"), new[] { BilOp.Var("lv") },
                Array.Empty<BilVariableOperand>()));
            mainEntry.Instructions.Add(new SetWrapperFieldInstruction(BilOp.Var("lv2"),
                BilOp.Var("h"), BilOp.Wrapper("Wrap"),
                BilOp.Field("Wrap#level@.i32")));
            mainEntry.Instructions.Add(new RetInstruction(BilOp.Var("h")));
            main.Blocks.Add(mainEntry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule GetSelfModule()
        {
            var module = new BilModule();
            var wrap = new BilTypeDeclaration("Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            wrap.GenericParameters.Add("TTarget");
            wrap.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Wrap$.proxy.read()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                }));
            module.LocalSymbols.Add(wrap);
            var host = new BilTypeDeclaration("Host", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Host#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(host);
            var proxy = new BilFunction("Wrap$.proxy.read()@.i32");
            proxy.Args.Add(new BilArgDeclaration(".return", ".i32"));
            proxy.Args.Add(new BilArgDeclaration(".this", "Wrap"));
            proxy.Vars.Add(new BilVarDeclaration(".generic<$.generic.TTarget>", "s"));
            proxy.Vars.Add(new BilVarDeclaration(".i32", "n"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new GetSelfInstruction(BilOp.Var("s")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("s"), BilOp.Var("n"),
                BilOp.Field("Host#n@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("n")));
            proxy.Blocks.Add(entry);
            module.Functions.Add(proxy);
            return module;
        }

        private static void TestNumericCasts()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 1000\n" +
                "    var b: i64 = (a as i64)\n" +
                "    var c: i16 = (a as i16)\n" +
                "    var d: u8 = (42 as u8)\n" +
                "    var e: double = (a as double)\n" +
                "    var f: i32 = ((e as i32) + (c as i32))\n" +
                "    return ((f + (d as i32)) + (b as i32))\n" +
                "}\n");
            CheckOk("数值 cast", result);
            CheckI32("widening/narrowing 抽样", result, 3042);
        }

        private static void TestReferenceCasts()
        {
            var up = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): bool {\n" +
                "    var d: Animal = new Dog()\n" +
                "    return (d is Dog)\n" +
                "}\n");
            CheckOk("引用 is", up);
            CheckBool("Dog is Dog", up, true);
            var down = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    var a: Animal = new Dog()\n" +
                "    var d = (a as Dog)\n" +
                "    return 7\n" +
                "}\n");
            CheckOk("向下 cast", down);
            CheckI32("Animal→Dog", down, 7);
        }

        private static void TestCastFailureAndSafe()
        {
            var fail = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    var a: Animal = new Animal()\n" +
                "    var d = (a as Dog)\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("失败 cast 抛 CastException",
                fail.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("CastException"),
                fail.Exception?.ToString() ?? "<null>");
            var safe = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): bool {\n" +
                "    var a: Animal = new Animal()\n" +
                "    var d = (a as? Dog)\n" +
                "    return (d == null)\n" +
                "}\n");
            CheckOk("cast.safe", safe);
            CheckBool("safe 产 null", safe, true);
        }

        private static void TestAnyBoxUnbox()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var a: Any = 21\n" +
                "    var n = (a as i32)\n" +
                "    return (n + n)\n" +
                "}\n");
            CheckOk("Any 装拆箱", result);
            CheckI32("21+21", result, 42);
        }

        private static void TestTypeIsSupersCase()
        {
            var isCheck = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): bool {\n" +
                "    var d = new Dog()\n" +
                "    return (d is Animal)\n" +
                "}\n");
            CheckOk("type.is", isCheck);
            CheckBool("Dog is Animal", isCheck, true);
            var supers = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): bool {\n" +
                "    var a = new Animal()\n" +
                "    return (a supers Dog)\n" +
                "}\n");
            CheckOk("type.supers", supers);
            CheckBool("Animal supers Dog", supers, true);
            var isCase = Run(
                "enum struct Outcome { }[Ok, Failed]\n" +
                "pub func main(): bool {\n" +
                "    const r: Outcome = .Failed\n" +
                "    return (r is .Failed)\n" +
                "}\n");
            CheckOk("type.is.case", isCase);
            CheckBool("is .Failed", isCase, true);
        }

        private static void TestTypeWithAndGetId()
        {
            var typeOf = Run(
                "pub class Box { pub var n: i32 = 0 }\n" +
                "pub func main(): bool {\n" +
                "    var b = new Box()\n" +
                "    var t = typeOf(b)\n" +
                "    return (b is t)\n" +
                "}\n");
            CheckOk("getid.var + type.is.indirect", typeOf);
            CheckBool("b is typeOf(b)", typeOf, true);
            var typeId = Run(
                "pub class Box { pub var n: i32 = 0 }\n" +
                "pub func main(): bool {\n" +
                "    var b = new Box()\n" +
                "    var t = typeOf(Box)\n" +
                "    return (b is t)\n" +
                "}\n");
            CheckOk("getid.type + type.is.indirect", typeId);
            CheckBool("b is typeOf(Box)", typeId, true);
        }

        private static void TestLambdaInvokeIndirect()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n");
            CheckOk("lambda invoke.indirect", result);
            CheckI32("fn(41)", result, 42);
        }

        // get.wrapper / get.wrapper.indirect：frontend 无整体取值路径，直接构造。
        private static void TestGetWrapperDirectModule()
        {
            var module = WrapperHostModule();
            var main = module.Functions.First(f => f.Symbol == "$main()@Host");
            main.Vars.Add(new BilVarDeclaration("Wrap", "w"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Wrap>", "wid"));
            main.Vars.Add(new BilVarDeclaration(".i32", "lv3"));
            var entry = main.Blocks[0];
            entry.Instructions.RemoveAt(entry.Instructions.Count - 1);
            entry.Instructions.Add(new GetWrapperInstruction(BilOp.Var("h"),
                BilOp.Type("Wrap"), BilOp.Var("w")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Wrap"), BilOp.Var("wid")));
            entry.Instructions.Add(new GetWrapperIndirectInstruction(BilOp.Var("h"),
                BilOp.Var("wid"), BilOp.Var("w")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("w"), BilOp.Var("lv3"),
                BilOp.Field("Wrap#level@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("lv3")));
            var result = BilVm.Run(module);
            CheckOk("get.wrapper", result);
            CheckI32("wrapper.level", result, 9);
        }

        // frontend 尚不发射 new.indirect / get.field.indirect；直接构造测 VM。
        private static void TestIndirectFieldAndNew()
        {
            var module = IndirectBoxModule();
            var result = BilVm.Run(module);
            CheckOk("indirect 字段/构造", result);
            CheckI32("new.indirect + set/get.field.indirect", result, 7);
        }

        // 直构 BIL：add 指令按精确类型派发用户 plus（§22.3）。
        private static void TestUserOperatorAdd()
        {
            var module = VectorPlusModule();
            var result = BilVm.Run(module);
            CheckOk("用户 operator plus", result);
            CheckI32("Vector2 add.x", result, 4);
        }

        // 源码运算符位置：用户 plus / compareTo 四比较 / 一元 / 复合赋值 /
        // and 两侧求值 / 运算结果参与后续表达式
        private static void TestUserOperatorSourceDispatch()
        {
            var plus = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    return ((c.x + 1))\n" +
                "}\n");
            CheckOk("源码 plus", plus);
            CheckI32("1+2 再 +1 = 4", plus, 4);

            var compare = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator compareTo(another: Vec): ComparisonResult {\n" +
                "        if ((x < another.x)) { return .LesserThanAnother }\n" +
                "        if ((x > another.x)) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = new Vec(1)\n" +
                "    var n = 0\n" +
                "    if ((a < b)) { n = (n + 1) }\n" +
                "    if ((a <= c)) { n = (n + 1) }\n" +
                "    if ((b > a)) { n = (n + 1) }\n" +
                "    if ((c >= a)) { n = (n + 1) }\n" +
                "    if ((b < a)) { n = (n + 10) }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("源码 compareTo", compare);
            CheckI32("< <= > >= 四真一假", compare, 4);

            var unary = Run(
                "class Bits {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator opposite(): Bits { return new Bits((0 - v)) }\n" +
                "    pub operator not(): Bits { return new Bits(if ((v == 0)) { return@_ 1 } else { return@_ 0 }) }\n" +
                "    pub operator bitwiseNot(): Bits { return new Bits(!v) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Bits(3)\n" +
                "    var z = new Bits(0)\n" +
                "    var n = 0\n" +
                "    if (((-a).v == (0 - 3))) { n = (n + 1) }\n" +
                "    if (((not z).v == 1)) { n = (n + 1) }\n" +
                "    if (((!a).v == (!3))) { n = (n + 1) }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("源码一元 opposite/not/bitwiseNot", unary);
            CheckI32("三元均命中", unary, 3);

            var compound = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(10)\n" +
                "    var b = new Vec(5)\n" +
                "    a += b\n" +
                "    return a.x\n" +
                "}\n");
            CheckOk("源码复合赋值 +=", compound);
            CheckI32("10 += 5 → 15", compound, 15);

            var bothSides = Run(
                "class Flag {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n) { }\n" +
                "    pub operator and(other: Flag): Flag { return other }\n" +
                "}\n" +
                "var hits: i32\n" +
                "func bump(): Flag {\n" +
                "    hits = (hits + 1)\n" +
                "    return new Flag(1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    hits = 0\n" +
                "    var a = new Flag(0)\n" +
                "    var b = a and bump()\n" +
                "    return (hits + b.n)\n" +
                "}\n");
            CheckOk("用户 and 两侧求值", bothSides);
            CheckI32("hits=1 且取右侧 n=1", bothSides, 2);
        }

        // §22.5 方法 hook core::Any$call???：烘焙归 Middleware，VM 行为参考
        // 即链末默认实现——未路由抛 core::NoSuchMethodException（RUNTIME §14.2）。
        private static void TestDowngradeCallWildcard()
        {
            const string service =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n";
            var thrown = Run(service +
                "pub func main(): i32 {\n" +
                "    var service = new Service()\n" +
                "    service.fetchUserById(42)\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("降级未路由抛 NoSuchMethodException",
                thrown.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("NoSuchMethodException"),
                thrown.Exception?.ToString() ?? "<null>");
            TestHarness.CheckTrue("异常消息带请求 symbol",
                thrown.Exception?.Message.Contains("Service$fetchUserById") == true,
                thrown.Exception?.Message ?? "<null>");
            var caught = Run(service +
                "pub func main(): i32 {\n" +
                "    var service = new Service()\n" +
                "    try {\n" +
                "        service.fetchUserById(42)\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        return 7\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("降级异常可 catch", caught);
            CheckI32("catch 返回 7", caught, 7);
        }

        // Method wrapper wildcard 全形状转发：.name 显式传入 inner，VM 消费它
        // 重路由下一环；.name 运行时值 = 完整 BIL 方法符号。
        private static void TestMethodWrapperWildcardInnerFullShape()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(\"name=\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(41)\n" +
                "}\n");
            CheckOk("Method wrapper wildcard 全形状 inner", result);
            CheckI32("普通方法经 wildcard 返回 42", result, 42);
            TestHarness.CheckTrue(".name = 完整 BIL 方法符号",
                result.Stdout.Contains("name=Service$fetch(x:.i32)@.i32") == true,
                result.Stdout);
        }

        // lambda 头 Method wrapper wildcard：.name 诚实填 $$call 合成符号。
        private static void TestLambdaMethodWrapperWildcardInner()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(\"name=\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var fn = func{ @Timed (x: i32): i32 -> (x + 1) }\n" +
                "    return fn(41)\n" +
                "}\n");
            CheckOk("lambda Method wrapper wildcard 全形状 inner", result);
            CheckI32("lambda 经 wildcard 返回 42", result, 42);
            TestHarness.CheckTrue(".name = $$call 合成符号",
                result.Stdout.Contains("name=..lambda..")
                && result.Stdout.Contains("$$call(x:.i32)@.i32"),
                result.Stdout);
        }

        // 双 Entity wrapper：外层 specific，内层 wildcard。wildcard 环 inner 全形状
        // 透传，VM 按传入 symbol 重路由到链末原始方法。
        private static void TestWildcardInnerMiddleOfWrapperChain()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WOuter {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 { return inner(x) }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WInner {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"inner:\" + symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WOuter\n" +
                "@WInner\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n");
            CheckOk("wildcard 在双 wrapper 链中间", result);
            CheckI32("双链 wildcard 透传返回 42", result, 42);
            TestHarness.CheckTrue("wildcard 环收到正确 symbol",
                result.Stdout.Contains("inner:Service$ping(x:.i32)@.i32") == true,
                result.Stdout);
        }

        // Value wrapper 最原始的用户场景回归：init 实参透传 + get proxy
        // 每次先计数、状态原地持久。init 实参 a=10 落到 step；const b 初值
        // 0 三次读取经 .proxy.get 链依次得到 10/20/30；最后 count==3。
        private static void TestWrapperValueGetProxyInitArg()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper WrapperA {\n" +
                "    pub var count: i32\n" +
                "    pub var step: i32\n" +
                "    pub init(s: i32) {\n" +
                "        count = 0\n" +
                "        step = s\n" +
                "    }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        count = (count + 1)\n" +
                "        return (((value as i32) + (count * step)) as TValue)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = 10\n" +
                "    @WrapperA(a)\n" +
                "    const b = 0\n" +
                "    var r1 = b\n" +
                "    var r2 = b\n" +
                "    var r3 = b\n" +
                "    var ok1 = ((b:WrapperA.step == 10) and (r1 == 10))\n" +
                "    var ok2 = (((ok1 and (r2 == 20)) and (r3 == 30))" +
                " and (b:WrapperA.count == 3))\n" +
                "    if (ok2) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    } else {\n" +
                "        core.io.Console.println(\"FAIL\")\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            CheckOk("WrapperA init 实参透传 + get proxy 状态持久", result);
            TestHarness.Check("WrapperA stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("WrapperA main 返回 0", result, 0);
        }

        // Value 派发 a)：同时实现 get/set 的 Clamped 风格 wrapper 修饰可变
        // var——写 200 经 set 链夹到 100、写 -20 经 set 链夹到 0（inner 写回）。
        private static void TestValueWrapperClampedMutableVar()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init() {\n" +
                "        min = 0\n" +
                "        max = 100\n" +
                "    }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        if ((v > max)) { v = max }\n" +
                "        if ((v < min)) { v = min }\n" +
                "        inner((v as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    health = 200\n" +
                "    var a = health\n" +
                "    health = -20\n" +
                "    var b = health\n" +
                "    if (((a == 100) and (b == 0))) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    } else {\n" +
                "        core.io.Console.println(\"FAIL\")\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            CheckOk("Clamped 局部 var 写夹取", result);
            TestHarness.Check("Clamped stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Clamped main 返回 0", result, 0);
        }

        // Value 派发 b)：同一局部叠两个 Value wrapper——读序内层先
        //（B.get → A.get）、写序外层先（A.set → B.set）。
        private static void TestValueWrapperTwoLayerOrder()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"A.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"A.set\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"B.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"B.set\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 0\n" +
                "    x = 1\n" +
                "    var r = x\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("双层 Value wrapper 读序内层先/写序外层先", result);
            TestHarness.Check("双层 Value wrapper 顺序 stdout", result.Stdout,
                "A.set\n" +
                "B.set\n" +
                "A.set\n" +
                "B.set\n" +
                "B.get\n" +
                "A.get\n");
        }

        // Value 派发 c)：只带 get proxy 的 wrapper 修饰只读 const——读取经
        // proxy 生效。（var + get-only wrapper 自 §14.3 只读适用性检查前移
        // 后是 P3 编译错误，不再到 VM——回归用例在 Binder 套件）
        private static void TestValueWrapperGetOnlyProxy()
        {
            var readOnly = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper ReadOnly {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((value as i32) + 1) as TValue)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @ReadOnly\n" +
                "    const y: i32 = 1\n" +
                "    var v = y\n" +
                "    return v\n" +
                "}\n");
            CheckOk("get-only wrapper const 读经 proxy", readOnly);
            CheckI32("const 初值 1 经 get proxy 返回 2", readOnly, 2);
        }

        // String 插值：i32 插值 + 拼接混合；class 实例插值输出类型名。
        private static void TestStringInterpolationToString()
        {
            var result = Run(
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(a: i32, b: i32) {\n" +
                "        x = a\n" +
                "        y = b\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var n = 42\n" +
                "    var p = new Point(1, 2)\n" +
                "    core.io.Console.println((\"n=${n}\") + \"!\")\n" +
                "    core.io.Console.println(\"p=${p}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("String 插值 i32 与 class 实例", result);
            TestHarness.Check("插值 stdout", result.Stdout,
                "n=42!\n" +
                "p=Point\n");
            CheckI32("插值 main 返回 0", result, 0);
        }

        // Entity 派发 a)：specific 方法 proxy 环绕 inner——前后各 println，
        // 验证顺序与返回值（proxy 可改返回值）。
        private static void TestEntitySpecificMethodProxySurrounds()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Around {\n" +
                "    pub init()\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"before\")\n" +
                "        var r = inner(x)\n" +
                "        core.io.Console.println(\"after\")\n" +
                "        return (r + 1)\n" +
                "    }\n" +
                "}\n" +
                "@Around\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "        return (x * 2)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(21)\n" +
                "}\n");
            CheckOk("Entity specific 方法 proxy 环绕", result);
            TestHarness.Check("环绕 stdout 顺序", result.Stdout,
                "before\n" +
                "body\n" +
                "after\n");
            CheckI32("proxy 改返回值 21*2+1", result, 43);
        }

        // Entity 派发 b)：wildcard 方法 proxy 两方向——已声明成员无 specific
        // 时落 wildcard 并经 inner 到原始方法；未声明成员经 call??? 进入同一
        // wildcard 并被 proxy 直接路由成功（不 inner）。
        private static void TestEntityWildcardMethodProxyBothDirections()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        if (symbol == \"Service$fetchUserById(.i32)@.any\") {\n" +
                "            return (99 as TReturn)\n" +
                "        } else {\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(41)\n" +
                "    var b = (s.fetchUserById(42) as i32)\n" +
                "    return ((a * 1000) + b)\n" +
                "}\n");
            CheckOk("Entity wildcard 两方向", result);
            CheckI32("ping 经 inner=42、fetchUserById 被 proxy 路由=99", result, 42099);
            TestHarness.CheckTrue("wildcard 收到已声明 symbol",
                result.Stdout.Contains("Service$ping") == true, result.Stdout);
            TestHarness.CheckTrue("wildcard 收到未声明 symbol",
                result.Stdout.Contains("Service$fetchUserById") == true, result.Stdout);
        }

        // Entity 派发 c)：getter/setter proxy——宿主字段读写各绕一层并计数。
        private static void TestEntityGetterSetterProxyCounts()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var gets: i32\n" +
                "    pub var sets: i32\n" +
                "    pub init() {\n" +
                "        gets = 0\n" +
                "        sets = 0\n" +
                "    }\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {\n" +
                "        gets = (gets + 1)\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.name\\<TField>(value: TField) {\n" +
                "        sets = (sets + 1)\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    var n = s.name\n" +
                "    var ok1 = ((n == \"b\") and (s:Counting.gets == 1))\n" +
                "    var ok2 = (ok1 and (s:Counting.sets == 1))\n" +
                "    if (ok2) { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("Entity getter/setter proxy 计数", result);
            CheckI32("写读各绕一层且字段生效", result, 1);
        }

        // Entity 派发 e)：operator proxy——frontend 的 `+` 不查用户 operator
        //（TestUserOperatorAdd 同因），直接发 add 指令验证 `+` 命中
        // .proxy.opr.*（wildcard proxy 直接返回 (99,0) 的 Vec）。
        private static void TestEntityOperatorProxyWildcardDirectModule()
        {
            var result = BilVm.Run(WrappedVecOperatorProxyModule());
            CheckOk("Entity operator .proxy.opr.* 直构", result);
            CheckI32("+ 命中 .proxy.opr.* 返回 99", result, 99);
        }

        // Entity 派发 f)：proxy 状态跨调用持久（计数器累加，值参与返回值）。
        private static void TestEntityProxyStatePersists()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(10)\n" +
                "    var b = s.fetch(10)\n" +
                "    if (((a == 11) and (b == 12))) { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("Entity proxy 状态持久", result);
            CheckI32("第 1 次 11、第 2 次 12", result, 1);
        }

        // Entity 派发 g)：get.self——Entity proxy 体内读宿主字段。
        private static void TestEntityProxySelfReadsHostField()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.peek(): i32 {\n" +
                "        var host = (self as Service)\n" +
                "        return host.n\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 42 }\n" +
                "    pub func peek(): i32 { return 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.peek()\n" +
                "}\n");
            CheckOk("Entity proxy self 读宿主字段", result);
            CheckI32("self.n == 42", result, 42);
        }

        // Entity 派发 h)：负例直构——未绑定语境下 .generic 参与 cast 抛
        // VmException（执行期类型操作绝不恒等放行）。
        private static void TestEntityGenericCastUnboundDirectModule()
        {
            var result = BilVm.Run(UnboundGenericCastModule());
            TestHarness.CheckTrue("未绑定 .generic cast 抛 VmException",
                result.Exception != null
                && result.Exception.Message.Contains("无法解析泛型占位")
                && result.Exception.Message.Contains(".generic<$.generic.T>"),
                result.Exception?.ToString() ?? "<null>");
        }

        // .proxy.call a)：实例方法 specific 环绕 + 改返回值（形状镜像被修饰
        // 方法参数）。
        private static void TestMethodWrapperCallSpecificSurrounds()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"before\")\n" +
                "        var r = inner(x)\n" +
                "        core.io.Console.println(\"after\")\n" +
                "        return (((r as i32) + 1) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "        return (x * 2)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(21)\n" +
                "}\n");
            CheckOk("Method wrapper specific 环绕", result);
            TestHarness.Check("Method wrapper stdout 顺序", result.Stdout,
                "before\n" +
                "body\n" +
                "after\n");
            CheckI32("Method wrapper 改返回值 43", result, 43);
        }

        // .proxy.call b)：静态方法经 companion（shared Method wrapper）。
        private static void TestMethodWrapperStaticViaCompanion()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub class Calc {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub static func total(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return Calc.total(41)\n" +
                "}\n");
            CheckOk("静态 Method wrapper 经 companion", result);
            CheckI32("Calc.total(41) 返回 42", result, 42);
        }

        // .proxy.call c)：同方法双 Method wrapper 的 outer→inner 顺序。
        private static void TestMethodWrapperDoubleLayerOrder()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"A\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"B\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @A\n" +
                "    @B\n" +
                "    pub func fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "        return x\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(42)\n" +
                "}\n");
            CheckOk("双 Method wrapper 顺序", result);
            TestHarness.Check("双 Method wrapper stdout", result.Stdout,
                "A\n" +
                "B\n" +
                "body\n");
            CheckI32("双 Method wrapper 透传 42", result, 42);
        }

        // .proxy.call d)：wrapper 状态跨调用持久（计数器累加）。
        private static void TestMethodWrapperStatePersists()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Counted {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        calls = (calls + 1)\n" +
                "        var r = inner(x)\n" +
                "        return (((r as i32) + calls) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Counted\n" +
                "    pub func fetch(x: i32): i32 { return (x * 10) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(1)\n" +
                "    var b = s.fetch(1)\n" +
                "    return ((a * 100) + b)\n" +
                "}\n");
            CheckOk("Method wrapper 状态持久", result);
            CheckI32("第 1 次 11、第 2 次 12 → 1112", result, 1112);
        }

        // .proxy.call e)：参数透传正确性（三参数，proxy 内插值打印实参）。
        private static void TestMethodWrapperArgPassThrough()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Echo {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(a: i32, b: i32, c: i32): TReturn {\n" +
                "        core.io.Console.println(\"a=${a} b=${b} c=${c}\")\n" +
                "        return inner(a, b, c)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Echo\n" +
                "    pub func sum(a: i32, b: i32, c: i32): i32 {\n" +
                "        return (((a * 100) + (b * 10)) + c)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.sum(1, 2, 3)\n" +
                "}\n");
            CheckOk("Method wrapper 参数透传", result);
            TestHarness.Check("实参插值 stdout", result.Stdout, "a=1 b=2 c=3\n");
            CheckI32("sum(1,2,3) 返回 123", result, 123);
        }

        // ===== @EntryPoint（SYNTAX §17.1）=====

        // 命名空间内的 main 经 @EntryPoint 成为入口（裸 main 命名约定
        // 只认全局命名空间的历史问题修复）
        private static void TestEntryPointNamespaceMain()
        {
            var result = Run(
                "namespace app\n" +
                "@EntryPoint\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"ns main\")\n" +
                "    return 42\n" +
                "}\n");
            CheckOk("命名空间 @EntryPoint main", result);
            CheckI32("命名空间 main 返回 42", result, 42);
            TestHarness.Check("命名空间 main stdout", result.Stdout, "ns main\n");
        }

        // 静态成员方法经 @EntryPoint 成为入口
        private static void TestEntryPointStaticMember()
        {
            var result = Run(
                "pub class App {\n" +
                "    pub init()\n" +
                "    @EntryPoint\n" +
                "    pub static func run(): i32 { return 7 }\n" +
                "}\n");
            CheckOk("静态成员 @EntryPoint", result);
            CheckI32("App.run() 返回 7", result, 7);
        }

        // 多入口：缺省运行报错（报文含 --entry-point 提示）；显式符号选中；
        // 指定符号非 entrypoint 报错。同一模块多次运行（VM 无残留状态）
        private static void TestEntryPointMultipleAndExplicitSelection()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 { return 1 }\n" +
                "@EntryPoint\n" +
                "pub func other(): i32 { return 2 }\n");
            TestHarness.CheckTrue("双入口编译无诊断", !unit.Diagnostics.HasErrors);

            var autoRunFailed = false;
            try
            {
                BilVm.Run(module);
            }
            catch (VmException ex)
            {
                autoRunFailed = true;
                TestHarness.CheckTrue("多入口报文提示 --entry-point",
                    ex.Message.Contains("--entry-point"), ex.Message);
                TestHarness.CheckTrue("多入口报文列出候选符号",
                    ex.Message.Contains("$main()@.i32") && ex.Message.Contains("$other()@.i32"),
                    ex.Message);
            }
            TestHarness.CheckTrue("多入口缺省运行抛 VmException", autoRunFailed);

            var other = BilVm.Run(module, 0, "$other()@.i32");
            CheckOk("显式选择 other 无异常", other);
            CheckI32("other() 返回 2", other, 2);
            var main = BilVm.Run(module, 0, "$main()@.i32");
            CheckOk("显式选择 main 无异常", main);
            CheckI32("main() 返回 1", main, 1);

            var badSymbolFailed = false;
            try
            {
                BilVm.Run(module, 0, "$nope()@.i32");
            }
            catch (VmException ex)
            {
                badSymbolFailed = true;
                TestHarness.CheckTrue("非 entrypoint 符号报文",
                    ex.Message.Contains("不是 entrypoint"), ex.Message);
            }
            TestHarness.CheckTrue("--entry-point 指定非入口符号抛 VmException", badSymbolFailed);
        }

        // ===== 全局函数 Method wrapper（§14.4 修复——此前被静默忽略）=====

        // 全局函数经每命名空间 singleton 宿主（..globals.host）获得与静态
        // 方法 companion 相同的 wrapper 链；发射形状断言宿主声明与安装指令
        private static void TestGlobalMethodWrapperEndToEnd()
        {
            var source =
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "@Trace()\n" +
                "pub func heavy(): i32 { return 21 }\n" +
                "pub func main(): i32 {\n" +
                "    var r = heavy()\n" +
                "    return (r * 2)\n" +
                "}\n";
            var (unit, module, text) = BilTestHarness.EmitBilUnit(source);
            TestHarness.CheckTrue("全局函数 wrapper 编译无诊断", !unit.Diagnostics.HasErrors);
            TestHarness.CheckTrue("合成宿主 singleton 声明",
                text.Contains(".type ..globals.host = class pub singleton shared compiler-generated"),
                text);
            TestHarness.CheckTrue("宿主 ..init.wrapper 安装 new.wrapper.method",
                text.Contains("new.wrapper.method fn(..globals.host$heavy()@.i32) type(Trace)"),
                text);
            var result = BilVm.Run(module);
            CheckOk("全局函数 Method wrapper 执行", result);
            TestHarness.Check("proxy 已执行（trace 打印）", result.Stdout, "trace\n");
            CheckI32("heavy()*2 返回 42", result, 42);
        }

        // 双层 wrapper 的 outer→inner 顺序与参数透传（与实例/静态方法同口径）
        private static void TestGlobalMethodWrapperDoubleLayerOrder()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"A\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"B\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@A\n" +
                "@B\n" +
                "pub func work(x: i32): i32 {\n" +
                "    core.io.Console.println(\"body\")\n" +
                "    return x\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return work(42)\n" +
                "}\n");
            CheckOk("全局函数双 wrapper 顺序", result);
            TestHarness.Check("全局函数双 wrapper stdout", result.Stdout,
                "A\n" +
                "B\n" +
                "body\n");
            CheckI32("全局函数双 wrapper 透传 42", result, 42);
        }

        // 命名空间内的全局函数 wrapper：宿主 singleton 落在该命名空间
        //（与 @EntryPoint 命名空间 main 同场景——切片/宿主归属同覆盖）
        private static void TestGlobalMethodWrapperInNamespace()
        {
            var result = Run(
                "namespace app\n" +
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper T {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"proxied\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@T\n" +
                "pub func helper(x: i32): i32 { return (x * 2) }\n" +
                "@EntryPoint\n" +
                "pub func main(): i32 {\n" +
                "    return helper(21)\n" +
                "}\n");
            CheckOk("命名空间全局函数 wrapper", result);
            TestHarness.Check("proxy 已执行", result.Stdout, "proxied\n");
            CheckI32("helper(21) 返回 42", result, 42);
        }

        // .proxy.call f)：get.self 直构模块——Method wrapper 的 .proxy.call
        // 模板 fn 内 get.self 产出宿主（.this 为 wrapper 实例）。
        private static void TestMethodWrapperGetSelfDirectModule()
        {
            var module = MethodWrapperGetSelfModule();
            var host = new VmObject("Host", valueType: false);
            host.WriteField("Host#n@.i32", new VmI32(42));
            var wrapper = new VmObject("Timed", valueType: true) { Host = host };
            var result = RunPrepared(module, "Timed$$.proxy.call(x:.i32)@.i32",
                new VmValue[] { wrapper, new VmI32(0) });
            CheckOk("Method wrapper get.self 直构", result);
            CheckI32("self.n == 42", result, 42);
        }

        // .proxy.call g)：!= 经 .proxy.opr.equals 链取反——直构模块
        // WrappedVecNeModule：proxy 恒 true → != 得 false。
        private static void TestMethodWrapperNotEqualsViaOprEqualsDirectModule()
        {
            var result = BilVm.Run(WrappedVecNeModule());
            CheckOk("WrappedVecNeModule 直构", result);
            CheckBool("proxy equals 恒 true → != 取反 false", result, false);
        }

        // 异常 getMessage 三场景：失败 cast 的 CastException 消息含类型信息；
        // wrapper 降级未路由的 NoSuchMethodException 消息含请求 symbol；
        // 用户子类 override getMessage 返回自定义串并多态打印。
        private static void TestExceptionGetMessage()
        {
            var cast = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: Animal = new Animal()\n" +
                "        var d = (a as Dog)\n" +
                "        return 0\n" +
                "    } catch (e: core.CastException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("CastException 被 catch", cast);
            CheckI32("CastException catch 返回 7", cast, 7);
            TestHarness.CheckTrue("getMessage 含源类型",
                cast.Stdout.Contains("Animal") == true, cast.Stdout);
            TestHarness.CheckTrue("getMessage 含目标类型",
                cast.Stdout.Contains("Dog") == true, cast.Stdout);

            const string service =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n";
            var downgrade = Run(service +
                "pub func main(): i32 {\n" +
                "    var service = new Service()\n" +
                "    try {\n" +
                "        service.fetchUserById(42)\n" +
                "        return 0\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("NoSuchMethodException 被 catch", downgrade);
            CheckI32("NoSuchMethodException catch 返回 7", downgrade, 7);
            TestHarness.CheckTrue("getMessage 含请求 symbol",
                downgrade.Stdout.Contains("Service$fetchUserById") == true,
                downgrade.Stdout);

            var custom = Run(
                "pub open class MyException : core.Exception {\n" +
                "    pub init()\n" +
                "    pub override func getMessage(): String { return \"custom-message\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new MyException()\n" +
                "        return 0\n" +
                "    } catch (e: core.Exception) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("用户异常 override getMessage 被 catch", custom);
            TestHarness.Check("多态 getMessage stdout", custom.Stdout, "custom-message\n");
            CheckI32("用户异常 catch 返回 7", custom, 7);
        }

        // 整数除零（SYNTAX §8.1 / BIL §11.2）：抛语言级
        // core.DividedByZeroException——try/catch 可捕获并读 getMessage；
        // 未捕获则异常对象沿帧链传播；u64 同例；float/double 除零保持
        // IEEE 754（inf/NaN），不抛。
        private static void TestIntegerDivisionByZero()
        {
            var caught = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: i32 = 10\n" +
                "        var b: i32 = 0\n" +
                "        var c = (a / b)\n" +
                "        return 0\n" +
                "    } catch (e: core.DividedByZeroException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("i32 除零被 catch", caught);
            CheckI32("除零 catch 返回 7", caught, 7);
            TestHarness.Check("除零 getMessage stdout", caught.Stdout, "整数除以零\n");

            var uncaught = Run(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 10\n" +
                "    var b: i32 = 0\n" +
                "    return (a / b)\n" +
                "}\n");
            TestHarness.CheckTrue("未捕获除零抛 DividedByZeroException",
                uncaught.Exception?.ExceptionObject is VmObject divObj
                && divObj.TypeRef.Contains("DividedByZeroException")
                && uncaught.Exception.Message.Contains("整数除以零"),
                uncaught.Exception?.ToString() ?? "<null>");
            // MW9b：顶层未捕获格式对齐 native reporter「{类型全名}: {message}」
            TestHarness.Check("未捕获除零顶层格式",
                uncaught.Exception?.Message ?? "",
                "core::DividedByZeroException: 整数除以零");

            var u64 = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: u64 = 10UL\n" +
                "        var b: u64 = 0UL\n" +
                "        var c = (a / b)\n" +
                "        return 0\n" +
                "    } catch (e: core.DividedByZeroException) {\n" +
                "        return 9\n" +
                "    }\n" +
                "}\n");
            CheckOk("u64 除零被 catch", u64);
            CheckI32("u64 除零 catch 返回 9", u64, 9);

            var f64 = Run(
                "pub func main(): double {\n" +
                "    var a: double = 1.0\n" +
                "    var b: double = 0.0\n" +
                "    return (a / b)\n" +
                "}\n");
            CheckOk("double 除零不抛", f64);
            TestHarness.CheckTrue("double 除零得 +Inf",
                f64.ReturnValue is VmF64 inf && double.IsPositiveInfinity(inf.Value),
                f64.ReturnValue?.ToStandardText() ?? "<null>");
            var nan = Run(
                "pub func main(): double {\n" +
                "    var a: double = 0.0\n" +
                "    var b: double = 0.0\n" +
                "    return (a / b)\n" +
                "}\n");
            CheckOk("double 零除零不抛", nan);
            TestHarness.CheckTrue("double 零除零得 NaN",
                nan.ReturnValue is VmF64 n && double.IsNaN(n.Value),
                nan.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // lambda Method wrapper VM 端到端：specific 环绕 + 改返回值；状态
        // 持久；捕获 lambda 共存；双 Method wrapper 顺序；init 实参形态
        //（实参为外层局部，验证透传）。
        private static void TestLambdaMethodWrapperEndToEnd()
        {
            var surround = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"before\")\n" +
                "        var r = inner(x)\n" +
                "        core.io.Console.println(\"after\")\n" +
                "        return (((r as i32) + 1) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{ @Timed (x: i32): i32 -> (x + 1) }\n" +
                "    return f(41)\n" +
                "}\n");
            CheckOk("lambda Method wrapper 环绕", surround);
            TestHarness.Check("lambda 环绕 stdout", surround.Stdout,
                "before\n" +
                "after\n");
            CheckI32("lambda 经 wrapper 返回 43", surround, 43);

            var state = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Counted {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        calls = (calls + 1)\n" +
                "        var r = inner(x)\n" +
                "        return (((r as i32) + calls) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{ @Counted (x: i32): i32 -> (x * 10) }\n" +
                "    var a = f(1)\n" +
                "    var b = f(1)\n" +
                "    return ((a * 100) + b)\n" +
                "}\n");
            CheckOk("lambda Method wrapper 状态持久", state);
            CheckI32("lambda 第 1 次 11、第 2 次 12 → 1112", state, 1112);

            var captured = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var base = 40\n" +
                "    var f = func{ @Timed (x: i32): i32 -> (x + base) }\n" +
                "    return f(2)\n" +
                "}\n");
            CheckOk("捕获 lambda + Method wrapper 共存", captured);
            CheckI32("base 40 + x 2 经 wrapper 返回 42", captured, 42);

            var doubleLayer = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"A\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"B\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{ @A @B (x: i32): i32 -> (x + 1) }\n" +
                "    return f(41)\n" +
                "}\n");
            CheckOk("lambda 双 Method wrapper", doubleLayer);
            TestHarness.Check("lambda 双 wrapper stdout", doubleLayer.Stdout,
                "A\n" +
                "B\n");
            CheckI32("lambda 双 wrapper 返回 42", doubleLayer, 42);

            var initArg = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Tagged {\n" +
                "    pub var tag: String\n" +
                "    pub init(t: String) { tag = t }\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"tag=\" + tag)\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var tag = \"hi\"\n" +
                "    var f = func{ @Tagged(tag) (x: i32): i32 -> (x + 1) }\n" +
                "    return f(41)\n" +
                "}\n");
            CheckOk("lambda Method wrapper init 实参透传", initArg);
            TestHarness.Check("init 实参 stdout", initArg.Stdout, "tag=hi\n");
            CheckI32("init 实参透传后返回 42", initArg, 42);
        }

        private static BilModule IndirectBoxModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_7", BilScalarType.I32, "7"));
            var box = new BilTypeDeclaration("Box", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "Box#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticField,
                "Box#.static.tag@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, "Box$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(box);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var init = new BilFunction("Box$init()@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Box"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "v"));
            main.Vars.Add(new BilVarDeclaration("Box", "obj"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Box>", "tid"));
            main.Vars.Add(new BilVarDeclaration(".fieldid<Box, .i32, instance>", "fid"));
            main.Vars.Add(new BilVarDeclaration(".fieldid<Box, .i32, static>", "sfid"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("v")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Box"), BilOp.Var("tid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#n@.i32"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#.static.tag@.i32"),
                BilOp.Var("sfid")));
            entry.Instructions.Add(new NewIndirectInstruction(BilOp.Var("tid"), BilOp.Var("obj"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new SetFieldIndirectInstruction(BilOp.Var("v"), BilOp.Var("obj"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new SetFieldStaticIndirectInstruction(BilOp.Var("v"),
                BilOp.Var("tid"), BilOp.Var("sfid")));
            entry.Instructions.Add(new GetFieldIndirectInstruction(BilOp.Var("obj"), BilOp.Var("r"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule VectorPlusModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_1", BilScalarType.I32, "1"));
            module.Resources.Add(new BilScalarResource("R_3", BilScalarType.I32, "3"));
            var vec = new BilTypeDeclaration("Vec", BilTypeKind.Struct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "Vec#x@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "Vec#y@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$init(x:.i32,y:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$$plus(other:Vec)@Vec",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("plus"),
                }));
            module.LocalSymbols.Add(vec);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var init = new BilFunction("Vec$init(x:.i32,y:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Vec"));
            init.Args.Add(new BilArgDeclaration("x", ".i32"));
            init.Args.Add(new BilArgDeclaration("y", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("x"),
                BilOp.Var(".this"), BilOp.Field("Vec#x@.i32")));
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("y"),
                BilOp.Var(".this"), BilOp.Field("Vec#y@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);
            var plus = new BilFunction("Vec$$plus(other:Vec)@Vec");
            plus.Args.Add(new BilArgDeclaration(".return", "Vec"));
            plus.Args.Add(new BilArgDeclaration(".this", "Vec"));
            plus.Args.Add(new BilArgDeclaration("other", "Vec"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "ax"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "bx"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "sx"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "ay"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "by"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "sy"));
            plus.Vars.Add(new BilVarDeclaration("Vec", "r"));
            var plusEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            plusEntry.Instructions.Add(new GetFieldInstruction(BilOp.Var(".this"),
                BilOp.Var("ax"), BilOp.Field("Vec#x@.i32")));
            plusEntry.Instructions.Add(new GetFieldInstruction(BilOp.Var("other"),
                BilOp.Var("bx"), BilOp.Field("Vec#x@.i32")));
            plusEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("ax"), BilOp.Var("bx"), BilOp.Var("sx")));
            plusEntry.Instructions.Add(new GetFieldInstruction(BilOp.Var(".this"),
                BilOp.Var("ay"), BilOp.Field("Vec#y@.i32")));
            plusEntry.Instructions.Add(new GetFieldInstruction(BilOp.Var("other"),
                BilOp.Var("by"), BilOp.Field("Vec#y@.i32")));
            plusEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("ay"), BilOp.Var("by"), BilOp.Var("sy")));
            plusEntry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"), BilOp.Var("r"),
                new[] { BilOp.Var("sx"), BilOp.Var("sy") }));
            plusEntry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            plus.Blocks.Add(plusEntry);
            module.Functions.Add(plus);
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "one"));
            main.Vars.Add(new BilVarDeclaration(".i32", "three"));
            main.Vars.Add(new BilVarDeclaration("Vec", "a"));
            main.Vars.Add(new BilVarDeclaration("Vec", "b"));
            main.Vars.Add(new BilVarDeclaration("Vec", "c"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("one")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("three")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"), BilOp.Var("a"),
                new[] { BilOp.Var("one"), BilOp.Var("one") }));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"), BilOp.Var("b"),
                new[] { BilOp.Var("three"), BilOp.Var("one") }));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("c")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("c"), BilOp.Var("x"),
                BilOp.Field("Vec#x@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // Entity operator 派发直构：Vec 带 wrapped(W)，W 只有 wildcard
        // .proxy.opr.*。frontend 的 `+` 不查用户 operator（TestUserOperatorAdd
        // 同因），此处直接发 add 指令验证 `+` 命中 .proxy.opr.*——proxy 直接
        // 返回 (99,0) 的 Vec，链末原始 plus 不会被走到。
        private static BilModule WrappedVecOperatorProxyModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_99", BilScalarType.I32, "99"));
            module.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "0"));

            var w = new BilTypeDeclaration("W", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            w.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "W$$.proxy.opr.*(symbol:.string)@Vec",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilOperatorModifier(".proxy.opr.*"),
                    new BilWrapperProxyModifier(BilProxyKind.Wildcard),
                }));
            module.LocalSymbols.Add(w);

            var vec = new BilTypeDeclaration("Vec", BilTypeKind.Struct,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich),
                new BilWrappedModifier("W"));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Vec#x@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Vec#y@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$init(x:.i32,y:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$..init.wrapper()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$$plus(other:Vec)@Vec",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("plus"),
                }));
            module.LocalSymbols.Add(vec);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var proxy = new BilFunction("W$$.proxy.opr.*(symbol:.string)@Vec");
            proxy.Args.Add(new BilArgDeclaration(".return", "Vec"));
            proxy.Args.Add(new BilArgDeclaration(".this", "W"));
            proxy.Args.Add(new BilArgDeclaration("symbol", ".string"));
            proxy.Args.Add(new BilArgDeclaration(".kwargs.namedArgs",
                ".array<.pair<.string, .any>>"));
            proxy.Args.Add(new BilArgDeclaration(".vargs.unnamedArgs", ".array<.any>"));
            proxy.Vars.Add(new BilVarDeclaration(".i32", "n99"));
            proxy.Vars.Add(new BilVarDeclaration(".i32", "z0"));
            proxy.Vars.Add(new BilVarDeclaration("Vec", "r"));
            var proxyEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            proxyEntry.Instructions.Add(new LoadInstruction(module.Resources[0],
                BilOp.Var("n99")));
            proxyEntry.Instructions.Add(new LoadInstruction(module.Resources[1],
                BilOp.Var("z0")));
            proxyEntry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"),
                BilOp.Var("r"), new[] { BilOp.Var("n99"), BilOp.Var("z0") }));
            proxyEntry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            proxy.Blocks.Add(proxyEntry);
            module.Functions.Add(proxy);

            var init = new BilFunction("Vec$init(x:.i32,y:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Vec"));
            init.Args.Add(new BilArgDeclaration("x", ".i32"));
            init.Args.Add(new BilArgDeclaration("y", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("x"),
                BilOp.Var(".this"), BilOp.Field("Vec#x@.i32")));
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("y"),
                BilOp.Var(".this"), BilOp.Field("Vec#y@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            var initWrapper = new BilFunction("Vec$..init.wrapper()@.void");
            initWrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            initWrapper.Args.Add(new BilArgDeclaration(".this", "Vec"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new NewWrapperEntityInstruction(BilOp.Type("W"),
                Array.Empty<BilVariableOperand>()));
            wrapperEntry.Instructions.Add(new RetInstruction());
            initWrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(initWrapper);

            var plus = new BilFunction("Vec$$plus(other:Vec)@Vec");
            plus.Args.Add(new BilArgDeclaration(".return", "Vec"));
            plus.Args.Add(new BilArgDeclaration(".this", "Vec"));
            plus.Args.Add(new BilArgDeclaration("other", "Vec"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "z0"));
            plus.Vars.Add(new BilVarDeclaration("Vec", "r"));
            var plusEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            plusEntry.Instructions.Add(new LoadInstruction(module.Resources[1],
                BilOp.Var("z0")));
            plusEntry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"),
                BilOp.Var("r"), new[] { BilOp.Var("z0"), BilOp.Var("z0") }));
            plusEntry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            plus.Blocks.Add(plusEntry);
            module.Functions.Add(plus);

            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "n99"));
            main.Vars.Add(new BilVarDeclaration(".i32", "z0"));
            main.Vars.Add(new BilVarDeclaration("Vec", "a"));
            main.Vars.Add(new BilVarDeclaration("Vec", "b"));
            main.Vars.Add(new BilVarDeclaration("Vec", "c"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0],
                BilOp.Var("n99")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1],
                BilOp.Var("z0")));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Vec"),
                BilOp.Var("a"), Array.Empty<BilVariableOperand>(),
                new[] { BilOp.Var("n99"), BilOp.Var("z0") }));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Vec"),
                BilOp.Var("b"), Array.Empty<BilVariableOperand>(),
                new[] { BilOp.Var("z0"), BilOp.Var("n99") }));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("c")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("c"),
                BilOp.Var("x"), BilOp.Field("Vec#x@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // != 直构：Vec 带 wrapped(W)，W 的 .proxy.opr.equals 恒 true；`a != b`
        // 由 equals 取反推导，因此结果为 false（原始 equals 返回 false 但不会
        // 被走到）。
        private static BilModule WrappedVecNeModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_1", BilScalarType.I32, "1"));
            module.Resources.Add(new BilScalarResource("R_2", BilScalarType.I32, "2"));
            module.Resources.Add(new BilScalarResource("R_TRUE", BilScalarType.Bool, "true"));
            module.Resources.Add(new BilScalarResource("R_FALSE", BilScalarType.Bool, "false"));

            var w = new BilTypeDeclaration("W", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            w.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "W$$.proxy.opr.equals(other:Vec)@.bool",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilOperatorModifier(".proxy.opr.equals"),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                }));
            module.LocalSymbols.Add(w);

            var vec = new BilTypeDeclaration("Vec", BilTypeKind.Struct,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich),
                new BilWrappedModifier("W"));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Vec#x@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Vec#y@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$init(x:.i32,y:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$..init.wrapper()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$$equals(other:Vec)@.bool",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("equals"),
                }));
            module.LocalSymbols.Add(vec);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.bool",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var proxy = new BilFunction("W$$.proxy.opr.equals(other:Vec)@.bool");
            proxy.Args.Add(new BilArgDeclaration(".return", ".bool"));
            proxy.Args.Add(new BilArgDeclaration(".this", "W"));
            proxy.Args.Add(new BilArgDeclaration("other", "Vec"));
            proxy.Vars.Add(new BilVarDeclaration(".bool", "t"));
            var proxyEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            proxyEntry.Instructions.Add(new LoadInstruction(module.Resources[2],
                BilOp.Var("t")));
            proxyEntry.Instructions.Add(new RetInstruction(BilOp.Var("t")));
            proxy.Blocks.Add(proxyEntry);
            module.Functions.Add(proxy);

            var init = new BilFunction("Vec$init(x:.i32,y:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Vec"));
            init.Args.Add(new BilArgDeclaration("x", ".i32"));
            init.Args.Add(new BilArgDeclaration("y", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("x"),
                BilOp.Var(".this"), BilOp.Field("Vec#x@.i32")));
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("y"),
                BilOp.Var(".this"), BilOp.Field("Vec#y@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            var initWrapper = new BilFunction("Vec$..init.wrapper()@.void");
            initWrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            initWrapper.Args.Add(new BilArgDeclaration(".this", "Vec"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new NewWrapperEntityInstruction(BilOp.Type("W"),
                Array.Empty<BilVariableOperand>()));
            wrapperEntry.Instructions.Add(new RetInstruction());
            initWrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(initWrapper);

            var equals = new BilFunction("Vec$$equals(other:Vec)@.bool");
            equals.Args.Add(new BilArgDeclaration(".return", ".bool"));
            equals.Args.Add(new BilArgDeclaration(".this", "Vec"));
            equals.Args.Add(new BilArgDeclaration("other", "Vec"));
            equals.Vars.Add(new BilVarDeclaration(".bool", "f"));
            var equalsEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            equalsEntry.Instructions.Add(new LoadInstruction(module.Resources[3],
                BilOp.Var("f")));
            equalsEntry.Instructions.Add(new RetInstruction(BilOp.Var("f")));
            equals.Blocks.Add(equalsEntry);
            module.Functions.Add(equals);

            var main = new BilFunction("$main()@.bool");
            main.Args.Add(new BilArgDeclaration(".return", ".bool"));
            main.Vars.Add(new BilVarDeclaration(".i32", "one"));
            main.Vars.Add(new BilVarDeclaration(".i32", "two"));
            main.Vars.Add(new BilVarDeclaration("Vec", "a"));
            main.Vars.Add(new BilVarDeclaration("Vec", "b"));
            main.Vars.Add(new BilVarDeclaration(".bool", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0],
                BilOp.Var("one")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1],
                BilOp.Var("two")));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Vec"),
                BilOp.Var("a"), Array.Empty<BilVariableOperand>(),
                new[] { BilOp.Var("one"), BilOp.Var("two") }));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Vec"),
                BilOp.Var("b"), Array.Empty<BilVariableOperand>(),
                new[] { BilOp.Var("one"), BilOp.Var("two") }));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.CmpNe,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("r")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // Method wrapper .proxy.call 的 get.self 直构：proxy 模板 fn 的
        // .this 是 wrapper 实例，get.self 产出已安装的宿主（Host.n == 42）。
        private static BilModule MethodWrapperGetSelfModule()
        {
            var module = new BilModule();
            var timed = new BilTypeDeclaration("Timed", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            timed.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Timed$$.proxy.call(x:.i32)@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilOperatorModifier(".proxy.call"),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                }));
            module.LocalSymbols.Add(timed);

            var host = new BilTypeDeclaration("Host", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Host#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(host);

            var proxy = new BilFunction("Timed$$.proxy.call(x:.i32)@.i32");
            proxy.Args.Add(new BilArgDeclaration(".return", ".i32"));
            proxy.Args.Add(new BilArgDeclaration(".this", "Timed"));
            proxy.Args.Add(new BilArgDeclaration("x", ".i32"));
            proxy.Vars.Add(new BilVarDeclaration("Host", "s"));
            proxy.Vars.Add(new BilVarDeclaration(".i32", "n"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new GetSelfInstruction(BilOp.Var("s")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("s"),
                BilOp.Var("n"), BilOp.Field("Host#n@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("n")));
            proxy.Blocks.Add(entry);
            module.Functions.Add(proxy);
            return module;
        }

        // 未绑定语境下 .generic 参与 cast 的负例直构：fn 没有同名 .generic
        // hidden 实参槽位，执行期类型解析必须抛 VmException（绝不恒等放行）。
        private static BilModule UnboundGenericCastModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_1", BilScalarType.I32, "1"));
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "n"));
            main.Vars.Add(new BilVarDeclaration(".generic<$.generic.T>", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0],
                BilOp.Var("n")));
            entry.Instructions.Add(new CastInstruction(BilOp.Var("n"), BilOp.Var("r"),
                BilOp.Type(".generic<$.generic.T>"), isSafe: false));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("n")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static void TestIfElse()
        {
            var thenBranch = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    if (x == 1) {\n" +
                "        return 2\n" +
                "    } else {\n" +
                "        return 3\n" +
                "    }\n" +
                "}\n");
            CheckOk("if then", thenBranch);
            CheckI32("if then 返回 2", thenBranch, 2);
            var elseBranch = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    if (x == 1) {\n" +
                "        return 2\n" +
                "    } else {\n" +
                "        return 3\n" +
                "    }\n" +
                "}\n");
            CheckOk("if else", elseBranch);
            CheckI32("if else 返回 3", elseBranch, 3);
            var noElse = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    if (false) { x = 9 }\n" +
                "    return x\n" +
                "}\n");
            CheckOk("if 无 else", noElse);
            CheckI32("if 无 else 不改 x", noElse, 1);
            var expr = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    return if ((x > 0)) { 4 } else { 5 }\n" +
                "}\n");
            CheckOk("if 表达式", expr);
            CheckI32("if 表达式 then", expr, 4);
            var seq = Run(
                "pub func main(): i32 {\n" +
                "    seq {\n" +
                "        core.io.Console.println(\"a\")\n" +
                "    }\n" +
                "    core.io.Console.println(\"b\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("call blk seq", seq);
            TestHarness.Check("seq stdout", seq.Stdout, "a\nb\n");
        }

        private static void TestWhileAndDoWhile()
        {
            var whileLoop = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    var n: i32 = 0\n" +
                "    while (i < 4) {\n" +
                "        n = (n + i)\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("while", whileLoop);
            CheckI32("while 0+1+2+3", whileLoop, 6);
            var zero = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    while (false) { n = 1 }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("while 零次", zero);
            CheckI32("while false 不进体", zero, 0);
            var doWhile = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    do {\n" +
                "        i = (i + 1)\n" +
                "    } while (i < 3)\n" +
                "    return i\n" +
                "}\n");
            CheckOk("do-while", doWhile);
            CheckI32("do-while 至少一次到 3", doWhile, 3);
            var doOnce = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    do { i = 7 } while (false)\n" +
                "    return i\n" +
                "}\n");
            CheckOk("do-while 一次", doOnce);
            CheckI32("loop.rev body 至少一次", doOnce, 7);
        }

        private static void TestForRangeAndBreakContinue()
        {
            var range = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    for (i in 0 to 4) {\n" +
                "        n = (n + i)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("for 范围", range);
            CheckI32("for [0,4) 求和", range, 6);
            var brk = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        i = (i + 1)\n" +
                "        n = (n + 1)\n" +
                "        if (i == 3) { break }\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("break", brk);
            CheckI32("break 在 3", brk, 3);
            var cont = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 4) {\n" +
                "        i = (i + 1)\n" +
                "        if (i == 2) { continue }\n" +
                "        n = (n + i)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("continue", cont);
            CheckI32("continue 跳过 2", cont, 8);
        }

        // 范围循环经约束：动态派发命中类型自己的 EnumerateInRange
        private static void TestForRangeConstraintDispatch()
        {
            var viaI32 = Run(
                "pub func sum\\<T extends i32>(a: T, b: T): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return sum(1, 5)\n" +
                "}\n");
            CheckOk("T extends i32 范围循环", viaI32);
            CheckI32("sum(1,5)=10（[1,5)）", viaI32, 10);

            var viaStep = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n" +
                "pub func count\\<T extends Step>(a: T, b: T): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return count(new Step(1), new Step(3))\n" +
                "}\n");
            CheckOk("T extends Step 自定义枚举", viaStep);
            CheckI32("count(Step(1),Step(3))=15（[1,6)）", viaStep, 15);

            var direct = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in new Step(1) to new Step(3)) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckOk("直接自定义类型范围循环", direct);
            CheckI32("Step [1,6) 求和 15", direct, 15);

            var stepToI32 = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: i32): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, end)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in new Step(1) to 5) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckOk("Step to i32 不同型两端", stepToI32);
            CheckI32("Step(1) to 5 = 10（[1,5)）", stepToI32, 10);

            var overloadI32 = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: i32): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, end)\n" +
                "    }\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in new Step(1) to 5) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckOk("重载选 end: i32", overloadI32);
            CheckI32("Step(1) to 5 走 i32 重载 = 10", overloadI32, 10);

            var overloadStep = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: i32): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, end)\n" +
                "    }\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in new Step(1) to new Step(3)) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckOk("重载选 end: Step", overloadStep);
            CheckI32("Step(1) to Step(3) 走 Step 重载 = 15", overloadStep, 15);
        }

        // 具名导入泛型类型定义后实例化（§15.2）
        private static void TestNamedImportGenericType()
        {
            var result = Run(
                "import core.Pair\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Pair\\<i32, String>(7, \"ok\")\n" +
                "    return p.key\n" +
                "}\n");
            CheckOk("import core.Pair 后实例化", result);
            CheckI32("Pair.key == 7", result, 7);
        }

        private static void TestNamedBreakContinue()
        {
            var namedBreak = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    for (i in 0 to 3) named outer {\n" +
                "        for (j in 0 to 3) {\n" +
                "            n = (n + 1)\n" +
                "            break@outer\n" +
                "        }\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("named break", namedBreak);
            CheckI32("break@outer 一次", namedBreak, 1);
            var namedContinue = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    for (i in 0 to 3) named outer {\n" +
                "        n = (n + 1)\n" +
                "        for (j in 0 to 3) {\n" +
                "            continue@outer\n" +
                "        }\n" +
                "        n = (n + 100)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("named continue", namedContinue);
            CheckI32("continue@outer 跳过 100", namedContinue, 3);
        }

        private static void TestSwitchStatementAndExpression()
        {
            var stmt = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 2\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 10 }\n" +
                "        (2) -> { return 20 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckOk("switch 语句", stmt);
            CheckI32("switch 命中 2", stmt, 20);
            var def = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 9\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 10 }\n" +
                "        (2) -> { return 20 }\n" +
                "        default -> { return 7 }\n" +
                "    }\n" +
                "}\n");
            CheckOk("switch default", def);
            CheckI32("switch 走 default", def, 7);
            var expr = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 2\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 10 }\n" +
                "        (2) -> { 20 }\n" +
                "        default -> { 0 }\n" +
                "    }\n" +
                "}\n");
            CheckOk("switch 表达式", expr);
            CheckI32("switch 表达式 20", expr, 20);
            var pattern = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 12\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 1 }\n" +
                "        (_ > 10) -> { 2 }\n" +
                "        default -> { 0 }\n" +
                "    }\n" +
                "}\n");
            CheckOk("switch pattern", pattern);
            CheckI32("pattern _ > 10", pattern, 2);
        }

        private static void TestConditionalExpectedTypeMaterialization()
        {
            var implicitPick = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub class Cat : Animal { pub init() {} }\n" +
                "pub func pick(flag: bool): Animal {\n" +
                "    return if (flag) { new Dog() } else { new Cat() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = pick(true)\n" +
                "    if (a is Dog) { return 1 } else { return 2 }\n" +
                "}\n");
            CheckOk("隐式 if 公共基类", implicitPick);
            CheckI32("隐式 if 命中 Dog", implicitPick, 1);
            var explicitPick = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub class Cat : Animal { pub init() {} }\n" +
                "pub func pick(flag: bool): Animal {\n" +
                "    return if (flag) { return@_ new Dog() } else { return@_ new Cat() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = pick(false)\n" +
                "    if (a is Cat) { return 3 } else { return 4 }\n" +
                "}\n");
            CheckOk("显式 return@ if 公共基类", explicitPick);
            CheckI32("显式 if 命中 Cat", explicitPick, 3);
            var switchPick = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub class Cat : Animal { pub init() {} }\n" +
                "pub func pick(flag: bool): Animal {\n" +
                "    return switch (flag) {\n" +
                "        (true) -> { new Dog() }\n" +
                "        default -> { new Cat() }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = pick(true)\n" +
                "    if (a is Dog) { return 5 } else { return 6 }\n" +
                "}\n");
            CheckOk("隐式 switch 公共基类", switchPick);
            CheckI32("隐式 switch 命中 Dog", switchPick, 5);
            var nullable = Run(
                "pub func pick(flag: bool): String? {\n" +
                "    return if (flag) { null } else { \"x\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = pick(true)\n" +
                "    var b = pick(false)\n" +
                "    if (a == null) {\n" +
                "        if (b == null) { return 0 } else { return 8 }\n" +
                "    }\n" +
                "    return 9\n" +
                "}\n");
            CheckOk("隐式 if 可空", nullable);
            CheckI32("null / \"x\" 物化", nullable, 8);
        }

        private static void TestTryCatchFinally()
        {
            var caught = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.RuntimeException(\"boom\")\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        core.io.Console.println(\"catch\")\n" +
                "        return 7\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n");
            CheckOk("try 落 catch", caught);
            TestHarness.Check("catch+finally 顺序", caught.Stdout, "catch\nfin\n");
            CheckI32("catch 返回 7", caught, 7);
            var normal = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        core.io.Console.println(\"try\")\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("try 正常 + finally", normal);
            TestHarness.Check("正常路径 finally", normal.Stdout, "try\nfin\n");
            CheckI32("正常路径返回", normal, 1);
            var inherit = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.IOException(\"io\")\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 4\n" +
                "    }\n" +
                "}\n");
            CheckOk("catch 兼容子类", inherit);
            CheckI32("IOException 命中 RuntimeException", inherit, 4);
            var miss = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.IOException(\"io\")\n" +
                "        return 0\n" +
                "    } catch (_: core.CastException) {\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckTrue("未命中 catch 传播",
                miss.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("IOException"),
                miss.Exception?.ToString() ?? "<null>");
            var nested = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.IOException(\"io\")\n" +
                "        } catch (_: core.CastException) {\n" +
                "            core.io.Console.println(\"no\")\n" +
                "        }\n" +
                "        return 0\n" +
                "    } catch (_: core.IOException) {\n" +
                "        core.io.Console.println(\"io\")\n" +
                "        return 5\n" +
                "    }\n" +
                "}\n");
            CheckOk("嵌套 try", nested);
            TestHarness.Check("嵌套 catch 外层", nested.Stdout, "io\n");
            CheckI32("嵌套返回 5", nested, 5);
            var rethrow = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.IOException(\"io\")\n" +
                "    } catch (_: core.IOException) {\n" +
                "        throw new core.RuntimeException(\"re\")\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"f\")\n" +
                "    }\n" +
                "}\n");
            TestHarness.Check("catch 再抛仍跑 finally", rethrow.Stdout, "f\n");
            TestHarness.CheckTrue("finally 后传播新异常",
                rethrow.Exception?.ExceptionObject is VmObject re
                && re.TypeRef.Contains("RuntimeException"),
                rethrow.Exception?.ToString() ?? "<null>");
            var finThrow = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } finally(_) {\n" +
                "        throw new core.RuntimeException(\"fin\")\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckTrue("finally throw 覆盖 ret",
                finThrow.Exception?.ExceptionObject is VmObject ft
                && ft.TypeRef.Contains("RuntimeException"),
                finThrow.Exception?.ToString() ?? "<null>");
            var castCatch = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: Animal = new Animal()\n" +
                "        var d = (a as Dog)\n" +
                "        return 0\n" +
                "    } catch (_: core.CastException) {\n" +
                "        return 8\n" +
                "    }\n" +
                "}\n");
            CheckOk("CastException 可 catch", castCatch);
            CheckI32("cast 失败落 catch", castCatch, 8);
        }

        private static void TestThrowAcrossFunction()
        {
            var result = Run(
                "pub func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        boom()\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("throw 跨函数", result);
            CheckI32("跨函数 catch", result, 7);
            var uncaught = Run(
                "pub func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return boom()\n" +
                "}\n");
            TestHarness.CheckTrue("无人捕获则 Failed",
                uncaught.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("RuntimeException"),
                uncaught.Exception?.ToString() ?? "<null>");
        }

        private static void TestRetBreakContinueThroughFinally()
        {
            var ret = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 3\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n");
            CheckOk("ret 穿越 finally", ret);
            TestHarness.Check("ret 前 finally", ret.Stdout, "fin\n");
            CheckI32("ret 值保留", ret, 3);
            var brk = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        try {\n" +
                "            i = (i + 1)\n" +
                "            break\n" +
                "        } finally(_) {\n" +
                "            core.io.Console.println(\"f\")\n" +
                "        }\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            CheckOk("break 穿越 finally", brk);
            TestHarness.Check("break 前 finally", brk.Stdout, "f\n");
            CheckI32("break 后 i", brk, 1);
            var cont = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    var n: i32 = 0\n" +
                "    while (i < 3) {\n" +
                "        i = (i + 1)\n" +
                "        try {\n" +
                "            continue\n" +
                "        } finally(_) {\n" +
                "            n = (n + 1)\n" +
                "        }\n" +
                "        n = (n + 100)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("continue 穿越 finally", cont);
            CheckI32("continue 只跑 finally", cont, 3);
        }

        // ===== §16.5 推广：call/if/try region 的 breakid matching 消费
        // （手工模块——frontend 不生成引用这些 token 的 break）=====
        private static void TestRegionBreakIdConsumption()
        {
            // (a) call blk region：块内 break 命中自身 breakid 被消费，
            // 续 call 的后一条指令（break 后指令不执行、不外溢越界）
            var callModule = RegionModuleSkeleton(out var callMain, out var callEntry);
            var callSeq = new BilBlock("seq0");
            callMain.Blocks.Add(callSeq);
            callEntry.Instructions.Add(LoadI32(callModule, 0, "a"));
            callEntry.Instructions.Add(new CallBlockInstruction(callSeq, BilOp.Var("b0")));
            callEntry.Instructions.Add(LoadI32(callModule, 7, "x"));
            callEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("x")));
            callEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            callSeq.Instructions.Add(LoadI32(callModule, 5, "a"));
            callSeq.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            callSeq.Instructions.Add(LoadI32(callModule, 9, "a"));
            var callResult = BilVm.Run(callModule);
            CheckOk("call region break 消费", callResult);
            CheckI32("call 后续行（5+7）", callResult, 12);

            // (b1) if region：then 内 break 命中消费，续 if 后指令
            var thenModule = RegionModuleSkeleton(out var thenMain, out var thenEntry);
            var thenBlock = new BilBlock("if0-then");
            var elseBlock = new BilBlock("if0-else");
            thenMain.Blocks.Add(thenBlock);
            thenMain.Blocks.Add(elseBlock);
            thenEntry.Instructions.Add(new LoadInstruction(thenModule.Resources[10],
                BilOp.Var("c")));
            thenEntry.Instructions.Add(new IfInstruction(BilOp.Var("c"), thenBlock, elseBlock,
                BilOp.Var("b0")));
            thenEntry.Instructions.Add(LoadI32(thenModule, 7, "x"));
            thenEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("x")));
            thenEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            thenBlock.Instructions.Add(LoadI32(thenModule, 5, "a"));
            thenBlock.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            thenBlock.Instructions.Add(LoadI32(thenModule, 9, "a"));
            elseBlock.Instructions.Add(LoadI32(thenModule, 9, "a"));
            var thenResult = BilVm.Run(thenModule);
            CheckOk("if then break 消费", thenResult);
            CheckI32("if then 后续行（5+7）", thenResult, 12);

            // (b2) if region：else 内 break 命中消费
            var elseModule = RegionModuleSkeleton(out var elseMain, out var elseEntry);
            var then2 = new BilBlock("if0-then");
            var else2 = new BilBlock("if0-else");
            elseMain.Blocks.Add(then2);
            elseMain.Blocks.Add(else2);
            elseEntry.Instructions.Add(new LoadInstruction(elseModule.Resources[11],
                BilOp.Var("c")));
            elseEntry.Instructions.Add(new IfInstruction(BilOp.Var("c"), then2, else2,
                BilOp.Var("b0")));
            elseEntry.Instructions.Add(LoadI32(elseModule, 7, "x"));
            elseEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("x")));
            elseEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            then2.Instructions.Add(LoadI32(elseModule, 9, "a"));
            else2.Instructions.Add(LoadI32(elseModule, 5, "a"));
            else2.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            else2.Instructions.Add(LoadI32(elseModule, 9, "a"));
            var elseResult = BilVm.Run(elseModule);
            CheckOk("if else break 消费", elseResult);
            CheckI32("if else 后续行（5+7）", elseResult, 12);

            // (c1) try region：body 内 break tryId——finally 先执行，
            // 再在 try 边界消费
            var bodyModule = RegionModuleSkeleton(out var bodyMain, out var bodyEntry);
            var tryBody = new BilBlock("try0-body");
            var tryFin = new BilBlock("try0-finally");
            bodyMain.Blocks.Add(tryBody);
            bodyMain.Blocks.Add(tryFin);
            bodyEntry.Instructions.Add(new TryInstruction(tryBody, BilOp.Var("slot"),
                EmptyCatchTable(bodyModule), tryFin, BilOp.Var("b0")));
            bodyEntry.Instructions.Add(LoadI32(bodyModule, 7, "x"));
            bodyEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("f"), BilOp.Var("a")));
            bodyEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("a")));
            bodyEntry.Instructions.Add(new RetInstruction(BilOp.Var("a")));
            tryBody.Instructions.Add(LoadI32(bodyModule, 5, "a"));
            tryBody.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            tryBody.Instructions.Add(LoadI32(bodyModule, 9, "a"));
            tryFin.Instructions.Add(LoadI32(bodyModule, 3, "f"));
            var bodyResult = BilVm.Run(bodyModule);
            CheckOk("try body break 经 finally 消费", bodyResult);
            CheckI32("finally 执行且 break 消费（5+3+7）", bodyResult, 15);

            // (c2) try region：catch handler 内 break tryId——handler 完成
            // 后经 finally 在 try 边界消费
            var catchModule = RegionModuleSkeleton(out var catchMain, out var catchEntry);
            var catchBody = new BilBlock("try0-body");
            var catchHandler = new BilBlock("try0-catch0");
            var catchFin = new BilBlock("try0-finally");
            catchMain.Blocks.Add(catchBody);
            catchMain.Blocks.Add(catchHandler);
            catchMain.Blocks.Add(catchFin);
            catchEntry.Instructions.Add(new TryInstruction(catchBody, BilOp.Var("slot"),
                MyErrCatchTable(catchModule, catchHandler), catchFin, BilOp.Var("b0")));
            catchEntry.Instructions.Add(LoadI32(catchModule, 7, "x"));
            catchEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("h"), BilOp.Var("a")));
            catchEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("f"), BilOp.Var("a")));
            catchEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("a")));
            catchEntry.Instructions.Add(new RetInstruction(BilOp.Var("a")));
            catchBody.Instructions.Add(LoadI32(catchModule, 1, "a"));
            catchBody.Instructions.Add(new NewInstruction(BilOp.Type("MyErr"),
                BilOp.Var("obj"), Array.Empty<BilVariableOperand>()));
            catchBody.Instructions.Add(new ThrowInstruction(BilOp.Var("obj")));
            catchHandler.Instructions.Add(LoadI32(catchModule, 3, "h"));
            catchHandler.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            catchHandler.Instructions.Add(LoadI32(catchModule, 9, "h"));
            catchFin.Instructions.Add(LoadI32(catchModule, 5, "f"));
            var catchResult = BilVm.Run(catchModule);
            CheckOk("catch handler break 消费", catchResult);
            CheckI32("handler break 经 finally 消费（1+3+5+7）", catchResult, 16);

            // (c3) try region：finally 内 break tryId——finally 完成后在
            // try 边界消费（SavedCompletion=Normal 被 finally 的 Break 覆盖）
            var finModule = RegionModuleSkeleton(out var finMain, out var finEntry);
            var finBody = new BilBlock("try0-body");
            var finFin = new BilBlock("try0-finally");
            finMain.Blocks.Add(finBody);
            finMain.Blocks.Add(finFin);
            finEntry.Instructions.Add(new TryInstruction(finBody, BilOp.Var("slot"),
                EmptyCatchTable(finModule), finFin, BilOp.Var("b0")));
            finEntry.Instructions.Add(LoadI32(finModule, 7, "x"));
            finEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("f"), BilOp.Var("a")));
            finEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("a")));
            finEntry.Instructions.Add(new RetInstruction(BilOp.Var("a")));
            finBody.Instructions.Add(LoadI32(finModule, 5, "a"));
            finFin.Instructions.Add(LoadI32(finModule, 3, "f"));
            finFin.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            finFin.Instructions.Add(LoadI32(finModule, 9, "f"));
            var finResult = BilVm.Run(finModule);
            CheckOk("finally 内 break 消费", finResult);
            CheckI32("finally break 在 try 边界消费（5+3+7）", finResult, 15);

            // (c4) break tryId 不进 catch matching：body break 时 catch
            // 不触发（handler 不写 h），break 在 try 边界消费
            var noCatchModule = RegionModuleSkeleton(out var noCatchMain, out var noCatchEntry);
            var ncBody = new BilBlock("try0-body");
            var ncHandler = new BilBlock("try0-catch0");
            noCatchMain.Blocks.Add(ncBody);
            noCatchMain.Blocks.Add(ncHandler);
            noCatchEntry.Instructions.Add(LoadI32(noCatchModule, 0, "h"));
            noCatchEntry.Instructions.Add(new TryInstruction(ncBody, BilOp.Var("slot"),
                MyErrCatchTable(noCatchModule, ncHandler), null, BilOp.Var("b0")));
            noCatchEntry.Instructions.Add(LoadI32(noCatchModule, 7, "x"));
            noCatchEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("h"), BilOp.Var("x"), BilOp.Var("x")));
            noCatchEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            ncBody.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            ncHandler.Instructions.Add(LoadI32(noCatchModule, 9, "h"));
            var noCatchResult = BilVm.Run(noCatchModule);
            CheckOk("break 不进 catch matching", noCatchResult);
            CheckI32("catch 未触发且 break 消费（0+7）", noCatchResult, 7);
        }

        // ===== §16.7 finally(e) 基础语义：进入 finally 时仅 pending 为
        // Throw 才把异常对象写入 ExceptionSlot；Normal/Return/Break 等
        // 一切非 Throw completion 必须看到 null =====
        private static void TestFinallyExceptionSlotSemantics()
        {
            var normal = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "    } finally(e) {\n" +
                "        if (e == null) { core.io.Console.println(\"null\") }\n" +
                "        else { core.io.Console.println(\"exc\") }\n" +
                "    }\n" +
                "    return 5\n" +
                "}\n");
            CheckOk("Normal 进 finally(e)", normal);
            TestHarness.Check("Normal 路径 e 为 null", normal.Stdout, "null\n");
            CheckI32("Normal 后续行", normal, 5);

            var ret = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 3\n" +
                "    } finally(e) {\n" +
                "        if (e == null) { core.io.Console.println(\"null\") }\n" +
                "        else { core.io.Console.println(\"exc\") }\n" +
                "    }\n" +
                "}\n");
            CheckOk("Return 进 finally(e)", ret);
            TestHarness.Check("Return 路径 e 为 null", ret.Stdout, "null\n");
            CheckI32("Return 值保留", ret, 3);

            var brk = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        try {\n" +
                "            i = (i + 1)\n" +
                "            break\n" +
                "        } finally(e) {\n" +
                "            if (e == null) { core.io.Console.println(\"null\") }\n" +
                "            else { core.io.Console.println(\"exc\") }\n" +
                "        }\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            CheckOk("Break 进 finally(e)", brk);
            TestHarness.Check("Break 路径 e 为 null", brk.Stdout, "null\n");
            CheckI32("Break 后 i", brk, 1);

            // Throw 路径回归：未捕获异常进入 finally 时 e 为异常对象
            var thr = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.RuntimeException(\"x\")\n" +
                "        } finally(e) {\n" +
                "            if (e == null) { core.io.Console.println(\"null\") }\n" +
                "            else { core.io.Console.println(\"exc\") }\n" +
                "        }\n" +
                "    } catch (_: core.Exception) {\n" +
                "        return 1\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Throw 进 finally(e)", thr);
            TestHarness.Check("Throw 路径 e 为异常对象", thr.Stdout, "exc\n");
            CheckI32("外层 catch 捕获", thr, 1);
        }

        // region break 手工模块骨架：资源 R_0..R_9（i32 0-9）+ R_T/R_F
        //（bool true/false，下标 10/11）+ R_N（null，下标 12）；类型
        // MyErr class；main 变量 a/h/f/x（.i32）、c（.bool）、b0/b1/b2
        //（.breakid）、slot/obj（.any）；entry 为 entrypoint
        private static BilModule RegionModuleSkeleton(out BilFunction main, out BilBlock entry)
        {
            var module = new BilModule();
            for (var i = 0; i <= 9; i++)
            {
                module.Resources.Add(new BilScalarResource("R_" + i, BilScalarType.I32,
                    i.ToString()));
            }
            module.Resources.Add(new BilScalarResource("R_T", BilScalarType.Bool, "true"));
            module.Resources.Add(new BilScalarResource("R_F", BilScalarType.Bool, "false"));
            module.Resources.Add(new BilNullResource("R_N", ".any"));
            module.LocalSymbols.Add(new BilTypeDeclaration("MyErr", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public)));
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "a"));
            main.Vars.Add(new BilVarDeclaration(".i32", "h"));
            main.Vars.Add(new BilVarDeclaration(".i32", "f"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            main.Vars.Add(new BilVarDeclaration(".bool", "c"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b0"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b1"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b2"));
            main.Vars.Add(new BilVarDeclaration(".any", "slot"));
            main.Vars.Add(new BilVarDeclaration(".any", "obj"));
            entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static LoadInstruction LoadI32(BilModule module, int n, string variable) =>
            new LoadInstruction(module.Resources[n], BilOp.Var(variable));

        private static BilCatchTableResource EmptyCatchTable(BilModule module)
        {
            var table = new BilCatchTableResource("R_CT", Array.Empty<BilCatchEntry>());
            module.Resources.Add(table);
            return table;
        }

        private static BilCatchTableResource MyErrCatchTable(BilModule module, BilBlock handler)
        {
            var table = new BilCatchTableResource("R_CTE",
                new[] { new BilCatchEntry(new BilTypeOperand("MyErr"), handler) });
            module.Resources.Add(table);
            return table;
        }

        private static void TestUsingDisposeOrder()
        {
            var result = Run(
                "class Tracer implements core.IDisposable {\n" +
                "    pub var name: String\n" +
                "    pub init(_ -> name)\n" +
                "    pub override func dispose() {\n" +
                "        core.io.Console.println(name)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    seq using(const a = new Tracer(\"a\"))\n" +
                "    using(const b = new Tracer(\"b\")) {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("using 清理", result);
            TestHarness.Check("using 逆序 dispose", result.Stdout, "body\nb\na\n");
            CheckI32("using 返回", result, 0);
        }

        private static void TestLoopEnumeratorDirectModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "0"));
            module.Resources.Add(new BilScalarResource("R_3", BilScalarType.I32, "3"));
            module.Resources.Add(new BilScalarResource("R_1", BilScalarType.I32, "1"));
            module.Resources.Add(new BilScalarResource("R_10", BilScalarType.I32, "10"));
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "i"));
            main.Vars.Add(new BilVarDeclaration(".i32", "sum"));
            main.Vars.Add(new BilVarDeclaration(".i32", "one"));
            main.Vars.Add(new BilVarDeclaration(".i32", "ten"));
            main.Vars.Add(new BilVarDeclaration(".i32", "limit"));
            main.Vars.Add(new BilVarDeclaration(".bool", "c"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            var body = new BilBlock("loop0-body");
            var enumBlock = new BilBlock("loop0-enum");
            var judge = new BilBlock("loop0-judge");
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("i")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("sum")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[2], BilOp.Var("one")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[3], BilOp.Var("ten")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("limit")));
            entry.Instructions.Add(new LoopInstruction(BilOp.Var("c"), body, enumBlock, judge,
                BilOp.Var("b"), isRev: false));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("sum")));
            judge.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.CmpLt,
                BilOp.Var("i"), BilOp.Var("limit"), BilOp.Var("c")));
            body.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("sum"), BilOp.Var("ten"), BilOp.Var("sum")));
            enumBlock.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("i"), BilOp.Var("one"), BilOp.Var("i")));
            main.Blocks.Add(entry);
            main.Blocks.Add(body);
            main.Blocks.Add(enumBlock);
            main.Blocks.Add(judge);
            module.Functions.Add(main);
            var result = BilVm.Run(module);
            CheckOk("loop 三 block", result);
            CheckI32("judge/body/enum 协议", result, 30);
        }

        private static void TestAsyncAwaitResult()
        {
            var result = Run(
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = add(41)\n" +
                "    var n = await t\n" +
                "    return n\n" +
                "}\n");
            CheckOk("async await 取值", result);
            CheckI32("await add(41)", result, 42);
            var none = Run(
                "async func ping() { }\n" +
                "pub func main(): i32 {\n" +
                "    await ping()\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("await 无结果 Task", none);
            CheckI32("void Task 之后", none, 1);
        }

        private static void TestAwaitExceptionAndCompleted()
        {
            var caught = Run(
                "async func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 3\n" +
                "    }\n" +
                "}\n");
            CheckOk("await 异常传播", caught);
            CheckI32("await 点 catch", caught, 3);
            var twice = Run(
                "async func quick(): i32 { return 5 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = quick()\n" +
                "    var a = await t\n" +
                "    var b = await t\n" +
                "    return a + b\n" +
                "}\n");
            CheckOk("await 已完成 Task", twice);
            CheckI32("二次 await 不重跑", twice, 10);
        }

        private static void TestForkJoinAndFireAndForget()
        {
            var join = Run(
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "pub func main(): i32 {\n" +
                "    var a = add(1)\n" +
                "    var b = add(2)\n" +
                "    var c = add(3)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    var z = await c\n" +
                "    return ((x + y) + z)\n" +
                "}\n");
            CheckOk("fork/join", join);
            CheckI32("1+1 + 2+1 + 3+1", join, 9);
            var ghost = Run(
                "async func ghost() {\n" +
                "    throw new core.RuntimeException(\"bg\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    ghost()\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("fire-and-forget 后台异常经 quiescence 可见",
                ghost.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("RuntimeException"),
                ghost.Exception?.ToString() ?? "<null>");
            TestHarness.CheckTrue("fire-and-forget 入口仍返回",
                ghost.ReturnValue is VmI32 n && n.Value == 0,
                ghost.ReturnValue?.ToStandardText() ?? "<null>");
        }

        private static void TestYieldForms()
        {
            var bare = Run(
                "pub func main(): i32 {\n" +
                "    yield\n" +
                "    return 4\n" +
                "}\n");
            CheckOk("裸 yield", bare);
            CheckI32("裸 yield 后终结", bare, 4);
            var sleep = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    yield sleep(5)\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("yield EventAlarm/sleep", sleep);
            CheckI32("sleep 后恢复", sleep, 1);
            var poll = Run(
                "import core.coroutine.*\n" +
                "pub shared class Flip : PollingAlarm {\n" +
                "    pub var ready: bool = false\n" +
                "    pub override func isReady(): bool { return ready }\n" +
                "}\n" +
                "async func arm(f: Flip) {\n" +
                "    yield\n" +
                "    f.ready = true\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flip()\n" +
                "    arm(f)\n" +
                "    yield f\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("yield PollingAlarm", poll);
            CheckI32("翻牌后恢复", poll, 1);
        }

        private static void TestConcurrentPrintLines()
        {
            var result = Run(
                "async func say(msg: String) {\n" +
                "    core.io.Console.println(msg + \"\\n\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    say(\"one\")\n" +
                "    say(\"two\")\n" +
                "    say(\"three\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("并发 print", result);
            TestHarness.CheckTrue("行 one 完整出现", result.Stdout.Contains("one\n"),
                result.Stdout);
            TestHarness.CheckTrue("行 two 完整出现", result.Stdout.Contains("two\n"),
                result.Stdout);
            TestHarness.CheckTrue("行 three 完整出现", result.Stdout.Contains("three\n"),
                result.Stdout);
        }

        private static void TestAwaitThroughTryFinally()
        {
            var ok = Run(
                "async func pause(): i32 {\n" +
                "    yield\n" +
                "    return 9\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var n = await pause()\n" +
                "        return n\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n");
            CheckOk("await 穿越 try/finally", ok);
            TestHarness.Check("恢复后 finally", ok.Stdout, "fin\n");
            CheckI32("finally 后返回", ok, 9);
            var boom = Run(
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
            CheckOk("await 异常穿越 finally", boom);
            TestHarness.Check("异常路径 finally", boom.Stdout, "fin\n");
            CheckI32("catch 返回", boom, 2);
        }

        private static void TestCoroutineStressForkJoin()
        {
            var result = Run(
                "async func tree(n: i32): i32 {\n" +
                "    if (n <= 0) {\n" +
                "        return 1\n" +
                "    }\n" +
                "    var left = tree(n - 1)\n" +
                "    var right = tree(n - 1)\n" +
                "    var a = await left\n" +
                "    var b = await right\n" +
                "    return a + b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var root = tree(7)\n" +
                "    var n = await root\n" +
                "    return n\n" +
                "}\n");
            CheckOk("100+ 协程 fork/join", result);
            CheckI32("tree(7) = 128", result, 128);
        }

        // ===== Stage B：return@ route 展开（StructuredExitRouting）端到端 =====
        private static void TestStructuredExitRouting()
        {
            // (a) if 表达式值块早退：结果正确且后续语句不执行
            var early = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    var r = if ((x == 0)) {\n" +
                "        x = (x + 1)\n" +
                "        if ((x == 1)) { return@_ 10 }\n" +
                "        x = (x + 100)\n" +
                "        return@_ x\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return (r + x)\n" +
                "}\n");
            CheckOk("值块早退", early);
            CheckI32("早退结果 10 且 x+100 跳过", early, 11);

            // (b) 嵌套 if/switch 多个 return@：指向外层 if target
            // （if region 穿 switch region 两级 relay）与同 target 多出口
            var nested = Run(
                "pub func main(): i32 {\n" +
                "    var r = if ((1 == 1)) named outer {\n" +
                "        var s = switch (2) {\n" +
                "            (2) -> {\n" +
                "                if ((2 == 2)) { return@outer 20 }\n" +
                "                return@_ 21\n" +
                "            }\n" +
                "            default -> { return@_ 99 }\n" +
                "        }\n" +
                "        return@outer s\n" +
                "    } else {\n" +
                "        return@outer 0\n" +
                "    }\n" +
                "    var t = switch (1) {\n" +
                "        (1) -> {\n" +
                "            if ((1 == 1)) { return@_ 7 }\n" +
                "            return@_ 8\n" +
                "        }\n" +
                "        default -> { return@_ 9 }\n" +
                "    }\n" +
                "    return (r + t)\n" +
                "}\n");
            CheckOk("嵌套 return@ 多 target", nested);
            CheckI32("if←switch relay 20 + switch←if relay 7", nested, 27);

            // (c) 用户 §5 例：return@middle / return@outer 两路径的
            // cleanup 执行与 afterTry/afterMiddle 跳过
            var pathA = Run(
                "pub func main(): i32 {\n" +
                "    seq named outer {\n" +
                "        seq named middle {\n" +
                "            try {\n" +
                "                if (true) { return@middle }\n" +
                "                if (false) { return@outer }\n" +
                "                core.io.Console.println(\"work\")\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"cleanup\")\n" +
                "            }\n" +
                "            core.io.Console.println(\"afterTry\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"afterMiddle\")\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("return@middle 路径", pathA);
            TestHarness.Check("cleanup 执行、afterTry 跳过、afterMiddle 执行",
                pathA.Stdout, "cleanup\nafterMiddle\n");
            CheckI32("return@middle 返回", pathA, 1);
            var pathB = Run(
                "pub func main(): i32 {\n" +
                "    seq named outer {\n" +
                "        seq named middle {\n" +
                "            try {\n" +
                "                if (false) { return@middle }\n" +
                "                if (true) { return@outer }\n" +
                "                core.io.Console.println(\"work\")\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"cleanup\")\n" +
                "            }\n" +
                "            core.io.Console.println(\"afterTry\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"afterMiddle\")\n" +
                "    }\n" +
                "    return 2\n" +
                "}\n");
            CheckOk("return@outer 路径", pathB);
            TestHarness.Check("cleanup 执行、afterTry/afterMiddle 跳过",
                pathB.Stdout, "cleanup\n");
            CheckI32("return@outer 返回", pathB, 2);
            var pathC = Run(
                "pub func main(): i32 {\n" +
                "    seq named outer {\n" +
                "        seq named middle {\n" +
                "            try {\n" +
                "                if (false) { return@middle }\n" +
                "                if (false) { return@outer }\n" +
                "                core.io.Console.println(\"work\")\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"cleanup\")\n" +
                "            }\n" +
                "            core.io.Console.println(\"afterTry\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"afterMiddle\")\n" +
                "    }\n" +
                "    return 3\n" +
                "}\n");
            CheckOk("无 exit 路径", pathC);
            TestHarness.Check("work/cleanup/afterTry/afterMiddle 全执行",
                pathC.Stdout, "work\ncleanup\nafterTry\nafterMiddle\n");
            CheckI32("无 exit 返回", pathC, 3);

            // (d) return@ 穿 try/catch/finally：finally 执行、catch 不触发
            var throughTry = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        try {\n" +
                "            return@_ 5\n" +
                "        } catch (_: core.Exception) {\n" +
                "            core.io.Console.println(\"catch\")\n" +
                "        } finally(_) {\n" +
                "            core.io.Console.println(\"fin\")\n" +
                "        }\n" +
                "        return@_ 6\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("return@ 穿 try/catch/finally", throughTry);
            TestHarness.Check("finally 执行且 catch 不触发", throughTry.Stdout, "fin\n");
            CheckI32("return@ 产值 5", throughTry, 5);

            // (e) weaving 旧 bug 回归：try 后 continuation 抛 catch 类型
            // 异常不再被前 catch 捕获（continuation 不再织入 try region）
            var notCaught = Run(
                "pub func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        try {\n" +
                "            core.io.Console.println(\"t\")\n" +
                "        } catch (_: core.RuntimeException) {\n" +
                "            return@_ 2\n" +
                "        }\n" +
                "        return@_ boom()\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckTrue("try 后 continuation 异常不被前 catch 捕获",
                notCaught.Exception?.ExceptionObject is VmObject escaped
                && escaped.TypeRef.Contains("RuntimeException"),
                notCaught.Exception?.ToString() ?? "<null>");

            // (f) finally 中新 return@ 覆盖原 exit（route 覆写，
            // dispatcher 看到新目标）
            var overrideExit = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        try {\n" +
                "            return@_ 1\n" +
                "        } finally(_) {\n" +
                "            return@_ 2\n" +
                "        }\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("finally return@ 覆盖", overrideExit);
            CheckI32("finally 产值 2 覆盖 1", overrideExit, 2);
            // finally throw 覆盖并跳过 dispatcher
            var overrideThrow = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        try {\n" +
                "            return@_ 1\n" +
                "        } finally(_) {\n" +
                "            throw new core.RuntimeException(\"f\")\n" +
                "        }\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckTrue("finally throw 覆盖并跳过 dispatcher",
                overrideThrow.Exception?.ExceptionObject is VmObject thrown
                && thrown.TypeRef.Contains("RuntimeException"),
                overrideThrow.Exception?.ToString() ?? "<null>");

            // (f2) finally 内尾位 return@ 必须以 abrupt completion（break）
            // 覆盖 SavedCompletion（同 region 尾位 break 省略的 finally
            // 例外回归）：body return@outer、finally return@middle——
            // 若 finally 的 break 被省略（落尾 Normal），VM 恢复 body 的
            // return@outer，afterMiddle/afterOuter 均被跳过；正确行为
            // 命中 middle——afterMiddle/afterOuter 都执行
            var finallyOverrideNamed = Run(
                "pub func main(): i32 {\n" +
                "    seq named outer {\n" +
                "        seq named middle {\n" +
                "            try {\n" +
                "                return@outer\n" +
                "            } finally(_) {\n" +
                "                return@middle\n" +
                "            }\n" +
                "            core.io.Console.println(\"afterTry\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"afterMiddle\")\n" +
                "    }\n" +
                "    core.io.Console.println(\"afterOuter\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("finally return@middle 覆盖 body return@outer", finallyOverrideNamed);
            TestHarness.Check("命中 middle（afterTry 跳过、afterMiddle/afterOuter 执行）",
                finallyOverrideNamed.Stdout, "afterMiddle\nafterOuter\n");
            CheckI32("middle 路径返回", finallyOverrideNamed, 1);

            // (g) 语句 seq return@name（无 result）执行
            var stmtSeq = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    seq named s {\n" +
                "        x = 1\n" +
                "        if ((x == 1)) { return@s }\n" +
                "        x = 99\n" +
                "    }\n" +
                "    x = (x + 1)\n" +
                "    return x\n" +
                "}\n");
            CheckOk("语句 seq return@name", stmtSeq);
            CheckI32("x=99 跳过、x+1 执行", stmtSeq, 2);

            // (h) seq using 单/双层：return@ 穿越时逆序 dispose 后再 relay
            var usingRelay = Run(
                "class Res implements core.IDisposable {\n" +
                "    pub const tag: String\n" +
                "    pub init(t: String) { tag = t }\n" +
                "    pub override func dispose() { core.io.Console.println(tag) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        seq using(const a = new Res(\"da\")) using(const b = new Res(\"db\")) {\n" +
                "            return@_ 7\n" +
                "        }\n" +
                "        return@_ 8\n" +
                "    }\n" +
                "    var s = seq {\n" +
                "        seq using(const c = new Res(\"dc\")) {\n" +
                "            if ((1 == 1)) { return@_ 3 }\n" +
                "            return@_ 4\n" +
                "        }\n" +
                "        return@_ 5\n" +
                "    }\n" +
                "    return (r + s)\n" +
                "}\n");
            CheckOk("using 穿越 relay", usingRelay);
            TestHarness.Check("逆序 dispose（db,da 后 dc）", usingRelay.Stdout, "db\nda\ndc\n");
            CheckI32("using 穿越产值 7 + 3", usingRelay, 10);

            // (i) 循环体 return@ 外层值块（while / do-while / for）
            var whileExit = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        var x: i32 = 3\n" +
                "        while ((x > 1)) {\n" +
                "            return@_ 42\n" +
                "        }\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("while 体 return@ 外层值块", whileExit);
            CheckI32("while 体产值 42", whileExit, 42);
            var doWhileExit = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        do { return@_ 8 } while (false)\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("do-while 体 return@ 外层值块", doWhileExit);
            CheckI32("do-while 体产值 8", doWhileExit, 8);
            var forExit = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        for (i in 0 to 5) {\n" +
                "            if ((i == 2)) { return@_ 15 }\n" +
                "        }\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("for 体 return@ 外层值块", forExit);
            CheckI32("for 体产值 15", forExit, 15);

            // (j) 嵌套循环多级 relay
            var nestedLoop = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq named outer {\n" +
                "        while (true) {\n" +
                "            while (true) {\n" +
                "                return@outer 21\n" +
                "            }\n" +
                "            return@outer 0\n" +
                "        }\n" +
                "        return@outer 1\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("嵌套循环多级 relay", nestedLoop);
            CheckI32("内层 return@ 穿两层 loop 产值 21", nestedLoop, 21);

            // (j2) 循环体内 return@ + 嵌套 relay：混合形态（内层产值 /
            // 外层逃逸）经两层 loop dispatcher；hint 不得影响 VM 结果
            var mixedNested = Run(
                "pub func pick(flag: bool): i32 {\n" +
                "    var r: i32 = seq named outer {\n" +
                "        var t: i32 = seq {\n" +
                "            while (true) {\n" +
                "                while (flag) {\n" +
                "                    return@_ 21\n" +
                "                }\n" +
                "                return@outer 0\n" +
                "            }\n" +
                "            return@_ 1\n" +
                "        }\n" +
                "        return@outer t\n" +
                "    }\n" +
                "    return r\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return (pick(true) + pick(false))\n" +
                "}\n");
            CheckOk("混合形态嵌套 loop relay", mixedNested);
            CheckI32("true→21 + false→0", mixedNested, 21);

            // (k) 循环内 break/continue 与 return@ 共存：break/continue
            // 不写 route，dispatcher fall-through 落到循环后 return@
            var mixBreak = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        var x: i32 = 5\n" +
                "        var acc: i32 = 0\n" +
                "        while ((x > 0)) {\n" +
                "            if ((x == 5)) { x = (x - 1)\ncontinue }\n" +
                "            if ((x == 1)) { break }\n" +
                "            if ((x == 99)) { return@_ 99 }\n" +
                "            acc = (acc + x)\n" +
                "            x = (x - 1)\n" +
                "        }\n" +
                "        return@_ acc\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("break/continue 与 return@ 共存（break 路径）", mixBreak);
            CheckI32("continue 跳过 5、break 于 1，acc=4+3+2", mixBreak, 9);
            var mixReturn = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        var x: i32 = 5\n" +
                "        while ((x > 0)) {\n" +
                "            if ((x == 3)) { return@_ 77 }\n" +
                "            if ((x == 1)) { break }\n" +
                "            x = (x - 1)\n" +
                "        }\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("break/continue 与 return@ 共存（return@ 路径）", mixReturn);
            CheckI32("x==3 时 return@ 77", mixReturn, 77);

            // (l) 循环内 return@ 穿 try/finally：finally 执行且可覆盖
            var loopThroughTry = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        var x: i32 = 1\n" +
                "        while ((x > 0)) {\n" +
                "            try {\n" +
                "                return@_ 5\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"fin\")\n" +
                "            }\n" +
                "        }\n" +
                "        return@_ 6\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("循环内 return@ 穿 try/finally", loopThroughTry);
            TestHarness.Check("finally 执行", loopThroughTry.Stdout, "fin\n");
            CheckI32("穿 try 产值 5", loopThroughTry, 5);
            var loopFinallyOverride = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        while (true) {\n" +
                "            try {\n" +
                "                return@_ 1\n" +
                "            } finally(_) {\n" +
                "                return@_ 2\n" +
                "            }\n" +
                "        }\n" +
                "        return@_ 3\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("循环内 finally return@ 覆盖", loopFinallyOverride);
            CheckI32("finally 产值 2 覆盖 1", loopFinallyOverride, 2);
        }

        // bug13①：泛型构造类型的运行期 init 匹配——定义级 init 签名的
        // .generic 占位按构造实参代入后比对（dist repro_bug2 与牵连用例）：
        // new core.Pair<String, i32>(...) / 用户泛型类带参构造 / kwargs 打包
        private static void TestGenericConstructedNewInit()
        {
            var result = Run(
                "class Container\\<T> {\n" +
                "    pub const item: T\n" +
                "    pub init(_ -> item) { }\n" +
                "}\n" +
                "func config(options: named Any...) {\n" +
                "    core.io.Console.println(\"kwargs-ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new core.Pair\\<String, i32>(\"age\", 3)\n" +
                "    core.io.Console.println(\"${p.key}:${p.value}\")\n" +
                "    var c = new Container\\<i32>(1)\n" +
                "    core.io.Console.println(\"${c.item}\")\n" +
                "    config(isDark = true, level = 3)\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("泛型构造类型 init 匹配", result);
            TestHarness.Check("泛型构造 stdout", result.Stdout,
                "age:3\n1\nkwargs-ok\n");
            CheckI32("main 返回 0", result, 0);
        }

        // bug13① 牵连：解构用 Pair 子类的 super(k, v)——extends 构造实参
        // 代入基类定义级 init 签名后匹配（dist 12_destructure 形态）
        private static void TestDestructuringSuperGenericInit()
        {
            var result = Run(
                "pub class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n" +
                "        super(k, v)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var (k, v) = new Entry(\"age\", 3)\n" +
                "    core.io.Console.println(\"${k}:${v}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Pair 子类 super 调用 + 解构", result);
            TestHarness.Check("解构 stdout", result.Stdout, "age:3\n");
            CheckI32("main 返回 0", result, 0);
        }

        // bug14：方法符号内嵌闭合泛型类型引用——canonical 符号是成员声明行
        // 的单个词（§5.2），实参分隔不得含空白；发射紧凑形态后 BilReader/
        // 验证器/VM 全链路可消化（dist _repro_func2param 形态扩展为实调）
        private static void TestClosedGenericParamSymbolEndToEnd()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func take2(f: core.Func\\<i32, i32>): i32 {\n" +
                "    return f(1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{(x: i32): i32 -> (x + 41)}\n" +
                "    return take2(f)\n" +
                "}\n");
            TestHarness.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            // 签名形态回归锁：符号内闭合泛型紧凑无空白
            TestHarness.CheckTrue("方法符号内嵌闭合泛型紧凑形态",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "$take2(f:core::Func<.i32,.i32>)@.i32"),
                string.Join(", ", module.LocalSymbols.OfType<BilSimpleMemberDeclaration>()
                    .Select(d => d.Symbol)));
            // BilWriter 文本经 BilReader 回读 + 验证器零错误（VM 装载前置）
            var reparsed = BilReader.Read(text);
            BilTestHarness.CheckBilValid("回读模块验证器零错误", reparsed);
            var result = BilVm.Run(reparsed);
            CheckOk("回读模块 VM 运行", result);
            CheckI32("take2(lambda) = 42", result, 42);
        }

        // 用户泛型函数形参含函数泛型参数的构造类型（.array<.generic<T>> /
        // .nullable<.generic<T>>）：调用点帧未绑 .generic.T，cast 不得抛
        // 「无法解析泛型占位」；纯 .generic<T> 形参作对照。
        private static void TestGenericFunctionConstructedParams()
        {
            var firstI32 = Run(
                "import core.collections.*\n" +
                "pub func firstOf\\<T>(arr: Array\\<T>): T { return (arr[0] as T) }\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 42\n" +
                "    a[1] = 1\n" +
                "    a[2] = 2\n" +
                "    return firstOf\\<i32>(a)\n" +
                "}\n");
            CheckOk("firstOf<i32>", firstI32);
            CheckI32("firstOf<i32> = 42", firstI32, 42);

            var firstString = Run(
                "import core.collections.*\n" +
                "pub func firstOf\\<T>(arr: Array\\<T>): T { return (arr[0] as T) }\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOfElements\\<String>(\"ok\", \"no\")\n" +
                "    core.io.Console.println(firstOf\\<String>(a))\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("firstOf<String>", firstString);
            TestHarness.Check("firstOf<String> stdout", firstString.Stdout, "ok\n");
            CheckI32("firstOf<String> 返回 0", firstString, 0);

            var nested = Run(
                "import core.collections.*\n" +
                "pub func firstNested\\<T>(arr: Array\\<Array\\<T>>): Array\\<T> {\n" +
                "    return (arr[0] as Array\\<T>)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var row = arrayOf\\<i32>(2)\n" +
                "    row[0] = 7\n" +
                "    row[1] = 8\n" +
                "    var outer = arrayOf\\<Array\\<i32>>(1)\n" +
                "    outer[0] = row\n" +
                "    var got = firstNested\\<i32>(outer)\n" +
                "    return got[0] if? 0\n" +
                "}\n");
            CheckOk("嵌套 Array<Array<T>> 形参", nested);
            CheckI32("firstNested = 7", nested, 7);

            var nullable = Run(
                "pub func unwrapOr\\<T>(v: T?, fallback: T): T {\n" +
                "    if (v == null) {\n" +
                "        return fallback\n" +
                "    }\n" +
                "    return v\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var n: i32? = 9\n" +
                "    core.io.Console.println(\"${unwrapOr\\<i32>(n, 0)}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("nullable<T> 形参", nullable);
            TestHarness.Check("unwrapOr(9, 0) 打印", nullable.Stdout, "9\n");
            CheckI32("nullable main 返回 0", nullable, 0);

            var id = Run(
                "pub func id\\<T>(x: T): T { return x }\n" +
                "pub func main(): i32 { return id\\<i32>(42) }\n");
            CheckOk("纯 .generic<T> 形参对照", id);
            CheckI32("id<i32>(42) = 42", id, 42);

            var typeOfT = Run(
                "pub func matchesT\\<T>(x: T): bool {\n" +
                "    var t = typeOf(T)\n" +
                "    return (x is t)\n" +
                "}\n" +
                "pub func main(): bool {\n" +
                "    return matchesT\\<i32>(1)\n" +
                "}\n");
            CheckOk("typeOf(T) 绑定帧 getid.type 解析", typeOfT);
            CheckBool("1 is typeOf(T)", typeOfT, true);
        }

        // 固定泛型推断端到端：无显式实参调用 + 泛型 operator 运算符位置
        private static void TestGenericInferenceEndToEnd()
        {
            var first = Run(
                "import core.collections.*\n" +
                "pub func firstOf\\<T>(arr: Array\\<T>): T { return (arr[0] as T) }\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 42\n" +
                "    a[1] = 1\n" +
                "    a[2] = 2\n" +
                "    return firstOf(a)\n" +
                "}\n");
            CheckOk("firstOf 推断+运行", first);
            CheckI32("firstOf(a) = 42", first, 42);

            var plus = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus\\<TAnother>(another: TAnother): Vec {\n" +
                "        var w = another as Vec\n" +
                "        return new Vec((x + w.x))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    return c.x\n" +
                "}\n");
            CheckOk("泛型 plus 运算符位置", plus);
            CheckI32("1+2 = 3", plus, 3);

            var compare = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator compareTo\\<TAnother>(another: TAnother): ComparisonResult {\n" +
                "        var w = another as Vec\n" +
                "        if ((x < w.x)) { return .LesserThanAnother }\n" +
                "        if ((x > w.x)) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    if ((a < b)) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("泛型 compareTo 运算符位置", compare);
            CheckI32("1 < 2", compare, 1);

            var compound = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus\\<TAnother>(another: TAnother): Vec {\n" +
                "        var w = another as Vec\n" +
                "        return new Vec((x + w.x))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(10)\n" +
                "    var b = new Vec(5)\n" +
                "    a += b\n" +
                "    return a.x\n" +
                "}\n");
            CheckOk("泛型 plus 复合赋值", compound);
            CheckI32("10+=5 = 15", compound, 15);

            var unary = Run(
                "class Bits {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator opposite(): Bits { return new Bits((0 - v)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Bits(3)\n" +
                "    return (-a).v\n" +
                "}\n");
            CheckOk("非泛型一元对照", unary);
            CheckI32("-3", unary, -3);

            var dispatch = Run(
                "class Box {\n" +
                "    pub var tag: i32\n" +
                "    pub init(_ -> tag) { }\n" +
                "    pub operator plus\\<TAnother>(another: TAnother): i32 {\n" +
                "        if ((another is i32)) { return (tag + (another as i32)) }\n" +
                "        if ((another is String)) { return (tag + 100) }\n" +
                "        return tag\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box(1)\n" +
                "    var a = b + 2\n" +
                "    var c = b + \"x\"\n" +
                "    return (a + c)\n" +
                "}\n");
            CheckOk("推断类型运行时派发", dispatch);
            CheckI32("1+2 与 1+String → 3+101 = 104", dispatch, 104);
        }

        // 泛型参数经约束的成员/运算符：静态按界定型，运行时按实际 typeid 派发
        private static void TestGenericParamConstraintDispatch()
        {
            var sum = Run(
                "pub interface Addable { pub operator plus(other: Addable): Addable }\n" +
                "pub class A implements Addable {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n) { }\n" +
                "    pub operator plus(other: Addable): Addable {\n" +
                "        return new A((n + ((other as A).n)))\n" +
                "    }\n" +
                "}\n" +
                "pub class B implements Addable {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n) { }\n" +
                "    pub operator plus(other: Addable): Addable {\n" +
                "        return new B(((n + ((other as B).n)) + 100))\n" +
                "    }\n" +
                "}\n" +
                "pub func sum\\<T extends Addable>(a: T, b: T): Addable { return a + b }\n" +
                "pub func main(): i32 {\n" +
                "    var x = (sum\\<A>(new A(1), new A(2)) as A).n\n" +
                "    var y = (sum\\<B>(new B(3), new B(4)) as B).n\n" +
                "    return (x + y)\n" +
                "}\n");
            CheckOk("sum<T extends Addable> 动态派发", sum);
            CheckI32("A:1+2=3 与 B:3+4+100=107 → 110", sum, 110);

            var cmp = Run(
                "pub interface Ordered { pub operator compareTo(other: Ordered): ComparisonResult }\n" +
                "pub class N implements Ordered {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator compareTo(other: Ordered): ComparisonResult {\n" +
                "        var w = (other as N).v\n" +
                "        if ((v < w)) { return .LesserThanAnother }\n" +
                "        if ((v > w)) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "}\n" +
                "pub func clamp\\<T extends Ordered>(x: T, lo: T, hi: T): T {\n" +
                "    if ((x < lo)) { return lo }\n" +
                "    if ((x > hi)) { return hi }\n" +
                "    return x\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new N(0)\n" +
                "    var b = new N(5)\n" +
                "    var c = new N(10)\n" +
                "    var r = clamp\\<N>(a, b, c)\n" +
                "    return r.v\n" +
                "}\n");
            CheckOk("clamp 经 compareTo 约束", cmp);
            CheckI32("clamp(0,5,10)=5", cmp, 5);

            var eq = Run(
                "pub interface Equatable { pub operator equals(other: Equatable): bool }\n" +
                "pub class Tag implements Equatable {\n" +
                "    pub var id: i32\n" +
                "    pub init(_ -> id) { }\n" +
                "    pub operator equals(other: Equatable): bool {\n" +
                "        return (id == ((other as Tag).id))\n" +
                "    }\n" +
                "}\n" +
                "pub func same\\<T extends Equatable>(a: T, b: T): bool { return a == b }\n" +
                "pub func main(): i32 {\n" +
                "    if (same\\<Tag>(new Tag(7), new Tag(7))) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("== 经 equals 约束", eq);
            CheckI32("same Tag(7)", eq, 1);

            var sumAll = Run(
                "import core.collections.*\n" +
                "pub interface Addable { pub operator plus(other: Addable): Addable }\n" +
                "pub class N implements Addable {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator plus(other: Addable): Addable {\n" +
                "        return new N((v + ((other as N).v)))\n" +
                "    }\n" +
                "}\n" +
                "pub func sumAll\\<T extends Addable>(arr: Array\\<T>): Addable {\n" +
                "    var acc: Addable = (arr[0] as T)\n" +
                "    var i = 1\n" +
                "    while ((i < arr.length)) {\n" +
                "        acc = (acc + (arr[i] as T))\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<N>(3)\n" +
                "    a[0] = new N(10)\n" +
                "    a[1] = new N(20)\n" +
                "    a[2] = new N(12)\n" +
                "    return ((sumAll\\<N>(a) as N).v)\n" +
                "}\n");
            CheckOk("sumAll Array<T> 循环累加", sumAll);
            CheckI32("10+20+12=42", sumAll, 42);

            var ts = Run(
                "pub func show\\<T>(x: T): String { return x.toString() }\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(show\\<i32>(42))\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("无约束 T toString 端到端", ts);
            TestHarness.Check("toString(42)", ts.Stdout, "42\n");

            var pair = Run(
                "pub interface Addable { pub operator plus(other: Addable): Addable }\n" +
                "pub class N implements Addable {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator plus(other: Addable): Addable {\n" +
                "        return new N((v + ((other as N).v)))\n" +
                "    }\n" +
                "}\n" +
                "pub class Pair\\<T extends Addable> {\n" +
                "    pub var a: T\n" +
                "    pub var b: T\n" +
                "    pub init(_ -> a, _ -> b) { }\n" +
                "    pub func add(): Addable { return a + b }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Pair\\<N>(new N(3), new N(4))\n" +
                "    return ((p.add() as N).v)\n" +
                "}\n");
            CheckOk("Pair<T extends Addable> 内 +", pair);
            CheckI32("3+4=7", pair, 7);
        }

        // getid.type 对含 .generic< 的构造类型走 ResolveTypeRef：绑定帧
        // 把 .array<.generic<$.generic.T>> 物化为 .array<.i32>
        private static void TestGetIdTypeResolvesNestedGeneric()
        {
            var module = new BilModule();
            var probe = new BilFunction("$probe()@.typeid");
            probe.Args.Add(new BilArgDeclaration(".return", ".typeid"));
            probe.Args.Add(new BilArgDeclaration(".generic.T", ".typeid"));
            probe.Vars.Add(new BilVarDeclaration(".typeid", "t"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new GetIdTypeInstruction(
                BilOp.Type(".array<.generic<$.generic.T>>"), BilOp.Var("t")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("t")));
            probe.Blocks.Add(entry);
            module.Functions.Add(probe);

            var result = RunPrepared(module, "$probe()@.typeid",
                new[] { new VmTypeId(".i32") });
            CheckOk("getid.type 解析嵌套泛型占位", result);
            TestHarness.CheckTrue("物化为 .array<.i32>",
                result.ReturnValue is VmTypeId id && id.TypeSymbol == ".array<.i32>",
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // bug17：String.length 内建 const i64（.bootstrap.rg ext 声明，
        // VM get.field 直读，同 Array.length 通道）——字面量/多行/插值串
        private static void TestStringLength()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var s = \"hello\"\n" +
                "    core.io.Console.println(\"${s.length}\")\n" +
                "    var multi = \"\"\"\n" +
                "line1\n" +
                "line2\n" +
                "\"\"\"\n" +
                "    core.io.Console.println(\"${multi.length}\")\n" +
                "    var name = \"world\"\n" +
                "    core.io.Console.println(\"${\"hi ${name}\".length}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("String.length", result);
            TestHarness.Check("length stdout", result.Stdout, "5\n11\n8\n");
            CheckI32("main 返回 0", result, 0);
        }

        // §13.3 写序：wrapper 链 → setter → 直写 backing
        // wrapper(5+10)=15 → setter(15*2)=30
        private static void TestWrapperOutsideSetterWriteOrder()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        inner(((v + 10) as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Shift()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { value = value * 2 }\n" +
                "    } = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 5\n" +
                "    return h.hp\n" +
                "}\n");
            CheckOk("写序 wrapper→setter", result);
            CheckI32("wrapper(5+10)=15 → setter*2=30", result, 30);
        }

        // §13.3 读序：backing → getter → wrapper 链
        // backing 10 → getter*2=20 → wrapper+1=21
        private static void TestWrapperOutsideGetterReadOrder()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        var v = (value as i32)\n" +
                "        return ((v + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Shift()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value * 2 }\n" +
                "        pub set(value: _) { }\n" +
                "    } = 10\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n");
            CheckOk("读序 getter→wrapper", result);
            CheckI32("backing 10 → getter*2=20 → wrapper+1=21", result, 21);
        }

        // 初始化器经 setter、不绕尚未安装的 wrapper
        private static void TestWrapperAccessorInitializerNoCrash()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @W()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { if (value > 100) { value = 100 } }\n" +
                "    } = 150\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n");
            CheckOk("初始化器经 setter 不崩溃", result);
            CheckI32("init 豁免 wrapper、setter 钳制 150→100", result, 100);
        }

        // setter 体内多次读写 value（..value 直达 backing）
        private static void TestSetterBodyMultipleValueAccess()
        {
            var result = Run(
                "pub class Box {\n" +
                "    pub var n: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            var t = value\n" +
                "            value = t + 1\n" +
                "        }\n" +
                "    } = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    b.n = 5\n" +
                "    return b.n\n" +
                "}\n");
            CheckOk("setter 体内多次读写 value", result);
            CheckI32("value 读 5 再写 t+1 → 6", result, 6);
        }

        // 局部变量（cell）同等外置顺序
        private static void TestWrapperAccessorLocalOrder()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
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
                "pub func main(): i32 {\n" +
                "    @Shift()\n" +
                "    var x: i32 {\n" +
                "        get(value: _) {\n" +
                "            core.io.Console.println(\"user.get\")\n" +
                "            return value * 2\n" +
                "        }\n" +
                "        set(value: _) {\n" +
                "            core.io.Console.println(\"user.set\")\n" +
                "            value = value * 2\n" +
                "        }\n" +
                "    } = 1\n" +
                "    x = 5\n" +
                "    return x\n" +
                "}\n");
            CheckOk("局部 wrapper+访问器外置序", result);
            TestHarness.Check("局部写读打印序", result.Stdout,
                "wrapper.set\nuser.set\nwrapper.set\nuser.set\nuser.get\nwrapper.get\n");
            CheckI32("局部 5→15→30 读 60→61", result, 61);
        }

        private static BilVmResult RunPrepared(BilModule module, string functionSymbol,
            IReadOnlyList<VmValue> arguments)
        {
            var context = new VmContext(module);
            var executor = new VmExecutor(context);
            var function = context.FindFunction(functionSymbol);
            TestHarness.CheckTrue("预备 fn 存在", function != null, functionSymbol);
            var coroutine = executor.Spawn(function!, arguments);
            executor.Publish(coroutine);
            executor.WaitQuiescence();
            return new BilVmResult(context.Stdout, context.Stderr, coroutine.Result,
                coroutine.Failure);
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

        private static void CheckBool(string label, BilVmResult result, bool expected)
        {
            TestHarness.CheckTrue(label,
                result.ReturnValue is VmBool flag && flag.Value == expected,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }
    }
}
