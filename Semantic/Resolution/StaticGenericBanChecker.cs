namespace RigiCompiler
{
    // 静态成员 × 类级泛型：声明侧禁令（SYNTAX §9.2.3）。
    // Ban 1 签名：静态字段类型、静态方法形参/返回/约束不得提及所属类型链
    // 上的类型参数。Ban 2：singleton 不得声明类型参数；仅含静态成员的类型
    // 不得声明类型参数（Rigi 无 static class，此为等价形态）。
    internal sealed class StaticGenericBanChecker : ResolverVisitor<StaticGenericBanChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph || entry.Symbol is not TypeSymbol type || type.IsBuiltin)
                {
                    continue;
                }
                CheckTypeDeclaration(type, entry, env);
            }
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                if (entry.Symbol is FieldSymbol { IsStatic: true } field)
                {
                    CheckStaticField(field, entry, env);
                }
                else if (entry.Symbol is MethodSymbol { IsStatic: true } method)
                {
                    CheckStaticMethod(method, entry, env);
                }
            }
        }

        private static void CheckTypeDeclaration(TypeSymbol type, DeclEntry entry,
            ResolveEnvironment env)
        {
            if (type.GenericParameters.Count == 0) return;
            if (type.IsSingleton)
            {
                env.Error(entry.Node.Span,
                    string.Format(StaticGenericRules.SingletonGenericMessage, type.Name));
                return;
            }
            if (type.Kind == TypeKind.Interface) return;
            var hasUserMember = false;
            var hasInstanceMember = false;
            foreach (var field in type.Fields)
            {
                if (!StaticGenericRules.IsUserMember(field)) continue;
                hasUserMember = true;
                if (!field.IsStatic) hasInstanceMember = true;
            }
            foreach (var method in type.Methods)
            {
                if (!StaticGenericRules.IsUserMember(method)) continue;
                hasUserMember = true;
                if (!method.IsStatic) hasInstanceMember = true;
            }
            if (hasUserMember && !hasInstanceMember)
            {
                env.Error(entry.Node.Span,
                    string.Format(StaticGenericRules.StaticOnlyGenericMessage, type.Name));
            }
        }

        private static void CheckStaticField(FieldSymbol field, DeclEntry entry,
            ResolveEnvironment env)
        {
            var owner = field.Owner ?? entry.DeclaringType;
            if (owner == null || owner.IsBuiltin) return;
            StaticGenericRules.CheckEnclosingTypeParameterUse(field.FieldType, owner,
                entry.Node.Span, env.Error);
        }

        private static void CheckStaticMethod(MethodSymbol method, DeclEntry entry,
            ResolveEnvironment env)
        {
            var owner = method.Owner ?? entry.DeclaringType;
            if (owner == null || owner.IsBuiltin) return;
            var span = entry.Node.Span;
            if (StaticGenericRules.CheckEnclosingTypeParameterUse(method.ReturnType, owner,
                span, env.Error))
            {
                return;
            }
            foreach (var parameter in method.Parameters)
            {
                if (StaticGenericRules.CheckEnclosingTypeParameterUse(parameter.Type, owner,
                    span, env.Error))
                {
                    return;
                }
            }
            foreach (var genericParameter in method.GenericParameters)
            {
                foreach (var constraint in genericParameter.Constraints)
                {
                    if (StaticGenericRules.CheckEnclosingTypeParameterUse(constraint.Bound, owner,
                        span, env.Error))
                    {
                        return;
                    }
                }
            }
        }
    }
}
