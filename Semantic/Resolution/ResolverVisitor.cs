namespace RigiCompiler
{
    // P2 阶段 visitor 基类（M55 visitor 化协议同款：静态 Visit 唯一入口 +
    // Enter/Exit 生命周期 finally 配对）。P2 的遍历是「阶段 × 条目平铺」，
    // 不同于 Binder 的深递归：每阶段一个子类，VisitCore 内自行遍历条目。
    internal abstract class ResolverVisitor<TSelf> where TSelf : ResolverVisitor<TSelf>, new()
    {
        public static void Visit(ResolveEnvironment env)
        {
            var visitor = new TSelf();
            try
            {
                visitor.Enter(env);
                visitor.VisitCore(env);
            }
            finally
            {
                visitor.Exit(env);
            }
        }

        protected abstract void VisitCore(ResolveEnvironment env);

        protected virtual void Enter(ResolveEnvironment env) { }

        protected virtual void Exit(ResolveEnvironment env) { }
    }
}
