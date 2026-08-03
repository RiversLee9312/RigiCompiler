namespace LatteCompiler
{
    // 路径表达式（M42 统一形态）的值位置绑定（S5/S7c-2/S7f，SYNTAX §1.4/§3.4/§9）。
    // 形态分派：this 首段 → this 路径；纯调用形态 → 直接/实例调用；
    // 纯值路径（无后缀）→ 局部/参数/宿主与命名空间字段/容器成员；
    // 首段为值的多段 → 实例链上色；其余（表达式底座/索引/wrapper）
    // 报归口诊断。自旧 BindSession.BindPath 等迁移，行为不变。

    // 路径表达式值绑定（分派器入口；forAssignment = false）
    internal sealed class PathVisitor : ExpressionVisitor<PathVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            return PathFacility.BindPath((PathExpressionASTNode)node, scope, ctx, env,
                forAssignment: false);
        }
    }

    internal static class PathFacility
    {
        // S8b smart cast：引用收窄查询——收窄事实表命中且类型确实变窄时
        // 包 BoundSmartCastExpression（P4a 物化为显式 cast）
        private static BoundExpression ApplyNarrowing(ASTNode node, BoundExpression bound,
            NarrowKey? key, BindContext ctx)
        {
            if (key == null) return bound;
            var narrowed = ctx.Flow.LookupNarrow(key);
            if (narrowed == null || ReferenceEquals(narrowed, bound.Type)) return bound;
            return new BoundSmartCastExpression(node, bound, narrowed);
        }

        // 跨簇收窄包装入口（CallVisitors 调用链头用；语义同 ApplyNarrowing）
        public static BoundExpression ApplyNarrowingPublic(ASTNode node, BoundExpression bound,
            NarrowKey? key, BindContext ctx)
        {
            return ApplyNarrowing(node, bound, key, ctx);
        }

        // 赋值目标绑定入口（ExpressionStatementVisitor 专用）：
        // 定义而非「使用」——符号引用不经 unassigned 检查
        public static BoundExpression? VisitForAssignment(PathExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            return BindPath(node, scope, ctx, env, forAssignment: true);
        }

        // 路径绑定核心（PathVisitor 与 VisitForAssignment 共用）
        public static BoundExpression? BindPath(PathExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env, bool forAssignment)
        {
            // 表达式底座（(a).b / foo().b / 字面量.foo）：实例成员链归 S8
            if (node.Head.Expression != null)
            {
                env.Error(node.Span, "P3: instance member access is not supported yet (S8)");
                return null;
            }
            // 泛型实参（首段/段）：使用侧归 S9
            if (node.Head.GenericArguments.Count > 0
                || node.Segments.Any(s => s.GenericArguments.Count > 0))
            {
                env.Error(node.Span, "P3: generic type arguments are not supported yet (S9)");
                return null;
            }
            // this 首段（S7c-2）：值位置 this 或实例链起点
            if (node.Head.Name == "this")
            {
                return BindThisPath(node, scope, ctx, env);
            }
            // 纯调用形态 → 直接调用（静态/全局）或实例调用（首段为值）
            if (CallForm.TryGet(node, out var calleeSegments, out var callArguments))
            {
                var binding = CallFacility.BindCall(node, calleeSegments, callArguments!, scope,
                    ctx, env);
                if (binding == null) return null;
                if (binding.IsVoid)
                {
                    env.Error(node.Span, $"Method '{binding.Method.Name}' has no result (void) " +
                        "and cannot be used as a value");
                    return null;
                }
                if (binding.Receiver != null)
                {
                    return new BoundInstanceCallExpression(node, binding.Receiver,
                        binding.Method, binding.Arguments,
                        (TypeSymbol)binding.Method.ReturnType!);
                }
                return new BoundCallExpression(node, binding.Method, binding.Arguments,
                    (TypeSymbol)binding.Method.ReturnType!);
            }
            // 非调用形态的后缀与特殊连接符：逐一归口诊断
            if (node.Head.Suffixes.Any(s => s.Kind == PathSuffixKind.Index)
                || node.Segments.Any(s => s.Suffixes.Any(x => x.Kind == PathSuffixKind.Index)))
            {
                env.Error(node.Span, "P3: index access is not supported yet (S8)");
                return null;
            }
            if (node.Segments.Any(s => s.Connector == PathConnector.Colon))
            {
                env.Error(node.Span, "P3: wrapper access is not supported yet (S11)");
                return null;
            }
            // S7f：含 SafeDot 段的路径走实例链（BindInstanceChain 的 SafeDot
            // 分派）；首段带后缀（foo()?.bar 等表达式结果底座）仍归 S8
            bool hasSafeDot = node.Segments.Any(s => s.Connector == PathConnector.SafeDot);
            if (hasSafeDot && node.Head.Suffixes.Count > 0)
            {
                env.Error(node.Span, "P3: instance member access is not supported yet (S8)");
                return null;
            }
            if (!hasSafeDot
                && (node.Head.Suffixes.Count > 0 || node.Segments.Any(s => s.Suffixes.Count > 0)))
            {
                env.Error(node.Span, "P3: instance member access is not supported yet (S8)");
                return null;
            }
            // 纯值路径：单段查找序 局部 → 参数 → 命名空间链字段 → 通配 import；
            // 多段 = 容器 + 末段成员
            if (node.Segments.Count == 0)
            {
                var name = node.Head.Name!;
                // switch pattern 占位（S7d）：占位栈非空时 _ 命中栈顶
                // selector（嵌套 switch 逐层向内；栈空 = _ 不在 pattern
                // 上下文，落普通查找报未定义名）
                if (name == "_" && ctx.SwitchSelectors.Count > 0)
                {
                    var placeholderSelector = ctx.SwitchSelectors.Peek();
                    var placeholder = new BoundSwitchPlaceholderExpression(node,
                        placeholderSelector, placeholderSelector.Type);
                    // S8b（Q4）：分支体入口已把 selector 键收窄（`(_ is T)`
                    // 分支）——占位引用同收窄
                    return ApplyNarrowing(node, placeholder,
                        ConditionFactsExtractor.TryKeyOf(placeholderSelector, ctx), ctx);
                }
                var local = scope.Lookup(name);
                if (local != null)
                {
                    if (!forAssignment && !ctx.Flow.IsAssigned(local))
                    {
                        env.Error(node.Span, $"Use of unassigned local variable '{name}'");
                    }
                    // 源码局部 Type 恒非空（null 是 P4a 合成 .breakid
                    // 局部的特例，P3 不可能遇到）
                    var localReference = new BoundValueReferenceExpression(node, local,
                        local.Type!);
                    // S8b：收窄区域内包 SmartCast（赋值目标位置不收窄——
                    // forAssignment 是定义而非读取）
                    return forAssignment
                        ? (BoundExpression)localReference
                        : ApplyNarrowing(node, localReference, NarrowKey.ForSymbol(local), ctx);
                }
                var parameter = ctx.Method.Parameters.FirstOrDefault(p => p.Name == name);
                if (parameter != null)
                {
                    if (parameter.Type is not TypeSymbol paramType)
                    {
                        env.Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                        return null;
                    }
                    var paramReference = new BoundValueReferenceExpression(node, parameter,
                        paramType);
                    return forAssignment
                        ? (BoundExpression)paramReference
                        : ApplyNarrowing(node, paramReference, NarrowKey.ForSymbol(parameter), ctx);
                }
                var field = MemberLookup.FindField(name, ctx, env);
                if (field != null)
                {
                    return BindFieldReference(node, field, ctx, env);
                }
                env.Error(node.Span, $"Undefined name: '{name}'");
                return null;
            }
            // 多段：首段命中局部/参数 → 实例链上色（S7c-2）；
            // 否则前 N-1 段解析为容器，末段查成员
            var segments = PathSegmentNames(node);
            var headLocal = scope.Lookup(segments[0]);
            var headParameter = headLocal == null
                ? ctx.Method.Parameters.FirstOrDefault(p => p.Name == segments[0]) : null;
            if (headLocal != null || headParameter != null)
            {
                BoundExpression headReceiver;
                if (headLocal != null)
                {
                    if (!forAssignment && !ctx.Flow.IsAssigned(headLocal))
                    {
                        env.Error(node.Span,
                            $"Use of unassigned local variable '{segments[0]}'");
                    }
                    // 源码局部 Type 恒非空（同单段分支）
                    headReceiver = new BoundValueReferenceExpression(node, headLocal,
                        headLocal.Type!);
                    if (!forAssignment)
                    {
                        // S8b：实例链头收窄（a.b.c 的 a 在收窄区域内）
                        headReceiver = ApplyNarrowing(node, headReceiver,
                            NarrowKey.ForSymbol(headLocal), ctx);
                    }
                }
                else
                {
                    if (headParameter!.Type is not TypeSymbol headParamType)
                    {
                        env.Error(node.Span,
                            "P3: generic type parameters are not supported yet (S9)");
                        return null;
                    }
                    headReceiver = new BoundValueReferenceExpression(node, headParameter,
                        headParamType);
                    if (!forAssignment)
                    {
                        headReceiver = ApplyNarrowing(node, headReceiver,
                            NarrowKey.ForSymbol(headParameter), ctx);
                    }
                }
                return BindInstanceChain(node, headReceiver, node.Segments, scope, ctx, env);
            }
            var container = MemberLookup.ResolveContainer(segments, node.Span, ctx, env);
            if (container == null) return null;
            var member = MemberLookup.FindMember(container, segments[^1]);
            var pathText = string.Join(".", segments);
            return member switch
            {
                FieldSymbol memberField => BindFieldReference(node, memberField, ctx, env),
                MethodSymbol => ErrorAndNull(env, node.Span,
                    $"Method '{pathText}' cannot be used as a value"),
                null => ErrorAndNull(env, node.Span, $"Undefined name: '{pathText}'"),
                _ => ErrorAndNull(env, node.Span, $"'{pathText}' cannot be used as a value"),
            };
        }

        // 路径的段名序列（首段名 + 各段名）；调用方保证无表达式底座
        private static List<string> PathSegmentNames(PathExpressionASTNode node)
        {
            var segments = new List<string> { node.Head.Name! };
            segments.AddRange(node.Segments.Select(s => s.Name));
            return segments;
        }

        // 字段引用上色（迁移自旧 BindSession.BindFieldReference，行为不变）：
        // 无标注字段类型推断归后续（诊断）；泛型字段类型最小替换（S7f）：
        // 实例字段以宿主（method.Owner）为 receiver 链取构造实参；全局/
        // static 字段声明类型不含泛型参数，原样直通；实例字段在实例上下文
        // 补 this（静态上下文诊断）
        public static BoundExpression? BindFieldReference(ASTNode node, FieldSymbol field,
            BindContext ctx, BindEnvironment env)
        {
            if (field.FieldType == null)
            {
                env.Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                    "(field type inference is not supported yet)");
                return null;
            }
            var fieldType = SymbolLookup.SubstituteFieldType(field, ctx.Method.Owner);
            if (fieldType == null || SymbolLookup.ContainsGenericParameter(fieldType))
            {
                env.Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                return null;
            }
            if (field.Owner != null && !field.IsStatic)
            {
                // 实例字段（S7c-2）：当前上下文有 this（实例方法/ext 方法
                // 体内，method.Owner 统一承载宿主）→ this.field；静态
                // 上下文（static 方法/全局函数）→ 诊断
                if (ctx.Method.Owner != null && !ctx.Method.IsStatic)
                {
                    var access = new BoundFieldAccessExpression(node,
                        new BoundThisExpression(node, ctx.Method.Owner), field, fieldType);
                    // S8b：this.f 收窄（const 字段 + 非 init 体内，经
                    // ConstFieldRules.IsNarrowable 判定）
                    return ApplyNarrowing(node, access,
                        NarrowKey.TryFromFieldAccess(access.Receiver, field, ctx), ctx);
                }
                env.Error(node.Span, $"P3: instance field '{field.Name}' requires a receiver" +
                    " ('this' is not available in a static context)");
                return null;
            }
            // 全局/静态字段：键 = 字段符号本身（const 全局字段收窄永不失效）
            var reference = new BoundFieldReferenceExpression(node, field, fieldType);
            return ApplyNarrowing(node, reference,
                ConstFieldRules.IsNarrowable(field, ctx) ? NarrowKey.ForSymbol(field) : null, ctx);
        }

        // this 路径（S7c-2，SYNTAX §9）：值位置 this（Type = 宿主类型，
        // method.Owner 统一承载——普通成员为声明类型，ext 方法为目标类型）
        // 或实例链起点。静态上下文（static 方法/全局函数）不可用
        private static BoundExpression? BindThisPath(PathExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            if (ctx.Method.Owner == null || ctx.Method.IsStatic)
            {
                env.Error(node.Span, "P3: 'this' is not available in a static context");
                return null;
            }
            // this 自身的后缀（this(...) / this[...]）：无意义形态
            if (node.Head.Suffixes.Count > 0)
            {
                env.Error(node.Span, "P3: instance member access is not supported yet (S8)");
                return null;
            }
            BoundExpression receiver = new BoundThisExpression(node, ctx.Method.Owner);
            if (node.Segments.Count == 0)
            {
                return receiver;
            }
            return BindInstanceChain(node, receiver, node.Segments, scope, ctx, env);
        }

        // 实例成员链上色：首段已绑出 receiver，段序列沿 receiver 静态
        // 类型逐段上色——段带恰好一个 Call 后缀 → 实例方法调用；无后缀
        // → 实例字段访问；其余（Index/多后缀）归口 S8。返回链末端表达式
        private static BoundExpression? BindInstanceChain(ASTNode node, BoundExpression receiver,
            IReadOnlyList<PathSegmentASTNode> chainSegments, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            foreach (var segment in chainSegments)
            {
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
                    env.Error(segment.Span, "P3: wrapper access is not supported yet (S11)");
                    return null;
                }
                else
                {
                    // 普通段：可空值不提供隐式成员访问（须逐段标注 ?.，§3.4）
                    if (receiver.Type.ConstructedFrom == env.B.NullableDefinition)
                    {
                        env.Error(segment.Span,
                            $"Member '{segment.Name}' cannot be accessed on nullable type " +
                            $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'; use '?.' for safe access");
                        return null;
                    }
                    next = BindInstanceSegment(segment, receiver, scope, ctx, env);
                    if (next == null) return null;
                }
                if (next == null) return null;
                receiver = next;
            }
            return receiver;
        }

        // 普通实例段（无后缀 → 字段；恰一个 Call 后缀 → 实例方法调用）
        private static BoundExpression? BindInstanceSegment(PathSegmentASTNode segment,
            BoundExpression receiver, Scope scope, BindContext ctx, BindEnvironment env)
        {
            if (segment.Suffixes.Count == 0)
            {
                return BindInstanceFieldAccess(segment, receiver, segment.Name, env, ctx);
            }
            if (segment.Suffixes.Count == 1
                && segment.Suffixes[0].Kind == PathSuffixKind.Call)
            {
                var call = CallFacility.BindInstanceMethodCall(segment, receiver, segment.Name,
                    segment.Suffixes[0].Arguments!, scope, ctx, env);
                if (call == null) return null;
                if (call.IsVoid)
                {
                    env.Error(segment.Span, $"Method '{call.Method.Name}' has no result " +
                        "(void) and cannot be used as a value");
                    return null;
                }
                return new BoundInstanceCallExpression(segment, receiver,
                    call.Method, call.Arguments, (TypeSymbol)call.Method.ReturnType!);
            }
            env.Error(segment.Span,
                "P3: instance member access is not supported yet (S8)");
            return null;
        }

        // 安全访问段（S7f，SYNTAX §3.4）：receiver 必须 Nullable<T>；段在
        // 非空 T 上绑定（占位叶子承载 unwrap 后的 receiver，P4a 物化替换）；
        // 结果类型：成员类型已可空则原样（不二次包装），否则包 Nullable
        private static BoundExpression? BindSafeSegment(PathSegmentASTNode segment,
            BoundExpression receiver, Scope scope, BindContext ctx, BindEnvironment env)
        {
            if (receiver.Type.ConstructedFrom != env.B.NullableDefinition
                || receiver.Type.TypeArguments![0] is not TypeSymbol element)
            {
                env.Error(segment.Span, $"Safe access '?.' requires a nullable receiver " +
                    $"(got '{BoundAnalysis.TypeDisplay(receiver.Type)}')");
                return null;
            }
            var placeholder = new BoundSafeAccessReceiverExpression(segment, element);
            var access = BindInstanceSegment(segment, placeholder, scope, ctx, env);
            if (access == null) return null;
            var resultType = access.Type.ConstructedFrom == env.B.NullableDefinition
                ? access.Type
                : env.Unit.Symbols.GetNullable(access.Type);
            return new BoundSafeAccessExpression(segment, receiver, placeholder, access,
                resultType);
        }

        // 实例字段访问：receiver 静态类型沿 BaseType 链查找（接口无
        // 实例字段；ext 注册字段同路径；访问控制检查归 S8e）
        public static BoundExpression? BindInstanceFieldAccess(ASTNode node,
            BoundExpression receiver, string name, BindEnvironment env, BindContext ctx)
        {
            var field = SymbolLookup.FindInstanceField(receiver.Type, name);
            if (field == null)
            {
                env.Error(node.Span, SymbolLookup.FindInstanceMethods(receiver.Type, name).Count > 0
                    ? $"'{name}' on type '{BoundAnalysis.TypeDisplay(receiver.Type)}' is not a field"
                    : $"Undefined member '{name}' on type " +
                        $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
                return null;
            }
            if (field.FieldType == null)
            {
                env.Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                    "(field type inference is not supported yet)");
                return null;
            }
            // 泛型字段类型的最小替换（S7f 解构场景）：声明类型是宿主泛型
            // 参数时按 receiver 链上的构造类型取实参；取不到具体类型归 S9
            var fieldType = SymbolLookup.SubstituteFieldType(field, receiver.Type);
            if (fieldType == null || SymbolLookup.ContainsGenericParameter(fieldType))
            {
                env.Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                return null;
            }
            var access = new BoundFieldAccessExpression(node, receiver, field, fieldType);
            // S8b：const 字段稳定链收窄（TryFromFieldAccess 含 IsNarrowable
            // 判定；不稳定链返回 null 直通）
            return ApplyNarrowing(node, access,
                NarrowKey.TryFromFieldAccess(receiver, field, ctx), ctx);
        }

        private static BoundExpression? ErrorAndNull(BindEnvironment env, CharRange? span,
            string message)
        {
            env.Error(span, message);
            return null;
        }
    }
}
