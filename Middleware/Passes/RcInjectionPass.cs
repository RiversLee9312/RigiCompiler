using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// RcInjection（MW7a 决策层）：按槽分类在 MIR 插入 acquire/release。
    /// 读：Mir + Layout；写：原地改写指令列表与终结符。
    /// MW9a 第 C 棒增补：ExcTarget==null 的可抛指令统一解析到函数级共享
    /// 传播垫 mw.propagate（按 ret 出口同口径 release 全部托管槽后
    /// MirRetThrow 异常返回，pending 已在 TLS）。
    /// </summary>
    public sealed class RcInjectionPass : IMwStage
    {
        public const string RetLocalName = "$mw.ret";
        // MW9a：函数级共享传播垫块 id（ExcTarget==null 的唯一解析落点）
        public const string PropagateBlockId = "mw.propagate";
        public string Name => "RcInjection";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("RcInjection 要求 Mir 已挂载");
            foreach (var fn in mir.Functions)
            {
                if (fn.Blocks.Count == 0)
                {
                    continue;
                }
                InjectFunction(context, fn, out var releaseOrder);
                CheckFunction(context, fn, releaseOrder);
            }
        }

        private static void InjectFunction(MwContext context, MirFunction fn,
            out List<string> releaseOrder)
        {
            var kinds = ClassifyAll(context, fn);
            var moveSlots = CollectMoveSlots(fn);
            releaseOrder = BuildReleaseOrder(fn, kinds, moveSlots);
            foreach (var block in fn.Blocks)
            {
                RewriteBlock(context, fn, block, kinds, releaseOrder);
            }
            InsertEntryAcquires(fn, kinds);
            ResolveExceptionEdges(context, fn, releaseOrder);
        }

        // MW11a：所有权转移（move）槽收集——面取走 +1，本地不再 release：
        // MirCoroutineCreate.FrameSlot（frame +1 移交协程续体，
        // cohandle 借用语义，由 resume fn 在 DONE 出口做最终 release）。
        // 这是 ARC 最易错点：move 槽进 release 序列 = UAF/双降
        private static HashSet<string> CollectMoveSlots(MirFunction fn)
        {
            var slots = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    if (inst is MirCoroutineCreate create)
                    {
                        slots.Add(create.FrameSlot);
                    }
                }
            }
            return slots;
        }

        // MW11a：resume fn 的 frame 参数是借用约定（+1 由 spawn 点 move
        // 进续体持有；SUSPENDED/YIELDED 出口不释放——续体仍存活；DONE
        // 出口做最终 release）
        private static bool IsResumeFrameParam(MirFunction fn, string name) =>
            fn.IsCoroutineResume && name == CoroutineSplitPass.FrameParamName;

        // MW11b 棒3：probe fn 的 alarm 参数同为借用约定（+1 由 C 侧
        // 探测登记项持有至摘链；probe 借用读取，入口不 acquire、出口
        // 不 release——无 DONE 出口式的最终 release 点）
        private static bool IsProbeAlarmParam(MirFunction fn, string name) =>
            fn.IsPollProbe && name == CoroutineSplitPass.ProbeParamName;

        // DONE 出口判定（resume fn）：块内含 MirCoroutineDone 标记
        //（split 的 complete/fail 终态序列与传播垫共有），frame 在此
        // 出口最终 release
        private static bool IsCoroutineDoneExit(MirFunction fn, MirBlock block) =>
            fn.IsCoroutineResume && block.Instructions.Any(
                i => i is MirCoroutineDone);

        // 出口有效 release 序列：基础序列（releaseOrder，已排除 move 槽
        // 与 frame 参数）+ DONE 出口/传播垫（block==null）追加 frame
        // 参数最终 release。B-1：plain resume（tainted 普通 fn）的
        // frame 所有权归调用方（不经 MirCoroutineCreate move），任何
        // 出口都不做 frame 最终 release
        private static List<string> EffectiveReleaseOrder(MirFunction fn, MirBlock? block,
            List<string> releaseOrder)
        {
            if (!fn.IsCoroutineResume || fn.IsPlainResume)
            {
                return releaseOrder;
            }
            if (block == null || IsCoroutineDoneExit(fn, block))
            {
                var effective = new List<string>(releaseOrder) { CoroutineSplitPass.FrameParamName };
                return effective;
            }
            return releaseOrder;
        }

        // RigiResumeCode DONE（rigi_rt/cohandle.h 口径；resume fn 传播垫尾）
        // MW11b 棒3：probe fn 传播垫尾返回码（RigiPollProbeFn -1 =
        // isReady 抛异常，pending 保持置位由恢复块失败尾取走——棒5a 起
        // 探测由恢复块直调 $mw.poll_probe，-1 语义不变）
        private const int ProbeCodeError = -1;

        // MW9a：ExcTarget==null 的可抛指令（MirCall/MirSuperCall/
        // MirInvokeIndirect/MirThrow；MW9b-G 扩面 MirBinaryIntrinsic（除零
        // 守卫）/MirCast/MirUnboxAny/MirSetArray/MirNewIndirect）统一改写
        // 指向函数级共享传播垫。
        // 垫 = 按 ret 出口同口径 release 全部托管槽（无任何豁免——异常
        // 路径无返回值）+ MirRetThrow 收尾；MirThrow 出厂块的终结符同步
        // 从 MirRetThrow 改道 MirBranch(垫)。try 展开产物的 ExcTarget
        // 恒非空（B 棒契约），此处只触用户函数体直写 throw/调用的 null
        // MW9a：ExcTarget==null 的可抛指令（MirCall/MirSuperCall/
        // MirInvokeIndirect/MirThrow；MW9b-G 扩面 MirBinaryIntrinsic（除零
        // 守卫）/MirCast/MirUnboxAny/MirSetArray/MirNewIndirect）统一改写
        // 指向函数级共享传播垫。
        // 垫 = 按 ret 出口同口径 release 全部托管槽（无任何豁免——异常
        // 路径无返回值）+ MirRetThrow 收尾；MirThrow 出厂块的终结符同步
        // 从 MirRetThrow 改道 MirBranch(垫)。try 展开产物的 ExcTarget
        // 恒非空（B 棒契约），此处只触用户函数体直写 throw/调用的 null。
        // MW11c 棒5a 分叉：resume fn 的垫尾 = 失败终态序列
        //（take pending → native 失败注册表 → task.failureNodeId →
        // fail() → publishAll → noteTerminal → MirCoroutineDone →
        // ret DONE——CoroutineSplitPass.EmitFailTerminal）+ 垫自带托管
        // 局部显式 release + 标准 release 序列 + frame 最终 release
        //（未捕获异常不跨协程帧传播，归宿是 Task FAILED）；普通 fn
        // 维持 release + MirRetThrow
        private static void ResolveExceptionEdges(MwContext context, MirFunction fn,
            List<string> releaseOrder)
        {
            var needsPad = false;
            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    if (HasNullExcTarget(inst))
                    {
                        needsPad = true;
                        break;
                    }
                }
                if (needsPad)
                {
                    break;
                }
            }
            if (!needsPad)
            {
                return;
            }
            MirBlock pad;
            if (fn.IsPlainResume)
            {
                // B-1 垫尾分叉：plain resume（tainted 普通 fn）的传播垫
                // = release 全托管槽 + ret FAILED（3）——pending 保持
                // 置位沿调用链上传（调用方调用点 FAILED 臂取走重抛）；
                // 无 Task 终态序列、无 frame 最终 release（所有权归
                // 调用方，callee 槽随调用方 frame 释放）
                var failCode = ProxyWildcardAbi.FreshLocal(fn, "$mw.code.",
                    MirType.Of(".i32"));
                var padInsts = new List<MirInst>(releaseOrder.Count + 2)
                {
                    new MirLoadResource(
                        ProxyWildcardAbi.AddI32Resource(context,
                            CoroutineSplitPass.PlainResumeFailedCode), failCode),
                };
                foreach (var name in releaseOrder)
                {
                    padInsts.Add(new MirReleaseSlot(name));
                }
                pad = new MirBlock(PropagateBlockId, padInsts,
                    new MirRet(new MirLocalOperand(failCode)));
            }
            else if (fn.IsPollProbe)
            {
                // MW11b 棒3 垫尾分叉：probe fn 的传播垫 = release 全托管
                // 槽 + ret -1——pending 保持置位（不 MirTakePending），
                // 由 drain 探测轮在 probe 返 -1 后 rigi_exc_take 取走，
                // 走 yield 点失败路径（Task FAILED）
                var errorCode = ProxyWildcardAbi.FreshLocal(fn, "$mw.code.",
                    MirType.Of(".i32"));
                var padInsts = new List<MirInst>(releaseOrder.Count + 2)
                {
                    new MirLoadResource(
                        ProxyWildcardAbi.AddI32Resource(context, ProbeCodeError), errorCode),
                };
                foreach (var name in releaseOrder)
                {
                    padInsts.Add(new MirReleaseSlot(name));
                }
                pad = new MirBlock(PropagateBlockId, padInsts,
                    new MirRet(new MirLocalOperand(errorCode)));
            }
            else if (fn.IsCoroutineResume)
            {
                // 棒5a 垫尾分叉：失败终态序列（take pending → native
                // 失败注册表登记 → task.failureNodeId → fail() →
                // publishAll → noteTerminal → MirCoroutineDone → ret
                // DONE；未捕获异常归宿 Task FAILED，不跨协程帧传播）。
                // 垫在 releaseOrder 冻结后合成：序列新建的托管局部显式
                // 释放（先于标准序列），标准序列 + frame 最终 release
                // 收尾（EffectiveReleaseOrder block==null 通道）
                var padManaged = new List<string>();
                var padInsts = new List<MirInst>();
                var terminator = CoroutineSplitPass.EmitFailTerminal(context,
                    context.Mir!, fn, padInsts, CoroutineSplitPass.TaskFieldSymbolOfResume(
                        context, fn, out var taskTypeRef), taskTypeRef,
                    CoroutineSplitPass.ResolveRuntime(context, context.Mir!),
                    padManaged);
                foreach (var name in padManaged)
                {
                    padInsts.Add(new MirReleaseSlot(name));
                }
                foreach (var name in EffectiveReleaseOrder(fn, null!, releaseOrder))
                {
                    padInsts.Add(new MirReleaseSlot(name));
                }
                pad = new MirBlock(PropagateBlockId, padInsts, terminator);
            }
            else
            {
                var padInsts = new List<MirInst>(releaseOrder.Count);
                foreach (var name in releaseOrder)
                {
                    padInsts.Add(new MirReleaseSlot(name));
                }
                pad = new MirBlock(PropagateBlockId, padInsts, new MirRetThrow());
            }
            fn.AddBlock(pad);
            foreach (var block in fn.Blocks)
            {
                if (ReferenceEquals(block, pad))
                {
                    continue;
                }
                var insts = block.InstructionList;
                for (var i = 0; i < insts.Count; i++)
                {
                    insts[i] = insts[i] switch
                    {
                        MirCall call when call.ExcTarget == null => new MirCall(
                            call.Target, call.Args, call.Result, pad,
                            operatorDispatch: call.OperatorDispatch,
                            hostConstructedRef: call.HostConstructedRef),
                        MirSuperCall superCall when superCall.ExcTarget == null => new MirSuperCall(
                            superCall.Target, superCall.Args, superCall.Result, pad),
                        MirInvokeIndirect invoke when invoke.ExcTarget == null => new MirInvokeIndirect(
                            invoke.CallTarget, invoke.Args, invoke.Result, invoke.CallTargetType, pad),
                        MirThrow throwInst when throwInst.ExcTarget == null => new MirThrow(
                            throwInst.Exception, pad),
                        // MW9b-G：守卫型可抛指令同口径解析
                        MirBinaryIntrinsic binary when binary.ExcTarget == null =>
                            new MirBinaryIntrinsic(binary.Op, binary.Left, binary.Right,
                                binary.LeftType, binary.RightType, binary.ResultType,
                                binary.Target, pad),
                        MirGenericBinaryOp genericBinary when genericBinary.ExcTarget == null =>
                            new MirGenericBinaryOp(genericBinary.Op, genericBinary.Left,
                                genericBinary.Right, genericBinary.LeftType,
                                genericBinary.RightType, genericBinary.ResultType,
                                genericBinary.Target, pad),
                        MirGenericUnaryOp genericUnary when genericUnary.ExcTarget == null =>
                            new MirGenericUnaryOp(genericUnary.Op, genericUnary.Operand,
                                genericUnary.OperandType, genericUnary.ResultType,
                                genericUnary.Target, pad),
                        MirCast cast when cast.ExcTarget == null => new MirCast(
                            cast.Source, cast.Target, cast.IsSafe, cast.TargetTypeRef,
                            cast.TargetTypeId, pad),
                        MirUnboxAny unbox when unbox.ExcTarget == null => new MirUnboxAny(
                            unbox.Source, unbox.Target, pad),
                        MirSetArray setArray when setArray.ExcTarget == null => new MirSetArray(
                            setArray.Collection, setArray.Index, setArray.Element,
                            setArray.CollectionType, pad),
                        MirNewIndirect newIndirect when newIndirect.ExcTarget == null =>
                            new MirNewIndirect(newIndirect.TypeId, newIndirect.Args,
                                newIndirect.Target, pad),
                        MirGetField getField when getField.ExcTarget == null => new MirGetField(
                            getField.Object, getField.FieldSymbol, getField.Target, pad),
                        _ => insts[i],
                    };
                }
                // MirRetThrow 收尾的非垫块恒以 MirThrow 收尾（B 棒契约）：
                // throw 已解析进垫，终结符随之改道
                if (block.Terminator is MirRetThrow)
                {
                    block.Terminator = new MirBranch(pad.Id);
                }
            }
        }

        private static bool HasNullExcTarget(MirInst inst) => inst switch
        {
            MirCall call => call.ExcTarget == null,
            MirSuperCall superCall => superCall.ExcTarget == null,
            MirInvokeIndirect invoke => invoke.ExcTarget == null,
            MirThrow throwInst => throwInst.ExcTarget == null,
            // MW9b-G：守卫型可抛指令
            MirBinaryIntrinsic binary => binary.ExcTarget == null,
            // G4：占位派发节点（运行期抛出点：候选落空/除零守卫）
            MirGenericBinaryOp genericBinary => genericBinary.ExcTarget == null,
            MirGenericUnaryOp genericUnary => genericUnary.ExcTarget == null,
            MirCast cast => cast.ExcTarget == null,
            MirUnboxAny unbox => unbox.ExcTarget == null,
            MirSetArray setArray => setArray.ExcTarget == null,
            MirNewIndirect newIndirect => newIndirect.ExcTarget == null,
            MirGetField getField => getField.ExcTarget == null,
            _ => false,
        };

        private static Dictionary<string, ManagedSlotKind> ClassifyAll(
            MwContext context, MirFunction fn)
        {
            var kinds = new Dictionary<string, ManagedSlotKind>(System.StringComparer.Ordinal);
            foreach (var local in fn.Locals)
            {
                kinds[local.Name] = TypeLayout.ClassifySlot(context, local.Type);
            }
            return kinds;
        }

        private static List<string> BuildReleaseOrder(MirFunction fn,
            Dictionary<string, ManagedSlotKind> kinds, HashSet<string> moveSlots)
        {
            var addrAliases = CollectAddrAliasTargets(fn);
            var order = new List<string>();
            foreach (var local in fn.Locals)
            {
                if (local.Name == RetLocalName || !TypeLayout.IsManagedSlot(kinds[local.Name]))
                {
                    continue;
                }
                if (IsExemptThis(fn, local.Name, kinds))
                {
                    continue;
                }
                if (addrAliases.Contains(local.Name))
                {
                    continue;
                }
                // MW11a：move 槽（所有权已移交运行时）与 resume fn 的
                // frame 借用参数不进基础 release 序列（frame 的最终
                // release 由 EffectiveReleaseOrder 在 DONE 出口/垫追加）；
                // MW11b：probe fn 的 alarm 借用参数同样不进（无任何出口
                // release 点——C 侧登记项持有）
                if (moveSlots.Contains(local.Name))
                {
                    continue;
                }
                if (IsResumeFrameParam(fn, local.Name) || IsProbeAlarmParam(fn, local.Name))
                {
                    continue;
                }
                order.Add(local.Name);
            }
            return order;
        }

        // get.wrapper[.field/.method].addr 目标局部：槽已重定向为宿主隐藏槽地址
        //（MW10 刀3c 环 receiver 取址），非自有 +1 存储——地址读取不产生
        // 值拷贝，不引入 acquire/release 义务，排除出 release 序列（否则
        // 出口 release 会落到宿主隐藏槽上造成重复释放）
        private static HashSet<string> CollectAddrAliasTargets(MirFunction fn)
        {
            var targets = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    switch (inst)
                    {
                        case MirGetWrapperAddr addr:
                            targets.Add(addr.Target);
                            break;
                        case MirGetWrapperFieldAddr fieldAddr:
                            targets.Add(fieldAddr.Target);
                            break;
                        case MirGetWrapperMethodAddr methodAddr:
                            targets.Add(methodAddr.Target);
                            break;
                    }
                }
            }
            return targets;
        }

        // 值类型宿主的 .this 是调用方存储别名，不纳入所有权
        private static bool IsExemptThis(MirFunction fn, string name,
            Dictionary<string, ManagedSlotKind> kinds) =>
            (name == WrapperSelfParameterPass.SelfParameter && fn.Parameters.Any(p => p.Name == name))
            || name == ".this"
            && (kinds[name] == ManagedSlotKind.RichValue
                || fn.Symbol.Owner?.Declaration.Kind is BilTypeKind.Struct
                    or BilTypeKind.EnumStruct or BilTypeKind.Wrapper);

        // 值类型参数的 +1 由 EmitInitRichValue 落槽建立，避免与规则 1 双计。
        // MW11a：resume fn 的 frame 参数是借用（spawn 点 move 进续体的
        // +1），入口不 acquire
        private static bool ShouldAcquireParam(MirFunction fn, MirLocal parameter,
            Dictionary<string, ManagedSlotKind> kinds)
        {
            if (IsExemptThis(fn, parameter.Name, kinds))
            {
                return false;
            }
            if (IsResumeFrameParam(fn, parameter.Name) || IsProbeAlarmParam(fn, parameter.Name))
            {
                return false;
            }
            return kinds.TryGetValue(parameter.Name, out var kind)
                && kind is ManagedSlotKind.FatReference or ManagedSlotKind.String;
        }

        private static void RewriteBlock(MwContext context, MirFunction fn, MirBlock block,
            Dictionary<string, ManagedSlotKind> kinds, List<string> releaseOrder)
        {
            var rewritten = new List<MirInst>(block.InstructionList.Count + 8);
            foreach (var inst in block.InstructionList)
            {
                if (inst is MirCopyLocal copy && IsManagedTarget(copy.Target, kinds))
                {
                    if (copy.Source is MirLocalOperand source && source.Name == copy.Target)
                    {
                        continue;
                    }
                    rewritten.Add(new MirReleaseSlot(copy.Target));
                    rewritten.Add(copy);
                    rewritten.Add(new MirAcquireSlot(copy.Target));
                    continue;
                }
                if (ProductionTarget(inst) is { } target && IsManagedTarget(target, kinds))
                {
                    rewritten.Add(new MirReleaseSlot(target));
                }
                rewritten.Add(inst);
            }
            if (block.Terminator is MirRet ret)
            {
                if (ret.Value is MirLocalOperand retLocal
                    && TypeLayout.IsManagedSlot(TypeLayout.ClassifySlot(context, fn.ReturnType)))
                {
                    EnsureRetLocal(context, fn, kinds);
                    if (retLocal.Name != RetLocalName)
                    {
                        rewritten.Add(new MirReleaseSlot(RetLocalName));
                        rewritten.Add(new MirCopyLocal(ret.Value, RetLocalName));
                        rewritten.Add(new MirAcquireSlot(RetLocalName));
                    }
                    block.Terminator = new MirRet(new MirLocalOperand(RetLocalName));
                }
                foreach (var name in EffectiveReleaseOrder(fn, block, releaseOrder))
                {
                    rewritten.Add(new MirReleaseSlot(name));
                }
            }
            block.InstructionList.Clear();
            block.InstructionList.AddRange(rewritten);
        }

        private static void EnsureRetLocal(MwContext context, MirFunction fn,
            Dictionary<string, ManagedSlotKind> kinds)
        {
            if (fn.TryFindLocal(RetLocalName, out _))
            {
                return;
            }
            fn.AddLocal(new MirLocal(RetLocalName, fn.ReturnType));
            kinds[RetLocalName] = TypeLayout.ClassifySlot(context, fn.ReturnType);
        }

        private static void InsertEntryAcquires(MirFunction fn,
            Dictionary<string, ManagedSlotKind> kinds)
        {
            var acquires = new List<MirInst>();
            foreach (var parameter in fn.Parameters)
            {
                if (ShouldAcquireParam(fn, parameter, kinds))
                {
                    acquires.Add(new MirAcquireSlot(parameter.Name));
                }
            }
            if (acquires.Count == 0)
            {
                return;
            }
            fn.Blocks[0].InstructionList.InsertRange(0, acquires);
        }

        private static bool IsManagedTarget(string name,
            Dictionary<string, ManagedSlotKind> kinds) =>
            kinds.TryGetValue(name, out var kind) && TypeLayout.IsManagedSlot(kind);

        private static string? ProductionTarget(MirInst inst) => inst switch
        {
            MirLoadResource load => load.Target,
            MirBinaryIntrinsic binary => binary.Target,
            MirGenericBinaryOp genericBinary => genericBinary.Target,
            MirGenericUnaryOp genericUnary => genericUnary.Target,
            MirCall call => call.Result,
            MirSuperCall superCall => superCall.Result,
            MirInvokeIndirect invoke => invoke.Result,
            MirNewIndirect newIndirect => newIndirect.Target,
            MirNewObject newObject => newObject.Target,
            MirNewValue newValue => newValue.Target,
            MirNewCase newCase => newCase.Target,
            MirGetWrapper getWrapper => getWrapper.Target,
            MirGetWrapperField getWrapperField => getWrapperField.Target,
            MirGetSelf getSelf => getSelf.Target,
            MirInnerCall inner when inner.Result != null => inner.Result,
            MirGetField getField => getField.Target,
            MirGetStatic getStatic => getStatic.Target,
            MirGetArray getArray => getArray.Target,
            MirWrapNullable wrap => wrap.Target,
            MirUnwrapNullable unwrap => unwrap.Target,
            MirBoxAny box => box.Target,
            MirUnboxAny unbox => unbox.Target,
            MirCast cast => cast.Target,
            MirNewArray newArray => newArray.Target,
            // MW9a：pending 移入目标槽（移动语义建立 owned +1，不插 acquire；
            // 旧值按产出类前置 release）。MirThrow 不消耗操作数槽，无动作
            MirTakePending takePending => takePending.TargetLocal,
            // MW11a：MirSpawn 产出热 Task 胖引用（+1 随 out 移交）；
            // MirTaskWait 终态路径产出结果/异常拷贝（+1 随拷贝移交；
            // SUSPENDED 路径不写槽，零值 release 无操作）。
            // MW11c 棒5a：MirFailureLoad 产出异常拷贝（+1 随拷贝移交）
            MirFailureLoad failureLoad => failureLoad.OutFatSlot,
            _ => null,
        };

        private static void CheckFunction(MwContext context, MirFunction fn,
            List<string> releaseOrder)
        {
            var kinds = ClassifyAll(context, fn);
            var moveSlots = CollectMoveSlots(fn);
            // 期望出口序列必须用注入期冻结的 releaseOrder：棒5a 传播垫
            // EmitFailTerminal 在冻结之后追加托管局部（垫内自释放），
            // 重建 order 会把垫专用槽算进所有 MirRet 出口而误报不完整
            foreach (var block in fn.Blocks)
            {
                if (block.Terminator is not MirRet && block.Terminator is not MirRetThrow)
                {
                    continue;
                }
                // MW9a：MirRetThrow 出口恒为传播垫（其余出厂块已被解析改道）
                if (block.Terminator is MirRetThrow && block.Id != PropagateBlockId)
                {
                    throw new CompilerInternalException(
                        $"RcInjection 自检失败：{fn.Symbol.Canonical} 块 {block.Id} 非传播垫却 MirRetThrow 收尾");
                }
                var insts = block.InstructionList;
                // MW11a：DONE 出口/垫的有效序列含 frame 最终 release
                var effective = block.Id == PropagateBlockId
                    ? EffectiveReleaseOrder(fn, null, releaseOrder)
                    : EffectiveReleaseOrder(fn, block, releaseOrder);
                if (insts.Count < effective.Count)
                {
                    throw new CompilerInternalException(
                        $"RcInjection 自检失败：{fn.Symbol.Canonical} 块 {block.Id} release 序列不完整");
                }
                var start = insts.Count - effective.Count;
                for (var i = 0; i < effective.Count; i++)
                {
                    if (insts[start + i] is not MirReleaseSlot release
                        || release.Local != effective[i])
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 块 {block.Id} release 序列不匹配");
                    }
                    if (release.Local == RetLocalName)
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 出口 release 含 {RetLocalName}");
                    }
                    if (moveSlots.Contains(release.Local))
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 出口 release 含 move 槽 ${release.Local}（所有权已移交运行时）");
                    }
                }
            }
            foreach (var block in fn.Blocks)
            {
                var insts = block.InstructionList;
                for (var i = 0; i < insts.Count; i++)
                {
                    if (insts[i] is not MirCopyLocal copy
                        || !IsManagedTarget(copy.Target, kinds))
                    {
                        continue;
                    }
                    if (i == 0 || i + 1 >= insts.Count
                        || insts[i - 1] is not MirReleaseSlot rel || rel.Local != copy.Target
                        || insts[i + 1] is not MirAcquireSlot acq || acq.Local != copy.Target)
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 未展开的托管 CopyLocal ${copy.Target}");
                    }
                }
            }
            var expected = new List<string>();
            foreach (var parameter in fn.Parameters)
            {
                if (ShouldAcquireParam(fn, parameter, kinds))
                {
                    expected.Add(parameter.Name);
                }
            }
            var entry = fn.Blocks[0].InstructionList;
            if (entry.Count < expected.Count)
            {
                throw new CompilerInternalException(
                    $"RcInjection 自检失败：{fn.Symbol.Canonical} entry 缺参数 acquire 序列");
            }
            for (var i = 0; i < expected.Count; i++)
            {
                if (entry[i] is not MirAcquireSlot acq || acq.Local != expected[i])
                {
                    throw new CompilerInternalException(
                        $"RcInjection 自检失败：{fn.Symbol.Canonical} entry 参数 acquire 序列不匹配");
                }
            }
            // MW9a：全部可抛指令的 ExcTarget 必须已解析（指向派发垫/逃逸垫/
            // 传播垫）；残留 null = 传播垫创建或改写遗漏。传播垫自身的
            // MirCall（棒5a EmitFailTerminal：failure_record/fail/publishAll）
            // 是终点，改写进自己会重入，故意保持 null
            foreach (var block in fn.Blocks)
            {
                if (block.Id == PropagateBlockId)
                {
                    continue;
                }
                foreach (var inst in block.Instructions)
                {
                    if (HasNullExcTarget(inst))
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 残留 ExcTarget==null 的 {inst.GetType().Name}");
                    }
                }
            }
            // MW11a：move 槽全局免配平断言——任何 MirReleaseSlot 命中
            // move 槽即 ARC 配平错误（面已取走 +1，本地 release 会双降）。
            // 唯一合法例外：产出前置 release（紧挨着产出该槽的指令之前，
            // 释放的是旧值——move 槽合成时零初始化，实为无操作）
            foreach (var block in fn.Blocks)
            {
                var insts = block.InstructionList;
                for (var i = 0; i < insts.Count; i++)
                {
                    if (insts[i] is not MirReleaseSlot release
                        || !moveSlots.Contains(release.Local))
                    {
                        continue;
                    }
                    var isPreProduction = i + 1 < insts.Count
                        && (ProductionTarget(insts[i + 1]) == release.Local
                            // 托管 CopyLocal 三段式的首段 release（旧值）
                            || (insts[i + 1] is MirCopyLocal copy
                                && copy.Target == release.Local));
                    if (!isPreProduction)
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 块 {block.Id} release 命中 move 槽 ${release.Local}");
                    }
                }
            }
        }
    }
}
