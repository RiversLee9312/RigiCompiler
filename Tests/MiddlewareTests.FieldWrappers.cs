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
        // FieldWrappers 职责；与主文件共享同一类型、字段及生命周期。

        // ===== wrapper 隐藏槽内存路径（MW10）：get/set/new.wrapper.* =====

        private static void TestWrapperStorageEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: i32\n" +
                "    pub init() { level = 7 }\n" +
                "    pub func dump(): i32 { return level }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 3 }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func readEntity(s: Service): i32 {\n" +
                "    return s:Logged.level\n" +
                "}\n" +
                "pub func writeEntity(s: Service) {\n" +
                "    s:Logged.level = 42\n" +
                "}\n" +
                "pub func callEntity(s: Service): i32 {\n" +
                "    return s:Logged.dump()\n" +
                "}\n" +
                "pub func readField(hero: Hero): i32 {\n" +
                "    return hero.hp:Clamped.min\n" +
                "}\n" +
                "pub func writeField(hero: Hero) {\n" +
                "    hero.hp:Clamped.min = 9\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    writeEntity(s)\n" +
                "    var h = new Hero()\n" +
                "    writeField(h)\n" +
                "    var a = readEntity(s)\n" +
                "    var b = readField(h)\n" +
                "    var c = callEntity(s)\n" +
                "    return a\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.store.bil");
            CaseAssertions.CheckTrue("wrapper 存储用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var allInsts = context.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("MIR 含 get.wrapper", allInsts.OfType<MirGetWrapper>().Any());
            CaseAssertions.CheckTrue("BIL 含 get.wrapper.field",
                text.Contains("get.wrapper.field"), text);
            CaseAssertions.CheckTrue("MIR 含 get.wrapper.field",
                allInsts.OfType<MirGetWrapperField>().Any());
            CaseAssertions.CheckTrue("MIR 含 set.wrapper.field",
                allInsts.OfType<MirSetWrapperField>().Any());
            CaseAssertions.CheckTrue("MIR 含 new.wrapper.entity",
                allInsts.OfType<MirNewWrapper>().Any(n => n.Kind == MirWrapperInstallKind.Entity));
            CaseAssertions.CheckTrue("MIR 含 new.wrapper.field",
                allInsts.OfType<MirNewWrapper>().Any(n => n.Kind == MirWrapperInstallKind.Field));
            var index = WrapperApplicationIndex.Build(context.Symbols);
            CaseAssertions.CheckTrue("应用索引 Entity(Service)=Logged",
                index.EntityWrappers("Service").Contains("Logged"));
            CaseAssertions.CheckTrue("应用索引字段-Value(Hero#hp)=Clamped",
                index.FieldWrappers("Hero#hp@.i32").Contains("Clamped"));
            CaseAssertions.CheckTrue("Logged init 可达",
                context.Mir.Functions.Any(f => f.Symbol.Canonical.Contains("Logged$init")));

            using var llvmLease2861 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 模块含 Logged init",
                ll.Contains("Logged$init") || ll.Contains("Logged$init("), ll);
        }

        // ===== specific Entity proxy 烘焙（MW10）：get.self + inner 链接 =====

        private static void TestProxyBakingEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): i32 {\n" +
                "        var host = self\n" +
                "        return (inner(arg) + 1)\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): i32 { return arg }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.doSomething(5)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.proxy.bil");
            CaseAssertions.CheckTrue("proxy 烘焙用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());
            CaseAssertions.CheckTrue("特化体通过独立参数读取 self，无回指指令",
                !allInsts.OfType<MirGetSelf>().Any()
                && functions.Any(f => f.Parameters.Any(p => p.Name == WrapperSelfParameterPass.SelfParameter)));
            CaseAssertions.CheckTrue("合成 .wrapped. 原始体",
                functions.Any(f => f.Symbol.Canonical.Contains(
                    ProxyBakeSupport.WrappedInfix)));
            CaseAssertions.CheckTrue("合成 .bake. 特化体",
                functions.Any(f => f.Symbol.Canonical.Contains(ProxyBakeSupport.BakeInfix)));
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical.Contains("Service$doSomething")
                && !f.Symbol.Canonical.Contains(ProxyBakeSupport.WrappedInfix)
                && !f.Symbol.Canonical.Contains(ProxyBakeSupport.BakeInfix));
            CaseAssertions.CheckTrue("原名槽是 trampoline（get.wrapper.addr + call）",
                trampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperAddr>()
                    .Any()
                && trampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().Any());

            using var llvmLease2917 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含原名 doSomething",
                ll.Contains("Service$doSomething"), ll);
            CaseAssertions.CheckTrue("LLVM 含 .wrapped. 原始体",
                ll.Contains(".wrapped."), ll);
            CaseAssertions.CheckTrue("LLVM 含 .bake. 特化体",
                ll.Contains(".bake."), ll);
        }

        // ===== proxy 环 receiver 取址形态（MW10 刀3c §14.5 原地访问） =====

        // 环 receiver 一律 get.wrapper[.field].addr（宿主隐藏槽就地地址，
        // 非值拷贝）；别名目标局部不进 acquire/release 序列（RichValue
        // wrapper 带 String 字段强制 managed 分类以覆盖该判别）；place
        // 路径的 get.wrapper/get.wrapper.field 值拷贝语义不回归
        private static void TestProxyRingReceiverAddrEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub var tag: String\n" +
                "    pub init() {\n" +
                "        calls = 0\n" +
                "        tag = \"t\"\n" +
                "    }\n" +
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
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Tag {\n" +
                "    pub var label: String\n" +
                "    pub init() { label = \"L\" }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Tag\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(1)\n" +
                "    var b = s.fetch(1)\n" +
                "    var h = new Hero()\n" +
                "    h.name = \"b\"\n" +
                "    var n = h.name\n" +
                "    var e = s:Counting.calls\n" +
                "    var z = h.name:Tag.label\n" +
                "    return (((a + b) + e))\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.addr.bil");
            CaseAssertions.CheckTrue("环 receiver 取址用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();

            // trampoline 首环 receiver：get.wrapper.addr（无拷贝形态残留）
            var trampoline = functions.Single(f =>
                f.Symbol.Canonical.Contains("Service$fetch")
                && !f.Symbol.Canonical.Contains(ProxyBakeSupport.WrappedInfix)
                && !f.Symbol.Canonical.Contains(ProxyBakeSupport.BakeInfix));
            var trampAddr = trampoline.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirGetWrapperAddr>().ToList();
            CaseAssertions.CheckTrue("trampoline receiver 为 get.wrapper.addr",
                trampAddr.Any(g => g.WrapperType == "Counting")
                && !trampoline.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapper>().Any());
            // RichValue wrapper（含 String 字段）：别名目标不进 acquire/release
            var trampRc = trampoline.Blocks.SelectMany(b => b.Instructions).ToList();
            foreach (var addr in trampAddr)
            {
                CaseAssertions.CheckTrue("trampoline 别名目标无 release/acquire（" + addr.Target + "）",
                    !trampRc.OfType<MirReleaseSlot>().Any(r => r.Local == addr.Target)
                    && !trampRc.OfType<MirAcquireSlot>().Any(r => r.Local == addr.Target));
            }

            // Value 链使用点 receiver：get.wrapper.field.addr；别名目标同样免 release
            var main = functions.Single(f => f.Symbol.Canonical.Contains("$main"));
            var mainInsts = main.Blocks.SelectMany(b => b.Instructions).ToList();
            var fieldAddr = mainInsts.OfType<MirGetWrapperFieldAddr>()
                .Where(g => g.WrapperType == "Tag").ToList();
            CaseAssertions.CheckTrue("Value 链使用点 receiver 为 get.wrapper.field.addr（get+set 各一）",
                fieldAddr.Count == 2);
            foreach (var addr in fieldAddr)
            {
                CaseAssertions.CheckTrue("使用点别名目标无 release（" + addr.Target + "）",
                    !mainInsts.OfType<MirReleaseSlot>().Any(r => r.Local == addr.Target));
            }

            // place 路径值拷贝语义不回归：s:Counting.calls → get.wrapper、
            // h.name:Tag.label → get.wrapper.field（拷贝形态仍在）
            CaseAssertions.CheckTrue("place Entity wrapper 读保持 get.wrapper 拷贝",
                allInsts.OfType<MirGetWrapper>().Any(g => g.WrapperType == "Counting"));
            CaseAssertions.CheckTrue("place 字段-Value wrapper 读保持 get.wrapper.field 拷贝",
                allInsts.OfType<MirGetWrapperField>().Any(g => g.WrapperType == "Tag"));

            // .ll 形状：trampoline 体内无 wrapper 值拷贝 acquire（取址不产生
            // 值拷贝，旧形态此处必有 rigi_value_acquire）
            using var llvmLease3029 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            var marker = ll.IndexOf("Service$fetch", System.StringComparison.Ordinal);
            CaseAssertions.CheckTrue("LLVM 含原名 fetch", marker >= 0, ll);
            var tail = ll.IndexOf("\ndefine ", marker, System.StringComparison.Ordinal);
            var slice = tail < 0 ? ll.Substring(marker) : ll.Substring(marker, tail - marker);
            CaseAssertions.CheckTrue("trampoline 体无 rigi_value_acquire（无值拷贝）",
                !slice.Contains("rigi_value_acquire"), slice);
        }

        // ===== 字段-Value wrapper get/set 链烘焙（MW10 刀2） =====

        // 单字段双 wrapper（@A 外 @B 内）：set 链环间 MirCall 链接、get 链
        // 内→外调用序、init 写豁免、cell getValue/setValue 壳化成链
        private static void TestValueProxyBakingEmission()
        {
            const string wrappers =
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n";
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                wrappers +
                "pub class Hero {\n" +
                "    @A\n" +
                "    @B\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 0\n" +
                "    x = 1\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 5\n" +
                "    var a = x\n" +
                "    var b = h.hp\n" +
                "    return ((a + b))\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.value.bil");
            CaseAssertions.CheckTrue("Value 链用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("Value 链烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // set 链：环符号存在 + 环间 MirCall 链接（A.set→B.set→终态）
            var aSet = Fn("A$.bake.Hero$hp$.set");
            var bSet = Fn("B$.bake.Hero$hp$.set");
            var terminalSet = Fn("Hero$hp$.wrapped.set");
            CaseAssertions.CheckTrue("A.set 环内经 get.wrapper.field.addr 取 B 槽地址并 MirCall B.set",
                aSet.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperFieldAddr>()
                    .Any(g => g.WrapperType == "B")
                && calls(aSet).Any(c => c.Target.Canonical == bSet.Symbol.Canonical));
            CaseAssertions.CheckTrue("B.set 环 MirCall 链末终态",
                calls(bSet).Any(c => c.Target.Canonical == terminalSet.Symbol.Canonical));
            CaseAssertions.CheckTrue("hp 无用户 setter：终态裸写 backing",
                terminalSet.Blocks.SelectMany(b => b.Instructions).OfType<MirSetField>()
                    .Any(s => s.FieldSymbol == "Hero#hp@.i32"));

            // get 链：使用点（main 读 h.hp）环序 = 终态 → B（内）→ A（外）
            var main = Fn("$main");
            var mainInsts = main.Blocks.SelectMany(b => b.Instructions).ToList();
            var getSeq = mainInsts.Where(inst =>
                inst is MirCall c && (c.Target.Canonical == "Hero$hp$.wrapped.get()@core::i32"
                    || c.Target.Canonical.Contains("$.bake.Hero$hp$.get"))).ToList();
            CaseAssertions.CheckTrue("get 链使用点三步：终态 → B.get → A.get",
                getSeq.Count == 3
                && getSeq[0] is MirCall c0
                    && c0.Target.Canonical == "Hero$hp$.wrapped.get()@core::i32"
                && getSeq[1] is MirCall c1
                    && c1.Target.Canonical.StartsWith("B$.bake.Hero$hp$.get")
                && getSeq[2] is MirCall c2
                    && c2.Target.Canonical.StartsWith("A$.bake.Hero$hp$.get"),
                string.Join(" | ", getSeq));

            // get 环形参表：隐藏 typeid 形参已剔除（.this + value 二参）
            CaseAssertions.CheckTrue("get 环形参剔除 .generic.TValue",
                Fn("A$.bake.Hero$hp$.get").Parameters.Count == 3
                && Fn("A$.bake.Hero$hp$.get").Parameters.Last().Name == WrapperSelfParameterPass.SelfParameter);

            // init 写豁免：..init.field.hp 内 MirSetField 未被改写为链
            var initField = Fn("Hero$..init.field.hp");
            CaseAssertions.CheckTrue("init 族写豁免（MirSetField 保持裸写）",
                initField.Blocks.SelectMany(b => b.Instructions).OfType<MirSetField>()
                    .Any(s => s.FieldSymbol == "Hero#hp@.i32")
                && !calls(initField).Any(c =>
                    c.Target.Canonical.Contains(ProxyBakeSupport.BakeInfix)));

            // cell：wrapped 局部的 getValue/setValue 壳化成链，使用点
            // invoke core::Cell$getValue/setValue 不动
            var cellGet = functions.Single(f => f.Symbol.Canonical.StartsWith("..cell..")
                && f.Symbol.Canonical.Contains("$getValue"));
            var cellSet = functions.Single(f => f.Symbol.Canonical.StartsWith("..cell..")
                && f.Symbol.Canonical.Contains("$setValue"));
            CaseAssertions.CheckTrue("cell getValue 壳：终态调用 + 环调用",
                calls(cellGet).Any(c => c.Target.Canonical.Contains("$value$.wrapped.get"))
                && calls(cellGet).Count(c =>
                    c.Target.Canonical.Contains("$.bake.")) == 2);
            CaseAssertions.CheckTrue("cell setValue 壳：get.wrapper.field.addr + 最外环（A）调用",
                cellSet.Blocks.SelectMany(b => b.Instructions).OfType<MirGetWrapperFieldAddr>()
                    .Any(g => g.WrapperType == "A")
                && calls(cellSet).Any(c =>
                    c.Target.Canonical.StartsWith("A$.bake.")));
            CaseAssertions.CheckTrue("cell 使用点 invoke 不动",
                calls(main).Any(c => c.Target.Canonical.StartsWith("core::Cell$getValue"))
                && calls(main).Any(c => c.Target.Canonical.StartsWith("core::Cell$setValue")));
            // cell 终态 = 原访问器体（裸读写 backing）
            var cellTerminalSet = functions.Single(f =>
                f.Symbol.Canonical.Contains("$value$.wrapped.set"));
            CaseAssertions.CheckTrue("cell set 终态裸写 backing",
                cellTerminalSet.Blocks.SelectMany(b => b.Instructions).OfType<MirSetField>()
                    .Any(s => s.FieldSymbol.Contains("#value@.i32")));

            using var llvmLease3162 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 Value 链烘焙环",
                ll.Contains(".bake.Hero$hp"), ll);
        }

        // ===== Entity 字段 get/set proxy 链烘焙（MW10 刀3b） =====

        // specific get/set 链形状（环符号、环序、终态、零 MirInnerCall、
        // Entity 隐藏槽 get.wrapper）+ wildcard get.* 的 symbol 资源 =
        // 字段 canonical + W 短路 E + init 读命中/写豁免 + 无 proxy 层
        // 透明跳过与访问器兜底
        private static void TestEntityFieldProxyBakingEmission()
        {
            const string valueWrapper =
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n";
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                valueWrapper +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.name\\<TField>(value: TField) {\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        return value\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Empty {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() {\n" +
                "        name = \"a\"\n" +
                "        var c = name\n" +
                "    }\n" +
                "}\n" +
                "@Audit\n" +
                "pub class S2 {\n" +
                "    pub var title: String\n" +
                "    pub init() { title = \"t\" }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Dual {\n" +
                "    @A\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init()\n" +
                "}\n" +
                "@Empty\n" +
                "pub class Plain {\n" +
                "    pub var raw: i32 = 0\n" +
                "    pub var computed: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { computed = value }\n" +
                "    } = 0\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    var n = s.name\n" +
                "    var s2 = new S2()\n" +
                "    var t = s2.title\n" +
                "    var d = new Dual()\n" +
                "    d.hp = 5\n" +
                "    var h = d.hp\n" +
                "    var p = new Plain()\n" +
                "    p.computed = 3\n" +
                "    var c2 = p.computed\n" +
                "    p.raw = 4\n" +
                "    var r2 = p.raw\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.entityfield.bil");
            CaseAssertions.CheckTrue("Entity 字段链用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("Entity 字段链烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>().ToList());

            // specific set 链：环符号 + 终态 + 环内经 get.self 宿主裸写
            var cSet = Fn("Counting$.bake.Service$name$.set");
            var terminalSet = Fn("Service$name$.wrapped.set");
            CaseAssertions.CheckTrue("Counting.set 环 MirCall 链末终态",
                calls(cSet).Any(c => c.Target.Canonical == terminalSet.Symbol.Canonical));
            CaseAssertions.CheckTrue("name 无用户 setter：终态裸写 backing",
                terminalSet.Blocks.SelectMany(b => b.Instructions).OfType<MirSetField>()
                    .Any(s => s.FieldSymbol == "Service#name@.string"));

            // specific get 使用点（main 读 s.name）：终态 → get.wrapper
            //（Entity 隐藏槽，非 get.wrapper.field）→ Counting.get 环
            var main = Fn("$main");
            var mainInsts = main.Blocks.SelectMany(b => b.Instructions).ToList();
            var getSeq = mainInsts.Where(inst =>
                inst is MirCall c && (c.Target.Canonical == "Service$name$.wrapped.get()@core::String"
                    || c.Target.Canonical.StartsWith("Counting$.bake.Service$name$.get"))).ToList();
            CaseAssertions.CheckTrue("Entity get 使用点两步：终态 → Counting.get 环",
                getSeq.Count == 2
                && getSeq[0] is MirCall c0
                    && c0.Target.Canonical == "Service$name$.wrapped.get()@core::String"
                && getSeq[1] is MirCall c1
                    && c1.Target.Canonical.StartsWith("Counting$.bake.Service$name$.get"),
                string.Join(" | ", getSeq));
            CaseAssertions.CheckTrue("Entity 环 receiver 经 get.wrapper.addr（非字段槽）",
                mainInsts.OfType<MirGetWrapperAddr>().Any(g => g.WrapperType == "Counting")
                && !mainInsts.OfType<MirGetWrapperFieldAddr>()
                    .Any(g => g.WrapperType == "Counting"));

            // specific set 使用点（main 写 s.name）：get.wrapper.addr + 最外环调用
            CaseAssertions.CheckTrue("Entity set 使用点：get.wrapper.addr + Counting.set 环调用",
                mainInsts.OfType<MirGetWrapperAddr>().Any(g => g.WrapperType == "Counting")
                && calls(main).Any(c =>
                    c.Target.Canonical.StartsWith("Counting$.bake.Service$name$.set")));

            // wildcard get 环：symbol 形参剔除（.this + value 二参）且
            // symbol 资源 = 字段 canonical 全串
            var aGet = Fn("Audit$.bake.S2$title$.get");
            CaseAssertions.CheckTrue("wildcard get 环形参剔除 symbol，追加宿主参数",
                aGet.Parameters.Count == 3 && aGet.Parameters.Last().Name == WrapperSelfParameterPass.SelfParameter);
            CaseAssertions.CheckTrue("wildcard get 环 symbol 资源 = 字段 canonical",
                aGet.Blocks.SelectMany(b => b.Instructions).OfType<MirLoadResource>().Any()
                && context.Module.Resources.OfType<BilScalarResource>()
                    .Any(r => r.LiteralText.Contains("S2#title@.string")));

            // W 短路 E：Dual.hp 自身 wrapped → 只跑 Value 链，无 Entity 环
            CaseAssertions.CheckTrue("W 短路 E：hp 走 Value 链且无 Entity 环",
                functions.Any(f => f.Symbol.Canonical.Contains("A$.bake.Dual$hp"))
                && !functions.Any(f =>
                    f.Symbol.Canonical.Contains("Counting$.bake.Dual$hp")));

            // init 读命中/写豁免：Service$init 内 name 裸写（无 set 环调用）
            // 且读走 get 链（终态调用存在）
            var init = functions.Single(f => f.Symbol.Canonical == "Service$init()@.void");
            var initInsts = init.Blocks.SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("init 写豁免（MirSetField 保持裸写）",
                initInsts.OfType<MirSetField>().Any(s => s.FieldSymbol == "Service#name@.string")
                && !calls(init).Any(c =>
                    c.Target.Canonical.Contains("$.bake.Service$name$.set")));
            CaseAssertions.CheckTrue("init 读命中 get 链（终态 + 环调用）",
                calls(init).Any(c => c.Target.Canonical == "Service$name$.wrapped.get()@core::String")
                && calls(init).Any(c =>
                    c.Target.Canonical.StartsWith("Counting$.bake.Service$name$.get")));

            // 无 proxy 层：有用户访问器 → 访问器调用兜底；无 → 保持裸访
            CaseAssertions.CheckTrue("无 proxy 层读兜底为 getter 调用",
                calls(main).Any(c => c.Target.Canonical == "Plain$.get.computed@.i32"));
            CaseAssertions.CheckTrue("无 proxy 层写兜底为 setter 调用",
                calls(main).Any(c => c.Target.Canonical == "Plain$.set.computed@.i32"));
            CaseAssertions.CheckTrue("无 proxy 层无访问器字段保持裸访",
                mainInsts.OfType<MirSetField>().Any(s => s.FieldSymbol == "Plain#raw@.i32")
                && mainInsts.OfType<MirGetField>().Any(g => g.FieldSymbol == "Plain#raw@.i32"));

            using var llvmLease3339 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含 Entity 字段链烘焙环",
                ll.Contains(".bake.Service$name"), ll);
        }

    }
}
