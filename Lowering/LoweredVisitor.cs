namespace LatteCompiler
{
    // 降级 visitor 的 CRTP 协议基类（VISITOR_REWRITE.md §6，仿 Binder 协议）：
    // 静态 Visit 为唯一入口——创建子类实例、模板化管理生命周期
    // （Enter/Exit 配对，outputStack/映射栈压弹在此固化），子类只实现 VisitCore。
    // 上行合成：返回降级产物；失败经 LowerEnvironment.Error 落诊断并返回 null
    // （调用方放弃整个函数体——遇未覆盖节点报 P4 Error 并跳过，§8）。
    internal abstract class LoweredVisitor<TSelf, TResult, TContext>
        where TSelf : LoweredVisitor<TSelf, TResult, TContext>, new()
    {
        public static TResult? Visit(BoundNode node, TContext ctx, LowerEnvironment env)
        {
            var visitor = new TSelf();
            try
            {
                visitor.Enter(node, ctx, env);
                return visitor.VisitCore(node, ctx, env);
            }
            finally
            {
                visitor.Exit(node, ctx, env);
            }
        }

        protected abstract TResult? VisitCore(BoundNode node, TContext ctx, LowerEnvironment env);

        protected virtual void Enter(BoundNode node, TContext ctx, LowerEnvironment env) { }

        protected virtual void Exit(BoundNode node, TContext ctx, LowerEnvironment env) { }
    }
}
