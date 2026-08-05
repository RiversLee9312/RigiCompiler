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

        // ===== variadic 参数索引访问（BIL §7.1 ABI ↔ P3 体内视角桥接）=====
        // fn .args 的 .vargs.<名> = .array<.any>、.kwargs.<名> =
        // .array<.pair<.string, .any>>，而 P3 体内引用定型 Array\<元素\> /
        // Array\<Pair\<String, T\>\>——容器元素类型两视角不一致：索引节点
        // Type 取 ABI 元素类型（get.array/set.array 与容器声明对齐，
        // §21.3 按容器声明推元素期望），读位置外包拆箱 cast 回 P3 静态
        // 元素类型（下游零适配），写位置按 ABI 元素类型装箱 cast
        // （§6.5——读写两形态与复合赋值共用本组设施）

        // 判定：receiver 为 variadic 参数引用（P4a 产物形态——参数引用
        // 降级为 LoweredValueReferenceExpression）
        public static bool IsVariadicParameterIndex(LoweredIndexExpression indexAccess) =>
            indexAccess.Receiver is LoweredValueReferenceExpression
            { Symbol: ParameterSymbol { IsVariadic: true }
                or ParameterSymbol { IsNamedVariadic: true } };

        // ABI 元素类型：具名包（IsNamedVariadic）→ core::Pair\<String,
        // Any\> 构造（与 .kwargs 隐藏条目元素同型）；位置包（IsVariadic）
        // → .any。named 须先判——具名形态两标记双置（SYNTAX §4.3，
        // named 是 ... 的修饰：ParameterListParserLayer 两标记独立填充）
        public static TypeSymbol VariadicIndexElementType(LoweredIndexExpression indexAccess,
            LowerEnvironment env)
        {
            var anyType = env.Unit.Symbols.Bootstrap.Any;
            var parameter = (ParameterSymbol)
                ((LoweredValueReferenceExpression)indexAccess.Receiver).Symbol;
            if (parameter.IsNamedVariadic)
            {
                return env.Unit.Symbols.GetConstructedType(FindCorePairDefinition(env),
                    env.Unit.Symbols.Bootstrap.String, anyType);
            }
            return anyType;
        }

        // 赋值 place 的 ABI 元素类型（写形态 cast 物化目标）：剥
        // LoweredCastExpression 壳（读形态产物外包的拆箱 cast——P4b
        // AssignmentEmitter 同款剥壳）后命中 variadic 参数索引时返回其
        // Type（即 ABI 元素类型）；否则 null（按原声明类型物化）
        public static TypeSymbol? VariadicIndexAbiTypeOfPlace(LoweredExpression place,
            LowerEnvironment env)
        {
            while (place is LoweredCastExpression castShell) place = castShell.Source;
            return place is LoweredIndexExpression indexAccess
                && IsVariadicParameterIndex(indexAccess)
                ? (TypeSymbol)indexAccess.Type : null;
        }

        // core::Pair 定义查找（.bootstrap.latte 自举，按「名 + 泛型
        // 元数 2」查询——与 Binding/PathVisitors.FindCorePairDefinition
        // 同款；P4b VarArgsEmitter.BootstrapPairDefinition 先例）
        private static TypeSymbol FindCorePairDefinition(LowerEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            return core?.Types.FirstOrDefault(t => t.Name == "Pair"
                && t.GenericParameters.Count == 2)
                ?? throw new CompilerInternalException(
                    "stdlib core::Pair 缺失（variadic 索引 ABI 元素类型依赖）");
        }
    }
}
