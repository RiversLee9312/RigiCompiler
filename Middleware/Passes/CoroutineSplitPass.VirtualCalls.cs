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
        // VirtualCalls 职责；与主文件共享同一类型、字段及生命周期。

        // B-2 虚/interface/class 运算符派发挂起点改写（调用点动态分
        // 流；对齐 VM 帧栈模型——运行期目标是谁，挂起/恢复语义就与
        // 直调该目标完全一致）：
        //   head（原位置）：存活跃槽（含接收者与各 callee 槽）+
        //     state=N（先于下钻）→ 落首入分流链；
        //   首入分流链（vdisp）：闭包全类臂最深派生优先 type.is 判
        //     ——tainted 实现臂 → 建该实现 frame + 落参 → 调用块下
        //     钻；非 tainted 实现臂 → 原调用块（普通虚派发）；默认
        //     臂（闭包外/null 接收者）→ 原调用块（NRE 语义保持）；
        //   调用块（首入与恢复共用，每实现一块）：MirResumeCall 下
        //     钻 → 四码分流（0/1 上传 / 2 读该实现 frame.$mw.result
        //     续行 / 3 取 pending 沿原 ExcTarget 重抛）；
        //   恢复分流链（vrdisp，state N 恢复块落点）：同序 type.is
        //     重判直落调用块（frame 不重建——首入已建）；默认臂防御
        //     不可达（挂起前提即首入命中 tainted 臂）。
        // callee frame 所有权归本层（各 callee 槽持 +1；resume 借用
        // 约定不做最终 release）
        private void EmitVirtualCallSplit(MwContext context, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh)
        {
            var site = point.Virtual!;
            var callId = CallBlockId(point);
            var dispatchId = callId + ".vdisp";
            var defaultId = callId + ".vdflt";
            var propSuspendId = callId + ".vprops";
            var propYieldId = callId + ".vpropy";
            var failId = callId + ".vfail";
            var receiverOp = new MirLocalOperand(site.ReceiverLocal);

            emitSave(headInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            headInsts.Add(i32Const(point.State, stateConst));
            headInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            resumeFn.AddBlock(new MirBlock(headId, headInsts,
                new MirBranch(dispatchId)));

            // 每 tainted 实现的协议块 id（臂序即 site.Arms 序——最深
            // 派生优先；同实现多类共用一个协议块组）
            var implBlockIds = new Dictionary<CallSiteInfo, (string New, string Call,
                string Done)>();
            var nextImpl = 0;
            foreach (var arm in site.Arms)
            {
                if (arm.Impl != null && !implBlockIds.ContainsKey(arm.Impl))
                {
                    implBlockIds.Add(arm.Impl, (callId + ".vnew" + nextImpl,
                        callId + ".vcall" + nextImpl, callId + ".vdone" + nextImpl));
                    nextImpl++;
                }
            }

            // 分流链（首入/恢复共用生成器；resume=true 时 tainted 臂
            // 直落调用块、默认臂防御不可达）。R2-a：单臂多 type.is
            // 目标（泛型类 = 模板空壳 + 各闭合构造 sheet）——同臂内
            // 逐目标 OR，任一命中即进臂
            void EmitDispatchChain(string chainId, string checkPrefix, bool resume)
            {
                for (var k = 0; k < site.Arms.Count; k++)
                {
                    var arm = site.Arms[k];
                    var checkId = k == 0 ? chainId : checkPrefix + (k - 1) + ".next";
                    string hitId;
                    string missId;
                    if (arm.Impl != null)
                    {
                        hitId = resume
                            ? implBlockIds[arm.Impl].Call
                            : implBlockIds[arm.Impl].New;
                    }
                    else
                    {
                        // 恢复链上的非 tainted 臂不可达（挂起前提即首
                        // 入命中 tainted 臂，接收者类型不变）——防御
                        hitId = resume ? "mw.state.bad" : defaultId;
                    }
                    missId = k + 1 < site.Arms.Count
                        ? checkPrefix + k + ".next"
                        : (resume ? "mw.state.bad" : defaultId);
                    for (var t = 0; t < arm.TypeRefs.Count; t++)
                    {
                        var cond = fresh("$mw.vchk.", Bool);
                        var isLast = t + 1 >= arm.TypeRefs.Count;
                        var thisCheckId = t == 0 ? checkId : checkId + ".t" + t;
                        resumeFn.AddBlock(new MirBlock(thisCheckId, new List<MirInst>
                        {
                            new MirTypeCheck(MirTypeCheckKind.Is, receiverOp,
                                arm.TypeRefs[t], null, cond),
                        }, new MirCondBranch(new MirLocalOperand(cond), hitId,
                            isLast ? missId : checkId + ".t" + (t + 1))));
                    }
                }
            }
            EmitDispatchChain(dispatchId, callId + ".vc", resume: false);
            EmitDispatchChain(VirtualResumeDispatchId(point), callId + ".vrc",
                resume: true);

            // 默认臂/非 tainted 臂：原调用直落续行（vtable/iMap 动态
            // 派发——运行期目标必非 tainted，否则必中上方臂）
            resumeFn.AddBlock(new MirBlock(defaultId,
                new List<MirInst> { site.OriginalCall },
                new MirBranch(ContBlockId(point))));

            // 每 tainted 实现：建 frame + 落参（首入）→ 调用块四码
            foreach (var (impl, ids) in implBlockIds)
            {
                var calleeOp = new MirLocalOperand(impl.CalleeLocal);
                var frameType = context.Symbols.FindType(impl.CalleeFrameCanonical)
                    ?? throw new CompilerInternalException(
                        "tainted 虚实现 frame 未预注册: " + impl.CalleeFrameCanonical);
                var frameInit = context.Symbols.FindMember(
                    SyntheticTypePlanner.FrameInitCanonicalOf(impl.CalleeFrameCanonical))
                    ?? throw new CompilerInternalException(
                        "tainted 虚实现 frame init 缺失: " + impl.CalleeFrameCanonical);
                var newInsts = new List<MirInst>
                {
                    new MirNewObject(frameType, null, frameInit,
                        new List<MirOperand>(), impl.CalleeLocal),
                };
                foreach (var drop in impl.Drops)
                {
                    if (drop.Operand != null)
                    {
                        var operand = drop.Operand;
                        if (operand is MirLocalOperand local)
                        {
                            var sourceType = resumeFn.FindLocal(local.Name).Type;
                            var targetType = drop.OperandTargetType ?? sourceType;
                            if (sourceType.Canonical != targetType.Canonical
                                && (TypeLayout.IsGenericPlaceholder(sourceType)
                                    || TypeLayout.IsGenericPlaceholder(targetType)))
                            {
                                // 协议臂写具体 callee frame 前显式编组；不能把胖值直接写进标量字段。
                                // ABI 同构白名单（typefix）：.typeid/.fieldid 与
                                // FatReference 一样是「值即引用」形态——Type<X> 在
                                // 调用点（闭合 .typeid<X>）与共享体（开放
                                // .typeid<.generic<T>>）两侧都是 8B 裸 sheet 指针，
                                // 恒等拷贝即正确。早前按「非 FatReference ⇒ 装箱」
                                // 把 Type<X> 装箱成 16B Any 胖值，frame 字段只有
                                // 8B，StoreAt 裸 store 只搬胖值第 0 字段（视图
                                // sheet core::Type<X>），被调侧 typeNameOf 的
                                // typeid 装箱 toString 遂输出 "core::Type<X>"
                                //（readAs Type<T> 值形态 native 缺陷）。
                                var converted = fresh("$mw.call.arg.", targetType);
                                newInsts.Add(TypeLayout.IsGenericPlaceholder(targetType)
                                    ? TypeLayout.IsTypeId(sourceType)
                                        || TypeLayout.IsFieldId(sourceType)
                                        || TypeLayout.ClassifySlot(context.Layout, sourceType) == ManagedSlotKind.FatReference
                                        ? new MirCopyLocal(operand, converted)
                                        : new MirBoxAny(operand, converted)
                                    : new MirCast(operand, converted, false, targetType.Canonical, null, site.ExcTarget));
                                operand = new MirLocalOperand(converted);
                            }
                        }
                        newInsts.Add(new MirSetField(operand, calleeOp,
                            drop.FrameFieldSymbol));
                    }
                    else if (drop.TypeIdTypeRef != null)
                    {
                        var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                        newInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                        newInsts.Add(new MirSetField(new MirLocalOperand(tid),
                            calleeOp, drop.FrameFieldSymbol));
                    }
                    else if (drop.ReceiverTypeIdOwner != null)
                    {
                        // R2-a：类级 typeid 运行期取自接收者实例隐
                        // 藏字段（首入块执行，接收者槽仍活跃）
                        var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                        newInsts.Add(new MirGetClassTypeArgument(drop.ReceiverTypeIdOperand ?? receiverOp,
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
                // callee 槽回存本层 frame：head 的 emitSave 在分流/建
                // frame 之前执行（槽位尚为 null），恢复块从本层 frame
                // 恢复 callee 槽下钻——必须在此把新建 frame 落进保存槽
                var callerFrameCanonical = resumeFn.FindLocal(FrameParamName)
                    .Type.Canonical;
                newInsts.Add(new MirSetField(calleeOp, frameOp,
                    SyntheticTypePlanner.FrameFieldSymbol(callerFrameCanonical,
                        impl.CalleeLocal, impl.CalleeFrameCanonical)));
                resumeFn.AddBlock(new MirBlock(ids.New, newInsts,
                    new MirBranch(ids.Call)));

                var code = fresh("$mw.call.code.", I32);
                var codeTable = new BilSwitchTableResource(
                    "$mw.coroutine.call." + _resourceCounter++, ".i32",
                    new[] { "0", "1", "2", "3" });
                context.Module.Resources.Add(codeTable);
                resumeFn.AddBlock(new MirBlock(ids.Call, new List<MirInst>
                {
                    new MirResumeCall(impl.ResumeSymbol, impl.CalleeLocal, code),
                }, new MirSwitch(new MirLocalOperand(code), codeTable,
                    new[] { propSuspendId, propYieldId, ids.Done, failId },
                    "mw.state.bad")));

                // DONE：借用 callee frame 读 $mw.result 续行
                var doneInsts = new List<MirInst>();
                if (site.Result != null)
                {
                    if (impl.ResultFieldSymbol == null)
                    {
                        throw new CompilerInternalException(
                            "tainted 虚调用有结果槽但实现无 $mw.result: "
                            + impl.Callee.Symbol.Canonical);
                    }
                    var sourceType = impl.Callee.ReturnType;
                    var targetType = resumeFn.FindLocal(site.Result).Type;
                    if (sourceType.Canonical != targetType.Canonical
                        && (TypeLayout.IsGenericPlaceholder(sourceType)
                            || TypeLayout.IsGenericPlaceholder(targetType)))
                    {
                        var raw = fresh("$mw.call.result.", sourceType);
                        doneInsts.Add(new MirGetField(calleeOp, impl.ResultFieldSymbol, raw));
                        // ABI 同构白名单（typefix，与实参方向同口径）：
                        // 被调返回闭合 Type<X>/FieldId（8B 裸指针）写入调用点
                        // 开放 Type<T> 结果槽（同为 8B 裸指针）时恒等拷贝；
                        // 装箱会把视图 sheet 截进结果槽，下游 toString/is
                        // 全部拿到 "core::Type<X>" 包装器身份。
                        doneInsts.Add(TypeLayout.IsGenericPlaceholder(targetType)
                            ? TypeLayout.IsTypeId(sourceType)
                                || TypeLayout.IsFieldId(sourceType)
                                ? new MirCopyLocal(new MirLocalOperand(raw), site.Result)
                                : new MirBoxAny(new MirLocalOperand(raw), site.Result)
                            : new MirCast(new MirLocalOperand(raw), site.Result, false,
                                targetType.Canonical, null, site.ExcTarget));
                    }
                    else doneInsts.Add(new MirGetField(calleeOp, impl.ResultFieldSymbol,
                        site.Result));
                }
                resumeFn.AddBlock(new MirBlock(ids.Done, doneInsts,
                    new MirBranch(ContBlockId(point))));
            }

            // SUSPENDED/YIELDED 上传（各实现调用块共用两块——上传码
            // 是常量，无需引用各调用块的 code 局部）：frame 已写完，
            // 直返常量码（Tasked 调用方先投影自身 Task Suspended）
            void EmitPropBlock(string blockId, int resumeCode)
            {
                var propInsts = new List<MirInst>();
                if (taskFieldSymbol != null)
                {
                    EmitTaskMark(context, propInsts, frameOp, taskFieldSymbol,
                        taskTypeRef!, "markSuspended", fresh);
                }
                var propCode = fresh("$mw.code.", I32);
                propInsts.Add(i32Const(resumeCode, propCode));
                resumeFn.AddBlock(new MirBlock(blockId, propInsts,
                    new MirRet(new MirLocalOperand(propCode))));
            }
            EmitPropBlock(propSuspendId, ResumeSuspended);
            EmitPropBlock(propYieldId, ResumeYielded);

            // FAILED（共用）：pending 已在 TLS（callee plain 垫尾保持
            // 置位），取走重抛沿原 ExcTarget
            var exc = fresh("$mw.call.exc.", Any);
            var failInsts = new List<MirInst>
            {
                new MirTakePending(exc),
                new MirThrow(new MirLocalOperand(exc), site.ExcTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                site.ExcTarget != null
                    ? (MirTerminator)new MirBranch(site.ExcTarget.Id)
                    : new MirRetThrow()));
        }

    }
}
