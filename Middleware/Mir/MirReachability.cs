using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// 调用图可达性（MIR 构建的输入）：从入口 fn 出发沿 invoke 边收闭包，
    /// 产出 MirBuilder 的构建顺序。模块中不可达的 fn（如 bootstrap 预定义
    /// 符号的编译器合成体 toString——它们不进符号段，verifier 以硬编码环境
    /// 闭合）不建 MIR 也不进发射；这同时是模块级死代码消除。可达边目前
    /// 只有 invoke 族（虚调用/interface 派发边随 MW6 进入）。
    /// </summary>
    public static class MirReachability
    {
        // 可达 fn 的 canonical 序（入口优先，BFS 发现序）
        public static IReadOnlyList<string> ResolveBuildOrder(MwContext context)
        {
            var bySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bySymbol.Add(bilFn.Symbol, bilFn);
            }

            var order = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (var bilFn in context.Module.Functions)
            {
                // 无符号段声明的 fn 是预定义符号的合成体（§9.1 verifier 以
                // 硬编码环境豁免），永不为入口；不可达则不建 MIR
                var member = context.Symbols.FindMember(bilFn.Symbol);
                if (member != null && member.HasKeyword(BilKeyword.Entrypoint))
                {
                    queue.Enqueue(bilFn.Symbol);
                }
            }
            while (queue.Count > 0)
            {
                var symbol = queue.Dequeue();
                if (!seen.Add(symbol))
                {
                    continue;
                }
                order.Add(symbol);
                foreach (var edge in DirectCallEdges(context, bySymbol[symbol]))
                {
                    if (bySymbol.ContainsKey(edge))
                    {
                        queue.Enqueue(edge);
                    }
                }
            }
            return order;
        }

        // fn 体内的模块内直接调用边：invoke 族目标经 Binding 判定为
        // DirectCallBinding（native 面/外部声明不构成可达边）。BIL 的
        // block 平铺在 fn 级（region 子块同列），无需递归遍历
        private static IEnumerable<string> DirectCallEdges(MwContext context, BilFunction fn)
        {
            var edges = new List<string>();
            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    var target = inst switch
                    {
                        InvokeInstruction invoke => invoke.Method.Symbol,
                        InvokeNoResultInstruction invokeNoResult => invokeNoResult.Method.Symbol,
                        _ => null,
                    };
                    if (target == null)
                    {
                        continue;
                    }
                    // 目标不在符号段属预定义符号调用（MirBuilder 建 MIR 时
                    // 受控拒绝）；此处只回答"是否模块内直接调用"
                    var member = context.Symbols.FindMember(target);
                    if (member != null && ImplBinder.BindCall(member) is DirectCallBinding)
                    {
                        edges.Add(member.Canonical);
                    }
                }
            }
            return edges;
        }
    }
}
