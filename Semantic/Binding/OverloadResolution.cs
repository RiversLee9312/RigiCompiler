namespace LatteCompiler
{
    // 重载解析（S8d，SYNTAX §4.2）：source-level ranking 的唯一落点
    // （BIL §3.3——之后各层不再 ranking）。三步：结构过滤（静默）→
    // 类型适用性（静默）→ 最具体胜出；前两步不产生诊断，唯一落定后
    // 实参已是规范参数序（ARCH §2）。
    // S9b 增补泛型方法（SYNTAX §4.2 定稿）：调用带显式泛型实参时候选池
    // 仅泛型方法（实参个数与泛型参数个数匹配过滤），不带时泛型方法不
    // 参与候选（仅剩泛型候选时诊断「需要显式泛型实参」）；泛型候选按
    // 实参代入后的签名参与三步（SubstituteType 视图，符号身份不变）。
    // 可变参数方法不参与调用绑定，归口诊断。
    //
    // 单候选（含结构过滤后唯一）走 CallFacility.BindArguments 快路径——
    // 逐实参带目标类型绑定，保持既有诊断语义；多候选路径先无目标类型
    // 预绑实参（null 字面量占位，落定阶段以胜者形参类型定型），类型
    // 层面完成过滤与 ranking，全程静默直至产生唯一结论。
    internal static class OverloadResolution
    {
        // 泛型候选的代入视图：三步比较与实参定型的类型依据
        // （Method 保持定义级符号身份——诊断/发射用；参数类型为代入后）
        internal sealed class CandidateView
        {
            public MethodSymbol Method { get; }
            public IReadOnlyList<SemanticSymbol> ParameterTypes { get; }
            public SemanticSymbol? ReturnType { get; }

            public CandidateView(MethodSymbol method, IReadOnlyList<SemanticSymbol> parameterTypes,
                SemanticSymbol? returnType)
            {
                Method = method;
                ParameterTypes = parameterTypes;
                ReturnType = returnType;
            }
        }

        // 主入口：候选集 + 实参 → (胜者, 规范序实参, 代入后返回类型)；
        // 失败落诊断返回 null。返回类型为胜者视图的 ReturnType（泛型候选
        // 已代入显式实参 + 宿主构造实参；非泛型候选即声明返回类型）——
        // 调用方据此定型 Bound 节点（定义级 Method.ReturnType 在泛型下是
        // 未代入的 T）。
        // explicitTypeArgs：显式泛型实参（null = 未提供；S9b 仅固定泛型
        // 参数，可变泛型参数包归 S9d）。
        // receiverType：调用点 receiver 静态类型（null = 静态/全局调用或
        // this 上下文）——实例方法签名中的宿主泛型参数沿链取构造实参代入
        public static (MethodSymbol Method, List<BoundExpression> Arguments,
            SemanticSymbol? ReturnType)? Resolve(
            ASTNode node, List<MethodSymbol> candidates, List<ArgumentASTNode> arguments,
            Scope scope, BindContext ctx, BindEnvironment env,
            IReadOnlyList<SemanticSymbol>? explicitTypeArgs = null,
            TypeSymbol? receiverType = null)
        {
            // 候选池过滤（SYNTAX §4.2 S9 定稿）：显式实参 → 仅泛型方法
            // （个数匹配）；不带 → 仅非泛型方法
            List<CandidateView> pool;
            if (explicitTypeArgs != null)
            {
                var generics = candidates.Where(m => m.GenericParameters.Count > 0).ToList();
                var matching = generics
                    .Where(m => m.GenericParameters.Count == explicitTypeArgs.Count)
                    .ToList();
                if (matching.Count == 0)
                {
                    if (generics.Count > 0)
                    {
                        env.Error(node.Span, $"'{generics[0].Name}' expects " +
                            $"{generics[0].GenericParameters.Count} type argument(s), got " +
                            $"{explicitTypeArgs.Count}");
                    }
                    else
                    {
                        env.Error(node.Span,
                            $"'{candidates[0].Name}' is not a generic method");
                    }
                    return null;
                }
                // 使用侧约束检查（SYNTAX §3.6）：显式实参须满足泛型参数约束
                if (!GenericConstraints.CheckArguments(explicitTypeArgs,
                    matching[0].GenericParameters, node.Span, env))
                {
                    return null;
                }
                pool = matching.Select(m => ViewOf(m, explicitTypeArgs, receiverType, env)).ToList();
            }
            else
            {
                pool = candidates.Where(m => m.GenericParameters.Count == 0)
                    .Select(m => new CandidateView(m, m.Parameters.Select(p => p.Type!)
                        .ToList(), m.ReturnType))
                    .ToList();
                if (pool.Count == 0 && candidates.Any(m => m.GenericParameters.Count > 0))
                {
                    env.Error(node.Span, $"'{candidates[0].Name}' is a generic method; " +
                        "provide explicit type arguments");
                    return null;
                }
            }
            // S9d：单候选可变参数方法放行（BindArguments 打包）；多候选
            // 含可变参数仍归口（包实参不参与 ranking，组合形态待 S9d 定稿）
            if (pool.Count > 1
                && pool.Any(v => v.Method.Parameters.Any(p => p.IsVariadic || p.IsNamedVariadic)))
            {
                env.Error(node.Span,
                    "P3: overload resolution with variadic parameters is not supported yet");
                return null;
            }
            if (pool.Count == 1)
            {
                var single = CallFacility.BindArguments(pool[0].Method, arguments, scope, node.Span,
                    ctx, env, pool[0].ParameterTypes);
                return single == null ? null : (pool[0].Method, single, pool[0].ReturnType);
            }
            // 第一步：结构过滤（静默）——实参→形参映射（名字存在/不重复/
            // 个数适配默认值）
            var mapped = new List<(CandidateView View, int[] Mapping)>();
            foreach (var view in pool)
            {
                var mapping = TryMapArguments(view.Method, arguments);
                if (mapping != null) mapped.Add((view, mapping));
            }
            if (mapped.Count == 0)
            {
                env.Error(node.Span, $"No applicable overload of '{pool[0].Method.Name}' for the " +
                    $"given arguments ({pool.Count} candidates)");
                return null;
            }
            if (mapped.Count == 1)
            {
                var only = CallFacility.BindArguments(mapped[0].View.Method, arguments, scope,
                    node.Span, ctx, env, mapped[0].View.ParameterTypes);
                return only == null ? null : (mapped[0].View.Method, only, mapped[0].View.ReturnType);
            }
            // 实参预绑定（无目标类型；null 字面量占位待胜者形参类型定型）。
            // 任一实参失败即整体失败——表达式自身诊断已报，不再级联误诊
            var boundArgs = new BoundExpression?[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i].Value.Expression is LiteralExpressionASTNode
                    { Literal: NullLiteralASTNode }) continue;
                boundArgs[i] = ExpressionDispatcher.Visit(arguments[i].Value.Expression, scope,
                    ctx, env);
                if (boundArgs[i] == null) return null;
            }
            // 第二步：类型适用性（静默）
            var applicable = mapped.Where(x => IsApplicable(x.View, x.Mapping, boundArgs, env))
                .ToList();
            if (applicable.Count == 0)
            {
                env.Error(node.Span, $"No applicable overload of '{pool[0].Method.Name}' for the " +
                    $"given arguments ({pool.Count} candidates)");
                return null;
            }
            // 第三步：最具体胜出——唯一不被任何其他候选严格优于的候选
            var winners = new List<(CandidateView View, int[] Mapping)>();
            for (int i = 0; i < applicable.Count; i++)
            {
                var dominated = false;
                for (int j = 0; j < applicable.Count; j++)
                {
                    if (i != j && IsBetter(applicable[j], applicable[i], boundArgs, env))
                    {
                        dominated = true;
                        break;
                    }
                }
                if (!dominated) winners.Add(applicable[i]);
            }
            if (winners.Count > 1)
            {
                // 平局打破（SYNTAX §4.2）：本次调用填充默认值形参更少者优先
                // （未填形参总数 = 填充的默认值个数——结构过滤已保证未填
                // 形参全有默认值）
                var minFill = winners.Min(w => w.View.Method.Parameters.Count - w.Mapping.Length);
                winners = winners.Where(w => w.View.Method.Parameters.Count - w.Mapping.Length
                    == minFill).ToList();
            }
            if (winners.Count != 1)
            {
                var sigs = string.Join(", ", applicable.Select(x => SignatureOf(x.View.Method)));
                env.Error(node.Span, $"Call to '{pool[0].Method.Name}' is ambiguous between: {sigs}");
                return null;
            }
            var winner = winners[0];
            return Materialize(winner.View, winner.Mapping, boundArgs, arguments, scope,
                node.Span, ctx, env) is { } finalArgs
                ? (winner.View.Method, finalArgs, winner.View.ReturnType)
                : null;
        }

        // 泛型候选代入视图：显式实参按泛型参数序代入参数/返回类型；
        // 实例方法另作宿主代入——receiver 静态类型沿 BaseType 链找
        // method.Owner 的构造，宿主泛型参数按构造实参替换（`Box<i32>` 上
        // get\<U> 的返回 T → i32）；receiver 为定义级/静态时宿主参数保留
        // 身份（this 上下文、ext 成员等）。代入失败（仅防御）回退声明类型
        private static CandidateView ViewOf(MethodSymbol method,
            IReadOnlyList<SemanticSymbol>? typeArgs, TypeSymbol? receiverType,
            BindEnvironment env)
        {
            List<SemanticSymbol>? hostArgs = null;
            if (method.Owner != null && receiverType != null
                && method.Owner.GenericParameters.Count > 0)
            {
                for (var t = receiverType; t != null; t = t.BaseType)
                {
                    if (ReferenceEquals(t.ConstructedFrom, method.Owner)
                        && t.TypeArguments != null)
                    {
                        hostArgs = t.TypeArguments.ToList();
                        break;
                    }
                }
            }
            var generics = method.GenericParameters;
            var args = typeArgs ?? Array.Empty<SemanticSymbol>();
            IReadOnlyList<GenericParameterSymbol> hostGenerics = method.Owner != null
                ? method.Owner.GenericParameters
                : Array.Empty<GenericParameterSymbol>();
            var parameterTypes = method.Parameters
                .Select(p => SubstituteAll(p.Type!, generics, args, hostGenerics, hostArgs, env)
                    ?? p.Type!)
                .ToList();
            var returnType = method.ReturnType == null
                ? null
                : SubstituteAll(method.ReturnType, generics, args, hostGenerics, hostArgs, env)
                    ?? method.ReturnType;
            return new CandidateView(method, parameterTypes, returnType);
        }

        // 双层代入：方法泛型参数（显式实参）→ 宿主泛型参数（构造实参）→
        // 原样（身份保留）。构造类型逐项替换后经驻留入口重建
        private static SemanticSymbol? SubstituteAll(SemanticSymbol type,
            IReadOnlyList<GenericParameterSymbol> methodGenerics,
            IReadOnlyList<SemanticSymbol> methodArgs,
            IReadOnlyList<GenericParameterSymbol> hostGenerics,
            IReadOnlyList<SemanticSymbol>? hostArgs, BindEnvironment env)
        {
            if (type is GenericParameterSymbol parameter)
            {
                for (int i = 0; i < methodGenerics.Count; i++)
                {
                    if (ReferenceEquals(methodGenerics[i], parameter)) return methodArgs[i];
                }
                if (hostArgs != null)
                {
                    for (int i = 0; i < hostGenerics.Count; i++)
                    {
                        if (ReferenceEquals(hostGenerics[i], parameter)) return hostArgs[i];
                    }
                }
                return type;
            }
            if (type is TypeSymbol { ConstructedFrom: not null, TypeArguments: { } args } constructed)
            {
                var substituted = new SemanticSymbol[args.Count];
                var changed = false;
                for (int i = 0; i < args.Count; i++)
                {
                    var inner = SubstituteAll(args[i], methodGenerics, methodArgs, hostGenerics,
                        hostArgs, env);
                    if (inner == null) return null;
                    substituted[i] = inner;
                    changed |= !ReferenceEquals(inner, args[i]);
                }
                if (!changed) return constructed;
                return env.Unit.Symbols.GetConstructedType(constructed.ConstructedFrom,
                    substituted);
            }
            return type;
        }

        // 结构映射（静默）：mapping[实参序] = 形参序；null = 结构不适用。
        // 位置实参按源码顺序依次占位（与具名穿插无关，SYNTAX §4.2）；
        // 具名实参按形参名归位；重复填充即不适用；未填充形参必须携带默认值
        private static int[]? TryMapArguments(MethodSymbol method, List<ArgumentASTNode> arguments)
        {
            var parameters = method.Parameters;
            var mapping = new int[arguments.Count];
            var occupied = new bool[parameters.Count];
            var nextPositional = 0;
            for (int i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                int index;
                if (argument.Name == null)
                {
                    if (nextPositional >= parameters.Count) return null;
                    index = nextPositional++;
                }
                else
                {
                    index = -1;
                    for (int p = 0; p < parameters.Count; p++)
                    {
                        if (parameters[p].Name == argument.Name) { index = p; break; }
                    }
                    if (index < 0) return null;
                }
                if (occupied[index]) return null;
                occupied[index] = true;
                mapping[i] = index;
            }
            for (int p = 0; p < parameters.Count; p++)
            {
                if (!occupied[p] && parameters[p].DefaultValue == null) return null;
            }
            return mapping;
        }

        // 类型适用性（静默）：每个实参类型可赋给对应形参类型（视图的
        // 代入后类型）；null 字面量实参要求形参为 Nullable\<T\>；形参类型
        // 不可判（泛型参数 / ErrorType 毒化）的候选保守剔除
        private static bool IsApplicable(CandidateView view, int[] mapping,
            BoundExpression?[] boundArgs, BindEnvironment env)
        {
            for (int i = 0; i < mapping.Length; i++)
            {
                if (view.ParameterTypes[mapping[i]] is not TypeSymbol paramType
                    || paramType is ErrorTypeSymbol)
                {
                    return false;
                }
                if (boundArgs[i] == null)
                {
                    if (paramType.ConstructedFrom != env.B.NullableDefinition) return false;
                    continue;
                }
                if (!SymbolLookup.IsAssignable(boundArgs[i]!.Type, paramType, env)) return false;
            }
            return true;
        }

        // A 严格优于 B：每个实参处 A 对应形参类型都可赋给 B 对应形参类型，
        // 且至少一处反向不可赋值（更具体）；null 字面量实参位置两者皆
        // Nullable（适用性已保证），不构成优劣
        private static bool IsBetter((CandidateView View, int[] Mapping) a,
            (CandidateView View, int[] Mapping) b, BoundExpression?[] boundArgs,
            BindEnvironment env)
        {
            var strict = false;
            for (int i = 0; i < boundArgs.Length; i++)
            {
                if (boundArgs[i] == null) continue;
                var pa = (TypeSymbol)a.View.ParameterTypes[a.Mapping[i]];
                var pb = (TypeSymbol)b.View.ParameterTypes[b.Mapping[i]];
                if (!SymbolLookup.IsAssignable(pa, pb, env)) return false;
                if (!SymbolLookup.IsAssignable(pb, pa, env)) strict = true;
            }
            return strict;
        }

        // 落定：按胜者构建规范参数序实参列表——预绑实参归位（null 字面量
        // 以胜者形参类型定型）+ 缺省形参填默认值（查预绑定表；预绑定失败
        // 的按缺失处理——声明点诊断已报）
        private static List<BoundExpression>? Materialize(CandidateView view, int[] mapping,
            BoundExpression?[] boundArgs, List<ArgumentASTNode> arguments, Scope scope,
            CharRange? callSpan, BindContext ctx, BindEnvironment env)
        {
            var parameters = view.Method.Parameters;
            var result = new BoundExpression?[parameters.Count];
            var failed = false;
            for (int i = 0; i < mapping.Length; i++)
            {
                var paramType = view.ParameterTypes[mapping[i]];
                var value = boundArgs[i];
                if (value == null)
                {
                    value = LiteralVisitor.Visit(arguments[i].Value.Expression, scope, ctx, env,
                        paramType as TypeSymbol);
                    if (value == null) { failed = true; continue; }
                }
                // 防御终检（与适用性判定同源，预期必过）
                if (!SymbolLookup.IsAssignable(value.Type, paramType, env))
                {
                    env.Error(arguments[i].Value.Span ?? arguments[i].Span,
                        $"Cannot pass '{BoundAnalysis.TypeDisplay(value.Type)}' as " +
                        $"'{BoundAnalysis.TypeDisplay(paramType)}'");
                    failed = true;
                    continue;
                }
                result[mapping[i]] = value;
            }
            for (int p = 0; p < parameters.Count; p++)
            {
                if (result[p] != null) continue;
                var defaultValue = env.GetParameterDefault(parameters[p]);
                if (defaultValue == null)
                {
                    env.Error(callSpan, $"Missing argument for parameter '{parameters[p].Name}'");
                    failed = true;
                    continue;
                }
                result[p] = defaultValue;
            }
            return failed ? null : result.Select(b => b!).ToList();
        }

        // 诊断用签名文本：name(T1, T2)
        private static string SignatureOf(MethodSymbol method)
        {
            var parts = method.Parameters.Select(p => p.Type is TypeSymbol t
                ? BoundAnalysis.TypeDisplay(t)
                : p.Type?.Name ?? "?");
            return $"{method.Name}({string.Join(", ", parts)})";
        }
    }
}
