namespace RigiCompiler
{
    // 降级共享设施（自旧 LowerSession 迁移，行为不变）：实参降级与子类型
    // cast 物化（ARCH §6.1，BIL §6.5）。
    internal static class LoweringFacility
    {
        // 实参降级：逐实参递归降级 + 按形参类型的 cast 物化
        // （parameters 为 null = 无显式 init 的零参构造等无形参场景）。
        // S9d：可变参数包实参（BoundVarArgsArgument）直通不 cast——
        // 打包与装箱归 P4b（包是隐藏参数形态，元素类型不是形参类型）。
        // guard：兄弟求值序保护（EvalOrderGuard）——未传时自建并在返回前
        // Seal（实参间自动获得保护）；传入外部 guard 时只 Track 不 Seal
        //（外层统一 Seal），此时返回值是未封口形态，外层调用方须改用
        // Seal 结果。Track 的必须是最终形态（EnsureDeclaredType 之后）
        public static List<LoweredExpression>? LowerArguments(
            IReadOnlyList<BoundExpression> arguments, IReadOnlyList<ParameterSymbol>? parameters,
            LowerContext ctx, LowerEnvironment env, EvalOrderGuard? guard = null,
            TypeSymbol? parameterConstructedHost = null, MethodSymbol? parameterMethod = null,
            IReadOnlyList<SemanticSymbol>? methodTypeArguments = null)
        {
            var own = guard == null;
            guard ??= new EvalOrderGuard(ctx);
            var result = new List<LoweredExpression>();
            for (int i = 0; i < arguments.Count; i++)
            {
                var lowered = LowerExpressionDispatcher.Visit(arguments[i], ctx, env);
                if (lowered == null) return null;
                if (lowered is LoweredVarArgsArgument)
                {
                    guard.Track(arguments[i], lowered);
                    result.Add(lowered);
                    continue;
                }
                // 构造宿主的形参代入（MW11d-C 修复）：声明侧形参类型里的宿主
                // 泛型参数按构造类型实参代入（QueueItem<TMessage> 的 init 形参
                // .nullable<.generic.T> → .nullable<.generic.TMessage>）——
                // 否则转换目标在当前帧是悬空 .generic 引用（声明类型的泛型
                // 参数名与调用帧不同名时 native 运行期必崩，VM 按名碰巧解析）
                var declaredParamType = parameters?[i].Type;
                // 合成调用可保留声明方法；按符号身份代入方法实参，避免
                // Nullable<T> 中的方法 T 被调用者同名宿主 T 捕获。
                if (declaredParamType != null && parameterMethod != null
                    && methodTypeArguments?.Count == parameterMethod.GenericParameters.Count
                    && methodTypeArguments.Count > 0)
                    declaredParamType = SymbolLookup.SubstituteType(declaredParamType,
                        parameterMethod.GenericParameters, methodTypeArguments, env.Unit.Symbols);
                if (parameterConstructedHost is { ConstructedFrom: not null }
                    && declaredParamType != null)
                {
                    declaredParamType = SymbolLookup.SubstituteHost(declaredParamType,
                        parameterConstructedHost.ConstructedFrom, parameterConstructedHost,
                        env.Unit.Symbols);
                }
                lowered = EnsureDeclaredType(arguments[i], lowered, declaredParamType);
                guard.Track(arguments[i], lowered);
                result.Add(lowered);
            }
            if (own) return new List<LoweredExpression>(guard.Seal());
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

        // 实例方法 receiver 的定型与普通值转换分离：构造类型
        // Host<A> 调用其定义 Host<T> 上的成员时，receiver 已是该
        // 方法的精确宿主实例，不生成 Host<A> -> Host 的擦除 cast。
        // 只有语义符号身份相同才能走此路；同名不同元数（Task / Task<T>）
        // 必然是不同符号。真正的基类/接口/Any 上转仍使用受检 cast。
        public static LoweredExpression EnsureReceiverType(BoundNode origin,
            LoweredExpression value, TypeSymbol? declaredOwner)
        {
            if (declaredOwner == null || value.Type is not TypeSymbol receiverType)
            {
                return EnsureDeclaredType(origin, value, declaredOwner);
            }
            var receiverDefinition = receiverType.ConstructedFrom ?? receiverType;
            var ownerDefinition = declaredOwner.ConstructedFrom ?? declaredOwner;
            if (ReferenceEquals(receiverDefinition, ownerDefinition)) return value;
            // 基类成员调用也必须使用代入后的宿主 B<i32>，不能把
            // C<i32> -> B<T> 的合法上转误降级成 C<i32> -> 裸 B。
            var pending = new Queue<TypeSymbol>();
            var seen = new HashSet<TypeSymbol>();
            pending.Enqueue(receiverType);
            while (pending.TryDequeue(out var current))
            {
                if (!seen.Add(current)) continue;
                if (ReferenceEquals(current.ConstructedFrom ?? current, ownerDefinition))
                    return EnsureDeclaredType(origin, value, current);
                if (current.BaseType != null) pending.Enqueue(current.BaseType);
                foreach (var iface in current.Interfaces) pending.Enqueue(iface);
            }
            return EnsureDeclaredType(origin, value, declaredOwner);
        }

        // ===== variadic 参数索引访问（BIL §7.1 ABI ↔ P3 体内视角桥接）=====
        // fn .args 的 .vargs.<名> = .array<.any>、.kwargs.<名> =
        // .array<.pair<.string, .any>>，而 P3 体内引用定型 Array\<元素\> /
        // Array\<Pair\<String, T\>\>——容器元素类型两视角不一致：读形态索引
        // 节点 Type 取 .nullable<ABI 元素>（Q6：get.array 内建形态结果恒
        // 可空，§21.3 按容器声明推 .nullable<元素> 期望）。位置包逐元素
        // 装拆箱；具名包必须转换 value 并重建 Pair，禁止整对泛型强转。
        // 写形态节点 Type 取 ABI 元素类型，写值按同一规则反向适配。

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

        // core::Pair 定义查找（.bootstrap.rg 自举，按「名 + 泛型
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

        // 仅用于具名参数包边界，适配对象数据而非放宽泛型转换。
        public static LoweredExpression AdaptNamedArgumentPair(BoundNode origin,
            LoweredExpression value, SemanticSymbol target, LowerEnvironment env)
        {
            if (ReferenceEquals(value.Type, target)) return value;
            TypeSymbol? PairType(SemanticSymbol type)
            {
                if (type is TypeSymbol nullable
                    && ReferenceEquals(nullable.ConstructedFrom, env.Unit.Symbols.Bootstrap.NullableDefinition))
                    type = nullable.TypeArguments![0];
                return type as TypeSymbol;
            }
            var definition = FindCorePairDefinition(env);
            var sourcePair = PairType(value.Type);
            var targetPair = PairType(target);
            if (!ReferenceEquals(sourcePair?.ConstructedFrom, definition)
                || !ReferenceEquals(targetPair?.ConstructedFrom, definition))
                return EnsureDeclaredType(origin, value, target);
            var arguments = new[] { sourcePair!.TypeArguments![1], targetPair!.TypeArguments![1] };
            var helper = definition.Methods.Single(m => m.Name == "convertArgument");
            var resultType = env.Unit.Symbols.GetNullable(targetPair);
            var call = new BoundCallExpression(origin.Syntax, helper,
                Array.Empty<BoundExpression>(), resultType, arguments);
            var source = EnsureDeclaredType(origin, value, env.Unit.Symbols.GetNullable(sourcePair));
            return EnsureDeclaredType(origin,
                new LoweredCallExpression(call, helper, new[] { source }, arguments), target);
        }
    }
}
