namespace RigiCompiler
{
    // getAtIndex 声明形状检查（SYNTAX §13.2，Q6）：
    //   operator getAtIndex(index: TIndex): TElement?
    // 恰好 1 个形参（类型由实现自定）、返回类型必须是 Nullable\<TElement\>
    // 构造（含代入口径：实参可为泛型参数——Array\<T\> 内建形态即
    // Nullable\<T\>）。形状违反 = 该运算符在索引读取使用点不可能按
    // 「读元素必得 T?」的空安全语义消费（死声明），在声明处拒绝。
    // ext operator 同路径。setAtIndex（索引写）不在本次变更范围，不查。
    internal sealed class IndexOperatorChecker
        : ResolverVisitor<IndexOperatorChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            var nullableDef = env.Unit.Symbols.Bootstrap.NullableDefinition;
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                if (entry.Symbol is not MethodSymbol
                    { Kind: MethodKind.Operator, Name: "getAtIndex" } method)
                {
                    continue;
                }
                if (method.Parameters.Count != 1)
                {
                    env.Error(entry.Node.Span,
                        $"Operator 'getAtIndex' must have exactly one parameter " +
                        $"(got {method.Parameters.Count})");
                }
                if (method.ReturnType is ErrorTypeSymbol) continue;
                if (!IsNullableConstruction(method.ReturnType, nullableDef))
                {
                    env.Error(entry.Node.Span,
                        "Operator 'getAtIndex' must return a nullable type " +
                        "(T? = Nullable<T>): 索引读取一律返回 T?（§13.2）");
                }
            }
        }

        // 必须是 Nullable\<T\> 构造（定义引用相等——Nullable 是 bootstrap
        // 内建，无 stdlib 缺席兜底场景）
        private static bool IsNullableConstruction(SemanticSymbol? returnType,
            TypeSymbol nullableDef)
        {
            return returnType is TypeSymbol type
                && type.TypeArguments is { Count: 1 }
                && ReferenceEquals(type.ConstructedFrom, nullableDef);
        }
    }
}
