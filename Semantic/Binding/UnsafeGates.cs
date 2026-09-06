namespace RigiCompiler
{
    // unsafe 是调用点约束，不参与重载排序；先选定方法，再检查词法上下文。
    internal static class UnsafeGates
    {
        public static void CheckMethod(MethodSymbol method, ASTNode node,
            BindContext ctx, BindEnvironment env)
            => CheckMethod(method, node.Span, ctx, env);

        public static void CheckMethod(MethodSymbol method, CharRange? span,
            BindContext ctx, BindEnvironment env)
        {
            if (!ctx.IsUnsafe && (method.IsUnsafe
                || (method.Owner?.ConstructedFrom ?? method.Owner)?.IsUnsafe == true))
            {
                env.Error(span, $"Unsafe operation '{method.Name}' requires an unsafe context");
            }
        }

        // 动态构造不进行普通重载排序：只检查已知具体类型及按实参静态类型
        // 精确匹配的 init；未知泛型目标继续由既有运行期构造协议处理。
        public static void CheckDynamicConstruction(SemanticSymbol type,
            IReadOnlyList<BoundExpression> arguments, ASTNode node,
            BindContext ctx, BindEnvironment env)
        {
            if (ctx.IsUnsafe || type is not TypeSymbol concrete) return;
            CheckConstruction(concrete, node, ctx, env);
            foreach (var init in (concrete.ConstructedFrom ?? concrete).Methods)
            {
                if (init.Kind != MethodKind.Init || !init.IsUnsafe
                    || init.Name == RigiCompiler.Bil.BilSpellings.InitSerializableMethodName
                    || init.Parameters.Count != arguments.Count) continue;
                var matches = true;
                for (var i = 0; i < arguments.Count; i++)
                {
                    if (init.Parameters[i].Type is not { } parameterType
                        || !ReferenceEquals(arguments[i].Type,
                            SymbolLookup.SubstituteForReceiver(parameterType, init, concrete, env.Unit.Symbols)))
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches) CheckMethod(init, node, ctx, env);
            }
        }
        public static void CheckConstruction(TypeSymbol type, ASTNode node,
            BindContext ctx, BindEnvironment env)
        {
            if (!ctx.IsUnsafe && (type.ConstructedFrom ?? type).IsUnsafe)
            {
                env.Error(node.Span, $"Unsafe construction '{type.Name}' requires an unsafe context");
            }
        }
    }
}
