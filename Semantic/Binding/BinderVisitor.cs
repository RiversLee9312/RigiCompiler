namespace RigiCompiler
{
    // 绑定 visitor 的 CRTP 协议基类（M55 定稿，
    // 协议 v2）：静态 Visit 为唯一入口——创建子类实例（new() 约束）、模板化管理
    // 生命周期（Enter/Exit 配对，栈压/弹在此固化，杜绝手工配对泄漏），子类只
    // 实现 VisitCore。
    //
    // 上行合成协议（Binder 与 Parser 下行施工的本质差异）：子表达式绑完才建父节点，
    // 故返回绑定产物；失败经 BindEnvironment.Error 落诊断并返回 null
    // （可恢复诊断模型，M36）。
    //
    // 协议 v2 参数：scope 是绑定遍历的固有下传参数（当前词法环境，随块嵌套
    // 变化——等价 Parser 的施工目标）；表达式另需期望类型下传（null 字面量
    // 定型等），由 ExpressionVisitor 专用基类承载。
    //
    // 生命周期规则：栈类上下文（循环/值块/switch 占位）的压栈必须在 Enter、
    // 弹栈必须在 Exit——finally 保证配对，异常路径不泄漏。
    internal abstract class BinderVisitor<TSelf, TResult, TContext>
        where TSelf : BinderVisitor<TSelf, TResult, TContext>, new()
    {
        public static TResult? Visit(ASTNode node, Scope scope, TContext ctx, BindEnvironment env)
        {
            var visitor = new TSelf();
            try
            {
                visitor.Enter(node, scope, ctx, env);
                return visitor.VisitCore(node, scope, ctx, env);
            }
            finally
            {
                visitor.Exit(node, scope, ctx, env);
            }
        }

        protected abstract TResult? VisitCore(ASTNode node, Scope scope, TContext ctx,
            BindEnvironment env);

        protected virtual void Enter(ASTNode node, Scope scope, TContext ctx, BindEnvironment env) { }

        protected virtual void Exit(ASTNode node, Scope scope, TContext ctx, BindEnvironment env) { }
    }

    // 表达式绑定专用协议（协议 v2）：在通用签名上追加期望类型下传
    // （expectedType——null 字面量定型、return/赋值/实参的目标类型传播）。
    internal abstract class ExpressionVisitor<TSelf, TContext>
        where TSelf : ExpressionVisitor<TSelf, TContext>, new()
    {
        public static BoundExpression? Visit(ASTNode node, Scope scope, TContext ctx,
            BindEnvironment env, TypeSymbol? expectedType = null)
        {
            var visitor = new TSelf();
            try
            {
                visitor.Enter(node, scope, ctx, env);
                return visitor.VisitCore(node, scope, ctx, env, expectedType);
            }
            finally
            {
                visitor.Exit(node, scope, ctx, env);
            }
        }

        protected abstract BoundExpression? VisitCore(ASTNode node, Scope scope, TContext ctx,
            BindEnvironment env, TypeSymbol? expectedType);

        protected virtual void Enter(ASTNode node, Scope scope, TContext ctx, BindEnvironment env) { }

        protected virtual void Exit(ASTNode node, Scope scope, TContext ctx, BindEnvironment env) { }
    }

    // 施工壳填充协议：值块/循环/函数体等「壳先建压栈、体绑完回填」的少数场景
    // （return@/break/continue 需要壳先于体存在以命中标签）。shell 由调用方
    // 创建传入，visitor 原地填充，无返回产物。
    internal abstract class BinderShellVisitor<TSelf, TShell, TContext>
        where TSelf : BinderShellVisitor<TSelf, TShell, TContext>, new()
    {
        public static void VisitInto(ASTNode node, Scope scope, TShell shell, TContext ctx,
            BindEnvironment env)
        {
            var visitor = new TSelf();
            try
            {
                visitor.Enter(node, scope, shell, ctx, env);
                visitor.VisitCoreInto(node, scope, shell, ctx, env);
            }
            finally
            {
                visitor.Exit(node, scope, shell, ctx, env);
            }
        }

        protected abstract void VisitCoreInto(ASTNode node, Scope scope, TShell shell, TContext ctx,
            BindEnvironment env);

        protected virtual void Enter(ASTNode node, Scope scope, TShell shell, TContext ctx,
            BindEnvironment env) { }

        protected virtual void Exit(ASTNode node, Scope scope, TShell shell, TContext ctx,
            BindEnvironment env) { }
    }
}
