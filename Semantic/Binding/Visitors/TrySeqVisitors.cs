namespace LatteCompiler
{
    // try/catch/finally（S7e，SYNTAX §8）、throw（S7d）、seq 双形态（S7e，§10）。
    // 自旧 BindSession.BindTry/BindThrow/BindSeqStatement/BindSeqExpression
    // 迁移，行为不变。
    internal sealed class TryVisitor : BinderVisitor<TryVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var tryNode = (TryCatchFinallyStatementASTNode)node;
            var before = ctx.Flow.Snapshot();
            // S8b 收窄维度（SYNTAX §3.5，与 if/switch 同构）：try/catch/finally
            // 各从 before 快照出发绑定——try 体内收窄（如 guard）不泄入 catch/
            // finally（异常路径上该收窄恰恰不成立），各 catch 依次不继承前一体
            var beforeNarrowed = ctx.Flow.SnapshotNarrowed();
            var tryBlock = BlockDispatcher.Visit(tryNode.TryBlock, scope, ctx, env);
            var tryAssigned = ctx.Flow.Snapshot();
            var tryNarrowed = ctx.Flow.SnapshotNarrowed();

            var catches = new List<BoundCatchClause>();
            var catchTails = new List<HashSet<LocalSymbol>>();
            var catchNarrowedTails = new List<Dictionary<NarrowKey, TypeSymbol>>();
            foreach (var catchNode in tryNode.CatchClauses)
            {
                ctx.Flow.Restore(before);
                ctx.Flow.RestoreNarrowed(beforeNarrowed);
                var exceptionType = TypeReferences.Resolve(catchNode.ExceptionType, catchNode.Span,
                    ctx.Frame, env);
                if (exceptionType != null
                    && !SymbolLookup.IsAssignable(exceptionType, env.B.Exception, env))
                {
                    env.Error(catchNode.Span, $"catch type must be compatible with 'Exception' " +
                        $"(got '{BoundAnalysis.TypeDisplay(exceptionType)}')");
                }
                // catch 变量：_ 即丢弃（VariableName 为 null）；const 局部，
                // 命中即已赋值（作用域限 catch 体）
                LocalSymbol? variable = null;
                var catchScope = new Scope(scope);
                if (catchNode.VariableName != null && exceptionType != null)
                {
                    variable = new LocalSymbol(catchNode.VariableName, exceptionType,
                        isConst: true);
                    catchScope.Declare(variable);
                    ctx.Locals.Add(variable);
                    ctx.Flow.MarkAssigned(variable);
                }
                var body = BlockDispatcher.Visit(catchNode.Body, catchScope, ctx, env);
                catchTails.Add(ctx.Flow.Snapshot());
                catchNarrowedTails.Add(ctx.Flow.SnapshotNarrowed());
                if (exceptionType != null)
                {
                    catches.Add(new BoundCatchClause(catchNode, variable, exceptionType, body));
                }
            }

            BoundBlock? finallyBlock = null;
            LocalSymbol? finallyVariable = null;
            HashSet<LocalSymbol>? finallyAssigned = null;
            if (tryNode.FinallyBlock != null)
            {
                ctx.Flow.Restore(before);
                ctx.Flow.RestoreNarrowed(beforeNarrowed);
                var finallyScope = new Scope(scope);
                if (tryNode.FinallyParameter != null)
                {
                    finallyVariable = new LocalSymbol(tryNode.FinallyParameter,
                        env.Unit.Symbols.GetNullable(env.B.Exception), isConst: true);
                    finallyScope.Declare(finallyVariable);
                    ctx.Locals.Add(finallyVariable);
                    ctx.Flow.MarkAssigned(finallyVariable);
                }
                finallyBlock = BlockDispatcher.Visit(tryNode.FinallyBlock, finallyScope, ctx, env);
                finallyAssigned = ctx.Flow.Snapshot();
            }

            // DA 合并（S7e）：有 catch 时 before ∪ (try ∩ 各 catch)，无 catch
            // 时 try 直通——异常必穿透，finally 恒执行并集
            HashSet<LocalSymbol> merged;
            if (catchTails.Count > 0)
            {
                merged = new HashSet<LocalSymbol>(tryAssigned);
                foreach (var set in catchTails) merged.IntersectWith(set);
                merged.UnionWith(before);
            }
            else
            {
                merged = tryAssigned;
            }
            if (finallyAssigned != null)
            {
                merged.UnionWith(finallyAssigned);
                if (finallyVariable != null) merged.Remove(finallyVariable);
            }
            ctx.Flow.Restore(merged);
            // 收窄合并（对齐 DA 口径——收窄表非单调，无 before∪ 规则）：
            // 有 catch 时出口路径 = try 正常尾 ∪ 各 catch 尾 → 纯交集
            // （try 体内的 guard 收窄不活到出口——catch 路径上它不成立）；
            // 无 catch 时异常穿透，出口路径唯一 = try 正常尾 → try 尾直通。
            // finally 不参与交集（其入口按异常路径保守从 before 绑定，
            // 正常路径上 try 的收窄效果不应被 finally 尾态误杀）；但 finally
            // 恒执行于所有到达 try 之后的路径——其体内赋值根的收窄必失效
            // （同循环出口规则）
            if (catchNarrowedTails.Count > 0)
            {
                var narrowedTails = new List<Dictionary<NarrowKey, TypeSymbol>> { tryNarrowed };
                narrowedTails.AddRange(catchNarrowedTails);
                ctx.Flow.MergeNarrowedBranches(narrowedTails);
            }
            else
            {
                ctx.Flow.RestoreNarrowed(tryNarrowed);
            }
            if (tryNode.FinallyBlock != null)
            {
                foreach (var root in LoopVisitor.CollectAssignedRoots(tryNode.FinallyBlock, scope,
                    ctx.Frame))
                {
                    ctx.Flow.ClearRoot(root);
                }
            }
            return new BoundTryStatement(node, tryBlock, catches, finallyBlock, finallyVariable);
        }
    }

    // throw（S7d，SYNTAX §8）：异常表达式必须与异常根 core.Exception 兼容
    // （IsAssignable 沿 BaseType 链命中；ErrorType 毒化静默）
    internal sealed class ThrowVisitor : BinderVisitor<ThrowVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var throwNode = (ThrowStatementASTNode)node;
            var exception = ExpressionDispatcher.Visit(throwNode.Exception.Expression, scope, ctx,
                env, env.B.Exception);
            if (exception == null) return null;
            if (!SymbolLookup.IsAssignable(exception.Type, env.B.Exception, env))
            {
                env.Error(throwNode.Exception.Span ?? throwNode.Span,
                    $"Cannot throw '{BoundAnalysis.TypeDisplay(exception.Type)}' " +
                    "(not compatible with 'Exception')");
                return null;
            }
            return new BoundThrowStatement(node, exception);
        }
    }

    // seq 语句（S7e，SYNTAX §10.1）：块级顺序执行区——绑定直通块分派
    // （作用域/DA 语义与裸块相同）；M61 起 named 语句 seq 可作 return@
    // 目标（§6.1：压标签栈绑体，try/finally 配对；仅显式 named 压栈——
    // `_` 默认标签值块专属）；using 归 S13（拦截）
    internal sealed class SeqStatementVisitor
        : BinderVisitor<SeqStatementVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var seq = (SeqBlockExpressionASTNode)node;
            if (seq.UsingBindings.Count > 0)
            {
                env.Error(seq.Span, "P3: using bindings are not supported yet (S13)");
                return null;
            }
            var shell = new BoundSeqStatement(node, seq.IsVolatile, seq.Label);
            if (seq.Label != null)
            {
                ctx.Labels.PushSeqLabel(shell);
            }
            try
            {
                shell.Body = BlockDispatcher.Visit(seq.Body, scope, ctx, env);
            }
            finally
            {
                if (seq.Label != null) ctx.Labels.PopSeqLabel();
            }
            return shell;
        }
    }

    // seq 表达式（S7e，SYNTAX §10.2）：体即值块（标签同源 Label ?? "_"，
    // 取值规则同 if 表达式分支体）；volatile 置位到值块（BIL §9.6
    // block 修饰符）；using 归 S13（拦截）；必须产值（至少一条路径
    // return@——无产值的 seq 块应写语句形态）
    internal sealed class SeqExpressionVisitor
        : ExpressionVisitor<SeqExpressionVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var seq = (SeqBlockExpressionASTNode)node;
            if (seq.UsingBindings.Count > 0)
            {
                env.Error(seq.Span, "P3: using bindings are not supported yet (S13)");
                return null;
            }
            var shell = new ValueBlockShell(new BoundValueBlock(seq.Body, seq.Label ?? "_"),
                "seq expression");
            ValueBlockVisitor.VisitInto(seq.Body, scope, shell, ctx, env);
            shell.Block.IsVolatile = seq.IsVolatile;
            if (shell.Block.ValueType == null)
            {
                env.Error(seq.Span, "seq expression must produce a value " +
                    "(at least one path must return@ a value)");
                return null;
            }
            return new BoundSeqExpression(node, shell.Block, shell.Block.ValueType);
        }
    }
}
