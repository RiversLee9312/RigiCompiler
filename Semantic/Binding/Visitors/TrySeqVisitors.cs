namespace RigiCompiler
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
            // #28④：降级调用结果 Any 可 throw（P4a cast 物化到 Exception）
            if (!SymbolLookup.IsAssignable(exception.Type, env.B.Exception, env)
                && !BoundAnalysis.IsDowngradeCallResult(exception, env))
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
    // `_` 默认标签值块专属）；两种 seq using 形态共用同一绑定设施。
    internal sealed class SeqStatementVisitor
        : BinderVisitor<SeqStatementVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var seq = (SeqBlockExpressionASTNode)node;
            var shell = new BoundSeqStatement(node, seq.IsVolatile, seq.Label);
            if (seq.Label != null)
            {
                ctx.Labels.PushSeqLabel(shell);
            }
            try
            {
                var usingScope = new Scope(scope);
                var bindings = UsingBindingBinder.Bind(seq.UsingBindings, usingScope, ctx, env);
                shell.UsingBindings = bindings;
                shell.Body = BlockDispatcher.Visit(seq.Body, usingScope, ctx, env);
            }
            finally
            {
                if (seq.Label != null) ctx.Labels.PopSeqLabel();
            }
            return shell;
        }
    }

    // seq 表达式（S7e，SYNTAX §6.1）：体即值块（标签同源 Label ?? "_"，
    // 取值规则同 if 表达式分支体：恰好一条非赋值表达式语句即隐式值；
    // 多语句须显式 return@）；using initializer/body 可见前序资源；
    // 无本块产值且路径未全逃逸则报错（无产值的 seq 应写语句形态）
    internal sealed class SeqExpressionVisitor
        : ExpressionVisitor<SeqExpressionVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var seq = (SeqBlockExpressionASTNode)node;
            var usingScope = new Scope(scope);
            var usingBindings = UsingBindingBinder.Bind(seq.UsingBindings, usingScope, ctx, env);
            var shell = new ValueBlockShell(new BoundValueBlock(seq.Body, seq.Label ?? "_"),
                "seq expression");
            // 外部期望类型回填值块（绑定前就绪）——体内 return@ 值表达式
            // 据其做上下文定型（同一机制同 if/switch 表达式分支）
            shell.Block.ExpectedType = expectedType;
            ValueBlockVisitor.VisitInto(seq.Body, usingScope, shell, ctx, env);
            shell.Block.IsVolatile = seq.IsVolatile;
            if (shell.Block.ValueType == null)
            {
                // 体全路径向外逃逸（无命中自身的 return@——体内每条
                // return@ 都穿透到外层块——且路径全终止）：表达式永不
                // 落穿，合法；类型取外部期望类型兜底（表达式位引用只落
                // 在不可达死代码里）。无期望类型则无法定型，维持报错
                if (BoundAnalysis.GuaranteesValueReturn(shell.Block.Block))
                {
                    if (expectedType != null)
                    {
                        return new BoundSeqExpression(node, shell.Block, expectedType,
                            usingBindings);
                    }
                    env.Error(seq.Span, "seq expression escapes on all paths without " +
                        "producing a value (a type annotation is required to type it)");
                    return null;
                }
                env.Error(seq.Span, "seq expression must produce a value " +
                    "(at least one path must return@ a value)");
                return null;
            }
            return new BoundSeqExpression(node, shell.Block, shell.Block.ValueType, usingBindings);
        }
    }

    internal static class UsingBindingBinder
    {
        public static IReadOnlyList<BoundUsingBinding> Bind(
            IReadOnlyList<UsingBindingASTNode> nodes, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var bindings = new List<BoundUsingBinding>();
            foreach (var usingNode in nodes)
            {
                var declaredType = usingNode.Type == null
                    ? null
                    : TypeReferences.Resolve(usingNode.Type, usingNode.Span, ctx.Frame, env);
                var initializer = ExpressionDispatcher.Visit(usingNode.Initializer.Expression,
                    scope, ctx, env, declaredType as TypeSymbol);
                var resourceType = declaredType ?? initializer?.Type;
                if (initializer == null || resourceType is not TypeSymbol type
                    || type is ErrorTypeSymbol) continue;
                if (declaredType != null && !SymbolLookup.IsAssignable(initializer.Type,
                    declaredType, env)
                    && !BoundAnalysis.IsDowngradeCallResult(initializer, env))
                {
                    env.Error(usingNode.Initializer.Span ?? usingNode.Span,
                        $"Cannot assign '{BoundAnalysis.TypeDisplay(initializer.Type)}' to " +
                        $"'{BoundAnalysis.TypeDisplay(declaredType)}'");
                }
                var disposable = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                    .FirstOrDefault(n => n.Name == "core")?.Types
                    .FirstOrDefault(t => t.Name == "IDisposable" && t.GenericParameters.Count == 0);
                if (disposable == null || !SymbolLookup.IsAssignable(type, disposable, env))
                {
                    env.Error(usingNode.Span, $"using resource type '{BoundAnalysis.TypeDisplay(type)}' " +
                        "must be assignable to 'core.IDisposable'");
                    continue;
                }
                var dispose = SymbolLookup.FindInstanceMethods(
                    SymbolLookup.EffectiveMemberType(type, env), "dispose", env.Unit.Symbols)
                    .FirstOrDefault(m => m.Parameters.Count == 0 && m.ReturnType == null
                        && ctx.Frame.CanAccess(m));
                if (dispose == null)
                {
                    env.Error(usingNode.Span,
                        $"using resource type '{BoundAnalysis.TypeDisplay(type)}' has no accessible no-argument dispose method");
                    continue;
                }
                if (dispose.IsAsync || dispose.IsOpen || dispose.IsAbstract)
                {
                    env.Error(usingNode.Span,
                        $"using resource type '{BoundAnalysis.TypeDisplay(type)}' has an unsupported dispose method " +
                        "(dispose must be synchronous, closed, and non-abstract)");
                    continue;
                }
                if (scope.DeclaresHere(usingNode.VariableName))
                {
                    env.Error(usingNode.Span, $"Duplicate local variable '{usingNode.VariableName}'");
                    continue;
                }
                var local = new LocalSymbol(usingNode.VariableName, resourceType, usingNode.IsConst,
                    isUsingResource: true);
                scope.Declare(local);
                ctx.Locals.Add(local);
                ctx.Flow.MarkAssigned(local);
                var receiver = new BoundValueReferenceExpression(usingNode, local, resourceType);
                var disposeCall = new BoundCallStatement(usingNode, dispose,
                    Array.Empty<BoundExpression>(), receiver);
                bindings.Add(new BoundUsingBinding(usingNode, local, initializer, dispose,
                    disposeCall));
            }
            return bindings;
        }
    }
}
