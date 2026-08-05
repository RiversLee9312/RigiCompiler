namespace LatteCompiler
{
    // 安全访问与空值回退脱糖（S7f，SYNTAX §3.4；BIL §3.4）。
    // 自旧 LowerSession.LowerSafeAccess/LowerSafeReceiver/
    // LowerNullFallback/NullCheckCondition 迁移，行为不变。

    // `?.` 脱糖：
    //   a?.b ⇒ 前置 s_recv = a'；前置 s_result = null；
    //           前置 if (s_recv != null) { <access' 前置语句> s_result = cast(access', R?) }；
    //           表达式位 s_result 引用
    // receiver 物化保证只求值一次；Access 降级在 thenBlock 输出列表上下文
    // 进行——子树内脱糖表达式的前置语句随 thenBlock 走（§3.4：receiver
    // 为空则整体不求值）；Access 内的占位叶子经 safeReceivers
    // 栈映射为 cast(s_recv, T)（unwrap，§12.1——Enter 压栈/Exit 弹栈，
    // 替代旧代码无 finally 保护的手工配对）；null 检查 =
    // cmp.ne(s_recv, null 资源)（§19.1：null 资源类型即 .nullable<T>，
    // 满足 §11.5 严格相同）；结果局部的 R → Nullable\<R\> 包装经
    // EnsureDeclaredType 物化（已可空时直通）
    internal sealed class SafeAccessRewriter
        : LoweredVisitor<SafeAccessRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var safeAccess = (BoundSafeAccessExpression)node;
            var receiver = LowerExpressionDispatcher.Visit(safeAccess.Receiver, ctx, env);
            if (receiver == null) return null;
            var receiverLocal = ctx.Synth.NewSynthLocal(receiver.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(safeAccess,
                SynthLocalFactory.ReferenceTo(safeAccess, receiverLocal), receiver));
            var result = ctx.Synth.NewSynthLocal(safeAccess.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(safeAccess,
                SynthLocalFactory.ReferenceTo(safeAccess, result),
                new LoweredConstantExpression(safeAccess, null!, safeAccess.Type)));
            var condition = NullSafetyFacility.NullCheckCondition(safeAccess, receiverLocal, env);
            // 占位映射仅 Access 降级期间存活（try/finally 配对——修固旧代码
            // 无 finally 保护的手工压弹）
            ctx.Targets.PushSafeReceiver(safeAccess.Placeholder, receiverLocal,
                safeAccess.Placeholder.Type);
            // Access 降级收进 thenBlock 的输出列表上下文（§3.4「receiver 为空
            // 则整体不求值」）：Access 子树内脱糖类表达式（短路 and/or、
            // if/switch/seq 表达式、复合赋值、if?）的前置语句落入 thenBlock，
            // 不泄漏到 null 检查之前——与 NullFallbackRewriter else 分支的
            // LowerAssignInNewBlock 独立块上下文同一机制
            var thenStatements = new List<LoweredStatement>();
            LoweredExpression? access;
            ctx.Output.Push(thenStatements);
            try
            {
                access = LowerExpressionDispatcher.Visit(safeAccess.Access, ctx, env);
                if (access != null)
                {
                    var wrapped = LoweringFacility.EnsureDeclaredType(safeAccess, access,
                        safeAccess.Type);
                    thenStatements.Add(new LoweredAssignmentStatement(safeAccess,
                        SynthLocalFactory.ReferenceTo(safeAccess, result), wrapped));
                }
            }
            finally
            {
                ctx.Output.Pop();
                ctx.Targets.PopSafeReceiver();
            }
            if (access == null) return null;
            ctx.Output.Add(new LoweredIfStatement(safeAccess, condition,
                new LoweredBlock(safeAccess, thenStatements), null));
            return SynthLocalFactory.ReferenceTo(safeAccess, result);
        }
    }

    // 占位叶子 → 物化 receiver 局部的 unwrap cast（引用相等查栈，
    // 逐层向内命中；栈空/未命中 = 内部一致性破坏）
    internal sealed class SafeReceiverRewriter
        : LoweredVisitor<SafeReceiverRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var placeholder = (BoundSafeAccessReceiverExpression)node;
            var entry = ctx.Targets.FindSafeReceiver(placeholder);
            if (entry != null)
            {
                return new LoweredCastExpression(placeholder,
                    SynthLocalFactory.ReferenceTo(placeholder, entry.Value.Receiver),
                    entry.Value.UnwrapType, isSafe: false, entry.Value.UnwrapType);
            }
            env.Error(placeholder.Syntax.Span,
                "P4: safe access receiver placeholder without enclosing safe access");
            return null;
        }
    }

    // `if?` 脱糖：
    //   l if? r ⇒ 前置 s_left = l'；
    //             前置 if (s_left != null) { s_result = cast(s_left, T) }
    //                     else { s_result = r' }；
    //             表达式位 s_result 引用（r 延迟求值由 if 结构保证）
    internal sealed class NullFallbackRewriter
        : LoweredVisitor<NullFallbackRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var nullFallback = (BoundNullFallbackExpression)node;
            var left = LowerExpressionDispatcher.Visit(nullFallback.Left, ctx, env);
            if (left == null) return null;
            var leftLocal = ctx.Synth.NewSynthLocal(left.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(nullFallback,
                SynthLocalFactory.ReferenceTo(nullFallback, leftLocal), left));
            var result = ctx.Synth.NewSynthLocal(nullFallback.Type);
            var condition = NullSafetyFacility.NullCheckCondition(nullFallback, leftLocal, env);
            var thenBlock = new LoweredBlock(nullFallback, new List<LoweredStatement>
            {
                new LoweredAssignmentStatement(nullFallback,
                    SynthLocalFactory.ReferenceTo(nullFallback, result),
                    new LoweredCastExpression(nullFallback,
                        SynthLocalFactory.ReferenceTo(nullFallback, leftLocal), nullFallback.Type,
                        isSafe: false, nullFallback.Type)),
            });
            var elseBlock = ExpressionFacility.LowerAssignInNewBlock(nullFallback,
                nullFallback.Right, result, ctx, env);
            if (elseBlock == null) return null;
            ctx.Output.Add(new LoweredIfStatement(nullFallback, condition,
                thenBlock, elseBlock));
            return SynthLocalFactory.ReferenceTo(nullFallback, result);
        }
    }

    internal static class NullSafetyFacility
    {
        // null 检查条件（§19.1/§11.5）：cmp.ne(local, null 资源) → bool
        public static LoweredBinaryExpression NullCheckCondition(BoundNode origin,
            LocalSymbol local, LowerEnvironment env)
        {
            return new LoweredBinaryExpression(origin, BilIntrinsicOp.CmpNe,
                SynthLocalFactory.ReferenceTo(origin, local),
                new LoweredConstantExpression(origin, null!, local.Type!),
                env.Unit.Symbols.Bootstrap.Bool);
        }
    }
}
