namespace LatteCompiler
{
    // 流分析状态（M55 visitor 化协议）：definite assignment（M41 最小版；
    // S7b 分支合并；S7c-1 循环两规则）。S8b 的 smart cast 收窄事实表将长在
    // 这里——收窄与 DA 同为流敏感事实，同生命周期分叉/合并/恢复
    // （SYNTAX §3.5）。
    //
    // 分叉/合并原语（迁移自旧 BindSession 的内联代码，语义不变）：
    //   before = Snapshot() → 绑定分支体 → tail = Snapshot() → Restore(before)
    //   if 双分支合并：tailT ∩ tailF ∪ before；switch 多分支：before ∪ (∩ 全尾)；
    //   无 else：保守 Restore(before)；while 后 = before；do-while 后 = 体尾。
    internal sealed class FlowState
    {
        // 已赋值局部变量集合（参数恒已赋值，不入集合——读检查只查局部声明）
        private readonly HashSet<LocalSymbol> assigned = new HashSet<LocalSymbol>();

        public bool IsAssigned(LocalSymbol local)
        {
            return assigned.Contains(local);
        }

        public void MarkAssigned(LocalSymbol local)
        {
            assigned.Add(local);
        }

        // 分叉：当前集快照（分支入口前的 before 集）
        public HashSet<LocalSymbol> Snapshot()
        {
            return new HashSet<LocalSymbol>(assigned);
        }

        // 恢复：回到快照状态（分支绑定后回到 before，或合并后落地）
        public void Restore(HashSet<LocalSymbol> snapshot)
        {
            assigned.Clear();
            assigned.UnionWith(snapshot);
        }

        // if 双分支合并：before ∪ (tailT ∩ tailF)
        public void MergeIfBranches(HashSet<LocalSymbol> before, HashSet<LocalSymbol> tailTrue,
            HashSet<LocalSymbol> tailFalse)
        {
            tailTrue.IntersectWith(tailFalse);
            tailTrue.UnionWith(before);
            Restore(tailTrue);
        }

        // switch 多分支合并（if 双分支规则的推广）：before ∪ (∩ 各分支尾集合)
        public void MergeBranches(HashSet<LocalSymbol> before, List<HashSet<LocalSymbol>> branchTails)
        {
            if (branchTails.Count == 0)
            {
                Restore(before);
                return;
            }
            var merged = new HashSet<LocalSymbol>(branchTails[0]);
            for (int i = 1; i < branchTails.Count; i++)
            {
                merged.IntersectWith(branchTails[i]);
            }
            merged.UnionWith(before);
            Restore(merged);
        }

        // ===== S8b smart cast 收窄事实表（SYNTAX §3.5）=====
        // 与 DA 同生命周期的流敏感事实：分叉（Snapshot/Restore）、边覆盖
        // （ApplyNarrow）、失效（ClearRoot/赋值点）、合并（**纯交集**——
        // 收窄表非单调（失效会移除键），不能用 DA 的 before∪∩ 规则：
        // before 中被分支内失效的键不得复活；before 未动的键两尾集都含，
        // 交集天然保留）

        private readonly Dictionary<NarrowKey, TypeSymbol> narrowed =
            new Dictionary<NarrowKey, TypeSymbol>();

        // 查询收窄：命中返回收窄类型（引用绑定点包装 BoundSmartCastExpression）
        public TypeSymbol? LookupNarrow(NarrowKey key)
        {
            return narrowed.TryGetValue(key, out var type) ? type : null;
        }

        // 边事实覆盖（同键覆盖当前表）
        public void ApplyNarrow(IReadOnlyDictionary<NarrowKey, TypeSymbol> facts)
        {
            foreach (var (key, type) in facts)
            {
                narrowed[key] = type;
            }
        }

        // 单条收窄（switch 分支体入口等场景）
        public void SetNarrow(NarrowKey key, TypeSymbol type)
        {
            narrowed[key] = type;
        }

        // 收窄失效：var 局部/参数被赋值（含复合赋值）时清除根为该符号的
        // 全部键（根本身 + 以其为根的字段链）
        public void ClearRoot(SemanticSymbol symbol)
        {
            rootScratch.Clear();
            foreach (var key in narrowed.Keys)
            {
                if (ReferenceEquals(key.Root, symbol)) rootScratch.Add(key);
            }
            foreach (var key in rootScratch)
            {
                narrowed.Remove(key);
            }
        }

        private readonly List<NarrowKey> rootScratch = new List<NarrowKey>();

        // 收窄表快照/恢复（与 DA 快照配对使用）
        public Dictionary<NarrowKey, TypeSymbol> SnapshotNarrowed()
        {
            return new Dictionary<NarrowKey, TypeSymbol>(narrowed);
        }

        public void RestoreNarrowed(Dictionary<NarrowKey, TypeSymbol> snapshot)
        {
            narrowed.Clear();
            foreach (var (key, type) in snapshot)
            {
                narrowed[key] = type;
            }
        }

        // 收窄合并（分支汇合）：纯交集——键在两尾集都存在且收窄类型相同
        // （符号引用相等）才保留；结果直接落地为当前表
        public void MergeNarrowed(Dictionary<NarrowKey, TypeSymbol> tailTrue,
            Dictionary<NarrowKey, TypeSymbol> tailFalse)
        {
            var merged = new Dictionary<NarrowKey, TypeSymbol>();
            foreach (var (key, type) in tailTrue)
            {
                if (tailFalse.TryGetValue(key, out var other) && ReferenceEquals(type, other))
                {
                    merged[key] = type;
                }
            }
            RestoreNarrowed(merged);
        }

        // 多分支收窄合并（switch 推广）：键在全部尾集都存在且类型相同
        public void MergeNarrowedBranches(List<Dictionary<NarrowKey, TypeSymbol>> branchTails)
        {
            if (branchTails.Count == 0) return;
            var merged = new Dictionary<NarrowKey, TypeSymbol>(branchTails[0]);
            foreach (var (key, type) in branchTails[0])
            {
                for (int i = 1; i < branchTails.Count; i++)
                {
                    if (!branchTails[i].TryGetValue(key, out var other)
                        || !ReferenceEquals(type, other))
                    {
                        merged.Remove(key);
                        break;
                    }
                }
            }
            RestoreNarrowed(merged);
        }
    }
}
