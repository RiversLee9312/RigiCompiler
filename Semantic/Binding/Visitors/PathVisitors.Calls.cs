
namespace RigiCompiler
{
    internal static partial class PathFacility
    {
        // Calls 职责；与主文件共享同一类型、字段及生命周期。

        // CallForm 特殊形态分类（普通函数调用绑定前）：enum-case 路径/
        // 调用（`EnumType.Case(args)`，含命名空间限定）与具化泛型构造
        // （`TResult()`）。handled=true 时返回绑定产物（null = 失败，
        // 诊断已落袋）；handled=false 表示非特殊形态，调用方走
        // CallFacility.BindCall。普通表达式路径绑定与表达式语句绑定
        // 共用——enum case 识别只有一个事实来源。绑定产物的 Syntax 恒为
        // 调用所在的 path 节点（node）：表达式语境历来如此，语句语境
        // 自分类集中起与之对齐（此前具化构造语句挂语句节点；下游只经
        // .Syntax.Span 取位置，path 节点位置更精确）。
        public static BoundExpression? TryBindSpecialPathCall(PathExpressionASTNode node,
            List<string> calleeSegments, List<ArgumentASTNode> callArguments,
            List<TypeReferenceASTNode>? genericArguments, Scope scope, BindContext ctx,
            BindEnvironment env, bool forAssignment, out bool handled)
        {
            var caseCall = TryBindEnumCasePath(node, scope, ctx, env, forAssignment,
                out var caseCallHandled);
            if (caseCallHandled)
            {
                handled = true;
                return caseCall;
            }
            if (calleeSegments.Count == 1 && genericArguments == null)
            {
                var reified = CallFacility.TryBindReifiedConstruction(node, calleeSegments[0],
                    callArguments, scope, ctx, env, out handled);
                if (handled) return reified;
            }
            handled = false;
            return null;
        }

        // §12 `EnumType.Case` 全形路径探测（值位置）：沿段序列找
        // 「enum struct 前缀 + case 段」——前缀（首段 + 前 j 段，可无
        // 命名空间限定）静默解析为定义级 enum struct，第 j 段名命中
        // case（真实成员优先——FindMember 命中即退出，交既有流程，
        // 解析优先级与可见性同既有成员解析惯例）。命中后该段首 Call
        // 后缀为参数化调用（与 `.Case(args)` 省略形式同一通道），无
        // 后缀为固定 case 值；该段剩余后缀折叠、后续段交实例链。
        // handled=false 表示非 case 路径（诊断交既有流程）；handled=true
        // 时返回值为绑定结果（null = 失败，诊断已落袋）。forAssignment
        // 不介入（case 不是赋值目标，交既有「Undefined name」诊断）
        private static BoundExpression? TryBindEnumCasePath(PathExpressionASTNode node,
            Scope scope, BindContext ctx, BindEnvironment env, bool forAssignment,
            out bool handled)
        {
            handled = false;
            if (forAssignment) return null;
            if (node.Head.Name == null || node.Head.Expression != null) return null;
            if (node.Head.Suffixes.Count > 0 || node.Head.GenericArguments.Count > 0) return null;
            // 值优先级（同既有调用/路径解析惯例）：首段命中局部/参数/
            // 字段时全形 case 路径不介入
            var headName = node.Head.Name;
            if (scope.LookupSymbol(headName) != null) return null;
            if (!ctx.Frame.IsDefaultValueContext
                && ctx.Frame.Method.Parameters.Any(p => p.Name == headName))
            {
                return null;
            }
            if (MemberLookup.FindField(headName, ctx.Frame, env) != null) return null;
            for (int j = 0; j < node.Segments.Count; j++)
            {
                var segment = node.Segments[j];
                if (segment.Connector != PathConnector.Dot || segment.GenericArguments.Count > 0)
                {
                    break;
                }
                // 前缀各段（命名空间限定链）必须无后缀
                if (j > 0 && node.Segments[j - 1].Suffixes.Count > 0) break;
                var prefix = new List<string> { headName };
                for (int k = 0; k < j; k++) prefix.Add(node.Segments[k].Name);
                var container = ResolvePathSilently(prefix, ctx.Frame, env);
                // 前缀不可解析（更长前缀亦然）或普通类型：交既有流程；
                // 命名空间：继续深入一段
                if (container == null) break;
                if (container is not TypeSymbol containerType) continue;
                var definition = containerType.ConstructedFrom ?? containerType;
                if (definition.Kind != TypeKind.EnumStruct) break;
                // 真实成员优先（FindMember 命中由既有容器路径处理）
                if (MemberLookup.FindMember(definition, segment.Name) != null) break;
                handled = true;
                // 泛型构造 enum 归口（与省略形式同一口径）
                if (containerType.ConstructedFrom != null)
                {
                    env.Error(segment.Span ?? node.Span,
                        "P3: generic enum cases are not supported yet (S11)");
                    return null;
                }
                var caseSymbol = EnumCaseFacility.FindCase(definition, segment.Name,
                    segment.Span ?? node.Span, env);
                if (caseSymbol == null) return null;
                // 模板绑定失败（HoleParameters 未落定）——静默（声明点已诊断）
                if (caseSymbol.HoleParameters == null) return null;
                BoundExpression? caseValue;
                var suffixStart = 0;
                if (segment.Suffixes.Count > 0
                    && segment.Suffixes[0].Kind == PathSuffixKind.Call)
                {
                    caseValue = EnumCaseFacility.BindParameterizedCall(node, segment.Name,
                        segment.Span ?? node.Span, segment.Suffixes[0].Arguments!, definition,
                        scope, ctx, env);
                    suffixStart = 1;
                }
                else if (caseSymbol.HoleParameters.Count > 0)
                {
                    env.Error(segment.Span ?? node.Span, $"Case '{caseSymbol.Name}' requires " +
                        $"{caseSymbol.HoleParameters.Count} argument(s)");
                    return null;
                }
                else
                {
                    caseValue = new BoundEnumCaseExpression(node, caseSymbol,
                        Array.Empty<BoundExpression>(),
                        env.GetEnumCaseFixedArguments(caseSymbol));
                }
                if (caseValue == null) return null;
                var folded = FoldSuffixes(node, caseValue, segment.Suffixes, suffixStart,
                    forAssignment && j == node.Segments.Count - 1, scope, ctx, env);
                if (folded == null) return null;
                return BindInstanceChain(node, folded, node.Segments.Skip(j + 1).ToList(),
                    scope, ctx, env, forAssignment);
            }
            return null;
        }

        // 段名序列静默解析（§12 全形 case 路径探测的前缀解析）：形态
        // 同 MemberLookup.ResolveContainer 但消费全段；失败或
        // ErrorType 返回 null（不落诊断——未命中由调用方走既有流程）
        private static SemanticSymbol? ResolvePathSilently(IReadOnlyList<string> names,
            BindFunctionFrame frame, BindEnvironment env)
        {
            var path = new Symbol();
            foreach (var name in names)
            {
                path.elements.Add(new SymbolElement { name = name });
            }
            var resolved = env.Names.ResolveSymbolPath(path, frame.FileCtx, frame.DeclaringType,
                frame.Method, allowImports: true, reportErrors: false, span: null);
            return resolved is ErrorTypeSymbol ? null : resolved;
        }

        // 首段是否值符号（g7 泛型实参分流的「首段非值」判定）：局部/参数/
        // 裸名字段任一命中即为值——值上的头段泛型实参无构造落点，保持
        // 「not supported yet」归口（与 TryBindEnumCasePath 的值优先
        // 探测同序）
        private static bool IsValueHead(string name, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            if (scope.LookupSymbol(name) != null) return true;
            if (!ctx.Frame.IsDefaultValueContext
                && ctx.Frame.Method.Parameters.Any(p => p.Name == name))
            {
                return true;
            }
            return MemberLookup.FindField(name, ctx.Frame, env) != null;
        }

        // 容器路径上成员段 Call 后缀（#20②）：`ns.make().field` /
        // `Type.factory()[0].x`——CallFacility 绑定调用（与纯调用形态同池），
        // 调用结果作 receiver，折叠该段剩余后缀后 BindInstanceChain 续链。
        // void 调用不能作值/receiver
        private static BoundExpression? BindContainerCallChain(PathExpressionASTNode node,
            int callSegIndex, Scope scope, BindContext ctx, BindEnvironment env, bool forAssignment)
        {
            var callSeg = node.Segments[callSegIndex];
            var calleeSegments = new List<string> { node.Head.Name! };
            for (int i = 0; i <= callSegIndex; i++)
            {
                calleeSegments.Add(node.Segments[i].Name);
            }
            var binding = CallFacility.BindCall(node, calleeSegments, callSeg.Suffixes[0].Arguments!,
                scope, ctx, env,
                callSeg.GenericArguments.Count > 0 ? callSeg.GenericArguments : null,
                containerTypeArguments: node.Head.GenericArguments.Count > 0
                    ? node.Head.GenericArguments : null);
            if (binding == null) return null;
            if (binding.IsInnerCall)
            {
                if (binding.IsVoid)
                {
                    env.Error(node.Span, "Method 'inner' has no result (void) " +
                        "and cannot be used as a value");
                    return null;
                }
                BoundExpression innerValue = new BoundInnerCallExpression(node, binding.Arguments,
                    binding.ResultType!,
                    forwardedGenericPacks: binding.ForwardedGenericPacks);
                var afterInner = node.Segments.Skip(callSegIndex + 1).ToList();
                var foldedInner = FoldSuffixes(node, innerValue, callSeg.Suffixes, 1,
                    forAssignment && afterInner.Count == 0, scope, ctx, env);
                if (foldedInner == null) return null;
                return BindInstanceChain(node, foldedInner, afterInner, scope, ctx, env,
                    forAssignment);
            }
            if (binding.IsSuperCall)
            {
                if (binding.IsVoid)
                {
                    env.Error(node.Span, "Method 'super' has no result (void) and cannot be used as a value");
                    return null;
                }
                BoundExpression superValue = new BoundSuperCallExpression(node, binding.Method,
                    binding.Arguments, binding.ResultType!, binding.TypeArguments, binding.GenericPack);
                var afterSuper = node.Segments.Skip(callSegIndex + 1).ToList();
                var foldedSuper = FoldSuffixes(node, superValue, callSeg.Suffixes, 1,
                    forAssignment && afterSuper.Count == 0, scope, ctx, env);
                if (foldedSuper == null) return null;
                return BindInstanceChain(node, foldedSuper, afterSuper, scope, ctx, env, forAssignment);
            }
            // S10：async 无结果调用有 Task 值——仅真 void 拒绝
            if (binding.ResultType == null)
            {
                env.Error(node.Span, $"Method '{binding.Method.Name}' has no result (void) " +
                    "and cannot be used as a value");
                return null;
            }
            BoundExpression callValue = binding.Receiver != null
                ? new BoundInstanceCallExpression(node, binding.Receiver, binding.Method,
                    binding.Arguments, binding.ResultType!, binding.TypeArguments,
                    binding.GenericPack)
                : new BoundCallExpression(node, binding.Method, binding.Arguments,
                    binding.ResultType!, binding.TypeArguments, binding.GenericPack,
                    binding.IsIndirect, binding.IndirectTarget);
            var afterCall = node.Segments.Skip(callSegIndex + 1).ToList();
            var foldedCall = FoldSuffixes(node, callValue, callSeg.Suffixes, 1,
                forAssignment && afterCall.Count == 0, scope, ctx, env);
            if (foldedCall == null) return null;
            return BindInstanceChain(node, foldedCall, afterCall, scope, ctx, env, forAssignment);
        }

    }
}
