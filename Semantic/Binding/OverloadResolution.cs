namespace LatteCompiler
{
    // 重载解析（S8d，SYNTAX §4.2）：source-level ranking 的唯一落点
    // （BIL §3.3——之后各层不再 ranking）。三步：结构过滤（静默）→
    // 类型适用性（静默）→ 最具体胜出；前两步不产生诊断，唯一落定后
    // 实参已是规范参数序（ARCH §2）。泛型方法（S9）与可变参数方法
    // 不参与调用绑定，归口诊断。
    //
    // 单候选（含结构过滤后唯一）走 CallFacility.BindArguments 快路径——
    // 逐实参带目标类型绑定，保持既有诊断语义；多候选路径先无目标类型
    // 预绑实参（null 字面量占位，落定阶段以胜者形参类型定型），类型
    // 层面完成过滤与 ranking，全程静默直至产生唯一结论。
    internal static class OverloadResolution
    {
        // 主入口：候选集 + 实参 → (胜者, 规范序实参)；失败落诊断返回 null
        public static (MethodSymbol Method, List<BoundExpression> Arguments)? Resolve(
            ASTNode node, List<MethodSymbol> candidates, List<ArgumentASTNode> arguments,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            // 预处理：泛型方法（S9）与可变参数方法不参与调用绑定（SYNTAX §4.2）
            var pool = candidates.Where(m => m.GenericParameters.Count == 0).ToList();
            if (pool.Count == 0)
            {
                env.Error(node.Span, "P3: generic calls are not supported yet (S9)");
                return null;
            }
            pool = pool.Where(m => !m.Parameters.Any(p => p.IsVariadic || p.IsNamedVariadic))
                .ToList();
            if (pool.Count == 0)
            {
                env.Error(node.Span, "P3: variadic parameters are not supported yet");
                return null;
            }
            if (pool.Count == 1)
            {
                var single = CallFacility.BindArguments(pool[0], arguments, scope, node.Span,
                    ctx, env);
                return single == null ? null : (pool[0], single);
            }
            // 第一步：结构过滤（静默）——实参→形参映射（名字存在/不重复/
            // 个数适配默认值）
            var mapped = new List<(MethodSymbol Method, int[] Mapping)>();
            foreach (var method in pool)
            {
                var mapping = TryMapArguments(method, arguments);
                if (mapping != null) mapped.Add((method, mapping));
            }
            if (mapped.Count == 0)
            {
                env.Error(node.Span, $"No applicable overload of '{pool[0].Name}' for the " +
                    $"given arguments ({pool.Count} candidates)");
                return null;
            }
            if (mapped.Count == 1)
            {
                var only = CallFacility.BindArguments(mapped[0].Method, arguments, scope,
                    node.Span, ctx, env);
                return only == null ? null : (mapped[0].Method, only);
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
            var applicable = mapped.Where(x => IsApplicable(x.Method, x.Mapping, boundArgs, env))
                .ToList();
            if (applicable.Count == 0)
            {
                env.Error(node.Span, $"No applicable overload of '{pool[0].Name}' for the " +
                    $"given arguments ({pool.Count} candidates)");
                return null;
            }
            // 第三步：最具体胜出——唯一不被任何其他候选严格优于的候选
            var winners = new List<(MethodSymbol Method, int[] Mapping)>();
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
                var minFill = winners.Min(w => w.Method.Parameters.Count - w.Mapping.Length);
                winners = winners.Where(w => w.Method.Parameters.Count - w.Mapping.Length
                    == minFill).ToList();
            }
            if (winners.Count != 1)
            {
                var sigs = string.Join(", ", applicable.Select(x => SignatureOf(x.Method)));
                env.Error(node.Span, $"Call to '{pool[0].Name}' is ambiguous between: {sigs}");
                return null;
            }
            var winner = winners[0];
            return Materialize(winner.Method, winner.Mapping, boundArgs, arguments, scope,
                node.Span, ctx, env) is { } finalArgs
                ? (winner.Method, finalArgs)
                : null;
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

        // 类型适用性（静默）：每个实参类型可赋给对应形参类型；null 字面量
        // 实参要求形参为 Nullable\<T\>；形参类型不可判（泛型参数 S9 /
        // ErrorType 毒化）的候选保守剔除
        private static bool IsApplicable(MethodSymbol method, int[] mapping,
            BoundExpression?[] boundArgs, BindEnvironment env)
        {
            for (int i = 0; i < mapping.Length; i++)
            {
                if (method.Parameters[mapping[i]].Type is not TypeSymbol paramType
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
        private static bool IsBetter((MethodSymbol Method, int[] Mapping) a,
            (MethodSymbol Method, int[] Mapping) b, BoundExpression?[] boundArgs,
            BindEnvironment env)
        {
            var strict = false;
            for (int i = 0; i < boundArgs.Length; i++)
            {
                if (boundArgs[i] == null) continue;
                var pa = (TypeSymbol)a.Method.Parameters[a.Mapping[i]].Type!;
                var pb = (TypeSymbol)b.Method.Parameters[b.Mapping[i]].Type!;
                if (!SymbolLookup.IsAssignable(pa, pb, env)) return false;
                if (!SymbolLookup.IsAssignable(pb, pa, env)) strict = true;
            }
            return strict;
        }

        // 落定：按胜者构建规范参数序实参列表——预绑实参归位（null 字面量
        // 以胜者形参类型定型）+ 缺省形参填默认值（查预绑定表；预绑定失败
        // 的按缺失处理——声明点诊断已报）
        private static List<BoundExpression>? Materialize(MethodSymbol winner, int[] mapping,
            BoundExpression?[] boundArgs, List<ArgumentASTNode> arguments, Scope scope,
            CharRange? callSpan, BindContext ctx, BindEnvironment env)
        {
            var parameters = winner.Parameters;
            var result = new BoundExpression?[parameters.Count];
            var failed = false;
            for (int i = 0; i < mapping.Length; i++)
            {
                var paramType = (TypeSymbol)parameters[mapping[i]].Type!;
                var value = boundArgs[i];
                if (value == null)
                {
                    value = LiteralVisitor.Visit(arguments[i].Value.Expression, scope, ctx, env,
                        paramType);
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
