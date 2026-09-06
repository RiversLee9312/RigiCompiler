namespace RigiCompiler
{
    internal sealed class PlaceOfVisitor : ExpressionVisitor<PlaceOfVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var syntax = (PlaceOfExpressionASTNode)node;
            var operand = ExpressionDispatcher.Visit(syntax.Operand.Expression, scope, ctx, env);
            if (operand == null || operand.Type is ErrorTypeSymbol) return null;
            var definition = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core")?.Types
                .FirstOrDefault(t => t.Name == "Place" && t.GenericParameters.Count == 1);
            if (definition?.SourceFile?.IsCompilerLibrary != true)
            {
                env.Error(node.Span, "placeOf requires core.Place from the standard library");
                return null;
            }
            CellStorageInfo? storage = null;
            var dynamicTarget = operand.Type is GenericParameterSymbol
                || operand.Type is TypeSymbol { Kind: TypeKind.Interface }
                || ReferenceEquals(operand.Type, env.B.Any);
            if (dynamicTarget || operand.Type is TypeSymbol { IsValueTypeBranch: true })
            {
                // 与捕获共用幂等提升：重复 placeOf 仍取同一 Cell。
                storage = operand switch
                {
                    BoundValueReferenceExpression { Symbol: LocalSymbol local } =>
                        CellClassFactory.EnsureCellStorage(local, node, ctx, env),
                    BoundValueReferenceExpression { Symbol: ParameterSymbol parameter } =>
                        CellClassFactory.EnsureCellStorage(parameter, node, ctx, env),
                    BoundFieldReferenceExpression field => EnsureGlobalStorage(field.Field, env),
                    _ => null,
                };
                if (storage == null)
                {
                    env.Error(node.Span,
                        "placeOf requires stable value storage; this temporary, field or index cannot preserve cell identity");
                    return null;
                }
            }
            var resultType = env.Unit.Symbols.GetConstructedType(definition, operand.Type);
            return new BoundPlaceOfExpression(node, operand, resultType, storage, dynamicTarget);
        }

        private static CellStorageInfo? EnsureGlobalStorage(FieldSymbol field, BindEnvironment env)
        {
            if (field.CellStorage != null) return field.CellStorage;
            // 全局 Cell 自身是单例，已有初始化/读写降级能保证稳定身份。
            // 普通实例字段与尚未落地 companion 的静态字段不能临时装箱。
            if (field.Owner != null || field.SourceFile == null
                || env.Declarations.DeclarationOf(field) is not VariableDeclarationASTNode declaration)
                return null;
            return CellClassFactory.EnsureCellStorage(field, declaration,
                env.Declarations.FileContextOf(field.SourceFile), env);
        }
    }
}
