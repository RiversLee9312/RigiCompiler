namespace LatteCompiler
{
    // 类别分派器（P4a，VISITOR_REWRITE.md §6）：Bound 节点 → 结构 visitor
    // 的唯一 switch 所在（对应旧 LowerSession 的 LowerStatement/LowerExpression
    // 分派）。遇未覆盖节点：报 P4 Error 并返回 null（调用方放弃整个函数体）。
    internal static class LowerStatementDispatcher
    {
        public static LoweredStatement? Visit(BoundStatement statement, LowerContext ctx,
            LowerEnvironment env)
        {
            return statement switch
            {
                BoundBlock block => LowerBlockVisitor.Visit(block, ctx, env),
                BoundLocalDeclarationStatement => LocalDeclarationRewriter.Visit(statement, ctx, env),
                BoundDestructuringDeclarationStatement => DestructuringRewriter.Visit(statement,
                    ctx, env),
                BoundExpressionStatement => ExpressionStatementRewriter.Visit(statement, ctx, env),
                BoundCallStatement => CallStatementRewriter.Visit(statement, ctx, env),
                BoundLoop loop => loop.Kind == LoopKind.For
                    ? ForLoopRewriter.Visit(loop, ctx, env)
                    : LoopRewriter.Visit(loop, ctx, env),
                BoundSwitchStatement => SwitchStatementRewriter.Visit(statement, ctx, env),
                BoundTryStatement => TryRewriter.Visit(statement, ctx, env),
                BoundSeqStatement => SeqStatementRewriter.Visit(statement, ctx, env),
                BoundThrowStatement => ThrowRewriter.Visit(statement, ctx, env),
                BoundAssignmentStatement => AssignmentRewriter.Visit(statement, ctx, env),
                BoundReturnStatement => ReturnRewriter.Visit(statement, ctx, env),
                BoundIfStatement => IfStatementRewriter.Visit(statement, ctx, env),
                BoundReturnValueStatement => ReturnValueRewriter.Visit(statement, ctx, env),
                BoundLoopControl => LoopControlRewriter.Visit(statement, ctx, env),
                _ => Unsupported(statement, env),
            };
        }

        private static LoweredStatement? Unsupported(BoundNode node, LowerEnvironment env)
        {
            env.Error(node.Syntax.Span,
                $"P4: node kind not supported by minimal lowering (S7): " +
                node.GetType().Name);
            return null;
        }
    }

    internal static class LowerExpressionDispatcher
    {
        public static LoweredExpression? Visit(BoundExpression expression, LowerContext ctx,
            LowerEnvironment env)
        {
            return expression switch
            {
                BoundLiteralExpression => LiteralRewriter.Visit(expression, ctx, env),
                BoundValueReferenceExpression => ValueReferenceRewriter.Visit(expression, ctx, env),
                BoundFieldReferenceExpression => FieldReferenceRewriter.Visit(expression, ctx, env),
                BoundBinaryExpression => BinaryRewriter.Visit(expression, ctx, env),
                BoundUnaryExpression => UnaryRewriter.Visit(expression, ctx, env),
                BoundCallExpression => CallExpressionRewriter.Visit(expression, ctx, env),
                BoundNewExpression => NewExpressionRewriter.Visit(expression, ctx, env),
                BoundIfExpression => IfExpressionRewriter.Visit(expression, ctx, env),
                BoundSwitchExpression => SwitchExpressionRewriter.Visit(expression, ctx, env),
                BoundSwitchPlaceholderExpression => SwitchPlaceholderRewriter.Visit(expression,
                    ctx, env),
                BoundCompoundAssignmentExpression => CompoundAssignmentRewriter.Visit(expression,
                    ctx, env),
                BoundThisExpression => ThisRewriter.Visit(expression, ctx, env),
                BoundInstanceCallExpression => InstanceCallRewriter.Visit(expression, ctx, env),
                BoundFieldAccessExpression => FieldAccessRewriter.Visit(expression, ctx, env),
                BoundIndexExpression => IndexRewriter.Visit(expression, ctx, env),
                BoundCastExpression => CastRewriter.Visit(expression, ctx, env),
                BoundSmartCastExpression => SmartCastRewriter.Visit(expression, ctx, env),
                BoundTypeCheckExpression => TypeCheckRewriter.Visit(expression, ctx, env),
                BoundTypeOfExpression => TypeOfRewriter.Visit(expression, ctx, env),
                BoundSafeAccessExpression => SafeAccessRewriter.Visit(expression, ctx, env),
                BoundSafeAccessReceiverExpression => SafeReceiverRewriter.Visit(expression,
                    ctx, env),
                BoundNullFallbackExpression => NullFallbackRewriter.Visit(expression, ctx, env),
                BoundSeqExpression => SeqExpressionRewriter.Visit(expression, ctx, env),
                _ => Unsupported(expression, env),
            };
        }

        private static LoweredExpression? Unsupported(BoundNode node, LowerEnvironment env)
        {
            env.Error(node.Syntax.Span,
                $"P4: node kind not supported by minimal lowering (S7): " +
                node.GetType().Name);
            return null;
        }
    }

    // 块降级（前置语句机制的核心）：Enter 建输出列表压栈，Exit 弹栈
    // （finally 配对——替代旧代码手工 try/finally）；逐语句降级收集
    internal sealed class LowerBlockVisitor : LoweredVisitor<LowerBlockVisitor, LoweredBlock, LowerContext>
    {
        protected override void Enter(BoundNode node, LowerContext ctx, LowerEnvironment env)
        {
            ctx.OutputStack.Push(new List<LoweredStatement>());
        }

        protected override void Exit(BoundNode node, LowerContext ctx, LowerEnvironment env)
        {
            ctx.OutputStack.Pop();
        }

        protected override LoweredBlock? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var block = (BoundBlock)node;
            var statements = ctx.OutputStack.Peek();
            foreach (var statement in block.Statements)
            {
                var lowered = LowerStatementDispatcher.Visit(statement, ctx, env);
                if (lowered == null) return null;
                statements.Add(lowered);
            }
            return new LoweredBlock(block, statements);
        }
    }

    // if 语句恒等降级：条件/分支内表达式递归降级（前置语句进对应块）
    internal sealed class IfStatementRewriter
        : LoweredVisitor<IfStatementRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var ifStatement = (BoundIfStatement)node;
            var condition = LowerExpressionDispatcher.Visit(ifStatement.Condition, ctx, env);
            if (condition == null) return null;
            var trueBlock = LowerBlockVisitor.Visit(ifStatement.TrueBlock, ctx, env);
            var falseBlock = ifStatement.FalseBlock == null
                ? null : LowerBlockVisitor.Visit(ifStatement.FalseBlock, ctx, env);
            if (trueBlock == null || (ifStatement.FalseBlock != null && falseBlock == null))
            {
                return null;
            }
            return new LoweredIfStatement(ifStatement, condition, trueBlock, falseBlock);
        }
    }
}
