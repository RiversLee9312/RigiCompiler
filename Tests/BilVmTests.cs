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
