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
        // MethodWrappers 职责；与主文件共享同一类型、字段及生命周期。

        // ===== Method wrapper 烘焙（MW10 刀6，§14.4） =====

        // specific 实例方法链：trampoline 在实现槽 fn（
        // MirGetWrapperMethodAddr 取槽 → 调首环），原始体外移
        // $.mwrapped.，环 inner 直调 $.mwrapped.（接收者 = 宿主本体）
        private static void TestMethodProxyBakingEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"before\")\n" +
                "        var r = inner(x)\n" +
                "        return r\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(21)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.method.bil");
            CaseAssertions.CheckTrue("Method wrapper 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("Method 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // trampoline = 原名槽 fn（Service$fetch）
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$fetch(x:.i32)@.i32");
            var trampInsts = trampoline.Blocks.SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("trampoline 经 get.wrapper.method.addr 取 Timed 槽",
                trampInsts.OfType<MirGetWrapperMethodAddr>().Any(g =>
                    g.MethodSymbol == "Service$fetch(x:.i32)@.i32" && g.WrapperType == "Timed"));
            var ring = Fn("Timed$.bake.Service$fetch");
            CaseAssertions.CheckTrue("trampoline 调首环（specific 不打包）",
                calls(trampoline).Any(c => c.Target.Canonical == ring.Symbol.Canonical)
                && !trampInsts.OfType<MirNewArray>().Any());
            // 环 inner → $.mwrapped. 终态（receiver = 宿主）
            var raw = Fn("Service$.mwrapped.fetch");
            CaseAssertions.CheckTrue("环 inner 直调 $.mwrapped. 原始体",
                calls(ring).Any(c => c.Target.Canonical == raw.Symbol.Canonical));
            CaseAssertions.CheckTrue("$.mwrapped. 保留原始方法体（mul 指令）",
                raw.Blocks.SelectMany(b => b.Instructions).OfType<MirBinaryIntrinsic>()
                    .Any(b => b.Op == RigiCompiler.Bil.BilBinaryOp.Mul));
            CaseAssertions.CheckTrue("环特化剔除 .generic.TReturn 形参",
                ring.Parameters.Count == 3 && ring.Parameters.Last().Name == WrapperSelfParameterPass.SelfParameter
                && ring.Parameters[0].Name == ".this"
                && ring.Parameters[1].Name == "x");

            using var llvmLease4520 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 method 环与 $.mwrapped. 体",
                ll.Contains("Timed$.bake.Service$fetch") && ll.Contains("$.mwrapped.fetch"),
                ll);
        }

        // 双层 specific：outer 环 inner → inner 环（receiver 经槽地址），
        // inner 环 → $.mwrapped.（outer→inner 声明序 = 安装序）
        private static void TestMethodProxyBakingDoubleLayer()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @A\n" +
                "    @B\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(42)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.method2.bil");
            CaseAssertions.CheckTrue("双层 Method wrapper 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            var outer = Fn("A$.bake.Service$fetch");
            var inner = Fn("B$.bake.Service$fetch");
            var raw = Fn("Service$.mwrapped.fetch");
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$fetch(x:.i32)@.i32");
            CaseAssertions.CheckTrue("trampoline 调最外环 A",
                calls(trampoline).Any(c => c.Target.Canonical == outer.Symbol.Canonical)
                && trampoline.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapperMethodAddr>().Any(g => g.WrapperType == "A"));
            CaseAssertions.CheckTrue("A 环 inner → B 环（receiver 经 B 槽地址）",
                calls(outer).Any(c => c.Target.Canonical == inner.Symbol.Canonical)
                && outer.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapperMethodAddr>().Any(g => g.WrapperType == "B"));
            CaseAssertions.CheckTrue("B 环 inner → $.mwrapped. 终态",
                calls(inner).Any(c => c.Target.Canonical == raw.Symbol.Canonical));
        }

        // wildcard 环：trampoline 打包（.name 资源 = 实现槽 canonical +
        // 具名包 Pair 逐项），环 inner 恒等转发解包直进 $.mwrapped.；
        // 环 ABI 返回 .any、trampoline 拆回原返回类型
        private static void TestMethodProxyBakingWildcard()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(.name)\n" +
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
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.methodwc.bil");
            CaseAssertions.CheckTrue("wildcard Method wrapper 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("wildcard Method 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            var trampoline = functions.Single(f =>
                f.Symbol.Canonical == "Service$fetch(x:.i32)@.i32");
            var trampInsts = trampoline.Blocks.SelectMany(b => b.Instructions).ToList();
            var ring = Fn("Timed$.bake.Service$fetch");
            CaseAssertions.CheckTrue("wildcard trampoline 打包具名包（Pair 逐项 + 数组）",
                trampInsts.OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical.Contains("Pair"))
                && trampInsts.OfType<MirNewArray>().Any());
            CaseAssertions.CheckTrue(".name 资源 = 实现槽 canonical",
                context.Module.Resources.OfType<BilScalarResource>().Any(r =>
                    r.LiteralText.Contains("Service$fetch(x:.i32)@.i32")));
            CaseAssertions.CheckTrue("wildcard trampoline 调首环",
                calls(trampoline).Any(c => c.Target.Canonical == ring.Symbol.Canonical));
            // 环 inner：按名解包（$mw.named.lookup 逐形参查找）→ 调终态，
            // 结果装箱 .any
            var ringInsts = ring.Blocks.SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("wildcard 环 inner 按名解包直进 $.mwrapped.",
                calls(ring).Any(c => c.Target.Canonical.Contains("$mw.named.lookup"))
                && calls(ring).Any(c =>
                    c.Target.Canonical.Contains("Service$.mwrapped.fetch"))
                && ringInsts.OfType<MirBoxAny>().Any());
            // 按名还原合成 fn：单实例、key 内容相等比对（string CmpEq）、
            // 命中取 value、缺名补 null（VM UnboxNamedArgs 同口径）
            var lookup = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("$mw.named.lookup("));
            var lookupInsts = lookup.Blocks.SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("$mw.named.lookup 按 key 内容相等查找",
                lookupInsts.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol == ProxyWildcardAbi.PairKeyFieldSymbol)
                && lookupInsts.OfType<MirBinaryIntrinsic>().Any(b =>
                    b.Op == RigiCompiler.Bil.BilBinaryOp.CmpEq
                    && b.LeftType.IsString)
                && lookupInsts.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol == ProxyWildcardAbi.PairValueFieldSymbol));
            CaseAssertions.CheckTrue("$mw.named.lookup 缺名补 null（.any 零值胖引用）",
                lookupInsts.OfType<MirLoadResource>().Any(l =>
                    l.Resource is BilNullResource nullRes && nullRes.TypeRef == ".any")
                && lookup.Blocks.Any(b => b.Terminator is MirCondBranch)
                && lookupInsts.OfType<MirGetArray>().Any());

            using var llvmLease4662 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 wildcard method 环",
                ll.Contains("Timed$.bake.Service$fetch"), ll);
        }

        // 按名还原（遗留12 任务①，对齐 VM UnboxNamedArgs）：乱序/缺名
        // 与位置无关——环 inner 对每具名形参发一次 $mw.named.lookup
        //（名资源逐参物化），合成 fn 模块级单实例（多方法烘焙共享）；
        // 缺名支返 .any 零值胖引用（VM 缺名补 VmNull 同口径）
        private static void TestMethodProxyWildcardUnpackByName()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func add(a: i32, b: i32): i32 { return (a + b) }\n" +
                "    @Timed\n" +
                "    pub func sub(a: i32, b: i32): i32 { return (a - b) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return (s.add(1, 2) + s.sub(3, 4))\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.methodwc.names.bil");
            CaseAssertions.CheckTrue("按名还原用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // 模块级单实例（两方法烘焙共享同一 lookup）
            var lookups = functions.Where(f =>
                f.Symbol.Canonical.StartsWith("$mw.named.lookup(")).ToList();
            CaseAssertions.CheckTrue("$mw.named.lookup 模块级单实例", lookups.Count == 1);
            var lookup = lookups[0];

            foreach (var name in new[] { "add", "sub" })
            {
                var ring = functions.Single(f =>
                    f.Symbol.Canonical.Contains("Timed$.bake.Service$" + name));
                var ringInsts = ring.Blocks.SelectMany(b => b.Instructions).ToList();
                var lookupCalls = calls(ring).Where(c =>
                    c.Target.Canonical == lookup.Symbol.Canonical).ToList();
                CaseAssertions.CheckTrue(name + " 环 inner 逐具名形参一次 lookup",
                    lookupCalls.Count == 2);
                // 每参名资源物化后作 lookup 第二实参（按名而非按位）
                var nameArgs = lookupCalls.Select(c =>
                    ((MirLocalOperand)c.Args[1]).Name).ToList();
                var literals = ringInsts.OfType<MirLoadResource>()
                    .Where(l => nameArgs.Contains(l.Target))
                    .Select(l => ((BilScalarResource)l.Resource).LiteralText).ToList();
                CaseAssertions.CheckTrue(name + " 环 lookup 名资源 = 形参名（a/b）",
                    literals.Contains("\"a\"") && literals.Contains("\"b\""));
                CaseAssertions.CheckTrue(name + " 环 inner 直进 $.mwrapped.",
                    calls(ring).Any(c =>
                        c.Target.Canonical.Contains("Service$.mwrapped." + name)));
            }

            // 乱序/缺名由 lookup 按名语义兜底：key 内容相等命中、全包
            // 扫描环、缺名返 null（VM UnboxNamedArgs 同口径）
            var lookupInsts = lookup.Blocks.SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("lookup 读 Pair key 字段",
                lookupInsts.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol == ProxyWildcardAbi.PairKeyFieldSymbol));
            CaseAssertions.CheckTrue("lookup key 内容相等比对（string CmpEq）",
                lookupInsts.OfType<MirBinaryIntrinsic>().Any(b =>
                    b.Op == RigiCompiler.Bil.BilBinaryOp.CmpEq
                    && b.LeftType.IsString));
            CaseAssertions.CheckTrue("lookup 全包扫描环（CondBranch + Branch）",
                lookup.Blocks.Any(b => b.Terminator is MirCondBranch)
                && lookup.Blocks.Any(b => b.Terminator is MirBranch));
            CaseAssertions.CheckTrue("lookup 缺名补 null（VM 同口径）",
                lookupInsts.OfType<MirLoadResource>().Any(l =>
                    l.Resource is BilNullResource nullRes && nullRes.TypeRef == ".any"));

            using var llvmLease4748 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 $mw.named.lookup",
                ll.Contains("mw.named.lookup"), ll);
        }

        // 宿主形态覆盖：静态方法（companion 实例 fn 被 trampoline、静态
        // 壳不动）、全局函数（..globals.host 实例 fn）、lambda（
        // ..lambda..UUID$$call fn）
        private static void TestMethodProxyBakingHostForms()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
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
                "@Timed\n" +
                "pub func heavy(x: i32): i32 { return (x + 2) }\n" +
                "pub func main(): i32 {\n" +
                "    var fn = func{ @Timed (x: i32): i32 -> (x + 3) }\n" +
                "    return ((Calc.total(1) + heavy(1)) + fn(1))\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.methodhosts.bil");
            CaseAssertions.CheckTrue("宿主形态用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;

            bool IsMethodTrampoline(MirFunction f) =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperMethodAddr>()
                    .Any();

            // 静态：companion 实例 fn 换 trampoline；壳体直调之（自然命中）
            var companionFn = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("Calc...companion$total("));
            CaseAssertions.CheckTrue("companion 实例 fn 是 method trampoline",
                IsMethodTrampoline(companionFn));
            var shell = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("Calc$.static.total("));
            CaseAssertions.CheckTrue("静态壳体不是 trampoline（直调 companion fn）",
                !IsMethodTrampoline(shell)
                && shell.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical == companionFn.Symbol.Canonical));
            CaseAssertions.CheckTrue("companion 原始体外移 $.mwrapped.",
                functions.Any(f => f.Symbol.Canonical.Contains(
                    "Calc...companion$.mwrapped.total")));

            // 全局函数：..globals.host 实例 fn 换 trampoline；$heavy 壳不动
            var hostFn = functions.Single(f =>
                f.Symbol.Canonical == "..globals.host$heavy(x:.i32)@.i32");
            CaseAssertions.CheckTrue("globals.host 实例 fn 是 method trampoline",
                IsMethodTrampoline(hostFn));
            CaseAssertions.CheckTrue("globals.host 原始体外移 $.mwrapped.",
                functions.Any(f => f.Symbol.Canonical.Contains(
                    "..globals.host$.mwrapped.heavy")));

            // lambda：$$call fn 换 trampoline（invoke.indirect 经 vtable 命中）
            var callFn = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("..lambda..")
                && f.Symbol.Canonical.Contains("$$call(x:.i32)"));
            CaseAssertions.CheckTrue("lambda $$call fn 是 method trampoline",
                IsMethodTrampoline(callFn));
            CaseAssertions.CheckTrue("lambda 原始体外移 $.mwrapped.",
                functions.Any(f => f.Symbol.Canonical.Contains("$.mwrapped.")
                    && f.Symbol.Canonical.Contains("$call(x:.i32)")));

            using var llvmLease4822 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含三形态烘焙环",
                ll.Contains("Timed$.bake.Calc...companion$total")
                && ll.Contains("Timed$.bake...globals.host$heavy")
                && ll.Contains("$$call"), ll);
        }

        // Entity×Method 三层组合：M 原名槽 = Entity trampoline
        //（get.wrapper.addr），$.wrapped. = method trampoline（
        // get.wrapper.method.addr），$.mwrapped. = 最深层原始体
        private static void TestMethodProxyEntityComposition()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Ent {\n" +
                "    pub init()\n" +
                "    operator .proxy.work(): i32 { return inner() }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Met {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn { return inner() }\n" +
                "}\n" +
                "@Ent\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Met\n" +
                "    pub func work(): i32 { return 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.work()\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.compose.bil");
            CaseAssertions.CheckTrue("Entity×Method 组合用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("组合烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            var outermost = functions.Single(f =>
                f.Symbol.Canonical == "Service$work()@.i32");
            CaseAssertions.CheckTrue("原名槽 = Entity trampoline（get.wrapper.addr Ent）",
                outermost.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperAddr>()
                    .Any(g => g.WrapperType == "Ent"));
            var entityRing = functions.Single(f =>
                f.Symbol.Canonical.Contains("Ent$.bake.Service$work"));
            CaseAssertions.CheckTrue("Entity 环 inner → $.wrapped.（method trampoline）",
                entityRing.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical == "Service$.wrapped.work()@core::i32"));
            var methodLayer = functions.Single(f =>
                f.Symbol.Canonical == "Service$.wrapped.work()@core::i32");
            CaseAssertions.CheckTrue("$.wrapped. = method trampoline（Met 槽地址 → Met 环）",
                methodLayer.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapperMethodAddr>().Any(g => g.WrapperType == "Met")
                && methodLayer.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("Met$.bake.Service$work")));
            var methodRing = functions.Single(f =>
                f.Symbol.Canonical.Contains("Met$.bake.Service$work"));
            CaseAssertions.CheckTrue("Method 环 inner → $.mwrapped. 最深层原始体",
                methodRing.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical == "Service$.mwrapped.work()@core::i32"));

            using var llvmLease4891 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含三层组合",
                ll.Contains("Ent$.bake.Service$work")
                && ll.Contains("$.wrapped.work") && ll.Contains("$.mwrapped.work"), ll);
        }

        // wildcard 环改写 .name 重路由（刀6b 正例，VM
        // RerouteWildcardInner 同口径）：proxy 体覆写 .name 形参后
        // inner(.name, args)——前端/验证器放行（保留首参操作数名恒等、
        // 值可改写），烘焙不再受控拒绝：wildcard 环 inner 改写为
        // hit/miss 动态分派块（hit = 本成员 canonical 恒等直进终态；
        // miss 调 $.mw.mwr router），router if 链覆盖宿主可烘焙方法
        //（other 未被烘焙 → 原名 fn；fetch 被烘焙 → $.mwrapped. 最深
        // 层原始体），链末 miss 抛 NoSuchMethodException
        private static void TestMethodProxyWildcardReroute()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        .name = \"Service$other(x:.i32)@.i32\"\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "    pub func other(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(1)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.reroute.bil");
            CaseAssertions.CheckTrue("改写 .name 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("改写 .name 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            var ring = functions.Single(f =>
                f.Symbol.Canonical.Contains("Timed$.bake.Service$fetch"));
            CaseAssertions.CheckTrue("wildcard 环 inner → hit/miss 动态分派块",
                ring.Blocks.Any(b => b.Terminator is MirCondBranch));
            CaseAssertions.CheckTrue("wildcard 环 miss 调 $.mw.mwr router",
                ring.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.StartsWith("Service$.mw.mwr(",
                        System.StringComparison.Ordinal)));

            var router = functions.Single(f =>
                f.Symbol.Canonical.StartsWith("Service$.mw.mwr(",
                    System.StringComparison.Ordinal));
            var routerCalls = router.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirCall>().ToList();
            CaseAssertions.CheckTrue("router 覆盖未烘焙方法原名 fn（other）",
                routerCalls.Any(c => c.Target.Canonical
                    == "Service$other(x:.i32)@.i32"));
            CaseAssertions.CheckTrue("router 覆盖被烘焙方法最深层原始体（fetch → $.mwrapped.）",
                routerCalls.Any(c => c.Target.Canonical
                    == "Service$.mwrapped.fetch(x:.i32)@core::i32"));
            CaseAssertions.CheckTrue("router 链末 miss 抛 NoSuchMethodException",
                router.Blocks.SelectMany(b => b.Instructions).OfType<MirThrow>().Any());

            using var llvmLease4962 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 mwr router",
                ll.Contains("Service$.mw.mwr("), ll);
        }

        // ===== super 绕过 wrapper 链（遗留12 任务②，对齐 VM
        // ResolveSuper 直接压帧） =====

        // MirSuperCall 目标改写：基类方法被 Method wrapper 烘焙 →
        // $.mwrapped.（Entity×Method 组合的最深层）；被 Entity wrapper
        // 烘焙 → $.wrapped.；无 wrapper 烘焙的基类方法保持原名槽不动
        private static void TestSuperCallBypassesWrapperBaking()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Met {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn { return inner() }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Ent {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(): i32 { return inner() }\n" +
                "}\n" +
                "pub open class BaseM {\n" +
                "    pub init()\n" +
                "    @Met\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class ChildM : BaseM {\n" +
                "    pub init()\n" +
                "    pub override func work(): i32 { return (super() + 10) }\n" +
                "}\n" +
                "@Ent\n" +
                "pub open class BaseE {\n" +
                "    pub init()\n" +
                "    pub open func ping(): i32 { return 2 }\n" +
                "}\n" +
                "@Ent\n" +
                "pub class ChildE : BaseE {\n" +
                "    pub init()\n" +
                "    pub override func ping(): i32 { return (super() + 20) }\n" +
                "}\n" +
                "pub open class BaseN {\n" +
                "    pub init()\n" +
                "    pub open func plain(): i32 { return 3 }\n" +
                "}\n" +
                "pub class ChildN : BaseN {\n" +
                "    pub init()\n" +
                "    pub override func plain(): i32 { return (super() + 30) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return ((new ChildM().work() + new ChildE().ping())\n" +
                "        + new ChildN().plain())\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "super.bypass.bil");
            CaseAssertions.CheckTrue("super 绕链用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            // 子类 override 体自身亦可能被烘焙外移（ChildE 带 @Ent），
            // MirSuperCall 全模块扫描按目标归组
            var superTargets = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).OfType<MirSuperCall>()
                .Select(s => s.Target.Canonical).ToList();

            // Method wrapper：super 目标 → $.mwrapped. 最深层原始体
            CaseAssertions.CheckTrue("super 目标改写为 $.mwrapped.（Method wrapper）",
                superTargets.Contains("BaseM$.mwrapped.work()@core::i32"));
            CaseAssertions.CheckTrue("$.mwrapped. 原始体 fn 存在",
                functions.Any(f => f.Symbol.Canonical == "BaseM$.mwrapped.work()@core::i32"));

            // Entity wrapper：super 目标 → $.wrapped. 原始体
            CaseAssertions.CheckTrue("super 目标改写为 $.wrapped.（Entity wrapper）",
                superTargets.Contains("BaseE$.wrapped.ping()@core::i32"));
            CaseAssertions.CheckTrue("$.wrapped. 原始体 fn 存在",
                functions.Any(f => f.Symbol.Canonical == "BaseE$.wrapped.ping()@core::i32"));

            // 无 wrapper：super 目标保持原名槽不动
            CaseAssertions.CheckTrue("无 wrapper 的 super 目标不动",
                superTargets.Contains("BaseN$plain()@.i32"));

            // 全模块不再残留指向被烘焙原名槽的 MirSuperCall
            CaseAssertions.CheckTrue("无残留指向 trampoline 的 MirSuperCall",
                !superTargets.Any(t => t == "BaseM$work()@.i32"
                    || t == "BaseE$ping()@.i32"));

            using var llvmLease5052 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 super 直调最深层原始体",
                ll.Contains("$.mwrapped.work") && ll.Contains("$.wrapped.ping"), ll);
        }

    }
}
