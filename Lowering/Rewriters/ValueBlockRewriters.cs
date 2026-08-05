namespace LatteCompiler
{
    // 值块降级与 if 转换（S7b 起；S7d continuation 编织重写，修复 M46
    // else-if 链缺陷；S7e seq/try 编织扩展）。自旧 LowerSession
    // .LowerValueBlock/TransformStatements/TransformWithContinuation/
    // WeaveContinuation/BlockTerminates/HasTerminatingPath/IsValueBlockWrite
    // 迁移，行为不变。

    // 值块降级的调用载荷（TContext 实参）：真实 LowerContext + 写目标局部
    // （同 SwitchMatchContext 模式——避免向 LowerContext 塞「当前 target」）
    internal sealed class ValueBlockContext
    {
        public ValueBlockContext(LowerContext context, LocalSymbol target)
        {
            Context = context;
            Target = target;
        }

        public LowerContext Context { get; }

        public LocalSymbol Target { get; }
    }

    // 值块降级（BoundValueBlock → LoweredBlock）：写目标局部由调用方
    // （if/switch/seq 表达式脱糖）给定；值块映射与输出列表压栈（Enter）/
    // 弹栈（Exit，finally 配对）供嵌套 return@ 查找。
    // - 隐式取值 → 单语句 v = expr；
    // - 显式：语句流降级，return@ ⇒ 写对应局部（本块或外层），同块其
    //   后语句丢弃（不可达死代码，P3 已保证路径必终止）；
    // - 收尾做 if 转换（TransformStatements）
    internal sealed class ValueBlockRewriter
        : LoweredVisitor<ValueBlockRewriter, LoweredBlock, ValueBlockContext>
    {
        protected override void Enter(BoundNode node, ValueBlockContext valueCtx,
            LowerEnvironment env)
        {
            valueCtx.Context.Output.Push();
            valueCtx.Context.Targets.PushValueBlock((BoundValueBlock)node, valueCtx.Target);
        }

        protected override void Exit(BoundNode node, ValueBlockContext valueCtx,
            LowerEnvironment env)
        {
            valueCtx.Context.Targets.PopValueBlock();
            valueCtx.Context.Output.Pop();
        }

        protected override LoweredBlock? VisitCore(BoundNode node, ValueBlockContext valueCtx,
            LowerEnvironment env)
        {
            var valueBlock = (BoundValueBlock)node;
            var ctx = valueCtx.Context;
            var statements = ctx.Output.Current;
            if (valueBlock.IsImplicitValue)
            {
                // P3 已判定：唯一语句是 BoundExpressionStatement
                if (valueBlock.Block.Statements.Count != 1
                    || valueBlock.Block.Statements[0]
                        is not BoundExpressionStatement expressionStatement)
                {
                    throw new CompilerInternalException(
                        "隐式取值值块的唯一语句不是表达式语句（P3 不变量破坏）");
                }
                var value = LowerExpressionDispatcher.Visit(expressionStatement.Expression, ctx, env);
                if (value == null) return null;
                statements.Add(new LoweredAssignmentStatement(expressionStatement,
                    SynthLocalFactory.ReferenceTo(expressionStatement, valueCtx.Target), value));
            }
            else
            {
                foreach (var statement in valueBlock.Block.Statements)
                {
                    var lowered = LowerStatementDispatcher.Visit(statement, ctx, env);
                    if (lowered == null) return null;
                    statements.Add(lowered);
                    // return@ 终止本块路径：其后语句不可达（死代码，
                    // P3 已保证路径必终止），直接截断不降级
                    if (statement is BoundReturnValueStatement) break;
                }
                ValueBlockFacility.TransformStatements(statements, ctx, env);
                // 编织拦截（S7e try+finally 部分终止）：诊断已落袋，放弃产物
                if (ctx.TransformFailed) return null;
            }
            return new LoweredBlock(valueBlock.Block, statements);
        }
    }

    // 值块设施：映射查找 + continuation 编织（LoweredTree 就地变换，
    // 非 visitor 遍历——保留静态设施形态）
    internal static class ValueBlockFacility
    {
        // return@ 目标值块映射查找（引用相等；未命中即内部错误——
        // P3 已保证 return@ 只在值块内）
        public static LocalSymbol FindTarget(BoundValueBlock valueBlock, LowerContext ctx)
        {
            return ctx.Targets.FindValueBlockTarget(valueBlock)
                ?? throw new CompilerInternalException(
                    "return@ 目标值块不在降级上下文内（P3 已保证只在值块内）");
        }

        // 值块语句序列的 if/switch 转换（就地；分支块不可变，故新建节点
        // 替换原位置）。本质是 continuation 编织（S7d 重写，修复 M46
        // else-if 链缺陷）：BIL 的 if/switch 分支块执行完必回到结构指令的
        // 下一条指令（§16.2/§16.6），而值块 return@ 语义是「写目标局部 +
        // 本路径不再执行后续」——故把「其后语句」（continuation）编织进
        // 每个会落到块尾的路径末端；终止于值块写入/throw 的路径丢弃
        // continuation。规则（对每条结构语句）：
        // - LoweredIfStatement/LoweredSwitch：其后语句序列 + 块外
        //   continuation 织入每个不终止分支末端（无 else 且需要时新建
        //   else 块），结构语句成为块内最后一条；全部分支终止时其后
        //   语句全丢弃（不可达）；
        // - 嵌套 LoweredBlock：同规则（continuation 织入块内）；
        // - 顶层值块写入：同块其后语句不可达（死代码，P3 已保证路径
        //   必终止），截断且不接收 continuation；
        // - 全无终止路径的结构语句不编织（continuation 留原位，保形）。
        // 「值块写入」判定：赋值目标引用值块映射栈中的目标局部（合成
        // 局部 .sN 只被值块写入与短路/if/switch 表达式结果使用——后
        // 两者不在映射栈上，互不混淆）。continuation 织入多个分支时
        // 语句节点对象共享（Lowered 节点无父链、不可变，发射期各分支
        // 块独立展开为指令文本）。
        // 例：{ if (c) { return@_ 1 }  x = 2  return@_ x }
        //   ⇒ { if (c) { v = 1 } else { x = 2; v = x } }
        public static void TransformStatements(List<LoweredStatement> statements,
            LowerContext ctx, LowerEnvironment env)
        {
            TransformWithContinuation(statements, new List<LoweredStatement>(), ctx, env);
        }

        // continuation = 本块结束后要执行的语句序列（块外 continuation；
        // 就地编织，见 TransformStatements 注释）
        private static void TransformWithContinuation(List<LoweredStatement> statements,
            List<LoweredStatement> continuation, LowerContext ctx, LowerEnvironment env)
        {
            for (int i = 0; i < statements.Count; i++)
            {
                switch (statements[i])
                {
                    case LoweredAssignmentStatement write when IsValueBlockWrite(write, ctx):
                        // 值块写入终止本路径：同块其后语句不可达，截断；
                        // continuation 同样不可达，不接收
                        statements.RemoveRange(i + 1, statements.Count - i - 1);
                        return;
                    case LoweredThrowStatement:
                        // throw 终止本路径（BIL §16.9 真终止）：同截断
                        statements.RemoveRange(i + 1, statements.Count - i - 1);
                        return;
                    case LoweredSeqExitStatement exit:
                        // return@语句seq（M61）：命中本层（seq 目标栈栈顶）
                        // 即消费（自身一并删除）；命中外层保留标记向上传播；
                        // 两种情形其后语句均不可达（终止本路径）
                        var consumeHere = ctx.Targets.IsCurrentSeqTarget(exit.Target);
                        statements.RemoveRange(consumeHere ? i : i + 1,
                            statements.Count - (consumeHere ? i : i + 1));
                        return;
                    case LoweredIfStatement ifStatement:
                    {
                        if (!HasTerminatingPath(ifStatement.TrueBlock, ctx)
                            && (ifStatement.FalseBlock == null
                                || !HasTerminatingPath(ifStatement.FalseBlock, ctx)))
                        {
                            break;    // 无终止路径：continuation 留原位（保形）
                        }
                        var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                        statements.RemoveRange(i + 1, statements.Count - i - 1);
                        rest.AddRange(continuation);
                        var trueBlock = BlockTerminates(ifStatement.TrueBlock, ctx)
                            ? WeaveContinuation(ifStatement.TrueBlock,
                                new List<LoweredStatement>(), ctx, env)
                            : WeaveContinuation(ifStatement.TrueBlock, rest, ctx, env);
                        LoweredBlock? falseBlock;
                        if (ifStatement.FalseBlock != null)
                        {
                            falseBlock = BlockTerminates(ifStatement.FalseBlock, ctx)
                                ? WeaveContinuation(ifStatement.FalseBlock,
                                    new List<LoweredStatement>(), ctx, env)
                                : WeaveContinuation(ifStatement.FalseBlock, rest, ctx, env);
                        }
                        else
                        {
                            // 无 else：不终止的 false 路径需要 continuation 时新建 else 块
                            falseBlock = rest.Count > 0
                                ? WeaveContinuation(
                                    new LoweredBlock(ifStatement.Origin,
                                        new List<LoweredStatement>()), rest, ctx, env)
                                : null;
                        }
                        statements[i] = new LoweredIfStatement(ifStatement.Origin,
                            ifStatement.Condition, trueBlock, falseBlock);
                        return;    // 结构语句成为块内最后一条
                    }
                    case LoweredSwitch switchStatement:
                    {
                        if (!switchStatement.Cases.Any(c => HasTerminatingPath(c.Body, ctx))
                            && !HasTerminatingPath(switchStatement.DefaultBody, ctx))
                        {
                            break;    // 无终止路径：continuation 留原位（保形）
                        }
                        var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                        statements.RemoveRange(i + 1, statements.Count - i - 1);
                        rest.AddRange(continuation);
                        var cases = switchStatement.Cases
                            .Select(c => new LoweredSwitchCase(c.Origin, c.Value,
                                BlockTerminates(c.Body, ctx)
                                    ? WeaveContinuation(c.Body, new List<LoweredStatement>(),
                                        ctx, env)
                                    : WeaveContinuation(c.Body, rest, ctx, env)))
                            .ToList();
                        var defaultBody = BlockTerminates(switchStatement.DefaultBody, ctx)
                            ? WeaveContinuation(switchStatement.DefaultBody,
                                new List<LoweredStatement>(), ctx, env)
                            : WeaveContinuation(switchStatement.DefaultBody, rest, ctx, env);
                        statements[i] = new LoweredSwitch(switchStatement.Origin,
                            switchStatement.Selector, cases, defaultBody,
                            switchStatement.BreakId);
                        return;
                    }
                    case LoweredBlock nested:
                    {
                        if (!HasTerminatingPath(nested, ctx))
                        {
                            break;    // 无终止路径：continuation 留原位（保形）
                        }
                        var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                        statements.RemoveRange(i + 1, statements.Count - i - 1);
                        rest.AddRange(continuation);
                        statements[i] = WeaveContinuation(nested, rest, ctx, env);
                        return;
                    }
                    case LoweredSeqBlock seqBlock:
                    {
                        // seq 块与 LoweredBlock 同构透明（S7e）：体编织后重建
                        if (!HasTerminatingPath(seqBlock.Body, ctx))
                        {
                            break;    // 无终止路径：continuation 留原位（保形）
                        }
                        var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                        statements.RemoveRange(i + 1, statements.Count - i - 1);
                        rest.AddRange(continuation);
                        statements[i] = new LoweredSeqBlock(seqBlock.Origin,
                            WeaveContinuation(seqBlock.Body, rest, ctx, env), seqBlock.IsVolatile);
                        return;
                    }
                    case LoweredTryStatement tryStatement:
                    {
                        // try 编织（S7e 定稿规则）：
                        // - finally 自身终止（含值块写入/throw，或末语句
                        //   ret——BIL 的 finally 全路径执行，其终止覆盖
                        //   所有路径）→ continuation 全丢弃；
                        // - 有 finally 且 try/catch 分支含终止路径且
                        //   continuation 非空 → 拦截（P4 Error，S7e
                        //   技术债）：BIL 的 finally 执行后必落到
                        //   continuation，无法表达「终止路径跳过
                        //   continuation」；
                        // - 其余（无 finally 或 continuation 为空）→
                        //   同 if 规则编织进每个不终止分支末端
                        var branchTerminates = HasTerminatingPath(tryStatement.TryBlock, ctx)
                            || tryStatement.Catches.Any(c => HasTerminatingPath(c.Body, ctx));
                        var finallyTerminates = tryStatement.FinallyBlock != null
                            && (HasTerminatingPath(tryStatement.FinallyBlock, ctx)
                                || tryStatement.FinallyBlock.Statements.Count > 0
                                && tryStatement.FinallyBlock.Statements[^1]
                                    is LoweredReturnStatement);
                        if (!branchTerminates && !finallyTerminates)
                        {
                            break;    // 无终止路径：continuation 留原位（保形）
                        }
                        var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                        statements.RemoveRange(i + 1, statements.Count - i - 1);
                        rest.AddRange(continuation);
                        if (finallyTerminates)
                        {
                            // finally 终止覆盖：continuation 全丢弃，节点原样保留
                            return;
                        }
                        if (tryStatement.FinallyBlock != null && rest.Count > 0)
                        {
                            env.Error(tryStatement.Origin.Syntax.Span,
                                "P4: value block weaving across try-finally with " +
                                "partial termination is not supported yet (S7e)");
                            ctx.TransformFailed = true;
                            return;
                        }
                        var newTryBlock = BlockTerminates(tryStatement.TryBlock, ctx)
                            ? WeaveContinuation(tryStatement.TryBlock,
                                new List<LoweredStatement>(), ctx, env)
                            : WeaveContinuation(tryStatement.TryBlock, rest, ctx, env);
                        var newCatches = tryStatement.Catches
                            .Select(c => new LoweredTryCatch(c.Origin, c.Variable,
                                c.ExceptionType,
                                BlockTerminates(c.Body, ctx)
                                    ? WeaveContinuation(c.Body, new List<LoweredStatement>(),
                                        ctx, env)
                                    : WeaveContinuation(c.Body, rest, ctx, env)))
                            .ToList();
                        statements[i] = new LoweredTryStatement(tryStatement.Origin,
                            newTryBlock, newCatches, tryStatement.FinallyBlock,
                            tryStatement.ExceptionSlot);
                        return;
                    }
                }
            }
            // 块内无待编织结构：continuation 原样接到块尾
            statements.AddRange(continuation);
        }

        // 把 continuation 织入块内（递归转换，产物为新建块）
        private static LoweredBlock WeaveContinuation(LoweredBlock block,
            List<LoweredStatement> continuation, LowerContext ctx, LowerEnvironment env)
        {
            var statements = new List<LoweredStatement>(block.Statements);
            TransformWithContinuation(statements, continuation, ctx, env);
            return new LoweredBlock(block.Origin, statements);
        }

        // 「块内所有路径都不会落到块尾」判定：末语句是值块写入/throw →
        // true；末语句是双分支都终止的 LoweredIfStatement / 全部分支体
        // （含 default）都终止的 LoweredSwitch → true；末语句是嵌套
        // LoweredBlock → 递归；其余 false（LoweredReturnStatement 函数
        // 返回与 LoweredLoopControl 真跳转不参与——其后语句在 BIL 块内
        // 本就不可达，无需编织介入）
        private static bool BlockTerminates(LoweredBlock block, LowerContext ctx)
        {
            if (block.Statements.Count == 0) return false;
            return block.Statements[^1] switch
            {
                LoweredAssignmentStatement assignment => IsValueBlockWrite(assignment, ctx),
                LoweredThrowStatement => true,
                LoweredSeqExitStatement => true,    // return@语句seq（M61）终止本路径
                LoweredIfStatement ifStatement => ifStatement.FalseBlock != null
                    && BlockTerminates(ifStatement.TrueBlock, ctx)
                    && BlockTerminates(ifStatement.FalseBlock, ctx),
                LoweredSwitch switchStatement =>
                    switchStatement.Cases.All(c => BlockTerminates(c.Body, ctx))
                    && BlockTerminates(switchStatement.DefaultBody, ctx),
                // S7e：seq 透明递归；try——finally 终止覆盖所有路径，
                // 否则 try 与全部 catch 都终止（未捕获异常穿透即终止，
                // 无 catch 时只剩 try 正常完成路径——单块判定）
                LoweredSeqBlock seqBlock => BlockTerminates(seqBlock.Body, ctx),
                LoweredTryStatement tryStatement =>
                    (tryStatement.FinallyBlock != null
                        && BlockTerminates(tryStatement.FinallyBlock, ctx))
                    || (BlockTerminates(tryStatement.TryBlock, ctx)
                        && tryStatement.Catches.All(c => BlockTerminates(c.Body, ctx))),
                LoweredBlock nested => BlockTerminates(nested, ctx),
                _ => false,
            };
        }

        // 「块内含不落到块尾的路径」判定（编织必要性闸门）：值块写入或
        // throw 出现即 true（递归 if/switch/嵌套块/seq/try 三分支——
        // S7e）；全无终止路径的结构语句无需编织，保持旧形态
        private static bool HasTerminatingPath(LoweredBlock block, LowerContext ctx)
        {
            foreach (var statement in block.Statements)
            {
                switch (statement)
                {
                    case LoweredAssignmentStatement assignment:
                        if (IsValueBlockWrite(assignment, ctx)) return true;
                        break;
                    case LoweredThrowStatement:
                        return true;
                    case LoweredSeqExitStatement:
                        // return@语句seq（M61）终止本路径
                        return true;
                    case LoweredIfStatement ifStatement:
                        if (HasTerminatingPath(ifStatement.TrueBlock, ctx)
                            || (ifStatement.FalseBlock != null
                                && HasTerminatingPath(ifStatement.FalseBlock, ctx)))
                        {
                            return true;
                        }
                        break;
                    case LoweredSwitch switchStatement:
                        if (switchStatement.Cases.Any(c => HasTerminatingPath(c.Body, ctx))
                            || HasTerminatingPath(switchStatement.DefaultBody, ctx))
                        {
                            return true;
                        }
                        break;
                    case LoweredBlock nested:
                        if (HasTerminatingPath(nested, ctx)) return true;
                        break;
                    case LoweredSeqBlock seqBlock:
                        if (HasTerminatingPath(seqBlock.Body, ctx)) return true;
                        break;
                    case LoweredTryStatement tryStatement:
                        if (HasTerminatingPath(tryStatement.TryBlock, ctx)
                            || tryStatement.Catches.Any(c => HasTerminatingPath(c.Body, ctx))
                            || (tryStatement.FinallyBlock != null
                                && HasTerminatingPath(tryStatement.FinallyBlock, ctx)))
                        {
                            return true;
                        }
                        break;
                }
            }
            return false;
        }

        private static bool IsValueBlockWrite(LoweredAssignmentStatement assignment,
            LowerContext ctx)
        {
            if (assignment.Target is not LoweredValueReferenceExpression reference)
            {
                return false;
            }
            return ctx.Targets.IsValueBlockTarget(reference.Symbol);
        }

        // 「语句序列含 LoweredSeqExit 标记」判定（M61，seq 语句降级的
        // 编织闸门）：递归 if/switch/嵌套块/seq/try 三分支；LoweredLoop
        // 不递归——隔循环 return@seq 已由 P3 拦截，循环体内 exit 的目标
        // 必在循环内（随循环体降级消费）
        public static bool ContainsSeqExit(IReadOnlyList<LoweredStatement> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case LoweredSeqExitStatement:
                        return true;
                    case LoweredIfStatement ifStatement:
                        if (ContainsSeqExit(ifStatement.TrueBlock.Statements)
                            || (ifStatement.FalseBlock != null
                                && ContainsSeqExit(ifStatement.FalseBlock.Statements)))
                        {
                            return true;
                        }
                        break;
                    case LoweredSwitch switchStatement:
                        if (switchStatement.Cases.Any(c => ContainsSeqExit(c.Body.Statements))
                            || ContainsSeqExit(switchStatement.DefaultBody.Statements))
                        {
                            return true;
                        }
                        break;
                    case LoweredBlock nested:
                        if (ContainsSeqExit(nested.Statements)) return true;
                        break;
                    case LoweredSeqBlock seqBlock:
                        if (ContainsSeqExit(seqBlock.Body.Statements)) return true;
                        break;
                    case LoweredTryStatement tryStatement:
                        if (ContainsSeqExit(tryStatement.TryBlock.Statements)
                            || tryStatement.Catches.Any(c => ContainsSeqExit(c.Body.Statements))
                            || (tryStatement.FinallyBlock != null
                                && ContainsSeqExit(tryStatement.FinallyBlock.Statements)))
                        {
                            return true;
                        }
                        break;
                }
            }
            return false;
        }
    }
}
