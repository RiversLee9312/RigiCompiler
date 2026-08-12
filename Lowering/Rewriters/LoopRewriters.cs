namespace LatteCompiler
{
    // 循环降级（S7c-1 while/do-while；S7c-2 for 脱糖，SYNTAX §7.3；
    // BIL §16.3/§16.4）。自旧 LowerSession.LowerLoop/LowerForLoop/
    // FindLoopBreakId 迁移，行为不变。

    // 循环映射查找设施
    internal static class LoopFacility
    {
        // BoundLoopControl.Target 经引用查循环映射栈得 BreakId；未命中
        // 即内部错误（P3 已保证目标循环包含该语句，降级上下文必在栈上）
        public static LocalSymbol FindBreakId(BoundLoop target, LowerContext ctx)
        {
            return ctx.Targets.FindLoopBreakId(target)
                ?? throw new CompilerInternalException(
                    "break/continue 目标循环不在降级上下文内（P3 已保证目标包含语句）");
        }
    }

    // while/do-while 降级：合成 bool 条件局部（.sN）与 .breakid 局部
    // （.bN）；条件表达式在独立块上下文降级并写条件局部，产物即 Judge 块
    // （前置语句随块走——条件内短路/if 表达式的展开自然落在 Judge 内）；
    // 映射压栈（Enter）后降级 Body（体内 break/continue 经引用命中本
    // 循环），Exit 弹栈。仅 While/DoWhile 路径——
    // Condition 恒非空（For 走 ForLoopRewriter 脱糖，不经此）
    internal sealed class LoopRewriter : LoweredVisitor<LoopRewriter, LoweredStatement, LowerContext>
    {
        private LocalSymbol condition = null!;
        private LocalSymbol breakId = null!;

        protected override void Enter(BoundNode node, LowerContext ctx, LowerEnvironment env)
        {
            // 合成顺序与旧代码一致（.sN 先于 .bN——SynthLocals 顺序即 .vars
            // 发射顺序，BIL 文本快照敏感）；压栈提前到条件降级前无语义影响
            // （条件表达式内不可能有指向本循环的 BoundLoopControl——P3 不变量）
            condition = ctx.Synth.NewSynthLocal(((BoundLoop)node).Condition!.Type);
            breakId = ctx.Synth.NewBreakIdLocal();
            ctx.Targets.PushLoop((BoundLoop)node, breakId);
        }

        protected override void Exit(BoundNode node, LowerContext ctx, LowerEnvironment env)
        {
            ctx.Targets.PopLoop();
        }

        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var loop = (BoundLoop)node;
            var judge = ExpressionFacility.LowerAssignInNewBlock(loop, loop.Condition!, condition,
                ctx, env);
            if (judge == null) return null;
            var body = LowerBlockVisitor.Visit(loop.Body, ctx, env);
            if (body == null) return null;
            return new LoweredLoop(loop, loop.Kind == LoopKind.DoWhile,
                judge, condition, body, breakId);
        }
    }

    // for 脱糖（S7c-2，SYNTAX §7.3 for-each 协议）：
    //   前置（当前块）：.e = <iterable 降级>.iterate()
    //   LoweredLoop{ IsRev=false,
    //     Judge = [ .c = .e.moveNext() ]（条件局部 .c 为合成 bool），
    //     Body = [ LoopVariable = .e.current(); <体降级> ],
    //     BreakId = .bN }
    // 产物复用 LoweredLoop——P4b 零新增。协议三方法符号与元素类型
    // 取 P3 挂在 BoundLoop 上的产物（P4 不做名字分析）；枚举器局部
    // 类型 = GetConstructedType(IEnumerator 定义, TItem)——定义经
    // MoveNextMethod.Owner 取（接口方法宿主编译期即定）
    internal sealed class ForLoopRewriter
        : LoweredVisitor<ForLoopRewriter, LoweredStatement, LowerContext>
    {
        private LocalSymbol enumerator = null!;
        private LocalSymbol condition = null!;
        private LocalSymbol breakId = null!;

        protected override void Enter(BoundNode node, LowerContext ctx, LowerEnvironment env)
        {
            // 合成顺序与旧代码一致（enumerator/condition 的 .sN 先于 breakId
            // 的 .bN——SynthLocals 顺序即 .vars 发射顺序，BIL 文本快照敏感）；
            // 压栈提前到 iterable 降级前无语义影响（iterable 内不可能有指向
            // 本循环的 BoundLoopControl——P3 不变量）
            var loop = (BoundLoop)node;
            var itemType = loop.LoopVariable!.Type!;
            var enumeratorDef = (TypeSymbol)loop.MoveNextMethod!.Owner!;
            var enumeratorType = env.Unit.Symbols.GetConstructedType(enumeratorDef, itemType);
            enumerator = ctx.Synth.NewSynthLocal(enumeratorType);
            condition = ctx.Synth.NewSynthLocal((TypeSymbol)loop.MoveNextMethod.ReturnType!);
            breakId = ctx.Synth.NewBreakIdLocal();
            ctx.Targets.PushLoop(loop, breakId);
        }

        protected override void Exit(BoundNode node, LowerContext ctx, LowerEnvironment env)
        {
            ctx.Targets.PopLoop();
        }

        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var loop = (BoundLoop)node;
            // P3 已保证 For 路径字段齐备（BoundLoop 注释的形态互斥约定）
            var loopVariable = loop.LoopVariable!;
            var itemType = loopVariable.Type!;
            var enumeratorType = (TypeSymbol)enumerator.Type!;
            var moveNextType = (TypeSymbol)loop.MoveNextMethod!.ReturnType!;
            // 前置：.e = <iterable>.iterate()（合成节点 Origin 指 for 语句）
            var iterable = LowerExpressionDispatcher.Visit(loop.Iterable!, ctx, env);
            if (iterable == null) return null;
            ctx.Output.Add(new LoweredAssignmentStatement(loop,
                SynthLocalFactory.ReferenceTo(loop, enumerator),
                new LoweredInstanceCallExpression(loop, iterable, loop.IterateMethod!,
                    new List<LoweredExpression>(), enumeratorType)));
            // Judge：.c = .e.moveNext()
            var judge = new LoweredBlock(loop, new List<LoweredStatement>
            {
                new LoweredAssignmentStatement(loop, SynthLocalFactory.ReferenceTo(loop, condition),
                    new LoweredInstanceCallExpression(loop,
                        SynthLocalFactory.ReferenceTo(loop, enumerator),
                        loop.MoveNextMethod, new List<LoweredExpression>(), moveNextType)),
            });
            // Body：头 = LoopVariable = .e.current()，其后体降级语句。
            // 被 lambda 捕获时每迭代新 cell（C#5 foreach 语义，SYNTAX §5.2/§7.3）：
            // loopVar = new ..cell..UUID(.e.current())——Body 头每轮执行即新 cell
            var body = LowerBlockVisitor.Visit(loop.Body, ctx, env);
            if (body == null) return null;
            LoweredExpression currentValue = new LoweredInstanceCallExpression(loop,
                SynthLocalFactory.ReferenceTo(loop, enumerator),
                loop.CurrentMethod!, new List<LoweredExpression>(), itemType);
            if (loopVariable.CellStorage is { } storage)
            {
                var wrapperArgs = CellWrappedNew.LowerWrapperInitArgs(loop, storage, ctx, env);
                currentValue = new LoweredNewExpression(loop, storage.ValueInit,
                    new List<LoweredExpression> { currentValue }, storage.CellType, wrapperArgs);
            }
            var bodyStatements = new List<LoweredStatement>
            {
                new LoweredAssignmentStatement(loop,
                    SynthLocalFactory.ReferenceTo(loop, loopVariable), currentValue),
            };
            bodyStatements.AddRange(body.Statements);
            return new LoweredLoop(loop, isRev: false, judge, condition,
                new LoweredBlock(loop.Body, bodyStatements), breakId);
        }
    }
}
