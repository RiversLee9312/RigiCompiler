namespace RigiCompiler
{
    // 调用绑定（S5/S7c-2/S8d）与 new 构造（S5）。
    // 自旧 BindSession.BindCall/BindCallee/BindInstanceCallForm/
    // BindInstanceMethodCall/BindArguments/BindNew 迁移；S8d 起候选解析
    // 统一经 OverloadResolution（重载 ranking/默认参数/具名重排，
    // SYNTAX §4.2），receiver 补 this 判定在落定胜者后进行。

    // 调用绑定的中间产物：值位置与语句位置分别落成
    // BoundCallExpression / BoundCallStatement（void 调用）；
    // Receiver 为 null = 静态/全局调用，非 null = 实例调用（S7c-2）。
    // IsInnerCall：M88 proxy 体 inner(...) 占位——Method 不用，Arguments
    // 已绑，落成 BoundInnerCallExpression；ForwardedGenericPacks 为 #27⑦
    // 待转发的可变泛型包（声明序）
    internal sealed class CallBinding
    {
        public MethodSymbol Method = null!;
        public IReadOnlyList<BoundExpression> Arguments = null!;
        public bool IsVoid;
        public BoundExpression? Receiver;
        // 固定泛型实参（显式或推断；非泛型调用为空列表；P4 发射 .generic.T 依据）
        public IReadOnlyList<SemanticSymbol> TypeArguments = Array.Empty<SemanticSymbol>();
        // g7：构造类型宿主上的静态调用的宿主泛型实参（`Box\<i32>.wrap(8)`
        // 的 i32；非该形态为空列表）。静态调用无 .this 可供 VM 注入宿主
        // typeid（§7.2），调用点必须显式携带（宿主在前、方法自有在后）
        public IReadOnlyList<SemanticSymbol> HostTypeArguments = Array.Empty<SemanticSymbol>();
        // S9d-2：泛型可变参数包推导产物（null = 无；P4 发射 .generic.TArgs 依据）
        public BoundGenericVarArgsArgument? GenericPack;
        // S9b：代入后返回类型（泛型候选视图产物；定义级 ReturnType 是 T）
        public SemanticSymbol? ResultType;
        // M88：inner(...) 模板占位
        public bool IsInnerCall;
        public bool IsSuperCall;
        // 间接调用（SYNTAX §5.2 callable 协议，BIL §15.3）：对 IndirectTarget
        // 对象虚调用其 $$call 实现（Method = 命中的 call 运算符符号）
        public bool IsIndirect;
        public BoundExpression? IndirectTarget;
        // #27⑦：inner 待转发的可变泛型包（仅 IsInnerCall；固定泛型不入列）
        public IReadOnlyList<GenericParameterSymbol> ForwardedGenericPacks =
            Array.Empty<GenericParameterSymbol>();
    }

    // 调用形态判定：符号头 + 全 Dot 段（无中间后缀）+ 整条链恰好一个
    // Call 后缀且位于链尾（首段 Call 后缀要求无段——S8c 起 foo().c 形态
    // 归路径绑定的调用结果底座；段 Call 后缀同理须在末段）。
    // 显式泛型实参（S9b/g7）分两处：
    //   genericArguments = 方法泛型实参——单段时取头段（`foo\<T>(x)`），
    //     多段时取末段（`a.foo\<T>(x)` / `Box\<i32>.wrap\<U>(8)` 的 U）；
    //   containerTypeArguments = 容器类型构造实参——多段时头段实参是
    //     类型构造实参（`Box\<i32>.wrap(8)` 的 i32，g7），单段恒 null。
    // 中间段带实参（嵌套构造类型容器）不判定为调用形态（落回路径绑定诊断）
    internal static class CallForm
    {
        public static bool TryGet(PathExpressionASTNode node,
            out List<string> calleeSegments, out List<ArgumentASTNode>? callArguments,
            out List<TypeReferenceASTNode>? genericArguments,
            out List<TypeReferenceASTNode>? containerTypeArguments)
        {
            calleeSegments = new List<string>();
            callArguments = null;
            genericArguments = null;
            containerTypeArguments = null;
            if (node.Head.Name == null) return false;
            calleeSegments.Add(node.Head.Name);
            var headGeneric = node.Head.GenericArguments.Count > 0
                ? node.Head.GenericArguments : null;
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
            if (node.Segments.Count == 0)
            {
                // 单段：头段实参 = 方法泛型实参（`foo\<T>(x)`）
                genericArguments = headGeneric;
            }
            else
            {
                // 多段：头段实参 = 容器类型构造实参（g7，`Box\<i32>.wrap(8)`）；
                // 方法实参只可能在末段（`Box\<i32>.wrap\<U>(8)` 两处并存）；
                // 中间段带实参不判定为调用形态
                containerTypeArguments = headGeneric;
                for (int i = 0; i < node.Segments.Count - 1; i++)
                {
                    if (node.Segments[i].GenericArguments.Count > 0) return false;
                }
                var last = node.Segments[^1];
                if (last.GenericArguments.Count > 0) genericArguments = last.GenericArguments;
            }
            return callArguments != null;
        }
    }

    internal static class CallFacility
    {
        // 显式泛型实参解析（S9b）：TypeReferenceASTNode 列表 → 已解析的
        // SemanticSymbol 列表（复用 NameResolver 类型引用解析，诊断按 P3
        // 落袋；任一项失败返回 null）。S8e 修复：实参类型引用是使用点
        // （SYNTAX §16.1）——与 TypeReferences.Resolve 同口径经
        // AccessChecker 判定可见性
        public static IReadOnlyList<SemanticSymbol>? ResolveGenericArguments(
            List<TypeReferenceASTNode> typeArguments, ASTNode node, BindContext ctx,
            BindEnvironment env)
        {
            var result = new List<SemanticSymbol>(typeArguments.Count);
            foreach (var argument in typeArguments)
            {
                var resolved = env.Names.ResolveTypeReference(argument, ctx.Frame.FileCtx,
                    ctx.Frame.DeclaringType, ctx.Frame.Method, argument.Span ?? node.Span);
                if (resolved == null) return null;
                // 使用点访问控制（f\<PrivateType\>() 跨文件拒绝；泛型参数与
                // ErrorType 毒化由 FindInaccessibleType 直通）。F1/V-A 起
                // 递归口径：f\<Box\<Hidden\>\() 的实参 Hidden 同检，命中报
                // 最深不可见者；写出点恒报并登记（下游推断位去重）
                var inaccessible = AccessChecker.FindInaccessibleType(resolved,
                    ctx.Frame.FileCtx.File, ctx.Frame.FileCtx.Namespace,
                    ctx.Frame.DeclaringType);
                if (inaccessible != null)
                {
                    env.Error(argument.Span ?? node.Span,
                        AccessChecker.InaccessibleMessage(inaccessible));
                    UseSiteAccessibility.NoteExplicitlyReported(inaccessible, ctx);
                    return null;
                }
                result.Add(resolved);
                // 填入点统一检查（g4 框架）：显式实参为构造类型时重跑
                // 显式界 + 隐式限制闭包（GenericConstraints 同通道设施；
                // 约束不满足由 OverloadResolution 逐候选回放，此处只收
                // 构造类型自身的闭包违规——失败不拦截，诊断已落袋）
                if (resolved is TypeSymbol { ConstructedFrom: not null } constructed)
                {
                    GenericConstraints.CheckConstructedType(constructed,
                        argument.Span ?? node.Span, env);
                }
            }
            return result;
        }

        // 直接调用绑定（纯调用形态路径）：多段首段为值 → 实例调用形态；
        // 否则经被调用方候选集解析（静态/全局或裸名实例方法补 this——
        // receiver 判定在重载解析落定胜者后进行，S8d）。
        // genericArguments：显式方法泛型实参 AST（S9b；null = 未提供）；
        // containerTypeArguments：头段容器类型构造实参 AST（g7，
        // `Box\<i32>.wrap(8)`；null = 未提供）
        public static CallBinding? BindCall(ASTNode node, List<string> calleeSegments,
            List<ArgumentASTNode> arguments, Scope scope, BindContext ctx, BindEnvironment env,
            List<TypeReferenceASTNode>? genericArguments = null,
            List<TypeReferenceASTNode>? containerTypeArguments = null)
        {
            // M88（SYNTAX §14.2 / ARCH §5.2）：proxy 体内 inner(...) 绑定为
            // BoundInnerCallExpression 占位（实参正常绑定；形状按 proxy 声明
            // 自身签名校验）。非 proxy 语境的 inner 是编译错误
            if (calleeSegments.Count == 1 && calleeSegments[0] == "inner")
            {
                return BindInnerCall(node, arguments, scope, ctx, env);
            }
            if (calleeSegments.Count == 1 && calleeSegments[0] == "super")
            {
                return BindSuperCall(node, arguments, scope, ctx, env, genericArguments);
            }
            // callable 协议（SYNTAX §5.2，BIL §15.3）：单段被调用名解析为
            // 局部/参数且其类型声明了 operator call → 间接调用（对该对象
            // 虚调用 $$call；lambda 隐藏类经覆写 abstract call 接入本协议）
            if (calleeSegments.Count == 1)
            {
                var headSymbol = scope.LookupSymbol(calleeSegments[0])
                    ?? (ctx.Frame.IsDefaultValueContext
                        ? null
                        : ctx.Frame.Method.Parameters
                            .FirstOrDefault(p => p.Name == calleeSegments[0]));
                if (headSymbol is LocalSymbol or ParameterSymbol)
                {
                    var callableReceiver = BindCallableReceiver(node, headSymbol, ctx, env);
                    if (callableReceiver != null && callableReceiver.Type is not ErrorTypeSymbol)
                    {
                        var callableType = SymbolLookup.EffectiveMemberType(callableReceiver.Type,
                            env);
                        if (FindCallOperators(callableType, env.Unit.Symbols).Count > 0)
                        {
                            return BindIndirectCallOverload(node, callableReceiver, callableType,
                                arguments, genericArguments, scope, ctx, env);
                        }
                    }
                }
                if (headSymbol == null
                    && MemberLookup.FindField(calleeSegments[0], ctx.Frame, env) is { } bareField)
                {
                    // 裸名字段（宿主类型的 callable 字段补 this）——与普通
                    // 字段引用同路径绑定（含访问控制/收窄/this 捕获记录）
                    var fieldReceiver = PathFacility.BindFieldReference(node, bareField, ctx, env);
                    if (fieldReceiver != null && fieldReceiver.Type is not ErrorTypeSymbol)
                    {
                        var fieldCallableType = SymbolLookup.EffectiveMemberType(fieldReceiver.Type,
                            env);
                        if (FindCallOperators(fieldCallableType, env.Unit.Symbols).Count > 0)
                        {
                            return BindIndirectCallOverload(node, fieldReceiver, fieldCallableType,
                                arguments, genericArguments, scope, ctx, env);
                        }
                    }
                }
            }
            // 多段首段为值（局部/参数/裸字段）→ 实例调用形态（S7c-2）。
            // 裸字段必须先于容器路径：`c.inc()` 的 c 是实例字段时不得
            // 当成类型/命名空间（同名字段与类型共存时字段优先）。
            // g7：首段为值时头段泛型实参无类型构造落点——沿用旧口径视作
            // 末段方法的显式泛型实参；两处实参并存无法消歧，诊断
            if (calleeSegments.Count > 1
                && (scope.LookupSymbol(calleeSegments[0]) != null
                    || ctx.Frame.Method.Parameters.Any(p => p.Name == calleeSegments[0])
                    || MemberLookup.FindField(calleeSegments[0], ctx.Frame, env) != null))
            {
                if (containerTypeArguments != null)
                {
                    if (genericArguments != null)
                    {
                        env.Error(node.Span, "P3: ambiguous generic arguments on value call " +
                            $"'{string.Join(".", calleeSegments)}' (head type arguments have no " +
                            "construction site on a value receiver)");
                        return null;
                    }
                    genericArguments = containerTypeArguments;
                    containerTypeArguments = null;
                }
                return BindInstanceCallForm(node, calleeSegments, arguments, scope, ctx, env,
                    genericArguments);
            }
            var candidates = ResolveCallee(node, calleeSegments, containerTypeArguments, scope,
                ctx, env, out var staticReceiverType);
            if (candidates == null) return null;
            var typeArgs = genericArguments == null
                ? null
                : ResolveGenericArguments(genericArguments, node, ctx, env);
            if (genericArguments != null && typeArgs == null) return null;
            // receiverType（S9b 修复）：裸名调用以当前宿主（method.Owner，
            // 泛型函数体内为定义级）为 receiver 链做宿主代入——沿
            // BaseType 链找候选 method.Owner 的构造取实参（与
            // BindInstanceMethodCall 同口径）；g7：多段容器为构造类型时
            // （Box\<i32>）优先作 receiverType——静态方法签名中的宿主
            // 泛型参数同样按构造实参代入（wrap(x: T): Box\<T> →
            // wrap(x: i32): Box\<i32>）；静态/全局候选不受影响
            // （ViewOf 仅对有主泛型候选生效）
            var resolved = OverloadResolution.Resolve(node, candidates, arguments, scope, ctx, env,
                typeArgs, receiverType: staticReceiverType ?? ctx.Frame.Method.Owner);
            if (resolved == null)
            {
                return null;
            }
            var (calleeMethod, boundArguments, calleeResultType, genericPack, resolvedTypeArgs) =
                resolved.Value;
            BoundExpression? receiver = null;
            if (calleeMethod.Owner != null && !calleeMethod.IsStatic)
            {
                // 实例方法（S7c-2）：补 this 仅限 callee 宿主在当前 this
                // 类型的 BaseType 链（含接口闭包）上——`A.m()` 命中他类
                // 实例方法时 this 类型不符，不能盲补（B 的 this 不是 A 的
                // receiver）；静态上下文（含默认值表达式）→ 诊断。
                // lambda 语境（§5.2）：Frame.Method 是隐藏类 $$call（恒实例），
                // 有效 this 类型与可用性取 LambdaThisType（外层 this）
                var thisOwner = PathFacility.EffectiveThisType(ctx, env);
                if (thisOwner != null
                    && IsOnThisChain(thisOwner, calleeMethod.Owner))
                {
                    receiver = new BoundThisExpression(node, thisOwner);
                    if (ctx.IsLambda && ctx.This != null) ctx.CapturedSymbols.Add(ctx.This);
                }
                else if (thisOwner == null)
                {
                    env.Error(node.Span, $"P3: instance method '{calleeMethod.Name}' requires " +
                        "a receiver ('this' is not available in a static context)");
                    return null;
                }
                else
                {
                    env.Error(node.Span, $"P3: instance method '{calleeMethod.Name}' requires " +
                        "a receiver ('this' is not an instance of " +
                        $"'{BoundAnalysis.TypeDisplay(calleeMethod.Owner)}')");
                    return null;
                }
            }
            return new CallBinding
            {
                Method = calleeMethod,
                Arguments = boundArguments,
                IsVoid = calleeMethod.ReturnType == null,
                Receiver = receiver,
                TypeArguments = resolvedTypeArgs,
                // g7：构造类型宿主上的静态调用携带宿主泛型实参（无 .this
                // 可供 VM 注入 .generic.* typeid，调用点显式物化，§7.2）
                HostTypeArguments = StaticHostTypeArguments(calleeMethod, staticReceiverType),
                GenericPack = genericPack,
                // S10：async 调用表达式类型改写为 Task\<T\>/Task（SYNTAX §4.5）
                ResultType = AsyncResultType(calleeMethod, calleeResultType, env),
            };
        }

        // g7：静态调用的宿主泛型实参（`Box\<i32>.wrap(8)` → [i32]）：沿容器
        // 类型 BaseType 链找 method.Owner 的构造取实参（同 ViewOf 口径）；
        // 非静态/非构造宿主/链上无匹配为空列表
        private static IReadOnlyList<SemanticSymbol> StaticHostTypeArguments(MethodSymbol method,
            TypeSymbol? containerType)
        {
            if (!method.IsStatic || method.Owner == null || containerType == null)
            {
                return Array.Empty<SemanticSymbol>();
            }
            for (var t = containerType; t != null; t = t.BaseType)
            {
                if (ReferenceEquals(t.ConstructedFrom, method.Owner) && t.TypeArguments != null)
                {
                    return t.TypeArguments;
                }
            }
            return Array.Empty<SemanticSymbol>();
        }

        // 具化泛型构造（SYNTAX §3.6/§3.7，BIL §14.2）：单段纯调用的被调名
        // 命中当前声明的泛型参数时，`TResult()` 是 typeid 构造——与动态
        // new 同一机制（运行期按 typeid 解析 init；抽象/enum struct/无匹配
        // init 抛 core.NoSuchMethodException）。返回非 null = 绑定产物；
        // handled = 已归口（含绑定失败——诊断已落袋，调用方直接放弃）。
        // g6（§3.7 编译期规则）：零参 T() 按「T 符合约束的最大基类」
        //（EffectiveMemberType——无约束/仅 supers/with = Any）静态判定：
        // 界没有可访问零参 init 即编译错误（Any/Object 无 init，故无约束
        // T 的 T() 一律报错）；内建标量界例外放行（i32() 等运行期得零值）；
        // 值类型界不放行（悲观假设）。带实参形态不做静态判定，保持运行期
        // 解析（§3.7 NoSuchMethodException 兜底）
        public static BoundExpression? TryBindReifiedConstruction(ASTNode node, string name,
            List<ArgumentASTNode> arguments, Scope scope, BindContext ctx, BindEnvironment env,
            out bool handled)
        {
            handled = false;
            var head = new Symbol();
            head.elements.Add(new SymbolElement { name = name });
            var probed = env.Names.ResolveSymbolPath(head, ctx.Frame.FileCtx,
                ctx.Frame.DeclaringType, ctx.Frame.Method, allowImports: true,
                reportErrors: false, span: null);
            if (probed is not GenericParameterSymbol genericParameter) return null;
            handled = true;
            if (arguments.Count == 0
                && !CheckReifiedZeroArgBound(node, genericParameter, ctx, env))
            {
                return null;
            }
            var boundArguments = BindDynamicNewArguments(node, arguments, scope, ctx, env);
            if (boundArguments == null) return null;
            return new BoundDynamicNewExpression(node, null, genericParameter, boundArguments,
                genericParameter);
        }

        // g6 零参界检查：bound 为 T 的最大基类（无约束 = Any）。放行条件
        // 三选一：内建标量（零值特例）/ 非抽象 class 界有可访问零参 init /
        // 界完全未声明 init 且无 DA 义务字段（默认构造可满足）。其余
        //（Any/Object/接口/值类型/enum/abstract/有 init 但无可访问零参）
        // 一律编译错误「没有该方法」
        private static bool CheckReifiedZeroArgBound(ASTNode node,
            GenericParameterSymbol genericParameter, BindContext ctx, BindEnvironment env)
        {
            var bound = SymbolLookup.EffectiveMemberType(genericParameter, env);
            var boundDef = bound.ConstructedFrom ?? bound;
            // 内建标量例外（§3.7：编译器特殊处理，运行期产零值——VM 的
            // IsPrimitiveZeroConstructible 同集）
            if (IsBuiltinScalarZero(boundDef, env.B)) return true;
            var display = BoundAnalysis.TypeDisplay(bound);
            bool Fail(string reason)
            {
                env.Error(node.Span,
                    $"'{genericParameter.Name}()' has no such method: {reason} (T() checks " +
                    "the constraint bound at compile time, §3.7)");
                return false;
            }
            if (boundDef.IsBuiltin || boundDef.Kind != TypeKind.Class)
            {
                // Any/Object/ValueType/Enum/接口/wrapper 无可构造零参 init；
                // 非内建 struct 界按悲观假设不放行；enum struct 恒不可构造
                //（§12.2）
                return Fail(genericParameter.Constraints.Any(
                        c => c.Kind == GenericConstraintKind.Extends)
                    ? $"bound '{display}' has no accessible zero-argument init"
                    : $"unconstrained type parameter resolves to Any, which has no " +
                    "zero-argument init");
            }
            if (boundDef.IsAbstract)
            {
                return Fail($"bound '{display}' is abstract");
            }
            var inits = boundDef.Methods.Where(m => m.Kind == MethodKind.Init).ToList();
            if (inits.Count == 0)
            {
                // 界无显式 init：默认构造路径——DA 义务字段非空则构造无法
                // 担保（与 new 使用点同口径；义务字段在界的声明点未必报过）
                var missing = InitFieldDa.RequiredFields(bound, env);
                if (missing.Count > 0)
                {
                    return Fail($"bound '{display}' has no init assigning non-nullable " +
                        $"field '{missing[0].Name}'");
                }
                return true;
            }
            // init 继承原则：界自身的零参 init（可见性按使用点过滤；
            // 基类零参 init 经默认构造合成已落到界的方法表上）
            if (inits.Any(m => m.Parameters.Count == 0 && ctx.Frame.CanAccess(m)))
            {
                return true;
            }
            return Fail($"bound '{display}' has no accessible zero-argument init");
        }

        // 内建标量零值集（与 VM IsPrimitiveZeroConstructible 同集：
        // 整数/浮点/bool/char/String）
        private static bool IsBuiltinScalarZero(TypeSymbol type, BootstrapSymbols b)
        {
            return ReferenceEquals(type, b.Int8) || ReferenceEquals(type, b.Int16)
                || ReferenceEquals(type, b.Int32) || ReferenceEquals(type, b.Int64)
                || ReferenceEquals(type, b.UInt8) || ReferenceEquals(type, b.UInt16)
                || ReferenceEquals(type, b.UInt32) || ReferenceEquals(type, b.UInt64)
                || ReferenceEquals(type, b.Float) || ReferenceEquals(type, b.Double)
                || ReferenceEquals(type, b.Bool) || ReferenceEquals(type, b.Char)
                || ReferenceEquals(type, b.String);
        }

        // 动态构造的实参绑定（new 动态形态与具化泛型构造共用）：无静态
        // init 形参可对位（init 重载解析在运行期），按书写序绑定；具名
        // 实参无法随 BIL 实参表携带名字（运行期按位置匹配），编译错误
        public static List<BoundExpression>? BindDynamicNewArguments(ASTNode node,
            List<ArgumentASTNode> arguments, Scope scope, BindContext ctx, BindEnvironment env)
        {
            var bound = new List<BoundExpression>(arguments.Count);
            foreach (var argument in arguments)
            {
                if (argument.Name != null)
                {
                    env.Error(argument.Span,
                        $"P3: named argument '{argument.Name}' is not supported in dynamic " +
                        "construction (runtime init resolution is positional)");
                    return null;
                }
                var value = ExpressionDispatcher.Visit(argument.Value.Expression, scope, ctx, env);
                if (value == null) return null;
                bound.Add(value);
            }
            return bound;
        }

        // 间接调用的重载落定（callable 协议共享落点：局部/参数值、callable
        // 字段、任意值表达式的调用后缀）：operator call 候选经
        // OverloadResolution 判定（默认参数/具名实参/显式泛型/泛型可变包
        // 同直接调用口径——M108：invoke.indirect 泛型 ABI 与 invoke 同构）
        public static CallBinding? BindIndirectCallOverload(ASTNode node,
            BoundExpression indirectTarget, TypeSymbol callableType,
            List<ArgumentASTNode> arguments, List<TypeReferenceASTNode>? genericArguments,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            var typeArgs = genericArguments == null
                ? null
                : ResolveGenericArguments(genericArguments, node, ctx, env);
            if (genericArguments != null && typeArgs == null) return null;
            var callOperators = FindCallOperators(callableType, env.Unit.Symbols);
            var resolvedCall = OverloadResolution.Resolve(node, callOperators,
                arguments, scope, ctx, env, typeArgs, receiverType: callableType);
            if (resolvedCall == null) return null;
            var (callOperator, callArguments, callResultType, callPack, callTypeArgs) =
                resolvedCall.Value;
            return new CallBinding
            {
                Method = callOperator,
                Arguments = callArguments,
                IsVoid = callOperator.ReturnType == null,
                TypeArguments = callTypeArgs,
                GenericPack = callPack,
                ResultType = AsyncResultType(callOperator, callResultType, env),
                IsIndirect = true,
                IndirectTarget = indirectTarget,
            };
        }

        // 类型是否声明 operator call（M105 语句位置尾 Call 分流：不可调时
        // 回退通用兜底，让 FoldSuffixes 报原「not callable」诊断）
        public static bool HasCallOperator(TypeSymbol type, SymbolGraph? symbols = null)
        {
            return FindCallOperators(type, symbols).Count > 0;
        }

        // callable 协议的 operator call 收集（沿 BaseType 链，定义级回退；
        // 不预过滤参数个数——默认参数/具名实参由 OverloadResolution 判定）：
        // 首个声明了 call 的层级即停——派生覆写遮蔽基类抽象 call（override
        // 语义），否则 lambda 隐藏类的 override 与基类 abstract 会双候选歧义。
        // 接口 lookup 另走闭包（与 FindInstanceMethods 同一口径）。
        private static List<MethodSymbol> FindCallOperators(TypeSymbol type,
            SymbolGraph? symbols = null)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var owner = t.ConstructedFrom ?? t;
                var found = owner.Methods.Where(m => m.Kind == MethodKind.Operator
                    && m.Name == "call" && !m.IsStatic).ToList();
                if (found.Count > 0) return found;
            }
            var definition = type.ConstructedFrom ?? type;
            if (symbols != null && definition.Kind == TypeKind.Interface)
            {
                foreach (var iface in OverrideChecker.InterfaceClosure(type, symbols))
                {
                    var owner = iface.ConstructedFrom ?? iface;
                    var found = owner.Methods.Where(m => m.Kind == MethodKind.Operator
                        && m.Name == "call" && !m.IsStatic).ToList();
                    if (found.Count > 0) return found;
                }
            }
            return new List<MethodSymbol>();
        }

        // callable 接收者绑定（单段局部/参数）：收窄包装与 lambda 捕获记录
        // 口径同 BindInstanceCallForm 的链头处理
        private static BoundExpression BindCallableReceiver(ASTNode node, SemanticSymbol headSymbol,
            BindContext ctx, BindEnvironment env)
        {
            BoundExpression receiver;
            if (headSymbol is LocalSymbol headLocal)
            {
                if (!ctx.Flow.IsAssigned(headLocal))
                {
                    env.Error(node.Span,
                        $"Use of unassigned local variable '{headLocal.Name}'");
                }
                // 局部访问器读（M107）：作 callable 接收者即读
                if ((headLocal.Getter != null || headLocal.Setter != null)
                    && headLocal.Getter == null)
                {
                    env.Error(node.Span, $"'{headLocal.Name}' has no getter");
                }
                receiver = new BoundValueReferenceExpression(node, headLocal, headLocal.Type!);
                if (ctx.IsLambda && !ctx.Locals.Contains(headLocal))
                    ctx.CapturedSymbols.Add(headLocal);
                return PathFacility.ApplyNarrowingPublic(node, receiver,
                    NarrowKey.ForSymbol(headLocal), ctx.Flow);
            }
            var headParameter = (ParameterSymbol)headSymbol;
            // S9d：可变参数体内视角（Array\<元素\> 包装——包上无 call operator，
            // 不会命中间接调用，视角统一口径保留）
            var headParameterType = PathFacility.VariadicParameterViewType(headParameter, env);
            receiver = new BoundValueReferenceExpression(node, headParameter, headParameterType);
            if (ctx.IsLambda && !ctx.LambdaParameters.Contains(headParameter))
                ctx.CapturedSymbols.Add(headParameter);
            return PathFacility.ApplyNarrowingPublic(node, receiver,
                NarrowKey.ForSymbol(headParameter), ctx.Flow);
        }

        // this 类型链命中判定（S7c-2 补 this 前置检查）：method 宿主
        // （定义级引用）在 host 的 BaseType 链或接口闭包上（构造类型回退
        // 定义比较）——`A.m()` 命中他类实例方法时 this 类型不符，不得盲补
        private static bool IsOnThisChain(TypeSymbol host, TypeSymbol owner)
        {
            for (var t = host; t != null; t = t.BaseType)
            {
                var definition = t.ConstructedFrom ?? t;
                if (ReferenceEquals(definition, owner)) return true;
                foreach (var iface in definition.Interfaces)
                {
                    if (ReferenceEquals(iface.ConstructedFrom ?? iface, owner)) return true;
                }
            }
            return false;
        }

        // S10（SYNTAX §4.5 表）：async 调用表达式类型改写——
        // `async func f(): TResult` 调用点类型 = core.coroutine.Task\<TResult\>，
        // `async func f()` = core.coroutine.Task（无结果调用仍可作值——Task
        // 句柄本身，await 归 S13）。Task 定义由 stdlib core/coroutine.rg
        // 声明；缺 stdlib 的驱动（测试 BindUnit 不带 stdlib）找不到定义时
        // 保留原返回类型（void 时保持 null）容错。
        public static SemanticSymbol? AsyncResultType(MethodSymbol method,
            SemanticSymbol? resultType, BindEnvironment env)
        {
            if (!method.IsAsync) return resultType;
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            var coroutine = core?.ChildNamespaces.FirstOrDefault(n => n.Name == "coroutine");
            var task = coroutine?.Types.FirstOrDefault(t => t.Name == "Task"
                && t.GenericParameters.Count == (resultType == null ? 0 : 1));
            if (task == null) return resultType;
            return resultType == null
                ? task
                : env.Unit.Symbols.GetConstructedType(task, resultType);
        }

        // 被调用方候选集解析：单段经 FindMethods 全查找序；多段经容器 + 末段
        // 方法（首段为值的多段已由 BindCall 分流）。重载解析与实例 receiver
        // 判定均在落定胜者后进行（S8d）。
        // containerTypeArguments（g7）：头段容器类型构造实参——随容器段名
        // 一并还原给 NameResolver，容器可解析为构造类型 Box\<i32\>；
        // containerType 输出该类型容器（构造或定义级；命名空间容器为
        // null），调用方作静态调用的宿主代入 receiverType
        private static List<MethodSymbol>? ResolveCallee(ASTNode node, List<string> calleeSegments,
            List<TypeReferenceASTNode>? containerTypeArguments, Scope scope, BindContext ctx,
            BindEnvironment env, out TypeSymbol? containerType)
        {
            containerType = null;
            var pathText = string.Join(".", calleeSegments);
            List<MethodSymbol> candidates;
            if (calleeSegments.Count == 1)
            {
                candidates = MemberLookup.FindMethods(calleeSegments[0], ctx.Frame, env,
                    node.Span);
            }
            else
            {
                // 首段为值的多段已由 BindCall 分流（实例调用形态）；
                // 此处前 N-1 段必为容器
                var container = MemberLookup.ResolveContainer(calleeSegments, node.Span, ctx.Frame,
                    env, segmentGenerics: containerTypeArguments == null
                        ? null
                        : new IReadOnlyList<TypeReferenceASTNode>?[] { containerTypeArguments });
                if (container == null) return null;
                candidates = container switch
                {
                    NamespaceSymbol ns => ns.Methods.Where(m => m.Name == calleeSegments[^1]).ToList(),
                    // 构造类型的成员表在其泛型定义上（ConstructedFrom 回退，
                    // 与 FindMethods/FindField 同口径）
                    TypeSymbol t => (t.ConstructedFrom ?? t).Methods
                        .Where(m => m.Name == calleeSegments[^1]).ToList(),
                    _ => new List<MethodSymbol>(),
                };
                if (container is TypeSymbol typeContainer) containerType = typeContainer;
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
            var accessible = candidates.Where(ctx.Frame.CanAccess).ToList();
            if (accessible.Count == 0)
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(candidates[0]));
                return null;
            }
            return accessible;
        }

        // 实例调用形态（首段为值的多段纯调用）：首段绑 receiver（局部/
        // 参数/裸字段），中间段沿 receiver 类型上色（CallForm.TryGet
        // 保证中间段无后缀，只能是字段），末段实例方法查找匹配
        private static CallBinding? BindInstanceCallForm(ASTNode node, List<string> calleeSegments,
            List<ArgumentASTNode> arguments, Scope scope, BindContext ctx, BindEnvironment env,
            List<TypeReferenceASTNode>? genericArguments = null)
        {
            BoundExpression receiver;
            var headSymbol = scope.LookupSymbol(calleeSegments[0]);
            if (headSymbol is LocalSymbol headLocal)
            {
                if (!ctx.Flow.IsAssigned(headLocal))
                {
                    env.Error(node.Span,
                        $"Use of unassigned local variable '{calleeSegments[0]}'");
                }
                receiver = new BoundValueReferenceExpression(node, headLocal, headLocal.Type!);
                if (ctx.IsLambda && !ctx.Locals.Contains(headLocal))
                    ctx.CapturedSymbols.Add(headLocal);
                // S8b：调用链头收窄（x.m() 的 x 在收窄区域内）
                receiver = PathFacility.ApplyNarrowingPublic(node, receiver,
                    NarrowKey.ForSymbol(headLocal), ctx.Flow);
            }
            else if (headSymbol is ParameterSymbol
                || ctx.Frame.Method.Parameters.Any(p => p.Name == calleeSegments[0]))
            {
                var headParameter = headSymbol as ParameterSymbol
                    ?? ctx.Frame.Method.Parameters.First(p => p.Name == calleeSegments[0]);
                // S9a 放行：参数类型可为泛型参数（引用相等身份）；
                // S9d 修复：可变参数体内视角（与 PathVisitors 首段参数
                // 同一包装——位置包 Array\<元素类型\>、具名包
                // Array\<Pair\<String, 元素类型\>\>，numbers.iterate()
                // 不再到元素类型上找成员）
                var headParameterType = PathFacility.VariadicParameterViewType(headParameter, env);
                receiver = new BoundValueReferenceExpression(node, headParameter,
                    headParameterType);
                if (ctx.IsLambda && !ctx.LambdaParameters.Contains(headParameter))
                    ctx.CapturedSymbols.Add(headParameter);
                receiver = PathFacility.ApplyNarrowingPublic(node, receiver,
                    NarrowKey.ForSymbol(headParameter), ctx.Flow);
            }
            else
            {
                // 裸字段作调用接收者（与路径绑定同口径，含 this 补全/收窄）
                var headField = MemberLookup.FindField(calleeSegments[0], ctx.Frame, env);
                if (headField == null) return null;
                var fieldReceiver = PathFacility.BindFieldReference(node, headField, ctx, env);
                if (fieldReceiver == null) return null;
                receiver = fieldReceiver;
            }
            for (int i = 1; i < calleeSegments.Count - 1; i++)
            {
                var next = PathFacility.BindInstanceFieldAccess(node, receiver, calleeSegments[i],
                    env, ctx);
                if (next == null) return null;
                // F1/V-B 段级收口（与 BindInstanceChain 同门）：多段调用
                // 形态 hd.h.n() 的中间段结果类型有效可见性；驻留去重
                UseSiteAccessibility.CheckChainValue(next, ctx, env);
                receiver = next;
            }
            return BindInstanceMethodCall(node, receiver, calleeSegments[^1], arguments, scope,
                ctx, env, genericArguments);
        }

        // 实例方法调用：receiver 按有效成员类型查找（泛型参数 T extends B
        // 按 B；无约束/supers/with 按 Any）。Bound receiver 保持原类型。
        // S9b：genericArguments 显式泛型实参（null = 未提供）
        public static CallBinding? BindInstanceMethodCall(ASTNode node, BoundExpression receiver,
            string name, List<ArgumentASTNode> arguments, Scope scope, BindContext ctx,
            BindEnvironment env, List<TypeReferenceASTNode>? genericArguments = null)
        {
            if (receiver.Type is ErrorTypeSymbol) return null;
            var receiverType = SymbolLookup.EffectiveMemberType(receiver.Type, env);
            var candidates = SymbolLookup.FindInstanceMethods(receiverType, name,
                env.Unit.Symbols);
            if (candidates.Count == 0)
            {
                // 同名字段存在：字段类型声明了 operator call → callable 协议
                // （SYNTAX §5.2，BIL §15.3）——对该字段对象虚调用 $$call；
                // 否则保持既有 "is not a method" 诊断（行为不变）
                if (SymbolLookup.FindInstanceField(receiverType, name) is { } callableField)
                {
                    var fieldType = SymbolLookup.SubstituteFieldType(callableField, receiverType,
                        env.Unit.Symbols);
                    if (fieldType is not ErrorTypeSymbol)
                    {
                        var callableFieldType = SymbolLookup.EffectiveMemberType(fieldType, env);
                        if (FindCallOperators(callableFieldType, env.Unit.Symbols).Count > 0)
                        {
                            var fieldTarget = new BoundFieldAccessExpression(node, receiver,
                                callableField, fieldType);
                            return BindIndirectCallOverload(node, fieldTarget, callableFieldType,
                                arguments, genericArguments, scope, ctx, env);
                        }
                    }
                    env.Error(node.Span, $"'{name}' on type '{BoundAnalysis.TypeDisplay(receiver.Type)}' is not a method");
                    return null;
                }
                if (!ProxyMatching.IsDowngradeEligible(receiverType))
                {
                    env.Error(node.Span, $"Undefined member '{name}' on type " +
                        $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
                    return null;
                }
                var downgradeTypeArgs = genericArguments == null
                    ? Array.Empty<SemanticSymbol>()
                    : ResolveGenericArguments(genericArguments, node, ctx, env);
                if (downgradeTypeArgs == null) return null;
                return BindDowngradeCall(node, receiver, receiverType, name, arguments,
                    downgradeTypeArgs, scope, ctx, env);
            }
            // 使用点访问控制（S8e，SYNTAX §16.1）：同 ResolveCallee 口径
            var accessible = candidates.Where(ctx.Frame.CanAccess).ToList();
            if (accessible.Count == 0)
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(candidates[0]));
                return null;
            }
            var typeArgs = genericArguments == null
                ? null
                : ResolveGenericArguments(genericArguments, node, ctx, env);
            if (genericArguments != null && typeArgs == null) return null;
            var resolved = OverloadResolution.Resolve(node, accessible, arguments, scope, ctx, env,
                typeArgs, receiverType);
            if (resolved == null) return null;
            var (selected, boundArguments, selectedResultType, genericPack, selectedTypeArgs) =
                resolved.Value;
            // S9a 放行：返回类型含泛型参数（引用相等身份透传，P4 §7.5 投影）
            return new CallBinding
            {
                Method = selected,
                Arguments = boundArguments,
                IsVoid = selected.ReturnType == null,
                Receiver = receiver,
                TypeArguments = selectedTypeArgs,
                GenericPack = genericPack,
                // S10：async 调用表达式类型改写（同 BindCall 口径）
                ResultType = AsyncResultType(selected, selectedResultType, env),
            };
        }

        // §14.2 / §14.3 get 类：Value `.proxy.get`、Entity `.proxy.get.*` /
        // `.proxy.get.<名>`。`.proxy.getFoo`（specific 方法）不命中。
        internal static bool IsGetCategoryProxy(string proxyName)
        {
            return proxyName == ".proxy.get" || proxyName.StartsWith(".proxy.get.", StringComparison.Ordinal);
        }

        // M88：proxy 体 inner(...) 模板占位绑定
        private static CallBinding? BindInnerCall(ASTNode node, List<ArgumentASTNode> arguments,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            if (!ctx.Proxy.IsActive)
            {
                env.Error(node.Span,
                    "P3: 'inner' is only available in a wrapper proxy body (§14.2)");
                return null;
            }
            var proxy = ctx.Frame.Method;
            // §14.2 / §14.3：get 类 proxy 不得调用 inner——value 参数即内层
            // 已算好的结果，get 链没有向内的下一环。
            if (IsGetCategoryProxy(proxy.Name))
            {
                env.Error(node.Span,
                    $"P3: get-category proxy '{proxy.Name}' cannot call inner(...) (§14.2/§14.3)");
                return null;
            }
            // 实参无目标类型预绑
            var boundArgs = new List<BoundExpression>();
            for (int i = 0; i < arguments.Count; i++)
            {
                var bound = ExpressionDispatcher.Visit(arguments[i].Value.Expression, scope, ctx,
                    env);
                if (bound == null) return null;
                boundArgs.Add(bound);
            }
            // 形状检查：inner(...) 调用形状 = proxy 函数自身的参数形状（全形状）。
            // wildcard 的保留首参（symbol / .name）同样必须显式出现在 inner 实参里，
            // 由 BIL invoke fn(..inner) 显式携带，VM 消费它做下一环重路由。
            if (!InnerShapeMatches(proxy.Parameters, arguments, boundArgs, env))
            {
                env.Error(node.Span,
                    $"inner(...) arguments do not match proxy '{proxy.Name}' shape (§14.2)");
                return null;
            }
            var isVoid = proxy.ReturnType == null;
            // #27⑦：按声明序收集 proxy 方法可变泛型包（固定泛型不转发——
            // 特化侧 Middleware 自持；包整体透传才需显式前置操作数）
            var forwardedPacks = new List<GenericParameterSymbol>();
            foreach (var genericParameter in proxy.GenericParameters)
            {
                if (genericParameter.IsVariadic || genericParameter.IsNamedVariadic)
                {
                    forwardedPacks.Add(genericParameter);
                }
            }
            return new CallBinding
            {
                Method = proxy,
                Arguments = boundArgs,
                IsVoid = isVoid,
                Receiver = null,
                TypeArguments = Array.Empty<SemanticSymbol>(),
                GenericPack = null,
                ResultType = isVoid ? null : proxy.ReturnType,
                IsInnerCall = true,
                ForwardedGenericPacks = forwardedPacks,
            };
        }

        // super(...) 仅绑定直接基类原始实现；访问控制和 wrapper 派发均不参与。
        private static CallBinding? BindSuperCall(ASTNode node, List<ArgumentASTNode> arguments,
            Scope scope, BindContext ctx, BindEnvironment env,
            List<TypeReferenceASTNode>? genericArguments)
        {
            var current = ctx.Frame.Method;
            if (genericArguments != null)
            {
                env.Error(node.Span, "P3: super(...) does not accept explicit type arguments");
                return null;
            }
            if (ctx.Proxy.IsActive)
            {
                env.Error(node.Span, "P3: super(...) is not available in a wrapper proxy body");
                return null;
            }
            if (current.Owner == null || current.IsStatic)
            {
                env.Error(node.Span, "P3: super(...) is only available in an instance member body");
                return null;
            }
            if (current.Kind != MethodKind.Init
                && (current.Kind != MethodKind.Regular || !current.IsOverride))
            {
                env.Error(node.Span,
                    "P3: super(...) is only available in an override method or init body");
                return null;
            }
            var baseType = current.Owner.BaseType;
            if (baseType == null)
            {
                env.Error(node.Span, "P3: super(...) requires a direct base type");
                return null;
            }
            if (current.GenericParameters.Any(p => p.IsVariadic || p.IsNamedVariadic))
            {
                env.Error(node.Span,
                    "P3: super(...) in a method with generic variadic parameters is not supported yet");
                return null;
            }
            var baseDefinition = baseType.ConstructedFrom ?? baseType;
            var candidates = baseDefinition.Methods.Where(method => current.Kind == MethodKind.Init
                ? method.Kind == MethodKind.Init
                : method.Kind == MethodKind.Regular && !method.IsStatic && method.Name == current.Name)
                .ToList();
            if (candidates.Count == 0)
            {
                env.Error(node.Span, current.Kind == MethodKind.Init
                    ? "P3: direct base type has no applicable init for super(...)"
                    : $"P3: direct base type has no method '{current.Name}' for super(...)");
                return null;
            }
            var forwardedTypeArgs = current.GenericParameters.Cast<SemanticSymbol>().ToList();
            var resolved = OverloadResolution.Resolve(node, candidates, arguments, scope, ctx, env,
                forwardedTypeArgs.Count == 0 ? null : forwardedTypeArgs, receiverType: baseType);
            if (resolved == null) return null;
            var resolvedArguments = resolved.Value.Arguments;
            // §9.2.2：super init 实参在编译期 cast 到被解析 init 的形参声明
            // 类型（精确符合——如 Leaf 实参 cast 到 Node 形参，落编译器生成
            // 的 cast 指令）。ranking 只在语义期：重载解析已选定目标 init，
            // VM 运行期重匹配（§15.5）按可赋值性进行，cast 把值类型窄化/
            // 装箱/可空包装在调用点落定，引用类型 upcast 为恒成功的零开销
            // 确认（VM 不改写运行期 typeid）
            if (current.Kind == MethodKind.Init)
            {
                var targetInit = resolved.Value.Method;
                for (var i = 0; i < resolvedArguments.Count
                    && i < targetInit.Parameters.Count; i++)
                {
                    var parameterType = targetInit.Parameters[i].Type;
                    var argument = resolvedArguments[i];
                    if (parameterType == null || argument.Type == null
                        || argument.Type is ErrorTypeSymbol)
                    {
                        continue;
                    }
                    // 构造泛型基类（Entry : Pair<String, i32> 的 super(k, v)）：
                    // 定义级形参类型按 extends 实参代入后再作 cast 目标；
                    // 代入后仍含泛型参数（泛型类内的 super）跳过——VM
                    // 运行期匹配对泛型占位本就代入后再比对（§15.5）
                    parameterType = env.Unit.Symbols.Substitute(parameterType,
                        baseDefinition, baseType);
                    if (parameterType == null || parameterType is ErrorTypeSymbol
                        || SymbolLookup.ContainsGenericParameter(parameterType)
                        || ReferenceEquals(argument.Type, parameterType))
                    {
                        continue;
                    }
                    resolvedArguments[i] = new BoundCastExpression(node, argument,
                        parameterType, isSafe: false, type: parameterType);
                }
            }
            return new CallBinding
            {
                Method = resolved.Value.Method,
                Arguments = resolvedArguments,
                IsVoid = resolved.Value.ReturnType == null,
                Receiver = null,
                TypeArguments = forwardedTypeArgs,
                GenericPack = resolved.Value.GenericPack,
                ResultType = resolved.Value.ReturnType,
                IsSuperCall = true,
            };
        }

        // inner 实参形状：个数一致；具名按名归位；类型兼容（无目标预绑
        // 产物 vs 形参类型）。wildcard 形参类型常为泛型参数（TNamedArgs 等）
        // 或可变参数包——体内引用视角是 Array 包（VariadicParameterViewType），
        // 与声明元素类型不同，须按体内视角比对；同名参数转发直通
        private static bool InnerShapeMatches(IReadOnlyList<ParameterSymbol> parameters,
            List<ArgumentASTNode> arguments, List<BoundExpression> boundArgs, BindEnvironment env)
        {
            if (parameters.Count != arguments.Count) return false;
            var used = new bool[parameters.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                int targetIndex;
                if (arguments[i].Name != null)
                {
                    targetIndex = -1;
                    for (int p = 0; p < parameters.Count; p++)
                    {
                        if (parameters[p].Name == arguments[i].Name) { targetIndex = p; break; }
                    }
                    if (targetIndex < 0 || used[targetIndex]) return false;
                }
                else
                {
                    targetIndex = -1;
                    for (int p = 0; p < parameters.Count; p++)
                    {
                        if (!used[p]) { targetIndex = p; break; }
                    }
                    if (targetIndex < 0) return false;
                }
                used[targetIndex] = true;
                var parameter = parameters[targetIndex];
                var paramType = parameter.Type;
                if (paramType is ErrorTypeSymbol) continue;
                // 同名参数直通转发（wildcard 包转发典型形态）
                if (boundArgs[i] is BoundValueReferenceExpression { Symbol: ParameterSymbol argParam }
                    && argParam.Name == parameter.Name)
                {
                    continue;
                }
                // 可变参数：体内视角是包数组，与声明元素类型不同
                var expected = parameter.IsVariadic || parameter.IsNamedVariadic
                    ? PathFacility.VariadicParameterViewType(parameter, env)
                    : paramType;
                if (expected == null) continue;
                if (!SymbolLookup.IsAssignable(boundArgs[i].Type, expected, env)
                    && !BoundAnalysis.IsDowngradeCallResult(boundArgs[i], env))
                {
                    return false;
                }
            }
            return used.All(u => u);
        }

        // M88 降级调用合成（SYNTAX §14.7 胖值 ABI）：未声明方法的调用点改绑
        // bootstrap Any.call???——三实参：
        //   symbol = PrintDowngradeRequest canonical；
        //   namedArgs = 具名包；unnamedArgs = 位置包。
        // 返回值恒 Any——调用点转换由 P4a §6.5 物化承担
        private static CallBinding? BindDowngradeCall(ASTNode node, BoundExpression receiver,
            TypeSymbol receiverType, string name,
            List<ArgumentASTNode> arguments, IReadOnlyList<SemanticSymbol> typeArguments,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            env.B.EnsureCallWildcard(env.Unit.Symbols);
            var callWildcard = env.B.CallWildcard;
            var boundArgs = new BoundExpression[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                var bound = ExpressionDispatcher.Visit(arguments[i].Value.Expression, scope, ctx,
                    env);
                if (bound == null) return null;
                boundArgs[i] = bound;
            }
            var requestArgs = new (string? ArgName, SemanticSymbol ArgType)[arguments.Count];
            var positionalValues = new List<BoundExpression>();
            var namedValues = new List<(string Name, BoundExpression Value)>();
            for (int i = 0; i < arguments.Count; i++)
            {
                requestArgs[i] = (arguments[i].Name, boundArgs[i].Type);
                if (arguments[i].Name != null)
                {
                    namedValues.Add((arguments[i].Name!, boundArgs[i]));
                }
                else
                {
                    positionalValues.Add(boundArgs[i]);
                }
            }
            var symbolLit = BindingDriver.MakeStringLiteral(env,
                CanonicalSymbolPrinter.PrintDowngradeRequest(receiverType, name, typeArguments,
                    requestArgs));
            var unnamedPack = new BoundVarArgsArgument(node, isNamed: false, positionalValues, null,
                env.Unit.Symbols.GetConstructedType(env.B.ArrayDefinition, env.B.Any));
            var namedPack = new BoundVarArgsArgument(node, isNamed: true,
                Array.Empty<BoundExpression>(), namedValues, BindingDriver.NamedPackType(env));
            return new CallBinding
            {
                Method = callWildcard,
                Arguments = new List<BoundExpression> { symbolLit, namedPack, unnamedPack },
                IsVoid = false,
                Receiver = receiver,
                TypeArguments = Array.Empty<SemanticSymbol>(),
                GenericPack = null,
                ResultType = env.B.Any,
            };
        }

        // 实参绑定（单候选落定）：位置实参按序、具名实参按形参名归位——
        // 产物已是规范参数序（ARCHITECTURE §2「BoundCall 已是规范参数序」）；
        // S8d：位置/具名冲突统一 Duplicate 诊断（此前位置实参会静默覆盖
        // 具名占位）+ 缺省形参以预绑定默认值填充。
        // S9b：parameterTypes 为泛型候选的代入后参数类型（null = 用
        // 声明类型——非泛型候选与既有行为一致）。
        // S9d：可变参数方法（末参 IsVariadic/IsNamedVariadic）——固定形参
        // 映射同前，剩余实参打包为 BoundVarArgsArgument（vargs 位置包 /
        // kwargs 具名包，作为规范序最后一个元素，Type = Array\<Any\>；
        // 具名实参名不在固定表且存在具名包时归包）
        public static List<BoundExpression>? BindArguments(MethodSymbol target,
            List<ArgumentASTNode> arguments, Scope scope, CharRange? callSpan, BindContext ctx,
            BindEnvironment env, IReadOnlyList<SemanticSymbol>? parameterTypes = null)
        {
            var parameters = target.Parameters;
            var effectiveTypes = parameterTypes ?? parameters.Select(p => p.Type!).ToList();
            var hasPack = parameters.Count > 0
                && (parameters[^1].IsVariadic || parameters[^1].IsNamedVariadic);
            var fixedCount = hasPack ? parameters.Count - 1 : parameters.Count;
            var bound = new BoundExpression?[parameters.Count];
            var failed = false;
            var nextPositional = 0;
            var vargsValues = new List<BoundExpression>();
            var kwargsValues = new List<(string Name, BoundExpression Value)>();
            foreach (var argument in arguments)
            {
                int index;
                if (argument.Name == null)
                {
                    if (nextPositional < fixedCount)
                    {
                        index = nextPositional++;
                    }
                    else if (hasPack && !parameters[^1].IsNamedVariadic)
                    {
                        // 位置包：剩余位置实参全部归包（元素绑定不在此处）
                        var packValue = ExpressionDispatcher.Visit(argument.Value.Expression,
                            scope, ctx, env);
                        if (packValue == null)
                        {
                            failed = true;
                            continue;
                        }
                        vargsValues.Add(packValue);
                        continue;
                    }
                    else
                    {
                        env.Error(argument.Span, $"Too many arguments for '{target.Name}'");
                        failed = true;
                        continue;
                    }
                }
                else
                {
                    index = -1;
                    for (int i = 0; i < fixedCount; i++)
                    {
                        if (parameters[i].Name == argument.Name) { index = i; break; }
                    }
                    if (index < 0)
                    {
                        // 具名包：具名实参名不在固定表且存在具名包时归包
                        if (hasPack && parameters[^1].IsNamedVariadic)
                        {
                            var packValue = ExpressionDispatcher.Visit(argument.Value.Expression,
                                scope, ctx, env);
                            if (packValue == null)
                            {
                                failed = true;
                                continue;
                            }
                            kwargsValues.Add((argument.Name, packValue));
                            continue;
                        }
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
                var expected = effectiveTypes[index] as TypeSymbol;
                var value = ExpressionDispatcher.Visit(argument.Value.Expression, scope, ctx, env,
                    expected);
                if (value == null)
                {
                    failed = true;
                    continue;
                }
                // S11e：降级调用结果 Any 可作任意形参实参（P4a cast 物化
                // 兜底，同 OverloadResolution.IsApplicable 豁免口径）
                if (expected != null && !SymbolLookup.IsAssignable(value.Type, expected, env)
                    && !BoundAnalysis.IsDowngradeCallResult(value, env))
                {
                    env.Error(argument.Value.Span ?? argument.Span,
                        $"Cannot pass '{BoundAnalysis.TypeDisplay(value.Type)}' as " +
                        $"'{BoundAnalysis.TypeDisplay(expected)}'");
                    failed = true;
                    continue;
                }
                bound[index] = value;
            }
            // S9d：包槽填打包节点（空包 = 空数组构造；syntax 取首个实参的
            // 表达式根——空包（`g()` 调全可变方法）无实参可挂，以当前源
            // 文件根占位（BoundNode.Syntax 非空契约；P4 打包仅作 Origin，
            // 文件级定位足够——调用节点顺传需 OverloadResolution 调用点
            // 配合，归后续）
            if (hasPack)
            {
                var packType = env.Unit.Symbols.GetConstructedType(env.B.ArrayDefinition,
                    env.B.Any);
                bound[fixedCount] = new BoundVarArgsArgument(
                    arguments.Count > 0 ? (ASTNode)arguments[0].Value : ctx.Frame.FileCtx.File,
                    isNamed: parameters[^1].IsNamedVariadic, vargsValues,
                    parameters[^1].IsNamedVariadic ? kwargsValues : null, packType);
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
    // 泛型定义不可构造；Class/Struct 可构造，Interface/Wrapper 各自归口
    // 诊断，EnumStruct 永久拒绝（§12.2：enum 值只能经具名 case 入口产生）；
    // init 匹配（无显式 init 时零参默认构造；多匹配归 S8d ranking）。
    // §3.7 动态形态：操作数未命中类型时按值绑定（Type\<T\> 值），走
    // new.indirect typeid 构造（与具化泛型 TResult() 同一机制）
    internal sealed class NewVisitor : ExpressionVisitor<NewVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var newNode = (NewExpressionASTNode)node;
            // 形态探测（不落袋，与 is/supers/with 右侧同口径）：未命中类型
            //（含命中命名空间等非类型符号）才尝试动态形态；命中类型/泛型
            // 参数走下方静态路径（诊断口径不变）
            var probed = env.Names.ResolveSymbolPath(newNode.Type.TypeSymbol.symbol,
                ctx.Frame.FileCtx, ctx.Frame.DeclaringType, ctx.Frame.Method,
                allowImports: true, reportErrors: false, span: null);
            if (probed is ErrorTypeSymbol || probed is not (TypeSymbol or GenericParameterSymbol))
            {
                return BindDynamic(newNode, scope, ctx, env);
            }
            var type = TypeReferences.Resolve(newNode.Type, newNode.Type.Span ?? newNode.Span,
                ctx.Frame, env, ctx);
            if (type is ErrorTypeSymbol) return null;
            if (type == null) return null;  // 解析失败（已诊断）
            // S9a：泛型参数 new 归 S9c（运行时按 typeid 构造，静态 init 查找
            // 不可行——P4 发射形态随 hidden args 落地）
            if (type is not TypeSymbol typeSymbol)
            {
                env.Error(newNode.Type.Span ?? newNode.Span,
                    $"P3: constructing a generic type parameter is not supported yet (S9)");
                return null;
            }
            if (typeSymbol.ConstructedFrom == null && typeSymbol.GenericParameters.Count > 0)
            {
                env.Error(newNode.Type.Span ?? newNode.Span,
                    $"Cannot construct generic type definition '{type.Name}'");
                return null;
            }
            switch (typeSymbol.Kind)
            {
                case TypeKind.Class:
                case TypeKind.Struct:
                    break;
                case TypeKind.Interface:
                    env.Error(newNode.Type.Span ?? newNode.Span,
                        $"Cannot construct interface '{type.Name}'");
                    return null;
                case TypeKind.EnumStruct:
                    // §12.2 永久规则：enum 值只能经具名 case 入口产生——
                    // 无论 init 是否 pub，直接构造一律非法（对
                    // Type\<Enum\> 值 new / 泛型 T() 运行时解析到 enum
                    // 同为非法构造——两动态路径已落地，运行期由 VM 的
                    // new.indirect 拒绝，抛 core.NoSuchMethodException）
                    env.Error(newNode.Type.Span ?? newNode.Span,
                        $"Cannot construct enum struct '{type.Name}' directly; " +
                        "use its named cases");
                    return null;
                default:
                    env.Error(newNode.Type.Span ?? newNode.Span,
                        $"Cannot construct wrapper '{type.Name}' (created by the compiler)");
                    return null;
            }
            // abstract 类不可构造（SYNTAX §9.2.1）
            if (typeSymbol.IsAbstract)
            {
                env.Error(newNode.Type.Span ?? newNode.Span,
                    $"Cannot construct an instance of abstract type '{type.Name}'");
                return null;
            }
            // S9c：构造类型的 init 在定义级查（构造类型成员表恒空——回退
            // ConstructedFrom；init 形参的宿主泛型参数由 Resolve 的
            // receiverType 代入——`new C\<i32>(1)` 的 init(x: T) 实参按 i32 绑定）
            var inits = (typeSymbol.ConstructedFrom ?? typeSymbol).Methods
                .Where(m => m.Kind == MethodKind.Init).ToList();
            if (inits.Count == 0)
            {
                // 无显式 init 的零参构造（§9.3 默认构造）
                if (newNode.Arguments.Count == 0)
                {
                    // P18/S2（§9.3 DA）：零值兜底已废除——类型从未声明
                    // init 时声明点不报错（抽象类/仅声明场景合法），构造点
                    // 要求不存在无初始值非空字段义务
                    var missing = InitFieldDa.RequiredFields(typeSymbol, env);
                    if (missing.Count > 0)
                    {
                        env.Error(newNode.Span,
                            $"Type '{typeSymbol.Name}' has no constructor that assigns " +
                            $"non-nullable field '{missing[0].Name}' (§9.3: declare an " +
                            "init that assigns it, add a declaration initializer, or make " +
                            "the field Nullable)");
                        return null;
                    }
                    return new BoundNewExpression(node, typeSymbol, null, new List<BoundExpression>());
                }
                env.Error(newNode.Span, $"Type '{typeSymbol.Name}' has no constructor");
                return null;
            }
            // 构造调用是使用点（S8e，SYNTAX §16.1，含 init 可见性 §12.2）：
            // 不可见 init 不参与重载解析；全部不可见时报不可见诊断
            var accessibleInits = inits.Where(ctx.Frame.CanAccess).ToList();
            if (accessibleInits.Count == 0)
            {
                env.Error(newNode.Span, AccessChecker.InaccessibleMessage(inits[0]));
                return null;
            }
            // init 重载解析与函数调用同一设施（S8d，SYNTAX §4.2）；S9c：
            // receiverType = 构造类型（init 宿主泛型参数代入）
            var resolved = OverloadResolution.Resolve(newNode, accessibleInits, newNode.Arguments,
                scope, ctx, env, receiverType: typeSymbol);
            if (resolved == null) return null;
            return new BoundNewExpression(node, typeSymbol, resolved.Value.Method,
                resolved.Value.Arguments);
        }

        // 动态形态（SYNTAX §3.7，BIL §14.2 new.indirect）：new 的操作数是
        // Type\<T\> 值——按值绑定（与 is/supers/with 动态右侧同一轮子），
        // 结果静态类型 = Type\<T\> 的 T；init 重载解析在运行期按 typeid
        // 完成（抽象/enum struct/无匹配 init 抛 core.NoSuchMethodException）。
        // 值未命中回落类型引用解析报原「未解析/不是类型」诊断
        private static BoundExpression? BindDynamic(NewExpressionASTNode newNode, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            var typeValue = PathFacility.BindTypeSlotValue(newNode, newNode.Type, scope, ctx, env,
                out var valueFound);
            if (typeValue == null)
            {
                // 值命中但绑定失败时诊断已落袋；未命中回落静态诊断口径
                if (valueFound) return null;
                _ = TypeReferences.Resolve(newNode.Type, newNode.Type.Span ?? newNode.Span,
                    ctx.Frame, env, ctx);
                return null;
            }
            // 值必须承载 Type\<T\>（new.indirect 的 TYPEID_VAR 操作数）；
            // ErrorType 毒化静默（结果沿用 ErrorType）
            SemanticSymbol resultType;
            if (typeValue.Type is ErrorTypeSymbol errorType)
            {
                resultType = errorType;
            }
            else if (typeValue.Type is TypeSymbol { ConstructedFrom: { } typeDefinition }
                valueType && ReferenceEquals(typeDefinition, env.B.TypeDefinition))
            {
                resultType = valueType.TypeArguments![0];
            }
            else
            {
                env.Error(newNode.Type.Span ?? newNode.Span,
                    "operand of 'new' must be a type or a Type\\<T\\> value: " +
                    $"'{NameResolver.PathText(newNode.Type.TypeSymbol.symbol)}'");
                return null;
            }
            var arguments = CallFacility.BindDynamicNewArguments(newNode, newNode.Arguments,
                scope, ctx, env);
            if (arguments == null) return null;
            return new BoundDynamicNewExpression(newNode, typeValue, null, arguments, resultType);
        }
    }
}
