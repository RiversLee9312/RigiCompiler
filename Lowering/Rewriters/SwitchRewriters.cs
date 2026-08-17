namespace RigiCompiler
{
    // switch 降级（S7d，SYNTAX §7.2；BIL §16.6）。
    // 自旧 LowerSession.LowerSwitchStatement/LowerSwitchCore/
    // LowerPatternSwitch/BuildPatternChain/FindSwitchTemp 迁移，行为不变。

    // switch 语句：分支体恒等降级（LoweredBlock），汇合进共用核心
    internal sealed class SwitchStatementRewriter
        : LoweredVisitor<SwitchStatementRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var switchStatement = (BoundSwitchStatement)node;
            var cases = new List<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                LoweredBlock Body)>();
            foreach (var boundCase in switchStatement.Cases)
            {
                var body = LowerBlockVisitor.Visit(boundCase.Body, ctx, env);
                if (body == null) return null;
                cases.Add((boundCase, boundCase.Match, boundCase.IsPattern, body));
            }
            var defaultBody = LowerBlockVisitor.Visit(switchStatement.DefaultBody, ctx, env);
            if (defaultBody == null) return null;
            return SwitchFacility.LowerCore(switchStatement, switchStatement.Selector, cases,
                defaultBody, ctx, env);
        }
    }

    // switch 降级共用核心（语句/表达式两形态汇合）：分支体已按形态预先
    // 降级。全值匹配 → LoweredSwitch（§16.6 指令 + 常量表；selector 只
    // 求值一次——发射期经临时变量物化）；任一 case 为 pattern →
    // 嵌套 if 链（§16.6：含 _ 的 pattern 分支不能进常量表）。
    // breakId（Stage B 可选）：switch 表达式全值路径的外部预建
    // .breakid（return@ 目标映射已登记该 id）；不传则维持语句路径
    // 现状自创建；pattern 路径忽略（if 链无总 region，表达式形态由
    // 调用方外包 LoweredSeqBlock）
    internal static class SwitchFacility
    {
        public static LoweredStatement? LowerCore(BoundNode origin, BoundExpression selector,
            IReadOnlyList<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                LoweredBlock Body)> cases,
            LoweredBlock defaultBody, LowerContext ctx, LowerEnvironment env,
            LocalSymbol? breakId = null)
        {
            if (cases.Any(c => c.IsPattern))
            {
                return LowerPatternSwitch(origin, selector, cases, defaultBody, ctx, env);
            }
            var loweredSelector = LowerExpressionDispatcher.Visit(selector, ctx, env);
            if (loweredSelector == null) return null;
            breakId ??= ctx.Synth.NewBreakIdLocal();
            var loweredCases = new List<LoweredSwitchCase>();
            foreach (var (caseOrigin, match, _, body) in cases)
            {
                // 值匹配分支：P3 已限定编译期常量（BoundLiteralExpression），
                // 恒等降级无前置语句
                var value = LowerExpressionDispatcher.Visit(match, ctx, env);
                if (value == null) return null;
                loweredCases.Add(new LoweredSwitchCase(caseOrigin, value, body));
            }
            return new LoweredSwitch(origin, loweredSelector, loweredCases, defaultBody, breakId);
        }

        // pattern 降级（§16.6 规则）：selector 先求值进合成局部 .sN
        // （前置语句，全 switch 只求值一次——占位 _ 即读该局部）；随后按
        // case 顺序构造嵌套 if 链（值匹配分支条件 = 合成 cmp.eq(.sN, 常量)，
        // pattern 分支条件 = 占位映射开启下的表达式降级），default 落最内
        // 层 else。首个命中胜出，与 §16.6 表序语义一致
        private static LoweredStatement? LowerPatternSwitch(BoundNode origin,
            BoundExpression selector,
            IReadOnlyList<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                LoweredBlock Body)> cases,
            LoweredBlock defaultBody, LowerContext ctx, LowerEnvironment env)
        {
            var selectorTemp = ctx.Synth.NewSynthLocal(selector.Type);
            var loweredSelector = LowerExpressionDispatcher.Visit(selector, ctx, env);
            if (loweredSelector == null) return null;
            ctx.Output.Add(new LoweredAssignmentStatement(origin,
                SynthLocalFactory.ReferenceTo(origin, selectorTemp), loweredSelector));
            ctx.Targets.PushSwitchTemp(selector, selectorTemp);
            try
            {
                return BuildPatternChain(origin, cases, 0, selectorTemp, defaultBody, ctx, env);
            }
            finally
            {
                ctx.Targets.PopSwitchTemp();
            }
        }

        // 嵌套 if 链构造（就地递归；每层在独立块上下文降级条件——条件内
        // 短路/if 表达式的前置语句自然落在该 if 所属块内，仿 else-if 链形态）
        private static LoweredBlock? BuildPatternChain(BoundNode origin,
            IReadOnlyList<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                LoweredBlock Body)> cases,
            int index, LocalSymbol selectorTemp, LoweredBlock defaultBody, LowerContext ctx,
            LowerEnvironment env)
        {
            var statements = new List<LoweredStatement>();
            ctx.Output.Push(statements);
            try
            {
                var (caseOrigin, match, isPattern, body) = cases[index];
                LoweredExpression? condition;
                if (isPattern)
                {
                    // 占位映射已由 LowerPatternSwitch 压栈，_ 读 selector 局部
                    condition = LowerExpressionDispatcher.Visit(match, ctx, env);
                }
                else
                {
                    var constant = LowerExpressionDispatcher.Visit(match, ctx, env);
                    if (constant == null) return null;
                    condition = new LoweredBinaryExpression(match, BilIntrinsicOp.CmpEq,
                        SynthLocalFactory.ReferenceTo(match, selectorTemp), constant,
                        env.Unit.Symbols.Bootstrap.Bool);
                }
                if (condition == null) return null;
                LoweredBlock elseBlock;
                if (index + 1 < cases.Count)
                {
                    var nested = BuildPatternChain(origin, cases, index + 1,
                        selectorTemp, defaultBody, ctx, env);
                    if (nested == null) return null;
                    elseBlock = nested;
                }
                else
                {
                    elseBlock = defaultBody;
                }
                statements.Add(new LoweredIfStatement(caseOrigin, condition, body, elseBlock,
                    ctx.Synth.NewBreakIdLocal()));
                return new LoweredBlock(origin, statements);
            }
            finally
            {
                ctx.Output.Pop();
            }
        }

        // BoundSwitchPlaceholderExpression.Selector 经引用查占位映射栈得
        // selector 物化局部；未命中即内部错误（P3 已保证 _ 只在 pattern
        // 匹配表达式内，降级上下文必在栈上）
        public static LocalSymbol FindTemp(BoundExpression selector, LowerContext ctx)
        {
            return ctx.Targets.FindSwitchTemp(selector)
                ?? throw new CompilerInternalException(
                    "switch 占位 _ 不在 pattern 降级上下文内（P3 已保证只在 case 匹配表达式内）");
        }
    }
}
