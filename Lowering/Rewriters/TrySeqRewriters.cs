namespace RigiCompiler
{
    // try 降级（S7e，SYNTAX §8；BIL §16.7）。自旧 LowerSession.LowerTry
    // 迁移，行为不变；M106 补 catch/finally(e) 被 lambda 捕获时的 cell 构造。
    // try → LoweredTryStatement（指令直接对应）：
    // - ExceptionSlot（$slot 操作数承载局部）：finally(e) 的 e 非空且未
    //   cell 化时即该局部（指令直写——e 的「无异常为 null」语义即指令
    //   写 slot 语义）；e 被 cell 化时 slot 与变量分离（合成 .sN 普通
    //   临时，类型 Nullable<core.Exception>——.vars 不投影 cell），
    //   finally 体头 `e = new ..cell..(slot)`；否则合成 .sN；
    // - 有名 catch：体头合成「变量 = cast slot」（Origin 指
    //   BoundCatchClause）——slot 的 Nullable<core.Exception> 到
    //   catch 类型的收窄走显式 cast（BIL §12.1），P3 已查兼容；
    //   被 lambda 捕获时再包 `new ..cell..(cast)`（进入 catch 块时
    //   构造一个 cell，SYNTAX §5.2/§8）；
    // - 三分支体各自恒等降级（前置语句随块走）
    internal sealed class TryRewriter : LoweredVisitor<TryRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var tryStatement = (BoundTryStatement)node;
            // ExceptionSlot：cell 化 finally(e) 时分离——try 指令只能直写
            // Nullable<Exception> 普通局部，不能写 cell 类型 .vars 条目
            var exceptionSlot = tryStatement.FinallyVariable is { CellStorage: not null }
                ? ctx.Synth.NewSynthLocal(env.Unit.Symbols.GetNullable(
                    env.Unit.Symbols.Bootstrap.Exception))
                : tryStatement.FinallyVariable
                    ?? ctx.Synth.NewSynthLocal(env.Unit.Symbols.GetNullable(
                        env.Unit.Symbols.Bootstrap.Exception));
            var tryBlock = LowerBlockVisitor.Visit(tryStatement.TryBlock, ctx, env);
            if (tryBlock == null) return null;
            var catches = new List<LoweredTryCatch>();
            foreach (var boundCatch in tryStatement.Catches)
            {
                var body = LowerBlockVisitor.Visit(boundCatch.Body, ctx, env);
                if (body == null) return null;
                if (boundCatch.Variable != null)
                {
                    // cast slot → catch 类型；cell 化时再包 new ..cell..(cast)
                    LoweredExpression catchValue = new LoweredCastExpression(boundCatch,
                        SynthLocalFactory.ReferenceTo(boundCatch, exceptionSlot),
                        boundCatch.ExceptionType, isSafe: false,
                        boundCatch.ExceptionType);
                    if (boundCatch.Variable.CellStorage is { } catchStorage)
                    {
                        var wrapperArgs = CellWrappedNew.LowerWrapperInitArgs(boundCatch,
                            catchStorage, ctx, env);
                        catchValue = new LoweredNewExpression(boundCatch, catchStorage.ValueInit,
                            new List<LoweredExpression> { catchValue }, catchStorage.CellType,
                            wrapperArgs);
                    }
                    var castAssign = new LoweredAssignmentStatement(boundCatch,
                        SynthLocalFactory.ReferenceTo(boundCatch, boundCatch.Variable),
                        catchValue);
                    body = new LoweredBlock(body.Origin,
                        new List<LoweredStatement> { castAssign }
                            .Concat(body.Statements).ToList());
                }
                catches.Add(new LoweredTryCatch(boundCatch, boundCatch.Variable,
                    boundCatch.ExceptionType, body));
            }
            LoweredBlock? finallyBlock = null;
            if (tryStatement.FinallyBlock != null)
            {
                finallyBlock = LowerBlockVisitor.Visit(tryStatement.FinallyBlock, ctx, env);
                if (finallyBlock == null) return null;
                // cell 化 finally(e)：体头 e = new ..cell..(slot)
                if (tryStatement.FinallyVariable is { CellStorage: { } finallyStorage } finallyVar)
                {
                    var wrapperArgs = CellWrappedNew.LowerWrapperInitArgs(tryStatement,
                        finallyStorage, ctx, env);
                    var cellAssign = new LoweredAssignmentStatement(tryStatement,
                        SynthLocalFactory.ReferenceTo(tryStatement, finallyVar),
                        new LoweredNewExpression(tryStatement, finallyStorage.ValueInit,
                            new List<LoweredExpression>
                            {
                                SynthLocalFactory.ReferenceTo(tryStatement, exceptionSlot)
                            },
                            finallyStorage.CellType, wrapperArgs));
                    finallyBlock = new LoweredBlock(finallyBlock.Origin,
                        new List<LoweredStatement> { cellAssign }
                            .Concat(finallyBlock.Statements).ToList());
                }
            }
            return new LoweredTryStatement(tryStatement, tryBlock, catches, finallyBlock,
                exceptionSlot);
        }
    }

    // seq 表达式脱糖（S7e，BIL §3.4 call 化）：合成结果局部 v；
    // 前置 LoweredSeqBlock（值块降级产物写 v，volatile 随值块置位）；
    // 表达式位 v 引用——与 if 表达式同构，差异仅在 seq 块发射形态
    // （独立 block + call 指令）
    internal sealed class SeqExpressionRewriter
        : LoweredVisitor<SeqExpressionRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var seqExpression = (BoundSeqExpression)node;
            var result = ctx.Synth.NewSynthLocal(seqExpression.Type);
            // 复杂外层值块 continuation 穿越 using 生成的 try/finally 仍由
            // ValueBlockRewriter 的 S7e 拦截负责；不要为表达式 using 放宽该边界。
            var body = ValueBlockRewriter.Visit(seqExpression.Body,
                new ValueBlockContext(ctx, result), env);
            if (body == null) return null;
            var protectedBody = body;
            var declarations = new List<List<LoweredStatement>>();
            foreach (var binding in seqExpression.UsingBindings)
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
            for (var i = seqExpression.UsingBindings.Count - 1; i >= 0; i--)
            {
                var binding = seqExpression.UsingBindings[i];
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
                        new LoweredTryStatement(seqExpression, tryBlock,
                            Array.Empty<LoweredTryCatch>(),
                            new LoweredBlock(binding, finallyStatements),
                            ctx.Synth.NewSynthLocal(env.Unit.Symbols.GetNullable(
                                env.Unit.Symbols.Bootstrap.Exception)))
                    }).ToList());
            }
            ctx.Output.Add(new LoweredSeqBlock(seqExpression, protectedBody,
                seqExpression.Body.IsVolatile));
            return SynthLocalFactory.ReferenceTo(seqExpression, result);
        }
    }
}
