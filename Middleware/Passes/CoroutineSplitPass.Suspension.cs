using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    public sealed partial class CoroutineSplitPass : IMwStage
    {
        // Suspension 职责；与主文件共享同一类型、字段及生命周期。


        // wait 块 = acquire gate →（handle==0 冷 Task：tryStart 一次性
        // 判定 → 赢家 spawnIntoLocked + noteSpawn + publish）→ 先存活跃
        // 槽 + state=N（临界区内——发布安全的前提：waiter 被排空重发布
        // 时 frame 必已写完）→ registerWaiter（当前协程句柄）→ release
        // → 四路 switch（0=挂起 ret SUSPENDED / 1=读 result 字段解包续行
        // / 2=native 失败注册表取异常沿原 ExcTarget 重抛 / 3=防御不可达）。
        // 恢复块重回 wait 块走终态快路径（task 槽在 frame）
        private void EmitAwaitSplit(MwContext context, MirFunction resumeFn, MirFunction fn,
            SuspensionPoint point, MirAwait awaitInst, string headId, List<MirInst> headInsts,
            MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh,
            System.Func<int, List<MirInst>, MirTerminator> resumeRet)
        {
            var awaitedTaskTypeRef = AwaitedTaskTypeRef(fn, awaitInst);
            var code = fresh("$mw.await.code.", I32);
            var waitId = WaitBlockId(point);
            var coldId = waitId + ".cold";
            var coldGoId = waitId + ".coldgo";
            var regId = waitId + ".reg";
            var suspendId = waitId + ".suspend";
            var doneId = waitId + ".done";
            var failId = waitId + ".fail";
            var cancelId = waitId + ".cancel";
            var taskOp = new MirLocalOperand(awaitInst.TaskSlot);
            var gateField = TaskField(context, awaitedTaskTypeRef, "gate", ".i64");

            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(waitId)));

            // wait：acquire → 冷启动分支 → 寄存
            var gate = fresh("$mw.await.gate.", I64);
            var hasRuntime = fresh("$mw.await.runtime.", Bool);
            var waitInsts = new List<MirInst>
            {
                new MirGetField(taskOp, gateField, gate),
                new MirCall(syms.MutexAcquire,
                    new List<MirOperand> { new MirLocalOperand(gate) }, null),
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "hasRuntime"),
                    new List<MirOperand> { taskOp }, hasRuntime),
            };
            resumeFn.AddBlock(new MirBlock(waitId, waitInsts,
                new MirCondBranch(new MirLocalOperand(hasRuntime), regId, coldId)));

            // 冷 Task 首次 await（§18.3/§18.4）：同一临界区内一次性判定
            // ——赢家 spawn-into（当前 Executor）+ noteSpawn + publish，
            // 输家按普通 waiter 登记（不抛）
            var st = fresh("$mw.await.st.", I32);
            var stZero = fresh("$mw.await.stz.", I32);
            var stGo = fresh("$mw.await.stgo.", Bool);
            resumeFn.AddBlock(new MirBlock(coldId, new List<MirInst>
            {
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "tryStart"),
                    new List<MirOperand> { taskOp }, st),
                i32Const(0, stZero),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(st),
                    new MirLocalOperand(stZero), I32, I32, Bool, stGo),
            }, new MirCondBranch(new MirLocalOperand(stGo), coldGoId, regId)));

            var dispC = fresh("$mw.disp.", MirType.Of(BilCompilerSymbols.Resolve(context.Module, DispatcherCanonical)));
            resumeFn.AddBlock(new MirBlock(coldGoId, new List<MirInst>
            {
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "spawnIntoLocked"),
                    new List<MirOperand> { taskOp }, null),
                new MirCall(syms.DispatcherGet, new List<MirOperand>(), dispC),
                new MirCall(syms.NoteSpawn,
                    new List<MirOperand> { new MirLocalOperand(dispC) }, null),
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "publishRuntime"),
                    new List<MirOperand> { taskOp }, null),
            }, new MirBranch(regId)));

            // reg：存活跃槽 + state=N（临界区内，见上注释）→
            // registerWaiter → release → 四路 switch
            var regInsts = new List<MirInst>();
            emitSave(regInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            regInsts.Add(i32Const(point.State, stateConst));
            regInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            var cur = fresh("$mw.await.cur.", I64);
            regInsts.Add(new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur));
            regInsts.Add(new MirCall(TaskFn(context, awaitedTaskTypeRef, "registerWaiter"),
                new List<MirOperand> { taskOp, new MirLocalOperand(cur) }, code));
            regInsts.Add(new MirCall(syms.MutexRelease,
                new List<MirOperand> { new MirLocalOperand(gate) }, null));
            var awaitTable = new BilSwitchTableResource(
                "$mw.coroutine.await." + _resourceCounter++, ".i32",
                new[]
                {
                    AwaitRegistered.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AwaitCompleted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AwaitFailed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AwaitCancelled.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            context.Module.Resources.Add(awaitTable);
            resumeFn.AddBlock(new MirBlock(regId, regInsts,
                new MirSwitch(new MirLocalOperand(code), awaitTable,
                    new[] { suspendId, doneId, failId, cancelId }, "mw.state.bad")));

            // 挂起：frame 已在临界区内写完，投影 Suspended，直返 SUSPENDED
            //（plain resume 无 Task——跳过投影，对齐 VM 无 TaskObject
            // 协程口径）
            var suspendInsts = new List<MirInst>();
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, suspendInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
            resumeFn.AddBlock(new MirBlock(suspendId, suspendInsts,
                resumeRet(ResumeSuspended, suspendInsts)));

            // 成功：读 result 字段（nullable<T>）解包回结果槽
            var doneInsts = new List<MirInst>();
            if (awaitInst.ResultSlot != null)
            {
                var resultType = fn.FindLocal(awaitInst.ResultSlot).Type;
                var rn = fresh("$mw.await.rn.",
                    MirType.Of(".nullable<" + resultType.Canonical + ">"));
                doneInsts.Add(new MirGetField(taskOp,
                    TaskField(context, awaitedTaskTypeRef, "result",
                        ".nullable<" + resultType.Canonical + ">"), rn));
                doneInsts.Add(new MirUnwrapNullable(new MirLocalOperand(rn),
                    resultType, awaitInst.ResultSlot, awaitInst.ExcTarget));
            }
            resumeFn.AddBlock(new MirBlock(doneId, doneInsts,
                new MirBranch(ContBlockId(point))));

            // 失败：native 失败注册表按节点 id 取异常（+1 随 out 移交），
            // 沿原 MirAwait 的 ExcTarget 重抛；null 留 RcInjection 进垫
            var nodeId = fresh("$mw.await.nid.", I64);
            var outFat = fresh("$mw.await.out.", Any);
            var failInsts = new List<MirInst>
            {
                new MirGetField(taskOp, TaskField(context, awaitedTaskTypeRef, "failureNodeId", ".i64"),
                    nodeId),
                new MirFailureLoad(nodeId, outFat),
                new MirThrow(new MirLocalOperand(outFat), awaitInst.ExcTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                awaitInst.ExcTarget != null
                    ? (MirTerminator)new MirBranch(awaitInst.ExcTarget.Id)
                    : new MirRetThrow()));

            resumeFn.AddBlock(new MirBlock(cancelId, new List<MirInst>(),
                new MirUnreachable()));
        }

        // Mutex.enter 改写（§19.6；对齐 VM MutexEnter）：gate 临界区内
        // tryEnter 判定；0=立即取得，release 后落原后继；1=已登记 FIFO
        // 队尾——临界区内写完 frame + markSuspended，再 release，ret
        // SUSPENDED。恢复块直落原后继（FIFO handoff 已移交锁，不重入
        // tryEnter）。enter 空体仅供 VM 方法 hook；native 本改写替换调用。
        private void EmitMutexEnterSplit(MwContext context, MirFunction resumeFn,
            SuspensionPoint point, MirCall enterCall, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh,
            System.Func<int, List<MirInst>, MirTerminator> resumeRet)
        {
            var mutexOp = enterCall.Args[0];
            var enterId = point.Block.Id + ".mxenter" + point.State;
            var gotId = enterId + ".got";
            var waitId = enterId + ".wait";
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(enterId)));

            var gate = fresh("$mw.mx.gate.", I64);
            var cur = fresh("$mw.mx.cur.", I64);
            var code = fresh("$mw.mx.code.", I32);
            var zero = fresh("$mw.mx.z.", I32);
            var got = fresh("$mw.mx.got.", Bool);
            var enterInsts = new List<MirInst>
            {
                new MirGetField(mutexOp, BilCompilerSymbols.Resolve(context.Module, MutexGateField), gate),
                new MirCall(syms.MutexAcquire,
                    new List<MirOperand> { new MirLocalOperand(gate) }, null),
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur),
                new MirCall(MutexFn(context, "tryEnter"),
                    new List<MirOperand> { mutexOp, new MirLocalOperand(cur) }, code),
                i32Const(0, zero),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(code),
                    new MirLocalOperand(zero), I32, I32, Bool, got),
            };
            resumeFn.AddBlock(new MirBlock(enterId, enterInsts,
                new MirCondBranch(new MirLocalOperand(got), gotId, waitId)));

            var gotInsts = new List<MirInst>
            {
                new MirCall(syms.MutexRelease,
                    new List<MirOperand> { new MirLocalOperand(gate) }, null),
            };
            resumeFn.AddBlock(new MirBlock(gotId, gotInsts,
                new MirBranch(ContBlockId(point))));

            var waitInsts = new List<MirInst>();
            emitSave(waitInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            waitInsts.Add(i32Const(point.State, stateConst));
            waitInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, waitInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
            waitInsts.Add(new MirCall(syms.MutexRelease,
                new List<MirOperand> { new MirLocalOperand(gate) }, null));
            resumeFn.AddBlock(new MirBlock(waitId, waitInsts,
                resumeRet(ResumeSuspended, waitInsts)));
        }

        // 裸 yield 改写（棒5a，对齐 VM YieldBare → Publish）：head →
        // 挂起段（存活跃槽 + state=N + Dispatcher.publish 自重排 +
        // ret YIELDED）；恢复块落原后继
        private void EmitYieldSplit(MwContext context, MirFunction resumeFn,
            SuspensionPoint point, string headId, List<MirInst> headInsts,
            MirLocalOperand frameOp, string stateFieldSymbol,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, List<MirInst>, MirTerminator> resumeRet,
            RuntimeSyms syms, System.Func<string, MirType, string> fresh)
        {
            var yieldId = point.Block.Id + ".yield" + point.State;
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(yieldId)));
            var insts = new List<MirInst>();
            emitSave(insts, point.LiveAfter);
            // state 常量化存 frame（恢复块经 switch(frame.state) 到达）
            var stateConst = fresh("$mw.state.c.", I32);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, point.State), stateConst));
            insts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            // Dispatcher.publish(当前协程)：自重排回所属 lane（lane 从
            // cohandle 槽读，§19.1 只保证重新经过一次调度决策）
            var cur = fresh("$mw.yield.cur.", I64);
            var disp = fresh("$mw.disp.", MirType.Of(BilCompilerSymbols.Resolve(context.Module, DispatcherCanonical)));
            insts.Add(new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur));
            insts.Add(new MirCall(syms.DispatcherGet, new List<MirOperand>(), disp));
            insts.Add(new MirCall(syms.Publish,
                new List<MirOperand> { new MirLocalOperand(disp),
                    new MirLocalOperand(cur) }, null));
            resumeFn.AddBlock(new MirBlock(yieldId, insts,
                resumeRet(ResumeYielded, insts)));
        }

        // 带 Alarm 的 yield 改写（棒5a）：head → 挂起段（存活跃槽 +
        // state=N + MirTypeCheck 分流）：
        //   PollingAlarm：poll_arm（退避复位）+ 自重排 + ret SUSPENDED
        //    ——恢复块（pollgate）先探测，未就绪 poll_schedule 再挂；
        //   EventAlarm：rigi_alarm_wait 闸内登记（§19.3 原子握手）——
        //    未触发 ret SUSPENDED 等响铃；已触发自重排（执行段仍结束，
        //    对齐 VM「已触发仍 Publish」口径）。
        private void EmitYieldAlarmSplit(MwContext context, MirFunction resumeFn,
            SuspensionPoint point, MirYieldAlarm yieldAlarm,
            string headId, List<MirInst> headInsts, MirLocalOperand frameOp,
            string stateFieldSymbol, string? taskFieldSymbol, string? taskTypeRef,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, List<MirInst>, MirTerminator> resumeRet,
            RuntimeSyms syms, System.Func<string, MirType, string> fresh)
        {
            var yieldId = point.Block.Id + ".yield" + point.State;
            var pollId = yieldId + ".poll";
            var eventId = yieldId + ".event";
            var signaledId = yieldId + ".signaled";
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(yieldId)));

            var insts = new List<MirInst>();
            emitSave(insts, point.LiveAfter);
            var stateConst = ProxyWildcardAbi.FreshLocal(resumeFn, "$mw.state.c.", I32);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, point.State), stateConst));
            insts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            // 运行时分类：PollingAlarm sheet 物化 + type.is.indirect
            var tid = fresh("$mw.tid.poll.", MirType.Of(".typeid"));
            var isPoll = fresh("$mw.ispoll.", Bool);
            insts.Add(new MirGetTypeId(PollingAlarmCanonical, tid));
            insts.Add(new MirTypeCheck(MirTypeCheckKind.Is,
                new MirLocalOperand(yieldAlarm.AlarmSlot), null,
                new MirLocalOperand(tid), isPoll));
            resumeFn.AddBlock(new MirBlock(yieldId, insts,
                new MirCondBranch(new MirLocalOperand(isPoll), pollId, eventId)));

            // PollingAlarm：arm + 自重排（VM YieldAlarm polling 段
            //  Publish 同口径；探测在恢复块进行）
            var curP = fresh("$mw.yield.cur.", I64);
            var dispP = fresh("$mw.disp.", MirType.Of(BilCompilerSymbols.Resolve(context.Module, DispatcherCanonical)));
            var pollInsts = new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curP),
                new MirCall(syms.PollArm,
                    new List<MirOperand> { new MirLocalOperand(curP) }, null),
                new MirCall(syms.DispatcherGet, new List<MirOperand>(), dispP),
                new MirCall(syms.Publish,
                    new List<MirOperand> { new MirLocalOperand(dispP),
                        new MirLocalOperand(curP) }, null),
            };
            resumeFn.AddBlock(new MirBlock(pollId, pollInsts,
                resumeRet(ResumeSuspended, pollInsts)));

            // EventAlarm：闸内登记 waiter；已触发（粘滞）→ 自重排。
            // L8：先经 ensureHandle 取底座句柄——用户直继子类
            // handle==0 时懒建手动事件粘滞底座（VM TryAwaitTimer
            // 同口径懒建），此后 rigi_alarm_wait 恒收非 0 句柄
            var ah = fresh("$mw.yield.ah.", I64);
            var curE = fresh("$mw.yield.cur.", I64);
            var rc = fresh("$mw.yield.rc.", I32);
            var rcZero = fresh("$mw.yield.rcz.", I32);
            var notRegistered = fresh("$mw.yield.sig.", Bool);
            var eventInsts = new List<MirInst>
            {
                new MirCall(EventAlarmFn(context, "ensureHandle"),
                    new List<MirOperand> { new MirLocalOperand(yieldAlarm.AlarmSlot) }, ah),
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curE),
                new MirCall(syms.AlarmWait,
                    new List<MirOperand> { new MirLocalOperand(ah),
                        new MirLocalOperand(curE) }, rc),
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 0), rcZero),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(rc),
                    new MirLocalOperand(rcZero), I32, I32, Bool, notRegistered),
            };
            resumeFn.AddBlock(new MirBlock(eventId, eventInsts,
                new MirCondBranch(new MirLocalOperand(notRegistered), signaledId,
                    yieldId + ".registered")));

            // 已触发：自重排后结束执行段（§19.4 末条：带 Alarm 的 yield
            // 恒结束当前执行段）
            var curS = fresh("$mw.yield.cur.", I64);
            var dispS = fresh("$mw.disp.", MirType.Of(BilCompilerSymbols.Resolve(context.Module, DispatcherCanonical)));
            var signaledInsts = new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curS),
                new MirCall(syms.DispatcherGet, new List<MirOperand>(), dispS),
                new MirCall(syms.Publish,
                    new List<MirOperand> { new MirLocalOperand(dispS),
                        new MirLocalOperand(curS) }, null),
            };
            resumeFn.AddBlock(new MirBlock(signaledId, signaledInsts,
                resumeRet(ResumeSuspended, signaledInsts)));

            // 已登记：等响铃，结束执行段（TaskState 投影仅 Tasked——
            // plain 无 Task 可投影，对齐 VM 无 TaskObject 协程口径）
            var registeredInsts = new List<MirInst>();
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, registeredInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
            resumeFn.AddBlock(new MirBlock(yieldId + ".registered",
                registeredInsts, resumeRet(ResumeSuspended, registeredInsts)));
        }

    }
}
