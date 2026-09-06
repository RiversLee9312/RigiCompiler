namespace RigiCompiler
{
    internal sealed class PlaceOfRewriter
        : LoweredVisitor<PlaceOfRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var place = (BoundPlaceOfExpression)node;
            LoweredExpression? target;
            if (place.Storage == null)
                target = LowerExpressionDispatcher.Visit(place.Operand, ctx, env);
            else if (place.Operand is BoundValueReferenceExpression reference)
                target = ctx.Closure.CellObjectFor(place, reference.Symbol);
            else if (place.Operand is BoundFieldReferenceExpression field)
                target = CellStorageLowering.CellObjectOf(place, field.Field, place.Storage, env);
            else
                throw new CompilerInternalException("placeOf 的稳定存储在 P3/P4 间丢失");
            if (target == null) return null;
            var type = (TypeSymbol)place.Type;
            var any = env.Unit.Symbols.Bootstrap.Any;
            var arguments = new List<LoweredExpression>
            {
                new LoweredCastExpression(place, target, any, false, any),
            };
            if (place.HasDynamicTarget)
            {
                // 存储引用无副作用，值读取只求值一次以区分实际对象/值分支。
                var value = LowerExpressionDispatcher.Visit(place.Operand, ctx, env);
                if (value == null) return null;
                arguments.Add(new LoweredCastExpression(place, value, any, false, any));
            }
            arguments.Add(new LoweredConstantExpression(place,
                place.Storage == null ? 0 : place.Storage.IsReadOnly ? 2 : 1,
                env.Unit.Symbols.Bootstrap.Int32));
            var init = (type.ConstructedFrom ?? type).Methods.Single(m =>
                m.Kind == MethodKind.Init && m.Parameters.Count == arguments.Count);
            return new LoweredNewExpression(place, init, arguments, type);
        }
    }
}
