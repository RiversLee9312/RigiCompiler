namespace RigiCompiler
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
            LoweredExpression? access = null;
            // bug O5（SYNTAX §3.4 + BIL §15.1/§21.3）：`?.` 调 void 方法
            // （语句位 AllowVoidCall 产物，Access 为 Type=Any 的
            // BoundInstanceCallExpression）——then 块发 LoweredCallStatement
            // 走 CallStatementEmitter 的 invoke.noret 路径，s_result 保持
            // null，不做赋值（赋值管线要结果槽，与 §15.1「void 必须
            // invoke.noret」冲突）。async void 调用点类型已改写为 Task
            // （§15.2，AsyncResultType），走常规表达式路径产 invoke，
            // 不在此特判
            var isVoidCall = safeAccess.Access is BoundInstanceCallExpression
            {
                Method.ReturnType: null, Method.IsAsync: false,
            };
            ctx.Output.Push(thenStatements);
            try
            {
                if (isVoidCall)
                {
                    var callStatement = LowerVoidInstanceCall(
                        (BoundInstanceCallExpression)safeAccess.Access, ctx, env);
                    if (callStatement == null) return null;
                    thenStatements.Add(callStatement);
                }
                else
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
            }
            finally
            {
                ctx.Output.Pop();
                ctx.Targets.PopSafeReceiver();
            }
            if (!isVoidCall && access == null) return null;
            ctx.Output.Add(new LoweredIfStatement(safeAccess, condition,
                new LoweredBlock(safeAccess, thenStatements), null, ctx.Synth.NewBreakIdLocal()));
            return SynthLocalFactory.ReferenceTo(safeAccess, result);
        }

        // bug O5：void 实例调用降级为调用语句（invoke.noret 路径）——
        // receiver/实参降级与 InstanceCallRewriter/CallStatementRewriter
        // 同口径（wrapper place 物化、宿主 cast、EvalOrderGuard 求值序
        // 保护）；占位叶子经 safeReceivers 栈映射为 unwrap cast
        private static LoweredCallStatement? LowerVoidInstanceCall(
            BoundInstanceCallExpression call, LowerContext ctx, LowerEnvironment env)
        {
            var guard = new EvalOrderGuard(ctx);
            LoweredExpression? receiver;
            if (call.Receiver is BoundWrapperAccessExpression place)
            {
                receiver = WrapperPlaceLowering.Materialize(place, null, ctx, env);
            }
            else
            {
                receiver = LowerExpressionDispatcher.Visit(call.Receiver, ctx, env);
                if (receiver != null)
                {
                    receiver = LoweringFacility.EnsureDeclaredType(call, receiver,
                        call.Method.Owner);
                }
            }
            if (receiver == null) return null;
            guard.Track(call.Receiver, receiver);
            var arguments = LoweringFacility.LowerArguments(call.Arguments,
                call.Method.Parameters, ctx, env, guard, call.Receiver.Type as TypeSymbol,
                call.Method, call.TypeArguments);
            if (arguments == null) return null;
            var genericPack = call.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(call.GenericPack, call.GenericPack.IsNamed,
                    call.GenericPack.TypeArguments, call.GenericPack.NamedTypes);
            var sealedSlots = guard.Seal();
            var sealedArguments = new List<LoweredExpression>(call.Arguments.Count);
            for (var i = 1; i < sealedSlots.Count; i++) sealedArguments.Add(sealedSlots[i]);
            return new LoweredCallStatement(call, call.Method, sealedArguments, sealedSlots[0],
                call.TypeArguments, genericPack);
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
                thenBlock, elseBlock, ctx.Synth.NewBreakIdLocal()));
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
