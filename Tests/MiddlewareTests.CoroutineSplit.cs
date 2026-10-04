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
        // CoroutineSplit 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestCoroutineSplit()
        {
            // ① 单 await 切分形态（全管线：split 后 RcInjection 自检通过
            // 即扩展生效——move 槽免配平/resume 垫分叉由下方显式断言复核）
            var ctx = PipelineFromSource(
                "async func one(): i32 { return 1 }\n" +
                "async func work(n: i32): i32 {\n" +
                "    var t = one()\n" +
                "    var r = await t\n" +
                "    return r + n\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = work(41)\n" +
                "    return 0\n" +
                "}\n", "coro.split1.bil");
            var stub = StubOf(ctx, "$work(");
            var frameCanonical = "$mw.frame." + stub.Symbol.Canonical;
            TestHarness.CheckTrue("① stub 保留原符号、返回 Task<T>",
                stub.IsAsync && !stub.IsCoroutineResume
                && stub.ReturnType.Canonical == "core.coroutine::Task<core::i32>",
                stub.ReturnType.Canonical);
            TestHarness.CheckTrue("① stub 单 entry 块（RcInjection 可另附传播垫）",
                stub.Blocks[0].Id == "entry"
                && stub.Blocks.All(b => b.Id == "entry"
                    || b.Id == RcInjectionPass.PropagateBlockId));
            var stubInsts = stub.Blocks[0].Instructions;
            // 棒5a 新形态：new frame + 参数落 frame + new Task（合成空
            // init）+ frame.task 回填 + MirCoroutineCreate + lane 继承 +
            // attachRuntime/noteSpawn/publish（Rigi 世界方法调）
            var create = stubInsts.OfType<MirCoroutineCreate>().SingleOrDefault();
            TestHarness.CheckTrue("① stub 含 MirCoroutineCreate",
                create != null, string.Join(",", stubInsts.Select(i => i.GetType().Name)));
            TestHarness.CheckTrue("① MirCoroutineCreate 携 resume 符号与 i64 句柄槽",
                create!.ResumeFn.Canonical == "$mw.resume." + stub.Symbol.Canonical
                && stub.FindLocal(create.HandleSlot).Type.Key == "i64");
            TestHarness.CheckTrue("① stub：new frame + new Task + 参数落 frame 字段",
                stubInsts.OfType<MirNewObject>().Any(n => n.Type.Canonical == frameCanonical)
                && stubInsts.OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical.StartsWith("core.coroutine::Task",
                        StringComparison.Ordinal))
                && stubInsts.OfType<MirSetField>().Any(s => s.FieldSymbol.Contains("#n@")));
            TestHarness.CheckTrue("① stub：frame.task 回填 + Rigi 桥方法调用",
                stubInsts.OfType<MirSetField>().Any(s =>
                    s.FieldSymbol.Contains("#" + CoroutineSplitPass.TaskSlotName + "@"))
                && stubInsts.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$attachRuntimeNative("))
                && stubInsts.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$noteSpawn("))
                && stubInsts.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$publishNative(")));
            // move 免配平：create 之后不得再有 frame 槽 release（create 前
            // 的产出前置 release 放的是零值，合法）
            var createIndex = stubInsts.Select((inst, i) => (inst, i))
                .First(t => ReferenceEquals(t.inst, create)).i;
            TestHarness.CheckTrue("① frame 槽 move 免配平（create 后无其 release）",
                stubInsts.Skip(createIndex).OfType<MirReleaseSlot>()
                    .All(r => r.Local != create.FrameSlot),
                string.Join(",", stubInsts.OfType<MirReleaseSlot>().Select(r => r.Local)));

            var resume = ResumeOf(ctx, stub);
            TestHarness.CheckTrue("① resume fn 标记与形态（frame→i32）",
                resume.IsCoroutineResume && resume.ReturnType.Key == "i32"
                && resume.Parameters.Count == 1
                && resume.Parameters[0].Name == CoroutineSplitPass.FrameParamName);
            TestHarness.CheckTrue("① resume entry：读 state + switch 分发",
                resume.Blocks[0].Id == "mw.entry"
                && resume.Blocks[0].Instructions[0] is MirGetField
                && resume.Blocks[0].Terminator is MirSwitch sw0
                && sw0.ItemTargets.Count == 2
                && sw0.ItemTargets[0] == "mw.state.0"
                && sw0.ItemTargets[1] == "mw.state.1"
                && sw0.DefaultTarget == "mw.state.bad");
            // 棒5a：wait 块 = gate 临界区 + registerWaiter 决策；reg 块
            // 四路 switch（0 挂起 / 1 成功 / 2 失败 / 3 防御）
            var regBlock = resume.Blocks.Single(b => b.Id.EndsWith(".wait1.reg",
                StringComparison.Ordinal));
            TestHarness.CheckTrue("① reg 块：registerWaiter + release + 四路 switch",
                regBlock.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$registerWaiter("))
                && regBlock.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("rigi_sync_mutex_release("))
                && regBlock.Terminator is MirSwitch swWait
                && swWait.ItemTargets.Count == 4);
            var waitBlock = resume.Blocks.Single(b => b.Id.EndsWith(".wait1",
                StringComparison.Ordinal));
            TestHarness.CheckTrue("① wait 块：gate 读取 + acquire + 冷启动判位",
                waitBlock.Instructions.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol.Contains("#gate@"))
                && waitBlock.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("rigi_sync_mutex_acquire("))
                && waitBlock.Terminator is MirCondBranch);
            var coldGo = resume.Blocks.Single(b => b.Id.Contains(".wait1.coldgo"));
            TestHarness.CheckTrue("① 冷启动分支：spawnIntoLocked + noteSpawn + publish",
                coldGo.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$spawnIntoLocked("))
                && coldGo.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$noteSpawn("))
                && coldGo.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$publishRuntime(")));
            var suspend = resume.Blocks.Single(b => b.Id.Contains(".wait1.suspend"));
            TestHarness.CheckTrue("① suspend 块：直返（frame 已在 reg 临界区写完）",
                suspend.Terminator is MirRet);
            TestHarness.CheckTrue("① reg 块：存 frame + state 常量化（临界区内）",
                regBlock.Instructions.OfType<MirSetField>().Any(
                    s => s.FieldSymbol.Contains("#state@")));
            TestHarness.CheckTrue("① suspend 出口 release 不含 frame（续体持有）",
                suspend.Instructions.OfType<MirReleaseSlot>().All(
                    r => r.Local != CoroutineSplitPass.FrameParamName));
            TestHarness.CheckTrue("① suspend 出口 release 含活跃托管槽 t",
                suspend.Instructions.OfType<MirReleaseSlot>().Any(r => r.Local == "t"),
                string.Join(",", suspend.Instructions.OfType<MirReleaseSlot>()
                    .Select(r => r.Local)));
            var done = resume.Blocks.Single(b => b.Id.Contains(".wait1.done"));
            TestHarness.CheckTrue("① done 块：读 result 字段 + 解包续行",
                done.Instructions.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol.Contains("#result@"))
                && done.Instructions.OfType<MirUnwrapNullable>().Any()
                && done.Terminator is MirBranch brDone && brDone.Target.Contains(".cont1"));
            var fail = resume.Blocks.Single(b => b.Id.Contains(".wait1.fail"));
            var pad = BlockOf(resume, RcInjectionPass.PropagateBlockId);
            TestHarness.CheckTrue("① FAILED 块：注册表读异常 + MirThrow 沿边进垫",
                fail.Instructions.OfType<MirFailureLoad>().Any()
                && fail.Instructions.OfType<MirThrow>().Any()
                && fail.Terminator is MirBranch brFail
                && brFail.Target == RcInjectionPass.PropagateBlockId);
            TestHarness.CheckTrue("① resume 垫尾：TakePending + 失败终态序列 + ret DONE",
                pad.Instructions[0] is MirTakePending
                && pad.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("rigi_failure_record("))
                && pad.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$fail("))
                && pad.Instructions.OfType<MirCoroutineDone>().Any()
                && pad.Terminator is MirRet);
            TestHarness.CheckTrue("① resume 垫尾 frame 最终 release（末位）",
                pad.Instructions.OfType<MirReleaseSlot>().LastOrDefault()?.Local
                    == CoroutineSplitPass.FrameParamName,
                string.Join(",", pad.Instructions.OfType<MirReleaseSlot>()
                    .Select(r => r.Local)));
            var complete = resume.Blocks.Single(
                b => b.Instructions.OfType<MirCoroutineDone>().Any()
                    && b.Id != RcInjectionPass.PropagateBlockId
                    && b.Instructions.OfType<MirCall>().Any(c =>
                        c.Target.Canonical.Contains("$complete(")));
            TestHarness.CheckTrue("① DONE 出口：complete + noteTerminal + ret + frame 末位 release",
                complete.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$noteTerminal("))
                && complete.Terminator is MirRet
                && complete.Instructions.OfType<MirReleaseSlot>().Last().Local
                    == CoroutineSplitPass.FrameParamName);
            TestHarness.CheckTrue("① 结果写 Task<T>.result（MirWrapNullable + setfield）",
                complete.Instructions.OfType<MirWrapNullable>().Any()
                && complete.Instructions.OfType<MirSetField>().Any(s =>
                    s.FieldSymbol.Contains("#result@")));
            var framePlan = ctx.Layout!.Find(frameCanonical);
            TestHarness.CheckTrue("① frame 字段：state + task + 参数 n + 活跃槽 t",
                framePlan != null
                && framePlan.Fields.Any(f => f.Symbol.Contains("#state@"))
                && framePlan.Fields.Any(f => f.Symbol.Contains(
                    "#" + CoroutineSplitPass.TaskSlotName + "@"))
                && framePlan.Fields.Any(f => f.Symbol.Contains("#n@"))
                && framePlan.Fields.Any(f => f.Symbol.Contains("#t@")),
                framePlan == null ? "<null>" : string.Join(",", framePlan.Fields
                    .Select(f => f.Symbol)));

            // ② 无挂起点 async fn 统一切分 + Task 零结果完成（无包装）
            ctx = PipelineFromSource(
                "async func ping() { }\n" +
                "pub func main(): i32 {\n" +
                "    var t = ping()\n" +
                "    return 1\n" +
                "}\n", "coro.split2.bil");
            stub = StubOf(ctx, "$ping(");
            TestHarness.CheckTrue("② 无挂起点 async fn 同样切 stub",
                stub.ReturnType.Canonical == "core.coroutine::Task"
                && stub.Blocks[0].Instructions.OfType<MirCoroutineCreate>().Any());
            resume = ResumeOf(ctx, stub);
            TestHarness.CheckTrue("② 无挂起点：switch 仅 state 0",
                resume.Blocks[0].Terminator is MirSwitch swPing
                && swPing.ItemTargets.Count == 1);
            complete = resume.Blocks.Single(
                b => b.Instructions.OfType<MirCoroutineDone>().Any()
                    && b.Id != RcInjectionPass.PropagateBlockId);
            TestHarness.CheckTrue("② Task 完成无结果包装（无 MirWrapNullable）",
                !complete.Instructions.OfType<MirWrapNullable>().Any()
                && complete.Instructions.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.Contains("$complete(")));

            // ②b void Task await Task<T>：result 字段必须走 Task<TReturn>
            // 声明（不得误用当前协程自身的 void Task 前缀）
            ctx = PipelineFromSource(
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "async func run() {\n" +
                "    var x = await add(1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n", "coro.split.void-await-generic.bil");
            resume = ResumeOf(ctx, StubOf(ctx, "$run("));
            var doneVoidAwait = resume.Blocks.Single(b => b.Id.Contains(".done",
                StringComparison.Ordinal) && b.Id.Contains(".wait",
                StringComparison.Ordinal));
            TestHarness.CheckTrue(
                "②b void Task await Task<T>：result 宿主是 Task<TReturn>",
                doneVoidAwait.Instructions.OfType<MirGetField>().Any(g =>
                    g.FieldSymbol.StartsWith("core.coroutine::Task<TReturn>#result@",
                        StringComparison.Ordinal)),
                string.Join(",", doneVoidAwait.Instructions.OfType<MirGetField>()
                    .Select(g => g.FieldSymbol)));

            // ②c invoke.indirect async $$call：变量调用 async lambda 产
            // Task，await 仍是挂起点；间接调用留在 resume 里走虚派发
            ctx = PipelineFromSource(
                "async func run(): i32 {\n" +
                "    const f = func{async (): i32 -> 1}\n" +
                "    return await f()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    return 0\n" +
                "}\n", "coro.split.async-indirect.bil");
            resume = ResumeOf(ctx, StubOf(ctx, "$run("));
            TestHarness.CheckTrue("②c resume 含 MirInvokeIndirect（async $$call）",
                resume.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirInvokeIndirect>().Any(),
                string.Join(",", resume.Blocks.SelectMany(b => b.Instructions)
                    .Select(i => i.GetType().Name)));
            TestHarness.CheckTrue("②c async lambda 调用仍切出 wait/registerWaiter",
                resume.Blocks.Any(b => b.Id.Contains(".wait"))
                && resume.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirCall>().Any(c =>
                        c.Target.Canonical.Contains("$registerWaiter(")));

            // ③ 同 fn 多 await：每挂起点恰一恢复 state
            ctx = PipelineFromSource(
                "async func one(): i32 { return 1 }\n" +
                "async func sum(): i32 {\n" +
                "    var a = await one()\n" +
                "    var b = await one()\n" +
                "    return a + b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = sum()\n" +
                "    return 0\n" +
                "}\n", "coro.split3.bil");
            stub = StubOf(ctx, "$sum(");
            resume = ResumeOf(ctx, stub);
            TestHarness.CheckTrue("③ 双 await：switch 三项（0/1/2）",
                resume.Blocks[0].Terminator is MirSwitch swSum
                && swSum.ItemTargets.Count == 3
                && swSum.ItemTargets[2] == "mw.state.2");
            TestHarness.CheckTrue("③ 双 await：两个 wait 块",
                resume.Blocks.Count(b => b.Id.Contains(".wait")) >= 2
                && resume.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirCall>().Count(c =>
                        c.Target.Canonical.Contains("$registerWaiter(")) == 2);

            // ④ 裸 yield：挂起段 = 存 frame + Dispatcher.publish 自重排
            // + ret YIELDED；恢复块落原后继
            ctx = PipelineFromSource(
                "async func step(): i32 {\n" +
                "    yield\n" +
                "    return 5\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = step()\n" +
                "    return 0\n" +
                "}\n", "coro.split4.bil");
            stub = StubOf(ctx, "$step(");
            resume = ResumeOf(ctx, stub);
            TestHarness.CheckTrue("④ 裸 yield：publish 自重排入挂起段",
                resume.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Count(c => c.Target.Canonical.Contains("$publishNative(")) == 1
                && !resume.Blocks.SelectMany(b => b.Instructions).OfType<MirYieldBare>()
                    .Any());
            TestHarness.CheckTrue("④ yield：switch 两项 + 恢复块落原后继",
                resume.Blocks[0].Terminator is MirSwitch swYield
                && swYield.ItemTargets.Count == 2
                && BlockOf(resume, "mw.state.1").Terminator is MirBranch brY
                && brY.Target.Contains(".cont"));

            // ⑤ loop 内 await：循环控制槽进 frame
            ctx = PipelineFromSource(
                "async func one(): i32 { return 1 }\n" +
                "async func acc(): i32 {\n" +
                "    var s = 0\n" +
                "    for (i in 0 to 2) {\n" +
                "        var t = one()\n" +
                "        s = s + await t\n" +
                "    }\n" +
                "    return s\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = acc()\n" +
                "    return 0\n" +
                "}\n", "coro.split5.bil");
            stub = StubOf(ctx, "$acc(");
            framePlan = ctx.Layout!.Find("$mw.frame." + stub.Symbol.Canonical);
            TestHarness.CheckTrue("⑤ loop 内 await：累加器/枚举器控制槽进 frame",
                framePlan != null
                && framePlan.Fields.Any(f => f.Symbol.Contains("#s@"))
                && framePlan.Fields.Any(f => f.Symbol.Contains("#.s0@")),
                framePlan == null ? "<null>" : string.Join(",", framePlan.Fields
                    .Select(f => f.Symbol)));

            // ⑥ try/finally 穿越 await：finally 清理状态（$mw.retv）进 frame
            ctx = PipelineFromSource(
                "async func one(): i32 { return 1 }\n" +
                "async func f(): i32 {\n" +
                "    try {\n" +
                "        return 7\n" +
                "    } finally(_) {\n" +
                "        var t = one()\n" +
                "        await t\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = f()\n" +
                "    return 0\n" +
                "}\n", "coro.split6.bil");
            stub = StubOf(ctx, "$f(");
            framePlan = ctx.Layout!.Find("$mw.frame." + stub.Symbol.Canonical);
            TestHarness.CheckTrue("⑥ finally 内 await：待返回槽 $mw.retv 进 frame",
                framePlan != null && framePlan.Fields.Any(f => f.Symbol.Contains("retv")),
                framePlan == null ? "<null>" : string.Join(",", framePlan.Fields
                    .Select(f => f.Symbol)));

            // ⑦ async 实例方法：.this 进 frame
            ctx = PipelineFromSource(
                "async func one(): i32 { return 1 }\n" +
                "shared class Counter {\n" +
                "    pub var n: i32\n" +
                "    pub init(v: i32) { n = v }\n" +
                "    pub async func bump(): i32 {\n" +
                "        var t = one()\n" +
                "        var r = await t\n" +
                "        return n + r\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter(1)\n" +
                "    var t = c.bump()\n" +
                "    return 0\n" +
                "}\n", "coro.split7.bil");
            stub = StubOf(ctx, "Counter$bump(");
            framePlan = ctx.Layout!.Find("$mw.frame." + stub.Symbol.Canonical);
            TestHarness.CheckTrue("⑦ 实例方法：.this 进 frame",
                framePlan != null && framePlan.Fields.Any(f => f.Symbol.Contains("#.this@")),
                framePlan == null ? "<null>" : string.Join(",", framePlan.Fields
                    .Select(f => f.Symbol)));

            // ⑧ 泛型类 async 方法：类级 typeid 恒进 frame
            ctx = PipelineFromSource(
                "shared class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(x: T) { v = x }\n" +
                "    pub async func get(): T { return v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<String>(\"x\")\n" +
                "    var t = b.get()\n" +
                "    return 0\n" +
                "}\n", "coro.split8.bil");
            stub = StubOf(ctx, "Box$get(");
            framePlan = ctx.Layout!.Find("$mw.frame." + stub.Symbol.Canonical);
            TestHarness.CheckTrue("⑧ 泛型类：类级 .generic.T typeid 进 frame",
                framePlan != null
                && framePlan.Fields.Any(f => f.Symbol.Contains("#.generic.T@")),
                framePlan == null ? "<null>" : string.Join(",", framePlan.Fields
                    .Select(f => f.Symbol)));
            // 泛型 async 返回 Task<T>：NewObject 不得落 arity-0 void Task
            // sheet（gate@72 vs Task<T> gate@88 → native 空 mutex 句柄）
            TestHarness.CheckTrue("⑧ new Task 身份带实参（非 void Task sheet）",
                stub.Blocks[0].Instructions.OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical.StartsWith("core.coroutine::Task<",
                        StringComparison.Ordinal)),
                string.Join(",", stub.Blocks[0].Instructions.OfType<MirNewObject>()
                    .Select(n => n.Type.Canonical)));

            // ⑨ FAILED 重抛沿用原 ExcTarget（try 内 await → 派发垫，非传播垫）
            ctx = PipelineFromSource(
                "async func boom(): i32 { throw new core.RuntimeException(\"x\") }\n" +
                "async func guarded(): i32 {\n" +
                "    try {\n" +
                "        var t = boom()\n" +
                "        var r = await t\n" +
                "        return r\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 3\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = guarded()\n" +
                "    return 0\n" +
                "}\n", "coro.split9.bil");
            stub = StubOf(ctx, "$guarded(");
            resume = ResumeOf(ctx, stub);
            fail = resume.Blocks.Single(b => b.Id.Contains(".wait") && b.Id.Contains(".fail"));
            TestHarness.CheckTrue("⑨ FAILED 块：MirThrow 沿原 ExcTarget 进 try 派发垫",
                fail.Instructions.OfType<MirThrow>().SingleOrDefault() is { } throwInst
                && throwInst.ExcTarget != null
                && throwInst.ExcTarget.Id.Contains(".dispatch")
                && fail.Terminator is MirBranch brG
                && brG.Target == throwInst.ExcTarget.Id,
                fail.Terminator?.GetType().Name ?? "<null>");

            // ⑩ B-1 栈式跨界（SYNTAX §11）：main 直接 await —— main
            // Task 包装 split（stub ret 热 Task + resume + frame）+
            // $mw.main.settle 合成；模块无残留 MirAwait
            ctx = PipelineFromSource(
                "async func one(): i32 { return 1 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = one()\n" +
                "    var r = await t\n" +
                "    return r\n" +
                "}\n", "coro.split10.bil");
            var mainStub = StubOf(ctx, "$main(");
            TestHarness.CheckTrue("⑩ main Task 包装 split（stub IsAsync + ret Task）",
                mainStub.IsAsync
                && mainStub.ReturnType.Canonical.StartsWith("core.coroutine::Task",
                    StringComparison.Ordinal),
                mainStub.Symbol.Canonical + " → " + mainStub.ReturnType.Canonical);
            var mainResume = ResumeOf(ctx, mainStub);
            TestHarness.CheckTrue("⑩ main resume 含 await wait 块（四路分流）",
                mainResume.Blocks.Any(b => b.Id.Contains(".wait")),
                string.Join(", ", mainResume.Blocks.Select(b => b.Id)));
            TestHarness.CheckTrue("⑩ $mw.main.settle 已合成",
                ctx.Mir!.Functions.Any(f => f.Symbol.Canonical.StartsWith(
                    "$mw.main.settle(", StringComparison.Ordinal)), "");
            TestHarness.CheckTrue("⑩ 模块无残留 MirAwait",
                ctx.Mir.Functions.SelectMany(f => f.Blocks)
                    .SelectMany(b => b.Instructions).All(i => i is not MirAwait), "");

            // ⑫ B-1：main → 单层同步 fn（内含 await）——callee 裸
            // frame split（IsPlainResume resume + $mw.result 字段 +
            // 原符号陷阱 stub），main resume 内调用点改写为
            // MirResumeCall 四码分流
            ctx = PipelineFromSource(
                "async func one(): i32 { return 1 }\n" +
                "func addTwice(n: i32): i32 {\n" +
                "    var t = one()\n" +
                "    var r = await t\n" +
                "    return r + n\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return addTwice(41)\n" +
                "}\n", "coro.split12.bil");
            var plainStub = StubOf(ctx, "$addTwice(");
            TestHarness.CheckTrue("⑫ tainted 普通 fn 原符号改陷阱 stub",
                !plainStub.IsAsync
                && plainStub.Blocks.Single().Terminator is MirUnreachable,
                plainStub.Symbol.Canonical);
            var plainResume = ResumeOf(ctx, plainStub);
            TestHarness.CheckTrue("⑫ plain resume 标记（IsCoroutineResume + IsPlainResume）",
                plainResume.IsCoroutineResume && plainResume.IsPlainResume, "");
            mainResume = ResumeOf(ctx, StubOf(ctx, "$main("));
            var callBlock = mainResume.Blocks.SingleOrDefault(b =>
                b.Instructions.OfType<MirResumeCall>().Any());
            TestHarness.CheckTrue("⑫ 调用点 MirResumeCall 四码分流",
                callBlock is { } cb
                && cb.Instructions.OfType<MirResumeCall>().Any()
                && cb.Terminator is MirSwitch,
                string.Join(", ", mainResume.Blocks.Select(b => b.Id)));
            TestHarness.CheckTrue("⑫ callee frame 含 $mw.result 字段",
                plainResume.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirSetField>()
                    .Any(s => s.FieldSymbol.Contains("#$mw.result@")), "");

            // ⑪ 不透明 AsyncAction 槽：跳过 coldtask 工厂，bindColdBody
            // 改写为 type.is 链 + $mw.bindcold.*（不再 MwNotSupported）
            ctx = PipelineFromSource(
                "import core.coroutine.*\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const t = wrap(func{async () -> { }})\n" +
                "    await t\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n", "coro.split11.bil");
            TestHarness.CheckTrue("⑪ 不透明 body 冷 Task 含 $mw.bindcold helper",
                ctx.Mir!.Functions.Any(f =>
                    f.Symbol.Canonical.Contains("$mw.bindcold.", StringComparison.Ordinal)),
                string.Join(",", ctx.Mir!.Functions.Select(f => f.Symbol.Canonical)
                    .Where(c => c.Contains("$mw.", StringComparison.Ordinal))));
            var bindBody = ctx.Mir.Functions.FirstOrDefault(f =>
                f.Symbol.Canonical.StartsWith("core.coroutine::Task$bindColdBody(",
                    StringComparison.Ordinal));
            TestHarness.CheckTrue("⑪ bindColdBody 含 type.is 链",
                bindBody != null
                && bindBody.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirTypeCheck>().Any(),
                bindBody == null ? "<null>" : string.Join(",",
                    bindBody.Blocks.SelectMany(b => b.Instructions)
                        .Select(i => i.GetType().Name)));

            // ⑫ 继承 $$call 的冷 body（Sub : Base : AsyncAction，Sub
            // 自身无 $$call）：工厂沿 extends 链解析，frame/resume 取
            // 声明宿主 Base 的 split 产物（VM 拍平 sheet 同语义）
            ctx = PipelineFromSource(
                "import core.coroutine.*\n" +
                "pub shared abstract class Base : core.AsyncAction {\n" +
                "    pub override async operator call() { }\n" +
                "}\n" +
                "pub shared class Sub : Base {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "async func run() {\n" +
                "    const t = new Task(new Sub())\n" +
                "    await t\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n", "coro.split12.bil");
            var coldFactory = ctx.Mir!.Functions.FirstOrDefault(f =>
                f.Symbol.Canonical.Contains("$mw.coldtask.", StringComparison.Ordinal)
                && f.Symbol.Canonical.Contains("|Sub", StringComparison.Ordinal));
            TestHarness.CheckTrue("⑫ 继承 $$call 冷 body 工厂生成",
                coldFactory != null,
                string.Join(",", ctx.Mir!.Functions.Select(f => f.Symbol.Canonical)
                    .Where(c => c.Contains("$mw.", StringComparison.Ordinal))));
            TestHarness.CheckTrue("⑫ 工厂 frame 取声明宿主 Base 的 split 产物",
                coldFactory != null
                && coldFactory.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirNewObject>().Any(n =>
                        n.Type.Canonical.Contains("Base$$call(",
                            StringComparison.Ordinal)),
                coldFactory == null ? "<null>" : string.Join(",",
                    coldFactory.Blocks.SelectMany(b => b.Instructions)
                        .OfType<MirNewObject>().Select(n => n.Type.Canonical)));
        }

        // ===== R2 残留边界负例：保留受控拒绝的形态钉住 =====

        private static void TestCoroutineSplitRejection()
        {
            // R2-b：proxy/wrapper 烘焙链（$.mwrapped.）触达含挂起点
            // fn——受控拒绝（router/trampoline 通配 ABI 的值包转发形
            // 态无挂起协议插点，运行期目标集随 wrapper 实例符号表动
            // 态决定，静态闭包不可枚举）；$$call/invoke.indirect 已
            // 放开（NativeE2E「栈式跨界 lambda 间接调用挂起」对拍）
            var wrapperCaught = false;
            try
            {
                PipelineFromSource(
                    "@WrapperTarget(.Method)\n" +
                    "pub wrapper Timed {\n" +
                    "    pub init()\n" +
                    "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                    "        var r = inner(x)\n" +
                    "        return (((r as i32) + 1) as TReturn)\n" +
                    "    }\n" +
                    "}\n" +
                    "pub func main(): i32 {\n" +
                    "    var f = func{ @Timed (x: i32): i32 -> {\n" +
                    "        yield\n" +
                    "        return@_ x + 1\n" +
                    "    }}\n" +
                    "    return f(41)\n" +
                    "}\n", "coro.reject.wrapper.bil");
            }
            catch (MwNotSupportedException ex)
            {
                wrapperCaught = ex.Message.Contains("proxy/wrapper 烘焙链");
            }
            TestHarness.CheckTrue("R2-b proxy 链 tainted 受控拒绝", wrapperCaught);

            // R2-c：值类型 init 含挂起点——受控拒绝（值类型构造路
            // 径无挂起协议：frame .this 借用形态与 sret/原地构造不
            // 兼容；此前静态 new 形态静默语义错位——native exit 5
            // 无输出 vs VM 正常——补闸）。class init 已放开
            //（NativeE2E「栈式跨界 new.indirect init 内挂起」对拍）
            var structCaught = false;
            try
            {
                PipelineFromSource(
                    "pub struct S {\n" +
                    "    pub var x: i32\n" +
                    "    pub init(v: i32) {\n" +
                    "        yield\n" +
                    "        x = v\n" +
                    "    }\n" +
                    "}\n" +
                    "pub func main(): i32 {\n" +
                    "    var s = new S(41)\n" +
                    "    return s.x\n" +
                    "}\n", "coro.reject.structinit.bil");
            }
            catch (MwNotSupportedException ex)
            {
                structCaught = ex.Message.Contains("值类型 init");
            }
            TestHarness.CheckTrue("R2-c 值类型 init tainted 受控拒绝", structCaught);

            // Phase 2.6（§19.2 语义纠偏）：PollingAlarm.isReady 允许含挂
            // 起点——原受控拒绝（76e304c #08 止血：tainted 实现 split 后
            // vtable 槽指陷阱、$mw.poll_probe 同步虚派发无恢复泵）随本节
            // 撤销。tainted 探测改经恢复块站点协议臂下钻：poll gate 分流
            // 命中 tainted 实现臂 → 建探测 frame + MirResumeCall 调
            // isReady 状态机（探测中途可挂起）；untainted 闭包仍走
            // $mw.poll_probe 同步廉价路径
            var probeCtx = PipelineFromSource(
                "import core.coroutine.*\n" +
                "pub shared class BadPoll : PollingAlarm {\n" +
                "    pub const cell: core.AtomicStruct\\<i64>\n" +
                "    pub init() { cell = new core.AtomicStruct\\<i64>(0L) }\n" +
                "    pub override func isReady(): bool {\n" +
                "        return cell.load() >= 0L\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var p = new BadPoll()\n" +
                "    yield p\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n", "coro.pollprobe.tainted.bil");
            var runStub = StubOf(probeCtx, "$run(");
            var runResume = ResumeOf(probeCtx, runStub);
            var runInsts = runResume.Blocks.SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue(
                "tainted isReady 编译通过（拒绝已撤销）", runInsts.Count > 0);
            TestHarness.CheckTrue(
                "tainted 探测站点协议化：MirResumeCall 直调 isReady 状态机",
                runInsts.OfType<MirResumeCall>().Any(rc =>
                    rc.ResumeFn.Canonical.Contains("BadPoll$isReady")),
                string.Join(",", runResume.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirResumeCall>().Select(rc => rc.ResumeFn.Canonical)));
            TestHarness.CheckTrue(
                "tainted 探测站点分流链化（probe 链首块走 type.is 臂而非同步三路 switch；"
                + "闭包含 abstract 声明臂时 untainted 联合尾仍保留廉价路径）",
                runResume.Blocks
                    .Where(b => b.Id.Contains(".pollgate") && b.Id.EndsWith(".probe"))
                    .All(b => b.Terminator is MirCondBranch));
            // 探测挂起子状态：isReady 中途挂起后重发布恢复的专用入口
            TestHarness.CheckTrue(
                "探测挂起子状态恢复分流链块存在",
                runResume.Blocks.Any(b => b.Id.Contains(".pollgate")
                    && b.Id.Contains(".pdisp")));
        }

    }
}
