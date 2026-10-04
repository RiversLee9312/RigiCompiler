namespace RigiCompiler
{
    // 路径表达式（M42 统一形态）的值位置绑定（S5/S7c-2/S7f/S8c/S11，
    // SYNTAX §1.4/§3.4/§9/§13.2/§14.5）。
    // 形态分派：泛型实参检查（S9）→ 表达式底座（(a+b).c / new X().c）→
    // this 首段 → 纯调用形态（直接/静态/裸名实例调用）→
    // 泛化链折叠：首段按值解析（占位/局部/参数/裸名字段）→ 首段后缀折叠
    // （索引访问 getAtIndex/setAtIndex；值调用归口）→ 实例链上色（含
    // S11 wrapper place 段：`obj:W` 双源查找 + 只读禁令全拦截面）；
    // 首段非值且首个后缀是 Call → 调用结果底座（foo().c / foo()[0] /
    // foo()?.bar）；否则容器路径（末段成员后缀折叠，Type.staticField[0] /
    // ns.field[i]；S11 起 Colon 段切分——Type.staticField:W 交实例链；
    // 容器成员段 Call 后缀后交实例链——ns.make().field / Type.make()[0]；
    // 中间 Index/Colon 仍归口）。自旧 BindSession.BindPath 等
    // 迁移；S8c 解开原表达式底座/索引/后缀链的 S8 归口拦截。

    // 路径表达式值绑定（分派器入口；forAssignment = false）。
    // expectedType 仅向「前导点 enum case 底座 + Call 后缀」的参数化
    // case 调用特判下传（S11——其余路径形态不消费期望类型）
    internal sealed class PathVisitor : ExpressionVisitor<PathVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            return PathFacility.BindPath((PathExpressionASTNode)node, scope, ctx, env,
                forAssignment: false, expectedType);
        }
    }

    internal static partial class PathFacility
    {

        // 路径绑定核心（PathVisitor 与 VisitForAssignment 共用）；
        // expectedType 只下传到表达式底座的参数化 enum case 特判（S11）
        public static BoundExpression? BindPath(PathExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env, bool forAssignment,
            TypeSymbol? expectedType = null)
        {
            // 表达式底座（S8c：(a+b).c / new X().c / 字面量.foo）
            if (node.Head.Expression != null)
            {
                return BindExpressionBasePath(node, scope, ctx, env, forAssignment, expectedType);
            }
            // this 首段（S7c-2）：值位置 this 或实例链起点
            if (node.Head.Name == "this")
            {
                return BindThisPath(node, scope, ctx, env, forAssignment);
            }
            // self 首段（S11b，SYNTAX §14.2）：proxy 体内绑定为宿主角色的
            // this（类型 = TTarget 代入结果）；非 proxy 语境是编译错误
            //（ARCH §5.2——self/inner/this 重写三者仅在 proxy 体语境存在）
            if (node.Head.Name == "self")
            {
                return BindSelfPath(node, scope, ctx, env, forAssignment);
            }
            // 先拦截链式/非调用 super；合法的单段 Call 留给 CallFacility 的
            // 语境与直接基类重载解析。
            if (node.Head.Name == "super" && (node.Segments.Count != 0
                || node.Head.Suffixes.Count != 1
                || node.Head.Suffixes[0].Kind != PathSuffixKind.Call))
            {
                env.Error(node.Span, "P3: 'super' must be used only as super(...)");
                return null;
            }
            // 纯调用形态 → 直接调用（静态/全局）或实例调用（首段为值）。
            // S9b：显式泛型实参随调用形态提取（CallForm out 参数）；
            // g7：多段头段实参为容器类型构造实参（containerTypeArguments）
            if (CallForm.TryGet(node, out var calleeSegments, out var callArguments,
                out var genericArguments, out var containerTypeArguments))
            {
                // 特殊 CallForm（enum-case / 具化构造）先于普通函数调用
                var special = TryBindSpecialPathCall(node, calleeSegments, callArguments!,
                    genericArguments, scope, ctx, env, forAssignment, out var specialHandled);
                if (specialHandled) return special;
                var binding = CallFacility.BindCall(node, calleeSegments, callArguments!, scope,
                    ctx, env, genericArguments, containerTypeArguments);
                if (binding == null) return null;
                // M88：inner(...) 占位；#27⑦ 携带泛型包透传
                if (binding.IsInnerCall)
                {
                    if (binding.IsVoid)
                    {
                        env.Error(node.Span, "Method 'inner' has no result (void) " +
                            "and cannot be used as a value");
                        return null;
                    }
                    return new BoundInnerCallExpression(node, binding.Arguments,
                        binding.ResultType!,
                        forwardedGenericPacks: binding.ForwardedGenericPacks);
                }
                if (binding.IsSuperCall)
                {
                    if (binding.IsVoid)
                    {
                        env.Error(node.Span, "Method 'super' has no result (void) and cannot be used as a value");
                        return null;
                    }
                    return new BoundSuperCallExpression(node, binding.Method, binding.Arguments,
                        binding.ResultType!, binding.TypeArguments, binding.GenericPack);
                }
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
                    binding.ResultType!, binding.TypeArguments, binding.GenericPack,
                    binding.IsIndirect, binding.IndirectTarget);
            }
            if (node.Head.Name == "super")
            {
                env.Error(node.Span, "P3: 'super' must be used only as super(...)");
                return null;
            }
            // 非调用形态的泛型实参——g7 收窄：头段实参 + 多段 + 首段非值
            // → 构造类型容器路径（`Box\<i32>.zero` / `Box\<i32>.wrap(8).v`），
            // 交下方容器路径绑定；单段裸实参（`x\<T>` 无调用后缀）、值上的
            // 头段实参与链中段实参仍归口（调用实参已由调用形态消费；
            // wrapper 段带显式泛型实参同此拦截，泛型 wrapper 的实参代入归
            // proxy 烘焙）
            var headTypeArguments = node.Head.GenericArguments.Count > 0
                ? node.Head.GenericArguments : null;
            if (headTypeArguments != null || node.Segments.Any(s => s.GenericArguments.Count > 0))
            {
                var headIsValue = headTypeArguments != null
                    && IsValueHead(node.Head.Name!, scope, ctx, env);
                if (node.Segments.Count == 0 || headIsValue
                    || node.Segments.Any(s => s.GenericArguments.Count > 0))
                {
                    env.Error(node.Span, "P3: generic type arguments are not supported yet (S9)");
                    return null;
                }
            }
            // ===== S8c 泛化链折叠：首段按值解析 → 首段后缀折叠 → 实例链 =====
            // 首段按值解析（switch 占位/局部/参数/裸名字段——单段与多段共用）。
            // 整条路径就是该符号本身（无段无后缀）才是赋值 place：跳过
            // unassigned/收窄（定义而非读取）。有段/后缀时首段是接收者，
            // 与读路径同等享受 smart cast（SYNTAX §3.5：`t.v = 2` 的 t）
            var headName = node.Head.Name!;
            var headIsWritePlace = forAssignment && node.Segments.Count == 0
                && node.Head.Suffixes.Count == 0;
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
                // 裸名 value 即 backing 字段——拦截在作用域链查找之前。
                // S9a 修复：backing 类型为泛型参数（`var item: T { get {
                // return value } }` 的 T）同样放行——FieldType 契约已是
                // SemanticSymbol；ErrorType 经 is TypeSymbol 判定照旧放行
                // （毒化静默，解析失败 P2 已诊断）
                if (headName == "value" && ctx.Accessor.Field != null
                    && ctx.Accessor.Field.FieldType is { } backingType
                    && (backingType is GenericParameterSymbol
                        || backingType is TypeSymbol
                        && !SymbolLookup.ContainsGenericParameter(backingType)))
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
                        ctx.Frame, ctx.Accessor.IsSetter, env.Unit.Symbols);
                }
                else
                {
                    var headSymbol = scope.LookupSymbol(headName);
                    if (headSymbol != null)
                    {
                        if (headSymbol is LocalSymbol headLocal
                            && !headIsWritePlace && !ctx.Flow.IsAssigned(headLocal))
                        {
                            env.Error(node.Span, $"Use of unassigned local variable '{headName}'");
                        }
                        // 源码局部 Type 恒非空（null 是 P4a 合成 .breakid
                        // 局部的特例，P3 不可能遇到）
                        var headType = headSymbol switch
                        {
                            LocalSymbol localSymbol => localSymbol.Type,
                            ParameterSymbol parameter => VariadicParameterViewType(parameter, env),
                            _ => null,
                        };
                        if (headType == null)
                        {
                            env.Error(node.Span, $"Undefined value '{headName}'");
                            return null;
                        }
                        // 局部访问器读检查（M107，§9.4.1）：仅 set 不可读。
                        // 作赋值接收者（t.v = 1）同样是读 t
                        if (!headIsWritePlace
                            && headSymbol is LocalSymbol accessorLocal
                            && (accessorLocal.Getter != null || accessorLocal.Setter != null)
                            && accessorLocal.Getter == null)
                        {
                            env.Error(node.Span, $"'{accessorLocal.Name}' has no getter");
                            return null;
                        }
                        if (ctx.IsLambda && !ctx.LambdaParameters.Contains(headSymbol)
                            && (headSymbol is not LocalSymbol local || !ctx.Locals.Contains(local)))
                            ctx.CapturedSymbols.Add(headSymbol);
                        headValue = new BoundValueReferenceExpression(node, headSymbol, headType);
                        // 接收者位置与读路径同收窄；赋值 place 本身不包
                        if (!headIsWritePlace)
                        {
                            headValue = ApplyNarrowing(node, headValue,
                                NarrowKey.ForSymbol(headSymbol), ctx.Flow);
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
                            // S9d：可变参数体内视角（位置包/具名包分型，见
                            // VariadicParameterViewType；P4 发射映射
                            // .vargs.<名>/.kwargs.<名>）
                            var paramType = VariadicParameterViewType(headParameter, env);
                            headValue = new BoundValueReferenceExpression(node, headParameter,
                                paramType);
                            if (!headIsWritePlace)
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
                        callBase.ResultType!, callBase.TypeArguments, callBase.GenericPack,
                        callBase.IsIndirect, callBase.IndirectTarget);
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
            // §12 全形 enum case 路径（`EnumType.Case` 及链上续段
            // `EnumType.Case.member` / `EnumType.Case(args).member`）：
            // 前缀为 enum struct 且段名命中 case 时按 case 值绑定，
            // 未命中交下方既有容器路径
            var casePath = TryBindEnumCasePath(node, scope, ctx, env, forAssignment,
                out var casePathHandled);
            if (casePathHandled) return casePath;
            // 容器路径（多段）：S11 起 Colon 段切分——容器只消费到首个
            // Colon 段之前（Type.staticField:W 形态的静态字段宿主），
            // Colon 起剩余段交实例链（wrapper place 绑定与只读禁令同
            // 实例路径一处）。容器成员段首后缀为 Call 时（#20②：
            // ns.make().field / Type.make()[0].x）——调用结果作 receiver，
            // 折叠该段剩余后缀后交实例链；无 Call 的中间 Index/其他后缀
            // 仍归口；无后缀 ns.Foo 方法仍「不能作为值」
            var colonIndex = -1;
            for (int i = 0; i < node.Segments.Count; i++)
            {
                if (node.Segments[i].Connector == PathConnector.Colon)
                {
                    colonIndex = i;
                    break;
                }
            }
            var memberIndex = colonIndex < 0 ? node.Segments.Count - 1 : colonIndex - 1;
            // `Type:W`：首段非值且无成员段——wrapper place 需要值宿主
            if (memberIndex < 0)
            {
                env.Error(node.Span,
                    $"Wrapper place ':{node.Segments[0].Name}' requires a value host");
                return null;
            }
            // 首段后缀：Call 已在上方「调用结果底座」分支处理；此处多段
            // 容器上的 Index/其他仍归口
            if (node.Head.Suffixes.Count > 0)
            {
                env.Error(node.Span, "P3: instance member access is not supported yet (S8)");
                return null;
            }
            // 扫描容器段（至 memberIndex）：首个 Call 后缀段为值转换点；
            // 其前不得有任何后缀（中间 Index 等保持归口）
            var callSegIndex = -1;
            for (int i = 0; i <= memberIndex; i++)
            {
                var seg = node.Segments[i];
                if (seg.Suffixes.Count == 0) continue;
                if (seg.Suffixes[0].Kind == PathSuffixKind.Call)
                {
                    callSegIndex = i;
                    break;
                }
                if (i < memberIndex)
                {
                    env.Error(node.Span, "P3: instance member access is not supported yet (S8)");
                    return null;
                }
                // i == memberIndex 且首后缀非 Call（字段后索引等）→ 下方字段路径
                break;
            }
            if (callSegIndex >= 0)
            {
                return BindContainerCallChain(node, callSegIndex, scope, ctx, env, forAssignment);
            }
            // 容器部分段名序列（首段名 + 成员前各段名；Colon 起段不属容器）；
            // g7：头段泛型实参随段名还原（Box\<i32>.zero → 容器 Box\<i32>）
            var segments = new List<string> { node.Head.Name! };
            segments.AddRange(node.Segments.Take(memberIndex + 1).Select(s => s.Name));
            var segmentGenerics = headTypeArguments == null
                ? null
                : new IReadOnlyList<TypeReferenceASTNode>?[] { headTypeArguments };
            // Type.staticField.rest：从最短类型前缀起把静态/全局字段切成
            // 值宿主，剩余段交实例链（Holder.current.origin.x）。找不到
            // 字段再按末段成员走原诊断。
            FieldSymbol? splitField = null;
            SemanticSymbol? splitContainer = null;
            var splitMemberSeg = -1;
            for (var i = 0; i <= memberIndex; i++)
            {
                var trySegments = new List<string> { node.Head.Name! };
                trySegments.AddRange(node.Segments.Take(i + 1).Select(s => s.Name));
                var tryContainer = MemberLookup.ResolveContainer(trySegments, node.Span,
                    ctx.Frame, env, reportErrors: false, segmentGenerics: segmentGenerics,
                    allowBareGenericDefinition: true);
                if (tryContainer == null) continue;
                if (MemberLookup.FindMember(tryContainer, trySegments[^1]) is FieldSymbol field)
                {
                    splitField = field;
                    splitContainer = tryContainer;
                    splitMemberSeg = i;
                    break;
                }
            }
            if (splitField != null)
            {
                if (StaticGenericRules.CheckConstructedStaticAccess(splitContainer!,
                    splitField.IsStatic, splitField.Name, node.Span, env.Error))
                {
                    return null;
                }
                var restStart = splitMemberSeg + 1;
                var fieldIsTerminal = restStart > memberIndex && colonIndex < 0
                    && node.Segments[splitMemberSeg].Suffixes.Count == 0;
                var fieldValue = BindFieldReference(node, splitField, ctx, env,
                    forAssignment && fieldIsTerminal,
                    staticHostType: splitContainer as TypeSymbol);
                if (fieldValue == null) return null;
                var foldedMember = FoldSuffixes(node, fieldValue,
                    node.Segments[splitMemberSeg].Suffixes, 0,
                    forAssignment && restStart > memberIndex && colonIndex < 0,
                    scope, ctx, env);
                if (foldedMember == null) return null;
                if (restStart >= node.Segments.Count) return foldedMember;
                return BindInstanceChain(node, foldedMember,
                    node.Segments.Skip(restStart).ToList(), scope, ctx, env, forAssignment);
            }
            var container = MemberLookup.ResolveContainer(segments, node.Span, ctx.Frame, env,
                segmentGenerics: segmentGenerics, allowBareGenericDefinition: true);
            if (container == null) return null;
            var member = MemberLookup.FindMember(container, segments[^1]);
            var pathText = string.Join(".", segments);
            switch (member)
            {
                case MethodSymbol:
                    return ErrorAndNull(env, node.Span,
                        $"Method '{pathText}' cannot be used as a value");
                case null:
                    return ErrorAndNull(env, node.Span, $"Undefined name: '{pathText}'");
                default:
                    return ErrorAndNull(env, node.Span, $"'{pathText}' cannot be used as a value");
            }
        }
    }
}
