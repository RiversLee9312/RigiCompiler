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
    /// 3b-δ1 增补：语言级借用槽机制——native 声明的 native-borrow 借用
    /// 返回标记（seed）沿 MIR 数据流传播为「借用槽」，借用槽零
    /// acquire/release 义务（split-heap 3d local 非原子化的封口前提）。
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
            // 3b-δ1：借用返回集合先于注入推导（注入会改写指令流，推导
            // 必须读原始 MIR）；挂载供 Emit 侧重算借用槽（CastEmitter
            // 对借用槽的 cast 读侧裸取，与义务豁免配平）
            var borrowedReturns = DeriveBorrowedReturns(mir);
            context.BorrowedReturnSymbols = borrowedReturns;
            foreach (var fn in mir.Functions)
            {
                if (fn.Blocks.Count == 0)
                {
                    continue;
                }
                InjectFunction(context, fn, borrowedReturns, out var releaseOrder);
                CheckFunction(context, fn, borrowedReturns, releaseOrder);
            }
        }

        // ===== 3b-δ1：借用传播规则清单（义务层真相源。MW12 借用字段
        // 先例的教训：借用形态必须让 RcInjection 知情——裸读 + 出口恒
        // release = 每调用净 -1 UAF；义务豁免只能做在本层）=====
        //
        // A. 函数级借用返回集合（键 = 成员 canonical，规避泛型具化升级
        //    换对象后 MwMemberSymbol 引用不等的问题）：
        //    A1 seed：native 声明带 native-borrow 修饰符（ABI 契约 = 返回
        //       无 +1 的裸胖引用，C 侧不 acquire）；
        //    A2 Rigi fn：全部 MirRet 出口的值槽都是借用槽 → 借用返回
        //       （借用性随 A1 经 B1 单调传播，朴素不动点至闭包；出口间
        //       混合 = 保持 owned 契约，借用出口照旧走 $mw.ret 三段式
        //       acquire 把借用提升为拥有——语义正确保守）；
        //    A3 invoke.indirect 虚槽调用不可达借用返回 fn（native 恒
        //       static、static 不进 vtable），推导不建虚边。
        // B. 函数内借用槽集合（flow-insensitive，槽粒度保守）：
        //    B1 借用返回调用的 result 槽（MirCall/MirSuperCall 按 Target
        //       canonical 查 A 集合；invoke.indirect/inner call 保守
        //       非借用）；
        //    B2 copy：源槽借用 → 目标借用；B3 cast：源槽借用 → 目标借用；
        //    B4 其他一切产出（new/get.field/get.array/box/take pending/
        //       …）→ 非借用污染；
        //    B5 槽混合产出（既有借用产出又有非借用产出）→ 非借用（有
        //       义务侧胜出，安全方向）。
        // C. 注入豁免：C1 借用槽不进出口 release 序列；C2 借用槽产出的
        //    前置 release 跳过（槽从未拥有旧值）；C3 借用槽 copy 三段式
        //    退化为纯 copy；C4 借用返回 fn 的 $mw.ret copy 保留、acquire
        //    跳过（返回借用契约；调用方按 B1 知情，无义务建立）。
        //    C5 作实参/copy/cast/return 不算逃逸：callee 参数槽照常
        //    acquire 自立（借用值经传参获得 callee 侧 +1，调用方无义务）。
        // D. 逃逸边界（借用值存储逃逸 = 编译拒绝）：
        //    D1 set.field / set.wrapper.field、D2 set.array、D3 协程
        //    frame move、D4 盒化（box.any/wrap.nullable）的源槽借用即
        //    CompilerInternalException；lambda 捕获经隐藏类 set.field
        //    被 D1 覆盖。Handle 仅 unsafe 域可达，不做运行时检查——
        //    借用寿命纪律（不得比借出它的 Handle 活得更久）是 unsafe
        //    契约，见 docs/SYNTAX/02-type-system.md §3.1.2。
        private static HashSet<string> DeriveBorrowedReturns(MirModule mir)
        {
            var borrowed = new HashSet<string>(System.StringComparer.Ordinal);
            // A1 seed——调用点感知：native fn 无函数体（无 BilFunction，不进
            // mir.Functions），声明上的 native-borrow 标记只能在实际调用点
            // 的 Target 符号（MwMemberSymbol.Declaration）读到；未被调用的
            // 借用 native 无需 seed
            foreach (var fn in mir.Functions)
            {
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        var targetSymbol = inst switch
                        {
                            MirCall call => call.Target,
                            MirSuperCall superCall => superCall.Target,
                            _ => null,
                        };
                        if (targetSymbol != null
                            && targetSymbol.HasKeyword(BilKeyword.NativeBorrow))
                        {
                            borrowed.Add(targetSymbol.Canonical);
                        }
                    }
                }
            }
            // A2：不动点。借用性单调递增（只增不减），朴素迭代必收敛；
            // 已在集合内的函数跳过（其出口判定不再变化）
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var fn in mir.Functions)
                {
                    if (borrowed.Contains(fn.Symbol.Canonical))
                    {
                        continue;
                    }
                    if (HasOnlyBorrowedReturns(fn, borrowed))
                    {
                        borrowed.Add(fn.Symbol.Canonical);
                        changed = true;
                    }
                }
            }
            return borrowed;
        }

        // A2 判定：fn 的全部 MirRet 出口值都是借用槽（无出口或存在无值
        // 出口 → 非借用返回；void fn 无 ret 值 → 非借用，无借用义务可言）
        private static bool HasOnlyBorrowedReturns(MirFunction fn,
            HashSet<string> borrowedReturns)
        {
            var hasValueRet = false;
            var slots = DeriveBorrowedSlots(fn, borrowedReturns);
            foreach (var block in fn.Blocks)
            {
                if (block.Terminator is not MirRet ret)
                {
                    continue;
                }
                if (ret.Value is not MirLocalOperand local
                    || !slots.Contains(local.Name))
                {
                    return false;
                }
                hasValueRet = true;
            }
            return hasValueRet;
        }

        // B：函数内借用槽集合（不动点；copy/cast 链式传播要求迭代至闭包）。
        // 改写后的 MIR 上重算结果一致（注入只加 release/acquire/copy，
        // 不新增借用产出面），CheckFunction 与 Emit 侧（CastEmitter 借用
        // 豁免）可直接复用
        internal static HashSet<string> DeriveBorrowedSlots(MirFunction fn,
            HashSet<string> borrowedReturns)
        {
            var candidates = new HashSet<string>(System.StringComparer.Ordinal);
            var polluted = new HashSet<string>(System.StringComparer.Ordinal);
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (ClassifyProduction(inst, borrowedReturns, candidates,
                                polluted, out var borrowed) is not { } target)
                        {
                            continue;
                        }
                        // 借用产出进 candidates（非借用产出已由
                        // ClassifyProduction 记入 polluted）；新加入 = 闭包
                        // 未稳定，继续迭代
                        if (borrowed && candidates.Add(target))
                        {
                            changed = true;
                        }
                    }
                }
            }
            candidates.ExceptWith(polluted);
            return candidates;
        }

        // B 单条产出分类：返回产出目标槽（非产出指令返回 null）；借用
        // 产出置 borrowed 并由调用方记入 candidates，非借用产出在处理
        // 中即时记入 polluted（B4/B5）
        private static string? ClassifyProduction(MirInst inst,
            HashSet<string> borrowedReturns, HashSet<string> candidates,
            HashSet<string> polluted, out bool borrowed)
        {
            borrowed = false;
            string? target;
            switch (inst)
            {
                case MirCall call when call.Result != null:
                    target = call.Result;
                    borrowed = borrowedReturns.Contains(call.Target.Canonical);
                    break;
                case MirSuperCall superCall when superCall.Result != null:
                    target = superCall.Result;
                    borrowed = borrowedReturns.Contains(superCall.Target.Canonical);
                    break;
                // 虚槽/内联调用目标静态不可知，恒按 owned 契约（保守）
                case MirInvokeIndirect invoke when invoke.Result != null:
                    target = invoke.Result;
                    break;
                case MirCast cast:
                    target = cast.Target;
                    borrowed = cast.Source is MirLocalOperand castSource
                        && IsBorrowedSlot(castSource.Name, candidates, polluted);
                    break;
                case MirCopyLocal copy:
                    target = copy.Target;
                    borrowed = copy.Source is MirLocalOperand copySource
                        && IsBorrowedSlot(copySource.Name, candidates, polluted);
                    break;
                default:
                    target = ProductionTarget(inst);
                    break;
            }
            if (target == null)
            {
                return null;
            }
            if (!borrowed)
            {
                polluted.Add(target);
            }
            return target;
        }

        private static bool IsBorrowedSlot(string name,
            HashSet<string> candidates, HashSet<string> polluted) =>
            candidates.Contains(name) && !polluted.Contains(name);

        // D：逃逸边界检查（注入前，原始 MIR 上）
        private static void CheckEscapeBoundary(MirFunction fn,
            HashSet<string> borrowedSlots)
        {
            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    var source = inst switch
                    {
                        MirSetField setField => LocalOperandName(setField.Source),
                        MirSetWrapperField setWrapper => LocalOperandName(setWrapper.Source),
                        MirSetArray setArray => LocalOperandName(setArray.Element),
                        // frame 槽是 move 语义的源槽名（协程续体比栈帧活得久）
                        MirCoroutineCreate create => create.FrameSlot,
                        MirBoxAny box => LocalOperandName(box.Source),
                        MirWrapNullable wrap => LocalOperandName(wrap.Source),
                        _ => null,
                    };
                    if (source != null && borrowedSlots.Contains(source))
                    {
                        throw new CompilerInternalException(
                            $"3b-δ1 借用逃逸：{fn.Symbol.Canonical} 块 {block.Id} "
                            + $"借用槽 ${source} 存储逃逸（{inst.GetType().Name}）"
                            + "——借用值不得存入字段/数组/盒/协程续体");
                    }
                }
            }
        }

        private static string? LocalOperandName(MirOperand operand) =>
            operand is MirLocalOperand local ? local.Name : null;


        private static void InjectFunction(MwContext context, MirFunction fn,
            HashSet<string> borrowedReturns, out List<string> releaseOrder)
        {
            var kinds = ClassifyAll(context, fn);
            var moveSlots = CollectMoveSlots(fn);
            // 3b-δ1：借用槽集合（原始 MIR 上推导；B5 保守规则下与
            // move 槽互斥——借用槽作 frame 源已被逃逸检查拒绝）
            var borrowedSlots = DeriveBorrowedSlots(fn, borrowedReturns);
            CheckEscapeBoundary(fn, borrowedSlots);
            releaseOrder = BuildReleaseOrder(fn, kinds, moveSlots, borrowedSlots);
            foreach (var block in fn.Blocks)
            {
                RewriteBlock(context, fn, block, kinds, releaseOrder,
                    borrowedSlots, borrowedReturns.Contains(fn.Symbol.Canonical));
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
            Dictionary<string, ManagedSlotKind> kinds, HashSet<string> moveSlots,
            HashSet<string> borrowedSlots)
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
                // 3b-δ1 C1：借用槽零义务——无 acquire 就无配平 release
                if (borrowedSlots.Contains(local.Name))
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
            Dictionary<string, ManagedSlotKind> kinds, List<string> releaseOrder,
            HashSet<string> borrowedSlots, bool isBorrowedReturn)
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
                    // 3b-δ1 C3：借用槽 copy 无义务（旧值恒零、新值借用）——
                    // 纯 copy，不插三段式
                    if (borrowedSlots.Contains(copy.Target))
                    {
                        rewritten.Add(copy);
                        continue;
                    }
                    rewritten.Add(new MirReleaseSlot(copy.Target));
                    rewritten.Add(copy);
                    rewritten.Add(new MirAcquireSlot(copy.Target));
                    continue;
                }
                if (ProductionTarget(inst) is { } target && IsManagedTarget(target, kinds)
                    // 3b-δ1 C2：借用槽产出无前置 release（槽从未拥有旧值）
                    && !borrowedSlots.Contains(target))
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
                        // 3b-δ1 C4：借用返回 fn 的 $mw.ret 零义务——不插
                        // 三段式（release/acquire 均无意义：$mw.ret 无拥有
                        // 历史），纯 copy。调用方按 B1 知情，无义务建立。
                        // 借用出口混 owned 出口的 fn 不进借用返回集合，本
                        // 分支不被触达，借用值照旧经三段式 acquire 提升为
                        // 拥有。
                        // owned 分支的 acquire 不可省：它让 $mw.ret 独立持
                        // 有返回值（源槽随后被 EffectiveReleaseOrder 出口
                        // release）。Emit 层交付即移动（TerminatorEmitter
                        // 纯 memcpy 给 out 首参，不再 release $mw.ret）——
                        // tag1 盒槽（Nullable 装箱）的 acquire 有深拷回写
                        // 副作用（arc.c rigi_value_walk 回写槽 payload），
                        // 此处 acquire 是交付块的唯一生产点，删除即 UAF。
                        if (!isBorrowedReturn)
                        {
                            rewritten.Add(new MirReleaseSlot(RetLocalName));
                            rewritten.Add(new MirCopyLocal(ret.Value, RetLocalName));
                            rewritten.Add(new MirAcquireSlot(RetLocalName));
                        }
                        else
                        {
                            rewritten.Add(new MirCopyLocal(ret.Value, RetLocalName));
                        }
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
            HashSet<string> borrowedReturns, List<string> releaseOrder)
        {
            var kinds = ClassifyAll(context, fn);
            var moveSlots = CollectMoveSlots(fn);
            // 3b-δ1：改写后 MIR 上重算借用槽（结果与注入前一致——注入只
            // 加 release/acquire/copy，不新增借用产出面）
            var borrowedSlots = DeriveBorrowedSlots(fn, borrowedReturns);
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
                    // 3b-δ1：借用槽 copy 是纯 copy（C3），要求无三段式——
                    // 出现三段式反而说明对借用槽错误建立了义务
                    if (borrowedSlots.Contains(copy.Target))
                    {
                        if (i > 0 && insts[i - 1] is MirReleaseSlot
                            || i + 1 < insts.Count && insts[i + 1] is MirAcquireSlot)
                        {
                            throw new CompilerInternalException(
                                $"RcInjection 自检失败：{fn.Symbol.Canonical} 借用槽 ${copy.Target} 的 copy 带了 acquire/release 三段式");
                        }
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
