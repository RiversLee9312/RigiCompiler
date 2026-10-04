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
        // Polling 职责；与主文件共享同一类型、字段及生命周期。

        // PollingAlarm 恢复闸（yield-alarm 的 state N 恢复块落点）：
        // poll_pending 判位——EventAlarm 响铃恢复（pending=0）直续原
        // 后继；PollingAlarm 重发布恢复（pending=1）先探测。
        // Phase 2.6 双路径（§19.2 语义纠偏——isReady 允许 await/yield）：
        // - 探测闭包含 tainted 实现（point.ProbeSite 非空）：经恢复块
        //   站点协议臂下钻——首入按 alarm 运行期类型分流建探测 frame、
        //   MirResumeCall 调 isReady 状态机；探测中途挂起（SUSPENDED/
        //   YIELDED）写探测子状态 ProbeState 退回等待，唤醒后经专用
        //   恢复块下钻续跑至完成再做一次性就绪判定（ready → poll_clear
        //   + 续行；not → poll_schedule + ret SUSPENDED，state 保持 N）；
        //   FAILED 沿 yield 点词法 try/catch 失败尾。untainted 联合臂
        //   保持 $mw.poll_probe 同步廉价路径（目标必非 tainted，虚派发
        //   安全）；分流 miss = 闭包外类型，防御不可达。
        // - 全 untainted 闭包：现状 $mw.poll_probe 同步虚派发（ready/
        //   not/异常三路 switch）。异常（-1，pending 已置位）→ 失败尾
        //   分叉：Tasked = 失败终态序列（Task FAILED，await 点重抛——
        //   对齐 VM ProbePolling 的 yield 点失败口径）；B-2 Plain =
        //   ret FAILED（pending 保持置位沿链上传，调用方调用点 FAILED
        //   臂取走重抛——对齐 VM 帧栈逐层展开口径；release 序列由
        //   RcInjection 标准 ret 出口配平）
        private void EmitPollGate(MwContext context, MirModule mir, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, MirLocalOperand frameOp,
            string stateFieldSymbol, string? taskFieldSymbol, string? taskTypeRef,
            RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave)
        {
            var gateId = PollGateBlockId(point);
            var probeId = gateId + ".probe";
            var readyId = gateId + ".ready";
            var waitId = gateId + ".wait";
            var failId = gateId + ".fail";
            var alarmSlot = ((MirYieldAlarm)point.Inst).AlarmSlot;

            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(resumeFn, prefix, type);

            var cur = Fresh("$mw.poll.cur.", I64);
            var pend = Fresh("$mw.poll.pend.", I32);
            var pendOne = Fresh("$mw.poll.p1.", I32);
            var isPending = Fresh("$mw.poll.isp.", Bool);
            resumeFn.AddBlock(new MirBlock(gateId, new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur),
                new MirCall(syms.PollPending,
                    new List<MirOperand> { new MirLocalOperand(cur) }, pend),
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 0), pendOne),
                new MirBinaryIntrinsic(BilBinaryOp.CmpNe, new MirLocalOperand(pend),
                    new MirLocalOperand(pendOne), I32, I32, Bool, isPending),
            }, new MirCondBranch(new MirLocalOperand(isPending), probeId,
                ContBlockId(point))));

            if (point.ProbeSite is { } probe)
            {
                var parkId = gateId + ".park";
                var cheapId = probeId + ".cheap";
                var pdispId = PollProbeResumeDispatchId(point);

                // 首入探测分流链（深→浅）与恢复分流链共用生成器：链上
                // 逐臂逐 type.is 目标 OR（任一命中即进臂）。首入命中
                // tainted 臂 → new 块建探测 frame；命中 untainted 联合
                // 臂 → $mw.poll_probe 廉价路径；miss = 闭包外类型——
                // vtable 派发目标必在闭包内，防御不可达。恢复链命中
                // tainted 臂 → 直落调用块（探测 frame 从本层 frame 槽
                // 恢复）；untainted 臂不可达（同步探测无挂起点，探测
                // 挂起前提即首入命中 tainted 臂）——防御 mw.state.bad
                void EmitProbeDispatchChain(string chainId, string checkPrefix,
                    bool resume)
                {
                    // untainted 联合尾链首（仅首入链存在；全 tainted 闭包
                    // 无尾——末臂 miss 直落防御不可达）
                    var hasTail = !resume && probe.UntaintedTypeRefs.Count > 0;
                    var tailId = probe.Arms.Count == 0
                        ? chainId
                        : checkPrefix + (probe.Arms.Count - 1) + ".next";
                    for (var k = 0; k < probe.Arms.Count; k++)
                    {
                        var (entry, typeRefs, _) = probe.Arms[k];
                        var checkId = k == 0 ? chainId : checkPrefix + (k - 1) + ".next";
                        var hitId = resume
                            ? ProbeCallBlockId(gateId, k)
                            : ProbeNewBlockId(gateId, k);
                        var missId = k + 1 < probe.Arms.Count
                            ? checkPrefix + k + ".next"
                            : hasTail ? tailId : "mw.state.bad";
                        for (var t = 0; t < typeRefs.Count; t++)
                        {
                            var cond = Fresh("$mw.pchk.", Bool);
                            var isLast = t + 1 >= typeRefs.Count;
                            var thisCheckId = t == 0 ? checkId : checkId + ".t" + t;
                            resumeFn.AddBlock(new MirBlock(thisCheckId,
                                new List<MirInst>
                                {
                                    new MirTypeCheck(MirTypeCheckKind.Is,
                                        new MirLocalOperand(alarmSlot), typeRefs[t],
                                        null, cond),
                                }, new MirCondBranch(new MirLocalOperand(cond), hitId,
                                    isLast ? missId : checkId + ".t" + (t + 1))));
                        }
                    }
                    if (!hasTail)
                    {
                        return;
                    }
                    // 首入链 untainted 联合尾：单臂多 type.is 目标 OR
                    var tailRefs = probe.UntaintedTypeRefs;
                    for (var t = 0; t < tailRefs.Count; t++)
                    {
                        var cond = Fresh("$mw.pchk.", Bool);
                        var isLast = t + 1 >= tailRefs.Count;
                        var thisCheckId = t == 0 ? tailId : tailId + ".t" + t;
                        resumeFn.AddBlock(new MirBlock(thisCheckId,
                            new List<MirInst>
                            {
                                new MirTypeCheck(MirTypeCheckKind.Is,
                                    new MirLocalOperand(alarmSlot), tailRefs[t],
                                    null, cond),
                            }, new MirCondBranch(new MirLocalOperand(cond), cheapId,
                                isLast ? "mw.state.bad" : tailId + ".t" + (t + 1))));
                    }
                }

                // 首入链（probeId = 链首）
                EmitProbeDispatchChain(probeId, gateId + ".pc", resume: false);

                // 每 tainted 实现：建探测 frame + 落参（首入）→ 调用块
                for (var k = 0; k < probe.Arms.Count; k++)
                {
                    var entry = probe.Arms[k].Impl;
                    var callId = ProbeCallBlockId(gateId, k);
                    var newId = ProbeNewBlockId(gateId, k);
                    var doneId = ProbeDoneBlockId(gateId, k);
                    var calleeOp = new MirLocalOperand(entry.CalleeLocal);
                    var frameType = context.Symbols.FindType(entry.CalleeFrameCanonical)
                        ?? throw new CompilerInternalException(
                            "tainted isReady frame 未预注册: " + entry.CalleeFrameCanonical);
                    var frameInit = context.Symbols.FindMember(
                        SyntheticTypePlanner.FrameInitCanonicalOf(entry.CalleeFrameCanonical))
                        ?? throw new CompilerInternalException(
                            "tainted isReady frame init 缺失: " + entry.CalleeFrameCanonical);
                    var newInsts = new List<MirInst>
                    {
                        new MirNewObject(frameType, null, frameInit,
                            new List<MirOperand>(), entry.CalleeLocal),
                    };
                    foreach (var drop in entry.Drops)
                    {
                        if (drop.Operand != null)
                        {
                            var operand = drop.Operand;
                            if (operand is MirLocalOperand local
                                && drop.OperandTargetType is { } targetType)
                            {
                                var sourceType = resumeFn.FindLocal(local.Name).Type;
                                if (sourceType.Canonical != targetType.Canonical
                                    && (TypeLayout.IsGenericPlaceholder(sourceType)
                                        || TypeLayout.IsGenericPlaceholder(targetType)))
                                {
                                    var converted = Fresh("$mw.probe.arg.", targetType);
                                    newInsts.Add(TypeLayout.ClassifySlot(context.Layout,
                                        sourceType) == ManagedSlotKind.FatReference
                                        ? new MirCopyLocal(operand, converted)
                                        : new MirBoxAny(operand, converted));
                                    operand = new MirLocalOperand(converted);
                                }
                            }
                            newInsts.Add(new MirSetField(operand, calleeOp,
                                drop.FrameFieldSymbol));
                        }
                        else if (drop.TypeIdTypeRef != null)
                        {
                            var tid = Fresh("$mw.probe.tid.", MirType.Of(".typeid"));
                            newInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                            newInsts.Add(new MirSetField(new MirLocalOperand(tid),
                                calleeOp, drop.FrameFieldSymbol));
                        }
                        else if (drop.ReceiverTypeIdOwner != null)
                        {
                            var tid = Fresh("$mw.probe.tid.", MirType.Of(".typeid"));
                            newInsts.Add(new MirGetClassTypeArgument(
                                drop.ReceiverTypeIdOperand ?? new MirLocalOperand(alarmSlot),
                                drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                            newInsts.Add(new MirSetField(new MirLocalOperand(tid),
                                calleeOp, drop.FrameFieldSymbol));
                        }
                        else
                        {
                            newInsts.Add(new MirSetField(
                                new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                                drop.FrameFieldSymbol));
                        }
                    }
                    // callee 槽回存本层 frame：探测挂起后 N' 恢复块从本层
                    // frame 恢复该槽下钻（EmitCallSplit 同口径）
                    emitSave(newInsts, new[] { entry.CalleeLocal });
                    resumeFn.AddBlock(new MirBlock(newId, newInsts,
                        new MirBranch(callId)));

                    var code = Fresh("$mw.poll.code.", I32);
                    var codeTable = new BilSwitchTableResource(
                        "$mw.coroutine.pollprobe." + _resourceCounter++, ".i32",
                        new[] { "0", "1", "2", "3" });
                    context.Module.Resources.Add(codeTable);
                    resumeFn.AddBlock(new MirBlock(callId, new List<MirInst>
                    {
                        new MirResumeCall(entry.ResumeSymbol, entry.CalleeLocal, code),
                    }, new MirSwitch(new MirLocalOperand(code), codeTable,
                        new[] { parkId, parkId, doneId, failId }, "mw.state.bad")));

                    // DONE：一次性就绪判定——探测返回 true → 就绪续行；
                    // false → 退回等待（未就绪分支与廉价路径 waitId 共用）。
                    // 探测完成即脱离子状态：state 写回 N（就绪续行后的
                    // 下一个挂起点会覆写；未就绪重排恢复必须重回 poll
                    // gate 再次首探，而非探测恢复链）
                    var doneInsts = new List<MirInst>();
                    var doneState = Fresh("$mw.poll.st.", I32);
                    doneInsts.Add(new MirLoadResource(
                        ProxyWildcardAbi.AddI32Resource(context, point.State),
                        doneState));
                    doneInsts.Add(new MirSetField(new MirLocalOperand(doneState),
                        frameOp, stateFieldSymbol));
                    var rdy = Fresh("$mw.poll.rdy.", Bool);
                    doneInsts.Add(new MirGetField(calleeOp,
                        entry.ResultFieldSymbol!, rdy));
                    resumeFn.AddBlock(new MirBlock(doneId, doneInsts,
                        new MirCondBranch(new MirLocalOperand(rdy), readyId,
                            waitId)));
                }

                // 探测中途挂起（isReady 状态机 SUSPENDED/YIELDED 上传）：
                // 挂起即继续等待——写探测子状态 N'（恢复入口直落探测恢复
                // 分流链续跑）+ ret SUSPENDED。poll_pending 保持置位；
                // 唤醒源是 isReady 内部的挂起源（await Task/EventAlarm/
                // 裸 yield 重发布），polling 退避扫描的重复重发布无害
                //（N' 恢复 → 探测幂等续跑）。TaskState 投影仅 Tasked
                var parkInsts = new List<MirInst>();
                var parkState = Fresh("$mw.poll.st.", I32);
                parkInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddI32Resource(context, probe.ProbeState),
                    parkState));
                parkInsts.Add(new MirSetField(new MirLocalOperand(parkState),
                    frameOp, stateFieldSymbol));
                if (taskFieldSymbol != null)
                {
                    EmitTaskMark(context, parkInsts, frameOp, taskFieldSymbol,
                        taskTypeRef!, "markSuspended", Fresh);
                }
                var parkCode = Fresh("$mw.code.", I32);
                parkInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddI32Resource(context, ResumeSuspended),
                    parkCode));
                resumeFn.AddBlock(new MirBlock(parkId, parkInsts,
                    new MirRet(new MirLocalOperand(parkCode))));

                // 恢复分流链（N' 恢复块落点；probeId 已被首入链占用，
                // pdispId 即链首）
                EmitProbeDispatchChain(pdispId, gateId + ".prc", resume: true);

                // untainted 联合臂：$mw.poll_probe 同步廉价路径（现状
                // probeId 内容；运行期目标必非 tainted——虚派发安全）
                var pr = Fresh("$mw.poll.pr.", I32);
                // probe 内用户 isReady 抛出时，普通 MirCall 的 pending 检查须
                // 直接落探测失败臂；否则会绕到 resume 函数级传播垫，跳过
                // yield 点保存的词法 try/catch。
                var probeFailTarget = new MirBlock(failId, new List<MirInst>(),
                    new MirUnreachable());
                var probeTable = new BilSwitchTableResource(
                    "$mw.coroutine.probe." + _resourceCounter++, ".i32",
                    new[] { "-1", "0", "1" });
                context.Module.Resources.Add(probeTable);
                resumeFn.AddBlock(new MirBlock(cheapId, new List<MirInst>
                {
                    new MirCall(EnsurePollProbe(context, mir),
                        new List<MirOperand> { new MirLocalOperand(alarmSlot) }, pr,
                        probeFailTarget),
                }, new MirSwitch(new MirLocalOperand(pr), probeTable,
                    new[] { failId, waitId, readyId }, "mw.state.bad")));
            }
            else
            {
                // 全 untainted 闭包：廉价路径（现状形态）
                var pr = Fresh("$mw.poll.pr.", I32);
                // probe 内用户 isReady 抛出时，普通 MirCall 的 pending 检查须
                // 直接落探测失败臂；否则会绕到 resume 函数级传播垫，跳过
                // yield 点保存的词法 try/catch。
                var probeFailTarget = new MirBlock(failId, new List<MirInst>(),
                    new MirUnreachable());
                var probeTable = new BilSwitchTableResource(
                    "$mw.coroutine.probe." + _resourceCounter++, ".i32",
                    new[] { "-1", "0", "1" });
                context.Module.Resources.Add(probeTable);
                resumeFn.AddBlock(new MirBlock(probeId, new List<MirInst>
                {
                    new MirCall(EnsurePollProbe(context, mir),
                        new List<MirOperand> { new MirLocalOperand(alarmSlot) }, pr,
                        probeFailTarget),
                }, new MirSwitch(new MirLocalOperand(pr), probeTable,
                    new[] { failId, waitId, readyId }, "mw.state.bad")));
            }

            // ready：解除轮询状态 → 原后继续行
            var curR = Fresh("$mw.poll.cur.", I64);
            resumeFn.AddBlock(new MirBlock(readyId, new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curR),
                new MirCall(syms.PollClear,
                    new List<MirOperand> { new MirLocalOperand(curR) }, null),
            }, new MirBranch(ContBlockId(point))));

            // not ready：退避重排程（VM SchedulePoll 同口径）→ 挂起
            var curW = Fresh("$mw.poll.cur.", I64);
            var waitInsts = new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curW),
                new MirCall(syms.PollSchedule,
                    new List<MirOperand> { new MirLocalOperand(curW) }, null),
            };
            // TaskState 投影仅 Tasked（plain 无 Task 可投影）
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, waitInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", Fresh);
            }
            var waitCode = Fresh("$mw.code.", I32);
            waitInsts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, ResumeSuspended), waitCode));
            resumeFn.AddBlock(new MirBlock(waitId, waitInsts,
                new MirRet(new MirLocalOperand(waitCode))));

            // 探测异常（pending 已置位）：Tasked 走失败终态序列（与
            // RcInjection resume 垫尾同构——统一走 EmitFailTerminal）；
            // B-2 Plain 走链式上传（ret FAILED——与 RcInjection plain
            // 垫尾同口径，托管槽 release 由标准 ret 出口配平）
            var curF = Fresh("$mw.poll.cur.", I64);
            var failInsts = new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curF),
                new MirCall(syms.PollClear,
                    new List<MirOperand> { new MirLocalOperand(curF) }, null),
            };
            MirTerminator failRet;
            var yieldInst = (MirYieldAlarm)point.Inst;
            if (yieldInst.ExcTarget != null)
            {
                var exc = Fresh("$mw.poll.exc.", Any);
                failInsts.Add(new MirTakePending(exc));
                failInsts.Add(new MirThrow(new MirLocalOperand(exc), yieldInst.ExcTarget));
                failRet = new MirBranch(yieldInst.ExcTarget.Id);
            }
            else if (taskFieldSymbol != null)
            {
                failRet = EmitFailTerminal(context, mir, resumeFn, failInsts,
                    taskFieldSymbol, taskTypeRef!, syms);
            }
            else
            {
                var failCode = Fresh("$mw.code.", I32);
                failInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddI32Resource(context, PlainResumeFailedCode),
                    failCode));
                failRet = new MirRet(new MirLocalOperand(failCode));
            }
            resumeFn.AddBlock(new MirBlock(failId, failInsts, failRet));
        }

    }
}
