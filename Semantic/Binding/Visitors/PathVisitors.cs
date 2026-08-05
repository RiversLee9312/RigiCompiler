namespace LatteCompiler
{
    // 路径表达式（M42 统一形态）的值位置绑定（S5/S7c-2/S7f/S8c，
    // SYNTAX §1.4/§3.4/§9/§13.2）。
    // 形态分派：泛型实参检查（S9）→ 表达式底座（(a+b).c / new X().c）→
    // this 首段 → 纯调用形态（直接/静态/裸名实例调用）→ Colon 归口（S11）→
    // 泛化链折叠：首段按值解析（占位/局部/参数/裸名字段）→ 首段后缀折叠
    // （索引访问 getAtIndex/setAtIndex；值调用归口）→ 实例链上色；
    // 首段非值且首个后缀是 Call → 调用结果底座（foo().c / foo()[0] /
    // foo()?.bar）；否则容器路径（末段成员后缀折叠，Type.staticField[0] /
    // ns.field[i]；中间段带后缀保持归口）。自旧 BindSession.BindPath 等
    // 迁移；S8c 解开原表达式底座/索引/后缀链的 S8 归口拦截。

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
            NarrowKey? key, FlowState flow)
        {
            if (key == null) return bound;
            var narrowed = flow.LookupNarrow(key);
            if (narrowed == null || ReferenceEquals(narrowed, bound.Type)) return bound;
            return new BoundSmartCastExpression(node, bound, narrowed);
        }

        // 跨簇收窄包装入口（CallVisitors 调用链头用；语义同 ApplyNarrowing）
        public static BoundExpression ApplyNarrowingPublic(ASTNode node, BoundExpression bound,
            NarrowKey? key, FlowState flow)
        {
            return ApplyNarrowing(node, bound, key, flow);
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
            // 表达式底座（S8c：(a+b).c / new X().c / 字面量.foo）
            if (node.Head.Expression != null)
            {
                return BindExpressionBasePath(node, scope, ctx, env, forAssignment);
            }
            // this 首段（S7c-2）：值位置 this 或实例链起点
            if (node.Head.Name == "this")
            {
                return BindThisPath(node, scope, ctx, env, forAssignment);
            }
            // 纯调用形态 → 直接调用（静态/全局）或实例调用（首段为值）。
            // S9b：显式泛型实参随调用形态提取（CallForm out 参数）
            if (CallForm.TryGet(node, out var calleeSegments, out var callArguments,
                out var genericArguments))
            {
                var binding = CallFacility.BindCall(node, calleeSegments, callArguments!, scope,
                    ctx, env, genericArguments);
                if (binding == null) return null;
                // S10：async 无结果调用有 Task 值（ResultType 非空）——仅
                // 真 void（ResultType == null）拒绝作值
                if (binding.ResultType == null)
                {
                    env.Error(node.Span, $"Method '{binding.Method.Name}' has no result (void) " +
                        "and cannot be used as a value");
                    return null;
                }
                if (binding.Receiver != null)
                {
                    return new BoundInstanceCallExpression(node, binding.Receiver,
                        binding.Method, binding.Arguments, binding.ResultType!,
                        binding.TypeArguments, binding.GenericPack);
                }
                return new BoundCallExpression(node, binding.Method, binding.Arguments,
                    binding.ResultType!, binding.TypeArguments, binding.GenericPack);
            }
            // 非调用形态的泛型实参（`x\<T>` 无调用后缀/链上段带实参）——
            // S9b 仍归口（调用实参已由调用形态消费）
            if (node.Head.GenericArguments.Count > 0
                || node.Segments.Any(s => s.GenericArguments.Count > 0))
            {
                env.Error(node.Span, "P3: generic type arguments are not supported yet (S9)");
                return null;
            }
            // wrapper 段（:连接符）归口 S11
            if (node.Segments.Any(s => s.Connector == PathConnector.Colon))
            {
                env.Error(node.Span, "P3: wrapper access is not supported yet (S11)");
                return null;
            }
            // ===== S8c 泛化链折叠：首段按值解析 → 首段后缀折叠 → 实例链 =====
            // 首段按值解析（switch 占位/局部/参数/裸名字段——单段与多段共用；
            // 局部/参数的 unassigned 检查与收窄包装仅在读语义（!forAssignment）
            // ——赋值目标位置是定义而非读取）
            var headName = node.Head.Name!;
            BoundExpression? headValue = null;
            if (headName == "_" && ctx.Labels.CurrentSelector is { } placeholderSelector)
            {
                // switch pattern 占位（S7d）：占位栈非空时 _ 命中栈顶
                // selector（嵌套 switch 逐层向内；栈空 = _ 不在 pattern
                // 上下文，落普通查找报未定义名）
                var placeholder = new BoundSwitchPlaceholderExpression(node,
                    placeholderSelector, placeholderSelector.Type);
                // S8b（Q4）：分支体入口已把 selector 键收窄（`(_ is T)`
                // 分支）——占位引用同收窄
                headValue = ApplyNarrowing(node, placeholder,
                    ConditionFactsExtractor.TryKeyOf(placeholderSelector, ctx.Frame), ctx.Flow);
            }
            else
            {
                // value 别名（S8e，SYNTAX §9.4.1）：backing 形态访问器体内
                // 裸名 value 即 backing 字段——拦截在作用域链查找之前
                if (headName == "value" && ctx.Accessor.Field != null
                    && ctx.Accessor.Field.FieldType is TypeSymbol backingType
                    && !SymbolLookup.ContainsGenericParameter(backingType))
                {
                    // getter 体内 value 只读：整体作赋值 place 时拦截
                    // （value.x = 1 这类深写仍按 backing 读取后字段写处理）
                    if (forAssignment && node.Segments.Count == 0
                        && node.Head.Suffixes.Count == 0 && !ctx.Accessor.IsSetter)
                    {
                        env.Error(node.Span, "Cannot assign to 'value' in a getter");
                        return null;
                    }
                    headValue = MakeBackingFieldReference(node, ctx.Accessor.Field, backingType,
                        ctx.Frame);
                }
                else
                {
                    var headLocal = scope.Lookup(headName);
                    if (headLocal != null)
                    {
                        if (!forAssignment && !ctx.Flow.IsAssigned(headLocal))
                        {
                            env.Error(node.Span, $"Use of unassigned local variable '{headName}'");
                        }
                        // 源码局部 Type 恒非空（null 是 P4a 合成 .breakid
                        // 局部的特例，P3 不可能遇到）
                        headValue = new BoundValueReferenceExpression(node, headLocal,
                            headLocal.Type!);
                        // S8b：收窄区域内包 SmartCast
                        if (!forAssignment)
                        {
                            headValue = ApplyNarrowing(node, headValue,
                                NarrowKey.ForSymbol(headLocal), ctx.Flow);
                        }
                    }
                    else
                    {
                        // 默认值表达式上下文看不到函数形参（SYNTAX §4.2 声明点作用域）
                        var headParameter = ctx.Frame.IsDefaultValueContext ? null
                            : ctx.Frame.Method.Parameters
                            .FirstOrDefault(p => p.Name == headName);
                        if (headParameter != null)
                        {
                            // S9a 放行：参数类型可为泛型参数（引用相等身份）；
                            // S9d：可变参数引用定型为 Array\<元素类型\>
                            // （体内视角是包数组；P4 发射映射 .vargs.<名>）
                            var paramType = headParameter.IsVariadic
                                || headParameter.IsNamedVariadic
                                ? (SemanticSymbol)env.Unit.Symbols.GetConstructedType(
                                    env.B.ArrayDefinition, headParameter.Type!)
                                : headParameter.Type!;
                            headValue = new BoundValueReferenceExpression(node, headParameter,
                                paramType);
                            if (!forAssignment)
                            {
                                headValue = ApplyNarrowing(node, headValue,
                                    NarrowKey.ForSymbol(headParameter), ctx.Flow);
                            }
                        }
                        else
                        {
                            // 裸名字段（宿主成员/命名空间链/通配 import；实例字段
                            // 在实例上下文补 this 由 BindFieldReference 承担）
                            var headField = MemberLookup.FindField(headName, ctx.Frame, env);
                            if (headField != null)
                            {
                                headValue = BindFieldReference(node, headField, ctx, env,
                                    forAssignment && node.Segments.Count == 0
                                        && node.Head.Suffixes.Count == 0);
                                if (headValue == null) return null;
                            }
                        }
                    }
                }
            }
            if (headValue != null)
            {
                // 首段为值：折叠首段后缀（索引写模式仅限全路径最后一步），
                // 再交实例链上色
                var folded = FoldSuffixes(node, headValue, node.Head.Suffixes, 0,
                    forAssignment && node.Segments.Count == 0, scope, ctx, env);
                if (folded == null) return null;
                return BindInstanceChain(node, folded, node.Segments, scope, ctx, env,
                    forAssignment);
            }
            // 首段非值且首个后缀是 Call → 调用结果底座（S8c：foo().c /
            // foo()[0] / foo()?.bar）——先按单段直接调用绑定，再折叠其余
            // 后缀、交实例链
            if (node.Head.Suffixes.Count > 0
                && node.Head.Suffixes[0].Kind == PathSuffixKind.Call)
            {
                var callBase = CallFacility.BindCall(node, new List<string> { headName },
                    node.Head.Suffixes[0].Arguments!, scope, ctx, env,
                    node.Head.GenericArguments.Count > 0 ? node.Head.GenericArguments : null);
                if (callBase == null) return null;
                // S10：async 无结果调用有 Task 值（同 BindCall 值位置口径）
                if (callBase.ResultType == null)
                {
                    env.Error(node.Span, $"Method '{callBase.Method.Name}' has no result (void) " +
                        "and cannot be used as a value");
                    return null;
                }
                BoundExpression callValue = callBase.Receiver != null
                    ? new BoundInstanceCallExpression(node, callBase.Receiver, callBase.Method,
                        callBase.Arguments, callBase.ResultType!, callBase.TypeArguments,
                        callBase.GenericPack)
                    : new BoundCallExpression(node, callBase.Method, callBase.Arguments,
                        callBase.ResultType!, callBase.TypeArguments, callBase.GenericPack);
                var foldedCall = FoldSuffixes(node, callValue, node.Head.Suffixes, 1,
                    forAssignment && node.Segments.Count == 0, scope, ctx, env);
                if (foldedCall == null) return null;
                return BindInstanceChain(node, foldedCall, node.Segments, scope, ctx, env,
                    forAssignment);
            }
            // 单段未命中：纯名字或索引非值（ns[0] 形态——命名空间/类型
            // 不作为值参与索引），同归未定义名
            if (node.Segments.Count == 0)
            {
                env.Error(node.Span, $"Undefined name: '{headName}'");
                return null;
            }
            // 容器路径（多段）：首段或中间段带后缀保持归口（容器成员链上
            // 的后缀折叠未支持）；末段成员字段解析后折叠末段后缀
            // （S8c：Type.staticField[0] / ns.field[i]）
            if (node.Head.Suffixes.Count > 0
                || node.Segments.Take(node.Segments.Count - 1).Any(s => s.Suffixes.Count > 0))
            {
                env.Error(node.Span, "P3: instance member access is not supported yet (S8)");
                return null;
            }
            var segments = PathSegmentNames(node);
            var container = MemberLookup.ResolveContainer(segments, node.Span, ctx.Frame, env);
            if (container == null) return null;
            var member = MemberLookup.FindMember(container, segments[^1]);
            var pathText = string.Join(".", segments);
            switch (member)
            {
                case FieldSymbol memberField:
                    var fieldValue = BindFieldReference(node, memberField, ctx, env,
                        forAssignment && node.Segments[^1].Suffixes.Count == 0);
                    if (fieldValue == null) return null;
                    return FoldSuffixes(node, fieldValue, node.Segments[^1].Suffixes, 0,
                        forAssignment, scope, ctx, env);
                case MethodSymbol:
                    return ErrorAndNull(env, node.Span,
                        $"Method '{pathText}' cannot be used as a value");
                case null:
                    return ErrorAndNull(env, node.Span, $"Undefined name: '{pathText}'");
                default:
                    return ErrorAndNull(env, node.Span, $"'{pathText}' cannot be used as a value");
            }
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
        // 补 this（静态上下文诊断）。
        // S8e（SYNTAX §16.1/§9.4.1）：使用点访问控制——带访问器字段读需
        // getter 存在且可见（写由赋值侧检查 setter，forAssignment 直达时
        // 跳过读侧检查），字段自身可见性不再检查（由访问器承载）；
        // 无访问器字段读写字位均检查字段可见性
        public static BoundExpression? BindFieldReference(ASTNode node, FieldSymbol field,
            BindContext ctx, BindEnvironment env, bool forAssignment = false)
        {
            if (field.Getter != null || field.Setter != null)
            {
                if (!forAssignment && !CheckReadable(field, node.Span, ctx, env))
                {
                    return null;
                }
            }
            else if (!ctx.Frame.CanAccess(field))
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(field));
                return null;
            }
            if (field.FieldType == null)
            {
                env.Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                    "(field type inference is not supported yet)");
                return null;
            }
            var fieldType = SymbolLookup.SubstituteFieldType(field, ctx.Frame.Method.Owner);
            // S9a 放行：替换失败的泛型参数类型原样保留（定义级宿主场景）
            if (fieldType == null)
            {
                env.Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                    "(field type inference is not supported yet)");
                return null;
            }
            if (field.Owner != null && !field.IsStatic)
            {
                // 实例字段（S7c-2）：当前上下文有 this（实例方法/ext 方法
                // 体内，method.Owner 统一承载宿主）→ this.field；静态
                // 上下文（static 方法/全局函数/默认值表达式）→ 诊断
                if (ctx.Frame.HasThis)
                {
                    var access = new BoundFieldAccessExpression(node,
                        new BoundThisExpression(node, ctx.Frame.Method.Owner!), field, fieldType);
                    // S8b：this.f 收窄（const 字段 + 非 init 体内，经
                    // ConstFieldRules.IsNarrowable 判定）
                    return ApplyNarrowing(node, access,
                        NarrowKey.TryFromFieldAccess(access.Receiver, field, ctx.Frame),
                        ctx.Flow);
                }
                env.Error(node.Span, $"P3: instance field '{field.Name}' requires a receiver" +
                    " ('this' is not available in a static context)");
                return null;
            }
            // 全局/静态字段：键 = 字段符号本身（const 全局字段收窄永不失效）
            var reference = new BoundFieldReferenceExpression(node, field, fieldType);
            return ApplyNarrowing(node, reference,
                ConstFieldRules.IsNarrowable(field, ctx.Frame) ? NarrowKey.ForSymbol(field) : null,
                ctx.Flow);
        }

        // 带访问器字段的读侧检查（S8e，SYNTAX §9.4.1；BindFieldReference
        // 与 BindInstanceFieldAccess 共用）：getter 存在且自身可见
        private static bool CheckReadable(FieldSymbol field, CharRange? span, BindContext ctx,
            BindEnvironment env)
        {
            if (field.Getter == null)
            {
                env.Error(span, $"'{field.Name}' has no getter");
                return false;
            }
            if (!ctx.Frame.CanAccess(field.Getter))
            {
                env.Error(span, $"'{field.Name}' getter is inaccessible due to its " +
                    "accessibility level");
                return false;
            }
            return true;
        }

        // backing 字段直达节点（S8e，SYNTAX §9.4.1）：访问器体内 value
        // 别名与驱动合成（隐含赋值/自动访问器体）共用——实例补 this，
        // 静态/全局直引；不接名称解析、不走访问器/访问控制检查
        // （backing 直达是编译器机制内部路径）
        public static BoundExpression MakeBackingFieldReference(ASTNode node, FieldSymbol field,
            SemanticSymbol fieldType, BindFunctionFrame frame)
        {
            if (field.Owner != null && !field.IsStatic)
            {
                return new BoundFieldAccessExpression(node,
                    new BoundThisExpression(node, frame.Method.Owner!), field, fieldType);
            }
            return new BoundFieldReferenceExpression(node, field, fieldType);
        }

        // this 路径（S7c-2，SYNTAX §9）：值位置 this（Type = 宿主类型，
        // method.Owner 统一承载——普通成员为声明类型，ext 方法为目标类型）
        // 或实例链起点；S8c 起首段后缀折叠（this[i] / this[i] = x 索引
        // 访问；this(...) 值调用未支持，由 FoldSuffixes 归口）。静态上下文
        // （static 方法/全局函数）不可用
        private static BoundExpression? BindThisPath(PathExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env, bool forAssignment)
        {
            if (!ctx.Frame.HasThis)
            {
                env.Error(node.Span, "P3: 'this' is not available in a static context");
                return null;
            }
            BoundExpression receiver = new BoundThisExpression(node, ctx.Frame.Method.Owner!);
            var folded = FoldSuffixes(node, receiver, node.Head.Suffixes, 0,
                forAssignment && node.Segments.Count == 0, scope, ctx, env);
            if (folded == null) return null;
            if (node.Segments.Count == 0)
            {
                return folded;
            }
            return BindInstanceChain(node, folded, node.Segments, scope, ctx, env, forAssignment);
        }

        // 表达式底座路径（S8c）：(a+b).c / new X().c / 字面量.foo——底座
        // 表达式先绑定（内部路径经 PathVisitor 递归上色），再折叠首段
        // 后缀、交实例链
        private static BoundExpression? BindExpressionBasePath(PathExpressionASTNode node,
            Scope scope, BindContext ctx, BindEnvironment env, bool forAssignment)
        {
            var receiver = ExpressionDispatcher.Visit(node.Head.Expression!.Expression, scope,
                ctx, env);
            if (receiver == null) return null;
            var folded = FoldSuffixes(node, receiver, node.Head.Suffixes, 0,
                forAssignment && node.Segments.Count == 0, scope, ctx, env);
            if (folded == null) return null;
            return BindInstanceChain(node, folded, node.Segments, scope, ctx, env, forAssignment);
        }

        // 实例成员链上色：首段已绑出 receiver，段序列沿 receiver 静态
        // 类型逐段上色（BindInstanceSegment 承担段后缀折叠：Call → 实例
        // 方法调用、Index → 字段后索引、无后缀 → 字段；S8c）。
        // forAssignment 仅传给最末段的最末后缀作索引写模式判定。
        // 返回链末端表达式
        private static BoundExpression? BindInstanceChain(ASTNode node, BoundExpression receiver,
            IReadOnlyList<PathSegmentASTNode> chainSegments, Scope scope, BindContext ctx,
            BindEnvironment env, bool forAssignment)
        {
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
                    env.Error(segment.Span, "P3: wrapper access is not supported yet (S11)");
                    return null;
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
                    if (next == null) return null;
                }
                if (next == null) return null;
                receiver = next;
            }
            return receiver;
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
                // S10：async 无结果调用有 Task 值（同 BindCall 值位置口径）
                if (call.ResultType == null)
                {
                    env.Error(segment.Span, $"Method '{call.Method.Name}' has no result " +
                        "(void) and cannot be used as a value");
                    return null;
                }
                value = new BoundInstanceCallExpression(segment, receiver,
                    call.Method, call.Arguments, call.ResultType!, call.TypeArguments);
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
        // getAtIndex）；Call 后缀是值调用/函数值形态（未支持）
        private static BoundExpression? FoldSuffixes(ASTNode node, BoundExpression receiver,
            IReadOnlyList<PathSuffixASTNode> suffixes, int startIndex, bool forWrite,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            for (int i = startIndex; i < suffixes.Count; i++)
            {
                var suffix = suffixes[i];
                if (suffix.Kind == PathSuffixKind.Call)
                {
                    env.Error(suffix.Span, "P3: calling a value is not supported yet (S8)");
                    return null;
                }
                var next = BindIndexAccess(suffix, receiver, suffix,
                    forWrite && i == suffixes.Count - 1, scope, ctx, env);
                if (next == null) return null;
                receiver = next;
            }
            return receiver;
        }

        // 索引访问绑定（S8c，SYNTAX §13.2）：读模式绑 getAtIndex(index)
        // （恰 1 参数，Type = 返回类型）；写模式（赋值 place 全路径最后一
        // 步）绑 setAtIndex(index, element)（恰 2 参数，Type = 元素形参
        // 类型）。§13.2 签名固定单 TIndex——多参数索引非法；具名实参与
        // 普通调用同规则（读模式经 BindArguments 归位，诊断自然产生）
        private static BoundExpression? BindIndexAccess(ASTNode node, BoundExpression receiver,
            PathSuffixASTNode suffix, bool forWrite, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            // 毒化静默：receiver 已失败时不再报次生错误
            if (receiver.Type is ErrorTypeSymbol) return null;
            var display = BoundAnalysis.TypeDisplay(receiver.Type);
            // （S9a：泛型参数 receiver 判型后不命中 nullable 分支）
            if (receiver.Type is TypeSymbol { ConstructedFrom: not null } receiverType
                && receiverType.ConstructedFrom == env.B.NullableDefinition)
            {
                env.Error(node.Span, $"Cannot index nullable type '{display}'");
                return null;
            }
            var name = forWrite ? "setAtIndex" : "getAtIndex";
            var candidates = receiver.Type is TypeSymbol indexReceiver
                ? SymbolLookup.FindInstanceOperators(indexReceiver, name, forWrite ? 2 : 1)
                : new List<MethodSymbol>();
            if (candidates.Count == 0)
            {
                env.Error(node.Span,
                    $"Type '{display}' does not define an index operator ('{name}')");
                return null;
            }
            // 使用点访问控制（S8e，SYNTAX §16.1：索引运算符是成员访问
            // 使用点）：不可见候选不参与；全部不可见报不可见诊断
            var accessible = candidates.Where(ctx.Frame.CanAccess).ToList();
            if (accessible.Count == 0)
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(candidates[0]));
                return null;
            }
            candidates = accessible;
            if (!forWrite)
            {
                // 读模式：重载解析复用调用设施（S8d；多候选按索引实参类型
                // ranking）——实参绑定、多参数/具名/缺失诊断自然产生。
                // receiverType = receiver 静态类型（索引 operator 宿主代入）
                var resolved = OverloadResolution.Resolve(node, candidates, suffix.Arguments,
                    scope, ctx, env, receiverType: receiver.Type as TypeSymbol);
                if (resolved == null) return null;
                var (op, boundArguments, opResultType, _) = resolved.Value;
                if (opResultType == null)
                {
                    env.Error(node.Span, $"Method '{op.Name}' has no result (void) " +
                        "and cannot be used as a value");
                    return null;
                }
                // S9a 放行：索引返回类型可为泛型参数（引用相等身份）
                return new BoundIndexExpression(node, receiver, boundArguments[0], op,
                    opResultType);
            }
            // 写模式多候选仍归口（RHS 类型在赋值侧才可知，ranking 无法在此进行）
            if (candidates.Count > 1)
            {
                env.Error(node.Span, $"P3: overload resolution for write-mode '{name}' " +
                    "is not supported yet");
                return null;
            }
            var writeOp = candidates[0];
            // 写模式：恰一个索引实参（多参数索引非法的定稿诊断）
            if (suffix.Arguments.Count != 1)
            {
                env.Error(node.Span, $"Index access on '{display}' expects exactly one " +
                    $"index argument, got {suffix.Arguments.Count}");
                return null;
            }
            var argument = suffix.Arguments[0];
            var indexParameter = writeOp.Parameters[0];
            if (argument.Name != null && argument.Name != indexParameter.Name)
            {
                env.Error(argument.Span,
                    $"'setAtIndex' has no parameter named '{argument.Name}'");
                return null;
            }
            // S9a 放行：索引形参类型可为泛型参数（引用相等身份）
            var indexType = indexParameter.Type;
            var index = ExpressionDispatcher.Visit(argument.Value.Expression, scope, ctx, env,
                indexType as TypeSymbol);
            if (index == null) return null;
            if (indexType != null && !SymbolLookup.IsAssignable(index.Type, indexType, env))
            {
                env.Error(argument.Value.Span ?? argument.Span,
                    $"Cannot pass '{BoundAnalysis.TypeDisplay(index.Type)}' as " +
                    $"'{BoundAnalysis.TypeDisplay(indexType)}'");
                return null;
            }
            // S9a 放行：元素形参类型可为泛型参数（引用相等身份）；形参类型
            // 必非空（P2 已定型）
            var elementType = writeOp.Parameters[1].Type!;
            return new BoundIndexExpression(node, receiver, index, writeOp, elementType);
        }

        // 安全访问段（S7f，SYNTAX §3.4）：receiver 必须 Nullable<T>；段在
        // 非空 T 上绑定（占位叶子承载 unwrap 后的 receiver，P4a 物化替换）；
        // 结果类型：成员类型已可空则原样（不二次包装），否则包 Nullable
        private static BoundExpression? BindSafeSegment(PathSegmentASTNode segment,
            BoundExpression receiver, Scope scope, BindContext ctx, BindEnvironment env)
        {
            // （S9a：泛型参数 receiver 判型后不命中 nullable 分支）
            if (receiver.Type is not TypeSymbol nullableReceiver
                || nullableReceiver.ConstructedFrom == null
                || nullableReceiver.ConstructedFrom != env.B.NullableDefinition
                || nullableReceiver.TypeArguments![0] is not TypeSymbol element)
            {
                env.Error(segment.Span, $"Safe access '?.' requires a nullable receiver " +
                    $"(got '{BoundAnalysis.TypeDisplay(receiver.Type)}')");
                return null;
            }
            var placeholder = new BoundSafeAccessReceiverExpression(segment, element);
            // `?.` 结果是值不是赋值 place——段内折叠恒为读语义
            var access = BindInstanceSegment(segment, placeholder, scope, ctx, env,
                forAssignment: false);
            if (access == null) return null;
            // 结果类型：成员类型已可空则原样（不二次包装），否则包 Nullable；
            // 泛型参数成员类型无静态 Nullable 构造，原样保留（S9a）
            var resultType = access.Type switch
            {
                TypeSymbol accessType when accessType.ConstructedFrom
                    == env.B.NullableDefinition => (SemanticSymbol)accessType,
                TypeSymbol accessType => env.Unit.Symbols.GetNullable(accessType),
                _ => access.Type,
            };
            return new BoundSafeAccessExpression(segment, receiver, placeholder, access,
                resultType);
        }

        // 实例字段访问：receiver 静态类型沿 BaseType 链查找（接口无
        // 实例字段；ext 注册字段同路径）。S8e 使用点检查同
        // BindFieldReference 口径（访问器读侧/字段可见性）
        public static BoundExpression? BindInstanceFieldAccess(ASTNode node,
            BoundExpression receiver, string name, BindEnvironment env, BindContext ctx,
            bool forAssignment = false)
        {
            // S9a：泛型参数 receiver 无成员表（判型后自然报未定义成员）
            if (receiver.Type is not TypeSymbol receiverType)
            {
                env.Error(node.Span, $"Undefined member '{name}' on type " +
                    $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
                return null;
            }
            var field = SymbolLookup.FindInstanceField(receiverType, name);
            if (field == null)
            {
                env.Error(node.Span, SymbolLookup.FindInstanceMethods(receiverType, name).Count > 0
                    ? $"'{name}' on type '{BoundAnalysis.TypeDisplay(receiver.Type)}' is not a field"
                    : $"Undefined member '{name}' on type " +
                        $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
                return null;
            }
            if (field.Getter != null || field.Setter != null)
            {
                if (!forAssignment && !CheckReadable(field, node.Span, ctx, env))
                {
                    return null;
                }
            }
            else if (!ctx.Frame.CanAccess(field))
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(field));
                return null;
            }
            if (field.FieldType == null)
            {
                env.Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                    "(field type inference is not supported yet)");
                return null;
            }
            // 泛型字段类型的最小替换（S7f 解构场景）：声明类型是宿主泛型
            // 参数时按 receiver 链上的构造类型取实参；receiver 定义级时
            // 原样保留（S9a 放行——引用相等身份）
            var fieldType = SymbolLookup.SubstituteFieldType(field, receiverType);
            if (fieldType == null)
            {
                env.Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                    "(field type inference is not supported yet)");
                return null;
            }
            var access = new BoundFieldAccessExpression(node, receiver, field, fieldType);
            // S8b：const 字段稳定链收窄（TryFromFieldAccess 含 IsNarrowable
            // 判定；不稳定链返回 null 直通）
            return ApplyNarrowing(node, access,
                NarrowKey.TryFromFieldAccess(receiver, field, ctx.Frame), ctx.Flow);
        }

        private static BoundExpression? ErrorAndNull(BindEnvironment env, CharRange? span,
            string message)
        {
            env.Error(span, message);
            return null;
        }
    }
}
