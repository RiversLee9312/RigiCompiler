namespace RigiCompiler
{
    // 值块壳的包装（壳协议 TShell 实参）：BoundValueBlock 壳 + 诊断构造名
    // （"if expression"/"switch expression"）——避免为两种调用方各建一个 visitor
    internal sealed class ValueBlockShell
    {
        public ValueBlockShell(BoundValueBlock block, string construct,
            bool allowImplicitValue = true)
        {
            Block = block;
            Construct = construct;
            AllowImplicitValue = allowImplicitValue;
        }

        public BoundValueBlock Block { get; }

        public string Construct { get; }

        public bool AllowImplicitValue { get; }
    }

    // if 语句（S7b，SYNTAX §7.1）：else if 链包成单语句 BoundBlock（Bound 层
    // 双分支形态）。definite assignment 分支合并：before ∪ (setT ∩ setF)；
    // 无 else 合并为 before。S8b 收窄维度（SYNTAX §3.5）：分支入口
    // = before ∪ 条件真/假边事实；guard——一分支终止（GuaranteesReturn）时
    // 后续收窄 = 对边流；都不终止时取纯交集（无 else 时假边流即 before
    // 自身——then 尾 ∩ before：before 中被 then 体内赋值失效的键不得
    // 复活，与双分支合并同规则）；DA 规则不因 guard 改变（行为零变化）。
    // 自旧 BindSession.BindIfStatement 迁移，行为不变。
    internal sealed class IfStatementVisitor : BinderVisitor<IfStatementVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var ifNode = (IfStatementASTNode)node;
            var condition = ExpressionDispatcher.Visit(ifNode.Condition.Expression, scope, ctx, env);
            Conditions.CheckBool(ifNode.Condition, ifNode.Span, condition, "if", env);
            var facts = ConditionFactsExtractor.Extract(condition, ctx.Frame);
            var before = ctx.Flow.Snapshot();
            var beforeNarrowed = ctx.Flow.SnapshotNarrowed();
            // then 入口 = before ∪ 真边事实
            ctx.Flow.ApplyNarrow(facts.True);
            var trueBlock = BlockDispatcher.Visit(ifNode.ThenBlock, scope, ctx, env);
            var trueAssigned = ctx.Flow.Snapshot();
            var trueNarrowed = ctx.Flow.SnapshotNarrowed();
            BoundBlock? falseBlock = null;
            HashSet<LocalSymbol>? falseAssigned = null;
            Dictionary<NarrowKey, TypeSymbol>? falseNarrowed = null;
            ctx.Flow.Restore(before);
            ctx.Flow.RestoreNarrowed(beforeNarrowed);
            switch (ifNode.ElseBranch)
            {
                case null:
                    break;
                case CodeBlockASTNode elseBlock:
                    // else 入口 = before ∪ 假边事实
                    ctx.Flow.ApplyNarrow(facts.False);
                    falseBlock = BlockDispatcher.Visit(elseBlock, scope, ctx, env);
                    falseAssigned = ctx.Flow.Snapshot();
                    falseNarrowed = ctx.Flow.SnapshotNarrowed();
                    break;
                case IfStatementASTNode elseIf:
                    // else if 链：递归绑定，包成单语句 BoundBlock（同假边上下文）
                    ctx.Flow.ApplyNarrow(facts.False);
                    var nested = IfStatementVisitor.Visit(elseIf, scope, ctx, env);
                    var statements = new List<BoundStatement>();
                    if (nested != null) statements.Add(nested);
                    falseBlock = new BoundBlock(elseIf, statements);
                    falseAssigned = ctx.Flow.Snapshot();
                    falseNarrowed = ctx.Flow.SnapshotNarrowed();
                    break;
                default:
                    throw new CompilerInternalException(
                        "未知 else 分支节点: " + ifNode.ElseBranch.GetType().Name);
            }
            // 合并：DA 规则不变（双分支 before∪(setT∩setF)；无 else 保守 before）；
            // 收窄按 guard/交集规则
            if (falseAssigned == null)
            {
                ctx.Flow.Restore(before);
                if (BoundAnalysis.GuaranteesReturn(trueBlock))
                {
                    // guard：then 终止 → 后续收窄 = before ∪ 假边
                    ctx.Flow.RestoreNarrowed(beforeNarrowed);
                    ctx.Flow.ApplyNarrow(facts.False);
                }
                else
                {
                    // 无 else 非 guard：后续 = then 尾 ∩ before（纯交集）——
                    // before 中被 then 体内赋值失效的键不得复活（直接恢复
                    // before 会把已失效的收窄带回来，是不 sound 的）
                    ctx.Flow.MergeNarrowed(trueNarrowed, beforeNarrowed);
                }
            }
            else
            {
                ctx.Flow.MergeIfBranches(before, trueAssigned, falseAssigned);
                var trueTerminates = BoundAnalysis.GuaranteesReturn(trueBlock);
                var falseTerminates = BoundAnalysis.GuaranteesReturn(falseBlock!);
                if (trueTerminates && !falseTerminates)
                {
                    // then 终止 → 后续收窄 = else 边流（假边 + else 体内效果）
                    ctx.Flow.RestoreNarrowed(falseNarrowed!);
                }
                else if (!trueTerminates && falseTerminates)
                {
                    // else 终止 → 后续收窄 = then 边流
                    ctx.Flow.RestoreNarrowed(trueNarrowed);
                }
                else if (trueTerminates && falseTerminates)
                {
                    // 双终止：后续不可达，保守 before
                    ctx.Flow.RestoreNarrowed(beforeNarrowed);
                }
                else
                {
                    // 都不终止：纯交集（同键同类型才保留）
                    ctx.Flow.MergeNarrowed(trueNarrowed, falseNarrowed!);
                }
            }
            if (condition == null) return null;
            return new BoundIfStatement(node, condition, trueBlock, falseBlock);
        }
    }

    // if 表达式（S7b，SYNTAX §7.1）：必须有 else（前端保证）；两分支各绑
    // 一个值块（标签同源——if 表达式的 named 标签或缺省 "_"），产值类型
    // 统一（符号 ==；ErrorType 毒化静默），纯穿透分支（ValueType null）
    // 不参与统一；definite assignment 合并规则同 if 语句。
    // 自旧 BindSession.BindIfExpression 迁移，行为不变。
    internal sealed class IfExpressionVisitor : ExpressionVisitor<IfExpressionVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var ifNode = (IfExpressionASTNode)node;
            var condition = ExpressionDispatcher.Visit(ifNode.Condition.Expression, scope, ctx, env);
            Conditions.CheckBool(ifNode.Condition, ifNode.Span, condition, "if", env);
            var facts = ConditionFactsExtractor.Extract(condition, ctx.Frame);
            var label = ifNode.Label ?? "_";
            var before = ctx.Flow.Snapshot();
            var beforeNarrowed = ctx.Flow.SnapshotNarrowed();
            // 分支入口收窄（S8b）：真/假边事实；表达式无「后续语句」区域，
            // 无 guard——合并恒为纯交集
            ctx.Flow.ApplyNarrow(facts.True);
            var trueBranch = BindBranch(ifNode.ThenBody, label, scope, ctx, env);
            var trueAssigned = ctx.Flow.Snapshot();
            var trueNarrowed = ctx.Flow.SnapshotNarrowed();
            ctx.Flow.Restore(before);
            ctx.Flow.RestoreNarrowed(beforeNarrowed);
            ctx.Flow.ApplyNarrow(facts.False);
            var falseBranch = BindBranch(ifNode.ElseBody, label, scope, ctx, env);
            var falseAssigned = ctx.Flow.Snapshot();
            var falseNarrowed = ctx.Flow.SnapshotNarrowed();
            ctx.Flow.MergeIfBranches(before, trueAssigned, falseAssigned);
            ctx.Flow.MergeNarrowed(trueNarrowed, falseNarrowed);
            if (condition == null) return null;
            // 产值类型统一：纯穿透分支（null）不参与；两分支都穿透即无产值
            var type = trueBranch.ValueType ?? falseBranch.ValueType;
            if (type == null)
            {
                env.Error(ifNode.Span, "if expression must produce a value " +
                    "(at least one branch must return@ a value)");
                return null;
            }
            if (trueBranch.ValueType != null && falseBranch.ValueType != null
                && !ReferenceEquals(trueBranch.ValueType, falseBranch.ValueType)
                && trueBranch.ValueType is not ErrorTypeSymbol
                && falseBranch.ValueType is not ErrorTypeSymbol)
            {
                env.Error(ifNode.Span,
                    $"if expression branches produce different types " +
                    $"('{BoundAnalysis.TypeDisplay(trueBranch.ValueType)}' and " +
                    $"'{BoundAnalysis.TypeDisplay(falseBranch.ValueType)}')");
                return null;
            }
            return new BoundIfExpression(node, condition, trueBranch, falseBranch, type);
        }

        private static BoundValueBlock BindBranch(CodeBlockASTNode node, string label, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            var shell = new ValueBlockShell(new BoundValueBlock(node, label), "if expression");
            ValueBlockVisitor.VisitInto(node, scope, shell, ctx, env);
            return shell.Block;
        }
    }

    // 值块绑定（S7b，SYNTAX §6.1；壳填充协议首验）：
    // - 施工壳先于分支体绑定创建并由调用方传入，Enter 压入值块标签栈
    //   （分支体内的 return@标签 经栈命中），Exit 弹栈（finally 配对）；
    // - 语法上恰好一条纯表达式语句（赋值语句不算）→ 隐式取值，
    //   ValueType = 该表达式类型；
    // - 否则所有执行路径必须显式 return@（GuaranteesValueReturn 检查，
    //   穿透终止也算路径终止），ValueType = 命中本块的 return@ 值类型
    //   统一结果；无本块产值（纯穿透）→ ValueType = null。
    // 自旧 BindSession.BindValueBlock 迁移，行为不变。
    internal sealed class ValueBlockVisitor
        : BinderShellVisitor<ValueBlockVisitor, ValueBlockShell, BindContext>
    {
        protected override void Enter(ASTNode node, Scope scope, ValueBlockShell shell,
            BindContext ctx, BindEnvironment env)
        {
            ctx.Labels.PushValueBlock(shell.Block);
        }

        protected override void Exit(ASTNode node, Scope scope, ValueBlockShell shell,
            BindContext ctx, BindEnvironment env)
        {
            ctx.Labels.PopValueBlock();
        }

        protected override void VisitCoreInto(ASTNode node, Scope scope, ValueBlockShell shell,
            BindContext ctx, BindEnvironment env)
        {
            var blockNode = (CodeBlockASTNode)node;
            var block = BlockDispatcher.Visit(blockNode, scope, ctx, env);
            shell.Block.Block = block;
            // M33 判定：语法上恰好一条纯表达式语句
            if (shell.AllowImplicitValue && blockNode.Statements.Count == 1
                && blockNode.Statements[0] is ExpressionStatementASTNode { AssignValue: null })
            {
                shell.Block.IsImplicitValue = true;
                // 绑定失败（产物缺失）时诊断已发，静默留 null ValueType；
                // void 调用落成 BoundCallStatement——无值可取
                if (block.Statements.Count == 1
                    && block.Statements[0] is BoundExpressionStatement expressionStatement)
                {
                    if (expressionStatement.Expression is BoundAwaitExpression { HasResult: false })
                    {
                        env.Error(node.Span, $"{shell.Construct} branch must produce a value " +
                            "(an await of core.coroutine.Task has no result)");
                        return;
                    }
                    shell.Block.ValueType = expressionStatement.Expression.Type;
                }
                else if (block.Statements.Count == 1
                    && block.Statements[0] is BoundCallStatement)
                {
                    env.Error(node.Span, $"{shell.Construct} branch must produce a value " +
                        "(a void call has no result)");
                }
                return;
            }
            if (!BoundAnalysis.GuaranteesValueReturn(block))
            {
                var article = "aeiou".Contains(shell.Construct[0]) ? "an" : "a";
                env.Error(node.Span, $"All code paths of {article} {shell.Construct} branch must " +
                    "explicitly return@ a value");
            }
            shell.Block.ValueType = BoundAnalysis.CollectBranchValueType(block, shell.Block,
                shell.Construct, env);
        }
    }

    // 条件共享设施
    internal static class Conditions
    {
        // 条件必须 bool（if 语句/表达式、循环同规则；ErrorType 毒化静默）
        public static void CheckBool(ExpressionRootASTNode conditionRoot, CharRange? fallbackSpan,
            BoundExpression? condition, string construct, BindEnvironment env)
        {
            if (condition != null && condition.Type is not ErrorTypeSymbol
                && !ReferenceEquals(condition.Type, env.B.Bool))
            {
                env.Error(conditionRoot.Span ?? fallbackSpan,
                    $"{construct} condition must be bool " +
                    $"(got '{BoundAnalysis.TypeDisplay(condition.Type)}')");
            }
        }
    }
}
