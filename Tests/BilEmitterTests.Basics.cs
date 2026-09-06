using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    // BilEmitter 基础发射测试（hello world 结构/Origin 链/资源去重/局部声明与赋值/一元与比较/invoke/new/标量资源/static 字段/未覆盖节点负例）

    public static partial class BilEmitterTests
    {
        // ===== hello world 模块结构断言（M58 起：原全模块黄金文本改为
        // CheckBilValid + 结构断言；fn 形状回归由后续用例 CheckFnShape 承担）=====
        private static void TestGoldenOutput()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(HelloWorldSource);
            CheckNoErrors("全管线无诊断", unit);
            BilTestHarness.CheckBilValid("验证器零错误（hello world）", module);
            TestHarness.CheckTrue("Metadata 恰一条 module = \"hello\"",
                module.Metadata.Count == 1
                && module.Metadata[0].Key == "module"
                && module.Metadata[0].Type == BilScalarType.String
                && module.Metadata[0].LiteralText == "\"hello\"");
            TestHarness.CheckTrue("Resources 恰 105 条且含 \"Hello, world!\" 标量资源",
                module.Resources.Count == 105
                && module.Resources.Any(r => r is BilScalarResource s
                    && s.Type == BilScalarType.String
                    && s.LiteralText == "\"Hello, world!\""));
            TestHarness.CheckTrue("LocalSymbols 含 core.io::Console 类型与 println 静态方法声明",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "core.io::Console")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Kind == BilMemberKind.StaticMethod
                        && d.Symbol == "core.io::Console$.static.println(text:.string)@.void"));
            TestHarness.CheckTrue("module.Functions 含 main 与 println 两个 fn",
                module.Functions.Any(f => f.Symbol == "$main()@.i32")
                && module.Functions.Any(f => f.Symbol
                    == "core.io::Console$.static.println(text:.string)@.void"));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            TestHarness.CheckTrue("main 恰一个 entrypoint block",
                main.Blocks.Count(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint)) == 1);
        }

        // ===== Origin 调试链（ARCHITECTURE §6.3）=====
        private static void TestOriginChain()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(HelloWorldSource);
            CheckNoErrors("全管线无诊断（Origin 链）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（Origin 链）", module);

            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var invoke = main.Blocks[0].Instructions.First(i => i is InvokeNoResultInstruction);
            TestHarness.CheckTrue("invoke.noret 的 Origin 是 LoweredCallStatement",
                invoke.Origin is LoweredCallStatement,
                invoke.Origin?.GetType().Name ?? "<null>");
            var lowered = invoke.Origin as LoweredCallStatement;
            TestHarness.CheckTrue("LoweredCallStatement.Origin 是 BoundCallStatement",
                lowered?.Origin is BoundCallStatement,
                lowered?.Origin.GetType().Name ?? "<null>");
            var bound = lowered?.Origin as BoundCallStatement;
            TestHarness.CheckTrue("BoundCallStatement.Syntax 非空", bound?.Syntax != null);
            TestHarness.CheckTrue("Syntax.Span 非空", bound?.Syntax.Span != null);
            TestHarness.Check("Syntax.Span.sourceName 是用户文件名",
                bound?.Syntax.Span?.sourceName ?? "<null>", BilTestHarness.UserSourceName);

            // load 的 Origin 是字面量 LoweredNode（值经 Origin.Syntax 回取）
            var load = main.Blocks[0].Instructions.First(i => i is LoadInstruction);
            TestHarness.CheckTrue("load 的 Origin 是 LoweredLiteralExpression",
                load.Origin is LoweredLiteralExpression,
                load.Origin?.GetType().Name ?? "<null>");
        }

        // ===== §17.1 @EntryPoint 发射：注解驱动 entrypoint 修饰符 =====
        private static void TestEntryPointAnnotationEmission()
        {
            // 命名空间静态方法 + @EntryPoint → entrypoint 修饰符；
            // 裸 main 约定保留（无注解也带 entrypoint）
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "namespace app\n" +
                "@EntryPoint\n" +
                "pub func main(): i32 { return 0 }\n" +
                "pub class App {\n" +
                "    @EntryPoint\n" +
                "    pub static func run(): i32 { return 0 }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（@EntryPoint 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（@EntryPoint 双入口合法）", module);
            TestHarness.CheckTrue("命名空间 main 带 entrypoint 修饰符",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "app::$main()@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier k
                        && k.Keyword == BilKeyword.Entrypoint)));
            TestHarness.CheckTrue("静态成员 run 带 entrypoint 修饰符",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "app::App")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Symbol == "app::App$.static.run()@.i32"
                        && d.Modifiers.Any(m => m is BilKeywordModifier k
                            && k.Keyword == BilKeyword.Entrypoint)));

            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("全管线无诊断（裸 main 约定保留）", unit2);
            TestHarness.CheckTrue("裸 main 约定仍投影 entrypoint",
                module2.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "$main()@.i32"
                    && d.Modifiers.Any(m => m is BilKeywordModifier k
                        && k.Keyword == BilKeyword.Entrypoint)));
        }

        // ===== §17.2 命名空间切片：EmitWithSlices 路由与资源回填 =====
        private static void TestNamespaceSliceEmission()
        {
            var (unit, result) = BilTestHarness.EmitBilSlices(
                "namespace app\n" +
                "@EntryPoint\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"sliced\")\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（切片发射）", unit);
            BilTestHarness.CheckBilValid("merged 验证器零错误", result.Merged);

            var slices = result.Slices;
            TestHarness.CheckTrue("切片含 app 命名空间",
                slices.Any(s => s.Key == "app"));
            TestHarness.CheckTrue("切片含 core.io 命名空间",
                slices.Any(s => s.Key == "core.io"));
            var appSlice = slices.Single(s => s.Key == "app").Value;
            TestHarness.CheckTrue("app 切片含 app::main 声明与 fn",
                appSlice.LocalSymbols.OfType<BilSimpleMemberDeclaration>()
                    .Any(d => d.Symbol == "app::$main()@.i32")
                && appSlice.Functions.Any(f => f.Symbol == "app::$main()@.i32"));
            TestHarness.CheckTrue("app 切片模块名带命名空间后缀",
                appSlice.Metadata.Any(m => m.Key == "module"
                    && m.LiteralText == "\"hello.app\""));
            // app::main 引用了 "sliced" 字符串资源（跨切片共享回填：
            // 切片单文件自足——BilReader 要求 res 在本文件可解析）
            TestHarness.CheckTrue("app 切片回填了引用的字符串资源",
                appSlice.Resources.Any(r => r is BilScalarResource s
                    && s.Type == BilScalarType.String && s.LiteralText == "\"sliced\""));
            // 合并语义：各切片符号/fn 之和 == merged（切片是 partition）
            var sliceSymbolCount = slices.Sum(s => s.Value.LocalSymbols.Count);
            var sliceFunctionCount = slices.Sum(s => s.Value.Functions.Count);
            TestHarness.CheckTrue("切片符号数之和 == merged 符号数",
                sliceSymbolCount == result.Merged.LocalSymbols.Count,
                $"{sliceSymbolCount} != {result.Merged.LocalSymbols.Count}");
            TestHarness.CheckTrue("切片 fn 数之和 == merged fn 数",
                sliceFunctionCount == result.Merged.Functions.Count,
                $"{sliceFunctionCount} != {result.Merged.Functions.Count}");
            // 切片序列化 → BilReader 单文件可解析（自足性端到端）
            foreach (var (ns, slice) in slices)
            {
                if (slice.LocalSymbols.Count == 0 && slice.Functions.Count == 0) continue;
                var roundtrip = BilReader.Read(BilWriter.Write(slice));
                TestHarness.CheckTrue($"切片 {ns} 序列化往返 fn 数一致",
                    roundtrip.Functions.Count == slice.Functions.Count);
            }
        }

        // ===== 资源去重：同（类型, 原文）字面量只登记一次 =====
        private static void TestResourceDeduplication()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"same\")\n" +
                "    core.io.Console.println(\"same\")\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（资源去重）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（资源去重）", module);
            // stdlib 基线 R_0..R_33（"\n"/false/1/true/0 + 棒5a
            // 「Task 只允许启动一次」+ bindColdBody 文案 + 调度骨架 i32 2/3/4/5 + null
            // I64Queue + Mutex.release finally 空 catch-table + Mutex 校验消息
            // + 两 catch-table + Repeat 校验消息 + CoroutineLocal TValue? 初值
            // null + withValue 两 catch-table + i32 8 + Task.executor null
            // + i32 -1 + MW9b 异常消息模板 7 条 + core.time 3 条
            // + Task<TReturn> 初值 null；MW11d-D Receiver/Messenger
            // 高层 API 追加 10 条：addListener/setExecutor 校验消息、
            // 各 try/finally 空 catch-table、pumpLoop 的捕获表与
            // receiverCache 的 null 初值）
            // + R_34 = "same"——"same" 不重复登记；return 0 与基线 i32 0
            // 同键去重
            TestHarness.CheckTrue("相同字面量只登记一个资源",
                module.Resources.Count == 105
                && module.Resources.Count(r => r is BilScalarResource s
                    && s.LiteralText == "\"same\"") == 1,
                string.Join(", ", module.Resources.Select(r => r.Name)));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var loads = main.Blocks[0].Instructions.Where(i => i is LoadInstruction).ToList();
            var same = module.Resources.OfType<BilScalarResource>()
                .Single(r => r.LiteralText == "\"same\"");
            TestHarness.CheckTrue("两处引用同一资源（去重）",
                loads.Count(l => l.Operands[0] is BilResourceOperand ro
                    && ReferenceEquals(ro.Resource, same)) == 2,
                "same=" + same.Name + " loads=" + loads.Count);
        }

        // ===== 局部声明 + 初始化器（set.var）与赋值 =====
        private static void TestLocalDeclarationAndAssignment()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    x = x * 3\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（声明与赋值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（声明与赋值）", module);
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { .i32 x, .i32 .t0, .i32 .t1, .i32 .t2, .i32 .t3, .i32 .t4 }\n" +
                "load res(#0) $.t0\n" +
                "load res(#1) $.t1\n" +
                "add $.t0 $.t1 $.t2\n" +
                "set.var $.t2 $x\n" +
                "load res(#2) $.t3\n" +
                "mul $x $.t3 $.t4\n" +
                "set.var $.t4 $x\n" +
                "ret $x\n");
        }

        // ===== 一元运算与比较运算（同字面量资源去重）=====
        private static void TestUnaryAndComparison()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 5\n" +
                "    var n: i32 = -a\n" +
                "    var b: bool = n == 5\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（一元与比较）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（一元与比较）", module);
            // 两处 5 共用同一资源（stdlib 基线 34 条在前：用户 5
            // 与基线 #10 同键去重，尾部 null 是 Task<TReturn> 初值）
BilTestHarness.CheckResShape("资源（5 去重）", module,
                "#0 = string \"\\n\"\n" +
                "#1 = i32 1\n" +
                "#2 = i32 0\n" +
                "#3 = catch-table {  }\n" +
                "#4 = catch-table {  }\n" +
                "#5 = catch-table {  }\n" +
                "#6 = i32 2\n" +
                "#7 = string \"Map 快照键值长度必须相等\"\n" +
                "#8 = bool false\n" +
                "#9 = null type(.generic<$.generic.V>)\n" +
                "#10 = null type(.generic<$.generic.K>)\n" +
                "#11 = null type(core::AtomicMapSnapshot<.generic<$.generic.K>, .generic<$.generic.V>>)\n" +
                "#12 = null type(.generic<$.generic.T>)\n" +
                "#13 = null type(core::AtomicSnapshot<.generic<$.generic.T>>)\n" +
                "#14 = bool true\n" +
                "#15 = i32 8\n" +
                "#16 = string \"Task 只允许启动一次：对已完成启动的 Task 调用 run\"\n" +
                "#17 = string \"冷 Task body 无法 spawn-into：无匹配闭包\"\n" +
                "#18 = i32 3\n" +
                "#19 = i32 4\n" +
                "#20 = i32 5\n" +
                "#21 = null type(core.coroutine::I64Queue)\n" +
                "#22 = catch-table {  }\n" +
                "#23 = string \"Mutex.release：令牌不属于此 Mutex 或已释放\"\n" +
                "#24 = catch-table {  }\n" +
                "#25 = catch-table {  }\n" +
                "#26 = string \"Timer.RepeatOption.Repeat：repeatCount 必须 > 0\"\n" +
                "#27 = null type(.generic<$.generic.TValue>)\n" +
                "#28 = catch-table {  }\n" +
                "#29 = catch-table {  }\n" +
                "#30 = i32 -1\n" +
                "#31 = null type(core.coroutine::Executor)\n" +
                "#32 = string \"目标 Place 不可写\"\n" +
                "#33 = string \"无法将 \"\n" +
                "#34 = string \" 转换为 \"\n" +
                "#35 = string \"new.indirect 目标不可构造：不匹配任何 init：\"\n" +
                "#36 = string \"整数除以零\"\n" +
                "#37 = string \"数组下标越界：\"\n" +
                "#38 = string \"（长度 \"\n" +
                "#39 = string \"）\"\n" +
                "#40 = string \"对象在销毁前从未调用 dispose()：\"\n" +
                "#41 = string \"core::UndisposedResourceException: \"\n" +
                "#42 = null type(core.messaging::QueueReaderState<.generic<$.generic.TMessage>>)\n" +
                "#43 = catch-table {  }\n" +
                "#44 = null type(core.messaging::QueueState<.generic<$.generic.TMessage>>)\n" +
                "#45 = i32 -3\n" +
                "#46 = i32 -4\n" +
                "#47 = i32 -9\n" +
                "#48 = i32 -5\n" +
                "#49 = catch-table {  }\n" +
                "#50 = i32 -2\n" +
                "#51 = catch-table {  }\n" +
                "#52 = i32 -6\n" +
                "#53 = i32 -8\n" +
                "#54 = catch-table {  }\n" +
                "#55 = i32 -7\n" +
                "#56 = i32 -10\n" +
                "#57 = null type(.generic<$.generic.TMessage>)\n" +
                "#58 = null type(core.messaging::QueueLogSegment<.generic<$.generic.TMessage>>)\n" +
                "#59 = null type(core.coroutine::Mutex.Lock)\n" +
                "#60 = i32 64\n" +
                "#61 = catch-table {  }\n" +
                "#62 = string \"MessageQueue: 句柄已释放或不存在\"\n" +
                "#63 = string \"MessageQueue: 句柄重复释放\"\n" +
                "#64 = string \"MessageQueue: 不能派生 Owner\"\n" +
                "#65 = string \"MessageQueue: 不能从该句柄派生 Sender\"\n" +
                "#66 = string \"MessageQueue: 不能从该句柄派生 Reader\"\n" +
                "#67 = string \"MessageQueue: 该句柄不能 post\"\n" +
                "#68 = string \"MessageQueue: 该句柄不能 next\"\n" +
                "#69 = string \"MessageQueue: 队列已 sealed，不能 post\"\n" +
                "#70 = string \"MessageQueue: 队列已 sealed，不能派生 Sender\"\n" +
                "#71 = string \"MessageQueue: 同一 Reader 同时只能有一个 outstanding next\"\n" +
                "#72 = string \"MessageQueue: 非法操作\"\n" +
                "#73 = catch-table {  }\n" +
                "#74 = catch-table {  }\n" +
                "#75 = string \"Receiver.addListener：Receiver 已 dispose\"\n" +
                "#76 = catch-table {  }\n" +
                "#77 = catch-table {  }\n" +
                "#78 = string \"Receiver.setExecutor：listener 未注册\"\n" +
                "#79 = catch-table {  }\n" +
                "#80 = catch-table {  }\n" +
                "#81 = catch-table { type(core::IllegalStateException) -> blk(try0-catch0) }\n" +
                "#82 = catch-table {  }\n" +
                "#83 = catch-table {  }\n" +
                "#84 = null type(core.messaging::Receiver<.generic<$.generic.TMessage>>)\n" +
                "#85 = catch-table {  }\n" +
                "#86 = catch-table {  }\n" +
                "#87 = null type(.any)\n" +
                "#88 = i64 0\n" +
                "#89 = string \"检测到环引用，需显式启用 loopedRefEnabled\"\n" +
                "#90 = i64 1\n" +
                "#91 = string \"无效或重复的序列化节点编号\"\n" +
                "#92 = string \"序列化引用指向不存在的节点\"\n" +
                "#93 = string \"无效的序列化引用编号\"\n" +
                "#94 = null type(.generic<$.generic.TField>)\n" +
                "#95 = string \"Parcel 中不存在键：\"\n" +
                "#96 = i32 999999\n" +
                "#97 = string \"TimeStamp.nanoseconds 越界：\"\n" +
                "#98 = string \"（范围 0..999999）\"\n" +
                "#99 = null type(.generic<$.generic.TReturn>)\n" +
                "#100 = catch-table {  }\n" +
                "#101 = string \"..ref\"\n" +
                "#102 = string \"..id\"\n" +
                "#103 = string \"..data\"");
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { .i32 a, .i32 n, .bool b, .i32 .t0, .i32 .t1, .i32 .t2, .bool .t3 }\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $a\n" +
                "opposite $a $.t1\n" +
                "set.var $.t1 $n\n" +
                "load res(#0) $.t2\n" +
                "cmp.eq $n $.t2 $.t3\n" +
                "set.var $.t3 $b\n" +
                "ret $n\n");
        }

        // ===== 用户类型 ==/!=（SYNTAX §13.2）：映射 operator equals；发射
        // 仍为 cmp.eq/cmp.ne（VM §22.3 按精确类型派发用户 equals + 取反）=====
        private static void TestUserOperatorEqualsEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Vec { pub operator equals(other: Vec): bool { return true } }\n" +
                "pub func eq(a: Vec, b: Vec): bool { return (a == b) }\n" +
                "pub func ne(a: Vec, b: Vec): bool { return (a != b) }\n");
            CheckNoErrors("全管线无诊断（用户 ==/!= 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（用户 ==/!= 发射）", module);
            BilTestHarness.CheckFnShape("用户 ==", module, "$eq(a:Vec,b:Vec)@.bool",
                ".vars { .bool .t0 }\n" +
                "cmp.eq $a $b $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("用户 !=", module, "$ne(a:Vec,b:Vec)@.bool",
                ".vars { .bool .t0 }\n" +
                "cmp.ne $a $b $.t0\n" +
                "ret $.t0\n");
        }

        // ===== 用户类型算术/比较/一元仍发 intrinsic 指令（VM 运行时派发）=====
        private static void TestUserOperatorPlusEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Vec {\n" +
                "    pub operator plus(other: Vec): Vec { return this }\n" +
                "    pub operator compareTo(other: Vec): i32 { return 0 }\n" +
                "    pub operator opposite(): Vec { return this }\n" +
                "}\n" +
                "pub func add(a: Vec, b: Vec): Vec { return a + b }\n" +
                "pub func lt(a: Vec, b: Vec): bool { return a < b }\n" +
                "pub func neg(a: Vec): Vec { return -a }\n");
            CheckNoErrors("全管线无诊断（用户 plus/compareTo/opposite 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（用户 operator 发射）", module);
            BilTestHarness.CheckFnShape("用户 + 仍发 add", module, "$add(a:Vec,b:Vec)@Vec",
                ".vars { Vec .t0 }\n" +
                "add $a $b $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("用户 < 仍发 cmp.lt", module, "$lt(a:Vec,b:Vec)@.bool",
                ".vars { .bool .t0 }\n" +
                "cmp.lt $a $b $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("用户 - 仍发 opposite", module, "$neg(a:Vec)@Vec",
                ".vars { Vec .t0 }\n" +
                "opposite $a $.t0\n" +
                "ret $.t0\n");
        }

        // ===== 带返回值 invoke（§15.1）与表达式语句（结果物化后丢弃）=====
        private static void TestInvokeWithResult()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func double(a: i32): i32 { return a * 2 }\n" +
                "pub func main(): i32 {\n" +
                "    double(5)\n" +
                "    return double(21)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（invoke）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（invoke）", module);
            BilTestHarness.CheckFnShape("double 指令与 .vars", module, "$double(a:.i32)@.i32",
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "load res(#0) $.t0\n" +
                "mul $a $.t0 $.t1\n" +
                "ret $.t1\n");
            BilTestHarness.CheckFnShape("main 指令与 .vars（表达式语句结果丢弃）",
                module, "$main()@.i32",
                ".vars { .i32 .t0, .i32 .t1, .i32 .t2, .i32 .t3 }\n" +
                "load res(#0) $.t0\n" +
                "invoke fn($double(a:.i32)@.i32) $.t1 [$.t0]\n" +
                "load res(#1) $.t2\n" +
                "invoke fn($double(a:.i32)@.i32) $.t3 [$.t2]\n" +
                "ret $.t3\n");
        }

        // ===== new 构造（§14.1；零参无显式 init）=====
        private static void TestNew()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Empty { }\n" +
                "pub func main(): i32 {\n" +
                "    var e: Empty = new Empty()\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（new）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（new）", module);
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { Empty e, Empty .t0, .i32 .t1 }\n" +
                "new type(Empty) $.t0 []\n" +
                "set.var $.t0 $e\n" +
                "load res(#0) $.t1\n" +
                "ret $.t1\n");
        }

        // ===== §19.1 标量资源全形态（bool/f64/f32/char/null）=====
        private static void TestLiteralResources()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var b: bool = true\n" +
                "    var d: double = 0.5\n" +
                "    var f: float = 0.1f\n" +
                "    var c: char = 'A'\n" +
                "    var s: String? = null\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（标量资源）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（标量资源）", module);
BilTestHarness.CheckResShape("Resources 全形态", module,
                "#0 = string \"\\n\"\n" +
                "#1 = i32 1\n" +
                "#2 = i32 0\n" +
                "#3 = catch-table {  }\n" +
                "#4 = catch-table {  }\n" +
                "#5 = catch-table {  }\n" +
                "#6 = i32 2\n" +
                "#7 = string \"Map 快照键值长度必须相等\"\n" +
                "#8 = bool false\n" +
                "#9 = null type(.generic<$.generic.V>)\n" +
                "#10 = null type(.generic<$.generic.K>)\n" +
                "#11 = null type(core::AtomicMapSnapshot<.generic<$.generic.K>, .generic<$.generic.V>>)\n" +
                "#12 = null type(.generic<$.generic.T>)\n" +
                "#13 = null type(core::AtomicSnapshot<.generic<$.generic.T>>)\n" +
                "#14 = bool true\n" +
                "#15 = i32 8\n" +
                "#16 = string \"Task 只允许启动一次：对已完成启动的 Task 调用 run\"\n" +
                "#17 = string \"冷 Task body 无法 spawn-into：无匹配闭包\"\n" +
                "#18 = i32 3\n" +
                "#19 = i32 4\n" +
                "#20 = i32 5\n" +
                "#21 = null type(core.coroutine::I64Queue)\n" +
                "#22 = catch-table {  }\n" +
                "#23 = string \"Mutex.release：令牌不属于此 Mutex 或已释放\"\n" +
                "#24 = catch-table {  }\n" +
                "#25 = catch-table {  }\n" +
                "#26 = string \"Timer.RepeatOption.Repeat：repeatCount 必须 > 0\"\n" +
                "#27 = null type(.generic<$.generic.TValue>)\n" +
                "#28 = catch-table {  }\n" +
                "#29 = catch-table {  }\n" +
                "#30 = i32 -1\n" +
                "#31 = null type(core.coroutine::Executor)\n" +
                "#32 = string \"目标 Place 不可写\"\n" +
                "#33 = string \"无法将 \"\n" +
                "#34 = string \" 转换为 \"\n" +
                "#35 = string \"new.indirect 目标不可构造：不匹配任何 init：\"\n" +
                "#36 = string \"整数除以零\"\n" +
                "#37 = string \"数组下标越界：\"\n" +
                "#38 = string \"（长度 \"\n" +
                "#39 = string \"）\"\n" +
                "#40 = string \"对象在销毁前从未调用 dispose()：\"\n" +
                "#41 = string \"core::UndisposedResourceException: \"\n" +
                "#42 = null type(core.messaging::QueueReaderState<.generic<$.generic.TMessage>>)\n" +
                "#43 = catch-table {  }\n" +
                "#44 = null type(core.messaging::QueueState<.generic<$.generic.TMessage>>)\n" +
                "#45 = i32 -3\n" +
                "#46 = i32 -4\n" +
                "#47 = i32 -9\n" +
                "#48 = i32 -5\n" +
                "#49 = catch-table {  }\n" +
                "#50 = i32 -2\n" +
                "#51 = catch-table {  }\n" +
                "#52 = i32 -6\n" +
                "#53 = i32 -8\n" +
                "#54 = catch-table {  }\n" +
                "#55 = i32 -7\n" +
                "#56 = i32 -10\n" +
                "#57 = null type(.generic<$.generic.TMessage>)\n" +
                "#58 = null type(core.messaging::QueueLogSegment<.generic<$.generic.TMessage>>)\n" +
                "#59 = null type(core.coroutine::Mutex.Lock)\n" +
                "#60 = i32 64\n" +
                "#61 = catch-table {  }\n" +
                "#62 = string \"MessageQueue: 句柄已释放或不存在\"\n" +
                "#63 = string \"MessageQueue: 句柄重复释放\"\n" +
                "#64 = string \"MessageQueue: 不能派生 Owner\"\n" +
                "#65 = string \"MessageQueue: 不能从该句柄派生 Sender\"\n" +
                "#66 = string \"MessageQueue: 不能从该句柄派生 Reader\"\n" +
                "#67 = string \"MessageQueue: 该句柄不能 post\"\n" +
                "#68 = string \"MessageQueue: 该句柄不能 next\"\n" +
                "#69 = string \"MessageQueue: 队列已 sealed，不能 post\"\n" +
                "#70 = string \"MessageQueue: 队列已 sealed，不能派生 Sender\"\n" +
                "#71 = string \"MessageQueue: 同一 Reader 同时只能有一个 outstanding next\"\n" +
                "#72 = string \"MessageQueue: 非法操作\"\n" +
                "#73 = catch-table {  }\n" +
                "#74 = catch-table {  }\n" +
                "#75 = string \"Receiver.addListener：Receiver 已 dispose\"\n" +
                "#76 = catch-table {  }\n" +
                "#77 = catch-table {  }\n" +
                "#78 = string \"Receiver.setExecutor：listener 未注册\"\n" +
                "#79 = catch-table {  }\n" +
                "#80 = catch-table {  }\n" +
                "#81 = catch-table { type(core::IllegalStateException) -> blk(try0-catch0) }\n" +
                "#82 = catch-table {  }\n" +
                "#83 = catch-table {  }\n" +
                "#84 = null type(core.messaging::Receiver<.generic<$.generic.TMessage>>)\n" +
                "#85 = catch-table {  }\n" +
                "#86 = catch-table {  }\n" +
                "#87 = null type(.any)\n" +
                "#88 = i64 0\n" +
                "#89 = string \"检测到环引用，需显式启用 loopedRefEnabled\"\n" +
                "#90 = i64 1\n" +
                "#91 = string \"无效或重复的序列化节点编号\"\n" +
                "#92 = string \"序列化引用指向不存在的节点\"\n" +
                "#93 = string \"无效的序列化引用编号\"\n" +
                "#94 = null type(.generic<$.generic.TField>)\n" +
                "#95 = string \"Parcel 中不存在键：\"\n" +
                "#96 = i32 999999\n" +
                "#97 = string \"TimeStamp.nanoseconds 越界：\"\n" +
                "#98 = string \"（范围 0..999999）\"\n" +
                "#99 = f64 0.5\n" +
                "#100 = f32 0.1\n" +
                "#101 = char 'A'\n" +
                "#102 = null type(.string)\n" +
                "#103 = null type(.generic<$.generic.TReturn>)\n" +
                "#104 = catch-table {  }\n" +
                "#105 = string \"..ref\"\n" +
                "#106 = string \"..id\"\n" +
                "#107 = string \"..data\"");
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { .bool b, .f64 d, .f32 f, .char c, .nullable<.string> s, " +
                ".bool .t0, .f64 .t1, .f32 .t2, .char .t3, .nullable<.string> .t4, .i32 .t5 }\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $b\n" +
                "load res(#1) $.t1\n" +
                "set.var $.t1 $d\n" +
                "load res(#2) $.t2\n" +
                "set.var $.t2 $f\n" +
                "load res(#3) $.t3\n" +
                "set.var $.t3 $c\n" +
                "load res(#4) $.t4\n" +
                "set.var $.t4 $s\n" +
                "load res(#5) $.t5\n" +
                "ret $.t5\n");
        }

        // ===== static 字段读写（§13.4 get/set.field.static）=====
        private static void TestStaticFieldReadWrite()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Counter { pub static var value: i32 }\n" +
                "pub func main(): i32 {\n" +
                "    Counter.value = 42\n" +
                "    return Counter.value\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（static 字段）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（static 字段）", module);
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "load res(#0) $.t0\n" +
                "set.field.static $.t0 type(Counter) field(Counter#.static.value@.i32)\n" +
                "get.field.static $.t1 type(Counter) field(Counter#.static.value@.i32)\n" +
                "ret $.t1\n");
        }

        // ===== 负例：未覆盖节点 → P4 Error =====
        // （S7c-2 后实例方法/init/operator 已开闸，源码侧 P4 发射全覆盖——
        // 以测试私有 Lowered 子类模拟「未来新增但 BilEmitter 尚未覆盖」的
        // 节点，仿 LowererTests.TestUnsupportedNode）
        private sealed class FutureLoweredStatement : LoweredStatement
        {
            public FutureLoweredStatement(BoundNode origin) : base(origin)
            {
            }
        }

        private static void TestUnsupportedNodes()
        {
            var root = TestHarness.ParseRoot("func f() { }\n");
            var unit = new CompilationUnit(root);
            var method = new MethodSymbol("future", MethodKind.Regular);
            var boundBody = new BoundBlock(root, new List<BoundStatement>());
            var body = new LoweredFunctionBody(method, new List<LocalSymbol>(),
                new LoweredBlock(boundBody,
                    new List<LoweredStatement> { new FutureLoweredStatement(boundBody) }));
            var module = BilEmitter.Emit(unit, new[] { body }, "future");
            TestHarness.CheckSemanticError("未覆盖节点报 P4 Error", unit.Diagnostics,
                "not supported by minimal emission");
            // 夹具修补：手工 MethodSymbol 未经 P1 收集，模块缺其声明——补上
            // 以聚焦「坏函数体跳过」本身的结构健康
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$future()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            // 坏函数体跳过后产出的模块仍应过验证器
            BilTestHarness.CheckBilValid("验证器零错误（未覆盖节点跳过坏函数体）", module);
        }

        // ===== toString 机制（SYNTAX §3.8 修订）：合成默认体 + any_to_string =====
        private static void TestBuiltinToStringEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(HelloWorldSource);
            CheckNoErrors("全管线无诊断（toString 合成体）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（toString 合成体）", module);

            // Any/Object 的 toString 不再是 native 成员——无任何 toString
            // 成员声明落地（内建宿主不进符号段；native 三件套不再发射）
            TestHarness.CheckTrue("Any/Object 无 toString 成员声明",
                !module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol.StartsWith("core::Any$toString")
                    || d.Symbol.StartsWith("core::Object$toString")));

            // any_to_string 以 priv native 全局声明落地（native 三件套；
            // 全局函数 canonical 带 $ 名段，同 $main 形态）
            var anyToString = module.LocalSymbols.OfType<BilSimpleMemberDeclaration>()
                .FirstOrDefault(d => d.Symbol == "core::$any_to_string(value:.any)@.string");
            TestHarness.CheckTrue("any_to_string 以 priv native 全局声明落地",
                anyToString != null
                && anyToString.Modifiers.OfType<BilAccessibilityModifier>()
                    .First().Accessibility == BilAccessibility.Private
                && anyToString.Modifiers.OfType<BilKeywordModifier>()
                    .Any(m => m.Keyword == BilKeyword.Native)
                && anyToString.Modifiers.OfType<BilNativeSymbolModifier>()
                    .First().Symbol == "any_to_string"
                && anyToString.Modifiers.OfType<BilNativeLibraryModifier>()
                    .First().Library == "rigi_rt");

            // 两个合成默认体（Any 的 .this 即 .any 直传；Object 先装箱 cast）
            BilTestHarness.CheckFnShape("Any.toString 合成体", module,
                "core::Any$toString()@.string",
                ".vars { .string .t0 }\n" +
                "invoke fn(core::$any_to_string(value:.any)@.string) $.t0 [$.this]\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("Object.toString 合成体（.this 先装箱 .any）", module,
                "core::Object$toString()@.string",
                ".vars { .any .t0, .string .t1 }\n" +
                "cast $.this $.t0 type(.any)\n" +
                "invoke fn(core::$any_to_string(value:.any)@.string) $.t1 [$.t0]\n" +
                "ret $.t1\n");
        }
    }
}
