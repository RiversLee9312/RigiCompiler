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
    /// </summary>
    public sealed class RcInjectionPass : IMwStage
    {
        public const string RetLocalName = "$mw.ret";
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
        }

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
            MirNewArray newArray => newArray.Target,
            _ => null,
        };

        private static void CheckFunction(MwContext context, MirFunction fn)
        {
            var kinds = ClassifyAll(context, fn);
            var releaseOrder = BuildReleaseOrder(fn, kinds);
            foreach (var block in fn.Blocks)
            {
                if (block.Terminator is not MirRet)
                {
                    continue;
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
        }
    }
}
