namespace LatteCompiler
{
    // 调用绑定（S5/S7c-2/S8d）与 new 构造（S5）。
    // 自旧 BindSession.BindCall/BindCallee/BindInstanceCallForm/
    // BindInstanceMethodCall/BindArguments/BindNew 迁移；S8d 起候选解析
    // 统一经 OverloadResolution（重载 ranking/默认参数/具名重排，
    // SYNTAX §4.2），receiver 补 this 判定在落定胜者后进行。

    // 调用绑定的中间产物：值位置与语句位置分别落成
    // BoundCallExpression / BoundCallStatement（void 调用）；
    // Receiver 为 null = 静态/全局调用，非 null = 实例调用（S7c-2）
    internal sealed class CallBinding
    {
        public MethodSymbol Method = null!;
        public IReadOnlyList<BoundExpression> Arguments = null!;
        public bool IsVoid;
        public BoundExpression? Receiver;
    }

    // 调用形态判定：符号头 + 全 Dot 段（无中间后缀）+ 整条链恰好一个
    // Call 后缀且位于链尾（首段 Call 后缀要求无段——S8c 起 foo().c 形态
    // 归路径绑定的调用结果底座；段 Call 后缀同理须在末段）。
    // 泛型/表达式底座已由路径绑定此前各自归口，不在此判定内
    internal static class CallForm
    {
        public static bool TryGet(PathExpressionASTNode node,
            out List<string> calleeSegments, out List<ArgumentASTNode>? callArguments)
        {
            calleeSegments = new List<string>();
            callArguments = null;
            if (node.Head.Name == null) return false;
            calleeSegments.Add(node.Head.Name);
            if (node.Head.Suffixes.Count > 1) return false;
            if (node.Head.Suffixes.Count == 1)
            {
                if (node.Head.Suffixes[0].Kind != PathSuffixKind.Call) return false;
                if (node.Segments.Count > 0) return false;
                callArguments = node.Head.Suffixes[0].Arguments;
            }
            for (int i = 0; i < node.Segments.Count; i++)
            {
                var segment = node.Segments[i];
                if (segment.Connector != PathConnector.Dot) return false;
                if (segment.Suffixes.Count > 1) return false;
                calleeSegments.Add(segment.Name);
                if (segment.Suffixes.Count == 1)
                {
                    if (segment.Suffixes[0].Kind != PathSuffixKind.Call || callArguments != null
                        || i < node.Segments.Count - 1)
                    {
                        return false;
                    }
                    callArguments = segment.Suffixes[0].Arguments;
                }
            }
            return callArguments != null;
        }
    }

    internal static class CallFacility
    {
        // 直接调用绑定（纯调用形态路径）：多段首段为值 → 实例调用形态；
        // 否则经被调用方候选集解析（静态/全局或裸名实例方法补 this——
        // receiver 判定在重载解析落定胜者后进行，S8d）
        public static CallBinding? BindCall(ASTNode node, List<string> calleeSegments,
            List<ArgumentASTNode> arguments, Scope scope, BindContext ctx, BindEnvironment env)
        {
            // 多段首段为值（局部/参数）→ 实例调用形态（S7c-2）
            if (calleeSegments.Count > 1
                && (scope.Lookup(calleeSegments[0]) != null
                    || ctx.Method.Parameters.Any(p => p.Name == calleeSegments[0])))
            {
                return BindInstanceCallForm(node, calleeSegments, arguments, scope, ctx, env);
            }
            var candidates = ResolveCallee(node, calleeSegments, scope, ctx, env);
            if (candidates == null) return null;
            var resolved = OverloadResolution.Resolve(node, candidates, arguments, scope, ctx, env);
            if (resolved == null) return null;
            var (calleeMethod, boundArguments) = resolved.Value;
            if (calleeMethod.ReturnType != null
                && SymbolLookup.ContainsGenericParameter(calleeMethod.ReturnType))
            {
                env.Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                return null;
            }
            BoundExpression? receiver = null;
            if (calleeMethod.Owner != null && !calleeMethod.IsStatic)
            {
                // 实例方法（S7c-2）：当前上下文有 this（实例方法/ext 方法
                // 体内）→ 补 this receiver；静态上下文（含默认值表达式）→ 诊断
                if (ctx.HasThis)
                {
                    receiver = new BoundThisExpression(node, ctx.Method.Owner!);
                }
                else
                {
                    env.Error(node.Span, $"P3: instance method '{calleeMethod.Name}' requires " +
                        "a receiver ('this' is not available in a static context)");
                    return null;
                }
            }
            return new CallBinding
            {
                Method = calleeMethod,
                Arguments = boundArguments,
                IsVoid = calleeMethod.ReturnType == null,
                Receiver = receiver,
            };
        }

        // 被调用方候选集解析：单段经 FindMethods 全查找序；多段经容器 + 末段
        // 方法（首段为值的多段已由 BindCall 分流）。重载解析与实例 receiver
        // 判定均在落定胜者后进行（S8d）
        private static List<MethodSymbol>? ResolveCallee(ASTNode node, List<string> calleeSegments,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            var pathText = string.Join(".", calleeSegments);
            List<MethodSymbol> candidates;
            if (calleeSegments.Count == 1)
            {
                candidates = MemberLookup.FindMethods(calleeSegments[0], ctx, env);
            }
            else
            {
                // 首段为值的多段已由 BindCall 分流（实例调用形态）；
                // 此处前 N-1 段必为容器
                var container = MemberLookup.ResolveContainer(calleeSegments, node.Span, ctx, env);
                if (container == null) return null;
                candidates = container switch
                {
                    NamespaceSymbol ns => ns.Methods.Where(m => m.Name == calleeSegments[^1]).ToList(),
                    TypeSymbol t => t.Methods.Where(m => m.Name == calleeSegments[^1]).ToList(),
                    _ => new List<MethodSymbol>(),
                };
                if (candidates.Count == 0
                    && MemberLookup.FindMember(container, calleeSegments[^1]) != null)
                {
                    env.Error(node.Span, $"'{pathText}' is not a method");
                    return null;
                }
            }
            if (candidates.Count == 0)
            {
                env.Error(node.Span, $"Undefined function: '{pathText}'");
                return null;
            }
            // 使用点访问控制（S8e，SYNTAX §16.1）：不可见候选不参与重载
            // 解析；全部不可见时报首个候选的不可见诊断
            var accessible = candidates.Where(ctx.CanAccess).ToList();
            if (accessible.Count == 0)
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(candidates[0]));
                return null;
            }
            return accessible;
        }

        // 实例调用形态（首段为值的多段纯调用）：首段绑 receiver，中间段
        // 沿 receiver 类型上色（CallForm.TryGet 保证中间段无后缀，只能是
        // 字段），末段实例方法查找匹配
        private static CallBinding? BindInstanceCallForm(ASTNode node, List<string> calleeSegments,
            List<ArgumentASTNode> arguments, Scope scope, BindContext ctx, BindEnvironment env)
        {
            BoundExpression receiver;
            var headLocal = scope.Lookup(calleeSegments[0]);
            if (headLocal != null)
            {
                if (!ctx.Flow.IsAssigned(headLocal))
                {
                    env.Error(node.Span,
                        $"Use of unassigned local variable '{calleeSegments[0]}'");
                }
                receiver = new BoundValueReferenceExpression(node, headLocal, headLocal.Type!);
                // S8b：调用链头收窄（x.m() 的 x 在收窄区域内）
                receiver = PathFacility.ApplyNarrowingPublic(node, receiver,
                    NarrowKey.ForSymbol(headLocal), ctx);
            }
            else
            {
                var headParameter = ctx.Method.Parameters.First(p => p.Name == calleeSegments[0]);
                if (headParameter.Type is not TypeSymbol paramType)
                {
                    env.Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
                receiver = new BoundValueReferenceExpression(node, headParameter, paramType);
                receiver = PathFacility.ApplyNarrowingPublic(node, receiver,
                    NarrowKey.ForSymbol(headParameter), ctx);
            }
            for (int i = 1; i < calleeSegments.Count - 1; i++)
            {
                var next = PathFacility.BindInstanceFieldAccess(node, receiver, calleeSegments[i],
                    env, ctx);
                if (next == null) return null;
                receiver = next;
            }
            return BindInstanceMethodCall(node, receiver, calleeSegments[^1], arguments, scope,
                ctx, env);
        }

        // 实例方法调用：receiver 静态类型沿 BaseType 链查找（接口
        // receiver 查接口自身成员；ext 注册成员同路径；S8e 起不可见
        // 候选经访问控制过滤）。值位置 void 检查由调用方做
        public static CallBinding? BindInstanceMethodCall(ASTNode node, BoundExpression receiver,
            string name, List<ArgumentASTNode> arguments, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var candidates = SymbolLookup.FindInstanceMethods(receiver.Type, name);
            if (candidates.Count == 0)
            {
                env.Error(node.Span, SymbolLookup.FindInstanceField(receiver.Type, name) != null
                    ? $"'{name}' on type '{BoundAnalysis.TypeDisplay(receiver.Type)}' is not a method"
                    : $"Undefined member '{name}' on type " +
                        $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
                return null;
            }
            // 使用点访问控制（S8e，SYNTAX §16.1）：同 ResolveCallee 口径
            var accessible = candidates.Where(ctx.CanAccess).ToList();
            if (accessible.Count == 0)
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(candidates[0]));
                return null;
            }
            var resolved = OverloadResolution.Resolve(node, accessible, arguments, scope, ctx, env);
            if (resolved == null) return null;
            var (selected, boundArguments) = resolved.Value;
            // 返回类型含未替换泛型参数（泛型接口/泛型类型成员的使用归
            // S9；for 协议内部路径不经此检查——P3 已备好具体类型）
            if (selected.ReturnType != null
                && SymbolLookup.ContainsGenericParameter(selected.ReturnType))
            {
                env.Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                return null;
            }
            return new CallBinding
            {
                Method = selected,
                Arguments = boundArguments,
                IsVoid = selected.ReturnType == null,
                Receiver = receiver,
            };
        }

        // 实参绑定（单候选落定）：位置实参按序、具名实参按形参名归位——
        // 产物已是规范参数序（ARCHITECTURE §2「BoundCall 已是规范参数序」）；
        // S8d：位置/具名冲突统一 Duplicate 诊断（此前位置实参会静默覆盖
        // 具名占位）+ 缺省形参以预绑定默认值填充
        public static List<BoundExpression>? BindArguments(MethodSymbol target,
            List<ArgumentASTNode> arguments, Scope scope, CharRange? callSpan, BindContext ctx,
            BindEnvironment env)
        {
            var parameters = target.Parameters;
            var bound = new BoundExpression?[parameters.Count];
            var failed = false;
            var nextPositional = 0;
            foreach (var argument in arguments)
            {
                int index;
                if (argument.Name == null)
                {
                    if (nextPositional >= parameters.Count)
                    {
                        env.Error(argument.Span, $"Too many arguments for '{target.Name}'");
                        failed = true;
                        continue;
                    }
                    index = nextPositional++;
                }
                else
                {
                    index = -1;
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        if (parameters[i].Name == argument.Name) { index = i; break; }
                    }
                    if (index < 0)
                    {
                        env.Error(argument.Span,
                            $"'{target.Name}' has no parameter named '{argument.Name}'");
                        failed = true;
                        continue;
                    }
                }
                if (bound[index] != null)
                {
                    env.Error(argument.Span,
                        $"Duplicate argument for parameter '{parameters[index].Name}'");
                    failed = true;
                    continue;
                }
                var expected = parameters[index].Type as TypeSymbol;
                var value = ExpressionDispatcher.Visit(argument.Value.Expression, scope, ctx, env,
                    expected);
                if (value == null)
                {
                    failed = true;
                    continue;
                }
                // 形参类型为泛型参数时兼容判定归 S9
                if (expected != null && !SymbolLookup.IsAssignable(value.Type, expected, env))
                {
                    env.Error(argument.Value.Span ?? argument.Span,
                        $"Cannot pass '{BoundAnalysis.TypeDisplay(value.Type)}' as " +
                        $"'{BoundAnalysis.TypeDisplay(expected)}'");
                    failed = true;
                    continue;
                }
                bound[index] = value;
            }
            for (int i = 0; i < parameters.Count; i++)
            {
                if (bound[i] != null) continue;
                // 缺省形参：预绑定默认值填充（S8d）；预绑定失败的按缺失处理
                // （声明点诊断已报，此处级联 Missing）
                var defaultValue = env.GetParameterDefault(parameters[i]);
                if (defaultValue != null)
                {
                    bound[i] = defaultValue;
                    continue;
                }
                env.Error(callSpan, $"Missing argument for parameter '{parameters[i].Name}'");
                failed = true;
            }
            return failed ? null : bound.Select(b => b!).ToList();
        }
    }

    // new 构造（S5）：类型引用解析（ErrorType 毒化直通；泛型参数已诊断）；
    // 泛型定义不可构造；Class/Struct 可构造，Interface/EnumStruct/Wrapper
    // 各自归口诊断；init 匹配（无显式 init 时零参默认构造；
    // 多匹配归 S8d ranking）
    internal sealed class NewVisitor : ExpressionVisitor<NewVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var newNode = (NewExpressionASTNode)node;
            var type = TypeReferences.Resolve(newNode.Type, newNode.Type.Span ?? newNode.Span,
                ctx, env);
            if (type is ErrorTypeSymbol) return null;
            if (type == null) return null;  // 泛型参数（已诊断）
            if (type.ConstructedFrom == null && type.GenericParameters.Count > 0)
            {
                env.Error(newNode.Type.Span ?? newNode.Span,
                    $"Cannot construct generic type definition '{type.Name}'");
                return null;
            }
            switch (type.Kind)
            {
                case TypeKind.Class:
                case TypeKind.Struct:
                    break;
                case TypeKind.Interface:
                    env.Error(newNode.Type.Span ?? newNode.Span,
                        $"Cannot construct interface '{type.Name}'");
                    return null;
                case TypeKind.EnumStruct:
                    env.Error(newNode.Type.Span ?? newNode.Span,
                        "P3: enum case construction is not supported yet (S11)");
                    return null;
                default:
                    env.Error(newNode.Type.Span ?? newNode.Span,
                        $"Cannot construct wrapper '{type.Name}' (created by the compiler)");
                    return null;
            }
            // abstract 类不可构造（SYNTAX §9.2.1）
            if (type.IsAbstract)
            {
                env.Error(newNode.Type.Span ?? newNode.Span,
                    $"Cannot construct an instance of abstract type '{type.Name}'");
                return null;
            }
            var inits = type.Methods.Where(m => m.Kind == MethodKind.Init).ToList();
            if (inits.Count == 0)
            {
                // 无显式 init 的零参构造（默认构造规则待规范明确，见技术债）
                if (newNode.Arguments.Count == 0)
                {
                    return new BoundNewExpression(node, type, null, new List<BoundExpression>());
                }
                env.Error(newNode.Span, $"Type '{type.Name}' has no constructor");
                return null;
            }
            // 构造调用是使用点（S8e，SYNTAX §16.1，含 init 可见性 §12.2）：
            // 不可见 init 不参与重载解析；全部不可见时报不可见诊断
            var accessibleInits = inits.Where(ctx.CanAccess).ToList();
            if (accessibleInits.Count == 0)
            {
                env.Error(newNode.Span, AccessChecker.InaccessibleMessage(inits[0]));
                return null;
            }
            // init 重载解析与函数调用同一设施（S8d，SYNTAX §4.2）
            var resolved = OverloadResolution.Resolve(newNode, accessibleInits, newNode.Arguments,
                scope, ctx, env);
            if (resolved == null) return null;
            return new BoundNewExpression(node, type, resolved.Value.Method,
                resolved.Value.Arguments);
        }
    }
}
