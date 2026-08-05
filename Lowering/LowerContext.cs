namespace LatteCompiler
{
    // 函数级降级上下文（P4a）：一个函数体降级期间存活的可变状态。
    // 每个函数体新建一个实例（同 BindContext 原则——新建即清空）。
    //
    // 组件化结构（M65 Lowering 侧组件化拆分，同 BindContext 先例）：
    // 本类是组合根，职责按隔离需求拆为组件——Synth（合成局部工厂：
    // .sN/.bN 命名与登记 + 合成值引用）、Output（前置语句机制：当前块
    // 输出语句列表栈）、Targets（降级目标状态：值块/循环/switch 占位/
    // 语句 seq/安全访问占位五映射栈）；Method 与 TransformFailed 留
    // 根部。分派器与 visitor 的 TContext 保持本类型不变（递归透传
    // 约束），组件随组合根同生同灭。
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

        // 降级目标状态（值块/循环/switch 占位/语句 seq/安全访问占位
        // 五映射栈）
        public LowerTargetState Targets { get; } = new LowerTargetState();

        // 编织拦截失败标记（S7e）：try+finally 部分终止编织拦截在
        // TransformWithContinuation 深处触发（void 链路无法返回值传播），
        // 置位后值块降级放弃产物——诊断已落袋，函数体跳过
        public bool TransformFailed { get; set; }
    }
}
