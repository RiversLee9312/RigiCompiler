namespace RigiCompiler
{
    // 类型引用初次解析早于约束与 wrapper 应用；能力容器必须在两者就绪后
    // 复核声明签名，否则只写 AtomicList<非Serializable> 的参数会漏检。
    internal sealed class AtomicContainerConstraintChecker : ResolverVisitor<AtomicContainerConstraintChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                switch (entry.Symbol)
                {
                    case FieldSymbol field:
                        Check(field.FieldType, entry.Node.Span, env);
                        break;
                    case MethodSymbol method:
                        Check(method.ReturnType, entry.Node.Span, env);
                        foreach (var parameter in method.Parameters)
                            Check(parameter.Type, entry.Node.Span, env);
                        break;
                    case TypeSymbol type:
                        Check(type.BaseType, entry.Node.Span, env);
                        break;
                }
            }
        }

        private static void Check(SemanticSymbol? symbol, CharRange? span, ResolveEnvironment env)
        {
            if (symbol is not TypeSymbol { ConstructedFrom: { } definition, TypeArguments: { } arguments }) return;
            if (definition.GenericParameters.Any(parameter => parameter.RequiresSharedSafe))
                GenericConstraints.CheckArguments(arguments, definition.GenericParameters,
                    span, env.Unit.Symbols, env.Error);
            foreach (var argument in arguments) Check(argument, span, env);
        }
    }
}
