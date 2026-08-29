using System.Collections.Generic;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// 数组运算符降级（MW4）：用户类型的 MirGetArray/MirSetArray 改写为
    /// MirCall($$getAtIndex/$$setAtIndex）；内建连续缓冲区（Array / Span /
    /// SharedSpan）保留原指令。读：Mir + Symbols + Layout；写：原地改写
    /// Mir 指令列表。本 pass 为小改写：唯一 switch 分派到内部类，无共享
    /// 可变状态，不上 CRTP。
    /// </summary>
    public sealed class IndexOperatorLoweringPass : IMwStage
    {
        public string Name => "IndexOperatorLowering";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("IndexOperatorLowering 要求 Mir 已挂载");
            foreach (var fn in mir.Functions)
            {
                RewriteFunction(context, fn);
            }
        }

        internal static void RewriteFunction(MwContext context, MirFunction fn)
        {
            foreach (var block in fn.Blocks)
            {
                RewriteBlock(context, block);
            }
        }

        private static void RewriteBlock(MwContext context, MirBlock block)
        {
            var insts = block.InstructionList;
            for (var i = 0; i < insts.Count; i++)
            {
                insts[i] = insts[i] switch
                {
                    MirGetArray get => GetArrayLowering.Rewrite(context, get),
                    MirSetArray set => SetArrayLowering.Rewrite(context, set),
                    var other => other,
                };
            }
        }

        // 用户类型下标读 → $$getAtIndex；连续缓冲区原样保留
        private static class GetArrayLowering
        {
            internal static MirInst Rewrite(MwContext context, MirGetArray inst)
            {
                if (TypeLayout.IsContiguousBuffer(inst.CollectionType))
                {
                    return inst;
                }
                var method = ImplBinder.FindIndexOperator(context.Symbols,
                    inst.CollectionType.Canonical, isGet: true)
                    ?? throw new MwNotSupportedException(
                        $"没有 getAtIndex：{inst.CollectionType.Canonical}");
                return new MirCall(method,
                    new List<MirOperand> { inst.Collection, inst.Index },
                    inst.Target);
            }
        }

        // 用户类型下标写 → $$setAtIndex；连续缓冲区原样保留
        private static class SetArrayLowering
        {
            internal static MirInst Rewrite(MwContext context, MirSetArray inst)
            {
                if (TypeLayout.IsContiguousBuffer(inst.CollectionType))
                {
                    return inst;
                }
                var method = ImplBinder.FindIndexOperator(context.Symbols,
                    inst.CollectionType.Canonical, isGet: false)
                    ?? throw new MwNotSupportedException(
                        $"没有 setAtIndex：{inst.CollectionType.Canonical}");
                return new MirCall(method,
                    new List<MirOperand>
                    {
                        inst.Collection, inst.Index, inst.Element,
                    }, null);
            }
        }
    }
}
