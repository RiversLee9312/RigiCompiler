namespace LatteCompiler
{
    // 函数级绑定上下文（VISITOR_REWRITE.md §3）：一个函数体绑定期间存活的
    // 可变状态。每个函数体新建一个实例（旧 BindSession 的「防御性清空」
    // 正是状态污染证据——对象化后新建即清空，无需 Clear）。
    internal sealed class BindContext : IFlowContext
    {
        public BindContext(MethodSymbol method, FileContext fileCtx, TypeSymbol? declaringType,
            bool isDefaultValueContext = false)
        {
            Method = method;
            FileCtx = fileCtx;
            DeclaringType = declaringType;
            IsDefaultValueContext = isDefaultValueContext;
        }

        // 当前函数上下文
        public MethodSymbol Method { get; }

        public FileContext FileCtx { get; }

        public TypeSymbol? DeclaringType { get; }

        // 默认值表达式绑定上下文（S8d，SYNTAX §4.2）：声明点作用域——
        // 看不到函数形参、视同静态上下文（无 this）；仅默认值绑定
        // （BindingDriver.BindOneParameterDefault 统一入口，含调用点懒触发）置位
        public bool IsDefaultValueContext { get; }

        // 当前上下文是否有 this receiver（实例方法/ext 方法体内；
        // 默认值表达式上下文视同静态——三处实例上色判定统一走此属性）
        public bool HasThis => Method.Owner != null && !Method.IsStatic && !IsDefaultValueContext;

        // 函数内全部局部符号（含值块/合成之外的源级声明），BoundFunctionBody.Locals 来源
        public List<LocalSymbol> Locals { get; } = new List<LocalSymbol>();

        // 流分析状态（DA；S8b 收窄表将长在这里）
        public FlowState Flow { get; } = new FlowState();

        // 值块标签栈（S7b）：绑定值块分支体时压入施工壳，return@标签 沿栈
        // 从内向外查找命中（引用相等即身份）。LoopDepth = 值块创建时的循环栈
        // 深度（S7c-1）：return@ 命中时若当前循环更深，说明隔着循环边界——
        // P4a 脱糖无法表达「跳出中间循环」，P3 拦截为诊断（S7c 技术债）
        public Stack<(BoundValueBlock Block, int LoopDepth)> ValueBlocks { get; } =
            new Stack<(BoundValueBlock, int)>();

        // 循环标签栈（S7c-1）：绑定循环体前压入施工壳（Label 可空），
        // break/continue 沿栈从内向外查找命中（引用相等即身份）；
        // 穿透值块命中外层循环合法（BIL §16.5 动态结构作用域）
        public Stack<BoundLoop> Loops { get; } = new Stack<BoundLoop>();

        // switch pattern 占位栈（S7d）：绑定含 _ 的 case 匹配表达式期间压入
        // 所属 switch 的 selector 表达式，路径绑定中单段名 _ 命中栈顶
        // （嵌套 switch 逐层向内命中）；仅匹配表达式绑定期间存活，分支体无
        // _ 语义（SYNTAX §7.2）
        public Stack<BoundExpression> SwitchSelectors { get; } = new Stack<BoundExpression>();
    }
}
