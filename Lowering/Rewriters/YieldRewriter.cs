namespace LatteCompiler
{
    internal sealed class YieldRewriter
        : LoweredVisitor<YieldRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var yield = (BoundYieldStatement)node;
            if (yield.Alarm == null) return new LoweredYieldStatement(yield, null);
            var alarm = LowerExpressionDispatcher.Visit(yield.Alarm, ctx, env);
            return alarm == null ? null : new LoweredYieldStatement(yield, alarm);
        }
    }
}
