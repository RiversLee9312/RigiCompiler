namespace RigiCompiler
{
    // 函数级降级上下文（P4a）：一个函数体降级期间存活的可变状态。
    // 每个函数体新建一个实例（同 BindContext 原则——新建即清空）。
    //
    // 组件化结构（M65 Lowering 侧组件化拆分，同 BindContext 先例）：
    // 本类是组合根，职责按隔离需求拆为组件——Synth（合成局部工厂：
    // .sN/.bN 命名与登记 + 合成值引用）、Output（前置语句机制：当前块
    // 输出语句列表栈）、Targets（降级目标状态：循环/switch 占位/安全
    // 访问占位三映射栈）、ExitTargets（return@ 目标映射表，Stage B）；
    // Method 留根部。分派器与 visitor 的 TContext 保持本类型不变
    // （递归透传约束），组件随组合根同生同灭。
    internal sealed class LowerContext
    {
        public LowerContext(MethodSymbol method)
        {
            Method = method;
        }

        // 当前函数的方法符号（return 语句 cast 物化取声明返回类型用）
        public MethodSymbol Method { get; }

        // 合成局部工厂（.sN/.bN 命名与登记 + 合成值引用 ReferenceTo）
        public SynthLocalFactory Synth { get; } = new SynthLocalFactory();

        // 前置语句输出状态（当前块输出语句列表栈）
        public LowerOutputState Output { get; } = new LowerOutputState();

        // 降级目标状态（循环/switch 占位/安全访问占位三映射栈）
        public LowerTargetState Targets { get; } = new LowerTargetState();

        // return@ 目标映射表（Stage B）：source-level exit 目标 →
        // （结果局部, 目标 region breakId），供 StructuredExitRouting
        // pass 展开标记
        public StructuredExitTargetTable ExitTargets { get; } =
            new StructuredExitTargetTable();

        // 闭包存储计划（SYNTAX §5.2；LoweringDriver 在体降级前构建——
        // 被捕获局部/参数的 cell 化与 lambda 体内捕获访问的唯一判定表；
        // 无闭包语境时为「空计划」（全部查询走默认路径）
        public ClosureStoragePlan Closure { get; internal set; } = null!;
    }
}
