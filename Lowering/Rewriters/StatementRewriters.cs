namespace RigiCompiler
{
    // 语句降级（S5–S7e 恒等降级为主）。自旧 LowerSession.LowerStatement
    // 各分支迁移，行为不变。

    // 局部声明：初始化表达式降级 + cast 物化（BIL §6.5）；
    // cell 化局部（统一 cell 存储，SYNTAX §5.2/§14.3）：存储是 cell——
    // 声明处构造隐藏子类实例（有初始化器 = init(value)，无初始化器 =
    // init() 空构造）
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
                // 无初始化器的 const 局部不存在（P3：const 必须初始化）——
                // ReadonlyCell 风味无空构造（DefaultInit 恒 null）
                var init = initializer != null ? storage.ValueInit
                    : storage.DefaultInit ?? throw new CompilerInternalException(
                        "ReadonlyCell 局部缺初始化器: " + decl.Local.Name);
                // M107：局部访问器自由变量捕获实参接在值参之后（同 lambda init）
                var args = new List<LoweredExpression>();
                if (initializer != null) args.Add(initializer);
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
            }
            return new LoweredLocalDeclarationStatement(decl, decl.Local, initializer);
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

    // void 调用语句：实参降级 + 实例 receiver 降级与调用点 cast 物化
    // （S7c-2，BIL §6.5）
    internal sealed class CallStatementRewriter
        : LoweredVisitor<CallStatementRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var call = (BoundCallStatement)node;
            var arguments = LoweringFacility.LowerArguments(call.Arguments, call.Method.Parameters,
                ctx, env);
            if (arguments == null) return null;
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
                        callReceiver = LoweringFacility.EnsureDeclaredType(call, callReceiver,
                            call.Method.Owner);
                    }
                }
                if (callReceiver == null) return null;
            }
            // S9d-2：泛型包无值子节点，恒等透传（打包归 P4b）
            var genericPack = call.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(call.GenericPack, call.GenericPack.IsNamed,
                    call.GenericPack.TypeArguments, call.GenericPack.NamedTypes);
            // §15.3 间接调用：目标对象表达式降级透传
            LoweredExpression? indirectTarget = null;
            if (call.IsIndirect)
            {
                indirectTarget = LowerExpressionDispatcher.Visit(call.IndirectTarget!, ctx, env);
                if (indirectTarget == null) return null;
            }
            return new LoweredCallStatement(call, call.Method, arguments, callReceiver,
                call.TypeArguments, genericPack, indirectTarget);
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
            value = LoweringFacility.EnsureDeclaredType(assignment, value, declaredType);
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

    // seq 语句降级（S7e，BIL §3.4 独立 block + call 化）；M61 起 named
    // seq 降级体期间压 seq 目标栈（return@语句seq 的归属比对），
    // 体含 exit 标记时跑 continuation 编织（消费命中本层者、截断传播
    // 外层者；无 exit 直通——零行为变化）。手动压栈收集（输出栈元素即
    // 可变 List——编织就地变换需要，仿 SwitchRewriters 先例）
    internal sealed class SeqStatementRewriter
        : LoweredVisitor<SeqStatementRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var seqStatement = (BoundSeqStatement)node;
            // 所有 seq 降级都压栈（含无名）——exit 归属比对按引用命中
            // 「本层」；无名 seq 不压栈会让栈顶指向外层 seq，导致外层
            // 目标被内层误消费（其后语句漏截断）
            ctx.Targets.PushSeqTarget(seqStatement);
            var statements = new List<LoweredStatement>();
            ctx.Output.Push(statements);
            try
            {
                foreach (var statement in seqStatement.Body.Statements)
                {
                    var lowered = LowerStatementDispatcher.Visit(statement, ctx, env);
                    if (lowered == null) return null;
                    statements.Add(lowered);
                }
                if (ValueBlockFacility.ContainsSeqExit(statements))
                {
                    ValueBlockFacility.TransformStatements(statements, ctx, env);
                    if (ctx.TransformFailed) return null;
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
                                     env.Unit.Symbols.Bootstrap.Exception)))
                        }).ToList());
                }
                return new LoweredSeqBlock(seqStatement, protectedBody, seqStatement.IsVolatile);
            }
            finally
            {
                ctx.Output.Pop();
                ctx.Targets.PopSeqTarget();
            }
        }
    }

    // return@语句seq 降级（M61，SYNTAX §6.1）：纯控制流标记节点——
    // 不产指令；目标 seq 降级层经 continuation 编织消费
    internal sealed class SeqExitRewriter
        : LoweredVisitor<SeqExitRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var exit = (BoundSeqExitStatement)node;
            return new LoweredSeqExitStatement(exit, exit.Target);
        }
    }

    // return@ 脱糖为「写目标值块局部」；目标沿值块映射栈查找（穿透外层
    // 时命中外层值块）。同块其后语句的截断由值块降级循环处理。普通块
    // （非值块降级上下文）出现即内部错误——P3 已保证 return@ 只在值块内
    internal sealed class ReturnValueRewriter
        : LoweredVisitor<ReturnValueRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var returnValue = (BoundReturnValueStatement)node;
            var writeTarget = ValueBlockFacility.FindTarget(returnValue.Target, ctx);
            var written = LowerExpressionDispatcher.Visit(returnValue.Value, ctx, env);
            if (written == null) return null;
            return new LoweredAssignmentStatement(returnValue,
                SynthLocalFactory.ReferenceTo(returnValue, writeTarget), written);
        }
    }

    // break/continue 是 BIL 真跳转（S7c-1，§16.5），直接携带目标循环的
    // breakid——穿透值块/嵌套块无需任何展开；其后语句在 BIL 块内自然
    // 不可达（无需 if 转换介入）
    internal sealed class LoopControlRewriter
        : LoweredVisitor<LoopControlRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var loopControl = (BoundLoopControl)node;
            return new LoweredLoopControl(loopControl, loopControl.IsBreak,
                LoopFacility.FindBreakId(loopControl.Target, ctx));
        }
    }

    // M109b-1：cell 构造点 wrapper 前缀实参降级。无参 → null（普通 new）；
    // 有参 → 非 null 列表（new.wrapped）。共享于 LocalDeclaration/Loop/TrySeq/
    // ClosureStoragePlan 四构造点
    internal static class CellWrappedNew
    {
        public static IReadOnlyList<LoweredExpression>? LowerWrapperInitArgs(
            BoundNode origin, CellStorageInfo storage, LowerContext ctx, LowerEnvironment env)
        {
            if (storage.InitWrapper == null || storage.InitWrapper.Parameters.Count == 0)
                return null;
            var result = new List<LoweredExpression>();
            foreach (var bound in storage.WrapperInitArguments)
            {
                var lowered = LowerExpressionDispatcher.Visit(bound, ctx, env);
                if (lowered == null) return null;
                result.Add(lowered);
            }
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
            var args = new List<LoweredExpression>();
            foreach (var argument in stmt.Arguments)
            {
                var lowered = LowerExpressionDispatcher.Visit(argument, ctx, env);
                if (lowered == null) return null;
                args.Add(lowered);
            }
            return new LoweredNewWrapperStatement(stmt, stmt.Kind, stmt.WrapperType, stmt.Target,
                args);
        }
    }
}
