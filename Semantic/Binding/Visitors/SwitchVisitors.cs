namespace RigiCompiler
{
    // switch 语句/表达式绑定（S7d，SYNTAX §7.2）。
    // 自旧 BindSession.BindSwitchStatement/BindSwitchExpression/BindSwitchMatch
    // 迁移，行为不变。

    // case 匹配绑定的调用载荷（TContext 实参）：真实 BindContext + 所属
    // switch 的 selector（可空 = 已绑定失败）——避免向 BindContext 塞
    // 「当前 selector」字段（那会退回 session 状态污染）
    internal sealed class SwitchMatchContext
    {
        public SwitchMatchContext(BindContext context, BoundExpression? selector)
        {
            Context = context;
            Selector = selector;
        }

        public BindContext Context { get; }

        public BoundExpression? Selector { get; }
    }

    // switch 语句：selector 先绑（DA 效果保留——selector 必求值一次）；
    // 每 case（匹配表达式 + 分支体）与 default 体各自从 before 快照
    // 出发绑定（前一分支的赋值效果不泄入后一分支），DA 合并
    // before ∪ (∩ 全部分支尾集合)——default 恒存在（Parser 强制），
    // 规则即 if 双分支合并的推广
    internal sealed class SwitchStatementVisitor
        : BinderVisitor<SwitchStatementVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var switchNode = (SwitchStatementASTNode)node;
            var selector = ExpressionDispatcher.Visit(switchNode.Selector.Expression, scope, ctx, env);
            // §7.2/§12：selector 为定义级 enum struct 时，分支体内
            // `.Case` 省略形式以 selector 类型为解析上下文（仅上下文
            // 贡献，分支体期望类型不受影响）；finally 配对弹栈
            var enumContext = EnumCaseContextOf(selector);
            if (enumContext != null) ctx.Labels.PushEnumCaseContext(enumContext);
            try
            {
            var before = ctx.Flow.Snapshot();
            var beforeNarrowed = ctx.Flow.SnapshotNarrowed();
            var cases = new List<BoundSwitchCase>();
            var branchTails = new List<HashSet<LocalSymbol>>();
            var narrowedTails = new List<Dictionary<NarrowKey, TypeSymbol>>();
            foreach (var caseNode in switchNode.Cases)
            {
                ctx.Flow.Restore(before);
                ctx.Flow.RestoreNarrowed(beforeNarrowed);
                var isPattern = SwitchMatchVisitor.IsPattern(caseNode);
                var match = SwitchMatchVisitor.Visit(caseNode, scope,
                    new SwitchMatchContext(ctx, selector), env);
                // Q4 分支体收窄（S8b，SYNTAX §3.5）：`(_ is T)`
                // 分支体内 selector 收窄为 T（selector 为可收窄目标时；
                // 分支体内 `_` 不可用是 §7.2 语义）
                ApplyCaseNarrowing(match, ctx.Frame, ctx.Flow);
                var body = BlockDispatcher.Visit(caseNode.Body, scope, ctx, env);
                branchTails.Add(ctx.Flow.Snapshot());
                narrowedTails.Add(ctx.Flow.SnapshotNarrowed());
                if (match != null)
                {
                    cases.Add(new BoundSwitchCase(caseNode, match, isPattern, body));
                }
            }
            ctx.Flow.Restore(before);
            ctx.Flow.RestoreNarrowed(beforeNarrowed);
            var defaultBody = BlockDispatcher.Visit(RequireDefault(switchNode.DefaultBody), scope,
                ctx, env);
            branchTails.Add(ctx.Flow.Snapshot());
            narrowedTails.Add(ctx.Flow.SnapshotNarrowed());
            ctx.Flow.MergeBranches(before, branchTails);
            ctx.Flow.MergeNarrowedBranches(narrowedTails);
            if (selector == null) return null;
            return new BoundSwitchStatement(node, selector, cases, defaultBody);
            }
            finally
            {
                if (enumContext != null) ctx.Labels.PopEnumCaseContext();
            }
        }

        // §7.2/§12 enum case 解析上下文：selector 静态类型为定义级
        // enum struct 时返回之（分支体内 `.Case` 省略形式的解析上下文）；
        // 否则 null（泛型构造 enum 同使用侧归口口径，不贡献上下文）
        internal static TypeSymbol? EnumCaseContextOf(BoundExpression? selector)
        {
            return selector?.Type is TypeSymbol { ConstructedFrom: null } type
                && type.Kind == TypeKind.EnumStruct ? type : null;
        }

        // `(_ is T)` 分支体收窄：match 剥 SmartCast 壳（外层收窄可能已包装
        // 占位）后是「is + 占位操作数 + 静态目标」形态时，selector 键 → T
        internal static void ApplyCaseNarrowing(BoundExpression? match, BindFunctionFrame frame,
            FlowState flow)
        {
            if (match is not BoundTypeCheckExpression
                { Kind: BoundTypeCheckKind.Is, TargetType: { } narrowedType } typeCheck)
            {
                return;
            }
            var operand = typeCheck.Operand is BoundSmartCastExpression smartCast
                ? smartCast.Operand : typeCheck.Operand;
            if (operand is not BoundSwitchPlaceholderExpression placeholder) return;
            var key = ConditionFactsExtractor.TryKeyOf(placeholder.Selector, frame);
            // S9a：收窄目标为泛型参数时不做静态收窄（判型跳过）
            if (key != null && narrowedType is TypeSymbol narrowed)
            {
                flow.SetNarrow(key, narrowed);
            }
        }

        // switch default 分支体（两形态 Parser 强制存在；缺失即 Parser 不变量破坏）
        internal static CodeBlockASTNode RequireDefault(CodeBlockASTNode? defaultBody)
        {
            return defaultBody ?? throw new CompilerInternalException(
                "switch 缺 default 分支（Parser 不变量破坏）");
        }
    }

    // switch 表达式：分支体（含 default）各绑一个值块（标签同源
    // Label ?? "_"——return@ 命中规则同 if 表达式），产值类型全分支
    // 统一（纯穿透分支不参与；全穿透即无产值；引用不等且非 ErrorType
    // 报不一致）；DA 合并同语句形态
    internal sealed class SwitchExpressionVisitor
        : ExpressionVisitor<SwitchExpressionVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var switchNode = (SwitchExpressionASTNode)node;
            var selector = ExpressionDispatcher.Visit(switchNode.Selector.Expression, scope, ctx, env);
            // §7.2/§12：enum struct selector 的分支体 `.Case` 解析
            // 上下文（同语句形态；仅上下文贡献，分支产值类型仍按
            // 既有全分支统一规则推导）
            var enumContext = SwitchStatementVisitor.EnumCaseContextOf(selector);
            if (enumContext != null) ctx.Labels.PushEnumCaseContext(enumContext);
            try
            {
            var label = switchNode.Label ?? "_";
            var before = ctx.Flow.Snapshot();
            var beforeNarrowed = ctx.Flow.SnapshotNarrowed();
            var cases = new List<BoundSwitchExpressionCase>();
            var branchTails = new List<HashSet<LocalSymbol>>();
            var narrowedTails = new List<Dictionary<NarrowKey, TypeSymbol>>();
            foreach (var caseNode in switchNode.Cases)
            {
                ctx.Flow.Restore(before);
                ctx.Flow.RestoreNarrowed(beforeNarrowed);
                var isPattern = SwitchMatchVisitor.IsPattern(caseNode);
                var match = SwitchMatchVisitor.Visit(caseNode, scope,
                    new SwitchMatchContext(ctx, selector), env);
                // Q4 分支体收窄（同语句形态）
                SwitchStatementVisitor.ApplyCaseNarrowing(match, ctx.Frame, ctx.Flow);
                var shell = new ValueBlockShell(new BoundValueBlock(caseNode.Body, label),
                    "switch expression");
                // 外部期望类型回填（同 if/seq 表达式机制——return@ 值
                // 表达式据其做上下文定型）
                shell.Block.ExpectedType = expectedType;
                ValueBlockVisitor.VisitInto(caseNode.Body, scope, shell, ctx, env);
                branchTails.Add(ctx.Flow.Snapshot());
                narrowedTails.Add(ctx.Flow.SnapshotNarrowed());
                if (match != null)
                {
                    cases.Add(new BoundSwitchExpressionCase(caseNode, match, isPattern, shell.Block));
                }
            }
            ctx.Flow.Restore(before);
            ctx.Flow.RestoreNarrowed(beforeNarrowed);
            var defaultBody = SwitchStatementVisitor.RequireDefault(switchNode.DefaultBody);
            var defaultShell = new ValueBlockShell(new BoundValueBlock(defaultBody, label),
                "switch expression");
            defaultShell.Block.ExpectedType = expectedType;
            ValueBlockVisitor.VisitInto(defaultBody, scope, defaultShell, ctx, env);
            branchTails.Add(ctx.Flow.Snapshot());
            narrowedTails.Add(ctx.Flow.SnapshotNarrowed());
            ctx.Flow.MergeBranches(before, branchTails);
            ctx.Flow.MergeNarrowedBranches(narrowedTails);
            if (selector == null) return null;
            // 产值类型统一（规则同 if 表达式）：纯穿透分支（ValueType null）
            // 不参与；有产值分支符号须引用相等（ErrorType 毒化静默）
            SemanticSymbol? type = null;
            foreach (var branch in cases.Select(c => c.Body).Append(defaultShell.Block))
            {
                if (branch.ValueType == null) continue;
                if (type == null)
                {
                    type = branch.ValueType;
                    continue;
                }
                if (!ReferenceEquals(type, branch.ValueType)
                    && type is not ErrorTypeSymbol && branch.ValueType is not ErrorTypeSymbol)
                {
                    env.Error(switchNode.Span,
                        $"switch expression branches produce different types " +
                        $"('{BoundAnalysis.TypeDisplay(type)}' and " +
                        $"'{BoundAnalysis.TypeDisplay(branch.ValueType)}')");
                    return null;
                }
            }
            if (type == null)
            {
                // 全部分支（含 default）全路径向外逃逸（各自无本块产值
                // 且路径全终止——体内每条 return@ 都穿透外层）：表达式
                // 永不落穿，合法；类型取外部期望类型兜底。无期望类型
                // 则无法定型，维持报错（同 seq 口径）
                if (cases.Select(c => c.Body).Append(defaultShell.Block)
                    .All(BoundAnalysis.ValueBlockEscapes))
                {
                    if (expectedType != null)
                    {
                        return new BoundSwitchExpression(node, selector, cases,
                            defaultShell.Block, expectedType);
                    }
                    env.Error(switchNode.Span, "switch expression escapes on all paths " +
                        "without producing a value (a type annotation is required to type it)");
                    return null;
                }
                env.Error(switchNode.Span, "switch expression must produce a value " +
                    "(at least one branch must return@ a value)");
                return null;
            }
            return new BoundSwitchExpression(node, selector, cases, defaultShell.Block, type);
            }
            finally
            {
                if (enumContext != null) ctx.Labels.PopEnumCaseContext();
            }
        }
    }

    // case 匹配表达式绑定（两形态共用）：先分类——AST 子树含单段路径 _
    // 即 pattern（SYNTAX §7.2：_ 引用 selector 的值），否则值匹配。
    // 值匹配：编译期常量最小口径（BoundLiteralExpression；enum case
    // 归 S11），类型与 selector 严格相同；pattern：占位栈开启下绑定
    // （Enter 压栈/Exit 弹栈，finally 配对），结果须 bool。
    // selector 已失败（null）时值匹配照常绑定（独立诊断），pattern
    // 静默跳过（_ 无所指，避免次生错误）；分类经 IsPattern 静态纯查询
    // 显式记录（P4a 按 §16.6 把 pattern 分支降级为嵌套条件）
    internal sealed class SwitchMatchVisitor
        : BinderVisitor<SwitchMatchVisitor, BoundExpression, SwitchMatchContext>
    {
        // 占位栈仅在 pattern 绑定时开启（实例字段记录是否已压栈，
        // Exit 配对弹栈——任务局部状态对象化的范例）
        private bool selectorPushed;

        // pattern 分类判定（调用方与 Enter 共用）：匹配表达式 AST 子树
        // 含单段路径 _（符号头名为 _ 且无后缀无段）即 pattern match。
        // 子树遍历统一走 AstStructureReflection（M28 唯一反射下钻）
        public static bool IsPattern(SwitchCaseASTNode node)
        {
            return ContainsPlaceholder(node.Pattern.Expression);
        }

        protected override void Enter(ASTNode node, Scope scope, SwitchMatchContext matchCtx,
            BindEnvironment env)
        {
            // pattern 且 selector 存活时才压栈（selector 失败时 pattern 静默跳过）
            if (IsPattern((SwitchCaseASTNode)node) && matchCtx.Selector != null)
            {
                matchCtx.Context.Labels.PushSelector(matchCtx.Selector);
                selectorPushed = true;
            }
        }

        protected override void Exit(ASTNode node, Scope scope, SwitchMatchContext matchCtx,
            BindEnvironment env)
        {
            if (selectorPushed)
            {
                matchCtx.Context.Labels.PopSelector();
            }
        }

        protected override BoundExpression? VisitCore(ASTNode node, Scope scope,
            SwitchMatchContext matchCtx, BindEnvironment env)
        {
            var caseNode = (SwitchCaseASTNode)node;
            var ctx = matchCtx.Context;
            var selector = matchCtx.Selector;
            if (IsPattern(caseNode))
            {
                if (selector == null) return null;
                var match = ExpressionDispatcher.Visit(caseNode.Pattern.Expression, scope, ctx, env,
                    selector.Type as TypeSymbol);
                if (match != null && match.Type is not ErrorTypeSymbol
                    && !ReferenceEquals(match.Type, env.B.Bool))
                {
                    env.Error(caseNode.Pattern.Span ?? caseNode.Span,
                        $"switch pattern case must be bool " +
                        $"(got '{BoundAnalysis.TypeDisplay(match.Type)}')");
                }
                return match;
            }
            var value = ExpressionDispatcher.Visit(caseNode.Pattern.Expression, scope, ctx, env,
                selector?.Type as TypeSymbol);
            if (value != null && selector != null
                && value.Type is not ErrorTypeSymbol && selector.Type is not ErrorTypeSymbol)
            {
                if (value is not BoundLiteralExpression)
                {
                    env.Error(caseNode.Pattern.Span ?? caseNode.Span,
                        "switch value-match case requires a compile-time constant");
                }
                else if (!ReferenceEquals(value.Type, selector.Type))
                {
                    env.Error(caseNode.Pattern.Span ?? caseNode.Span,
                        $"switch case constant type must equal the selector type " +
                        $"(got '{BoundAnalysis.TypeDisplay(value.Type)}' and " +
                        $"'{BoundAnalysis.TypeDisplay(selector.Type)}')");
                }
            }
            return value;
        }

        private static bool ContainsPlaceholder(ASTNode node)
        {
            if (node is PathExpressionASTNode path
                && path.Head.Expression == null && path.Head.Name == "_"
                && path.Head.Suffixes.Count == 0 && path.Segments.Count == 0)
            {
                return true;
            }
            foreach (var (child, _) in AstStructureReflection.EnumerateChildren(node))
            {
                if (ContainsPlaceholder(child)) return true;
            }
            return false;
        }
    }
}
