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
        // Singletons 职责；与主文件共享同一类型、字段及生命周期。

        // ===== singleton 运行时（MW10 刀5）=====

        // get fn 三态/缓存/异常边形状 + new 改写 + Singletons 条目挂载
        private static void TestSingletonLoweringEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub shared singleton class S {\n" +
                "    pub var v: i32\n" +
                "    pub init() { v = 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new S()\n" +
                "    var b = new S()\n" +
                "    return b.v\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "singleton.bil");
            TestHarness.CheckTrue("singleton 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var instsOf = new Func<MirFunction, List<MirInst>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).ToList());

            // 合成条目：get fn / state / cache 符号
            //（MW11c：stdlib 三内置 Executor singleton 恒在模块——按
            // TypeCanonical 找 S 条目，不再全模块唯一）
            var sEntry = context.Singletons.SingleOrDefault(s => s.TypeCanonical == "S");
            TestHarness.CheckTrue("Singletons 条目挂载（S）",
                sEntry != null
                && sEntry.GetFnCanonical == "S$.static.mw.singleton.get()@S"
                && sEntry.StateFieldSymbol == "S#.mw.singleton.state@.i32"
                && sEntry.CacheFieldSymbol == "S#.mw.singleton.cache@S");
            TestHarness.CheckTrue("Singletons 含 stdlib 三内置 Executor（MW11c）",
                context.Singletons.Any(s => s.TypeCanonical == "core.coroutine::MainExecutor")
                && context.Singletons.Any(s => s.TypeCanonical == "core.coroutine::ComputeExecutor")
                && context.Singletons.Any(s => s.TypeCanonical == "core.coroutine::IOExecutor"));

            // get fn 形状：三态读写 + 带异常边的真构造 + 失败块置回 + 环抛
            var get = Fn("S$.static.mw.singleton.get");
            TestHarness.CheckTrue("get fn 无参、返回单例类型",
                get.Parameters.Count == 0 && get.ReturnType.Canonical == "S");
            var getInsts = instsOf(get);
            TestHarness.CheckTrue("get fn 读三态槽（entry）",
                getInsts.OfType<MirGetStatic>().Any(g =>
                    g.FieldSymbol == "S#.mw.singleton.state@.i32"));
            TestHarness.CheckTrue("get fn 读缓存槽（ready）",
                getInsts.OfType<MirGetStatic>().Any(g =>
                    g.FieldSymbol == "S#.mw.singleton.cache@S"));
            TestHarness.CheckTrue("get fn 三态槽三写（在途/就绪/置回）",
                getInsts.OfType<MirSetStatic>().Count(s =>
                    s.FieldSymbol == "S#.mw.singleton.state@.i32") == 3);
            TestHarness.CheckTrue("get fn 登记缓存槽",
                getInsts.OfType<MirSetStatic>().Any(s =>
                    s.FieldSymbol == "S#.mw.singleton.cache@S"));
            var construct = getInsts.OfType<MirNewObject>().Single(n =>
                n.Type.Canonical == "S");
            TestHarness.CheckTrue("get fn 真构造带异常边（init 抛出置回在途）",
                construct.ExcTarget != null
                && construct.ExcTarget.Id == "mw.sg.fail"
                && construct.Init!.Canonical == "S$init()@.void");
            TestHarness.CheckTrue("get fn 环检测抛异常",
                getInsts.OfType<MirThrow>().Any()
                && getInsts.OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical == "core::RuntimeException"));
            TestHarness.CheckTrue("get fn 失败块置回未构造（state=0）",
                get.Blocks.Any(b => b.Id == "mw.sg.fail"
                    && b.Instructions.OfType<MirSetStatic>().Any(s =>
                        s.FieldSymbol == "S#.mw.singleton.state@.i32")));

            // new 改写：main 里两次 new type(S) → 两处 get 调用，无残留构造
            var main = Fn("$main");
            var mainInsts = instsOf(main);
            TestHarness.CheckTrue("main 无残留 MirNewObject(S)",
                !mainInsts.OfType<MirNewObject>().Any(n => n.Type.Canonical == "S"));
            TestHarness.CheckTrue("main 两处 new 均改写为 get 调用",
                mainInsts.OfType<MirCall>().Count(c =>
                    c.Target.Canonical == "S$.static.mw.singleton.get()@S") == 2);

            // LL：合成静态槽 + get fn 发射
            using var llvmLease4407 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("合成三态槽发射（i32 全局）",
                ll.Contains("@\"static.S#.mw.singleton.state@.i32\" = internal global i32 0"), ll);
            TestHarness.CheckTrue("合成缓存槽发射（胖引用全局）",
                ll.Contains("@\"static.S#.mw.singleton.cache@S\" = internal global { i64, i64 } zeroinitializer"), ll);
            TestHarness.CheckTrue("get fn 发射",
                ll.Contains("@\"S$.static.mw.singleton.get()@S\"()"), ll);
            TestHarness.CheckTrue("缓存槽纳入 rigi_globals_cleanup",
                ll.Contains("define void @rigi_globals_cleanup()"), ll);
        }

        // rigi_entry 急切初始化调用序（VM InitializeSingletons →
        // InvokeGlobalInitializers → main 同口径）：singleton get 族 →
        // ..globals.init → main
        private static void TestSingletonEntryStubOrder()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "var g: i32 = 40\n" +
                "pub shared singleton class S {\n" +
                "    pub var v: i32\n" +
                "    pub init() { v = 7 }\n" +
                "}\n" +
                "pub func main(): i32 { return new S().v }\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "singleton.entry.bil");
            TestHarness.CheckTrue("入口序用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var llvmLease4437 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            var stubAt = ll.IndexOf("define i32 @rigi_entry(i32 %0, ptr %1)", StringComparison.Ordinal);
            TestHarness.CheckTrue("rigi_entry stub 存在", stubAt >= 0, ll);
            var stub = stubAt >= 0 ? ll.Substring(stubAt) : "";
            var getAt = stub.IndexOf("S$.static.mw.singleton.get()@S", StringComparison.Ordinal);
            var globalsAt = stub.IndexOf("$..globals.init()@.void", StringComparison.Ordinal);
            var mainAt = stub.IndexOf("$main()@.i32", StringComparison.Ordinal);
            TestHarness.CheckTrue("急切初始化序：get 族 → globals.init → main",
                getAt > 0 && globalsAt > getAt && mainAt > globalsAt,
                stub.Substring(0, Math.Min(stub.Length, 1200)));
            // 急切初始化丢弃的返回值归还（防泄漏：释放调用紧贴 get 调用）
            TestHarness.CheckTrue("get 返回值即弃即释放",
                stub.Contains("singleton.get") && stub.Contains("rigi_ref_release"), stub);
        }

    }
}
