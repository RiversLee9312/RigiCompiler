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
    public static class BilVmTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("BilVm");

            TestHelloWorld();
            TestLocalArithmetic();
            TestUnaryNegation();
            TestInvokeWithResult();
            TestStringConcat();
            TestScalarLocals();
            TestClassInstanceFields();
            TestFieldZeroDefault();
            TestStaticFields();
            TestStructDeepCopy();
            TestArrayIndexOperators();
            TestEnumCasePayload();
            TestEnumCaseIdentity();
            TestGetterSetterOrder();
            TestInstanceMethodReceiver();
            TestBuiltinArrayDirectModule();
            TestArrayOfI32();
            TestArrayOfElementsString();
            TestWrapperInstallDirectModule();
            TestGetSelfDirectModule();
            TestNumericCasts();
            TestReferenceCasts();
            TestCastFailureAndSafe();
            TestAnyBoxUnbox();
            TestTypeIsSupersCase();
            TestTypeWithAndGetId();
            TestLambdaInvokeIndirect();
            TestGetWrapperDirectModule();
            TestIndirectFieldAndNew();
            TestUserOperatorAdd();
            TestDowngradeCallWildcard();
            TestMethodWrapperWildcardInnerFullShape();
            TestLambdaMethodWrapperWildcardInner();
            TestWildcardInnerMiddleOfWrapperChain();
            TestWrapperValueGetProxyInitArg();
            TestValueWrapperClampedMutableVar();
            TestValueWrapperTwoLayerOrder();
            TestValueWrapperGetOnlyProxy();
            TestStringInterpolationToString();
            TestEntitySpecificMethodProxySurrounds();
            TestEntityWildcardMethodProxyBothDirections();
            TestEntityGetterSetterProxyCounts();
            TestEntityOperatorProxyWildcardDirectModule();
            TestEntityProxyStatePersists();
            TestEntityProxySelfReadsHostField();
            TestEntityGenericCastUnboundDirectModule();
            TestMethodWrapperCallSpecificSurrounds();
            TestMethodWrapperStaticViaCompanion();
            TestMethodWrapperDoubleLayerOrder();
            TestMethodWrapperStatePersists();
            TestMethodWrapperArgPassThrough();
            TestMethodWrapperGetSelfDirectModule();
            TestMethodWrapperNotEqualsViaOprEqualsDirectModule();
            TestExceptionGetMessage();
            TestLambdaMethodWrapperEndToEnd();
            TestIfElse();
            TestWhileAndDoWhile();
            TestForRangeAndBreakContinue();
            TestNamedBreakContinue();
            TestSwitchStatementAndExpression();
            TestTryCatchFinally();
            TestThrowAcrossFunction();
            TestRetBreakContinueThroughFinally();
            TestUsingDisposeOrder();
            TestLoopEnumeratorDirectModule();
            TestAsyncAwaitResult();
            TestAwaitExceptionAndCompleted();
            TestForkJoinAndFireAndForget();
            TestYieldForms();
            TestConcurrentPrintLines();
            TestAwaitThroughTryFinally();
            TestCoroutineStressForkJoin();

            return TestHarness.Summary("BilVm");
        }

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
            var result = Run(
                "pub class Box { pub var n: i32 }\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    return b.n\n" +
                "}\n");
            CheckOk("字段零值", result);
            CheckI32("未初始化 i32 为零", result, 0);
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
                "    pub operator getAtIndex(index: i32): i32 { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag()\n" +
                "    b[0] = 21\n" +
                "    return b[0]\n" +
                "}\n");
            CheckOk("数组索引", result);
            CheckI32("get/set.array", result, 21);
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

        private static void TestEnumCaseIdentity()
        {
            var ok = Run(
                "enum struct Outcome { }[Ok, Failed]\n" +
                "pub func main(): Outcome { return .Ok }\n");
            CheckOk("enum 身份 Ok", ok);
            TestHarness.CheckTrue("Ok case 符号",
                ok.ReturnValue is VmEnum e && e.CaseSymbol == "Outcome.Ok",
                ok.ReturnValue?.ToStandardText() ?? "<null>");
            var failed = Run(
                "enum struct Outcome { }[Ok, Failed]\n" +
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
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
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
            CheckI32("元素是 2 不是零（长度特权已拆除）", result, 2);

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
                "    return ((a[0] + a[1]) + a[2])\n" +
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
                "    core.io.Console.println((a[0] + a[1]))\n" +
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
                "pub class Box { pub var n: i32 }\n" +
                "pub func main(): bool {\n" +
                "    var b = new Box()\n" +
                "    var t = typeOf(b)\n" +
                "    return (b is t)\n" +
                "}\n");
            CheckOk("getid.var + type.is.indirect", typeOf);
            CheckBool("b is typeOf(b)", typeOf, true);
            var typeId = Run(
                "pub class Box { pub var n: i32 }\n" +
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

        // frontend 的 `+` 不查用户 operator；直接发 add 测 §22.3 分派。
        private static void TestUserOperatorAdd()
        {
            var module = VectorPlusModule();
            var result = BilVm.Run(module);
            CheckOk("用户 operator plus", result);
            CheckI32("Vector2 add.x", result, 4);
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
                "B.get\n" +
                "A.get\n");
        }

        // Value 派发 c)：只带 get proxy 的 wrapper 修饰可变 var——非 init 写入
        // 在 VM 抛 VmException（含 wrapper 名与 .proxy.set）；const 场景读经
        // proxy 生效。
        private static void TestValueWrapperGetOnlyProxy()
        {
            var mutable = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper ReadOnly {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((value as i32) + 1) as TValue)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @ReadOnly\n" +
                "    var x: i32 = 1\n" +
                "    x = 2\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("get-only wrapper 非 init 写入抛 VmException",
                mutable.Exception != null
                && mutable.Exception.Message.Contains("ReadOnly")
                && mutable.Exception.Message.Contains(".proxy.set"),
                mutable.Exception?.ToString() ?? "<null>");

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
                "    pub var ready: bool\n" +
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
