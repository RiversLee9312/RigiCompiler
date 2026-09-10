namespace RigiCompiler
{
    // 局部变量声明（S5）与解构声明（S7f，SYNTAX §18）。
    // 自旧 BindSession.BindLocalDeclaration/BindDestructuring/FindCorePairDefinition
    // 迁移，行为不变。
    internal sealed class LocalDeclarationVisitor
        : BinderVisitor<LocalDeclarationVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var decl = (VariableDeclarationASTNode)node;
            // 解构声明（S7f，SYNTAX §18）：与单名形态互斥
            if (decl.DestructureNames != null)
            {
                // 解构分量上的 wrapper 应用归口（S11——分量的隐藏字段/栈帧
                // 槽布局归 proxy 烘焙统一处理）
                if (decl.Annotations.Count > 0)
                {
                    env.Error(decl.Span, "P3: wrapper applications on destructuring " +
                        "declarations are not supported yet (S11)");
                }
                return BindDestructuring(decl, scope, ctx, env);
            }
            // S9a：声明类型可为泛型参数（引用相等身份）
            SemanticSymbol? declaredType = null;
            if (decl.TypeAnnotation != null)
            {
                declaredType = TypeReferences.Resolve(decl.TypeAnnotation, decl.Span, ctx.Frame,
                    env, ctx);
            }
            // 初始化表达式先于变量入作用域绑定（var x = x 报未定义而非自引用）
            var init = decl.Initializer == null
                ? null
                : ExpressionDispatcher.Visit(decl.Initializer.Expression, scope, ctx, env,
                    declaredType as TypeSymbol);
            var type = declaredType ?? init?.Type;
            if (type == null)
            {
                env.Error(decl.Span,
                    $"Variable '{decl.Name}' requires a type annotation or an initializer");
                return null;
            }
            // 推断类型使用点检查（SYNTAX §16.1，bug S5 修复2；F1/V-A 起
            // 递归口径）：无标注 const/var 的推断类型同样是使用点——泄漏在
            // 声明推断点报一次，下游成员访问不重复报（级联控制；显式标注
            // 路径已由 TypeReferences.Resolve 检查——按语法上有无标注区分，
            // 标注解析失败（毒化 null）落入推断时不得重报）。F1 起与
            // ExpressionDispatcher 统一收口共用驻留去重：init 表达式经收口
            // 已报时本挂点静默（兜底非 dispatcher 来源，不重复报）
            if (decl.TypeAnnotation == null)
            {
                UseSiteAccessibility.CheckInferredType(type, decl.Span, ctx, env);
            }
            // 带访问器局部须显式类型标注（同字段规则——推断与访问器不共存）
            if ((decl.Getter != null || decl.Setter != null) && decl.TypeAnnotation == null)
            {
                env.Error(decl.Span,
                    $"Local '{decl.Name}' with accessors requires a type annotation");
            }
            if (decl.IsConst && init == null)
            {
                env.Error(decl.Span, $"Const '{decl.Name}' must have an initializer");
                return null;
            }
            if (declaredType != null && init != null
                && !SymbolLookup.IsAssignable(init.Type, declaredType, env)
                && !BoundAnalysis.IsDowngradeCallResult(init, env))
            {
                env.Error(decl.Initializer!.Span ?? decl.Span,
                    $"Cannot assign '{BoundAnalysis.TypeDisplay(init.Type)}' to " +
                    $"'{BoundAnalysis.TypeDisplay(declaredType)}'");
            }
            if (scope.DeclaresHere(decl.Name))
            {
                env.Error(decl.Span, $"Duplicate local variable '{decl.Name}'");
                return null;
            }
            var local = new LocalSymbol(decl.Name, type, decl.IsConst);
            // wrapper 应用先登记（不 cell 化）——访问器与 wrapper 共用一次
            // EnsureCellStorage（路线 C：cell 承载访问器体 + wrapped(W)）
            CollectLocalWrapperApplications(decl, local, scope, ctx, env);
            var hasAccessors = decl.Getter != null || decl.Setter != null;
            if (hasAccessors)
            {
                ValidateAndPrepareLocalAccessors(decl, local, env);
                // 访问器体绑定在声明点词法作用域（本局部尚未 Declare——
                // 体内自引用按未定义；自由变量捕获不收本符号）
                CellClassFactory.EnsureCellStorage(local, decl, ctx, env, scope,
                    decl.Getter, decl.Setter);
            }
            else if (local.AppliedWrappers.Count > 0)
            {
                CellClassFactory.EnsureCellStorage(local, decl, ctx, env);
            }
            scope.Declare(local);
            ctx.Locals.Add(local);
            if (init != null) ctx.Flow.MarkAssigned(local);
            return new BoundLocalDeclarationStatement(node, local, init);
        }

        // 局部访问器声明侧校验（M107，SYNTAX §9.4/§9.4.1 栈上形态）：
        // const+setter、无体计算、访问级别修饰符禁令；HasBackingStorage 落定
        private static void ValidateAndPrepareLocalAccessors(VariableDeclarationASTNode decl,
            LocalSymbol local, BindEnvironment env)
        {
            var sample = (decl.Getter ?? decl.Setter)!;
            local.HasBackingStorage = sample.HasBackingField;
            if (local.IsConst && decl.Setter != null)
            {
                env.Error(decl.Setter.Span ?? decl.Span,
                    $"Const local '{local.Name}' cannot declare a setter");
            }
            if (decl.Getter is { Body: null } && !local.HasBackingStorage)
            {
                env.Error(decl.Getter.Span ?? decl.Span,
                    $"Computed getter of '{local.Name}' must have a body " +
                    "(compiler-generated accessors require a backing field)");
            }
            if (decl.Setter is { Body: null } && !local.HasBackingStorage)
            {
                env.Error(decl.Setter.Span ?? decl.Span,
                    $"Computed setter of '{local.Name}' must have a body " +
                    "(compiler-generated accessors require a backing field)");
            }
            CheckLocalAccessorModifiers(decl.Getter, env);
            CheckLocalAccessorModifiers(decl.Setter, env);
        }

        // 局部访问器无可见性/多态概念（§9.4 栈上形态）——禁 pub/priv 等
        private static void CheckLocalAccessorModifiers(PropertyAccessorASTNode? accessor,
            BindEnvironment env)
        {
            if (accessor == null || accessor.Modifiers.Count == 0) return;
            foreach (var modifier in accessor.Modifiers.Distinct())
            {
                env.Error(accessor.Span,
                    $"Local variable accessor cannot have modifier '{modifier}' " +
                    "(local accessors have no visibility or inheritance)");
            }
        }

        // 局部变量 wrapper 应用登记（S11，SYNTAX §14.3/§14.9 矩阵 C）：
        // 栈上声明不进 P1/P2——注解解析与类别检查在此落地；栈上变量恒为
        // 合法 Value wrapper 目标（矩阵 C），无 shared/宿主检查。诊断措辞
        // 与 P2 WrapperCheckers 对齐。cell 化由调用方统一触发（可与访问器合并）
        private static void CollectLocalWrapperApplications(VariableDeclarationASTNode decl,
            LocalSymbol local, Scope scope, BindContext ctx, BindEnvironment env)
        {
            foreach (var annotation in decl.Annotations)
            {
                if (ResolveEnvironment.IsWrapperTargetAnnotation(annotation))
                {
                    env.Error(annotation.Span ?? decl.Span,
                        "@WrapperTarget can only be applied to wrapper declarations");
                    continue;
                }
                if (ResolveEnvironment.NativeAnnotationNameOf(annotation) is { } builtinName)
                {
                    env.Error(annotation.Span ?? decl.Span,
                        $"@{builtinName} can only be applied to native functions");
                    continue;
                }
                if (ResolveEnvironment.IsEntryPointAnnotation(annotation))
                {
                    env.Error(annotation.Span ?? decl.Span,
                        "@EntryPoint can only be applied to functions");
                    continue;
                }
                if (ResolveEnvironment.IsTerminalAnnotation(annotation))
                {
                    env.Error(annotation.Span ?? decl.Span,
                        "@Terminal can only be applied to wrapper declarations");
                    continue;
                }
                if (ResolveEnvironment.IsInternalAnnotation(annotation))
                {
                    env.Error(annotation.Span ?? decl.Span,
                        "@Internal can only be applied to wrapper declarations");
                    continue;
                }
                var resolved = env.Names.ResolveSymbolPath(annotation.Name.symbol,
                    ctx.Frame.FileCtx, ctx.Frame.DeclaringType, ctx.Frame.Method,
                    allowImports: true, reportErrors: true,
                    span: annotation.Name.Span ?? annotation.Span ?? decl.Span,
                    allowBareGenericDefinition: true);
                if (resolved is ErrorTypeSymbol) continue;    // 毒化静默
                if (resolved is not TypeSymbol { Kind: TypeKind.Wrapper } wrapperType)
                {
                    env.Error(annotation.Span ?? decl.Span,
                        $"'{NameResolver.PathText(annotation.Name.symbol)}' is not a wrapper type");
                    continue;
                }
                // wrapper 声明自身的 @WrapperTarget 缺失/非法已在 P2 声明处报过，静默
                if (wrapperType.WrapperTarget is { } targetKind
                    && targetKind != WrapperTargetKind.Value)
                {
                    env.Error(annotation.Span ?? decl.Span, targetKind == WrapperTargetKind.Entity
                        ? $"Entity wrapper '{wrapperType.Name}' can only be applied to type declarations"
                        : $"Method wrapper '{wrapperType.Name}' can only be applied to methods");
                    continue;
                }
                // §14.3：只实现 get 的 Value wrapper 只适用于只读变量——var
                // 上应用 get-only wrapper 即编译错误（写入失败检查前移，
                // 不再推迟到运行期；诊断只带声明名，不泄漏 cell 合成符号）
                if (!decl.IsConst && ProxyMatching.IsGetOnlyValueWrapper(wrapperType))
                {
                    env.Error(annotation.Span ?? decl.Span,
                        $"Value wrapper '{wrapperType.Name}' does not implement .proxy.set; " +
                        $"get-only wrappers cannot be applied to mutable variable '{decl.Name}' " +
                        "(§14.3: only read-only variables)");
                    continue;
                }
                WrapperApplicationChecker.CheckInternalApplication(wrapperType,
                    ctx.Frame.FileCtx.Namespace, annotation.Span ?? decl.Span, env.Error);
                var appliedType = WrapperApplicationChecker.SubstituteWrapperApplication(
                    wrapperType, local, env.Unit.Symbols);
                var app = new WrapperApplication(appliedType, annotation);
                GenericConstraints.CheckConstructedType(appliedType, annotation.Span ?? decl.Span, env);
                // M109b-1：cell 场景实参在声明点词法作用域绑定（外层局部/参数）
                WrapperInitSynthesis.BindInitArgsInScope(app, scope, ctx, env, decl);
                local.AppliedWrappers.Add(app);
            }
            WrapperApplicationChecker.CheckTerminalCombination(local.AppliedWrappers,
                decl.Span, env.Error);
        }

        // 解构声明（S7f，SYNTAX §18）：var (a, b) = pair——初始化器类型
        // 必须沿 BaseType 链达到 core.Pair\<TKey, TValue\> 构造；每个名字
        // 绑定为对应分量类型的局部（字段读取由 P4a 脱糖）。core.Pair 是
        // .bootstrap.rg 自举声明，编译器按 canonical 名硬编码参照
        // （同 M48 core.collections 协议先例）
        private static BoundStatement? BindDestructuring(VariableDeclarationASTNode node,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            // Parser 已强制 =；此处为防御性检查
            if (node.Initializer == null)
            {
                env.Error(node.Span, "Destructuring declaration requires an initializer");
                return null;
            }
            var init = ExpressionDispatcher.Visit(node.Initializer.Expression, scope, ctx, env);
            if (init == null) return null;
            if (init.Type is ErrorTypeSymbol) return null;
            var pairDef = FindCorePairDefinition(node.Span, env);
            if (pairDef == null) return null;
            // S9a：泛型参数类型判型后不参与 Pair 查找（自然报非 Pair 诊断）
            TypeSymbol? constructed = null;
            for (var t = init.Type as TypeSymbol; t != null; t = t.BaseType)
            {
                if (ReferenceEquals(t.ConstructedFrom, pairDef))
                {
                    constructed = t;
                    break;
                }
            }
            if (constructed == null)
            {
                env.Error(node.Span,
                    $"Destructuring requires a subtype of core.Pair\\<TKey, TValue\\> " +
                    $"(got '{BoundAnalysis.TypeDisplay(init.Type)}')");
                return null;
            }
            if (node.DestructureNames!.Count != 2)
            {
                env.Error(node.Span,
                    $"Destructuring of core.Pair requires exactly 2 names " +
                    $"(got {node.DestructureNames.Count})");
                return null;
            }
            var entries = new List<(LocalSymbol, FieldSymbol)>();
            var componentNames = new[] { "key", "value" };
            for (int i = 0; i < 2; i++)
            {
                // 分量类型 = 构造实参（S9a 放行：实参可为泛型参数，引用相等身份）
                var componentType = constructed.TypeArguments![i];
                // 推断分量类型使用点检查（§16.1，bug S5 修复2，同局部推断
                // 口径；F1 起递归口径 + 与统一收口驻留去重——init 表达式
                // Pair\<Hidden, ...\> 经收口递归命中已报时本挂点静默）
                UseSiteAccessibility.CheckInferredType(componentType, node.Span, ctx, env);
                var name = node.DestructureNames[i];
                if (scope.DeclaresHere(name))
                {
                    env.Error(node.Span, $"Duplicate local variable '{name}'");
                    return null;
                }
                var field = pairDef.Fields.First(f => f.Name == componentNames[i]);
                var local = new LocalSymbol(name, componentType, node.IsConst);
                scope.Declare(local);
                ctx.Locals.Add(local);
                ctx.Flow.MarkAssigned(local);
                entries.Add((local, field));
            }
            return new BoundDestructuringDeclarationStatement(node, init, entries);
        }

        // core.Pair 定义查找（.bootstrap.rg 自举提供；缺席即诊断——
        // BindUnit 类不带 stdlib 的驱动触不到解构绑定）
        private static TypeSymbol? FindCorePairDefinition(CharRange? span, BindEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            var pair = core?.Types.FirstOrDefault(t => t.Name == "Pair"
                && t.GenericParameters.Count == 2);
            if (pair == null)
            {
                env.Error(span, "P3: core.Pair not found " +
                    "(required by destructuring declaration; stdlib missing)");
            }
            return pair;
        }
    }

    // 表达式语句与赋值（S5；S8c 增补索引写入 place）。自旧
    // BindSession.BindExpressionStatement/BindAssignment 迁移，行为不变。
    internal sealed class ExpressionStatementVisitor
        : BinderVisitor<ExpressionStatementVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var stmt = (ExpressionStatementASTNode)node;
            if (stmt.AssignValue != null)
            {
                return BindAssignment(stmt, scope, ctx, env);
            }
            return BindNonAssignment(stmt, stmt.Expression.Expression, scope, ctx, env);
        }

        // 非赋值表达式语句的语句语境绑定：await 语句形态、void 调用落
        // BoundCallStatement（不报「无结果」）、非 void 调用/其它表达式包
        // BoundExpressionStatement（值被丢弃）。void lambda 单表达式体
        //（SYNTAX §5.1：与把该表达式写成一条语句完全等价）复用本分流。
        public static BoundStatement? BindNonAssignment(ASTNode syntax,
            ExpressionASTNode expression, Scope scope, BindContext ctx, BindEnvironment env)
        {
            if (UnaryVisitor.IsAwait(expression, out var awaitNode))
            {
                var awaitExpression = UnaryVisitor.BindStatementAwait(awaitNode, scope, ctx, env);
                return awaitExpression == null ? null : new BoundExpressionStatement(syntax,
                    awaitExpression);
            }
            // void 调用落成 BoundCallStatement，非 void 调用仍是表达式语句
            if (expression is PathExpressionASTNode path
                && CallForm.TryGet(path, out var calleeSegments, out var callArguments,
                    out var genericArguments, out var containerTypeArguments))
            {
                // 特殊 CallForm（enum-case / 具化构造）先于普通函数调用；
                // 产值被丢弃，与非常规调用表达式语句一致
                var special = PathFacility.TryBindSpecialPathCall(path, calleeSegments,
                    callArguments!, genericArguments, scope, ctx, env, forAssignment: false,
                    out var specialHandled);
                if (specialHandled)
                {
                    return special == null ? null : new BoundExpressionStatement(syntax, special);
                }
                var binding = CallFacility.BindCall(syntax, calleeSegments, callArguments!, scope,
                    ctx, env, genericArguments, containerTypeArguments);
                if (binding == null) return null;
                // M88：inner(...) 语句位置（含 void）；#27⑦ 携带泛型包透传
                if (binding.IsInnerCall)
                {
                    var innerType = binding.ResultType ?? env.B.Any;
                    return new BoundExpressionStatement(syntax,
                        new BoundInnerCallExpression(path, binding.Arguments, innerType,
                            isVoid: binding.IsVoid,
                            forwardedGenericPacks: binding.ForwardedGenericPacks));
                }
                if (binding.IsSuperCall)
                {
                    var superType = binding.ResultType ?? env.B.Any;
                    return new BoundExpressionStatement(syntax,
                        new BoundSuperCallExpression(path, binding.Method, binding.Arguments,
                            superType, binding.TypeArguments, binding.GenericPack,
                            isVoid: binding.IsVoid));
                }
                if (binding.IsVoid)
                {
                    return new BoundCallStatement(syntax, binding.Method, binding.Arguments,
                        binding.Receiver, binding.TypeArguments, binding.GenericPack,
                        binding.IsIndirect, binding.IndirectTarget);
                }
                if (binding.Receiver != null)
                {
                    return new BoundExpressionStatement(syntax,
                        new BoundInstanceCallExpression(path, binding.Receiver,
                            binding.Method, binding.Arguments, binding.ResultType!,
                            binding.TypeArguments, binding.GenericPack));
                }
                return new BoundExpressionStatement(syntax, new BoundCallExpression(path,
                    binding.Method, binding.Arguments, binding.ResultType!,
                    binding.TypeArguments, binding.GenericPack, binding.IsIndirect,
                    binding.IndirectTarget));
            }
            // M105：非 CallForm 尾 Call 分流——`(act)()` / `(getHandler())()` /
            // `handlers[0]()` 等值上的 void 间接调用在语句位置落 BoundCallStatement
            //（invoke.indirect.noret）；不可调回退通用兜底保诊断文本；非 void
            // 产值调用包表达式语句（与 FoldSuffixes 一致，避免重复绑定）
            if (expression is PathExpressionASTNode trailPath
                && PathFacility.TryBindReceiverBeforeTrailingValueCall(trailPath, scope, ctx, env,
                    out var trailReceiver, out var trailCall))
            {
                if (trailReceiver == null) return null;
                if (trailReceiver.Type is not ErrorTypeSymbol)
                {
                    var trailCallable = SymbolLookup.EffectiveMemberType(trailReceiver.Type, env);
                    if (CallFacility.HasCallOperator(trailCallable, env.Unit.Symbols))
                    {
                        var trailBinding = CallFacility.BindIndirectCallOverload(trailCall!,
                            trailReceiver, trailCallable, trailCall!.Arguments!, null, scope, ctx,
                            env);
                        if (trailBinding == null) return null;
                        if (trailBinding.ResultType == null)
                        {
                            return new BoundCallStatement(syntax, trailBinding.Method,
                                trailBinding.Arguments, trailBinding.Receiver,
                                trailBinding.TypeArguments, trailBinding.GenericPack,
                                isIndirect: true, indirectTarget: trailReceiver);
                        }
                        return new BoundExpressionStatement(syntax,
                            new BoundCallExpression(trailCall, trailBinding.Method,
                                trailBinding.Arguments, trailBinding.ResultType!,
                                trailBinding.TypeArguments, trailBinding.GenericPack,
                                isIndirect: true, indirectTarget: trailReceiver));
                    }
                }
                // 不可调：落入下方通用兜底，由 FoldSuffixes 报原诊断
            }
            // §14.5：`obj:W.m()` 含 Colon，CallForm 不认。语句位 void 调用
            // 合法——短暂打开 AllowVoidCall，void 实例调用收口 BoundCallStatement。
            if (expression is PathExpressionASTNode stmtPath)
            {
                ctx.AllowVoidCall = true;
                BoundExpression? bound;
                try
                {
                    bound = PathFacility.BindPath(stmtPath, scope, ctx, env, forAssignment: false);
                }
                finally
                {
                    ctx.AllowVoidCall = false;
                }
                if (bound == null) return null;
                if (bound is BoundInstanceCallExpression inst && inst.Method.ReturnType == null)
                {
                    return new BoundCallStatement(syntax, inst.Method, inst.Arguments,
                        inst.Receiver, inst.TypeArguments, inst.GenericPack);
                }
                return new BoundExpressionStatement(syntax, bound);
            }
            var expr = ExpressionDispatcher.Visit(expression, scope, ctx, env);
            return expr == null ? null : new BoundExpressionStatement(syntax, expr);
        }

        private static BoundStatement? BindAssignment(ExpressionStatementASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            // 赋值目标是定义而非「使用」：符号引用不经 unassigned 检查
            var target = node.Expression.Expression is PathExpressionASTNode targetPath
                ? PathFacility.VisitForAssignment(targetPath, scope, ctx, env)
                : ExpressionDispatcher.Visit(node.Expression.Expression, scope, ctx, env);
            var value = ExpressionDispatcher.Visit(node.AssignValue!.Expression, scope, ctx, env,
                target?.Type as TypeSymbol);
            if (target == null || value == null) return null;
            switch (target)
            {
                case BoundValueReferenceExpression { Symbol: LocalSymbol local }:
                    if (local.IsUsingResource)
                    {
                        env.Error(node.Span,
                            $"Cannot assign to using resource '{local.Name}'; " +
                            "using resource bindings cannot be reassigned");
                        return null;
                    }
                    // 局部访问器写检查（M107，§9.4.1）：仅 get 不可写
                    if ((local.Getter != null || local.Setter != null) && local.Setter == null)
                    {
                        env.Error(node.Span, $"'{local.Name}' has no setter");
                        return null;
                    }
                    if (local.IsConst)
                    {
                        env.Error(node.Span, $"Cannot assign to const '{local.Name}'");
                        return null;
                    }
                    ctx.Flow.MarkAssigned(local);
                    // S8b：var 局部重新赋值 → 收窄失效（含以其为根的字段链）
                    ctx.Flow.ClearRoot(local);
                    break;
                case BoundValueReferenceExpression { Symbol: ParameterSymbol parameter }:
                    // S8b：参数赋值 → 收窄失效（同 var 局部规则）
                    ctx.Flow.ClearRoot(parameter);
                    break;
                case BoundFieldReferenceExpression fieldReference:
                    // 字段写入统一检查（S8e）：带访问器字段查 setter，
                    // 无访问器字段走 const 规则
                    if (!ConstFieldRules.CheckWritable(fieldReference.Field, node.Span, ctx,
                        env))
                    {
                        return null;
                    }
                    break;
                case BoundFieldAccessExpression fieldAccess:
                    if (!ConstFieldRules.CheckWritable(fieldAccess.Field, node.Span, ctx,
                        env))
                    {
                        return null;
                    }
                    break;
                case BoundIndexExpression:
                    // S8c 索引写入：setAtIndex 已在目标绑定时解析（写模式）
                    // ——索引写入不改变量本身：无 const 检查、无
                    // MarkAssigned、无收窄失效
                    break;
                default:
                    env.Error(node.Expression.Span ?? node.Span,
                        "Assignment target must be a variable");
                    return null;
            }
            if (!SymbolLookup.IsAssignable(value.Type, target.Type, env)
                && !BoundAnalysis.IsDowngradeCallResult(value, env))
            {
                env.Error(node.AssignValue.Span ?? node.Span,
                    $"Cannot assign '{BoundAnalysis.TypeDisplay(value.Type)}' to " +
                    $"'{BoundAnalysis.TypeDisplay(target.Type)}'");
            }
            return new BoundAssignmentStatement(node, target, value);
        }
    }

    // return 语句（S5）与 return@标签 值块产出（S7b，SYNTAX §6.1）。
    // 自旧 BindSession.BindReturn 迁移，行为不变。
    internal sealed class ReturnVisitor : BinderVisitor<ReturnVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var ret = (ReturnStatementASTNode)node;
            // return@标签（SYNTAX §6.1）：终止标签对应值块的路径并把值作为该块
            // 产值；沿值块标签栈从内向外查找，未命中再查语句 seq 标签栈
            // （M61：return@语句seq 提前结束该块，必须不携带值）
            if (ret.Label != null)
            {
                var valueBlockEntry = ctx.Labels.FindValueBlock(ret.Label);
                var target = valueBlockEntry?.Block;
                if (target == null)
                {
                    // 语句 seq 目标（M61，SYNTAX §6.1 明文禁止跨循环）：
                    // 隔循环拦截必须保留；隔值块拦截（值块 continuation
                    // 无法表达「跳到外层 seq」）；不携带值（语句 seq 无
                    // 产值消费者）。值块目标跨循环不在此列——由
                    // StructuredExitRouting 展开为 route + break + dispatcher
                    var seqEntry = ctx.Labels.FindSeqLabel(ret.Label);
                    if (seqEntry != null)
                    {
                        if (ctx.Labels.LoopDepth > seqEntry.Value.LoopDepth)
                        {
                            env.Error(ret.Span, $"P3: return@{ret.Label} across a loop " +
                                "boundary not supported yet (S7c)");
                            return null;
                        }
                        if (ctx.Labels.ValueBlockDepth > seqEntry.Value.ValueBlockDepth)
                        {
                            env.Error(ret.Span, $"P3: return@{ret.Label} across a value " +
                                "block boundary not supported yet");
                            return null;
                        }
                        if (ret.Value != null)
                        {
                            env.Error(ret.Value.Span ?? ret.Span,
                                $"return@{ret.Label} cannot carry a value " +
                                "(target is a statement seq)");
                            return null;
                        }
                        return new BoundSeqExitStatement(node, seqEntry.Value.Seq);
                    }
                    env.Error(ret.Span, $"Undefined value block label: '{ret.Label}'");
                    return null;
                }
                // 值块目标跨循环由 StructuredExitRouting 展开（写结果局部
                // + 跨 region 时写 route + break 当前 loop region + 后随
                // dispatcher relay），P3 放行
                if (ret.Value == null)
                {
                    env.Error(ret.Span, $"return@{ret.Label} requires a value");
                    return null;
                }
                // 值表达式按目标值块的外部期望类型做上下文定型
                // （BoundValueBlock.ExpectedType——enum shorthand 等
                // 期望类型驱动推断在 return@ 位置同样可用）
                var labelValue = ExpressionDispatcher.Visit(ret.Value.Expression, scope, ctx, env,
                    target.ExpectedType as TypeSymbol);
                if (labelValue == null) return null;
                return new BoundReturnValueStatement(node, target, labelValue);
            }
            // 裸 return 不得穿透值块（SYNTAX §6.1）：裸 return 语义恒为结束外层
            // 函数，值块（含其内任意嵌套语句块）内的一切裸 return 必然穿透值块
            // 边界，一律编译错误；lambda 体内的裸 return 由解析层另行拦截（§5.1）
            if (ctx.Labels.ValueBlockDepth > 0)
            {
                env.Error(ret.Span, "Bare 'return' cannot cross a value block boundary " +
                    "(a bare return always ends the enclosing function); use 'return@label' " +
                    "to produce a value from the value block instead (SYNTAX §6.1)");
                return null;
            }
            if (ret.Value == null)
            {
                if (ctx.Frame.Method.ReturnType != null)
                {
                    env.Error(ret.Span, $"Function '{ctx.Frame.Method.Name}' must return a value");
                    return null;
                }
                return new BoundReturnStatement(node, null);
            }
            var value = ExpressionDispatcher.Visit(ret.Value.Expression, scope, ctx, env,
                ctx.Frame.Method.ReturnType as TypeSymbol);
            if (value == null) return null;
            if (ctx.Frame.Method.ReturnType == null)
            {
                env.Error(ret.Value.Span ?? ret.Span,
                    $"Void function '{ctx.Frame.Method.Name}' cannot return a value");
                return null;
            }
            // 返回类型为泛型参数时兼容判定归 S9（T? 收窄到 T 的 smart cast
            // 表目前只存 TypeSymbol，null 守卫后 return v: T? 仍走此跳过）；
            // 赋给标注 : T 的局部由声明/赋值路径用 IsAssignable 拦截。
            // S11e：降级调用结果 Any 可返回任意声明类型（P4a cast 物化兜底）
            if (ctx.Frame.Method.ReturnType is TypeSymbol returnType
                && !SymbolLookup.IsAssignable(value.Type, returnType, env)
                && !BoundAnalysis.IsDowngradeCallResult(value, env))
            {
                env.Error(ret.Value.Span ?? ret.Span,
                    $"Cannot return '{BoundAnalysis.TypeDisplay(value.Type)}' from function " +
                    $"returning '{BoundAnalysis.TypeDisplay(returnType)}'");
            }
            return new BoundReturnStatement(node, value);
        }
    }
}
