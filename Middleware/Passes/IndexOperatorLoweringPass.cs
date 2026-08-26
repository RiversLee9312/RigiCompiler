using System.Collections.Generic;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// 数组运算符降级（MW4）：用户类型的 MirGetArray/MirSetArray 改写为
    /// MirCall($$getAtIndex/$$setAtIndex）；内建连续缓冲区（Array / Span /
    /// SharedSpan）保留原指令。读：Mir + Symbols + Layout；写：原地改写
    /// Mir 指令列表。
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
                foreach (var block in fn.Blocks)
                {
                    RewriteBlock(context, block);
                }
            }
        }

        private static void RewriteBlock(MwContext context, MirBlock block)
        {
            var insts = block.InstructionList;
            for (var i = 0; i < insts.Count; i++)
            {
                switch (insts[i])
                {
                    case MirGetArray get when !TypeLayout.IsContiguousBuffer(get.CollectionType):
                    {
                        var method = MirBuilder.FindIndexOperator(context.Symbols,
                            get.CollectionType, isGet: true)
                            ?? throw new MwNotSupportedException(
                                $"没有 getAtIndex：{get.CollectionType.Canonical}");
                        insts[i] = new MirCall(method,
                            new List<MirOperand> { get.Collection, get.Index },
                            get.Target);
                        break;
                    }
                    case MirSetArray set when !TypeLayout.IsContiguousBuffer(set.CollectionType):
                    {
                        var method = MirBuilder.FindIndexOperator(context.Symbols,
                            set.CollectionType, isGet: false)
                            ?? throw new MwNotSupportedException(
                                $"没有 setAtIndex：{set.CollectionType.Canonical}");
                        insts[i] = new MirCall(method,
                            new List<MirOperand>
                            {
                                set.Collection, set.Index, set.Element,
                            }, null);
                        break;
                    }
                }
            }
        }
    }
}
