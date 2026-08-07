using LatteCompiler.Bil;

namespace LatteCompiler
{
    internal sealed class YieldEmitter : EmitVisitor<YieldEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var yield = (LoweredYieldStatement)node;
            var alarm = yield.Alarm == null ? null
                : EmitValueDispatcher.Visit(yield.Alarm, target, ctx, env);
            target.Instructions.Add(new YieldInstruction(alarm) { Origin = yield });
            return Unit.Value;
        }
    }
}
