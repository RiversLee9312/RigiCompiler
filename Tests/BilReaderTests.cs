using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// BilReader 往返测试：EmitBilUnit 产出 BilModule，BilWriter.Write 后
    /// BilReader.Read 再 BilWriter.Write，文本应一致；手工 BilModule 覆盖
    /// 指令族与 direct module 路径；解析错误锁行号/消息。
    /// </summary>
    public static class BilReaderTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static ParallelSuiteRunner.SuiteSpec Spec => new(
            "BilReader", Cases, sectionTitle: "BilReader");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestRoundTripBasics", TestRoundTripBasics),
            ("TestRoundTripControlFlow", TestRoundTripControlFlow),
            ("TestRoundTripEnum", TestRoundTripEnum),
            ("TestRoundTripLambda", TestRoundTripLambda),
            ("TestRoundTripWrapper", TestRoundTripWrapper),
            ("TestRoundTripCoroutine", TestRoundTripCoroutine),
            ("TestRoundTripInterpolation", TestRoundTripInterpolation),
            ("TestRoundTripTypeOf", TestRoundTripTypeOf),
            ("TestRoundTripDirectModule", TestRoundTripDirectModule),
            ("TestParseError", TestParseError),
        };

        // EmitBilUnit 编译源码并发射 BIL，Writer/Reader 往返后文本一致
        private static void RoundTrip(string label, string source)
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            TestHarness.CheckTrue(label + " 编译无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => $"{d.Phase}: {d.Message}")));
            if (unit.Diagnostics.HasErrors)
            {
                return;
            }
            RoundTripModule(label, module);
        }

        private static void RoundTripModule(string label, BilModule module)
        {
            var text1 = BilWriter.Write(module);
            var parsed = BilReader.Read(text1);
            BilTestHarness.CheckBilValid(label + " Reader 解析后可验证", parsed);
            var text2 = BilWriter.Write(parsed);
            TestHarness.CheckTrue(label + " round-trip 文本一致", text1 == text2,
                FirstDiff(text1, text2));
        }

        private static string FirstDiff(string a, string b)
        {
            var la = a.Split('\n');
            var lb = b.Split('\n');
            for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
            {
                var x = i < la.Length ? la[i] : "<EOF>";
                var y = i < lb.Length ? lb[i] : "<EOF>";
                if (x != y)
                {
                    return $"第{i + 1} 行不一致\n  A: {x}\n  B: {y}";
                }
            }
            return "";
        }

        // ===== 基础：字面量/cast/invoke/new/字段访问/struct 值拷贝 =====
        private static void TestRoundTripBasics()
        {
            RoundTripScalar();

            RoundTrip("类与 init",
                "pub class Box {\n" +
                "    pub var n: i32\n" +
                "    pub init(v: i32) { n = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box(7)\n" +
                "    b.n = (b.n + 1)\n" +
                "    return b.n\n" +
                "}\n");

            RoundTrip("struct 值拷贝与静态字段",
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub init(a: i32) { x = a }\n" +
                "}\n" +
                "pub class Counter { pub static var value: i32 }\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Point(1)\n" +
                "    var q = p\n" +
                "    Counter.value = 42\n" +
                "    return (Counter.value + q.x)\n" +
                "}\n");

            RoundTrip("数组与索引",
                "import core.collections.*\n" +
                "pub class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub init() { item = 0 }\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 10\n" +
                "    var b = new Bag()\n" +
                "    b[0] = 21\n" +
                "    return (((a[0] if? 0) + (b[0] if? 0)))\n" +
                "}\n");
        }

        internal static void RoundTripScalar()
        {
            RoundTrip("基础字面量与运算",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    x = (x * 3)\n" +
                "    var b: bool = true\n" +
                "    var d: double = 0.5\n" +
                "    var c: char = 'A'\n" +
                "    var n: i64 = (x as i64)\n" +
                "    return ((x + (b as i32)) + (n as i32))\n" +
                "}\n");
        }

        // ===== 控制流：if/loop/switch/try/throw/break/continue/seq/using =====
        private static void TestRoundTripControlFlow()
        {
            RoundTrip("if/loop/break/continue",
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        if (i == 5) {\n" +
                "            break\n" +
                "        }\n" +
                "        if ((i == 2)) {\n" +
                "            i = (i + 1)\n" +
                "            continue\n" +
                "        }\n" +
                "        n = (n + i)\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");

            RoundTrip("for 范围与 switch",
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    for (i in 0 to 4) {\n" +
                "        switch (i) {\n" +
                "            (1) -> { n = (n + 10) }\n" +
                "            (_ > 2) -> { n = (n + 100) }\n" +
                "            default -> { n = (n + 1) }\n" +
                "        }\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");

            RoundTrip("try/catch/finally 与 throw",
                "pub func risky(): i32 {\n" +
                "    try {\n" +
                "        throw new core.RuntimeException(\"boom\")\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 7\n" +
                "    } finally(_) {\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return risky()\n" +
                "}\n");

            RoundTrip("seq 块",
                "pub func main(): i32 {\n" +
                "    seq {\n" +
                "        var a: i32 = 1\n" +
                "        var b: i32 = 2\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
        }

        // ===== enum：new.case / type.is.case =====
        private static void TestRoundTripEnum()
        {
            RoundTrip("enum case payload 与判别",
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "    pub init(_ -> errorCode)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const r: RequestResult = .Failed(404)\n" +
                "    if (r is .Failed) {\n" +
                "        return r.errorCode\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
        }

        // ===== lambda：invoke.indirect =====
        private static void TestRoundTripLambda()
        {
            RoundTrip("lambda invoke.indirect",
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n");
        }

        // ===== wrapper：get.wrapper / get.self / inner / new.wrapped =====
        private static void TestRoundTripWrapper()
        {
            RoundTrip("Value wrapper 的 proxy",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Inc {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((value as i32) + 1) as TValue)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Inc()\n" +
                "    const c = 0\n" +
                "    return (c as i32)\n" +
                "}\n");

            RoundTrip("Entity wrapper get.self + invoke fn(..inner)",
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
                "    pub func ping(): i32 { return 42 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping()\n" +
                "}\n");
        }

        // ===== 协程：async/await/yield =====
        private static void TestRoundTripCoroutine()
        {
            RoundTrip("async await",
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = add(41)\n" +
                "    var n = await t\n" +
                "    return n\n" +
                "}\n");

            RoundTrip("yield sleep",
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    yield sleep(1)\n" +
                "    return 0\n" +
                "}\n");
        }

        // ===== 插值：toString + cast + invoke =====
        private static void TestRoundTripInterpolation()
        {
            RoundTrip("字符串插值与 toString",
                "pub func main() {\n" +
                "    var name = \"world\"\n" +
                "    var count = 3\n" +
                "    core.io.Console.println(\"Hello ${name}, count=${count + 1}\")\n" +
                "}\n");
        }

        // ===== typeOf：getid + type.is.indirect =====
        private static void TestRoundTripTypeOf()
        {
            RoundTrip("typeOf 与间接类型检查",
                "pub class Box { pub var n: i32 = 0 }\n" +
                "pub func main(): bool {\n" +
                "    var b = new Box()\n" +
                "    var t = typeOf(b)\n" +
                "    var u = typeOf(Box)\n" +
                "    return ((b is t) and (b is u))\n" +
                "}\n");
        }

        // ===== 手工模块：直接构造 BilModule 覆盖全部指令族 =====
        private static void TestRoundTripDirectModule()
        {
            var module = FullFamilyModule();
            var text1 = BilWriter.Write(module);
            var parsed = BilReader.Read(text1);
            var text2 = BilWriter.Write(parsed);
            TestHarness.CheckTrue("手工 BilModule round-trip 文本一致", text1 == text2,
                FirstDiff(text1, text2));
            // §16.5 推广：if/call/try 携带末尾 breakid 操作数的往返
            TestHarness.CheckTrue("if/call/try 带 breakid 往返",
                text1.Contains("if $flag blk(if0-then) blk(if0-else) $.b1")
                && text1.Contains("call blk(seq0) $.b3")
                && text2.Contains("if $flag blk(if0-then) blk(if0-else) $.b1")
                && text2.Contains("call blk(seq0) $.b3")
                && text2.Contains("$.b2"));
        }

        private static BilModule FullFamilyModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_Hint", BilScalarType.String, "\"{}\""));
            module.Resources.Add(new BilScalarResource("R_7", BilScalarType.I32, "7"));
            module.Resources.Add(new BilSwitchTableResource("R_Table", ".i32", new[] { "1", "2" }));
            module.Resources.Add(new BilCatchTableResource("R_Catch",
                new[] { new BilCatchEntry(new BilTypeOperand("Box"), new BilBlock("try0-catch0")) }));

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

            var wrap = new BilTypeDeclaration("Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            wrap.GenericParameters.Add("TTarget");
            module.LocalSymbols.Add(wrap);

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
            foreach (var v in new[]
            {
                (".i32", "a"), (".i32", "b"), (".i32", "x"), (".i32", "y"),
                (".typeid<Box>", "tid"), (".fieldid<Box, .i32, instance>", "fid"),
                (".fieldid<Box, .i32, static>", "sfid"), (".bool", "flag"),
                (".any", "anyv"), ("Box", "obj"), ("Wrap", "w"), (".typeid<Wrap>", "wid"),
                (".i32", "r"), (".breakid", ".b0"), (".array<.i32>", "arr"),
                (".i32", "e"), (".breakid", ".b1"), (".breakid", ".b2"),
                (".breakid", ".b3"),
            })
            {
                main.Vars.Add(new BilVarDeclaration(v.Item1, v.Item2));
            }
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new HintInstruction(module.Resources[0]));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("a")));
            entry.Instructions.Add(new SetVarInstruction(BilOp.Var("a"), BilOp.Var("b")));
            entry.Instructions.Add(new GetVarInstruction(BilOp.Var("b"), BilOp.Var("x")));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("x")));
            entry.Instructions.Add(new UnaryIntrinsicInstruction(BilUnaryOp.Opposite,
                BilOp.Var("x"), BilOp.Var("y")));
            entry.Instructions.Add(new CastInstruction(BilOp.Var("a"), BilOp.Var("x"),
                BilOp.Type(".i64"), isSafe: false));
            entry.Instructions.Add(new CastInstruction(BilOp.Var("a"), BilOp.Var("x"),
                BilOp.Type(".i64"), isSafe: true));
            entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a"), BilOp.Var("x"),
                BilOp.Var("tid"), isSafe: false));
            entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a"), BilOp.Var("x"),
                BilOp.Var("tid"), isSafe: true));
            entry.Instructions.Add(new DirectTypeCheckInstruction(BilTypeCheckKind.Is,
                BilOp.Var("obj"), BilOp.Type("Box"), BilOp.Var("flag")));
            entry.Instructions.Add(new DirectTypeCheckInstruction(BilTypeCheckKind.Supers,
                BilOp.Var("obj"), BilOp.Type("Box"), BilOp.Var("flag")));
            entry.Instructions.Add(new IndirectTypeCheckInstruction(BilTypeCheckKind.Is,
                BilOp.Var("obj"), BilOp.Var("tid"), BilOp.Var("flag")));
            entry.Instructions.Add(new IndirectTypeCheckInstruction(BilTypeCheckKind.With,
                BilOp.Var("obj"), BilOp.Var("wid"), BilOp.Var("flag")));
            entry.Instructions.Add(new GetIdVarInstruction(BilOp.Var("obj"), BilOp.Var("tid")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Box"), BilOp.Var("tid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#n@.i32"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new GetWrapperInstruction(BilOp.Var("obj"), BilOp.Type("Wrap"),
                BilOp.Var("w")));
            entry.Instructions.Add(new GetWrapperIndirectInstruction(BilOp.Var("obj"),
                BilOp.Var("wid"), BilOp.Var("w")));
            entry.Instructions.Add(new GetWrapperFieldInstruction(BilOp.Var("obj"),
                BilOp.Field("Box#n@.i32"), BilOp.Type("Wrap"), BilOp.Var("w")));
            entry.Instructions.Add(new GetSelfInstruction(BilOp.Var("obj")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("obj"), BilOp.Var("x"),
                BilOp.Field("Box#n@.i32")));
            entry.Instructions.Add(new GetFieldIndirectInstruction(BilOp.Var("obj"),
                BilOp.Var("x"), BilOp.Var("fid")));
            entry.Instructions.Add(new SetFieldInstruction(BilOp.Var("a"), BilOp.Var("obj"),
                BilOp.Field("Box#n@.i32")));
            entry.Instructions.Add(new SetFieldIndirectInstruction(BilOp.Var("a"), BilOp.Var("obj"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new GetFieldStaticInstruction(BilOp.Var("x"), BilOp.Type("Box"),
                BilOp.Field("Box#.static.tag@.i32")));
            entry.Instructions.Add(new GetFieldStaticIndirectInstruction(BilOp.Var("x"),
                BilOp.Var("tid"), BilOp.Var("sfid")));
            entry.Instructions.Add(new SetFieldStaticInstruction(BilOp.Var("a"), BilOp.Type("Box"),
                BilOp.Field("Box#.static.tag@.i32")));
            entry.Instructions.Add(new SetFieldStaticIndirectInstruction(BilOp.Var("a"),
                BilOp.Var("tid"), BilOp.Var("sfid")));
            entry.Instructions.Add(new SetWrapperFieldInstruction(BilOp.Var("a"), BilOp.Var("obj"),
                BilOp.Wrapper("Wrap"), BilOp.Field("Box#n@.i32")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Box"), BilOp.Var("obj"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new NewIndirectInstruction(BilOp.Var("tid"), BilOp.Var("obj"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Box"), BilOp.Var("obj"),
                new[] { BilOp.Var("a") }, Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new NewWrapperFieldInstruction(BilOp.Field("Box#n@.i32"),
                BilOp.Type("Wrap"), new[] { BilOp.Var("a") }));
            entry.Instructions.Add(new NewWrapperMethodInstruction(
                BilOp.Fn("Box$init()@.void"), BilOp.Type("Wrap"),
                new BilVariableOperand[0]));
            entry.Instructions.Add(new NewWrapperEntityInstruction(BilOp.Type("Wrap"),
                new[] { BilOp.Var("a") }));
            entry.Instructions.Add(new InvokeInstruction(BilOp.Fn("Box$init()@.void"),
                BilOp.Var("r"), Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new InvokeNoResultInstruction(BilOp.Fn("Box$init()@.void"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new InvokeIndirectInstruction(BilOp.Var("obj"),
                BilOp.Var("r"), Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new InvokeIndirectNoResultInstruction(BilOp.Var("obj"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new IfInstruction(BilOp.Var("flag"),
                new BilBlock("if0-then"), new BilBlock("if0-else"), BilOp.Var(".b1")));
            entry.Instructions.Add(new LoopInstruction(BilOp.Var("flag"), new BilBlock("loop0-body"),
                null, new BilBlock("loop0-judge"), BilOp.Var(".b0"), isRev: false));
            entry.Instructions.Add(new LoopInstruction(BilOp.Var("flag"), new BilBlock("loop1-body"),
                new BilBlock("loop1-enum"), new BilBlock("loop1-judge"), BilOp.Var(".b0"), isRev: true));
            entry.Instructions.Add(new SwitchInstruction(BilOp.Var("a"), module.Resources[2],
                new[] { new BilBlock("switch0-item0") }, new BilBlock("switch0-default"),
                BilOp.Var(".b0")));
            entry.Instructions.Add(new TryInstruction(new BilBlock("try0-body"),
                BilOp.Var("anyv"), module.Resources[3], new BilBlock("try0-finally"),
                BilOp.Var(".b2")));
            entry.Instructions.Add(new CallBlockInstruction(new BilBlock("seq0"),
                BilOp.Var(".b3")));
            entry.Instructions.Add(new AwaitInstruction(BilOp.Var("obj"), BilOp.Var("x")));
            entry.Instructions.Add(new YieldInstruction(BilOp.Var("obj")));
            entry.Instructions.Add(new ThrowInstruction(BilOp.Var("obj")));
            entry.Instructions.Add(new BreakInstruction(BilOp.Var(".b0")));
            entry.Instructions.Add(new ContinueInstruction(BilOp.Var(".b0")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("b")));
            main.Blocks.Add(entry);

            foreach (var id in new[]
            {
                "if0-then", "if0-else", "loop0-body", "loop0-judge", "loop1-body",
                "loop1-enum", "loop1-judge", "switch0-item0", "switch0-default",
                "try0-body", "try0-catch0", "try0-finally", "seq0",
            })
            {
                var blk = new BilBlock(id);
                blk.Instructions.Add(new RetInstruction());
                main.Blocks.Add(blk);
            }
            module.Functions.Add(main);
            return module;
        }

        // ===== 解析错误：非法声明与非法指令 =====
        private static void TestParseError()
        {
            try
            {
                BilReader.Read("BIL \"1.1\"\n\nMetadata {\n    module = string \"x\"\n}\n\n" +
                    "Resources {\n}\n\nLocalSymbols {\n    .type A = bogus pub {\n    }\n}\n" +
                    "ExternalSymbols {\n}\n");
                TestHarness.CheckTrue("非法类型种类应抛 BilParseException", false);
            }
            catch (BilParseException ex)
            {
                TestHarness.CheckTrue("非法类型种类异常带行号与消息",
                    ex.Line > 0 && ex.Message.Contains("bogus"),
                    $"line={ex.Line} msg={ex.Message}");
            }

            try
            {
                BilReader.Read("BIL \"1.1\"\n\nMetadata {\n}\n\nResources {\n}\n\n" +
                    "LocalSymbols {\n}\n\nExternalSymbols {\n}\n\n" +
                    "fn($main()@.i32) {\n    .args {\n        .return = .i32\n    }\n\n" +
                    "    .vars {\n    }\n\n    .block entry entrypoint {\n" +
                    "        unknownopcode $x\n    }\n}\n");
                TestHarness.CheckTrue("未知指令应抛 BilParseException", false);
            }
            catch (BilParseException ex)
            {
                TestHarness.CheckTrue("未知指令异常带行号", ex.Line == 24,
                    $"line={ex.Line} msg={ex.Message}");
            }
        }
    }
}
