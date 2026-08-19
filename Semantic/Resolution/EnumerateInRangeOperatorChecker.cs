namespace RigiCompiler
{
    // EnumerateInRange 声明形状检查（SYNTAX §7.3/§13.2）：
    //   operator EnumerateInRange(end: TEnd): core.collections.IEnumerable\<T>
    // 恰好 1 个形参（类型由实现自定）、返回必须是 IEnumerable\<T> 构造
    // （含代入口径：实参可为泛型参数）。形状违反 = 该运算符在范围循环
    // 使用点不可能按协议迭代（死声明），在声明处拒绝。ext operator 同路径。
    internal sealed class EnumerateInRangeOperatorChecker
        : ResolverVisitor<EnumerateInRangeOperatorChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            var enumerableDef = FindEnumerableDefinition(env);
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                if (entry.Symbol is not MethodSymbol
                    { Kind: MethodKind.Operator, Name: "EnumerateInRange" } method)
                {
                    continue;
                }
                if (method.Parameters.Count != 1)
                {
                    env.Error(entry.Node.Span,
                        $"Operator 'EnumerateInRange' must have exactly one parameter " +
                        $"(got {method.Parameters.Count})");
                }
                if (method.ReturnType is ErrorTypeSymbol) continue;
                if (!IsEnumerableConstruction(method.ReturnType, enumerableDef))
                {
                    env.Error(entry.Node.Span,
                        "Operator 'EnumerateInRange' must return " +
                        "core.collections.IEnumerable<T>");
                }
            }
        }

        // 与 LoopVisitors.FindCollectionType 同口径：符号图查 core.collections.IEnumerable
        private static TypeSymbol? FindEnumerableDefinition(ResolveEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            var collections = core?.ChildNamespaces
                .FirstOrDefault(n => n.Name == "collections");
            return collections?.Types.FirstOrDefault(t => t.Name == "IEnumerable");
        }

        // 必须是 IEnumerable\<T> 构造（定义引用相等；stdlib 缺席时按名兜底）
        private static bool IsEnumerableConstruction(SemanticSymbol? returnType,
            TypeSymbol? enumerableDef)
        {
            if (returnType is not TypeSymbol type
                || type.TypeArguments is not { Count: 1 })
            {
                return false;
            }
            if (enumerableDef != null)
            {
                return ReferenceEquals(type.ConstructedFrom, enumerableDef);
            }
            return type.Name == "IEnumerable" && type.ConstructedFrom != null;
        }
    }
}
