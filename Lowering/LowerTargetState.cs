namespace LatteCompiler
{
    // 降级目标状态（M65 Lowering 侧组件化拆分，自 LowerContext 迁出）：
    // 值块/循环/switch 占位/语句 seq/安全访问占位五条映射栈的统一家
    // （引用相等查找，嵌套逐层向内命中）。裸 Stack 与栈条目不外泄——
    // 压弹与查找一律经本类语义方法；本类只返回查找结果，不落诊断
    // （未命中的内部错误抛出/P4 诊断落袋都在调用方 Rewriter 处，
    // 文本与位置逐字保持）。
    internal sealed class LowerTargetState
    {
        // 值块目标映射栈条目：BoundValueBlock → 写入局部
        internal readonly struct ValueBlockEntry
        {
            public ValueBlockEntry(BoundValueBlock block, LocalSymbol target)
            {
                Block = block;
                Target = target;
            }

            public BoundValueBlock Block { get; }

            public LocalSymbol Target { get; }
        }

        // 循环映射栈条目（S7c-1）：BoundLoop → 合成 .breakid 局部
        internal readonly struct LoopEntry
        {
            public LoopEntry(BoundLoop loop, LocalSymbol breakId)
            {
                Loop = loop;
                BreakId = breakId;
            }

            public BoundLoop Loop { get; }

            public LocalSymbol BreakId { get; }
        }

        // switch pattern 占位映射栈条目（S7d）：所属 switch 的 selector
        // 表达式 → selector 物化局部
        internal readonly struct SwitchTempEntry
        {
            public SwitchTempEntry(BoundExpression selector, LocalSymbol temp)
            {
                Selector = selector;
                Temp = temp;
            }

            public BoundExpression Selector { get; }

            public LocalSymbol Temp { get; }
        }

        // 安全访问占位映射栈条目（S7f）：BoundSafeAccessReceiverExpression
        // 实例 →（物化 receiver 局部, unwrap 目标类型）；S9 放宽：
        // unwrap 目标可为泛型参数（§7.5 投影）
        internal readonly struct SafeReceiverEntry
        {
            public SafeReceiverEntry(BoundSafeAccessReceiverExpression placeholder,
                LocalSymbol receiver, SemanticSymbol unwrapType)
            {
                Placeholder = placeholder;
                Receiver = receiver;
                UnwrapType = unwrapType;
            }

            public BoundSafeAccessReceiverExpression Placeholder { get; }

            public LocalSymbol Receiver { get; }

            public SemanticSymbol UnwrapType { get; }
        }

        // 值块目标映射栈：BoundValueBlock → 写入局部（引用相等查找），供嵌套
        // return@（穿透外层值块）命中外层映射
        private readonly Stack<ValueBlockEntry> valueBlocks = new Stack<ValueBlockEntry>();

        // 循环映射栈（S7c-1）：BoundLoop → 合成 .breakid 局部（引用相等
        // 查找），进循环压栈、出循环弹栈；BoundLoopControl 经 Target
        // 引用查映射得 BreakId
        private readonly Stack<LoopEntry> loops = new Stack<LoopEntry>();

        // switch pattern 占位映射栈（S7d）：pattern 降级期间所属 switch 的
        // selector 表达式 → selector 物化局部（引用相等查找，嵌套 switch
        // 逐层向内命中）
        private readonly Stack<SwitchTempEntry> switchTemps = new Stack<SwitchTempEntry>();

        // 语句 seq 降级目标栈（M61）：named 语句 seq 降级体期间压入
        // （BoundSeqStatement 施工节点，引用相等即身份）；LoweredSeqExit
        // 标记经栈顶比对归属——命中本层消费、命中外层保留向上传播
        private readonly Stack<BoundSeqStatement> seqTargets = new Stack<BoundSeqStatement>();

        // 安全访问占位映射栈（S7f）：BoundSafeAccessReceiverExpression 实例 →
        // （物化 receiver 局部, unwrap 目标类型）——引用相等查找，嵌套安全
        // 访问（a?.b?.c）逐层向内命中；占位降级为 unwrap cast（§12.1）
        private readonly Stack<SafeReceiverEntry> safeReceivers = new Stack<SafeReceiverEntry>();

        // ===== 值块目标映射栈（S7b）=====

        // 降级值块期间压栈（施工节点 + 写入局部）
        public void PushValueBlock(BoundValueBlock block, LocalSymbol target)
        {
            valueBlocks.Push(new ValueBlockEntry(block, target));
        }

        public void PopValueBlock()
        {
            valueBlocks.Pop();
        }

        // return@ 目标值块映射查找（引用相等）；未命中返回 null——P3 已
        // 保证 return@ 只在值块内，调用方按内部错误抛出
        public LocalSymbol? FindValueBlockTarget(BoundValueBlock valueBlock)
        {
            foreach (var entry in valueBlocks)
            {
                if (ReferenceEquals(entry.Block, valueBlock)) return entry.Target;
            }
            return null;
        }

        // 「符号是某值块的写入目标局部」判定（值块写入 = 终止路径的编织
        // 闸门）：合成局部 .sN 只被值块写入与短路/if/switch 表达式结果
        // 使用——后两者不在映射栈上，互不混淆
        public bool IsValueBlockTarget(SemanticSymbol symbol)
        {
            foreach (var entry in valueBlocks)
            {
                if (ReferenceEquals(symbol, entry.Target)) return true;
            }
            return false;
        }

        // ===== 循环映射栈（S7c-1）=====

        // 进循环压栈（条件/iterable 降级前压入无语义影响——其中不可能有
        // 指向本循环的 BoundLoopControl，P3 不变量）
        public void PushLoop(BoundLoop loop, LocalSymbol breakId)
        {
            loops.Push(new LoopEntry(loop, breakId));
        }

        public void PopLoop()
        {
            loops.Pop();
        }

        // BoundLoopControl.Target 经引用查循环映射栈得 BreakId；未命中
        // 返回 null——P3 已保证目标循环包含该语句（降级上下文必在栈上），
        // 调用方按内部错误抛出
        public LocalSymbol? FindLoopBreakId(BoundLoop target)
        {
            foreach (var entry in loops)
            {
                if (ReferenceEquals(entry.Loop, target)) return entry.BreakId;
            }
            return null;
        }

        // ===== switch pattern 占位映射栈（S7d）=====

        // pattern 降级期间压栈（所属 switch 的 selector 表达式 + 物化局部）
        public void PushSwitchTemp(BoundExpression selector, LocalSymbol temp)
        {
            switchTemps.Push(new SwitchTempEntry(selector, temp));
        }

        public void PopSwitchTemp()
        {
            switchTemps.Pop();
        }

        // BoundSwitchPlaceholderExpression.Selector 经引用查占位映射栈得
        // selector 物化局部；未命中返回 null——P3 已保证 _ 只在 pattern
        // 匹配表达式内（降级上下文必在栈上），调用方按内部错误抛出
        public LocalSymbol? FindSwitchTemp(BoundExpression selector)
        {
            foreach (var entry in switchTemps)
            {
                if (ReferenceEquals(entry.Selector, selector)) return entry.Temp;
            }
            return null;
        }

        // ===== 语句 seq 降级目标栈（M61）=====

        // 语句 seq 降级体期间压栈。所有 seq 降级都压栈（含无名）——exit
        // 归属比对按引用命中「本层」；无名 seq 不压栈会让栈顶指向外层
        // seq，导致外层目标被内层误消费（其后语句漏截断）
        public void PushSeqTarget(BoundSeqStatement seq)
        {
            seqTargets.Push(seq);
        }

        public void PopSeqTarget()
        {
            seqTargets.Pop();
        }

        // return@语句seq 归属判定（栈顶比对）：栈非空且栈顶引用相等即
        // 命中本层（消费）；命中外层保留标记向上传播
        public bool IsCurrentSeqTarget(BoundSeqStatement target)
        {
            return seqTargets.Count > 0 && ReferenceEquals(seqTargets.Peek(), target);
        }

        // ===== 安全访问占位映射栈（S7f）=====

        // 占位映射仅 Access 降级期间存活（try/finally 配对压弹）
        public void PushSafeReceiver(BoundSafeAccessReceiverExpression placeholder,
            LocalSymbol receiver, SemanticSymbol unwrapType)
        {
            safeReceivers.Push(new SafeReceiverEntry(placeholder, receiver, unwrapType));
        }

        public void PopSafeReceiver()
        {
            safeReceivers.Pop();
        }

        // 占位叶子查找（引用相等，嵌套安全访问逐层向内命中）；未命中
        // 返回 null——栈空/未命中 = 内部一致性破坏，诊断在调用方落袋
        public SafeReceiverEntry? FindSafeReceiver(BoundSafeAccessReceiverExpression placeholder)
        {
            foreach (var entry in safeReceivers)
            {
                if (ReferenceEquals(entry.Placeholder, placeholder)) return entry;
            }
            return null;
        }
    }
}
