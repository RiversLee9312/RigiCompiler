namespace LatteCompiler
{
    // 控制流标签栈集（M65 Bind 侧组件化拆分，自 BindContext 迁出）：
    // 值块/循环/switch 占位/语句 seq 四条标签栈的统一家。裸 Stack 与
    // 栈条目不外泄——压弹与查找一律经本类语义方法（压栈时自记当时
    // 深度）；本类只返回查找结果与深度，不落诊断（诊断在调用方
    // visitor 处落袋，判定口径与原 BindContext 注释一致）。
    internal sealed class BindLabelState
    {
        // 值块标签栈条目（S7b）：施工壳 + 值块创建时的循环栈深度
        // （S7c-1——return@ 命中时若当前循环更深，说明隔着循环边界，
        // P4a 脱糖无法表达「跳出中间循环」，P3 拦截为诊断，S7c 技术债）
        internal readonly struct ValueBlockEntry
        {
            public ValueBlockEntry(BoundValueBlock block, int loopDepth)
            {
                Block = block;
                LoopDepth = loopDepth;
            }

            public BoundValueBlock Block { get; }

            public int LoopDepth { get; }
        }

        // 语句 seq 标签栈条目（M61，SYNTAX §6.1）：施工壳 + 压栈时刻的
        // 循环/值块深度——return@ 命中时隔循环/隔值块拦截用
        internal readonly struct SeqLabelEntry
        {
            public SeqLabelEntry(BoundSeqStatement seq, int loopDepth, int valueBlockDepth)
            {
                Seq = seq;
                LoopDepth = loopDepth;
                ValueBlockDepth = valueBlockDepth;
            }

            public BoundSeqStatement Seq { get; }

            public int LoopDepth { get; }

            public int ValueBlockDepth { get; }
        }

        private readonly Stack<ValueBlockEntry> valueBlocks = new Stack<ValueBlockEntry>();

        private readonly Stack<BoundLoop> loops = new Stack<BoundLoop>();

        private readonly Stack<BoundExpression> switchSelectors = new Stack<BoundExpression>();

        private readonly Stack<SeqLabelEntry> seqLabels = new Stack<SeqLabelEntry>();

        // 当前循环栈深度（return@ 隔循环拦截的比较基准）
        public int LoopDepth => loops.Count;

        // 当前值块栈深度（return@语句seq 隔值块拦截的比较基准）
        public int ValueBlockDepth => valueBlocks.Count;

        // ===== 值块标签栈（S7b，SYNTAX §6.1）=====
        // 绑定值块分支体时压入施工壳（自记当时循环深度），return@标签
        // 沿栈从内向外查找命中（Label 字符串相等；条目 Block 引用相等
        // 即身份）

        public void PushValueBlock(BoundValueBlock block)
        {
            valueBlocks.Push(new ValueBlockEntry(block, loops.Count));
        }

        public void PopValueBlock()
        {
            valueBlocks.Pop();
        }

        // return@标签 查找：从内向外首个 Label 命中；未命中返回 null
        public ValueBlockEntry? FindValueBlock(string label)
        {
            foreach (var entry in valueBlocks)
            {
                if (entry.Block.Label == label) return entry;
            }
            return null;
        }

        // ===== 循环标签栈（S7c-1）=====
        // 绑定循环体前压入施工壳（Label 可空），break/continue 沿栈从
        // 内向外查找命中（引用相等即身份）；穿透值块命中外层循环合法
        // （BIL §16.5 动态结构作用域）

        public void PushLoop(BoundLoop loop)
        {
            loops.Push(loop);
        }

        public void PopLoop()
        {
            loops.Pop();
        }

        // break/continue 目标查找：无标签取栈顶（最内层；栈空 = 循环外
        // 使用，返回 null），有标签从内向外首个 named 命中
        public BoundLoop? FindLoop(string? label)
        {
            if (label == null) return loops.Count > 0 ? loops.Peek() : null;
            foreach (var loop in loops)
            {
                if (loop.Label == label) return loop;
            }
            return null;
        }

        // ===== switch pattern 占位栈（S7d，SYNTAX §7.2）=====
        // 绑定含 _ 的 case 匹配表达式期间压入所属 switch 的 selector
        // 表达式，路径绑定中单段名 _ 命中栈顶（嵌套 switch 逐层向内
        // 命中）；仅匹配表达式绑定期间存活，分支体无 _ 语义

        public void PushSelector(BoundExpression selector)
        {
            switchSelectors.Push(selector);
        }

        public void PopSelector()
        {
            switchSelectors.Pop();
        }

        // 当前 selector（栈顶）；栈空 = _ 不在 pattern 上下文（返回
        // null，调用方落普通查找报未定义名）
        public BoundExpression? CurrentSelector =>
            switchSelectors.Count > 0 ? switchSelectors.Peek() : null;

        // ===== 语句 seq 标签栈（M61，SYNTAX §6.1）=====
        // 绑定 named 语句 seq 体期间压入（自记当时循环/值块深度；
        // 仅显式 named 的语句 seq 可作 return@ 目标——`_` 默认标签
        // 值块专属）

        public void PushSeqLabel(BoundSeqStatement seq)
        {
            seqLabels.Push(new SeqLabelEntry(seq, loops.Count, valueBlocks.Count));
        }

        public void PopSeqLabel()
        {
            seqLabels.Pop();
        }

        // return@语句seq 查找：从内向外首个 Label 命中；未命中返回
        // null（隔循环/隔值块拦截由调用方持条目深度与当前深度比较）
        public SeqLabelEntry? FindSeqLabel(string label)
        {
            foreach (var entry in seqLabels)
            {
                if (entry.Seq.Label == label) return entry;
            }
            return null;
        }
    }
}
