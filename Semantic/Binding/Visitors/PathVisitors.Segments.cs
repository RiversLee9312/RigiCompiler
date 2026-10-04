
namespace RigiCompiler
{
    internal static partial class PathFacility
    {
        // Segments 职责；与主文件共享同一类型、字段及生命周期。

        // 实例成员链上色：首段已绑出 receiver，段序列沿 receiver 静态
        // 类型逐段上色（BindInstanceSegment 承担段后缀折叠：Call → 实例
        // 方法调用、Index → 字段后索引、无后缀 → 字段；S8c）。
        // forAssignment 仅传给最末段的最末后缀作索引写模式判定。
        // 返回链末端表达式
        private static BoundExpression? BindInstanceChain(ASTNode node, BoundExpression receiver,
            IReadOnlyList<PathSegmentASTNode> chainSegments, Scope scope, BindContext ctx,
            BindEnvironment env, bool forAssignment)
        {
            // F1/V-B 段级收口：入口 receiver（名头段 + 头段后缀折叠产物，
            // 如 arr[0] 的索引结果——不经 ExpressionDispatcher）与逐段
            // 中间结果同门检查有效可见性；驻留类型去重保证已报的引入点
            //（标注/推断/上游段/dispatcher 链末检查）不重复报
            UseSiteAccessibility.CheckChainValue(receiver, ctx, env);
            for (int i = 0; i < chainSegments.Count; i++)
            {
                var segment = chainSegments[i];
                // 毒化静默：receiver 已失败时不再报次生错误
                if (receiver.Type is ErrorTypeSymbol) return null;
                BoundExpression? next;
                if (segment.Connector == PathConnector.SafeDot)
                {
                    // 安全访问段（S7f，SYNTAX §3.4）
                    next = BindSafeSegment(segment, receiver, scope, ctx, env);
                }
                else if (segment.Connector == PathConnector.Colon)
                {
                    // wrapper place 段（S11，SYNTAX §14.5）
                    next = BindWrapperSegment(segment, receiver,
                        isTerminal: i == chainSegments.Count - 1,
                        forAssignment: forAssignment && i == chainSegments.Count - 1,
                        scope, ctx, env);
                }
                else
                {
                    // 普通段：可空值不提供隐式成员访问（须逐段标注 ?.，§3.4）
                    // （S9a：泛型参数 receiver 判型后不命中 nullable 分支）
                    if (receiver.Type is TypeSymbol { ConstructedFrom: not null } receiverType
                        && receiverType.ConstructedFrom == env.B.NullableDefinition)
                    {
                        env.Error(segment.Span,
                            $"Member '{segment.Name}' cannot be accessed on nullable type " +
                            $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'; use '?.' for safe access");
                        return null;
                    }
                    next = BindInstanceSegment(segment, receiver, scope, ctx, env,
                        forAssignment && i == chainSegments.Count - 1);
                }
                // SafeDot 段失败（BindSafeSegment 返回 null）在循环尾统一拦截
                if (next == null) return null;
                UseSiteAccessibility.CheckChainValue(next, ctx, env);
                receiver = next;
            }
            return receiver;
        }

        // wrapper place 段（S11，SYNTAX §14.1/§14.5）：`obj:W`——绑定在宿主
        // 上的那份 wrapper 的只读 place。wrapper 查找三源同池：宿主来源符号
        //（字段/局部的 AppliedWrappers——Value wrapper）、宿主静态类型
        //（Entity wrapper，构造类型回退定义）、泛型参数 with 约束（等价
        // AppliedWrappers，合成 WrapperApplication.FromConstraint）；按段名
        // 匹配，恰好一命中，零命中（无应用）/多命中（同名歧义）均诊断。
        // nullable 宿主拒绝（与普通段同口径；Colon 无安全访问形态）。
        // 只读禁令（§14.5 全拦截面收口）：链末且无后缀的 Colon 段即整体
        // 赋值/取值——拒绝；取值逃逸（变量初始化/实参/返回值/推断源/运算
        // 与类型检查操作数……）全部经路径绑定结果，收口于本处。带后缀
        //（索引成员访问）或非链末（字段/方法段继续消费）即成员访问接收者
        // ——合法。
        private static BoundExpression? BindWrapperSegment(PathSegmentASTNode segment,
            BoundExpression receiver, bool isTerminal, bool forAssignment, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            if (receiver.Type is TypeSymbol { ConstructedFrom: not null } nullableReceiver
                && nullableReceiver.ConstructedFrom == env.B.NullableDefinition)
            {
                env.Error(segment.Span,
                    $"Wrapper place ':{segment.Name}' cannot be accessed on nullable type " +
                    $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
                return null;
            }
            // 符号源判型剥 SmartCast 壳（收窄包装不改变宿主来源符号）
            var host = receiver is BoundSmartCastExpression smartCast
                ? smartCast.Operand : receiver;
            var symbolWrappers = host switch
            {
                BoundFieldAccessExpression fieldAccess => fieldAccess.Field.AppliedWrappers,
                BoundFieldReferenceExpression fieldReference => fieldReference.Field.AppliedWrappers,
                BoundValueReferenceExpression { Symbol: LocalSymbol local } =>
                    local.AppliedWrappers,
                _ => null,
            };
            var matches = new List<WrapperApplication>();
            if (symbolWrappers != null)
            {
                matches.AddRange(symbolWrappers.Where(w => w.Wrapper.Name == segment.Name));
            }
            if (receiver.Type is TypeSymbol hostType)
            {
                matches.AddRange((hostType.ConstructedFrom ?? hostType).AppliedWrappers
                    .Where(w => w.Wrapper.Name == segment.Name));
            }
            // 第三源：泛型参数 with 约束（M79 遗留 param:W）——with ≡ AppliedWrappers
            if (receiver.Type is GenericParameterSymbol gp)
            {
                foreach (var constraint in gp.Constraints)
                {
                    if (constraint.Kind != GenericConstraintKind.With) continue;
                    if (constraint.Bound is not TypeSymbol wrapperBound) continue;
                    var wrapper = wrapperBound.ConstructedFrom ?? wrapperBound;
                    if (wrapper.Name == segment.Name)
                    {
                        matches.Add(WrapperApplication.FromConstraint(wrapper));
                    }
                }
            }
            if (matches.Count == 0)
            {
                env.Error(segment.Span, $"'{BoundAnalysis.TypeDisplay(receiver.Type)}' " +
                    $"has no wrapper '{segment.Name}' applied");
                return null;
            }
            if (matches.Count > 1)
            {
                env.Error(segment.Span, $"Ambiguous wrapper '{segment.Name}' on " +
                    $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
                return null;
            }
            var place = new BoundWrapperAccessExpression(segment, receiver, matches[0]);
            // 带后缀（`obj:W[0]` 索引成员访问）：照常折叠
            if (segment.Suffixes.Count > 0)
            {
                return FoldSuffixes(segment, place, segment.Suffixes, 0, forAssignment, scope,
                    ctx, env);
            }
            // 非链末：place 作成员访问接收者，交后续段继续消费
            if (!isTerminal) return place;
            // 只读禁令（§14.5）：wrapper place 不可整体赋值也不可整体取值
            if (forAssignment)
            {
                env.Error(segment.Span, $"Cannot assign to wrapper place ':{segment.Name}'");
                return null;
            }
            env.Error(segment.Span, $"Wrapper place ':{segment.Name}' cannot be used as a " +
                "value (only as a member access receiver)");
            return null;
        }

        // 普通实例段（S8c 段后缀折叠）：无后缀 → 字段；首个后缀 Call →
        // 实例方法调用（消费该后缀）、Index → 字段访问（段名上色，后缀
        // 全部待折叠）；其余后缀按序折叠（.foo()[0] / .foo[0]）
        private static BoundExpression? BindInstanceSegment(PathSegmentASTNode segment,
            BoundExpression receiver, Scope scope, BindContext ctx, BindEnvironment env,
            bool forAssignment)
        {
            if (segment.Suffixes.Count == 0)
            {
                return BindInstanceFieldAccess(segment, receiver, segment.Name, env, ctx,
                    forAssignment);
            }
            BoundExpression value;
            int consumed;
            if (segment.Suffixes[0].Kind == PathSuffixKind.Call)
            {
                var call = CallFacility.BindInstanceMethodCall(segment, receiver, segment.Name,
                    segment.Suffixes[0].Arguments!, scope, ctx, env,
                    segment.GenericArguments.Count > 0 ? segment.GenericArguments : null);
                if (call == null) return null;
                // c7b 修复：同名字段 callable 协议（`hd.f()` 链式中间段）时
                // call.IsIndirect=true 且 IndirectTarget=字段访问——必须落成
                // BoundCallExpression（invoke.indirect），不得误用段 receiver
                // 落 BoundInstanceCallExpression（会把宿主当委托 cast 崩溃）
                // S10：async 无结果调用有 Task 值（同 BindCall 值位置口径）
                if (call.ResultType == null)
                {
                    // §14.5：语句位允许 wrapper place 上调 void 方法；仅链末
                    // 纯 Call（无后续后缀）收口，其余仍作值拒绝。
                    if (ctx.AllowVoidCall && segment.Suffixes.Count == 1)
                    {
                        return call.IsIndirect
                            ? new BoundCallExpression(segment, call.Method, call.Arguments,
                                env.B.Any, call.TypeArguments, call.GenericPack,
                                isIndirect: true, indirectTarget: call.IndirectTarget)
                            : new BoundInstanceCallExpression(segment, call.Receiver ?? receiver,
                                call.Method, call.Arguments, env.B.Any, call.TypeArguments,
                                call.GenericPack);
                    }
                    env.Error(segment.Span, $"Method '{call.Method.Name}' has no result " +
                        "(void) and cannot be used as a value");
                    return null;
                }
                // wrapper 可投影为顶层兼容函数（Serializable.deepCopy）；
                // 此时实参已包含宿主，不能再补一份段 receiver。
                value = !call.IsIndirect && call.Receiver == null && call.Method.Owner == null
                    ? new BoundCallExpression(segment, call.Method, call.Arguments,
                        call.ResultType!, call.TypeArguments, call.GenericPack)
                    : call.IsIndirect
                    ? new BoundCallExpression(segment, call.Method, call.Arguments,
                        call.ResultType!, call.TypeArguments, call.GenericPack,
                        isIndirect: true, indirectTarget: call.IndirectTarget)
                    : new BoundInstanceCallExpression(segment, call.Receiver ?? receiver,
                        call.Method, call.Arguments, call.ResultType!, call.TypeArguments,
                        call.GenericPack);
                consumed = 1;
            }
            else
            {
                var field = BindInstanceFieldAccess(segment, receiver, segment.Name, env, ctx);
                if (field == null) return null;
                value = field;
                consumed = 0;
            }
            return FoldSuffixes(segment, value, segment.Suffixes, consumed, forAssignment, scope,
                ctx, env);
        }

        // 值上的后缀折叠（S8c）：按序折叠——Index 后缀绑索引访问（forWrite
        // 且是全路径最后一个后缀时为写模式 setAtIndex，否则读模式
        // getAtIndex）；Call 后缀 = callable 协议间接调用（SYNTAX §5.2：
        // 值类型声明 operator call 即可像函数一样调用，BIL §15.3）
        private static BoundExpression? FoldSuffixes(ASTNode node, BoundExpression receiver,
            IReadOnlyList<PathSuffixASTNode> suffixes, int startIndex, bool forWrite,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            for (int i = startIndex; i < suffixes.Count; i++)
            {
                var suffix = suffixes[i];
                if (suffix.Kind == PathSuffixKind.Call)
                {
                var callableLookup = SymbolLookup.EffectiveMemberType(receiver.Type, env);
                if (receiver.Type is ErrorTypeSymbol
                    || CallFacility.BindIndirectCallOverload(suffix, receiver, callableLookup,
                        suffix.Arguments!, null, scope, ctx, env) is not { } valueCall)
                {
                    env.Error(suffix.Span,
                        "P3: value of type " +
                        $"'{BoundAnalysis.TypeDisplay(receiver.Type)}' is not callable " +
                        "(no operator call)");
                    return null;
                }
                    // S10：async 无结果调用有 Task 值——仅真 void 拒绝作值
                    //（语句位置的 void 间接调用归 CallForm 直写形态 f(args)）
                    if (valueCall.ResultType == null)
                    {
                        env.Error(suffix.Span, "Method 'call' has no result (void) " +
                            "and cannot be used as a value");
                        return null;
                    }
                    receiver = new BoundCallExpression(suffix, valueCall.Method,
                        valueCall.Arguments, valueCall.ResultType!,
                        valueCall.TypeArguments, valueCall.GenericPack,
                        isIndirect: true, indirectTarget: receiver);
                    continue;
                }
                var next = BindIndexAccess(suffix, receiver, suffix,
                    forWrite && i == suffixes.Count - 1, scope, ctx, env);
                if (next == null) return null;
                receiver = next;
            }
            return receiver;
        }

    }
}
