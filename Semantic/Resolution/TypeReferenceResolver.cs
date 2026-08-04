namespace LatteCompiler
{
    // ===== 子任务 1：类型引用解析（字段 → 方法，init 映射依赖字段类型）=====
    //
    // 自旧 DeclarationResolver.ResolveSession.ResolveTypeReferences/
    // ResolveParameterType 迁移，行为不变。
    internal sealed class TypeReferenceResolver : ResolverVisitor<TypeReferenceResolver>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            // 先字段（init `_ -> field` 省略类型时沿用字段类型，字段须先就绪）
            foreach (var entry in env.Entries)
            {
                if (entry.Node is VariableDeclarationASTNode { TypeAnnotation: not null } v)
                {
                    ((FieldSymbol)entry.Symbol).FieldType = env.ResolveTypeReference(v.TypeAnnotation, entry);
                    CheckAccessible(((FieldSymbol)entry.Symbol).FieldType!,
                        v.TypeAnnotation.Span ?? entry.Node.Span, entry, env);
                }
            }
            foreach (var entry in env.Entries)
            {
                if (entry.Node is not CallableDeclarationASTNode fn) continue;
                var method = (MethodSymbol)entry.Symbol;
                if (fn.ReturnType != null)
                {
                    method.ReturnType = env.ResolveTypeReference(fn.ReturnType, entry);
                    CheckAccessible(method.ReturnType!, fn.ReturnType.Span ?? entry.Node.Span,
                        entry, env);
                }
                for (int i = 0; i < fn.Parameters.Parameters.Count; i++)
                {
                    method.Parameters[i].Type = ResolveParameterType(fn.Parameters.Parameters[i], entry, env);
                    CheckAccessible(method.Parameters[i].Type!,
                        fn.Parameters.Parameters[i].Span ?? entry.Node.Span, entry, env);
                }
                // 默认参数顺序（SYNTAX §4.2）：首个默认值之后的形参必须全部携带默认值
                var seenDefault = false;
                for (int i = 0; i < method.Parameters.Count; i++)
                {
                    if (method.Parameters[i].DefaultValue != null)
                    {
                        seenDefault = true;
                        continue;
                    }
                    if (seenDefault)
                    {
                        env.Error(fn.Parameters.Parameters[i].Span ?? fn.Span,
                            $"Parameter '{method.Parameters[i].Name}' must declare a default value " +
                            "(a preceding parameter has one)");
                    }
                }
            }
        }

        private static SemanticSymbol ResolveParameterType(ParameterASTNode p, DeclEntry entry,
            ResolveEnvironment env)
        {
            if (p.Type.TypeSymbol.symbol.elements.Count > 0)
            {
                return env.ResolveTypeReference(p.Type, entry);
            }
            // 空类型节点仅出现于 init 映射省略类型（§9.3：沿用字段类型）
            if (p.MappedFieldName == null)
            {
                env.Error(p.Span ?? entry.Node.Span, $"Parameter '{p.Name}' is missing a type annotation");
                return env.Unit.Symbols.ErrorType;
            }
            var field = env.FindField(entry.DeclaringType, p.MappedFieldName, out var fieldType);
            if (field == null)
            {
                env.Error(p.Span ?? entry.Node.Span,
                    $"Init parameter mapping targets unknown field: '{p.MappedFieldName}'");
                return env.Unit.Symbols.ErrorType;
            }
            if (fieldType == null)
            {
                env.Error(p.Span ?? entry.Node.Span,
                    $"Init parameter mapping requires field '{p.MappedFieldName}' to have a type annotation");
                return env.Unit.Symbols.ErrorType;
            }
            return fieldType;
        }

        // 声明侧访问控制（SYNTAX §16，S8e）：类型引用命中处即使用点——
        // 与 P3 函数体内检查共用 AccessChecker；毒化/泛型参数由设施内跳过
        private static void CheckAccessible(SemanticSymbol resolved, CharRange? span,
            DeclEntry entry, ResolveEnvironment env)
        {
            if (!AccessChecker.IsTypeAccessible(resolved, entry.Context.File,
                entry.Context.Namespace, entry.DeclaringType))
            {
                env.Error(span, AccessChecker.InaccessibleMessage(resolved));
            }
        }
    }
}
