namespace RigiCompiler
{
    // return@ 目标映射表（Stage B，return@/Value-Block Structured Exit
    // 重构）：P4a 函数级组件（挂 LowerContext，随组合根同生同灭，
    // 非运行时 flag）——source-level exit 目标（BoundValueBlock /
    // BoundSeqStatement，引用相等身份）→ 脱糖所需的两件套的编译期
    // 登记处：结果局部（return@值块的产值写入处；return@语句seq 为
    // null）与目标 region 的 .breakid 局部。
    // 注册时序：各 region 归属点（if/switch/seq 表达式、named seq
    // 语句、值块 lambda $$call 体）在 breakId 创建后、降级 body 前
    // 注册——体内 exit 的 StructuredExitRouting 展开只查本表。
    // 键用引用相等（SemanticSymbol/BoundNode 均引用相等身份），
    // 线性查找（同 LowerTargetState 各映射栈先例）。
    internal sealed class StructuredExitTargetTable
    {
        private readonly struct Entry
        {
            public Entry(BoundNode target, LocalSymbol? resultLocal, LocalSymbol targetBreakId)
            {
                Target = target;
                ResultLocal = resultLocal;
                TargetBreakId = targetBreakId;
            }

            public BoundNode Target { get; }

            public LocalSymbol? ResultLocal { get; }

            public LocalSymbol TargetBreakId { get; }
        }

        private readonly List<Entry> entries = new List<Entry>();

        // return@值块目标：结果局部 + 目标 region breakId
        public void Register(BoundValueBlock block, LocalSymbol resultLocal,
            LocalSymbol targetBreakId)
        {
            entries.Add(new Entry(block, resultLocal, targetBreakId));
        }

        // return@语句seq 目标：无结果局部（exit 不携值）
        public void Register(BoundSeqStatement seq, LocalSymbol targetBreakId)
        {
            entries.Add(new Entry(seq, null, targetBreakId));
        }

        // 引用相等查找；未命中即内部不变量破坏（P3 已保证 return@
        // 目标合法且在降级上下文内）
        public (LocalSymbol? ResultLocal, LocalSymbol TargetBreakId) Find(BoundNode target)
        {
            foreach (var entry in entries)
            {
                if (ReferenceEquals(entry.Target, target))
                {
                    return (entry.ResultLocal, entry.TargetBreakId);
                }
            }
            throw new CompilerInternalException(
                "return@ 目标未登记（P3 已保证目标在降级上下文内）: "
                + target.GetType().Name);
        }
    }
}
