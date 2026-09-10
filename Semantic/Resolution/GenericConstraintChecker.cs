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
                    // 约束边界不得引用同一声明泛型参数列表中的参数
                    // （§3.6 明文禁止，含嵌套泛型实参位置）——在边界解析前
                    // 拦截，替代「Unresolved type or namespace」通用报错
                    if (BoundReferencesOwnParameter(constraint.Bound.TypeSymbol.symbol,
                        parameters!, out var referenced))
                    {
                        env.Error(constraint.Bound.Span ?? constraint.Span ?? entry.Node.Span,
                            $"Constraint bound of '{parameter.Name}' cannot reference generic " +
                            $"parameter '{referenced}' of the same declaration (§3.6)");
                        continue;
                    }
                    var bound = env.ResolveTypeReference(constraint.Bound, entry);
                    if (bound is ErrorTypeSymbol) continue;    // 毒化静默
                    // 声明侧访问控制（§16，S8e；F1/V-A 起递归口径）：约束边界
                    // 引用即使用点——构造边界（Box\<Hidden\>）递归实参，命中
                    // 报最深不可见者
                    var inaccessibleBound = AccessChecker.FindInaccessibleType(bound,
                        entry.Context.File, entry.Context.Namespace, entry.DeclaringType);
                    if (inaccessibleBound != null)
                    {
                        env.Error(constraint.Bound.Span ?? constraint.Span ?? entry.Node.Span,
                            AccessChecker.InaccessibleMessage(inaccessibleBound));
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
                    if (bound is TypeSymbol { ConstructedFrom: not null } constructed)
                        env.TypeFillIns.Add((constructed, constraint.Bound.Span ?? entry.Node.Span));
                }
            }
        }

        // 边界符号路径是否引用参数列表中的泛型参数：单段裸名命中即引用
        //（与 NameResolver 的泛型参数查找口径一致——仅单段裸名）；嵌套
        // 泛型实参递归（List\<T2\> 的 T2 同禁）
        private static bool BoundReferencesOwnParameter(Symbol symbol,
            IReadOnlyList<GenericParameterSymbol> parameters, out string referenced)
        {
            referenced = "";
            if (symbol.elements.Count == 1
                && parameters.Any(p => p.Name == symbol.elements[0].name))
            {
                referenced = symbol.elements[0].name;
                return true;
            }
            foreach (var element in symbol.elements)
            {
                foreach (var generic in element.generics)
                {
                    // 实参是完整类型引用（g1）：可空后缀不影响「引用裸名」判定，
                    // 递归其符号路径即可（T? 的 T 同禁）
                    if (BoundReferencesOwnParameter(generic.TypeSymbol.symbol, parameters, out referenced))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
