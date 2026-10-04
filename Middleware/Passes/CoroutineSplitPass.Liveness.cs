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
        // Liveness 职责；与主文件共享同一类型、字段及生命周期。

        private List<SuspensionPoint> CollectSuspensionPoints(MwContext context,
            MirFunction fn, HashSet<string> tainted)
        {
            var points = new List<SuspensionPoint>();
            foreach (var block in fn.Blocks)
            {
                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    var inst = block.Instructions[i];
                    // B-1：直调 tainted fn 的调用点同为挂起点（callee
                    // 挂起沿链上传，本 fn 须在此 state 恢复下钻）；
                    // B-2：super 调用与直调形态运算符同为静态唯一目
                    // 标收编；虚/interface/class 运算符派发点闭包内
                    // 任一实现 tainted 则整点升级（动态分流协议）；
                    // Mutex.enter 优先判（其目标永不 tainted）
                    var isPoint = inst is MirAwait or MirYieldBare or MirYieldAlarm
                        || IsMutexEnter(context, inst)
                        || (inst is MirCall call && !call.OperatorDispatch
                            && tainted.Contains(call.Target.Canonical)
                            && Binding.ImplBinder.BindCall(call.Target)
                                is Binding.DirectCallBinding)
                        || (inst is MirCall operatorCall && operatorCall.OperatorDispatch
                            && tainted.Contains(operatorCall.Target.Canonical)
                            && Binding.ImplBinder.BindOperatorCall(operatorCall.Target)
                                is Binding.DirectCallBinding)
                        || (inst is MirSuperCall superCall
                            && tainted.Contains(superCall.Target.Canonical))
                        || (inst is MirNewObject newObject
                            && newObject.Init != null
                            && tainted.Contains(newObject.Init.Canonical));
                    var point = isPoint
                        ? new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = inst,
                        }
                        : null;
                    if (point == null && inst is MirCall dispatchCall
                        && !IsMutexEnter(context, dispatchCall)
                        && IsVirtualDispatchSite(dispatchCall)
                        && ClosurePairsOf(context, dispatchCall.Target,
                            dispatchCall.OperatorDispatch)
                            .Any(p => tainted.Contains(p.ImplCanonical)))
                    {
                        point = new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = inst,
                            Virtual = new VirtualSiteInfo(),
                        };
                    }
                    // R2-b：invoke.indirect 挂起点——$$call 闭包内任
                    // 一实现 tainted 则整点升级（接收者 = CallTarget，
                    // 虚派发臂协议同 MirCall 形态）
                    if (point == null && inst is MirInvokeIndirect invoke
                        && ClosurePairsOf(context,
                            IndirectCallOperatorOf(context, fn, invoke),
                            operatorDispatch: false)
                            .Any(p => tainted.Contains(p.ImplCanonical)))
                    {
                        point = new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = inst,
                            Virtual = new VirtualSiteInfo(),
                        };
                    }
                    // R2-c：new.indirect 挂起点——模块内 tainted
                    // class init 与站点静态实参形精确匹配则整点升
                    // 级（精确 sheet 臂协议）
                    if (point == null && inst is MirNewIndirect newIndirect
                        && IndirectInitRelevant(context, fn, newIndirect, tainted))
                    {
                        point = new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = inst,
                            IndirectInit = new IndirectInitSiteInfo(),
                        };
                    }
                    if (point != null)
                    {
                        points.Add(point);
                    }
                }
            }
            return points;
        }

        // 反向数据流活性分析：CFG 边 = 终结符边 + 全部可抛指令的
        // ExcTarget 异常边（try 派发垫/逃逸垫里的清理状态同样跨挂起，
        // 漏边会把 finally/catch 所需槽漏出 frame）
        private static void AnalyzeLiveness(MwContext context, MirFunction fn, List<SuspensionPoint> points)
        {
            var successors = new Dictionary<MirBlock, List<MirBlock>>();
            var byId = new Dictionary<string, MirBlock>(System.StringComparer.Ordinal);
            foreach (var block in fn.Blocks)
            {
                byId[block.Id] = block;
            }
            foreach (var block in fn.Blocks)
            {
                var edges = new List<MirBlock>();
                void AddEdge(string? targetId)
                {
                    if (targetId != null && byId.TryGetValue(targetId, out var target)
                        && !edges.Contains(target))
                    {
                        edges.Add(target);
                    }
                }
                switch (block.Terminator)
                {
                    case MirBranch branch:
                        AddEdge(branch.Target);
                        break;
                    case MirCondBranch cond:
                        AddEdge(cond.ThenTarget);
                        AddEdge(cond.ElseTarget);
                        break;
                    case MirSwitch sw:
                        foreach (var target in sw.ItemTargets)
                        {
                            AddEdge(target);
                        }
                        AddEdge(sw.DefaultTarget);
                        break;
                }
                foreach (var inst in block.Instructions)
                {
                    if (ExcTargetOf(inst) is { } exc)
                    {
                        AddEdge(exc.Id);
                    }
                }
                successors[block] = edges;
            }

            // 逐块 def/use 前缀（反向扫描用）；块内逐指令定点计算
            var liveIn = new Dictionary<MirBlock, HashSet<string>>();
            foreach (var block in fn.Blocks)
            {
                liveIn[block] = new HashSet<string>(System.StringComparer.Ordinal);
            }
            var pointByBlock = new Dictionary<MirBlock, List<SuspensionPoint>>();
            foreach (var point in points)
            {
                if (!pointByBlock.TryGetValue(point.Block, out var list))
                {
                    list = new List<SuspensionPoint>();
                    pointByBlock[point.Block] = list;
                }
                list.Add(point);
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var bi = fn.Blocks.Count - 1; bi >= 0; bi--)
                {
                    var block = fn.Blocks[bi];
                    var live = new HashSet<string>(System.StringComparer.Ordinal);
                    foreach (var succ in successors[block])
                    {
                        live.UnionWith(liveIn[succ]);
                    }
                    AddTerminatorUses(block.Terminator, live);
                    // 块内反向扫：挂起点先记录 live-after（指令之后的活跃
                    // 集），再按 def/use 回推
                    for (var i = block.Instructions.Count - 1; i >= 0; i--)
                    {
                        var inst = block.Instructions[i];
                        if (pointByBlock.TryGetValue(block, out var blockPoints))
                        {
                            foreach (var point in blockPoints)
                            {
                                if (point.InstIndex == i)
                                {
                                    point.LiveAfter = fn.Locals
                                        // new/cast 等在发射期隐式读取 typeid，MIR
                                        // 显式操作数的 def/use 看不到这些依赖。
                                        .Where(l => live.Contains(l.Name)
                                            || l.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                                        .Select(l => l.Name).ToList();
                                }
                            }
                        }
                        ApplyDefUse(inst, live);
                    }
                    if (!live.SetEquals(liveIn[block]))
                    {
                        liveIn[block] = live;
                        changed = true;
                    }
                }
            }
            foreach (var point in points)
            {
                // await 的 task 槽强制活跃：恢复块重回 wait 块重新登记/
                // 快路径（task 从 frame 恢复）
                if (point.Inst is MirAwait awaitInst
                    && !point.LiveAfter.Contains(awaitInst.TaskSlot))
                {
                    point.LiveAfter.Add(awaitInst.TaskSlot);
                }
                // 棒5a：yield-alarm 的 alarm 槽恒活跃——PollingAlarm
                // 恢复块要先经 $mw.poll_probe 探测（alarm 从 frame 恢复）
                if (point.Inst is MirYieldAlarm yieldAlarm
                    && !point.LiveAfter.Contains(yieldAlarm.AlarmSlot))
                {
                    point.LiveAfter.Add(yieldAlarm.AlarmSlot);
                }
                // Mutex.enter 的 receiver 恒活跃：恢复后 acquire 续行读
                // this.gate 构造 Lock
                if (point.Inst is MirCall enterCall && IsMutexEnter(context, enterCall)
                    && enterCall.Args.Count > 0
                    && enterCall.Args[0] is MirLocalOperand mutexThis
                    && !point.LiveAfter.Contains(mutexThis.Name))
                {
                    point.LiveAfter.Add(mutexThis.Name);
                }
                // B-1：tainted 调用点的 callee frame 槽恒活跃——恢复块
                // 重回调用块经 MirResumeCall 再下钻（frame 从调用方
                // frame 的 callee 槽恢复）
                if (point.CallSite != null
                    && !point.LiveAfter.Contains(point.CallSite.CalleeLocal))
                {
                    point.LiveAfter.Add(point.CallSite.CalleeLocal);
                }
                // B-2：init 构造点的 init frame 槽恒活跃（同直调口径）
                if (point.InitSite != null
                    && !point.LiveAfter.Contains(point.InitSite.Site.CalleeLocal))
                {
                    point.LiveAfter.Add(point.InitSite.Site.CalleeLocal);
                }
                // B-2：虚派发点的接收者（恢复分流链复读）与各 tainted
                // 实现的 callee frame 槽恒活跃
                if (point.Virtual != null)
                {
                    if (!point.LiveAfter.Contains(point.Virtual.ReceiverLocal))
                    {
                        point.LiveAfter.Add(point.Virtual.ReceiverLocal);
                    }
                    foreach (var arm in point.Virtual.Arms)
                    {
                        if (arm.Impl != null
                            && !point.LiveAfter.Contains(arm.Impl.CalleeLocal))
                        {
                            point.LiveAfter.Add(arm.Impl.CalleeLocal);
                        }
                    }
                }
                // R2-c：new.indirect 点的 typeid 槽（恢复分流链复
                // 读）、Target 槽（臂内分配落点 + 回存）与各臂
                // callee frame 槽恒活跃
                if (point.IndirectInit != null)
                {
                    if (!point.LiveAfter.Contains(point.IndirectInit.TypeIdLocal))
                    {
                        point.LiveAfter.Add(point.IndirectInit.TypeIdLocal);
                    }
                    if (!point.LiveAfter.Contains(point.IndirectInit.TargetLocal))
                    {
                        point.LiveAfter.Add(point.IndirectInit.TargetLocal);
                    }
                    foreach (var arm in point.IndirectInit.Arms)
                    {
                        if (!point.LiveAfter.Contains(arm.Impl.CalleeLocal))
                        {
                            point.LiveAfter.Add(arm.Impl.CalleeLocal);
                        }
                    }
                }
                // Phase 2.6：探测站点各 tainted isReady 实现的 callee
                // frame 槽恒活跃——探测挂起后恢复块重回探测调用块再下钻
                //（frame 从本层 frame 的探测槽恢复）；alarm 槽已由上方
                // yield-alarm 段保证
                if (point.ProbeSite != null)
                {
                    foreach (var arm in point.ProbeSite.Arms)
                    {
                        if (!point.LiveAfter.Contains(arm.Impl.CalleeLocal))
                        {
                            point.LiveAfter.Add(arm.Impl.CalleeLocal);
                        }
                    }
                }
            }
        }

        internal static bool IsMutexEnter(MwContext context, MirInst inst) =>
            inst is MirCall call
            && context.CompilerMember(MutexEnterPrefix)?.Canonical == call.Target.Canonical;

        internal static MwMemberSymbol MutexFn(MwContext context, string name) =>
            context.CompilerMember(MutexCanonical + "$" + name + "(")
            ?? throw new CompilerInternalException("stdlib 缺少 Mutex 通道: " + name);

        private static MirBlock? ExcTargetOf(MirInst inst) => inst switch
        {
            MirCall call => call.ExcTarget,
            MirSuperCall superCall => superCall.ExcTarget,
            MirInvokeIndirect invoke => invoke.ExcTarget,
            MirInnerCall inner => inner.ExcTarget,
            MirThrow throwInst => throwInst.ExcTarget,
            MirBinaryIntrinsic binary => binary.ExcTarget,
            MirCast cast => cast.ExcTarget,
            MirUnboxAny unbox => unbox.ExcTarget,
            MirSetArray setArray => setArray.ExcTarget,
            MirNewIndirect newIndirect => newIndirect.ExcTarget,
            MirNewObject newObject => newObject.ExcTarget,
            MirNewValue newValue => newValue.ExcTarget,
            MirGetField getField => getField.ExcTarget,
            MirAwait awaitInst => awaitInst.ExcTarget,
            MirYieldAlarm yieldAlarm => yieldAlarm.ExcTarget,
            _ => null,
        };

        private static void AddOperandUse(MirOperand? operand, HashSet<string> into)
        {
            if (operand is MirLocalOperand local)
            {
                into.Add(local.Name);
            }
        }

        private static void AddOperandUses(IEnumerable<MirOperand> operands, HashSet<string> into)
        {
            foreach (var operand in operands)
            {
                AddOperandUse(operand, into);
            }
        }

        private static void AddTerminatorUses(MirTerminator terminator, HashSet<string> into)
        {
            switch (terminator)
            {
                case MirRet ret:
                    AddOperandUse(ret.Value, into);
                    break;
                case MirCondBranch cond:
                    AddOperandUse(cond.Condition, into);
                    break;
                case MirSwitch sw:
                    AddOperandUse(sw.Selector, into);
                    break;
            }
        }

        // 单指令 def/use 回推：live = (live − def) ∪ use
        private static void ApplyDefUse(MirInst inst, HashSet<string> live)
        {
            if (DefOf(inst) is { } def)
            {
                live.Remove(def);
            }
            switch (inst)
            {
                case MirCopyLocal copy:
                    AddOperandUse(copy.Source, live);
                    break;
                case MirBinaryIntrinsic binary:
                    AddOperandUse(binary.Left, live);
                    AddOperandUse(binary.Right, live);
                    break;
                case MirUnaryIntrinsic unary:
                    AddOperandUse(unary.Operand, live);
                    break;
                // G4：占位运算符的运行期派发节点（操作数按借用计）
                case MirGenericBinaryOp genericBinary:
                    AddOperandUse(genericBinary.Left, live);
                    AddOperandUse(genericBinary.Right, live);
                    break;
                case MirGenericUnaryOp genericUnary:
                    AddOperandUse(genericUnary.Operand, live);
                    break;
                case MirCall call:
                    AddOperandUses(call.Args, live);
                    break;
                case MirSuperCall superCall:
                    AddOperandUses(superCall.Args, live);
                    break;
                case MirInnerCall inner:
                    AddOperandUses(inner.Args, live);
                    break;
                case MirInvokeIndirect invoke:
                    AddOperandUse(invoke.CallTarget, live);
                    AddOperandUses(invoke.Args, live);
                    break;
                case MirNewObject newObject:
                    AddOperandUses(newObject.Args, live);
                    AddOperandUses(newObject.WrapperArgs, live);
                    break;
                case MirNewValue newValue:
                    AddOperandUses(newValue.Args, live);
                    AddOperandUses(newValue.WrapperArgs, live);
                    break;
                case MirNewCase newCase:
                    AddOperandUses(newCase.Args, live);
                    break;
                case MirNewIndirect newIndirect:
                    AddOperandUse(newIndirect.TypeId, live);
                    AddOperandUses(newIndirect.Args, live);
                    break;
                case MirTypeCheck typeCheck:
                    AddOperandUse(typeCheck.Value, live);
                    AddOperandUse(typeCheck.TargetTypeId, live);
                    break;
                case MirIsCase isCase:
                    AddOperandUse(isCase.Value, live);
                    break;
                case MirGetField getField:
                    AddOperandUse(getField.Object, live);
                    break;
                case MirGetWrapper getWrapper:
                    AddOperandUse(getWrapper.Host, live);
                    break;
                case MirGetWrapperField getWrapperField:
                    AddOperandUse(getWrapperField.Host, live);
                    break;
                case MirGetWrapperAddr getWrapperAddr:
                    AddOperandUse(getWrapperAddr.Host, live);
                    break;
                case MirGetWrapperFieldAddr getWrapperFieldAddr:
                    AddOperandUse(getWrapperFieldAddr.Host, live);
                    break;
                case MirGetWrapperMethodAddr getWrapperMethodAddr:
                    AddOperandUse(getWrapperMethodAddr.Host, live);
                    break;
                case MirSetWrapperField setWrapperField:
                    AddOperandUse(setWrapperField.Source, live);
                    AddOperandUse(setWrapperField.Host, live);
                    break;
                case MirNewWrapper newWrapper:
                    AddOperandUse(newWrapper.Host, live);
                    AddOperandUses(newWrapper.Args, live);
                    break;
                case MirSetStatic setStatic:
                    AddOperandUse(setStatic.Source, live);
                    break;
                case MirSetField setField:
                    AddOperandUse(setField.Source, live);
                    AddOperandUse(setField.Object, live);
                    break;
                case MirGetArray getArray:
                    AddOperandUse(getArray.Collection, live);
                    AddOperandUse(getArray.Index, live);
                    break;
                case MirSetArray setArray:
                    AddOperandUse(setArray.Collection, live);
                    AddOperandUse(setArray.Index, live);
                    AddOperandUse(setArray.Element, live);
                    break;
                case MirNewArray newArray:
                    AddOperandUses(newArray.Elements, live);
                    break;
                case MirGetTypeIdVar getTypeIdVar:
                    AddOperandUse(getTypeIdVar.Value, live);
                    break;
                case MirGetClassTypeArgument argument:
                    AddOperandUse(argument.Receiver, live);
                    break;
                case MirWrapNullable wrap:
                    AddOperandUse(wrap.Source, live);
                    break;
                case MirUnwrapNullable unwrap:
                    AddOperandUse(unwrap.Source, live);
                    break;
                case MirBoxAny box:
                    AddOperandUse(box.Source, live);
                    break;
                case MirUnboxAny unbox:
                    AddOperandUse(unbox.Source, live);
                    break;
                case MirCast cast:
                    AddOperandUse(cast.Source, live);
                    AddOperandUse(cast.TargetTypeId, live);
                    break;
                case MirThrow throwInst:
                    AddOperandUse(throwInst.Exception, live);
                    break;
                case MirAwait awaitInst:
                    AddOperandUse(new MirLocalOperand(awaitInst.TaskSlot), live);
                    break;
                case MirYieldAlarm yieldAlarm:
                    AddOperandUse(new MirLocalOperand(yieldAlarm.AlarmSlot), live);
                    break;
                // 棒5a：create 借用 frame（move 语义归 RcInjection）
                case MirCoroutineCreate create:
                    AddOperandUse(new MirLocalOperand(create.FrameSlot), live);
                    break;
                // B-1：resume 直调借用 callee frame（所有权归调用方）
                case MirResumeCall resumeCall:
                    AddOperandUse(new MirLocalOperand(resumeCall.FrameSlot), live);
                    break;
                case MirFailureLoad failureLoad:
                    AddOperandUse(new MirLocalOperand(failureLoad.NodeIdSlot), live);
                    break;
            }
        }

        private static string? DefOf(MirInst inst) => inst switch
        {
            MirLoadResource load => load.Target,
            MirCopyLocal copy => copy.Target,
            MirBinaryIntrinsic binary => binary.Target,
            MirUnaryIntrinsic unary => unary.Target,
            MirGenericBinaryOp genericBinary => genericBinary.Target,
            MirGenericUnaryOp genericUnary => genericUnary.Target,
            MirCall call => call.Result,
            MirSuperCall superCall => superCall.Result,
            MirInvokeIndirect invoke => invoke.Result,
            MirInnerCall inner => inner.Result,
            MirNewObject newObject => newObject.Target,
            MirNewValue newValue => newValue.Target,
            MirNewCase newCase => newCase.Target,
            MirNewIndirect newIndirect => newIndirect.Target,
            MirTypeCheck typeCheck => typeCheck.Target,
            MirIsCase isCase => isCase.Target,
            MirGetField getField => getField.Target,
            MirGetWrapper getWrapper => getWrapper.Target,
            MirGetWrapperField getWrapperField => getWrapperField.Target,
            MirGetWrapperAddr getWrapperAddr => getWrapperAddr.Target,
            MirGetWrapperFieldAddr getWrapperFieldAddr => getWrapperFieldAddr.Target,
            MirGetWrapperMethodAddr getWrapperMethodAddr => getWrapperMethodAddr.Target,
            MirGetSelf getSelf => getSelf.Target,
            MirGetStatic getStatic => getStatic.Target,
            MirGetArray getArray => getArray.Target,
            MirNewArray newArray => newArray.Target,
            MirGetTypeId getTypeId => getTypeId.Target,
            MirGetTypeIdVar getTypeIdVar => getTypeIdVar.Target,
            MirGetClassTypeArgument argument => argument.Target,
            MirWrapNullable wrap => wrap.Target,
            MirUnwrapNullable unwrap => unwrap.Target,
            MirBoxAny box => box.Target,
            MirUnboxAny unbox => unbox.Target,
            MirCast cast => cast.Target,
            MirTakePending takePending => takePending.TargetLocal,
            MirAwait awaitInst => awaitInst.ResultSlot,
            MirCoroutineCreate create => create.HandleSlot,
            MirResumeCall resumeCall => resumeCall.CodeSlot,
            MirFailureLoad failureLoad => failureLoad.OutFatSlot,
            _ => null,
        };

    }
}
