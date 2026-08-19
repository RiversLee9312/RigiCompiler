namespace RigiCompiler
{
    // 重载解析（S8d，SYNTAX §4.2）：source-level ranking 的唯一落点
    // （BIL §3.3——之后各层不再 ranking）。三步：结构过滤（静默）→
    // 类型适用性（静默）→ 最具体胜出；前两步不产生诊断，唯一落定后
    // 实参已是规范参数序（ARCH §2）。
    // 泛型方法（SYNTAX §4.2）：调用带显式泛型实参时候选池仅泛型方法
    // （实参个数与**固定**泛型参数个数匹配——§4.3 包实参不显式书写；
    // 混合 `f\<T, TArgs...>` 按固定元数匹配，包由值实参推导进 GenericPack）。
    // 不带显式实参时：非泛型 + 全可变包 + **固定泛型（由值实参结构推断）**
    // 同台；推断失败的固定泛型候选排除，池空则诊断「无法从实参推断泛型
    // 实参」。推断规则：裸 T 绑定实参静态类型；构造模式（Array\<T>、
    // Pair\<K,V>、T?、Func\<...>、用户泛型等）递归下钻；同一参数多处
    // 绑定必须一致；未出现在任何值形参 / 仅 null 字面量 / 结构不匹配
    // 即失败。推断成功后过约束检查，代入视图参与三步 ranking。
    // CallBinding.TypeArguments 收固定实参（显式或推断，形态相同——
    // P4 物化 typeid，BIL §7 无单态化）。全可变包行为不变；混合形态
    // 固定部分可推断、包照旧推导。类型级构造器（new List\<i32>()
    // 省略实参）不做推断。
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
            // 固定泛型实参（显式或推断；非泛型 / 全可变包为空——P4 发射依据）
            public IReadOnlyList<SemanticSymbol> TypeArguments { get; }

            public CandidateView(MethodSymbol method, IReadOnlyList<SemanticSymbol> parameterTypes,
                SemanticSymbol? returnType, IReadOnlyList<SemanticSymbol>? typeArguments = null)
            {
                Method = method;
                ParameterTypes = parameterTypes;
                ReturnType = returnType;
                TypeArguments = typeArguments ?? Array.Empty<SemanticSymbol>();
            }
        }

        // 主入口：候选集 + 实参 → (胜者, 规范序实参, 代入后返回类型,
        // 泛型可变包推导产物, 固定泛型实参)；失败落诊断返回 null。
        // 返回类型为胜者视图的 ReturnType（泛型候选已代入显式/推断实参
        // + 宿主构造实参；非泛型候选即声明返回类型）——调用方据此定型
        // Bound 节点（定义级 Method.ReturnType 在泛型下是未代入的 T）。
        // TypeArguments 为胜者的固定泛型实参（显式或推断，须进入
        // CallBinding 车道抵达发射层）。
        // explicitTypeArgs：显式泛型实参（null = 未提供；仅固定泛型
        // 参数，可变泛型参数包不显式书写，由值实参推导）。
        // receiverType：调用点 receiver 静态类型（null = 静态/全局调用或
        // this 上下文）——实例方法签名中的宿主泛型参数沿链取构造实参代入
        public static (MethodSymbol Method, List<BoundExpression> Arguments,
            SemanticSymbol? ReturnType, BoundGenericVarArgsArgument? GenericPack,
            IReadOnlyList<SemanticSymbol> TypeArguments)? Resolve(
            ASTNode node, List<MethodSymbol> candidates, List<ArgumentASTNode> arguments,
            Scope scope, BindContext ctx, BindEnvironment env,
            IReadOnlyList<SemanticSymbol>? explicitTypeArgs = null,
            TypeSymbol? receiverType = null)
        {
            // 实参预绑定（无目标类型；null 字面量占位待胜者形参类型定型）：
            // 存在任意可变泛型包（全可变或固定+包混合）时必须先做——包类型
            // 实参推导需要实参静态类型（SYNTAX §4.3 ⑤）；多候选路径复用
            // 同一结果。任一实参失败即整体失败——表达式自身诊断已报，不
            // 级联误诊
            var needPrebind = candidates.Any(HasVariadicGenericPack)
                || (explicitTypeArgs == null && candidates.Any(m => FixedGenericCount(m) > 0));
            BoundExpression?[]? prebound = null;
            if (needPrebind)
            {
                prebound = PrebindArguments(node, arguments, scope, ctx, env);
                if (prebound == null) return null;
            }
            // 候选池过滤（SYNTAX §4.2/§4.3）：显式实参 → 仅泛型方法，
            // 元数按**固定**泛型参数个数匹配（包除外、不显式书写）；
            // 不带 → 非泛型 + 全可变包 + 固定泛型（值实参结构推断）
            List<CandidateView> pool;
            // 泛型包候选的推导产物（视图 → 包）：胜者落定后取回随结果返回
            var packByView = new Dictionary<CandidateView, BoundGenericVarArgsArgument>();
            if (explicitTypeArgs != null)
            {
                var generics = candidates.Where(m => m.GenericParameters.Count > 0).ToList();
                // §4.2：实参个数与固定泛型参数列表一致（泛型可变参数除外）
                var matching = generics
                    .Where(m => FixedGenericCount(m) == explicitTypeArgs.Count)
                    .ToList();
                // 泛型参数全可变的包候选排除（SYNTAX §4.3 定稿：包类型实参由
                // 值实参推导，永不显式书写）——不误伤「固定+可变」混合形态
                // （含固定参数即非全可变，固定部分仍是合法显式目标）；排除恰好
                // 清空匹配集时落专门诊断（不报误导性的元数不匹配）
                var packOnly = matching.Where(IsGenericPackCandidate).ToList();
                if (packOnly.Count > 0)
                {
                    matching = matching.Where(m => !IsGenericPackCandidate(m)).ToList();
                    if (matching.Count == 0)
                    {
                        env.Error(node.Span, $"'{packOnly[0].Name}': generic variadic pack " +
                            "arguments are derived from value arguments and must not be " +
                            "written explicitly (§4.3)");
                        return null;
                    }
                }
                if (matching.Count == 0)
                {
                    // 候选全是全可变包且给了显式实参（固定元数 0 ≠ N>0）：
                    // 专门诊断，避免「expects 0 type argument(s)」误导
                    if (generics.Count > 0 && generics.All(IsGenericPackCandidate))
                    {
                        env.Error(node.Span, $"'{generics[0].Name}': generic variadic pack " +
                            "arguments are derived from value arguments and must not be " +
                            "written explicitly (§4.3)");
                        return null;
                    }
                    if (generics.Count > 0)
                    {
                        env.Error(node.Span, $"'{generics[0].Name}' expects " +
                            $"{FixedGenericCount(generics[0])} type argument(s), got " +
                            $"{explicitTypeArgs.Count}");
                    }
                    else
                    {
                        env.Error(node.Span,
                            $"'{candidates[0].Name}' is not a generic method");
                    }
                    return null;
                }
                // 使用侧约束检查（SYNTAX §3.6）：显式实参只对照**固定**泛型
                // 参数（包约束在 DerivePack 逐推导类型检查）；不满足的候选
                // 与同元数过滤同层静默剔除；全部剔除才回放首候选的约束诊断
                var eligible = matching
                    .Where(m => SatisfiesConstraints(explicitTypeArgs, m, env)).ToList();
                if (eligible.Count == 0)
                {
                    GenericConstraints.CheckArguments(explicitTypeArgs,
                        FixedGenericParameters(matching[0]), node.Span, env);
                    return null;
                }
                // 混合候选：固定显式实参 + 包由值实参推导；代入视图用
                // 「固定实参 ∪ 包类型」完整序列，GenericPack 单独携带推导
                // 产物；TypeArguments 只收固定实参（显式或推断，形态相同）
                pool = new List<CandidateView>(eligible.Count);
                foreach (var method in eligible)
                {
                    if (HasVariadicGenericPack(method))
                    {
                        var pack = DerivePack(method, arguments, prebound!, node, env);
                        if (pack == null) return null;
                        var combined = BuildSubstitutionArgs(method, explicitTypeArgs,
                            pack.Value.PackType);
                        var view = ViewOf(method, combined, receiverType, env, explicitTypeArgs);
                        pool.Add(view);
                        packByView.Add(view, pack.Value.Pack);
                    }
                    else
                    {
                        pool.Add(ViewOf(method, explicitTypeArgs, receiverType, env,
                            explicitTypeArgs));
                    }
                }
            }
            else
            {
                // 非泛型方法同样经 ViewOf：宿主代入不可省——非泛型方法的
                // 签名仍可能引用宿主泛型参数（`Box<T>.getAtIndex` 返回 T），
                // 裸名/实例调用沿 receiver 构造链代入（否则 T 不代入，
                // IsApplicable 误判/返回类型漏代入）
                pool = candidates.Where(m => m.GenericParameters.Count == 0)
                    .Select(m => ViewOf(m, null, receiverType, env))
                    .ToList();
                foreach (var candidate in candidates.Where(IsGenericPackCandidate))
                {
                    var pack = DerivePack(candidate, arguments, prebound!, node, env);
                    if (pack == null) return null;
                    var view = ViewOf(candidate, new[] { pack.Value.PackType }, receiverType, env);
                    pool.Add(view);
                    packByView.Add(view, pack.Value.Pack);
                }
                // 固定泛型（含固定+包混合）：值实参结构推断；失败排除
                var constraintFailed = false;
                MethodSymbol? constraintWitness = null;
                IReadOnlyList<SemanticSymbol>? constraintArgs = null;
                foreach (var candidate in candidates.Where(m => FixedGenericCount(m) > 0))
                {
                    var inferred = TryInferFixedTypeArgs(candidate, arguments, prebound!,
                        receiverType, env);
                    if (inferred == null) continue;
                    if (!SatisfiesConstraints(inferred, candidate, env))
                    {
                        constraintFailed = true;
                        constraintWitness ??= candidate;
                        constraintArgs ??= inferred;
                        continue;
                    }
                    if (HasVariadicGenericPack(candidate))
                    {
                        var pack = DerivePack(candidate, arguments, prebound!, node, env);
                        if (pack == null) return null;
                        var combined = BuildSubstitutionArgs(candidate, inferred, pack.Value.PackType);
                        var view = ViewOf(candidate, combined, receiverType, env, inferred);
                        pool.Add(view);
                        packByView.Add(view, pack.Value.Pack);
                    }
                    else
                    {
                        pool.Add(ViewOf(candidate, inferred, receiverType, env, inferred));
                    }
                }
                if (pool.Count == 0 && candidates.Any(m => m.GenericParameters.Count > 0))
                {
                    if (constraintFailed && constraintWitness != null && constraintArgs != null)
                    {
                        GenericConstraints.CheckArguments(constraintArgs,
                            FixedGenericParameters(constraintWitness), node.Span, env);
                    }
                    else
                    {
                        env.Error(node.Span, $"'{candidates[0].Name}': cannot infer type " +
                            "arguments from the given arguments");
                    }
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
                return single == null ? null : (pool[0].Method, single, pool[0].ReturnType,
                    packByView.GetValueOrDefault(pool[0]), pool[0].TypeArguments);
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
                return only == null ? null : (mapped[0].View.Method, only,
                    mapped[0].View.ReturnType, packByView.GetValueOrDefault(mapped[0].View),
                    mapped[0].View.TypeArguments);
            }
            // 实参预绑定（无目标类型；null 字面量占位待胜者形参类型定型）。
            // 任一实参失败即整体失败——表达式自身诊断已报，不再级联误诊；
            // 泛型包候选存在时已提前预绑（推导需要），此处复用
            var boundArgs = prebound;
            if (boundArgs == null)
            {
                boundArgs = PrebindArguments(node, arguments, scope, ctx, env);
                if (boundArgs == null) return null;
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
                // 列出真正平局的 winners 子集（非全部 applicable——被占优
                // 淘汰的候选不参与歧义，列出会误导定位）
                var sigs = string.Join(", ", winners.Select(x => SignatureOf(x.View.Method)));
                env.Error(node.Span, $"Call to '{pool[0].Method.Name}' is ambiguous between: {sigs}");
                return null;
            }
            var winner = winners[0];
            return Materialize(winner.View, winner.Mapping, boundArgs, arguments, scope,
                node.Span, ctx, env) is { } finalArgs
                ? (winner.View.Method, finalArgs, winner.View.ReturnType,
                    packByView.GetValueOrDefault(winner.View), winner.View.TypeArguments)
                : null;
        }

        // 已绑定实参的重载解析（运算符位置：不重绑，避免 DA/收窄二次访问）。
        // 无显式泛型实参——固定泛型 operator 由值实参结构推断后入池。
        // 空池落诊断：约束失败回放约束检查；否则（推断全败）报无法推断。
        // 返回 TypeArguments 供调用方写入 Bound 节点（泛型 operator 发射）。
        public static (MethodSymbol Method, SemanticSymbol? ReturnType,
            IReadOnlyList<SemanticSymbol> TypeArguments)? ResolveBound(
            ASTNode node, List<MethodSymbol> candidates,
            IReadOnlyList<BoundExpression> boundArgs,
            BindEnvironment env, TypeSymbol? receiverType)
        {
            var pool = candidates.Where(m => m.GenericParameters.Count == 0)
                .Select(m => ViewOf(m, null, receiverType, env))
                .ToList();
            var constraintFailed = false;
            MethodSymbol? constraintWitness = null;
            IReadOnlyList<SemanticSymbol>? constraintArgs = null;
            foreach (var candidate in candidates.Where(m => FixedGenericCount(m) > 0))
            {
                var inferred = TryInferFromBoundArgs(candidate, boundArgs, receiverType, env);
                if (inferred == null) continue;
                if (!SatisfiesConstraints(inferred, candidate, env))
                {
                    constraintFailed = true;
                    constraintWitness ??= candidate;
                    constraintArgs ??= inferred;
                    continue;
                }
                pool.Add(ViewOf(candidate, inferred, receiverType, env, inferred));
            }
            if (pool.Count == 0)
            {
                if (constraintFailed && constraintWitness != null && constraintArgs != null)
                {
                    GenericConstraints.CheckArguments(constraintArgs,
                        FixedGenericParameters(constraintWitness), node.Span, env);
                }
                else if (candidates.Any(m => m.GenericParameters.Count > 0))
                {
                    env.Error(node.Span, $"'{candidates[0].Name}': cannot infer type " +
                        "arguments from the given arguments");
                }
                return null;
            }

            var boxed = new BoundExpression?[boundArgs.Count];
            for (int i = 0; i < boundArgs.Count; i++) boxed[i] = boundArgs[i];
            var mapping = new int[boundArgs.Count];
            for (int i = 0; i < mapping.Length; i++) mapping[i] = i;

            var applicable = new List<(CandidateView View, int[] Mapping)>();
            foreach (var view in pool)
            {
                if (view.ParameterTypes.Count != boundArgs.Count) continue;
                if (IsApplicable(view, mapping, boxed, env))
                {
                    applicable.Add((view, mapping));
                }
            }
            if (applicable.Count == 0)
            {
                env.Error(node.Span, $"No applicable overload of '{pool[0].Method.Name}' for the " +
                    $"given arguments ({pool.Count} candidates)");
                return null;
            }
            if (applicable.Count == 1)
            {
                return (applicable[0].View.Method, applicable[0].View.ReturnType,
                    applicable[0].View.TypeArguments);
            }

            var winners = new List<(CandidateView View, int[] Mapping)>();
            for (int i = 0; i < applicable.Count; i++)
            {
                var dominated = false;
                for (int j = 0; j < applicable.Count; j++)
                {
                    if (i != j && IsBetter(applicable[j], applicable[i], boxed, env))
                    {
                        dominated = true;
                        break;
                    }
                }
                if (!dominated) winners.Add(applicable[i]);
            }
            if (winners.Count != 1)
            {
                var sigs = string.Join(", ", winners.Select(x => SignatureOf(x.View.Method)));
                env.Error(node.Span, $"Call to '{pool[0].Method.Name}' is ambiguous between: {sigs}");
                return null;
            }
            return (winners[0].View.Method, winners[0].View.ReturnType,
                winners[0].View.TypeArguments);
        }

        // 实参预绑定（无目标类型；null 字面量占位）：任一实参绑定失败返回
        // null（表达式自身诊断已报）
        private static BoundExpression?[]? PrebindArguments(ASTNode node,
            List<ArgumentASTNode> arguments, Scope scope, BindContext ctx, BindEnvironment env)
        {
            var boundArgs = new BoundExpression?[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i].Value.Expression is LiteralExpressionASTNode
                    { Literal: NullLiteralASTNode }) continue;
                boundArgs[i] = ExpressionDispatcher.Visit(arguments[i].Value.Expression, scope,
                    ctx, env);
                if (boundArgs[i] == null) return null;
            }
            return boundArgs;
        }

        // 固定泛型结构推断（调用路径）：以值实参静态类型对形参模式逐位
        // 匹配。返回声明序固定实参；失败（未绑定 / 冲突 / 结构不匹配）
        // 返回 null。null 字面量不参与绑定。包实参跳过（由 DerivePack 推导）。
        private static IReadOnlyList<SemanticSymbol>? TryInferFixedTypeArgs(
            MethodSymbol method, List<ArgumentASTNode> arguments, BoundExpression?[] boundArgs,
            TypeSymbol? receiverType, BindEnvironment env)
        {
            var fixedGenerics = FixedGenericParameters(method);
            if (fixedGenerics.Count == 0) return Array.Empty<SemanticSymbol>();
            var hostView = ViewOf(method, null, receiverType, env);
            var bindings = new Dictionary<GenericParameterSymbol, SemanticSymbol>();
            var fixedParameters = method.Parameters
                .Where(p => !p.IsVariadic && !p.IsNamedVariadic).ToList();
            var hasValuePack = method.Parameters.Count > 0
                && (method.Parameters[^1].IsVariadic || method.Parameters[^1].IsNamedVariadic);
            var isNamedPack = hasValuePack && method.Parameters[^1].IsNamedVariadic;
            var nextPositional = 0;
            for (int i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                int paramIndex;
                if (argument.Name == null)
                {
                    var inPack = nextPositional >= fixedParameters.Count && hasValuePack
                        && !isNamedPack;
                    if (inPack) continue;
                    if (nextPositional >= fixedParameters.Count) return null;
                    paramIndex = method.Parameters.IndexOf(fixedParameters[nextPositional]);
                    nextPositional++;
                }
                else
                {
                    paramIndex = -1;
                    for (int p = 0; p < method.Parameters.Count; p++)
                    {
                        if (method.Parameters[p].Name == argument.Name) { paramIndex = p; break; }
                    }
                    var inPack = paramIndex < 0 && hasValuePack && isNamedPack;
                    if (inPack) continue;
                    if (paramIndex < 0) return null;
                    if (method.Parameters[paramIndex].IsVariadic
                        || method.Parameters[paramIndex].IsNamedVariadic)
                    {
                        continue;
                    }
                }
                if (!TryUnify(hostView.ParameterTypes[paramIndex], boundArgs[i]?.Type,
                    bindings, fixedGenerics))
                {
                    return null;
                }
            }
            return CollectBindings(fixedGenerics, bindings);
        }

        // 固定泛型结构推断（已绑定实参：运算符位置）。形参与实参按位置对齐。
        private static IReadOnlyList<SemanticSymbol>? TryInferFromBoundArgs(
            MethodSymbol method, IReadOnlyList<BoundExpression> boundArgs,
            TypeSymbol? receiverType, BindEnvironment env)
        {
            var fixedGenerics = FixedGenericParameters(method);
            if (fixedGenerics.Count == 0) return Array.Empty<SemanticSymbol>();
            var hostView = ViewOf(method, null, receiverType, env);
            if (hostView.ParameterTypes.Count != boundArgs.Count) return null;
            var bindings = new Dictionary<GenericParameterSymbol, SemanticSymbol>();
            for (int i = 0; i < boundArgs.Count; i++)
            {
                if (!TryUnify(hostView.ParameterTypes[i], boundArgs[i].Type, bindings,
                    fixedGenerics))
                {
                    return null;
                }
            }
            return CollectBindings(fixedGenerics, bindings);
        }

        // 收集声明序绑定；任一固定参数未绑定则失败
        private static IReadOnlyList<SemanticSymbol>? CollectBindings(
            List<GenericParameterSymbol> fixedGenerics,
            Dictionary<GenericParameterSymbol, SemanticSymbol> bindings)
        {
            var result = new SemanticSymbol[fixedGenerics.Count];
            for (int i = 0; i < fixedGenerics.Count; i++)
            {
                if (!bindings.TryGetValue(fixedGenerics[i], out var bound)) return null;
                result[i] = bound;
            }
            return result;
        }

        // 结构统一：裸方法泛型参数绑定实参类型；构造模式递归下钻
        // （含沿 BaseType 找同定义构造——lambda 隐藏类 : Func\<...>）；
        // 同一参数再绑定必须引用相等。actual == null 为 null 字面量，跳过。
        private static bool TryUnify(SemanticSymbol pattern, SemanticSymbol? actual,
            Dictionary<GenericParameterSymbol, SemanticSymbol> bindings,
            List<GenericParameterSymbol> methodFixed)
        {
            if (actual == null) return true;
            if (actual is ErrorTypeSymbol) return false;
            if (pattern is GenericParameterSymbol parameter
                && methodFixed.Contains(parameter))
            {
                if (bindings.TryGetValue(parameter, out var existing))
                {
                    return ReferenceEquals(existing, actual);
                }
                bindings[parameter] = actual;
                return true;
            }
            if (pattern is TypeSymbol { ConstructedFrom: not null, TypeArguments: { } patternArgs }
                constructed)
            {
                TypeSymbol? match = null;
                for (var t = actual as TypeSymbol; t != null; t = t.BaseType)
                {
                    if (t.ConstructedFrom != null
                        && ReferenceEquals(t.ConstructedFrom, constructed.ConstructedFrom)
                        && t.TypeArguments != null
                        && t.TypeArguments.Count == patternArgs.Count)
                    {
                        match = t;
                        break;
                    }
                }
                if (match == null) return false;
                for (int i = 0; i < patternArgs.Count; i++)
                {
                    if (!TryUnify(patternArgs[i], match.TypeArguments![i], bindings, methodFixed))
                    {
                        return false;
                    }
                }
                return true;
            }
            return true;
        }

        // 固定泛型参数个数（§4.2：显式实参元数口径——可变包除外）
        private static int FixedGenericCount(MethodSymbol method)
        {
            return method.GenericParameters.Count(p => !p.IsVariadic && !p.IsNamedVariadic);
        }

        // 固定泛型参数序列（声明序，供约束检查/显式实参对照）
        private static List<GenericParameterSymbol> FixedGenericParameters(MethodSymbol method)
        {
            return method.GenericParameters
                .Where(p => !p.IsVariadic && !p.IsNamedVariadic).ToList();
        }

        // 是否含可变泛型包（全可变或固定+包混合）
        private static bool HasVariadicGenericPack(MethodSymbol method)
        {
            return method.GenericParameters.Any(p => p.IsVariadic || p.IsNamedVariadic);
        }

        // 泛型包候选判定：泛型参数全为可变的泛型方法——包类型实参由值
        // 实参推导（§4.3），与固定泛型推断分道；无显式实参路径此类仍参与
        private static bool IsGenericPackCandidate(MethodSymbol method)
        {
            return method.GenericParameters.Count > 0
                && method.GenericParameters.All(p => p.IsVariadic || p.IsNamedVariadic);
        }

        // 混合形态代入序列：固定显式实参按声明序填入固定槽，可变包槽填
        // 包容器类型（.array<.typeid>/ .map<...>）——与 DerivePack 产物一致
        private static IReadOnlyList<SemanticSymbol> BuildSubstitutionArgs(
            MethodSymbol method, IReadOnlyList<SemanticSymbol> fixedArgs, SemanticSymbol packType)
        {
            var result = new List<SemanticSymbol>(method.GenericParameters.Count);
            var fixedIndex = 0;
            foreach (var parameter in method.GenericParameters)
            {
                if (parameter.IsVariadic || parameter.IsNamedVariadic)
                {
                    result.Add(packType);
                }
                else
                {
                    result.Add(fixedArgs[fixedIndex++]);
                }
            }
            return result;
        }

        // 包实参推导（S9d-2，SYNTAX §4.3 定稿⑤）：类型实参 = 归包值实参的
        // 静态类型——位置包 ← 归包位置实参类型序列；具名包 ← 归包具名
        // 实参「名 → 类型」映射。归包判定与 CallFacility.BindArguments 同源
        // （位置实参超固定形参且包为位置包 → 归包；具名实参名不在固定表
        // 且包为具名包 → 归包）。逐推导类型做约束检查（§4.3：如
        // `with Serializable`，定位到实参）；产物含代入视图用的包类型
        // （位置 .array<.typeid<.any>> / 具名 .map<.string, .typeid<.any>>，
        // BIL §7.1）。多可变泛型参数归口诊断。返回 null = 已诊断
        private static (BoundGenericVarArgsArgument Pack, SemanticSymbol PackType)? DerivePack(
            MethodSymbol method, List<ArgumentASTNode> arguments, BoundExpression?[] boundArgs,
            ASTNode node, BindEnvironment env)
        {
            var variadics = method.GenericParameters
                .Where(p => p.IsVariadic || p.IsNamedVariadic).ToList();
            if (variadics.Count > 1)
            {
                env.Error(node.Span,
                    "P3: multiple variadic generic parameters are not supported yet (S9d)");
                return null;
            }
            var packParameter = variadics[0];
            var fixedParameters = method.Parameters
                .Where(p => !p.IsVariadic && !p.IsNamedVariadic).ToList();
            var hasValuePack = method.Parameters.Count > 0
                && (method.Parameters[^1].IsVariadic || method.Parameters[^1].IsNamedVariadic);
            var isNamedPack = packParameter.IsNamedVariadic;
            var packTypes = new List<SemanticSymbol>();
            var namedTypes = new List<(string Name, SemanticSymbol Type)>();
            var nextPositional = 0;
            for (int i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                bool inPack;
                if (argument.Name == null)
                {
                    inPack = nextPositional >= fixedParameters.Count && hasValuePack
                        && !isNamedPack;
                    if (!inPack) nextPositional++;
                }
                else
                {
                    inPack = !fixedParameters.Any(p => p.Name == argument.Name)
                        && hasValuePack && isNamedPack;
                }
                if (!inPack) continue;
                var type = boundArgs[i]?.Type;
                if (type == null)
                {
                    env.Error(argument.Span, "P3: cannot infer a type argument from a null " +
                        "literal in a generic variadic pack (S9d)");
                    return null;
                }
                // 逐推导类型约束检查（SYNTAX §4.3：推导出的每个类型实参
                // 必须满足对应泛型参数的约束），失败定位到实参
                if (!GenericConstraints.CheckArguments(new[] { type },
                    new[] { packParameter }, argument.Value.Span ?? argument.Span, env))
                {
                    return null;
                }
                if (isNamedPack)
                {
                    namedTypes.Add((argument.Name!, type));
                }
                else
                {
                    packTypes.Add(type);
                }
            }
            var typeIdType = env.Unit.Symbols.GetConstructedType(
                env.Unit.Symbols.Bootstrap.TypeDefinition, env.Unit.Symbols.Bootstrap.Any);
            var packType = isNamedPack
                ? (SemanticSymbol)env.Unit.Symbols.GetConstructedType(
                    env.Unit.Symbols.Bootstrap.MapDefinition,
                    env.Unit.Symbols.Bootstrap.String, typeIdType)
                : env.Unit.Symbols.GetConstructedType(
                    env.Unit.Symbols.Bootstrap.ArrayDefinition, typeIdType);
            // Syntax 非空契约（BoundNode.Syntax）：空包无实参节点可指，
            // 以调用节点承载
            var pack = new BoundGenericVarArgsArgument(
                arguments.Count > 0 ? (ASTNode)arguments[0].Value : node,
                isNamed: isNamedPack, packTypes,
                isNamedPack ? namedTypes : null);
            return (pack, packType);
        }

        // 泛型候选代入视图：显式/推断实参按泛型参数序代入参数/返回类型；
        // 实例方法另作宿主代入——receiver 静态类型沿 BaseType 链找
        // method.Owner 的构造，宿主泛型参数按构造实参替换（`Box<i32>` 上
        // get\<U> 的返回 T → i32）；receiver 为定义级/静态时宿主参数保留
        // 身份（this 上下文、ext 成员等）。代入失败（仅防御）回退声明类型。
        // storedTypeArgs：写入视图的固定泛型实参（显式或推断；全可变包/
        // 非泛型为空）。substitution 用 typeArgs（混合形态含包容器类型）。
        private static CandidateView ViewOf(MethodSymbol method,
            IReadOnlyList<SemanticSymbol>? typeArgs, TypeSymbol? receiverType,
            BindEnvironment env, IReadOnlyList<SemanticSymbol>? storedTypeArgs = null)
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
            return new CandidateView(method, parameterTypes, returnType, storedTypeArgs);
        }

        // 双层代入：方法泛型参数（显式实参 / 混合形态的包容器类型）→
        // 宿主泛型参数（构造实参）→ 原样（身份保留）。构造类型逐项替换后
        // 经驻留入口重建。methodArgs 可能短于 methodGenerics（防御：仅
        // 固定显式、包槽未并入时保留身份，避免越界——正常路径由
        // BuildSubstitutionArgs 补齐）
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
                    if (!ReferenceEquals(methodGenerics[i], parameter)) continue;
                    return i < methodArgs.Count ? methodArgs[i] : type;
                }
                if (hostArgs != null)
                {
                    for (int i = 0; i < hostGenerics.Count; i++)
                    {
                        if (ReferenceEquals(hostGenerics[i], parameter)
                            && i < hostArgs.Count)
                        {
                            return hostArgs[i];
                        }
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

        // 逐候选约束满足判定（§3.6，静默版 GenericConstraints.CheckArguments）：
        // 候选过滤层不可落诊断，此处只判不报；显式实参只对照固定泛型参数
        // （包约束在 DerivePack）。判定语义与跳过规则（ErrorType 毒化/含未
        // 代入泛型参数跳过）与 GenericConstraints 保持一致——全部候选均不
        // 满足时由调用方回放 CheckArguments 产出诊断
        private static bool SatisfiesConstraints(IReadOnlyList<SemanticSymbol> typeArgs,
            MethodSymbol candidate, BindEnvironment env)
        {
            var generics = FixedGenericParameters(candidate);
            for (int i = 0; i < generics.Count && i < typeArgs.Count; i++)
            {
                var argument = typeArgs[i];
                if (argument is ErrorTypeSymbol) continue;
                if (SymbolLookup.ContainsGenericParameter(argument)) continue;
                foreach (var constraint in generics[i].Constraints)
                {
                    var bound = constraint.Bound;
                    if (bound == null || SymbolLookup.ContainsGenericParameter(bound)) continue;
                    var satisfied = constraint.Kind switch
                    {
                        GenericConstraintKind.Extends =>
                            SymbolLookup.IsAssignable(argument, bound, env),
                        GenericConstraintKind.Supers =>
                            SymbolLookup.IsAssignable(bound, argument, env),
                        GenericConstraintKind.With => bound is TypeSymbol wrapper
                            && HasWrapperApplied(argument, wrapper),
                        _ => true,
                    };
                    if (!satisfied) return false;
                }
            }
            return true;
        }

        // with 判定：wrapper 在实参的 wrapper 应用集合中（构造类型回退定义；
        // 镜像 GenericConstraints 的私有实现——静默过滤无法复用其落诊断入口）
        private static bool HasWrapperApplied(SemanticSymbol argument, TypeSymbol wrapper)
        {
            var definition = argument as TypeSymbol;
            if (definition?.ConstructedFrom != null) definition = definition.ConstructedFrom;
            return definition != null
                && definition.AppliedWrappers.Any(w => ReferenceEquals(w.WrapperDefinition, wrapper));
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
        // 代入后类型）；null 字面量实参要求形参为 Nullable\<T\>；
        // 形参为泛型参数时走 IsAssignable（同参直通；T extends B 可赋给 B）
        private static bool IsApplicable(CandidateView view, int[] mapping,
            BoundExpression?[] boundArgs, BindEnvironment env)
        {
            for (int i = 0; i < mapping.Length; i++)
            {
                var paramType = view.ParameterTypes[mapping[i]];
                if (paramType == null || paramType is ErrorTypeSymbol) return false;
                if (boundArgs[i] == null)
                {
                    if (paramType is not TypeSymbol ts
                        || ts.ConstructedFrom != env.B.NullableDefinition)
                    {
                        return false;
                    }
                    continue;
                }
                // S11e：降级调用结果 Any 可作任意形参实参（运行时 cast 兜底）
                if (!SymbolLookup.IsAssignable(boundArgs[i]!.Type, paramType, env)
                    && !BoundAnalysis.IsDowngradeCallResult(boundArgs[i]!, env)) return false;
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
                var pa = a.View.ParameterTypes[a.Mapping[i]];
                var pb = b.View.ParameterTypes[b.Mapping[i]];
                if (pa == null || pb == null) return false;
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
                // 防御终检（与适用性判定同源，预期必过）；S11e：降级调用
                // 结果豁免同 IsApplicable（动态结果可传入任意形参，cast 兜底）
                if (!SymbolLookup.IsAssignable(value.Type, paramType, env)
                    && !BoundAnalysis.IsDowngradeCallResult(value, env))
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
