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
        // CoroutineLowering 职责；与主文件共享同一类型、字段及生命周期。

        // ===== MW11a 棒2 阶段1：协程 lowering 形状 =====

        private static void TestCoroutineLowering()
        {
            // ① await 直译 MirAwait（task/result 槽 + ExcTarget==null 兜底）
            var mir = BuildMirModule(
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = add(41)\n" +
                "    var n = await t\n" +
                "    return n\n" +
                "}\n", "coro.await.bil");
            var main = mir.Functions.Single(f => f.IsEntrypoint);
            var awaitInst = main.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirAwait>().Single();
            TestHarness.CheckTrue("① await→MirAwait：task 槽携带",
                awaitInst.TaskSlot == "t", awaitInst.TaskSlot);
            TestHarness.CheckTrue("① await→MirAwait：result 槽携带（前端发临时槽）",
                awaitInst.ResultSlot != null
                && main.TryFindLocal(awaitInst.ResultSlot, out var awaitResultLocal)
                && awaitResultLocal.Type.Key == "i32",
                awaitInst.ResultSlot ?? "<null>");
            TestHarness.CheckTrue("① await→MirAwait：try 外 ExcTarget==null",
                awaitInst.ExcTarget == null);
            TestHarness.CheckTrue("① IsAsync 传播：add 为 async",
                mir.Functions.Single(f => f.Symbol.Canonical.Contains("$add(")).IsAsync);
            TestHarness.CheckTrue("① IsAsync 传播：main 非 async", !main.IsAsync);

            // ② await 无结果形态（await Task）：ResultSlot==null
            mir = BuildMirModule(
                "async func ping() { }\n" +
                "pub func main(): i32 {\n" +
                "    await ping()\n" +
                "    return 1\n" +
                "}\n", "coro.awaitvoid.bil");
            main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("② await Task 无结果槽",
                main.Blocks.SelectMany(b => b.Instructions).OfType<MirAwait>()
                    .Single().ResultSlot == null);

            // ③ 裸 yield → MirYieldBare
            mir = BuildMirModule(
                "async func ping() { yield }\n" +
                "pub func main(): i32 {\n" +
                "    await ping()\n" +
                "    return 1\n" +
                "}\n", "coro.yield.bil");
            var ping = mir.Functions.Single(f => f.Symbol.Canonical.Contains("$ping("));
            TestHarness.CheckTrue("③ 裸 yield→MirYieldBare",
                ping.Blocks.SelectMany(b => b.Instructions).OfType<MirYieldBare>().Any());

            // ④ 带 Alarm 的 yield → MirYieldAlarm（MW11b 棒3 解锁，
            // split 改写与 probe 合成归 TestCoroutineYieldAlarm）
            mir = BuildMirModule(
                "import core.coroutine.*\n" +
                "async func nap() { yield sleep(1) }\n" +
                "pub func main(): i32 {\n" +
                "    await nap()\n" +
                "    return 1\n" +
                "}\n", "coro.yieldalarm.bil");
            var nap = mir.Functions.Single(f => f.Symbol.Canonical.Contains("$nap("));
            TestHarness.CheckTrue("④ yield Alarm→MirYieldAlarm 携 alarm 槽",
                nap.Blocks.SelectMany(b => b.Instructions).OfType<MirYieldAlarm>()
                    .SingleOrDefault() is { } yieldAlarm
                && yieldAlarm.AlarmSlot.Length > 0);

            // ⑤ try 内 await：ExcTarget 指向本词法 try 派发垫
            mir = BuildMirModule(
                "async func boom(): i32 { throw new core.RuntimeException(\"x\") }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 3\n" +
                "    }\n" +
                "}\n", "coro.awaittry.bil");
            main = mir.Functions.Single(f => f.IsEntrypoint);
            awaitInst = main.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirAwait>().Single();
            TestHarness.CheckTrue("⑤ try 内 await：ExcTarget 为派发垫",
                awaitInst.ExcTarget != null
                && awaitInst.ExcTarget.Id.Contains(".dispatch")
                && awaitInst.ExcTarget.Instructions.Count > 0
                && awaitInst.ExcTarget.Instructions[0] is MirTakePending,
                awaitInst.ExcTarget?.Id ?? "<null>");
        }

        // ===== MW11a 棒2 阶段2：合成 frame 类型的 Layout 通道 =====

        private static void TestSyntheticFrameTypePlan()
        {
            var context = PipelineFromSource(
                "pub func main(): i32 { return 0 }\n", "frame.plan.bil");
            var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf("$main()@.i32");
            var slots = new (string SlotName, MirType Type)[]
            {
                ("obj", MirType.Of("core::Object")),
                ("text", MirType.Of(".string")),
                ("tid", MirType.Of(".typeid")),
                ("num", MirType.Of(".i32")),
            };
            var typeSymbol = SyntheticTypePlanner.EnsureFrameType(context, frameCanonical, slots);
            TestHarness.CheckTrue("frame 类型注册进 Symbols 驻留",
                ReferenceEquals(typeSymbol, context.Symbols.FindType(frameCanonical)));
            TestHarness.CheckTrue("frame 类型注册幂等（同 canonical 同对象）",
                ReferenceEquals(typeSymbol,
                    SyntheticTypePlanner.EnsureFrameType(context, frameCanonical, slots)));
            TestHarness.CheckTrue("frame 空 init 成员可查",
                context.Symbols.FindMember(
                    SyntheticTypePlanner.FrameInitCanonicalOf(frameCanonical)) != null);

            var plan = context.Layout!.Find(frameCanonical);
            TestHarness.CheckTrue("frame 布局计划注册可查（TypeSheet 发射零特例）",
                plan != null && plan.Kind == TypeLayoutKind.Class);
            // 偏移：state@16(i32) → obj@32(16B 对齐) → text@48 → tid@64(8B)
            // → num@72；size 补齐 80
            TestHarness.CheckTrue("frame 字段数 = state + 保存槽",
                plan!.Fields.Count == 5, plan.Fields.Count.ToString());
            var state = plan.Fields[0];
            TestHarness.CheckTrue("state 字段：偏移 16 / i32",
                state.Symbol == SyntheticTypePlanner.FrameFieldSymbol(
                    frameCanonical, SyntheticTypePlanner.StateFieldName, "core::i32")
                && state.Offset == 16 && state.Size == 4 && !state.IsReferenceSlot,
                state.Symbol + "@" + state.Offset);
            var obj = plan.Fields[1];
            TestHarness.CheckTrue("托管槽：16B 胖引用 16 对齐 @32",
                obj.Offset == 32 && obj.Size == 16 && obj.IsReferenceSlot,
                obj.Offset.ToString());
            var text = plan.Fields[2];
            TestHarness.CheckTrue("String 槽：@48 kind1",
                text.Offset == 48 && text.IsStringSlot, text.Offset.ToString());
            var tid = plan.Fields[3];
            TestHarness.CheckTrue("typeid 槽：8B 内联 @64 不进 refMap",
                tid.Offset == 64 && tid.Size == 8 && !tid.IsReferenceSlot
                && !tid.IsStringSlot, tid.Offset.ToString());
            TestHarness.CheckTrue("frame size 16 对齐补齐 80", plan.Size == 80,
                plan.Size.ToString());
            // refMap：obj（kind0 hop1：16→32）+ text（kind1 hop0：48→48）；
            // tid/num 非托管
            TestHarness.CheckTrue("refMap 恰两条（托管槽入射）",
                plan.RefMapCount == 2, plan.RefMapCount.ToString());
            TestHarness.CheckTrue("refMap[0] = kind0 hop1（obj）",
                plan.RefMap.Length == 2
                && TypeLayout.RefMapKindOf(plan.RefMap[0]) == TypeLayout.RefMapKindFatRef
                && TypeLayout.RefMapHopOf(plan.RefMap[0]) == 1);
            TestHarness.CheckTrue("refMap[1] = kind1 hop0（text）",
                plan.RefMap.Length == 2
                && TypeLayout.RefMapKindOf(plan.RefMap[1]) == TypeLayout.RefMapKindString
                && TypeLayout.RefMapHopOf(plan.RefMap[1]) == 0);
            TestHarness.CheckTrue("frame 无 vtable 槽（仅槽 0 init 分发器占位）",
                plan.VTableSlots.Count == 1
                && plan.VTableSlots[0] == LayoutEngine.InitDispatchSlot);
            TestHarness.CheckTrue("frame 无 iMap/基类",
                plan.IMap.Count == 0 && plan.BasePlan == null);
        }

        // ===== MW11a 棒2 阶段3：CoroutineSplit + RcInjection 扩展形状 =====

        // 按 stub 精确前缀找 fn（resume 合成名含 stub canonical，contains
        // 会误命中）
        private static MirFunction StubOf(MwContext context, string prefix) =>
            context.Mir!.Functions.Single(f => f.Symbol.Canonical.StartsWith(prefix,
                StringComparison.Ordinal));

        private static MirFunction ResumeOf(MwContext context, MirFunction stub) =>
            context.Mir!.Functions.Single(f => f.Symbol.Canonical
                == "$mw.resume." + stub.Symbol.Canonical);

        private static MirBlock BlockOf(MirFunction fn, string id) =>
            fn.Blocks.Single(b => b.Id == id);

    }
}
