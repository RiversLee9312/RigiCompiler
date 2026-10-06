using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Gate;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Passes;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Tests
{
    public static partial class MiddlewareTests
    {
        // WrapperRouting 职责；与主文件共享同一类型、字段及生命周期。

        // ===== router get/set 分支（MW10 刀3b） =====

        // 双层宿主（外 A 内 B，均带 .proxy.*/.proxy.get.*/.proxy.set.*）：
        // router(1) 的 if 链覆盖字段访问器符号（S3$.get|set.title@T），
        // 分支自 fromLayer=1 起进字段 Entity 环链——get 分支调 getter
        // 旁路体（$.wrapped..get.）+B 层 get 环，set 分支调 B 层 set 环
        private static void TestEntityFieldRouterBranchEmission()
        {
            const string wrapper =
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n";
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper A {\n" + wrapper + "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper B {\n" + wrapper + "}\n" +
                "@A\n" +
                "@B\n" +
                "pub class S3 {\n" +
                "    pub var title: String {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { }\n" +
                "    } = \"t\"\n" +
                "    pub init()\n" +
                "    pub func ping(): i32 { return 1 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new S3()\n" +
                "    return s.ping()\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.entityrouter.bil");
            CaseAssertions.CheckTrue("router 字段分支用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;

            var router = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("S3$.mw.router.1("));
            var routerCalls = router.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirCall>().ToList();
            var resources = context.Module.Resources.OfType<BilScalarResource>()
                .Select(r => r.LiteralText).ToList();
            CaseAssertions.CheckTrue("router 资源含 get/set 访问器符号",
                resources.Any(r => r.Contains("S3$.get.title@.string"))
                && resources.Any(r => r.Contains("S3$.set.title@.string")));
            CaseAssertions.CheckTrue("router get 分支调 getter 旁路体",
                routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("S3$.wrapped..get.title@")));
            CaseAssertions.CheckTrue("router get 分支自 fromLayer 起调 B 层 get 环",
                routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("B$.bake.S3$title$.get")));
            CaseAssertions.CheckTrue("router set 分支自 fromLayer 起调 B 层 set 环",
                routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("B$.bake.S3$title$.set")));
            CaseAssertions.CheckTrue("router 无 A 层字段环（layer < fromLayer 不进链）",
                !routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("A$.bake.S3$title$")));
            CaseAssertions.CheckTrue("访问器不落方法成员分支（无 $.wrapped..set.title 直调外环）",
                !routerCalls.Any(c =>
                    c.Target.Canonical.StartsWith("A$.bake.S3$.set.title@")
                    || c.Target.Canonical.StartsWith("A$.bake.S3$.get.title@")));

            using var llvmLease3421 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 router 与字段环",
                ll.Contains(".mw.router.1") && ll.Contains(".bake.S3$title"), ll);
        }

        // MW10 遗留④：wildcard set 环 inner 的跨字段重路由分派形状——
        // WA 层 wildcard 环 inner 改写为「symbol == 原字段 canonical 的
        // fast-path（静态链路）+ 否则调分派辅助 H$.mw.srt.1.<名>」双分支；
        // 分派辅助按 canonical 逐字段比对：specific 分支进他字段环变体
        //（$.setr.，终态仍指原字段），miss 落原字段终态；零 MirInnerCall
        private static void TestSetRingInnerRerouteDispatchEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#mp@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.mp\\<TField>(value: TField) { inner(value) }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub var mp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    return e.hp\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.setreroute.bil");
            CaseAssertions.CheckTrue("set 重路由分派用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("set 重路由烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var callsOf = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // hp 的 WA 层 wildcard 环：fast/slow CFG 分割（恒等 fast-path
            // 调原字段终态，slow 调分派辅助）
            var ring = Fn("WA$.bake.Entity$hp$.set(");
            var ringCalls = callsOf(ring);
            CaseAssertions.CheckTrue("hp 环含条件分支（fast-path + 分派双分支）",
                ring.Blocks.Any(b => b.Terminator is MirCondBranch));
            CaseAssertions.CheckTrue("hp 环 fast-path 直调原字段终态",
                ringCalls.Any(c =>
                    c.Target.Canonical.StartsWith("Entity$hp$.wrapped.set(")));
            CaseAssertions.CheckTrue("hp 环 slow 路径调分派辅助",
                ringCalls.Any(c =>
                    c.Target.Canonical.StartsWith("Entity$.mw.srt.1.hp(")));

            // 分派辅助：specific 分支（mp canonical 比对）进变体环，
            // miss 落原字段终态
            var dispatch = Fn("Entity$.mw.srt.1.hp(symbol:.string,value:.any)@.void");
            var dispatchCalls = callsOf(dispatch);
            var resources = context.Module.Resources.OfType<BilScalarResource>()
                .Select(r => r.LiteralText).ToList();
            CaseAssertions.CheckTrue("分派辅助资源含 mp 字段 canonical 比对字面量",
                resources.Any(r => r.Contains("Entity#mp@.i32")));
            CaseAssertions.CheckTrue("分派辅助 specific 分支进 mp 变体环",
                dispatchCalls.Any(c =>
                    c.Target.Canonical.StartsWith("WB$.bake.Entity$mp$.setr.hp.1(")));
            CaseAssertions.CheckTrue("分派辅助 miss 落原字段终态",
                dispatchCalls.Any(c =>
                    c.Target.Canonical.StartsWith("Entity$hp$.wrapped.set(")));

            // 变体环：inner 续跑无更多环 → 落原字段终态（VM FieldSymbol
            // 恒为原字段同口径）
            var variant = Fn("WB$.bake.Entity$mp$.setr.hp.1(");
            CaseAssertions.CheckTrue("mp 变体环 inner 落原字段终态",
                callsOf(variant).Any(c =>
                    c.Target.Canonical.StartsWith("Entity$hp$.wrapped.set(")));

            using var llvmLease3514 = LlvmHost.Enter();
            using var module2 = ModuleBuilder.Build(context, context.Mir!);
            var ll2 = module2.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含分派辅助与变体环",
                ll2.Contains(".mw.srt.1.hp") && ll2.Contains(".setr.hp.1"), ll2);
        }

        // MW10 遗留④形状守恒：剩余层无任何 set proxy 时分派恒落原字段
        // 终态（与恒等等价），环 inner 保持线性链接不引 CFG 分割、不产
        // 分派辅助
        private static void TestSetRingInnerRerouteLinearWhenNoBranches()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    return e.hp\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.setreroute.linear.bil");
            CaseAssertions.CheckTrue("线性守恒用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            CaseAssertions.CheckTrue("线性守恒烘焙后无残留 MirInnerCall",
                !functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                    .OfType<MirInnerCall>().Any());
            var ring = functions.Single(f =>
                f.Symbol.Canonical.Contains("Audit$.bake.Entity$hp$.set("));
            CaseAssertions.CheckTrue("无分派需求时环保持线性（无 CondBranch）",
                !ring.Blocks.Any(b => b.Terminator is MirCondBranch));
            CaseAssertions.CheckTrue("无分派需求时不产分派辅助 fn",
                !functions.Any(f => f.Symbol.Canonical.Contains("$.mw.srt.")));
        }

        // ===== wildcard Entity proxy 烘焙（MW10 刀3a）：胖值 ABI 链 =====

        // 单层 wildcard 全链形状：trampoline 打包（symbol 资源 + 空 named
        // 包 + unnamed 装箱包）→ baked 环（动态分派块：symbol 字符串比对
        // 命中解包直进 $.wrapped.，miss 调 router）→ router（if 链覆盖全部
        // 可烘焙成员，miss 抛 NoSuchMethodException）
        private static void TestWildcardProxyBakingEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Service$zap(x:.i32)@.i32\") {\n" +
                "            return (99 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "    pub func zap(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.wildcard.bil");
            CaseAssertions.CheckTrue("wildcard 烘焙用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("wildcard 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var instsOf = new Func<MirFunction, List<MirInst>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).ToList());
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                instsOf(f).OfType<MirCall>().ToList());

            // 环符号：TReturn 代入成员返回类型；ping/zap 各一环 + $.wrapped. 终态
            var pingRing = Fn("Router$.bake.Service$ping(x:.i32)@core::i32");
            var zapRing = Fn("Router$.bake.Service$zap(x:.i32)@core::i32");
            var pingWrapped = Fn("Service$.wrapped.ping");
            CaseAssertions.CheckTrue("zap 有 $.wrapped. 终态",
                functions.Any(f => f.Symbol.Canonical.Contains("Service$.wrapped.zap")));

            // trampoline：get.wrapper + symbol 资源 + 两个包数组 + 调环
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$ping(x:.i32)@.i32");
            var trampInsts = instsOf(trampoline);
            CaseAssertions.CheckTrue("wildcard trampoline 打包（get.wrapper.addr + symbol 资源 + 包数组）",
                trampInsts.OfType<MirGetWrapperAddr>().Any(g => g.WrapperType == "Router")
                && trampInsts.OfType<MirLoadResource>().Any()
                && trampInsts.OfType<MirNewArray>().Count() == 2
                && calls(trampoline).Any(c =>
                    c.Target.Canonical == pingRing.Symbol.Canonical));
            CaseAssertions.CheckTrue("trampoline unnamed 包逐参装箱 .any",
                trampInsts.OfType<MirBoxAny>().Any());

            // 环形参：.generic 三包已擦除/代入（.this + symbol + 两包）
            CaseAssertions.CheckTrue("wildcard 环形参擦除 .generic 三包",
                pingRing.Parameters.Count == 5 && pingRing.Parameters.Last().Name == WrapperSelfParameterPass.SelfParameter
                && !pingRing.Parameters.Any(p => p.Name.StartsWith(".generic.")));

            // 动态分派块：字符串比对 + 双分支；hit 解包直进 $.wrapped.，
            // miss 调 router 并拆回 .any
            CaseAssertions.CheckTrue("环内含 symbol 字符串比对",
                instsOf(pingRing).OfType<MirBinaryIntrinsic>().Any(b =>
                    b.Op == BilBinaryOp.CmpEq && b.LeftType.IsString));
            CaseAssertions.CheckTrue("环内含条件分支（hit/miss）",
                pingRing.Blocks.SelectMany(b => new[] { b.Terminator })
                    .OfType<MirCondBranch>().Any());
            CaseAssertions.CheckTrue("hit 分支解包直进 $.wrapped.（get.array + unbox）",
                instsOf(pingRing).OfType<MirGetArray>().Any()
                && instsOf(pingRing).OfType<MirUnboxAny>().Any()
                && calls(pingRing).Any(c =>
                    c.Target.Canonical == pingWrapped.Symbol.Canonical));
            var router = Fn("Service$.mw.router.1");
            CaseAssertions.CheckTrue("miss 分支调 router(H, 2 层界=1)",
                calls(pingRing).Any(c => c.Target.Canonical == router.Symbol.Canonical));

            // zap 环：(99 as TReturn) 的 .generic.TReturn 就地物化 getid.type
            CaseAssertions.CheckTrue("zap 环物化 .generic.TReturn typeid",
                instsOf(zapRing).OfType<MirGetTypeId>().Any(g =>
                    g.TypeRef.Contains("i32") && g.Target == ".generic.TReturn"));

            // router：if 链覆盖 ping/zap 两成员（fromLayer=1 无环 → 直调
            // $.wrapped. 终态），miss 抛 NoSuchMethodException
            CaseAssertions.CheckTrue("router 分支直调 $.wrapped. 终态（不回调 trampoline）",
                calls(router).Any(c => c.Target.Canonical == pingWrapped.Symbol.Canonical)
                && calls(router).Any(c => c.Target.Canonical.Contains("Service$.wrapped.zap"))
                && !calls(router).Any(c =>
                    c.Target.Canonical == trampoline.Symbol.Canonical));
            CaseAssertions.CheckTrue("router miss 抛 NoSuchMethodException",
                instsOf(router).OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical == "core::NoSuchMethodException")
                && instsOf(router).OfType<MirThrow>().Any()
                && router.Blocks.Any(b => b.Terminator is MirRetThrow));
            CaseAssertions.CheckTrue("router 结果装箱 .any（值类型 box）",
                instsOf(router).OfType<MirBoxAny>().Any());

            using var llvmLease3675 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 wildcard 环", ll.Contains(".bake.Service$ping"), ll);
            CaseAssertions.CheckTrue("LLVM 含 router", ll.Contains(".mw.router.1"), ll);
            CaseAssertions.CheckTrue("LLVM 含 $.wrapped. 原始体", ll.Contains(".wrapped."), ll);
        }

        // 遗6：泛型宿主成员经 wildcard 的烘焙形状——trampoline 把方法级
        // typeid 隐藏形参随值实参同装箱进 unnamed 位置包（声明序居值参
        // 前：MirBoxAny 两次）；环特化 TReturn 擦除 .any（泛型占位返回
        // 无 TypeSheet，环 ABI 以胖值承载）；终态 hit 分支从包首解包
        // typeid（MirGetArray + MirUnboxAny 到 .typeid 槽）再解值参，
        // 调 $.wrapped. 原始泛型体；router 直调分支同形（typeid 随包
        // 透传，无独立类型包渠道）
        private static void TestGenericWildcardBakingEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func pick\\<T>(x: T): T { return x }\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.pick\\<i32>(41)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.generic.wildcard.bil");
            CaseAssertions.CheckTrue("泛型 wildcard 烘焙用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("泛型 wildcard 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var instsOf = new Func<MirFunction, List<MirInst>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).ToList());
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                instsOf(f).OfType<MirCall>().ToList());

            // 环符号：TReturn 擦除 .any（泛型占位返回无 TypeSheet）
            var pickRing = Fn("Router$.bake.Service$pick");
            CaseAssertions.CheckTrue("泛型成员环返回擦除 .any",
                pickRing.ReturnType.IsAny);
            var pickWrapped = Fn("Service$.wrapped.pick");

            // trampoline：原名槽保留原签名（含 .generic.T 隐藏形参），
            // 打包 = symbol 资源 + 空 named 包 + unnamed 装箱包——
            // typeid 经 MirBoxAny 装箱居包首，占位值参是胖槽透转
            //（装箱在调用点边界已完成；VM BoxConcreteArgs 全量 VmAny
            // 包装的同构形态），unnamed 包恰 2 元素
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$pick(x:.generic<$.generic.T>)@.generic<$.generic.T>");
            CaseAssertions.CheckTrue("泛型 trampoline 保留 .generic.T 隐藏形参",
                trampoline.Parameters.Any(p => p.Name == ".generic.T"));
            var trampInsts = instsOf(trampoline);
            CaseAssertions.CheckTrue("泛型 trampoline 打包（typeid 装箱 + 占位值透转）",
                trampInsts.OfType<MirGetWrapperAddr>().Any()
                && trampInsts.OfType<MirBoxAny>().Any()
                && trampInsts.OfType<MirNewArray>().Any(n =>
                    n.Elements.Count == 2)
                && calls(trampoline).Any(c =>
                    c.Target.Canonical == pickRing.Symbol.Canonical));

            // 终态 hit 分支：包首 MirGetArray + MirUnboxAny 到 .typeid
            // 槽（方法级 typeid 恢复），随后调 $.wrapped. 原始泛型体
            var ringInsts = instsOf(pickRing);
            CaseAssertions.CheckTrue("泛型终态解包含 typeid 槽",
                ringInsts.OfType<MirUnboxAny>().Any(u =>
                    pickRing.FindLocal(u.Target).Type.Canonical.Contains(".typeid")
                    || pickRing.FindLocal(u.Target).Type.Canonical.Contains("core::Type")));
            CaseAssertions.CheckTrue("泛型终态直调 $.wrapped. 体",
                calls(pickRing).Any(c =>
                    c.Target.Canonical == pickWrapped.Symbol.Canonical));

            // router：分支直调 $.wrapped. 终态（typeid 随包透传，签名
            // 仍三包无独立类型包渠道），不调 trampoline
            var router = Fn("Service$.mw.router.1");
            CaseAssertions.CheckTrue("泛型 router 直调 $.wrapped.（包透传）",
                calls(router).Any(c => c.Target.Canonical == pickWrapped.Symbol.Canonical)
                && !calls(router).Any(c =>
                    c.Target.Canonical == trampoline.Symbol.Canonical)
                && router.Parameters.Count == 4);

            using var llvmLease3776 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含泛型 wildcard 环", ll.Contains(".bake.Service$pick"),
                ll);
        }

        // specific+wildcard 混合链：外层 WOuter specific、内层 WInner
        // wildcard（转录 VM TestWildcardInnerMiddleOfWrapperChain）——
        // specific 环 inner 打包进 wildcard 环；wildcard 环 hit 直进终态；
        // 只合成 router(H,2)
        private static void TestMixedSpecificWildcardBakingEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
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
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
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
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.mixed.bil");
            CaseAssertions.CheckTrue("混合链用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("混合链烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            var outerRing = Fn("WOuter$.bake.Service$ping");
            var innerRing = Fn("WInner$.bake.Service$ping");
            // specific 环 inner → wildcard 下一环：get.wrapper.addr(WInner) +
            // symbol 资源 + unnamed 装箱包 + 调内层环
            var outerInsts = outerRing.Blocks.SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("specific 环 inner 打包进 wildcard 环",
                outerInsts.OfType<MirGetWrapperAddr>().Any(g => g.WrapperType == "WInner")
                && outerInsts.OfType<MirLoadResource>().Any()
                && outerInsts.OfType<MirNewArray>().Any()
                && outerInsts.OfType<MirBoxAny>().Any()
                && calls(outerRing).Any(c =>
                    c.Target.Canonical == innerRing.Symbol.Canonical));
            // wildcard 环 hit 直进 $.wrapped. 终态
            CaseAssertions.CheckTrue("wildcard 内环 hit 直进 $.wrapped.",
                calls(innerRing).Any(c =>
                    c.Target.Canonical.Contains("Service$.wrapped.ping")));
            // 首环 specific：trampoline 为既有直传形态（不打包）
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$ping(x:.i32)@.i32");
            CaseAssertions.CheckTrue("首环 specific：trampoline 直调外环不打包",
                calls(trampoline).Any(c =>
                    c.Target.Canonical == outerRing.Symbol.Canonical)
                && !trampoline.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirNewArray>().Any());
            // wildcard 在第 1 层（inner）：只合成 router(H,2)
            CaseAssertions.CheckTrue("只合成 router(H,2)",
                functions.Any(f => f.Symbol.Canonical.Contains("Service$.mw.router.2"))
                && !functions.Any(f => f.Symbol.Canonical.Contains("Service$.mw.router.1")));

            using var llvmLease3861 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含混合链双环",
                ll.Contains("WOuter$.bake.") && ll.Contains("WInner$.bake."), ll);
        }

    }
}
