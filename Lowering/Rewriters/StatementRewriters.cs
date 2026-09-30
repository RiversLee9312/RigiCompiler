namespace RigiCompiler
{
    // 语句降级（S5–S7e 恒等降级为主）。自旧 LowerSession.LowerStatement
    // 各分支迁移，行为不变。

    // 局部声明：初始化表达式降级 + cast 物化（BIL §6.5）；
    // cell 化局部（统一 cell 存储，SYNTAX §5.2/§14.3）：存储是 cell——
    // 声明处构造隐藏子类实例。可变 Value wrapper 局部带初始化器时
    // 两步：空构造（装 wrapper）+ setValue（经 proxy.set，§9.4.1）；
    // const / 无 wrapper 仍直接 init(value)；无初始化器 = init() 空构造
    internal sealed class LocalDeclarationRewriter
        : LoweredVisitor<LocalDeclarationRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var decl = (BoundLocalDeclarationStatement)node;
            LoweredExpression? initializer = null;
            if (decl.Initializer != null)
            {
                initializer = LowerExpressionDispatcher.Visit(decl.Initializer, ctx, env);
                if (initializer == null) return null;
                // 初始化 cast 物化（BIL §6.5）：值类型 ≠ 局部声明类型
                initializer = LoweringFacility.EnsureDeclaredType(decl, initializer,
                    decl.Local.Type);
            }
            if (decl.Local.CellStorage is { } storage)
            {
                // 可变 wrapper cell 带初始化器：先 DefaultInit 空构造（保留
                // new.wrapped 前缀，wrapper 装到 cell 上），再 setValue 走
                // proxy.set。无 .proxy.set 的纯状态修饰器 / const 保持
                // ValueInit——VM set 链要求每环都有 .proxy.set。
                var viaSet = initializer != null
                    && !storage.IsReadOnly
                    && storage.DefaultInit != null
                    && HasSetProxyChain(storage);
                LoweredStatement? writeAfter = null;
                if (viaSet)
                {
                    writeAfter = ctx.Closure.TryRewriteCellWrite(decl,
                        new BoundValueReferenceExpression(decl.Syntax, decl.Local,
                            decl.Local.Type!), initializer!);
                    if (writeAfter == null) viaSet = false;
                }
                // 无初始化器的 const 局部不存在（P3：const 必须初始化）——
                // ReadonlyCell 风味无空构造（DefaultInit 恒 null）
                var init = viaSet
                    ? storage.DefaultInit!
                    : initializer != null ? storage.ValueInit
                    : storage.DefaultInit ?? throw new CompilerInternalException(
                        "ReadonlyCell 局部缺初始化器: " + decl.Local.Name);
                // M107：局部访问器自由变量捕获实参接在值参之后（同 lambda init）
                var args = new List<LoweredExpression>();
                if (initializer != null && !viaSet) args.Add(initializer);
                foreach (var capture in storage.AccessorCaptures)
                {
                    if (capture.IsThis)
                    {
                        args.Add(ctx.Closure.ThisValueFor(decl,
                            capture.Field.FieldType as TypeSymbol
                            ?? env.Unit.Symbols.ErrorType));
                    }
                    else
                    {
                        args.Add(ctx.Closure.CellObjectFor(decl, capture.Symbol));
                    }
                }
                // M109b-1：有参 ..init.wrapper → new.wrapped 前缀实参
                var wrapperArgs = CellWrappedNew.LowerWrapperInitArgs(decl, storage, ctx, env);
                initializer = new LoweredNewExpression(decl, init, args, storage.CellType,
                    wrapperArgs);
                var declaration = new LoweredLocalDeclarationStatement(decl, decl.Local,
                    initializer);
                if (writeAfter == null) return declaration;
                return new LoweredBlock(decl, new List<LoweredStatement>
                {
                    declaration, writeAfter
                });
            }
            return new LoweredLocalDeclarationStatement(decl, decl.Local, initializer);
        }

        // 值字段上每一环 Value wrapper 都实现了 .proxy.set 才走 set 链；
        // 纯状态修饰器（无 proxy）或残缺应用不得 setValue
        private static bool HasSetProxyChain(CellStorageInfo storage)
        {
            var wrappers = storage.ValueField.AppliedWrappers;
            if (wrappers.Count == 0) return false;
            foreach (var app in wrappers)
            {
                var found = false;
                foreach (var method in app.WrapperDefinition.Methods)
                {
                    if (method.Name == ".proxy.set") { found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }
    }

    // 表达式语句：递归降级
    internal sealed class ExpressionStatementRewriter
        : LoweredVisitor<ExpressionStatementRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var stmt = (BoundExpressionStatement)node;
            var expression = LowerExpressionDispatcher.Visit(stmt.Expression, ctx, env);
            if (expression == null) return null;
            return new LoweredExpressionStatement(stmt, expression);
        }
    }

    // void 调用语句：receiver 降级 + 实参降级 + 调用点 cast 物化
    // （S7c-2，BIL §6.5）。兄弟求值序保护（EvalOrderGuard）：降级顺序
    // 为 receiver 先、实参后（与 P4b CallStatementEmitter 发射序一致）；
    // 实参产前置语句时 receiver 物化合成局部插回前置流首位
    internal sealed class CallStatementRewriter
        : LoweredVisitor<CallStatementRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var call = (BoundCallStatement)node;
            // S1/g9：值类型 receiver 的可写 place 链（§10）——receiver
            // 拷贝物化，调用语句之后把 this 修改反向写回 place（块返回）
            // （chainfix：接管判定携带 ctx，环可写回性与写回构造同一
            // setter 使用点可见性口径）
            if (call.Receiver is not null and not BoundWrapperAccessExpression
                && !call.IsIndirect
                && WrapperPlaceLowering.TryValueReceiverCallTarget(call.Receiver,
                    call.Method.Owner, ctx, out var valueRoot, out var valueChain))
            {
                return RewriteValueReceiverCallStatement(call, valueRoot, valueChain, ctx, env);
            }
            var guard = new EvalOrderGuard(ctx);
            LoweredExpression? callReceiver = null;
            if (call.Receiver != null)
            {
                // S11c/M84：wrapper place 作 receiver——get.wrapper /
                // get.wrapper.field 值拷贝物化（与 InstanceCallRewriter 同路径）
                if (call.Receiver is BoundWrapperAccessExpression place)
                {
                    callReceiver = WrapperPlaceLowering.Materialize(place, null, ctx, env);
                }
                else
                {
                    callReceiver = LowerExpressionDispatcher.Visit(call.Receiver, ctx, env);
                    if (callReceiver != null)
                    {
                        callReceiver = LoweringFacility.EnsureReceiverType(call, callReceiver,
                            call.Method.Owner);
                    }
                }
                if (callReceiver == null) return null;
                guard.Track(call.Receiver, callReceiver);
            }
            var arguments = LoweringFacility.LowerArguments(call.Arguments, call.Method.Parameters,
                ctx, env, guard, call.Receiver?.Type as TypeSymbol, call.Method, call.TypeArguments);
            if (arguments == null) return null;
            // S9d-2：泛型包无值子节点，恒等透传（打包归 P4b）
            var genericPack = call.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(call.GenericPack, call.GenericPack.IsNamed,
                    call.GenericPack.TypeArguments, call.GenericPack.NamedTypes);
            // §15.3 间接调用：目标对象表达式降级透传
            LoweredExpression? indirectTarget = null;
            if (call.IsIndirect)
            {
                indirectTarget = guard.Lower(call.IndirectTarget!, env);
                if (indirectTarget == null) return null;
            }
            var sealedSlots = guard.Seal();
            var slotIndex = 0;
            if (call.Receiver != null) callReceiver = sealedSlots[slotIndex++];
            var sealedArguments = new List<LoweredExpression>(call.Arguments.Count);
            for (var i = 0; i < call.Arguments.Count; i++)
                sealedArguments.Add(sealedSlots[slotIndex++]);
            if (call.IsIndirect) indirectTarget = sealedSlots[slotIndex];
            return new LoweredCallStatement(call, call.Method, sealedArguments, callReceiver,
                call.TypeArguments, genericPack, indirectTarget);
        }

        // 值类型 receiver 可写 place 链的 void 调用语句（S1/g9，§10）：
        // 正向 get 链物化 receiver 拷贝（根单次求值共享）→ 调用语句 →
        // this 修改经值类型中间逐层反向 set 写回 place；返回块 =
        // [调用, 写回...]（正向 get 前置已入 Output，guard 只管实参段）
        private static LoweredStatement? RewriteValueReceiverCallStatement(
            BoundCallStatement call, BoundExpression valueRoot,
            List<BoundFieldAccessExpression> valueChain, LowerContext ctx,
            LowerEnvironment env)
        {
            var receiver = WrapperPlaceLowering.MaterializeValueReceiver(call, valueRoot,
                valueChain, ctx, env, out var intermediates, out var host);
            if (receiver == null) return null;
            var guard = new EvalOrderGuard(ctx);
            guard.Track(call.Receiver!, receiver);
            var arguments = LoweringFacility.LowerArguments(call.Arguments,
                call.Method.Parameters, ctx, env, guard, call.Receiver?.Type as TypeSymbol,
                call.Method, call.TypeArguments);
            if (arguments == null) return null;
            var genericPack = call.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(call.GenericPack, call.GenericPack.IsNamed,
                    call.GenericPack.TypeArguments, call.GenericPack.NamedTypes);
            var sealedSlots = guard.Seal();
            var sealedArguments = new List<LoweredExpression>(call.Arguments.Count);
            for (var i = 1; i < sealedSlots.Count; i++) sealedArguments.Add(sealedSlots[i]);
            var callStatement = new LoweredCallStatement(call, call.Method, sealedArguments,
                sealedSlots[0], call.TypeArguments, genericPack, null);
            var writebacks = WrapperPlaceLowering.BuildValueReceiverWritebacks(call,
                intermediates, host!, valueRoot, ctx, env);
            if (writebacks == null) return null;
            if (writebacks.Count == 0) return callStatement;
            var statements = new List<LoweredStatement> { callStatement };
            statements.AddRange(writebacks);
            return new LoweredBlock(call, statements);
        }
    }

    // 赋值：目标与值递归降级 + 值 cast 物化（BIL §6.5）。
    // variadic 参数索引写入（BIL §7.1）：place 剥壳（读形态产物外包的
    // 拆箱 cast）后命中 variadic 索引时，声明类型 = 容器 ABI 元素类型
    // （.any / Pair\<String, Any\>）——元素装箱与 .vargs./.kwargs.
    // 隐藏条目声明对齐
    internal sealed class AssignmentRewriter
        : LoweredVisitor<AssignmentRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var assignment = (BoundAssignmentStatement)node;
            // S11c/M84：wrapper place 直接字段写入 = set.wrapper.field 链
            //（§13.3；求值序不变——BuildWrapperFieldPlace 内宿主先行降级）
            if (WrapperPlaceLowering.DirectPlaceFieldTarget(assignment.Target)
                is { } placeAccess)
            {
                var writePlace = WrapperPlaceLowering.BuildWrapperFieldPlace(placeAccess,
                    (BoundWrapperAccessExpression)placeAccess.Receiver, placeAccess.Field,
                    null, ctx, env);
                if (writePlace == null) return null;
                var placeValue = LowerExpressionDispatcher.Visit(assignment.Value, ctx, env);
                if (placeValue == null) return null;
                placeValue = LoweringFacility.EnsureDeclaredType(assignment, placeValue,
                    placeAccess.Type);
                return new LoweredAssignmentStatement(assignment, writePlace, placeValue);
            }
            // M84：深层纯字段写穿 place.a.b... = rhs → 正向 get + 叶写 + 反向 set
            if (WrapperPlaceLowering.TryDeepFieldWriteTarget(assignment.Target,
                    out var deepPlace, out var deepChain))
            {
                return WrapperPlaceLowering.LowerDeepFieldWrite(assignment, deepPlace, deepChain,
                    assignment.Value, ctx, env);
            }
            // M111：索引写 place[i] / place.a.b[i] = rhs
            if (WrapperPlaceLowering.TryIndexWriteTarget(assignment.Target,
                    out var indexPlace, out var indexFields, out var indexExpr))
            {
                return WrapperPlaceLowering.LowerIndexWrite(assignment, indexPlace, indexFields,
                    indexExpr, assignment.Value, ctx, env);
            }
            // S1/g9：普通值类型中间链写穿 host.a.b... = rhs（§13.2）——
            // 正向 get 物化中间值 + 叶写 + 值类型中间反向 set 写回
            //（根含局部/参数/this/静态·全局字段）
            if (WrapperPlaceLowering.TryValueChainWriteTarget(assignment.Target,
                    out var valueRoot, out var valueChain))
            {
                return WrapperPlaceLowering.LowerValueChainFieldWrite(assignment, valueRoot,
                    valueChain, assignment.Value, ctx, env);
            }
            if (WrapperPlaceLowering.ContainsPlaceInTarget(assignment.Target))
            {
                WrapperPlaceLowering.UnsupportedWrite(assignment.Target, env);
                return null;
            }
            // 闭包存储计划（SYNTAX §5.2）：被捕获值引用的写入 = cell setValue
            // 调用（读写在 plan 一处收口；求值序 = 右值先行物化）
            if (ctx.Closure.IsCapturedReference(assignment.Target))
            {
                var cellValue = LowerExpressionDispatcher.Visit(assignment.Value, ctx, env);
                if (cellValue == null) return null;
                cellValue = LoweringFacility.EnsureDeclaredType(assignment, cellValue,
                    assignment.Target.Type);
                // 判定已命中，改写恒成功（null 仅限内部错误——this/const 写入）
                return ctx.Closure.TryRewriteAssignment(assignment, cellValue);
            }
            // 静态/全局 cell 化字段的写入 = cell setValue 调用（统一 cell
            // 存储，SYNTAX §14.3；求值序与上同——右值先行物化）
            if (assignment.Target is BoundFieldReferenceExpression
                { Field.CellStorage: not null })
            {
                var staticCellValue = LowerExpressionDispatcher.Visit(assignment.Value, ctx, env);
                if (staticCellValue == null) return null;
                staticCellValue = LoweringFacility.EnsureDeclaredType(assignment,
                    staticCellValue, assignment.Target.Type);
                return CellStorageLowering.TryRewriteStaticWrite(assignment, assignment.Target,
                    staticCellValue, env);
            }
            var target = LowerExpressionDispatcher.Visit(assignment.Target, ctx, env);
            var value = LowerExpressionDispatcher.Visit(assignment.Value, ctx, env);
            if (target == null || value == null) return null;
            var declaredType = (SemanticSymbol?)LoweringFacility.VariadicIndexAbiTypeOfPlace(
                target, env) ?? assignment.Target.Type;
            value = target is LoweredIndexExpression
                { Receiver: LoweredValueReferenceExpression { Symbol: ParameterSymbol { IsNamedVariadic: true } } }
                ? LoweringFacility.AdaptNamedArgumentPair(assignment, value, declaredType, env)
                : LoweringFacility.EnsureDeclaredType(assignment, value, declaredType);
            return new LoweredAssignmentStatement(assignment, target, value);
        }
    }

    // return：值递归降级 + cast 物化（声明返回类型，BIL §6.5）
    internal sealed class ReturnRewriter
        : LoweredVisitor<ReturnRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var ret = (BoundReturnStatement)node;
            LoweredExpression? returnValue = null;
            if (ret.Value != null)
            {
                returnValue = LowerExpressionDispatcher.Visit(ret.Value, ctx, env);
                if (returnValue == null) return null;
                returnValue = LoweringFacility.EnsureDeclaredType(ret, returnValue,
                    ctx.Method.ReturnType);
            }
            return new LoweredReturnStatement(ret, returnValue);
        }
    }

    // throw 降级（S7d，BIL §16.9）。#28④：降级调用结果的静态类型为
    // Any，仅该形态须先 cast 到 core.Exception，保证 BilVerifier §21.3 合法
    internal sealed class ThrowRewriter
        : LoweredVisitor<ThrowRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var throwStatement = (BoundThrowStatement)node;
            var thrown = LowerExpressionDispatcher.Visit(throwStatement.Exception, ctx, env);
            if (thrown == null) return null;
            if (throwStatement.Exception is BoundInstanceCallExpression { Method: { } method }
                && ReferenceEquals(method, env.Unit.Symbols.Bootstrap.CallWildcard))
            {
                thrown = LoweringFacility.EnsureDeclaredType(throwStatement, thrown,
                    env.Unit.Symbols.Bootstrap.Exception);
            }
            return new LoweredThrowStatement(throwStatement, thrown);
        }
    }

    // seq 语句降级（S7e，BIL §3.4 独立 block + call 化；Stage B：
    // continuation 编织闸门删除——return@语句seq 降级为
    // LoweredStructuredExit 标记，展开归 StructuredExitRouting pass）。
    // named seq 注册目标映射表（breakId 创建后、降级体前）；降级循环
    // 遇命中本层的 BoundSeqExitStatement 截断后续语句（静死——routing
    // 展开标记时同样截断，此处提前收口不降级死代码）。手动压栈收集
    // （输出栈元素即可变 List，仿 SwitchRewriters 先例）
    internal sealed class SeqStatementRewriter
        : LoweredVisitor<SeqStatementRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var seqStatement = (BoundSeqStatement)node;
            var seqBreakId = ctx.Synth.NewBreakIdLocal();
            // named seq 可作 return@ 目标（SYNTAX §6.1，M61）：注册目标
            // 映射表（无结果局部）；无名 seq 无人引用，不注册
            if (seqStatement.Label != null)
            {
                ctx.ExitTargets.Register(seqStatement, seqBreakId);
            }
            var statements = new List<LoweredStatement>();
            ctx.Output.Push(statements);
            try
            {
                foreach (var statement in seqStatement.Body.Statements)
                {
                    var lowered = LowerStatementDispatcher.Visit(statement, ctx, env);
                    if (lowered == null) return null;
                    statements.Add(lowered);
                    // return@本层 seq 终止本路径：其后语句不可达（静死），
                    // 截断不降级；目标为外层 seq 时不截断（routing 展开）
                    if (statement is BoundSeqExitStatement exit
                        && ReferenceEquals(exit.Target, seqStatement))
                    {
                        break;
                    }
                }
                // 初始化器按源码顺序降级；每个声明置于自己的 finally 之外，
                // 因而初始化失败不会释放尚未成功建立的资源。
                var declarations = new List<List<LoweredStatement>>();
                foreach (var binding in seqStatement.UsingBindings)
                {
                    var declarationStatements = new List<LoweredStatement>();
                    ctx.Output.Push(declarationStatements);
                    try
                    {
                        var declaration = LowerStatementDispatcher.Visit(
                            new BoundLocalDeclarationStatement(binding.Syntax, binding.Local,
                                binding.Initializer), ctx, env);
                        if (declaration == null) return null;
                        declarationStatements.Add(declaration);
                    }
                    finally
                    {
                        ctx.Output.Pop();
                    }
                    declarations.Add(declarationStatements);
                }
                var protectedBody = new LoweredBlock(seqStatement.Body, statements);
                for (var i = seqStatement.UsingBindings.Count - 1; i >= 0; i--)
                {
                    var binding = seqStatement.UsingBindings[i];
                    var finallyStatements = new List<LoweredStatement>();
                    ctx.Output.Push(finallyStatements);
                    try
                    {
                        var dispose = LowerStatementDispatcher.Visit(binding.DisposeCall, ctx, env);
                        if (dispose == null) return null;
                        finallyStatements.Add(dispose);
                    }
                    finally
                    {
                        ctx.Output.Pop();
                    }
                    var tryBlock = new LoweredBlock(binding,
                        new List<LoweredStatement> { protectedBody });
                    protectedBody = new LoweredBlock(binding,
                        declarations[i].Concat(new LoweredStatement[] {
                            new LoweredTryStatement(seqStatement, tryBlock,
                                Array.Empty<LoweredTryCatch>(),
                                 new LoweredBlock(binding, finallyStatements),
                                 ctx.Synth.NewSynthLocal(env.Unit.Symbols.GetNullable(
                                     env.Unit.Symbols.Bootstrap.Exception)),
                                 ctx.Synth.NewBreakIdLocal())
                        }).ToList());
                }
                return new LoweredSeqBlock(seqStatement, protectedBody, seqStatement.IsVolatile,
                    seqBreakId);
            }
            finally
            {
                ctx.Output.Pop();
            }
        }
    }

    // return@语句seq 降级（M61，SYNTAX §6.1；Stage B）：产
    // LoweredStructuredExit 标记（无值），展开归 StructuredExitRouting pass
    internal sealed class SeqExitRewriter
        : LoweredVisitor<SeqExitRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var exit = (BoundSeqExitStatement)node;
            return new LoweredStructuredExit(exit, exit.Target, null);
        }
    }

    // return@值块降级（Stage B）：产值先行降级（前置语句落当前流——
    // 保证值在 cleanup 前已求值），产 LoweredStructuredExit 标记
    // （结果局部写入与 region 跳出由 StructuredExitRouting 展开；
    // 目标解析归 StructuredExitTargetTable）
    internal sealed class ReturnValueRewriter
        : LoweredVisitor<ReturnValueRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var returnValue = (BoundReturnValueStatement)node;
            var value = LowerExpressionDispatcher.Visit(returnValue.Value, ctx, env);
            if (value == null) return null;
            return new LoweredStructuredExit(returnValue, returnValue.Target, value);
        }
    }

    // break/continue 是 BIL 真跳转（S7c-1，§16.5），直接携带目标循环的
    // breakid——穿透值块/嵌套块无需任何展开；其后语句在 BIL 块内自然
    // 不可达（无需 routing 介入）。Stage B 起按 IsBreak 分产两节点
    internal sealed class LoopControlRewriter
        : LoweredVisitor<LoopControlRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var loopControl = (BoundLoopControl)node;
            var breakId = LoopFacility.FindBreakId(loopControl.Target, ctx);
            return loopControl.IsBreak
                ? (LoweredStatement)new LoweredBreakStatement(loopControl, breakId)
                : new LoweredContinueStatement(loopControl, breakId);
        }
    }

    // M109b-1：cell 构造点 wrapper 前缀实参降级。无参 → null（普通 new）；
    // 有参 → 非 null 列表（new.wrapped）。共享于 LocalDeclaration/Loop/TrySeq/
    // ClosureStoragePlan 四构造点。
    // guard：兄弟求值序保护（EvalOrderGuard）——未传时自建并在返回前
    // Seal（语义同 LoweringFacility.LowerArguments）；传入外部 guard 时
    // 只 Track 不 Seal（外层统一 Seal），此时返回值是未封口形态，外层
    // 调用方须改用 Seal 结果
    internal static class CellWrappedNew
    {
        public static IReadOnlyList<LoweredExpression>? LowerWrapperInitArgs(
            BoundNode origin, CellStorageInfo storage, LowerContext ctx, LowerEnvironment env,
            EvalOrderGuard? guard = null)
        {
            if (storage.InitWrapper == null || storage.InitWrapper.Parameters.Count == 0)
                return null;
            var own = guard == null;
            guard ??= new EvalOrderGuard(ctx);
            var result = new List<LoweredExpression>();
            foreach (var bound in storage.WrapperInitArguments)
            {
                var lowered = LowerExpressionDispatcher.Visit(bound, ctx, env);
                if (lowered == null) return null;
                guard.Track(bound, lowered);
                result.Add(lowered);
            }
            if (own) return new List<LoweredExpression>(guard.Seal());
            return result;
        }
    }

    // §14.5 new.wrapper.* 恒等降级
    internal sealed class NewWrapperRewriter
        : LoweredVisitor<NewWrapperRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var stmt = (BoundNewWrapperStatement)node;
            // 兄弟求值序保护（EvalOrderGuard）：实参逐一登记后统一 Seal
            var guard = new EvalOrderGuard(ctx);
            foreach (var argument in stmt.Arguments)
            {
                if (guard.Lower(argument, env) == null) return null;
            }
            var sealedSlots = guard.Seal();
            var args = new List<LoweredExpression>(stmt.Arguments.Count);
            for (var i = 0; i < sealedSlots.Count; i++) args.Add(sealedSlots[i]);
            return new LoweredNewWrapperStatement(stmt, stmt.Kind, stmt.WrapperType, stmt.Target,
                args);
        }
    }
}
