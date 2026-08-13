namespace RigiCompiler
{
    // ===== 子任务 6：泛型约束声明侧检查 =====
    //
    // 自旧 DeclarationResolver.ResolveSession.CheckGenericConstraints 迁移，行为不变。
    internal sealed class GenericConstraintChecker : ResolverVisitor<GenericConstraintChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                var (list, parameters) = entry switch
                {
                    { Node: ClassDeclarationASTNode c } => (c.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: InterfaceDeclarationASTNode i } => (i.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: StructDeclarationASTNode s } => (s.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: EnumStructDeclarationASTNode e } => (e.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: WrapperDeclarationASTNode w } => (w.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: CallableDeclarationASTNode fn } => (fn.GenericParameters, ((MethodSymbol)entry.Symbol).GenericParameters),
                    _ => (null, null),
                };
                if (list == null) continue;
                foreach (var constraint in list.Constraints)
                {
                    // Target 必须是本声明的泛型参数（§3.6：裸标识符即泛型参数）
                    var targetName = ResolveEnvironment.BareNameOf(constraint.Target);
                    var parameter = targetName == null ? null : parameters!.FirstOrDefault(p => p.Name == targetName);
                    if (parameter == null)
                    {
                        env.Error(constraint.Target.Span ?? constraint.Span ?? entry.Node.Span,
                            "Constraint target must be a generic parameter of this declaration");
                        continue;
                    }
                    var bound = env.ResolveTypeReference(constraint.Bound, entry);
                    if (bound is ErrorTypeSymbol) continue;    // 毒化静默
                    // 声明侧访问控制（§16，S8e）：约束边界引用即使用点
                    if (!AccessChecker.IsTypeAccessible(bound, entry.Context.File,
                        entry.Context.Namespace, entry.DeclaringType))
                    {
                        env.Error(constraint.Bound.Span ?? constraint.Span ?? entry.Node.Span,
                            AccessChecker.InaccessibleMessage(bound));
                        continue;
                    }
                    // with 约束的边界必须是 wrapper 类型（§3.6）
                    if (constraint.Kind == GenericConstraintKind.With &&
                        bound is not TypeSymbol { Kind: TypeKind.Wrapper })
                    {
                        env.Error(constraint.Bound.Span ?? constraint.Span ?? entry.Node.Span,
                            $"'with' constraint bound of '{parameter.Name}' must be a wrapper type");
                        continue;
                    }
                    parameter.Constraints.Add(new GenericConstraintInfo(constraint.Kind, bound));
                }
            }
        }
    }
}
