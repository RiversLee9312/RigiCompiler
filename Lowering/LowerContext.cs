namespace LatteCompiler
{
    // 函数级降级上下文（P4a）：一个函数体降级期间存活的可变状态。
    // 每个函数体新建一个实例（同 BindContext 原则——新建即清空）。
    internal sealed class LowerContext
    {
        public LowerContext(MethodSymbol method)
        {
            Method = method;
        }

        // 当前函数的方法符号（return 语句 cast 物化取声明返回类型用）
        public MethodSymbol Method { get; }

        // 合成局部（.sN，BIL §5.1 编译器保留名，函数内唯一）与 .breakid
        // 局部（.bN）；收尾追加进 LoweredFunctionBody.Locals
        public List<LocalSymbol> SynthLocals { get; } = new List<LocalSymbol>();

        public int SynthCount { get; set; }

        public int BreakIdCount { get; set; }

        // 前置语句机制：当前块输出语句列表栈。块降级为每块建输出列表压栈；
        // 表达式降级向栈顶列表追加前置语句，自然排在属主语句之前
        public Stack<List<LoweredStatement>> OutputStack { get; } =
            new Stack<List<LoweredStatement>>();

        // 值块目标映射栈：BoundValueBlock → 写入局部（引用相等查找），供嵌套
        // return@（穿透外层值块）命中外层映射
        public Stack<(BoundValueBlock Block, LocalSymbol Target)> ValueBlocks { get; } =
            new Stack<(BoundValueBlock, LocalSymbol)>();

        // 循环映射栈（S7c-1）：BoundLoop → 合成 .breakid 局部（引用相等
        // 查找），进循环压栈、出循环弹栈；BoundLoopControl 经 Target
        // 引用查映射得 BreakId
        public Stack<(BoundLoop Loop, LocalSymbol BreakId)> Loops { get; } =
            new Stack<(BoundLoop, LocalSymbol)>();

        // switch pattern 占位映射栈（S7d）：pattern 降级期间所属 switch 的
        // selector 表达式 → selector 物化局部（引用相等查找，嵌套 switch
        // 逐层向内命中）
        public Stack<(BoundExpression Selector, LocalSymbol Temp)> SwitchTemps { get; } =
            new Stack<(BoundExpression, LocalSymbol)>();

        // 安全访问占位映射栈（S7f）：BoundSafeAccessReceiverExpression 实例 →
        // （物化 receiver 局部, unwrap 目标类型）——引用相等查找，嵌套安全
        // 访问（a?.b?.c）逐层向内命中；占位降级为 unwrap cast（§12.1）
        public Stack<(BoundSafeAccessReceiverExpression Placeholder, LocalSymbol Receiver,
            TypeSymbol UnwrapType)> SafeReceivers { get; } =
            new Stack<(BoundSafeAccessReceiverExpression, LocalSymbol, TypeSymbol)>();

        // 编织拦截失败标记（S7e）：try+finally 部分终止编织拦截在
        // TransformWithContinuation 深处触发（void 链路无法返回值传播），
        // 置位后值块降级放弃产物——诊断已落袋，函数体跳过
        public bool TransformFailed { get; set; }

        // 合成局部（BIL §5.1 编译器保留名 .sN，函数内唯一）
        public LocalSymbol NewSynthLocal(TypeSymbol type)
        {
            var local = new LocalSymbol(".s" + SynthCount, type, isConst: false);
            SynthCount++;
            SynthLocals.Add(local);
            return local;
        }

        // 合成 .breakid 局部（S7c-1，BIL §9.3 capability）：.bN 命名，
        // 函数内唯一；Type 为 null（无对应 TypeSymbol，见 LocalSymbol
        // 注释），emitter 侧 .vars 条目按 .breakid 投影
        public LocalSymbol NewBreakIdLocal()
        {
            var local = new LocalSymbol(".b" + BreakIdCount, null, isConst: false);
            BreakIdCount++;
            SynthLocals.Add(local);
            return local;
        }

        // 合成值引用（Origin 指最近语法来源，ARCH §5.1）
        public static LoweredValueReferenceExpression ReferenceTo(BoundNode origin,
            SemanticSymbol symbol)
        {
            return new LoweredValueReferenceExpression(origin, symbol);
        }
    }
}
