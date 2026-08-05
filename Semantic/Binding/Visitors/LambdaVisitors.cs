namespace LatteCompiler
{
    // lambda 表达式（SYNTAX §5.1）：绑定归 S13（await/lambda lowering 专项）。
    // 本 visitor 只做 S8f async 边界闸门 4（async lambda 捕获变量检查——
    // SYNTAX §4.5 "在 async lambda 处检查 4"，仅分析侧），随后落 S13 归口
    // 诊断并返回 null（lambda 不落 BoundTree）。
    internal sealed class LambdaVisitor : ExpressionVisitor<LambdaVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var lambda = (LambdaExpressionASTNode)node;
            if (lambda.IsAsync)
            {
                AsyncGates.CheckLambdaCaptures(lambda, scope, ctx, env);
            }
            env.Error(node.Span, "P3: lambda expressions are not supported yet (S13)");
            return null;
        }
    }
}
