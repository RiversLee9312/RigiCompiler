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
        // CoroutineAlarms 职责；与主文件共享同一类型、字段及生命周期。

        // ===== MW11b 棒3：yield Alarm 切分形态 + probe 合成 fn =====

        private static void TestCoroutineYieldAlarm()
        {
            // ① sleep 形态（棒5a）：挂起段 = 存 frame + state 常量化 +
            // PollingAlarm sheet 物化 + MirTypeCheck 分流（poll/event）；
            // event 分支 rigi_alarm_wait 闸内登记；恢复块落 pollgate
            //（pending 判位）而非直续原后继；lowering 层指令全消除
            var ctx = PipelineFromSource(
                "import core.coroutine.*\n" +
                "async func nap(): i32 {\n" +
                "    yield sleep(1)\n" +
                "    return 6\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = nap()\n" +
                "    return 0\n" +
                "}\n", "coro.alarm1.bil");
            var stub = StubOf(ctx, "$nap(");
            var resume = ResumeOf(ctx, stub);
            var allInsts = resume.Blocks.SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("① lowering 层指令全消除",
                !allInsts.OfType<MirYieldAlarm>().Any()
                && !allInsts.OfType<MirYieldBare>().Any(),
                string.Join(",", allInsts.Select(i => i.GetType().Name).Distinct()));
            CaseAssertions.CheckTrue("① PollingAlarm sheet 经 getid.type 物化 + MirTypeCheck 分流",
                allInsts.OfType<MirGetTypeId>().Any(g => g.TypeRef
                    == CoroutineSplitPass.PollingAlarmCanonical)
                && allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.Is && c.IsIndirect));
            var yieldBlock = resume.Blocks.Single(b => b.Id.EndsWith(".yield1",
                StringComparison.Ordinal) && b.Terminator is MirCondBranch);
            CaseAssertions.CheckTrue("① 挂起段：state 常量化存 frame + 分流条件分支",
                yieldBlock.Instructions.OfType<MirSetField>().Any(
                    s => s.FieldSymbol.Contains("#state@"))
                && yieldBlock.Terminator is MirCondBranch);
            var eventBlock = resume.Blocks.Single(b => b.Id.Contains(".yield1.event"));
            // L8：handle 直读改为 ensureHandle 调用（用户直继子类懒建
            // 默认底座）；登记仍走 rigi_alarm_wait 闸内握手
            CaseAssertions.CheckTrue("① event 分支：ensureHandle 取底座 + rigi_alarm_wait 登记",
                eventBlock.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("EventAlarm$ensureHandle("))
                && eventBlock.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("rigi_alarm_wait(")));
            var signaledBlock = resume.Blocks.Single(b => b.Id.Contains(".yield1.signaled"));
            CaseAssertions.CheckTrue("① 已触发分支：自重排（publish）+ ret SUSPENDED",
                signaledBlock.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$publishNative("))
                && signaledBlock.Terminator is MirRet);
            CaseAssertions.CheckTrue("① switch 两项 + 恢复块落 pollgate（不重调面）",
                resume.Blocks[0].Terminator is MirSwitch swAlarm
                && swAlarm.ItemTargets.Count == 2
                && BlockOf(resume, "mw.state.1").Terminator is MirBranch brAlarm
                && brAlarm.Target.Contains(".pollgate"));
            var pollGate = BlockOf(resume,
                ((MirBranch)BlockOf(resume, "mw.state.1").Terminator).Target);
            CaseAssertions.CheckTrue("① pollgate：poll_pending 判位 + 分支",
                pollGate.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("rigi_poll_pending("))
                && pollGate.Terminator is MirCondBranch);
            var probeBlock = resume.Blocks.Single(b => b.Id.Contains(".pollgate1.probe"));
            CaseAssertions.CheckTrue("① probe 块：$mw.poll_probe 直调 + 三路 switch",
                probeBlock.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical == CoroutineSplitPass.PollProbeCanonical)
                && probeBlock.Terminator is MirSwitch swProbe
                && swProbe.ItemTargets.Count == 3);
            var pollWait = resume.Blocks.Single(b => b.Id.Contains(".pollgate1.wait"));
            CaseAssertions.CheckTrue("① 未就绪：poll_schedule 退避 + ret SUSPENDED",
                pollWait.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("rigi_poll_schedule("))
                && pollWait.Terminator is MirRet);
            var pollReady = resume.Blocks.Single(b => b.Id.Contains(".pollgate1.ready"));
            CaseAssertions.CheckTrue("① 就绪：poll_clear + 落原后继",
                pollReady.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("rigi_poll_clear("))
                && pollReady.Terminator is MirBranch brReady
                && brReady.Target.Contains(".cont"));

            // ② probe 合成 fn 结构：IsPollProbe 标记 + alarm 胖引用参数
            // 借用（无 acquire/release）+ isReady 虚派发 + bool 双分支
            // 转 i32；异常垫尾 = ret -1 且 pending 不取（无 TakePending）
            var probe = ctx.Mir!.Functions.Single(f => f.Symbol.Canonical
                == CoroutineSplitPass.PollProbeCanonical);
            CaseAssertions.CheckTrue("② probe fn 标记与形态（alarm→i32）",
                probe.IsPollProbe && !probe.IsCoroutineResume
                && probe.ReturnType.Key == "i32"
                && probe.Parameters.Count == 1
                && probe.Parameters[0].Name == CoroutineSplitPass.ProbeParamName
                && probe.Parameters[0].Type.Canonical
                    == CoroutineSplitPass.PollingAlarmCanonical);
            CaseAssertions.CheckTrue("② probe entry：isReady 虚派发目标 + bool 分支",
                probe.Blocks[0].Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical == CoroutineSplitPass.PollProbeIsReadyCanonical)
                && probe.Blocks[0].Terminator is MirCondBranch);
            CaseAssertions.CheckTrue("② probe alarm 参数借用（全 fn 无其 acquire/release）",
                !probe.Blocks.SelectMany(b => b.Instructions).OfType<MirAcquireSlot>()
                    .Any(a => a.Local == CoroutineSplitPass.ProbeParamName)
                && !probe.Blocks.SelectMany(b => b.Instructions).OfType<MirReleaseSlot>()
                    .Any(r => r.Local == CoroutineSplitPass.ProbeParamName));
            var probePad = BlockOf(probe, RcInjectionPass.PropagateBlockId);
            CaseAssertions.CheckTrue("② probe 垫尾：ret -1 + pending 不取（无 TakePending）",
                probePad.Terminator is MirRet
                && !probePad.Instructions.OfType<MirTakePending>().Any()
                && probePad.Instructions.OfType<MirLoadResource>().Any(l =>
                    l.Resource is BilScalarResource error && error.LiteralText == "-1"),
                string.Join(",", probePad.Instructions.Select(i => i.GetType().Name)));

            // ③ 用户 PollingAlarm 子类：isReady override 经可达边收编
            //（probe 是 MIR 期合成，BIL 级不可见，无边则无 MIR 可发射）
            ctx = PipelineFromSource(
                "import core.coroutine.*\n" +
                "pub shared class Flip : PollingAlarm {\n" +
                "    pub var ready: bool = false\n" +
                "    pub override func isReady(): bool { return ready }\n" +
                "}\n" +
                "async func arm(f: Flip): i32 {\n" +
                "    yield f\n" +
                "    return 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flip()\n" +
                "    var t = arm(f)\n" +
                "    return 0\n" +
                "}\n", "coro.alarm2.bil");
            CaseAssertions.CheckTrue("③ isReady override 可达边收编",
                ctx.Mir!.Functions.Any(f => f.Symbol.Canonical.Contains("Flip$isReady")),
                string.Join(",", ctx.Mir.Functions.Select(f => f.Symbol.Canonical)
                    .Where(c => c.Contains("isReady"))));
        }

        // ===== MW11c 棒5a：协程 Emit 发射形态（.ll 黄金锚点，新模型）=====

        private static void TestCoroutineEmit()
        {
            // 单 await + 裸 yield 混合源：create 调用点、resume 签名与
            // switch 分发、frame sheet、Rigi 桥方法调用、导出符号包装、
            // rigi_entry 的 workerLoop drain 段一次覆盖
            var ctx = PipelineFromSource(
                "async func one(): i32 { return 1 }\n" +
                "async func work(n: i32): i32 {\n" +
                "    var t = one()\n" +
                "    yield\n" +
                "    var r = await t\n" +
                "    return r + n\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = work(41)\n" +
                "    return 0\n" +
                "}\n", "coro.emit1.bil");
            using var llvmLease1727 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll = module.PrintToString();

            var stub = StubOf(ctx, "$work(");
            var resumeCanonical = "$mw.resume." + stub.Symbol.Canonical;
            var frameCanonical = "$mw.frame." + stub.Symbol.Canonical;
            CaseAssertions.CheckTrue("Emit① rigi_coroutine_create 声明与调用（i64 双参）",
                ll.Contains("declare i64 @rigi_coroutine_create(i64, i64)")
                && ll.Contains("call i64 @rigi_coroutine_create("), ll);
            CaseAssertions.CheckTrue("Emit① resume fn 以 i32(ptr) C ABI 发射",
                ll.Contains("define internal i32 @\"" + resumeCanonical + "\"(ptr"), ll);
            CaseAssertions.CheckTrue("Emit① resume entry 读 state + switch 分发",
                ll.Contains("switch i32"), ll);
            CaseAssertions.CheckTrue("Emit① frame sheet 全局已发射",
                ll.Contains(GenericAbi.EscapeGlobalName("typesheet.", frameCanonical)), ll);
            CaseAssertions.CheckTrue("Emit① Rigi 桥方法调用（registerWaiter/publish）",
                ll.Contains("$registerWaiter(") && ll.Contains("$publishNative("), ll);
            CaseAssertions.CheckTrue("Emit① 导出符号包装恒发射（外部链接）",
                ll.Contains("define void @rigi_dispatcher_entry()")
                && ll.Contains("define void @rigi_dispatch_publish(i64"), ll);
            CaseAssertions.CheckTrue("Emit① rigi_entry：Dispatcher workerLoop drain 段",
                ll.Contains("$workerLoop(")
                && ll.Contains("declare void @rigi_main_worker_shutdown()"), ll);

            // 异常路径：resume 垫尾 = 失败注册表登记 + fail + ret DONE
            ctx = PipelineFromSource(
                "async func boom(): i32 { throw new core.RuntimeException(\"x\") }\n" +
                "pub func main(): i32 {\n" +
                "    var t = boom()\n" +
                "    return 0\n" +
                "}\n", "coro.emit2.bil");
            using var llvmLease1758 = LlvmHost.Enter();
            using var module2 = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll2 = module2.PrintToString();
            CaseAssertions.CheckTrue("Emit② resume 垫尾调 rigi_failure_record",
                ll2.Contains("declare i64 @rigi_failure_record(")
                && ll2.Contains("call i64 @rigi_failure_record("), ll2);
            CaseAssertions.CheckTrue("Emit② rigi_entry 未观察失败走注册表",
                ll2.Contains("declare i32 @rigi_failure_take_unobserved(ptr)")
                && ll2.Contains("call i32 @rigi_failure_take_unobserved("), ll2);

            // ③ yield Alarm：event 分支 alarm_wait + probe fn 普通 MIR
            // 形态发射 + 两分类 sheet
            ctx = PipelineFromSource(
                "import core.coroutine.*\n" +
                "async func nap(): i32 {\n" +
                "    yield sleep(1)\n" +
                "    return 6\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = nap()\n" +
                "    return 0\n" +
                "}\n", "coro.emit3.bil");
            using var llvmLease1779 = LlvmHost.Enter();
            using var module3 = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll3 = module3.PrintToString();
            CaseAssertions.CheckTrue("Emit③ rigi_alarm_wait 声明与调用",
                ll3.Contains("declare i32 @rigi_alarm_wait(i64, i64)")
                && ll3.Contains("call i32 @rigi_alarm_wait("), ll3);
            CaseAssertions.CheckTrue("Emit③ probe fn 以普通 MIR 形态发射（无 C ABI 特判）",
                ll3.Contains("define internal i32 @\""
                    + CoroutineSplitPass.PollProbeCanonical + "\"("), ll3);
            CaseAssertions.CheckTrue("Emit③ probe 垫尾 ret -1（pending 不取）",
                ll3.Contains("i32 -1"), ll3);
            CaseAssertions.CheckTrue("Emit③ 两分类 sheet 全局已发射",
                ll3.Contains("typesheet." + CoroutineSplitPass.PollingAlarmCanonical)
                && ll3.Contains("typesheet." + CoroutineSplitPass.EventAlarmCanonical), ll3);
        }

        private static MirFunction BuildMainMir(string source, string fileName)
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(source);
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, fileName);
            CaseAssertions.CheckTrue(fileName + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var mir = MirBuilder.Build(new MwContext(gate.Module!));
            return mir.Functions.Single(f => f.IsEntrypoint);
        }

        private static MirBlock BlockEnding(MirFunction fn, string suffix) =>
            fn.Blocks.Single(b => b.Id.EndsWith(suffix, StringComparison.Ordinal));

        private static string ExcVarOf(MirFunction fn) =>
            fn.Locals.Single(l => l.Type.Canonical.Contains("Nullable")
                && l.Type.Canonical.Contains("Exception")).Name;

    }
}
