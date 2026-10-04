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
        // DirectCalls 职责；与主文件共享同一类型、字段及生命周期。

        // B-1 tainted→tainted 直调改写（调用点是调用方的挂起点；对齐
        // VM 栈式模型——挂起的是整条帧链，恢复沿链逐层下钻）：
        //   head（原位置）：建 callee frame → 实参按形参序落 callee
        //     frame 字段 → 存活跃槽（含 callee 槽）+ state=N（先于下
        //     钻——callee 挂起时本层 frame 必须已可恢复）→ 落调用块；
        //   调用块（首入与恢复共用）：MirResumeCall 原生栈下钻 callee
        //     resume → 四码分流（0=SUSPENDED/1=YIELDED 上传同码 /
        //     2=DONE 读 callee frame.$mw.result 续行 / 3=FAILED 取
        //     pending 沿原 ExcTarget 重抛）。
        // callee frame 所有权归本层（callee 槽持 +1；callee resume 借
        // 用约定不做最终 release）
        private void EmitCallSplit(MwContext context, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh)
        {
            var info = point.CallSite!;
            // B-2：MirCall / MirSuperCall 同协议（静态唯一目标）
            var (result, excTarget) = point.Inst switch
            {
                MirCall call => (call.Result, call.ExcTarget),
                MirSuperCall superCall => (superCall.Result, superCall.ExcTarget),
                _ => throw new CompilerInternalException(
                    "EmitCallSplit 非调用挂起点: " + point.Inst.GetType().Name),
            };
            var callId = CallBlockId(point);
            var doneId = callId + ".done";
            var propId = callId + ".prop";
            var failId = callId + ".fail";
            var calleeOp = new MirLocalOperand(info.CalleeLocal);
            var frameType = context.Symbols.FindType(info.CalleeFrameCanonical)
                ?? throw new CompilerInternalException(
                    "tainted callee frame 未预注册: " + info.CalleeFrameCanonical);
            var frameInit = context.Symbols.FindMember(
                SyntheticTypePlanner.FrameInitCanonicalOf(info.CalleeFrameCanonical))
                ?? throw new CompilerInternalException(
                    "tainted callee frame init 缺失: " + info.CalleeFrameCanonical);

            headInsts.Add(new MirNewObject(frameType, null, frameInit,
                new List<MirOperand>(), info.CalleeLocal));
            // §7.2 落参计划（B-2）：直落实参 / 常量 typeid 合成 /
            // 调用方 .generic.* 局部转抄
            foreach (var drop in info.Drops)
            {
                if (drop.Operand != null)
                {
                    headInsts.Add(new MirSetField(drop.Operand, calleeOp,
                        drop.FrameFieldSymbol));
                }
                else if (drop.TypeIdTypeRef != null)
                {
                    var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                    headInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                    headInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                        drop.FrameFieldSymbol));
                }
                else if (drop.ReceiverTypeIdOwner != null)
                {
                    var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                    headInsts.Add(new MirGetClassTypeArgument(drop.ReceiverTypeIdOperand!,
                        drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                    headInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                        drop.FrameFieldSymbol));
                }
                else
                {
                    headInsts.Add(new MirSetField(
                        new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                        drop.FrameFieldSymbol));
                }
            }
            emitSave(headInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            headInsts.Add(i32Const(point.State, stateConst));
            headInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(callId)));

            var code = fresh("$mw.call.code.", I32);
            var codeTable = new BilSwitchTableResource(
                "$mw.coroutine.call." + _resourceCounter++, ".i32",
                new[] { "0", "1", "2", "3" });
            context.Module.Resources.Add(codeTable);
            resumeFn.AddBlock(new MirBlock(callId, new List<MirInst>
            {
                new MirResumeCall(info.ResumeSymbol, info.CalleeLocal, code),
            }, new MirSwitch(new MirLocalOperand(code), codeTable,
                new[] { propId, propId, doneId, failId }, "mw.state.bad")));

            // DONE：借用 callee frame 读 $mw.result 续行
            var doneInsts = new List<MirInst>();
            if (result != null)
            {
                if (info.ResultFieldSymbol == null)
                {
                    throw new CompilerInternalException(
                        "tainted 调用有结果槽但 callee 无 $mw.result: "
                        + info.Callee.Symbol.Canonical);
                }
                doneInsts.Add(new MirGetField(calleeOp, info.ResultFieldSymbol,
                    result));
            }
            resumeFn.AddBlock(new MirBlock(doneId, doneInsts,
                new MirBranch(ContBlockId(point))));

            // SUSPENDED/YIELDED 上传：frame 已写完，直返同码（Tasked
            // 调用方先投影自身 Task Suspended——VM 无 TaskObject 的
            // 协程不投影，plain 调用方同跳过）
            var propInsts = new List<MirInst>();
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, propInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
            resumeFn.AddBlock(new MirBlock(propId, propInsts,
                new MirRet(new MirLocalOperand(code))));

            // FAILED：pending 已在 TLS（callee plain 垫尾保持置位），
            // 取走重抛沿原 ExcTarget（对齐普通调用抛出语义；null 由
            // RcInjection 进本 fn 传播垫继续上传/吸收）
            var exc = fresh("$mw.call.exc.", Any);
            var failInsts = new List<MirInst>
            {
                new MirTakePending(exc),
                new MirThrow(new MirLocalOperand(exc), excTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                excTarget != null
                    ? (MirTerminator)new MirBranch(excTarget.Id)
                    : new MirRetThrow()));
        }

        // B-2 含挂起点 init 的构造改写（对齐 VM New 语义——分配 →
        // init.wrapper 缝合 → init 调用；init 挂起 = 整条帧链挂起）：
        //   head（原位置）：MirNewObject 分配（init.wrapper 原位缝合
        //     + 合成空 init——真 init 不在此跑）→ Target 槽落定 →
        //     建 init frame（.this = Target）+ 落参 → 存活跃槽 +
        //     state=N → 落调用块；
        //   调用块（首入与恢复共用）：MirResumeCall 下钻 init resume
        //     → 四码分流（0/1 上传 / 2 DONE 直落原后继——结果即
        //     Target 槽本身，无 $mw.result 可读 / 3 取 pending 沿原
        //     MirNewObject.ExcTarget 重抛——半构造对象随本层托管槽
        //     配平释放，对齐「init 内抛出释放新建实例」口径）。
        private void EmitInitSplit(MwContext context, MirModule mir, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh)
        {
            var initSite = point.InitSite!;
            var info = initSite.Site;
            var original = initSite.Original;
            var callId = CallBlockId(point);
            var doneId = callId + ".done";
            var propId = callId + ".prop";
            var failId = callId + ".fail";
            var calleeOp = new MirLocalOperand(info.CalleeLocal);
            var frameType = context.Symbols.FindType(info.CalleeFrameCanonical)
                ?? throw new CompilerInternalException(
                    "tainted init frame 未预注册: " + info.CalleeFrameCanonical);
            var frameInit = context.Symbols.FindMember(
                SyntheticTypePlanner.FrameInitCanonicalOf(info.CalleeFrameCanonical))
                ?? throw new CompilerInternalException(
                    "tainted init frame init 缺失: " + info.CalleeFrameCanonical);

            // 分配（空 init；init.wrapper/WrapperArgs/ExcTarget 原位）
            headInsts.Add(new MirNewObject(original.Type, original.InitWrapper,
                EmptyCtorInit(context, mir, original.Type), new List<MirOperand>(),
                original.Target, original.WrapperArgs, original.ExcTarget));
            // init frame：.this = 新建对象 + 用户实参/类级 typeid 落参
            headInsts.Add(new MirNewObject(frameType, null, frameInit,
                new List<MirOperand>(), info.CalleeLocal));
            foreach (var drop in info.Drops)
            {
                if (drop.Operand != null)
                {
                    headInsts.Add(new MirSetField(drop.Operand, calleeOp,
                        drop.FrameFieldSymbol));
                }
                else if (drop.TypeIdTypeRef != null)
                {
                    var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                    headInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                    headInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                        drop.FrameFieldSymbol));
                }
                else if (drop.ReceiverTypeIdOwner != null)
                {
                    var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                    headInsts.Add(new MirGetClassTypeArgument(drop.ReceiverTypeIdOperand!,
                        drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                    headInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                        drop.FrameFieldSymbol));
                }
                else
                {
                    headInsts.Add(new MirSetField(
                        new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                        drop.FrameFieldSymbol));
                }
            }
            emitSave(headInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            headInsts.Add(i32Const(point.State, stateConst));
            headInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(callId)));

            var code = fresh("$mw.call.code.", I32);
            var codeTable = new BilSwitchTableResource(
                "$mw.coroutine.call." + _resourceCounter++, ".i32",
                new[] { "0", "1", "2", "3" });
            context.Module.Resources.Add(codeTable);
            resumeFn.AddBlock(new MirBlock(callId, new List<MirInst>
            {
                new MirResumeCall(info.ResumeSymbol, info.CalleeLocal, code),
            }, new MirSwitch(new MirLocalOperand(code), codeTable,
                new[] { propId, propId, doneId, failId }, "mw.state.bad")));

            // DONE：结果即 Target 槽（head 已落定），直落原后继
            resumeFn.AddBlock(new MirBlock(doneId, new List<MirInst>(),
                new MirBranch(ContBlockId(point))));

            // SUSPENDED/YIELDED 上传（同直调口径）
            var propInsts = new List<MirInst>();
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, propInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
            resumeFn.AddBlock(new MirBlock(propId, propInsts,
                new MirRet(new MirLocalOperand(code))));

            // FAILED：pending 已在 TLS，取走重抛沿原构造异常边
            var exc = fresh("$mw.call.exc.", Any);
            var failInsts = new List<MirInst>
            {
                new MirTakePending(exc),
                new MirThrow(new MirLocalOperand(exc), original.ExcTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                original.ExcTarget != null
                    ? (MirTerminator)new MirBranch(original.ExcTarget.Id)
                    : new MirRetThrow()));
        }

        // R2-c new.indirect × tainted class init 的构造改写（分发点
        // 本身成为调用方挂起点；对齐 VM New 语义——分配 → init.wrapper
        // 缝合 → init 调用；init 挂起 = 整条帧链挂起）：
        //   head（原位置）：存活跃槽 + state=N → 精确 sheet 分流链；
        //   分流链（首入/恢复共用生成器）：臂条件 IsTypeId(臂 sheet)
        //     ∧ ¬IsTypeId(各派生排除 sheet)——分发器不继承 init，
        //     排除派生后等价精确相等（typeid 恢复后不变必命中同臂）；
        //   臂（首入）：以臂构造形态 MirNewObject 空 init 分配
        //     （init.wrapper 原位缝合字段初始值）→ Target 槽落定并
        //     回存本层 frame（head 的 emitSave 先于分流执行）→ 建
        //     init frame（.this = Target）+ 落参 → callee 槽回存；
        //   调用块（首入与恢复共用）：MirResumeCall 下钻 → 四码分流
        //     （0/1 上传 / 2 DONE 直落原后继——结果即 Target 槽 /
        //     3 取 pending 沿原 MirNewIndirect.ExcTarget 重抛——半
        //     构造对象随本层托管槽配平释放）；
        //   默认臂：原 MirNewIndirect 直落续行（同步分发器路径——
        //     运行期目标必非 tainted init 或 NoSuchMethod，语义保持）。
        // init frame 所有权归本层（callee 槽持 +1；resume 借用约定
        // 不做最终 release）
        private void EmitNewIndirectSplit(MwContext context, MirModule mir,
            MirFunction resumeFn, MirFunction fn, SuspensionPoint point, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh)
        {
            var site = point.IndirectInit!;
            var callId = CallBlockId(point);
            var dispatchId = callId + ".ndisp";
            var defaultId = callId + ".ndflt";
            var propSuspendId = callId + ".nprops";
            var propYieldId = callId + ".npropy";
            var failId = callId + ".nfail";
            var typeIdOp = new MirLocalOperand(site.TypeIdLocal);
            var targetOp = new MirLocalOperand(site.TargetLocal);
            var callerFrameCanonical = resumeFn.FindLocal(FrameParamName).Type.Canonical;
            var targetFrameField = SyntheticTypePlanner.FrameFieldSymbol(
                callerFrameCanonical, site.TargetLocal,
                fn.FindLocal(site.TargetLocal).Type.Canonical);

            emitSave(headInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            headInsts.Add(i32Const(point.State, stateConst));
            headInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            resumeFn.AddBlock(new MirBlock(headId, headInsts,
                new MirBranch(dispatchId)));

            var armBlockIds = new List<(string New, string Call, string Done)>();
            for (var k = 0; k < site.Arms.Count; k++)
            {
                armBlockIds.Add((callId + ".nnew" + k, callId + ".ncall" + k,
                    callId + ".ndone" + k));
            }

            // 分流链（首入/恢复共用生成器；resume=true 时臂直落调
            // 用块、默认臂防御不可达——挂起前提即首入命中某臂，
            // typeid 不变）
            void EmitDispatchChain(string chainId, string checkPrefix, bool resume)
            {
                for (var k = 0; k < site.Arms.Count; k++)
                {
                    var arm = site.Arms[k];
                    var checkId = k == 0 ? chainId : checkPrefix + (k - 1) + ".next";
                    var hitId = resume ? armBlockIds[k].Call : armBlockIds[k].New;
                    var missId = k + 1 < site.Arms.Count
                        ? checkPrefix + k + ".next"
                        : (resume ? "mw.state.bad" : defaultId);
                    var armCond = fresh("$mw.nchk.", Bool);
                    resumeFn.AddBlock(new MirBlock(checkId, new List<MirInst>
                    {
                        new MirTypeCheck(MirTypeCheckKind.IsTypeId, typeIdOp,
                            arm.SheetCanonical, null, armCond),
                    }, new MirCondBranch(new MirLocalOperand(armCond),
                        arm.ExclusionSheets.Count == 0 ? hitId : checkId + ".x0",
                        missId)));
                    for (var x = 0; x < arm.ExclusionSheets.Count; x++)
                    {
                        var exCond = fresh("$mw.nchk.", Bool);
                        var isLast = x + 1 >= arm.ExclusionSheets.Count;
                        resumeFn.AddBlock(new MirBlock(checkId + ".x" + x,
                            new List<MirInst>
                            {
                                new MirTypeCheck(MirTypeCheckKind.IsTypeId, typeIdOp,
                                    arm.ExclusionSheets[x], null, exCond),
                            }, new MirCondBranch(new MirLocalOperand(exCond), missId,
                                isLast ? hitId : checkId + ".x" + (x + 1))));
                    }
                }
            }
            EmitDispatchChain(dispatchId, callId + ".nc", resume: false);
            EmitDispatchChain(IndirectInitResumeDispatchId(point), callId + ".nrc",
                resume: true);

            // 默认臂：原 new.indirect 直落续行
            resumeFn.AddBlock(new MirBlock(defaultId,
                new List<MirInst> { site.Original },
                new MirBranch(ContBlockId(point))));

            // 每臂：分配 + 建 init frame + 落参（首入）→ 调用块四码
            for (var k = 0; k < site.Arms.Count; k++)
            {
                var arm = site.Arms[k];
                var ids = armBlockIds[k];
                var impl = arm.Impl;
                var calleeOp = new MirLocalOperand(impl.CalleeLocal);
                var frameType = context.Symbols.FindType(impl.CalleeFrameCanonical)
                    ?? throw new CompilerInternalException(
                        "tainted init frame 未预注册: " + impl.CalleeFrameCanonical);
                var frameInit = context.Symbols.FindMember(
                    SyntheticTypePlanner.FrameInitCanonicalOf(impl.CalleeFrameCanonical))
                    ?? throw new CompilerInternalException(
                        "tainted init frame init 缺失: " + impl.CalleeFrameCanonical);
                var newInsts = new List<MirInst>
                {
                    // 分配（空 init；init.wrapper/异常边原位——字段初
                    // 始值缝合与半构造抛出语义同静态构造点）
                    new MirNewObject(arm.AllocType, arm.InitWrapper,
                        EmptyCtorInit(context, mir, arm.AllocType),
                        new List<MirOperand>(), site.TargetLocal, null,
                        site.ExcTarget),
                    // Target 回存本层 frame（head 的 emitSave 先于分
                    // 流执行，槽位尚为旧值——恢复块从本层 frame 恢复）
                    new MirSetField(targetOp, frameOp, targetFrameField),
                    new MirNewObject(frameType, null, frameInit,
                        new List<MirOperand>(), impl.CalleeLocal),
                };
                foreach (var drop in impl.Drops)
                {
                    if (drop.Operand != null)
                    {
                        newInsts.Add(new MirSetField(drop.Operand, calleeOp,
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
                        var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                        newInsts.Add(new MirGetClassTypeArgument(drop.ReceiverTypeIdOperand!,
                            drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                        newInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                            drop.FrameFieldSymbol));
                    }
                    else
                    {
                        newInsts.Add(new MirSetField(
                            new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                            drop.FrameFieldSymbol));
                    }
                }
                // callee 槽回存本层 frame（同 Target 回存理由）
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

                // DONE：结果即 Target 槽（臂内已落定），直落原后继
                resumeFn.AddBlock(new MirBlock(ids.Done, new List<MirInst>(),
                    new MirBranch(ContBlockId(point))));
            }

            // SUSPENDED/YIELDED 上传（各臂调用块共用两块——上传码
            // 是常量；Tasked 调用方先投影自身 Task Suspended）
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

            // FAILED（共用）：pending 已在 TLS（callee plain 垫尾保
            // 持置位），取走重抛沿原构造异常边
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
