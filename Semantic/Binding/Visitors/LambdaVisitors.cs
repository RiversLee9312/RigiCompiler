using System;
using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler
{
    // lambda 对象模型绑定（SYNTAX §5.2）：每个 lambda 在绑定期合成隐藏类
    // （..lambda..UUID，与声明位置同命名空间），继承 core::Func/Action/
    // AsyncFunc/AsyncAction 四族之一（有无返回值 × 是否 async），体绑定为
    // 隐藏类 $$call 运算符的函数体。捕获集落定后合成 init（逐捕获字段赋值）
    // ——lambda 值就是普通对象，BIL 无 lambda 特例（new + invoke.indirect）。
    //
    // 捕获规则（§5.2）：除 this 与 lambda 自身参数外一律 Cell 化——
    // var 捕获 → Cell\<T\> 子类、const 捕获 → ReadonlyCell\<T\> 子类
    //（统一 cell 存储：逐变量隐藏子类，CellClassFactory 合成）；this
    // 捕获是普通字段。被捕获的局部/参数符号置 CellStorage 标记（P4a 据此
    // 把外层函数内的存储替换为 cell）并清洗外层流态的收窄事实（被捕获
    // 变量退出 smart cast，§3.5）。已被 wrapper cell 化的变量直接按
    // 引用捕获该 cell（不套第二层 cell）
    //
    // 泛型上下文：外层方法/外层声明类型链的泛型参数以**同一符号对象**
    // 挂进隐藏类 GenericParameters（GenericParameterSymbol 无宿主回指，
    // 引用相等身份共享合法）——构造点经 type 操作数转发外层 typeid，
    // 隐藏类 fn 的 .generic 引用与外层 fn 同符号、同 BIL 拼写。
    internal sealed class LambdaVisitor : ExpressionVisitor<LambdaVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var lambda = (LambdaExpressionASTNode)node;
            var unit = env.Unit;
            // 形参个数硬性上限（SYNTAX §5.1：与 stdlib 预生成元数一致）
            if (lambda.Parameters.Parameters.Count > CallableModel.MaxLambdaParameters)
            {
                env.Error(lambda.Span,
                    $"Lambda expression has at most {CallableModel.MaxLambdaParameters} parameters");
            }

            // ===== 1. 形参与返回类型（省略返回类型 = void lambda）=====
            var parameterSymbols = new List<ParameterSymbol>();
            foreach (var parameter in lambda.Parameters.Parameters)
            {
                var type = TypeReferences.Resolve(parameter.Type, parameter.Span ?? lambda.Span,
                    ctx.Frame, env, ctx);
                if (type == null) type = unit.Symbols.ErrorType;
                if (type is not TypeSymbol && type is not GenericParameterSymbol)
                {
                    env.Error(parameter.Span ?? lambda.Span,
                        $"Lambda parameter '{parameter.Name}' requires a type");
                    type = unit.Symbols.ErrorType;
                }
                parameterSymbols.Add(new ParameterSymbol(parameter.Name, type,
                    isVariadic: parameter.IsVariadic,
                    isNamedVariadic: parameter.IsNamedVariadic));
            }
            SemanticSymbol? returnType = null;
            if (lambda.ReturnType != null)
            {
                returnType = TypeReferences.Resolve(lambda.ReturnType,
                    lambda.ReturnType.Span ?? lambda.Span, ctx.Frame, env, ctx)
                    ?? unit.Symbols.ErrorType;
            }

            // ===== 2. 隐藏类合成（基类四选一；缺失 stdlib 时毒化兜底）=====
            var baseType = CallableModel.ConstructCallableBase(unit, lambda.IsAsync,
                returnType, parameterSymbols.Select(p => p.Type!).ToList());
            if (baseType == null)
            {
                env.Error(lambda.Span,
                    "P3: lambda requires the core::Func/Action family (stdlib not loaded)");
                baseType = unit.Symbols.Bootstrap.Object;
            }
            var hiddenClass = new TypeSymbol(
                env.HiddenName("lambda", lambda, unit.Symbols.StableTypeIdentity(ctx.Frame.Method)),
                TypeKind.Class, ns: ctx.Frame.FileCtx.Namespace, baseType: baseType,
                isShared: lambda.IsAsync)
            {
                Accessibility = Accessibility.Public,
            };
            // 泛型上下文共享：外层方法 + 外层声明类型链的泛型参数（同符号对象，
            // 序即构造点 type 实参序——声明类型链先行、方法随后）
            foreach (var genericParameter in InScopeGenericParameters(ctx))
            {
                hiddenClass.GenericParameters.Add(genericParameter);
            }

            // ===== 3. $$call 运算符（覆写基类 abstract call）=====
            var call = new MethodSymbol("call", MethodKind.Operator, owner: hiddenClass,
                isStatic: false, returnType: returnType, isAsync: lambda.IsAsync)
            {
                Accessibility = Accessibility.Public,
            };
            call.IsOverride = baseType != unit.Symbols.Bootstrap.Object;
            call.HasBody = true;
            foreach (var parameter in parameterSymbols) call.Parameters.Add(parameter);
            hiddenClass.Methods.Add(call);

            // ===== 3.5 lambda 头内部 wrapper 注解（SYNTAX §14.4）=====
            // 语义 = Method wrapper 修饰该 lambda：注解绑定在 lambda 表达式求值
            // 语境（外层函数作用域），实参可引用外层局部；wrapper 应用挂到
            // 隐藏类 $$call 运算符上（与普通实例方法 Method wrapper 同构）。
            CollectLambdaWrapperApplications(lambda, call, scope, ctx, env);
            var (initWrapper, wrapperInitArguments) =
                SynthesizeLambdaInitWrapper(lambda, hiddenClass, call, env);

            // ===== 4. 隔离绑定上下文（宿主 = 隐藏类；词法宿主/泛型解析 = 外层）=====
            var lambdaCtx = new BindContext(call, ctx.Frame.FileCtx, ctx.Frame.DeclaringType,
                isLambda: true, thisSymbol: ctx.This,
                lambdaThisType: ctx.IsLambda
                    ? ctx.LambdaThisType
                    : PathFacility.EffectiveThisType(ctx, env),
                // 成员查找宿主逐层传播（隐藏类不是词法宿主——外层类型的成员
                // 在 lambda 体内词法可见，同外层上下文）
                lookupHost: ctx.Frame.LookupHost,
                banEnclosingTypeParameters: ctx.Frame.BanEnclosingTypeParameters);
            lambdaCtx.Flow.InheritAssignedFrom(ctx.Flow);
            var lambdaScope = new Scope(scope);
            foreach (var outerParameter in ctx.Frame.Method.Parameters)
                lambdaScope.Declare(outerParameter);
            foreach (var parameter in parameterSymbols)
            {
                lambdaScope.Declare(parameter);
                lambdaCtx.LambdaParameters.Add(parameter);
            }

            // ===== 5. 体绑定（有值/void 双轨）=====
            BoundBlock syntheticBlock;
            BoundValueBlock? valueBlock = null;
            if (lambda.Body != null)
            {
                if (returnType != null)
                {
                    var expressionBody = ExpressionDispatcher.Visit(lambda.Body.Expression,
                        lambdaScope, lambdaCtx, env, returnType as TypeSymbol);
                    CheckReturnType(expressionBody, returnType, lambda, env);
                    syntheticBlock = new BoundBlock(lambda, expressionBody == null
                        ? Array.Empty<BoundStatement>()
                        : new BoundStatement[] { new BoundReturnStatement(lambda, expressionBody) });
                }
                else
                {
                    // void lambda 单表达式体（SYNTAX §5.1）：与把该表达式写成一条
                    // 语句完全等价——走语句语境绑定（复用表达式语句分流：void 调用
                    // 落 BoundCallStatement 不报「无结果」，非 void 调用值被丢弃）
                    var statement = ExpressionStatementVisitor.BindNonAssignment(lambda,
                        lambda.Body.Expression, lambdaScope, lambdaCtx, env);
                    syntheticBlock = new BoundBlock(lambda, statement == null
                        ? Array.Empty<BoundStatement>()
                        : new BoundStatement[] { statement });
                }
            }
            else if (lambda.BlockBody != null)
            {
                if (returnType != null)
                {
                    var shell = new ValueBlockShell(
                        new BoundValueBlock(lambda.BlockBody, lambda.Label ?? "_"),
                        "lambda expression", allowImplicitValue: false);
                    // 声明返回类型即值块的外部期望类型（同一机制——return@
                    // 值表达式据其做上下文定型）
                    shell.Block.ExpectedType = returnType as TypeSymbol;
                    ValueBlockVisitor.VisitInto(lambda.BlockBody, lambdaScope, shell, lambdaCtx, env);
                    if (shell.Block.ValueType != null)
                    {
                        CheckReturnType(shell.Block.ValueType, returnType, lambda, env);
                    }
                    else if (!BoundAnalysis.GuaranteesValueReturn(shell.Block.Block))
                    {
                        env.Error(lambda.Span,
                            "All code paths of a lambda expression must explicitly return@ a value");
                    }
                    syntheticBlock = shell.Block.Block;
                    // 值块带回（P4a 按值块协议降级 $$call 体——return@ 目标
                    // 局部映射与 continuation 编织需要 BoundValueBlock 引用）
                    valueBlock = shell.Block;
                }
                else
                {
                    // void lambda 块体：普通语句块（无 return@ 产值要求）
                    syntheticBlock = BlockDispatcher.Visit(lambda.BlockBody, lambdaScope,
                        lambdaCtx, env);
                }
            }
            else
            {
                env.Error(lambda.Span, "Lambda expression requires a body");
                syntheticBlock = new BoundBlock(lambda, Array.Empty<BoundStatement>());
            }

            // ===== 6. async 闸门（§4.5 闸门 2/3/4；void 返回免检）=====
            if (lambda.IsAsync)
            {
                AsyncGates.CheckLambdaSignature(parameterSymbols, returnType, lambda, env);
                AsyncGates.CheckLambdaCaptures(lambdaCtx.CapturedSymbols, lambda, env);
            }

            // ===== 7. 内层 lambda 的捕获是外层 lambda 的传递捕获 =====
            // 内层自身参数不外泄；外层 lambda 自身的参数/局部也不再向外传递
            // （外层 $$call 已有 CellLocal 存储——嵌套构造点取本层 cell 对象；
            // 误传会导致外层 init 在真正外层作用域找不到 cell，P4 报 Cell 族缺失）
            if (ctx.IsLambda)
            {
                foreach (var captured in lambdaCtx.CapturedSymbols)
                {
                    if (captured is ParameterSymbol nestedParameter
                        && lambdaCtx.LambdaParameters.Contains(nestedParameter)) continue;
                    if (captured is ParameterSymbol outerParameter
                        && ctx.LambdaParameters.Contains(outerParameter)) continue;
                    if (captured is LocalSymbol outerLocal
                        && ctx.Locals.Contains(outerLocal)) continue;
                    ctx.CapturedSymbols.Add(captured);
                }
            }

            // ===== 8. 捕获落定：闭包字段 + init + init 体 =====
            // 显式 this、隐式字段/方法访问和嵌套闭包都汇入捕获集；在生成
            // 字段之前拒绝，不能让借用宿主的 wrapper 副本进入闭包对象。
            if (lambdaCtx.LambdaThisType is TypeSymbol { Kind: TypeKind.Wrapper }
                && lambdaCtx.CapturedSymbols.Any(symbol => symbol is ThisSymbol))
            {
                env.Error(lambda.Span, "P3: A lambda cannot capture wrapper 'this'");
                return null;
            }
            var captures = BuildCaptures(lambda, lambdaCtx, ctx, hiddenClass, env);
            var (init, initBody) = SynthesizeInit(lambda, hiddenClass, captures);
            hiddenClass.LambdaClosure = new LambdaClosureInfo(hiddenClass, init, call, captures,
                valueBlock, initWrapper, wrapperInitArguments);

            // 被捕获变量退出 smart cast（§5.2/§3.5）：清洗**外层**流态中这些根的
            // 既有收窄事实（本 lambda 之前的收窄在捕获点即时失效）；之后的收窄
            // 由 FlowState.SetNarrow/ApplyNarrow 按 CellStorage 标记拦截
            foreach (var captured in lambdaCtx.CapturedSymbols)
            {
                ctx.Flow.ClearRoot(captured);
            }

            var callBody = new BoundFunctionBody(call, lambdaCtx.Locals.ToList(), syntheticBlock);
            // 泛型上下文：表达式类型为隐藏类的自构造形态（实参 = 外层泛型参数
            // 同符号对象——BIL type 操作数转发外层 typeid，§7.5 投影）
            var expressionType = hiddenClass.GenericParameters.Count == 0
                ? (TypeSymbol)hiddenClass
                : unit.Symbols.GetConstructedType(hiddenClass,
                    hiddenClass.GenericParameters.ToArray());
            var result = new BoundLambdaExpression(lambda, returnType, lambdaCtx.CapturedSymbols,
                hiddenClass.LambdaClosure!, callBody, initBody, expressionType);
            env.SyntheticLambdas.Add(result);
            return result;
        }

        // 外层方法 + 外层声明类型链的泛型参数（声明类型链先行、方法随后——
        // 与 NameResolver.FindGenericParameter 的查找范围一致；同符号对象共享）
        private static IEnumerable<GenericParameterSymbol> InScopeGenericParameters(
            BindContext ctx)
        {
            return CellClassFactory.InScopeGenericParameters(ctx);
        }

        // lambda 头内部注解登记（SYNTAX §14.4）：只接受 @WrapperTarget(.Method)
        // 的 wrapper；@WrapperTarget 内建注解挂 lambda 上非法；Value/Entity 目标
        // 报「不适用于 lambda」类诊断。wrapper 实参在外层函数作用域绑定
        // （与局部变量 wrapper 的声明点绑定同机制，可引用外层局部）。
        private static void CollectLambdaWrapperApplications(LambdaExpressionASTNode lambda,
            MethodSymbol call, Scope scope, BindContext ctx, BindEnvironment env)
        {
            foreach (var annotation in lambda.Annotations)
            {
                if (ResolveEnvironment.IsWrapperTargetAnnotation(annotation))
                {
                    env.Error(annotation.Span ?? lambda.Span,
                        "@WrapperTarget can only be applied to wrapper declarations");
                    continue;
                }
                if (ResolveEnvironment.NativeAnnotationNameOf(annotation) is { } builtinName)
                {
                    env.Error(annotation.Span ?? lambda.Span,
                        $"@{builtinName} can only be applied to native functions");
                    continue;
                }
                // @EntryPoint 内建注解（§17）只修饰静态方法，lambda 上非法
                if (ResolveEnvironment.IsEntryPointAnnotation(annotation))
                {
                    env.Error(annotation.Span ?? lambda.Span,
                        "@EntryPoint can only be applied to static methods");
                    continue;
                }
                if (ResolveEnvironment.IsTerminalAnnotation(annotation))
                {
                    env.Error(annotation.Span ?? lambda.Span,
                        "@Terminal can only be applied to wrapper declarations");
                    continue;
                }
                if (ResolveEnvironment.IsInternalAnnotation(annotation))
                {
                    env.Error(annotation.Span ?? lambda.Span,
                        "@Internal can only be applied to wrapper declarations");
                    continue;
                }
                var resolved = env.Names.ResolveSymbolPath(annotation.Name.symbol,
                    ctx.Frame.FileCtx, ctx.Frame.DeclaringType, ctx.Frame.Method,
                    allowImports: true, reportErrors: true,
                    span: annotation.Name.Span ?? annotation.Span ?? lambda.Span,
                    allowBareGenericDefinition: true);
                if (resolved is ErrorTypeSymbol) continue;    // 毒化静默
                if (resolved is not TypeSymbol { Kind: TypeKind.Wrapper } wrapperType)
                {
                    env.Error(annotation.Span ?? lambda.Span,
                        $"'{NameResolver.PathText(annotation.Name.symbol)}' is not a wrapper type");
                    continue;
                }
                // wrapper 声明自身的 @WrapperTarget 缺失/非法已在 P2 声明处报过，静默
                if (wrapperType.WrapperTarget is { } targetKind)
                {
                    if (targetKind != WrapperTargetKind.Method)
                    {
                        env.Error(annotation.Span ?? lambda.Span, targetKind == WrapperTargetKind.Entity
                            ? $"Entity wrapper '{wrapperType.Name}' cannot be applied to a lambda (only to type declarations)"
                            : $"Value wrapper '{wrapperType.Name}' cannot be applied to a lambda (only to fields and variables)");
                        continue;
                    }
                    // 与实例方法同规则：非 shared wrapper 不能修饰 shared 宿主
                    // （async lambda 的隐藏类是 shared）
                    if (!wrapperType.IsShared && call.Owner is { IsShared: true })
                    {
                        env.Error(annotation.Span ?? lambda.Span,
                            $"Non-shared wrapper '{wrapperType.Name}' cannot wrap a lambda of shared type '{call.Owner.Name}'");
                        continue;
                    }
                }
                WrapperApplicationChecker.CheckInternalApplication(wrapperType,
                    ctx.Frame.FileCtx.Namespace, annotation.Span ?? lambda.Span, env.Error);
                var app = new WrapperApplication(wrapperType, annotation);
                // 实参在 lambda 表达式求值语境（外层函数作用域）绑定
                WrapperInitSynthesis.BindInitArgsInScope(app, scope, ctx, env, lambda);
                call.AppliedWrappers.Add(app);
                // §14.4：specific `.proxy.call` 形状必须与 lambda $$call 全等
                // （TReturn 吸收实际返回类型；wildcard 不参与本检查）
                if (ProxyMatching.MethodWrapperSpecificShapeMismatch(wrapperType, call,
                        out var mismatchedProxy))
                {
                    env.Error(annotation.Span ?? lambda.Span,
                        $"Specific proxy '{mismatchedProxy!.Name}' on wrapper '{wrapperType.Name}' " +
                        "does not match the shape of lambda call (§14.4)");
                }
            }
            WrapperApplicationChecker.CheckTerminalCombination(call.AppliedWrappers,
                lambda.Span, env.Error);
        }

        // lambda 隐藏类 ..init.wrapper 合成（§14.4）：Method wrapper 应用挂
        // $$call 运算符；体内按声明序 outer→inner 发 new.wrapper.method。
        // 带实参时实参提升为本方法参数（w0..wN，序 = 应用序 × 实参序），
        // 构造点改走 new.wrapped（LambdaRewriter 消费本方法的参数签名）。
        private static (MethodSymbol? InitWrapper, IReadOnlyList<BoundExpression> Args)
            SynthesizeLambdaInitWrapper(LambdaExpressionASTNode lambda,
            TypeSymbol hiddenClass, MethodSymbol call, BindEnvironment env)
        {
            if (call.AppliedWrappers.Count == 0) return (null, Array.Empty<BoundExpression>());

            // 参数 = 逐应用逐 init 实参平铺（应用须已在外层作用域绑定）
            var parameters = new List<ParameterSymbol>();
            var wrapperArgExprs = new List<BoundExpression>();
            var paramIndex = 0;
            foreach (var app in call.AppliedWrappers)
            {
                var bound = app.BoundInitArguments;
                if (bound == null) continue;   // 绑定失败：诊断已报，跳过安装
                for (var i = 0; i < bound.Count; i++)
                {
                    parameters.Add(new ParameterSymbol("w" + paramIndex, bound[i].Type));
                    wrapperArgExprs.Add(bound[i]);
                    paramIndex++;
                }
            }

            var initWrapper = WrapperInitSynthesis.NewInitWrapperMethod(hiddenClass, parameters);
            hiddenClass.Methods.Add(initWrapper);

            // 体：按应用序发 new.wrapper.method，实参 = 对应参数切片
            var statements = new List<BoundStatement>();
            var cursor = 0;
            foreach (var app in call.AppliedWrappers)
            {
                if (app.BoundInitArguments == null) continue;
                var args = new List<BoundExpression>();
                for (var i = 0; i < app.BoundInitArguments.Count; i++)
                {
                    var p = parameters[cursor++];
                    args.Add(new BoundValueReferenceExpression((ASTNode?)app.Syntax ?? lambda, p, p.Type!));
                }
                statements.Add(new BoundNewWrapperStatement((ASTNode?)app.Syntax ?? lambda,
                    BoundNewWrapperKind.Method, app.Wrapper, call, args));
            }
            env.SyntheticCellBodies.Add(new BoundFunctionBody(initWrapper,
                Array.Empty<LocalSymbol>(), new BoundBlock(lambda, statements)));
            return (initWrapper, wrapperArgExprs);
        }

        // 捕获集 → 闭包字段序列（排序确定：this 先行，其余按符号名 Ordinal 序）：
        // this → 普通字段（外层有效 this 类型）；其余符号经 CellClassFactory
        // 统一 cell 化（幂等——已 cell 化的符号直接复用既有 cell 存储，不套
        // 第二层 cell），捕获字段类型 = 该符号的 cell 隐藏子类。同时回写符号
        // CellStorage 标记并清洗外层收窄事实
        private static IReadOnlyList<LambdaCaptureEntry> BuildCaptures(
            LambdaExpressionASTNode lambda, BindContext lambdaCtx, BindContext outerCtx,
            TypeSymbol hiddenClass, BindEnvironment env)
        {
            var ordered = lambdaCtx.CapturedSymbols
                .OrderBy(symbol => symbol is ThisSymbol ? 0 : 1)
                .ThenBy(symbol => symbol.Name, StringComparer.Ordinal);
            var captures = new List<LambdaCaptureEntry>();
            foreach (var symbol in ordered)
            {
                switch (symbol)
                {
                    case ThisSymbol:
                        {
                            var field = new FieldSymbol(".capture.this", owner: hiddenClass,
                                fieldType: lambdaCtx.LambdaThisType ?? env.Unit.Symbols.ErrorType);
                            hiddenClass.Fields.Add(field);
                            captures.Add(new LambdaCaptureEntry(symbol, field,
                                isThis: true, isReadOnly: true));
                            break;
                        }
                    case LocalSymbol local:
                        {
                            // cell 化语义归外层函数上下文（隐藏子类与外层同
                            // 命名空间、共享外层泛型上下文）
                            var storage = CellClassFactory.EnsureCellStorage(local, lambda,
                                outerCtx, env);
                            var field = new FieldSymbol(".capture." + local.Name,
                                owner: hiddenClass,
                                fieldType: storage?.CellType ?? env.Unit.Symbols.ErrorType);
                            hiddenClass.Fields.Add(field);
                            captures.Add(new LambdaCaptureEntry(symbol, field,
                                isThis: false, isReadOnly: storage?.IsReadOnly ?? local.IsConst));
                            break;
                        }
                    case ParameterSymbol parameter:
                        {
                            // 参数无 const 概念（可写），恒 Cell 风味；lambda
                            // 自身参数已在传递捕获时排除，不会到达此处
                            var storage = CellClassFactory.EnsureCellStorage(parameter, lambda,
                                outerCtx, env);
                            var field = new FieldSymbol(".capture." + parameter.Name,
                                owner: hiddenClass,
                                fieldType: storage?.CellType ?? env.Unit.Symbols.ErrorType);
                            hiddenClass.Fields.Add(field);
                            captures.Add(new LambdaCaptureEntry(symbol, field,
                                isThis: false, isReadOnly: false));
                            break;
                        }
                    default:
                        break;
                }
            }
            return captures;
        }

        // init 合成：参数 = 逐捕获字段（c0..cN，序即 Captures 序）；体 = 逐字段赋值
        private static (MethodSymbol Init, BoundFunctionBody Body) SynthesizeInit(
            LambdaExpressionASTNode lambda, TypeSymbol hiddenClass,
            IReadOnlyList<LambdaCaptureEntry> captures)
        {
            var init = new MethodSymbol("init", MethodKind.Init, owner: hiddenClass,
                isStatic: false, returnType: null)
            {
                Accessibility = Accessibility.Public,
                HasBody = true,
            };
            var statements = new List<BoundStatement>();
            for (var i = 0; i < captures.Count; i++)
            {
                var entry = captures[i];
                var parameter = new ParameterSymbol("c" + i, entry.Field.FieldType);
                init.Parameters.Add(parameter);
                statements.Add(new BoundAssignmentStatement(lambda,
                    new BoundFieldAccessExpression(lambda,
                        new BoundThisExpression(lambda, hiddenClass), entry.Field,
                        entry.Field.FieldType!),
                    new BoundValueReferenceExpression(lambda, parameter, entry.Field.FieldType!)));
            }
            hiddenClass.Methods.Add(init);
            return (init, new BoundFunctionBody(init, Array.Empty<LocalSymbol>(),
                new BoundBlock(lambda, statements)));
        }

        private static void CheckReturnType(BoundExpression? expression, SemanticSymbol returnType,
            LambdaExpressionASTNode lambda, BindEnvironment env)
        {
            if (expression != null) CheckReturnType(expression.Type, returnType, lambda, env);
        }

        private static void CheckReturnType(SemanticSymbol? actual, SemanticSymbol expected,
            LambdaExpressionASTNode lambda, BindEnvironment env)
        {
            if (actual == null || actual is ErrorTypeSymbol || expected is ErrorTypeSymbol) return;
            if (expected is TypeSymbol expectedType
                && !SymbolLookup.IsAssignable(actual, expectedType, env))
            {
                env.Error(lambda.ReturnType!.Span ?? lambda.Span,
                    $"Lambda result '{BoundAnalysis.TypeDisplay(actual)}' is not assignable to " +
                    $"'{BoundAnalysis.TypeDisplay(expected)}'");
            }
        }
    }
}
