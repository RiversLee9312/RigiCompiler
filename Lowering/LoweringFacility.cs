namespace LatteCompiler
{
    // 降级共享设施（自旧 LowerSession 迁移，行为不变）：实参降级与子类型
    // cast 物化（ARCH §6.1，BIL §6.5）。
    internal static class LoweringFacility
    {
        // 实参降级：逐实参递归降级 + 按形参类型的 cast 物化
        // （parameters 为 null = 无显式 init 的零参构造等无形参场景）。
        // S9d：可变参数包实参（BoundVarArgsArgument）直通不 cast——
        // 打包与装箱归 P4b（包是隐藏参数形态，元素类型不是形参类型）
        public static List<LoweredExpression>? LowerArguments(
            IReadOnlyList<BoundExpression> arguments, IReadOnlyList<ParameterSymbol>? parameters,
            LowerContext ctx, LowerEnvironment env)
        {
            var result = new List<LoweredExpression>();
            for (int i = 0; i < arguments.Count; i++)
            {
                var lowered = LowerExpressionDispatcher.Visit(arguments[i], ctx, env);
                if (lowered == null) return null;
                if (lowered is LoweredVarArgsArgument)
                {
                    result.Add(lowered);
                    continue;
                }
                result.Add(EnsureDeclaredType(arguments[i], lowered, parameters?[i].Type));
            }
            return result;
        }

        // 子类型 cast 物化（ARCH §6.1，BIL §6.5）：值的静态类型 ≠ 声明
        // 类型时包显式 cast——P3 已保证方向为子类型（装箱/基类视图均属
        // §12.1 内建引用视图转换）；类型相同、目标非 TypeSymbol（泛型
        // 参数归 S9）或任一侧 ErrorType（毒化静默）时直通
        public static LoweredExpression EnsureDeclaredType(BoundNode origin,
            LoweredExpression value, SemanticSymbol? declaredType)
        {
            if (declaredType is not TypeSymbol target) return value;
            if (ReferenceEquals(value.Type, target)) return value;
            if (value.Type is ErrorTypeSymbol || target is ErrorTypeSymbol) return value;
            return new LoweredCastExpression(origin, value, target, isSafe: false, target);
        }
    }
}
