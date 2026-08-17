namespace RigiCompiler
{
    // 值块降级（S7b 起；Stage B 重构：continuation 编织设施
    // （ValueBlockFacility：TransformStatements/WeaveContinuation 等）
    // 全部删除——return@ 降级为 LoweredStructuredExit 标记，展开归
    // P4a 末尾的 StructuredExitRouting normalization pass）。

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
    // （if/switch/seq 表达式脱糖）给定；输出列表压栈（Enter）/弹栈
    // （Exit，finally 配对）收集体内前置语句。
    // - 隐式取值 → 单语句 v = expr；
    // - 显式：语句流降级，return@ ⇒ LoweredStructuredExit 标记
    //   （ReturnValueRewriter），同块其后语句丢弃（不可达死代码，
    //   P3 已保证路径必终止；StructuredExitRouting 展开时同样截断）
    internal sealed class ValueBlockRewriter
        : LoweredVisitor<ValueBlockRewriter, LoweredBlock, ValueBlockContext>
    {
        protected override void Enter(BoundNode node, ValueBlockContext valueCtx,
            LowerEnvironment env)
        {
            valueCtx.Context.Output.Push();
        }

        protected override void Exit(BoundNode node, ValueBlockContext valueCtx,
            LowerEnvironment env)
        {
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
            }
            return new LoweredBlock(valueBlock.Block, statements);
        }
    }
}
