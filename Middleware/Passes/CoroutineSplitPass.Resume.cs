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
        // Resume 职责；与主文件共享同一类型、字段及生命周期。

        // ===== ④ resume fn 合成 =====
        // plan.Mode = Tasked：frame 带 $mw.task，挂起/恢复做 TaskState
        // 投影（markSuspended/markRunnable），DONE 尾走 Task 终态序列；
        // plan.Mode = Plain（B-1 tainted 普通 fn）：无 Task——投影跳过，
        // DONE 尾只写 $mw.result + ret DONE（frame 所有权归调用方，
        // 不做最终 release），失败经 RcInjection plain 垫尾 ret FAILED
        private MirFunction BuildResumeFunction(MwContext context, MirModule mir,
            SplitPlan plan, System.Func<MirLocal, string> fieldOf)
        {
            var fn = plan.Fn;
            var points = plan.Points;
            var savedSlots = plan.SavedSlots;
            var paramNames = plan.ParamNames;
            var stateFieldSymbol = plan.StateFieldSymbol;
            var taskFieldSymbol = plan.TaskFieldSymbol;
            var taskTypeRef = plan.TaskTypeRef;
            // 局部表：frame 参数 + 原 fn 全部局部副本（同名同型；参数在
            // resume fn 是普通局部，state 0 从 frame 恢复）
            var frameParam = new MirLocal(FrameParamName, plan.FrameMirType);
            var locals = new List<MirLocal> { frameParam };
            foreach (var local in fn.Locals)
            {
                locals.Add(new MirLocal(local.Name, local.Type));
            }
            var resumeFn = new MirFunction(plan.ResumeSymbol, I32,
                new List<MirLocal> { frameParam }, locals, new List<MirBlock>(),
                false, isCoroutineResume: true,
                isPlainResume: plan.Mode == SplitMode.Plain);
            if (plan.Mode == SplitMode.Plain) resumeFn.RestoredEntrySource = fn.Symbol.Canonical;
            var frameOp = new MirLocalOperand(FrameParamName);
            var syms = Syms(context, mir);

            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(resumeFn, prefix, type);
            MirLoadResource I32Const(int value, string target) =>
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, value), target);

            // 保存/恢复序列（保 savedSlots 序；frame 字段显式读写）
            void EmitSave(List<MirInst> insts, IReadOnlyList<string> slotNames)
            {
                foreach (var name in slotNames)
                {
                    var local = fn.FindLocal(name);
                    insts.Add(new MirSetField(new MirLocalOperand(name), frameOp,
                        fieldOf(local)));
                }
            }

            void EmitRestore(List<MirInst> insts, IReadOnlyList<string> slotNames)
            {
                foreach (var name in slotNames)
                {
                    var local = fn.FindLocal(name);
                    insts.Add(new MirGetField(frameOp, fieldOf(local), name));
                }
            }

            MirTerminator ResumeRet(int code, List<MirInst> insts)
            {
                var codeLocal = Fresh("$mw.code.", I32);
                insts.Add(I32Const(code, codeLocal));
                return new MirRet(new MirLocalOperand(codeLocal));
            }

            // entry：读 state → MirSwitch 分发（state 0=原入口，N=各恢复
            // 块；default=损坏防御不可达）。块 id 取 mw.entry——原 fn 首块
            // 同名 "entry" 且整体迁入本 fn，同名会在 Emit 块表撞键
            var stateLocal = Fresh("$mw.state.", I32);
            var itemTargets = new List<string> { "mw.state.0" };
            var tableElements = new List<string> { "0" };
            foreach (var point in points)
            {
                itemTargets.Add("mw.state." + point.State);
                tableElements.Add(point.State.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (point.ProbeSite != null)
                {
                    // Phase 2.6：探测挂起子状态——isReady 状态机中途挂起
                    // 后重发布恢复的专用入口（区别于未就绪退回等待后的
                    // 再次首探：那条路 state 保持 N 重入 poll gate）
                    itemTargets.Add("mw.state." + point.ProbeSite.ProbeState);
                    tableElements.Add(point.ProbeSite.ProbeState.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            var stateTable = new BilSwitchTableResource(
                "$mw.coroutine.state." + _resourceCounter++, ".i32", tableElements);
            context.Module.Resources.Add(stateTable);
            resumeFn.AddBlock(new MirBlock("mw.entry", new List<MirInst>
            {
                new MirGetField(frameOp, stateFieldSymbol, stateLocal),
            }, new MirSwitch(new MirLocalOperand(stateLocal), stateTable, itemTargets,
                "mw.state.bad")));
            resumeFn.AddBlock(new MirBlock("mw.state.bad", new List<MirInst>(),
                new MirUnreachable()));

            // state 0：恢复参数 + 类级 .generic.* 局部（初段的输入）后进原
            // 入口块；非参数局部此态未定义，不恢复
            var entryRestore = savedSlots
                .Where(l => paramNames.Contains(l.Name)
                    || l.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                .Select(l => l.Name).ToList();
            var state0Insts = new List<MirInst>();
            EmitRestore(state0Insts, entryRestore);
            var entryBlockId = fn.Blocks[0].Id;
            resumeFn.AddBlock(new MirBlock("mw.state.0", state0Insts,
                new MirBranch(entryBlockId)));

            // 挂起点恢复块（state N）：恢复活跃槽 → 重回 wait 块（await）
            // 或 yield-alarm 探测块 / 调用块（tainted 直调点）/ 原后继
            //（裸 yield / Mutex.enter）
            foreach (var point in points)
            {
                var restoreInsts = new List<MirInst>();
                EmitRestore(restoreInsts, point.LiveAfter);
                string resumeTarget;
                if (point.CallSite != null || point.InitSite != null)
                {
                    // B-1：重回调用块——callee frame 已从本 frame 的
                    // callee 槽恢复，MirResumeCall 再下钻
                    resumeTarget = CallBlockId(point);
                }
                else if (point.Virtual != null)
                {
                    // B-2：重回恢复分流链（type.is 重判——接收者已从
                    // frame 恢复，运行期类型不变必命中同臂）直落对应
                    // 实现的调用块再下钻
                    resumeTarget = VirtualResumeDispatchId(point);
                }
                else if (point.IndirectInit != null)
                {
                    // R2-c：重回恢复分流链（IsTypeId 重判——typeid
                    // 已从 frame 恢复，值不变必命中同臂）直落对应
                    // 臂的调用块再下钻
                    resumeTarget = IndirectInitResumeDispatchId(point);
                }
                else if (point.Inst is MirAwait)
                {
                    resumeTarget = WaitBlockId(point);
                }
                else if (point.Inst is MirYieldAlarm)
                {
                    // 棒5a：先经轮询判位分流——EventAlarm 响铃恢复直续
                    // 原后继；PollingAlarm 重发布恢复先探测
                    resumeTarget = PollGateBlockId(point);
                    // TaskState 投影仅 Tasked（plain 无 Task 可投影）
                    if (taskFieldSymbol != null)
                    {
                        EmitRestoreMark(context, restoreInsts, frameOp,
                            taskFieldSymbol, taskTypeRef!, Fresh);
                    }
                    resumeFn.AddBlock(new MirBlock("mw.state." + point.State, restoreInsts,
                        new MirBranch(resumeTarget)));
                    if (point.ProbeSite != null)
                    {
                        // Phase 2.6：探测挂起子状态 N' 恢复块——活跃槽
                        //（含探测 callee frame）从本层 frame 恢复后重回
                        // 探测恢复分流链，isReady 从挂起点续跑至完成再做
                        // 一次性就绪判定（对齐 VM 恢复式探测语义）
                        var probeRestore = new List<MirInst>();
                        EmitRestore(probeRestore, point.LiveAfter);
                        if (taskFieldSymbol != null)
                        {
                            EmitRestoreMark(context, probeRestore, frameOp,
                                taskFieldSymbol, taskTypeRef!, Fresh);
                        }
                        resumeFn.AddBlock(new MirBlock(
                            "mw.state." + point.ProbeSite.ProbeState, probeRestore,
                            new MirBranch(PollProbeResumeDispatchId(point))));
                    }
                    EmitPollGate(context, mir, resumeFn, fn, point, frameOp,
                        stateFieldSymbol, taskFieldSymbol, taskTypeRef, syms,
                        EmitSave);
                    continue;
                }
                else
                {
                    // 裸 yield / Mutex.enter：恢复直落原后继
                    resumeTarget = ContBlockId(point);
                }
                // TaskState 投影是 Task 持有者的可观察性；plain resume
                // 无 Task 可投影（对齐 VM：无 TaskObject 的协程不投影）
                if (taskFieldSymbol != null)
                {
                    EmitRestoreMark(context, restoreInsts, frameOp,
                        taskFieldSymbol, taskTypeRef!, Fresh);
                }
                resumeFn.AddBlock(new MirBlock("mw.state." + point.State, restoreInsts,
                    new MirBranch(resumeTarget)));
            }

            // 原体块迁入 + 挂起点改写 + MirRet 出口改写
            var pointsByBlock = points.GroupBy(p => p.Block)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.InstIndex).ToList());
            foreach (var block in fn.Blocks)
            {
                if (!pointsByBlock.TryGetValue(block, out var blockPoints))
                {
                    RewriteReturn(context, mir, resumeFn, block, plan, frameOp, syms);
                    resumeFn.AddBlock(block);
                    continue;
                }
                var segmentStart = 0;
                var currentId = block.Id;
                foreach (var point in blockPoints)
                {
                    var headInsts = block.Instructions
                        .Take(point.InstIndex).Skip(segmentStart).ToList();
                    if (point.CallSite != null)
                    {
                        EmitCallSplit(context, resumeFn, fn, point, currentId,
                            headInsts, frameOp, stateFieldSymbol, taskFieldSymbol,
                            taskTypeRef, syms, EmitSave, I32Const, Fresh);
                    }
                    else if (point.InitSite != null)
                    {
                        EmitInitSplit(context, mir, resumeFn, fn, point, currentId,
                            headInsts, frameOp, stateFieldSymbol, taskFieldSymbol,
                            taskTypeRef, syms, EmitSave, I32Const, Fresh);
                    }
                    else if (point.Virtual != null)
                    {
                        EmitVirtualCallSplit(context, resumeFn, fn, point, currentId,
                            headInsts, frameOp, stateFieldSymbol, taskFieldSymbol,
                            taskTypeRef, syms, EmitSave, I32Const, Fresh);
                    }
                    else if (point.IndirectInit != null)
                    {
                        EmitNewIndirectSplit(context, mir, resumeFn, fn, point,
                            currentId, headInsts, frameOp, stateFieldSymbol,
                            taskFieldSymbol, taskTypeRef, syms, EmitSave, I32Const,
                            Fresh);
                    }
                    else if (point.Inst is MirAwait awaitInst)
                    {
                        EmitAwaitSplit(context, resumeFn, fn, point, awaitInst,
                            currentId, headInsts, frameOp, stateFieldSymbol,
                            taskFieldSymbol, taskTypeRef,
                            syms, EmitSave, I32Const, Fresh,
                            ResumeRet);
                    }
                    else if (point.Inst is MirYieldAlarm yieldAlarm)
                    {
                        EmitYieldAlarmSplit(context, resumeFn, point, yieldAlarm,
                            currentId, headInsts, frameOp, stateFieldSymbol,
                            taskFieldSymbol, taskTypeRef,
                            EmitSave, ResumeRet, syms, Fresh);
                    }
                    else if (point.Inst is MirCall enterCall && IsMutexEnter(context, enterCall))
                    {
                        EmitMutexEnterSplit(context, resumeFn, point, enterCall,
                            currentId, headInsts, frameOp, stateFieldSymbol,
                            taskFieldSymbol, taskTypeRef, syms, EmitSave,
                            I32Const, Fresh, ResumeRet);
                    }
                    else
                    {
                        EmitYieldSplit(context, resumeFn, point, currentId, headInsts,
                            frameOp, stateFieldSymbol, EmitSave, ResumeRet, syms, Fresh);
                    }
                    segmentStart = point.InstIndex + 1;
                    currentId = ContBlockId(point);
                }
                // 尾段：挂起点之后的剩余指令 + 原终结符
                var tailInsts = block.Instructions.Skip(segmentStart).ToList();
                var tail = new MirBlock(currentId, tailInsts, block.Terminator);
                RewriteReturn(context, mir, resumeFn, tail, plan, frameOp, syms);
                resumeFn.AddBlock(tail);
            }
            return resumeFn;
        }

        private static string WaitBlockId(SuspensionPoint point) =>
            point.Block.Id + ".wait" + point.State;

        private static string ContBlockId(SuspensionPoint point) =>
            point.Block.Id + ".cont" + point.State;

        private static string CallBlockId(SuspensionPoint point) =>
            point.Block.Id + ".call" + point.State;

        private static string VirtualResumeDispatchId(SuspensionPoint point) =>
            point.Block.Id + ".call" + point.State + ".vrdisp";

        private static string IndirectInitResumeDispatchId(SuspensionPoint point) =>
            point.Block.Id + ".call" + point.State + ".nrdisp";

        private static string PollGateBlockId(SuspensionPoint point) =>
            point.Block.Id + ".pollgate" + point.State;

        // Phase 2.6：探测挂起子状态的恢复分流链块（type.is 重判直落对应
        // 实现的探测调用块）
        private static string PollProbeResumeDispatchId(SuspensionPoint point) =>
            PollGateBlockId(point) + ".pdisp";

        // Phase 2.6：探测站点臂块 id（首入建帧 / 双入口调用块 / 就绪判定）
        private static string ProbeNewBlockId(string gateId, int arm) =>
            gateId + ".pnew" + arm;

        private static string ProbeCallBlockId(string gateId, int arm) =>
            gateId + ".pcall" + arm;

        private static string ProbeDoneBlockId(string gateId, int arm) =>
            gateId + ".pdone" + arm;

    }
}
