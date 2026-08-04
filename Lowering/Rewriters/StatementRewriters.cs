namespace LatteCompiler
{
    // 语句降级（S5–S7e 恒等降级为主）。自旧 LowerSession.LowerStatement
    // 各分支迁移，行为不变。

    // 局部声明：初始化表达式降级 + cast 物化（BIL §6.5）
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
                callReceiver = LowerExpressionDispatcher.Visit(call.Receiver, ctx, env);
                if (callReceiver == null) return null;
                callReceiver = LoweringFacility.EnsureDeclaredType(call, callReceiver,
                    call.Method.Owner);
            }
            return new LoweredCallStatement(call, call.Method, arguments, callReceiver);
        }
    }

    // 赋值：目标与值递归降级 + 值 cast 物化（BIL §6.5）
    internal sealed class AssignmentRewriter
        : LoweredVisitor<AssignmentRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var assignment = (BoundAssignmentStatement)node;
            var target = LowerExpressionDispatcher.Visit(assignment.Target, ctx, env);
            var value = LowerExpressionDispatcher.Visit(assignment.Value, ctx, env);
            if (target == null || value == null) return null;
            value = LoweringFacility.EnsureDeclaredType(assignment, value, assignment.Target.Type);
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

    // throw 恒等降级（S7d，BIL §16.9 直接对应）
    internal sealed class ThrowRewriter
        : LoweredVisitor<ThrowRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var throwStatement = (BoundThrowStatement)node;
            var thrown = LowerExpressionDispatcher.Visit(throwStatement.Exception, ctx, env);
            if (thrown == null) return null;
            return new LoweredThrowStatement(throwStatement, thrown);
        }
    }

    // seq 语句降级（S7e，BIL §3.4 独立 block + call 化）；M61 起 named
    // seq 降级体期间压 SeqTargets 栈（return@语句seq 的归属比对），
    // 体含 exit 标记时跑 continuation 编织（消费命中本层者、截断传播
    // 外层者；无 exit 直通——零行为变化）。手动压栈收集（OutputStack
    // 元素即可变 List——编织就地变换需要，仿 SwitchRewriters 先例）
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
            ctx.SeqTargets.Push(seqStatement);
            var statements = new List<LoweredStatement>();
            ctx.OutputStack.Push(statements);
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
            }
            finally
            {
                ctx.OutputStack.Pop();
                ctx.SeqTargets.Pop();
            }
            return new LoweredSeqBlock(seqStatement,
                new LoweredBlock(seqStatement.Body, statements), seqStatement.IsVolatile);
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
                LowerContext.ReferenceTo(returnValue, writeTarget), written);
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
}
