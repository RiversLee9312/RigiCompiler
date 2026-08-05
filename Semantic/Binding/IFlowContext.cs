namespace LatteCompiler
{
    // 流分析上下文方言接口（M55 visitor 化协议「方言 = 同一状态对象的
    // 接口视图」）：初期从粗，仅 FlowState 一面；后续按真实隔离需求从
    // BindContext 拉接口（作用域/标签栈……），禁止切多个独立状态对象。
    internal interface IFlowContext
    {
        FlowState Flow { get; }
    }
}
