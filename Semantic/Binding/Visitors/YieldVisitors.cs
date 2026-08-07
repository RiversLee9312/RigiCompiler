namespace LatteCompiler
{
    internal sealed class YieldVisitor
        : BinderVisitor<YieldVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var yield = (YieldStatementASTNode)node;
            BoundExpression? alarm = null;
            if (yield.Alarm != null)
            {
                alarm = ExpressionDispatcher.Visit(yield.Alarm.Expression, scope, ctx, env);
                if (alarm == null) return null;
                if (alarm.Type is ErrorTypeSymbol) return null;
                var polling = FindAlarm("PollingAlarm", env);
                var eventAlarm = FindAlarm("EventAlarm", env);
                if (!IsAlarm(alarm.Type, polling, eventAlarm, env))
                {
                    env.Error(yield.Span,
                        $"P3: yield alarm must be assignable to core.coroutine.PollingAlarm or " +
                        $"core.coroutine.EventAlarm, got '{BoundAnalysis.TypeDisplay(alarm.Type)}'");
                    return null;
                }
            }
            ctx.Flow.ClearNarrowed();
            return new BoundYieldStatement(node, alarm);
        }

        private static bool IsAlarm(SemanticSymbol type, TypeSymbol? polling,
            TypeSymbol? eventAlarm, BindEnvironment env)
        {
            if (type is GenericParameterSymbol parameter)
            {
                return GenericConstraints.IsAlarmParameter(parameter, polling, eventAlarm, env)
                    || polling == null && eventAlarm == null;
            }
            // A missing root declaration leaves inheritance undecidable. Preserve
            // the verifier's conservative policy rather than guessing a rejection.
            if (polling == null && eventAlarm == null) return true;
            return polling != null && SymbolLookup.IsAssignable(type, polling, env)
                || eventAlarm != null && SymbolLookup.IsAssignable(type, eventAlarm, env);
        }

        private static TypeSymbol? FindAlarm(string name, BindEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            var coroutine = core?.ChildNamespaces.FirstOrDefault(n => n.Name == "coroutine");
            return coroutine?.Types.FirstOrDefault(t => t.Name == name &&
                t.GenericParameters.Count == 0);
        }
    }
}
