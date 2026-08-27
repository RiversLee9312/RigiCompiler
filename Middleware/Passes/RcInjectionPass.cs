using System.Collections.Generic;
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
                InjectFunction(context, fn);
                CheckFunction(context, fn);
            }
        }

        private static void InjectFunction(MwContext context, MirFunction fn)
        {
            var kinds = ClassifyAll(context, fn);
            var releaseOrder = BuildReleaseOrder(fn, kinds);
            foreach (var block in fn.Blocks)
            {
                RewriteBlock(context, fn, block, kinds, releaseOrder);
            }
            InsertEntryAcquires(fn, kinds);
            ResolveExceptionEdges(fn, releaseOrder);
        }

        // MW9a：ExcTarget==null 的可抛指令（MirCall/MirSuperCall/
        // MirInvokeIndirect/MirThrow；MW9b-G 扩面 MirBinaryIntrinsic（除零
        // 守卫）/MirCast/MirUnboxAny/MirSetArray/MirNewIndirect）统一改写
        // 指向函数级共享传播垫。
        // 垫 = 按 ret 出口同口径 release 全部托管槽（无任何豁免——异常
        // 路径无返回值）+ MirRetThrow 收尾；MirThrow 出厂块的终结符同步
        // 从 MirRetThrow 改道 MirBranch(垫)。try 展开产物的 ExcTarget
        // 恒非空（B 棒契约），此处只触用户函数体直写 throw/调用的 null
        private static void ResolveExceptionEdges(MirFunction fn, List<string> releaseOrder)
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
            var padInsts = new List<MirInst>(releaseOrder.Count);
            foreach (var name in releaseOrder)
            {
                padInsts.Add(new MirReleaseSlot(name));
            }
            var pad = new MirBlock(PropagateBlockId, padInsts, new MirRetThrow());
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
                            call.Target, call.Args, call.Result, pad),
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
            Dictionary<string, ManagedSlotKind> kinds)
        {
            var order = new List<string>();
            foreach (var local in fn.Locals)
            {
                if (local.Name == RetLocalName || !TypeLayout.IsManagedSlot(kinds[local.Name]))
                {
                    continue;
                }
                if (IsExemptThis(fn, local.Name))
                {
                    continue;
                }
                order.Add(local.Name);
            }
            return order;
        }

        // 值类型宿主的 .this 是调用方存储别名，不纳入所有权
        private static bool IsExemptThis(MirFunction fn, string name) =>
            name == ".this"
            && fn.Symbol.Owner?.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct;

        // 值类型参数的 +1 由 EmitInitRichValue 落槽建立，避免与规则 1 双计
        private static bool ShouldAcquireParam(MirFunction fn, MirLocal parameter,
            Dictionary<string, ManagedSlotKind> kinds)
        {
            if (IsExemptThis(fn, parameter.Name))
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
                foreach (var name in releaseOrder)
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
            MirCall call => call.Result,
            MirSuperCall superCall => superCall.Result,
            MirInvokeIndirect invoke => invoke.Result,
            MirNewIndirect newIndirect => newIndirect.Target,
            MirNewObject newObject => newObject.Target,
            MirNewValue newValue => newValue.Target,
            MirNewCase newCase => newCase.Target,
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
            _ => null,
        };

        private static void CheckFunction(MwContext context, MirFunction fn)
        {
            var kinds = ClassifyAll(context, fn);
            var releaseOrder = BuildReleaseOrder(fn, kinds);
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
                if (insts.Count < releaseOrder.Count)
                {
                    throw new CompilerInternalException(
                        $"RcInjection 自检失败：{fn.Symbol.Canonical} 块 {block.Id} release 序列不完整");
                }
                var start = insts.Count - releaseOrder.Count;
                for (var i = 0; i < releaseOrder.Count; i++)
                {
                    if (insts[start + i] is not MirReleaseSlot release
                        || release.Local != releaseOrder[i])
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 块 {block.Id} release 序列不匹配");
                    }
                    if (release.Local == RetLocalName)
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 出口 release 含 {RetLocalName}");
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
            // 传播垫）；残留 null = 传播垫创建或改写遗漏
            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    if (HasNullExcTarget(inst))
                    {
                        throw new CompilerInternalException(
                            $"RcInjection 自检失败：{fn.Symbol.Canonical} 残留 ExcTarget==null 的 {inst.GetType().Name}");
                    }
                }
            }
        }
    }
}
