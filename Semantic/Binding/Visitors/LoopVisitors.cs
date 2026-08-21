namespace RigiCompiler
{
    // 循环绑定（S7c-1 while/do-while/break/continue；S7c-2 for 双形态，
    // SYNTAX §7.3/§13.2）。自旧 BindSession.BindLoop/BindLoopControl/
    // BindForLoop/BindForBody/FindCollectionType/ResolveEnumerableElement
    // 迁移，行为不变。
    internal sealed class LoopVisitor : BinderVisitor<LoopVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var loop = (LoopStatementASTNode)node;
            if (loop.Kind == LoopKind.For)
            {
                return BindForLoop(loop, scope, ctx, env);
            }
            // While/DoWhile 必有条件（Parser 不变量；For 已提前返回）
            if (loop.Condition == null)
            {
                throw new CompilerInternalException(
                    "while/do-while 循环缺条件（Parser 不变量破坏）");
            }
            var shell = new BoundLoop(node, loop.Kind, loop.Label);
            if (loop.Kind == LoopKind.While)
            {
                var before = ctx.Flow.Snapshot();
                // 条件恒 bool：以 bool 为期望类型绑定（逃逸型 seq/if/switch
                // 表达式在条件位据此定型——体全路径向外逃逸、自身不产值，
                // 类型取期望类型兜底，同变量初始化位的类型标注机制）
                var condition = ExpressionDispatcher.Visit(loop.Condition.Expression, scope, ctx,
                    env, env.B.Bool);
                Conditions.CheckBool(loop.Condition, loop.Span, condition, "loop", env);
                // while 体入口收窄（S8b，SYNTAX §3.5）：条件真边事实
                // + before 中「体内不赋值」的键——先剔除体赋值根再覆盖真边；
                // 循环后 = before（出口不收窄），且 before 中体赋值根的键
                // 不得复活（回边保守——体可能执行，出口对恢复表同样剔除）；
                // do-while 体不带（v1 简化）
                var facts = ConditionFactsExtractor.Extract(condition, ctx.Frame);
                var beforeNarrowed = ctx.Flow.SnapshotNarrowed();
                var assignedRoots = CollectAssignedRoots(loop.Body, scope, ctx.Frame).ToList();
                foreach (var root in assignedRoots)
                {
                    ctx.Flow.ClearRoot(root);
                }
                ctx.Flow.ApplyNarrow(facts.True);
                LoopBodyVisitor.VisitInto(loop.Body, scope, shell, ctx, env);
                ctx.Flow.Restore(before);
                ctx.Flow.RestoreNarrowed(beforeNarrowed);
                foreach (var root in assignedRoots)
                {
                    ctx.Flow.ClearRoot(root);
                }
                if (condition == null) return null;
                shell.Condition = condition;
                return shell;
            }
            // DoWhile：体先行（至少一次），条件在体后。体入口收窄不带条件
            // 真边（v1 简化），但剔除体赋值根（回边保守，同 while）；
            // 出口对恢复表同样剔除（体至少执行一次——体赋值根的收窄必失效，
            // 不得随 before 快照复活）
            var doBeforeNarrowed = ctx.Flow.SnapshotNarrowed();
            var doAssignedRoots = CollectAssignedRoots(loop.Body, scope, ctx.Frame).ToList();
            foreach (var root in doAssignedRoots)
            {
                ctx.Flow.ClearRoot(root);
            }
            var doBefore = ctx.Flow.Snapshot();
            LoopBodyVisitor.VisitInto(loop.Body, scope, shell, ctx, env);
            ctx.Flow.RestoreNarrowed(doBeforeNarrowed);
            foreach (var root in doAssignedRoots)
            {
                ctx.Flow.ClearRoot(root);
            }
            // 同 while：条件恒 bool，以 bool 为期望类型绑定（逃逸型表达式定型）
            var revCondition = ExpressionDispatcher.Visit(loop.Condition.Expression, scope, ctx,
                env, env.B.Bool);
            Conditions.CheckBool(loop.Condition, loop.Span, revCondition, "loop", env);
            // 循环出口 = 全部出环路径交集（W7：do-while 体尾与 break 出环点取交）
            ctx.Flow.Restore(LocalLoopDa.ExitAfter(shell, doBefore));
            if (revCondition == null) return null;
            shell.Condition = revCondition;
            return shell;
        }

        // while 体收窄的保守剔除（S8b）：收集循环体 AST 全子树的赋值目标
        // 根符号（局部/参数——含嵌套块/if/内层循环/值块内的赋值）；
        // 字段/全局目标不产生根（var 字段不可收窄；const 字段不可赋值已拦截）。
        // internal：TryVisitor 的 finally 体赋值失效（恒执行块同规则）复用
        internal static IEnumerable<SemanticSymbol> CollectAssignedRoots(ASTNode node,
            Scope scope, BindFunctionFrame frame)
        {
            var names = new List<string>();
            CollectAssignmentTargetNames(node, names);
            foreach (var name in names)
            {
                var local = scope.Lookup(name);
                if (local != null)
                {
                    yield return local;
                    continue;
                }
                var parameter = frame.Method.Parameters.FirstOrDefault(p => p.Name == name);
                if (parameter != null) yield return parameter;
            }
        }

        private static void CollectAssignmentTargetNames(ASTNode node, List<string> names)
        {
            if (node is ExpressionStatementASTNode { AssignValue: not null } assignment
                && assignment.Expression.Expression is PathExpressionASTNode targetPath
                && targetPath.Head.Name != null)
            {
                names.Add(targetPath.Head.Name);
            }
            if (node is CompoundAssignmentExpressionASTNode compound
                && compound.Target.Expression is PathExpressionASTNode compoundPath
                && compoundPath.Head.Name != null)
            {
                names.Add(compoundPath.Head.Name);
            }
            foreach (var (child, _) in AstStructureReflection.EnumerateChildren(node))
            {
                CollectAssignmentTargetNames(child, names);
            }
        }

        // - 范围循环 `for (i in a to b)`（RangeTo 非 null）：始终按 a 的
        //   有效成员类型派发实例 operator EnumerateInRange（含约束界 /
        //   ext 注册——P2 已挂目标类型成员表），右操作数 b 走 §4.2
        //   ResolveBound（不必与 a 同型）；Iterable =
        //   BoundInstanceCallExpression{a, op, [b]}；区间开闭/步长由该
        //   operator 实现自定义，此处不做整数特化；
        // - for-each `for (item in collection)`：Iterable = 集合表达式；
        // 两形态汇合于 for-each 协议判定：Iterable 类型实现
        // core.collections::IEnumerable\<TItem\>（沿接口表找该定义的
        // 构造取实参），协议三方法符号挂到 BoundLoop（P4 不做名字分析，
        // ARCH §11.3 纪律）；循环变量 const 局部（只读默认，规范未明，
        // M48 登记）；DA：for 后 = before（体可能零次执行）
        private static BoundStatement? BindForLoop(LoopStatementASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            if (node.VariableName == null || node.Iterable == null)
            {
                throw new CompilerInternalException(
                    "for 循环缺循环变量或迭代源（Parser 不变量破坏）");
            }
            var before = ctx.Flow.Snapshot();
            BoundExpression? iterable;
            if (node.RangeTo != null)
            {
                var from = ExpressionDispatcher.Visit(node.Iterable.Expression, scope, ctx, env);
                var to = ExpressionDispatcher.Visit(node.RangeTo.Expression, scope, ctx, env);
                if (from == null || to == null) { ctx.Flow.Restore(before); return null; }
                // 毒化静默：任一侧已失败时不再报次生错误
                if (from.Type is ErrorTypeSymbol || to.Type is ErrorTypeSymbol)
                {
                    ctx.Flow.Restore(before);
                    return null;
                }
                var rangeLookup = SymbolLookup.EffectiveMemberType(from.Type, env);
                var candidates = SymbolLookup.FindInstanceOperators(rangeLookup,
                    "EnumerateInRange", 1, env.Unit.Symbols);
                if (candidates.Count == 0)
                {
                    env.Error(node.Span,
                        $"Type '{BoundAnalysis.TypeDisplay(from.Type)}' has no " +
                        "EnumerateInRange operator (required by range for loop)");
                    ctx.Flow.Restore(before);
                    return null;
                }
                var accessible = candidates.Where(ctx.Frame.CanAccess).ToList();
                if (accessible.Count == 0)
                {
                    env.Error(node.Span, AccessChecker.InaccessibleMessage(candidates[0]));
                    ctx.Flow.Restore(before);
                    return null;
                }
                var resolved = OverloadResolution.ResolveBound(node, accessible,
                    new[] { to }, env, rangeLookup);
                if (resolved == null)
                {
                    ctx.Flow.Restore(before);
                    return null;
                }
                if (resolved.Value.ReturnType is not TypeSymbol enumerableType)
                {
                    env.Error(node.Span,
                        "range for loop requires a concrete enumerable type " +
                        $"(got '{BoundAnalysis.TypeDisplay(from.Type)}' EnumerateInRange)");
                    ctx.Flow.Restore(before);
                    return null;
                }
                iterable = new BoundInstanceCallExpression(node.Iterable, from,
                    resolved.Value.Method, new List<BoundExpression> { to },
                    enumerableType, resolved.Value.TypeArguments);
            }
            else
            {
                iterable = ExpressionDispatcher.Visit(node.Iterable.Expression, scope, ctx, env);
                if (iterable == null) { ctx.Flow.Restore(before); return null; }
            }
            if (iterable.Type is ErrorTypeSymbol) { ctx.Flow.Restore(before); return null; }
            // for-each 协议判定与三方法符号
            var enumerableDef = FindCollectionType("IEnumerable", node.Span, env);
            var enumeratorDef = FindCollectionType("IEnumerator", node.Span, env);
            if (enumerableDef == null || enumeratorDef == null)
            {
                ctx.Flow.Restore(before);
                return null;
            }
            if (iterable.Type is ErrorTypeSymbol)
            {
                ctx.Flow.Restore(before);
                return null;
            }
            var iterableType = SymbolLookup.EffectiveMemberType(iterable.Type, env);
            var itemType = ResolveEnumerableElement(iterableType, enumerableDef, node.Span, env);
            if (itemType == null) { ctx.Flow.Restore(before); return null; }
            // 推断元素类型使用点检查（§16.1，bug S5 修复2，同局部推断口径；
            // F1 起递归口径 + 与统一收口驻留去重——iterable 表达式
            // Array\<Hidden\> 经收口递归命中已报时本挂点静默）
            UseSiteAccessibility.CheckInferredType(itemType, node.Span, ctx, env);
            var iterate = enumerableDef.Methods.FirstOrDefault(m => m.Name == "iterate");
            var moveNext = enumeratorDef.Methods.FirstOrDefault(m => m.Name == "moveNext");
            var current = enumeratorDef.Methods.FirstOrDefault(m => m.Name == "current");
            if (iterate == null || moveNext == null || current == null)
            {
                throw new CompilerInternalException(
                    "core.collections 迭代协议成员缺失（stdlib 不变量破坏）");
            }
            var loopVariable = new LocalSymbol(node.VariableName, itemType, isConst: true);
            ctx.Locals.Add(loopVariable);
            ctx.Flow.MarkAssigned(loopVariable);    // 体入口视为已赋值（每轮由枚举器赋）
            var shell = new BoundLoop(node, LoopKind.For, node.Label)
            {
                Iterable = iterable,
                LoopVariable = loopVariable,
                IterateMethod = iterate,
                MoveNextMethod = moveNext,
                CurrentMethod = current,
            };
            // for 体入口收窄：无条件真边（条件由脱糖承载），但剔除体赋值根
            // （回边保守，同 while）；出口对恢复表同样剔除（不得复活）
            var forBeforeNarrowed = ctx.Flow.SnapshotNarrowed();
            var forAssignedRoots = CollectAssignedRoots(node.Body, scope, ctx.Frame).ToList();
            foreach (var root in forAssignedRoots)
            {
                ctx.Flow.ClearRoot(root);
            }
            LoopBodyVisitor.VisitInto(node.Body, scope, shell, ctx, env);
            ctx.Flow.Restore(before);
            ctx.Flow.RestoreNarrowed(forBeforeNarrowed);
            foreach (var root in forAssignedRoots)
            {
                ctx.Flow.ClearRoot(root);
            }
            return shell;
        }

        // core.collections 协议定义查找（stdlib 内嵌源提供；缺席即诊断
        // ——BindUnit 类不带 stdlib 的驱动触不到 for 绑定）
        private static TypeSymbol? FindCollectionType(string name, CharRange? span,
            BindEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            var collections = core?.ChildNamespaces
                .FirstOrDefault(n => n.Name == "collections");
            var type = collections?.Types.FirstOrDefault(t => t.Name == name);
            if (type == null)
            {
                env.Error(span, $"P3: core.collections.{name} not found " +
                    "(required by for loop; stdlib missing)");
            }
            return type;
        }

        // for-each 协议判定：type 实现 core.collections::IEnumerable\<TItem\>
        // ——type 自身即该定义的构造（迭代源的静态类型就是接口，如
        // EnumerateInRange 的返回类型），或经 InterfaceClosure 取代入后
        // 的接口闭包（构造类型 Interfaces 表为空，必须走定义 + Substitute，
        // 与 IsAssignable 同口径；Bag\<i32\> → IEnumerable\<i32\>）。
        // 取实参 TItem（S9a 放行：实参可为泛型参数）；未实现即诊断
        private static SemanticSymbol? ResolveEnumerableElement(TypeSymbol type,
            TypeSymbol enumerableDef, CharRange? span, BindEnvironment env)
        {
            if (ReferenceEquals(type.ConstructedFrom, enumerableDef))
            {
                return type.TypeArguments![0];
            }
            foreach (var iface in OverrideChecker.InterfaceClosure(type, env.Unit.Symbols))
            {
                if (!ReferenceEquals(iface.ConstructedFrom, enumerableDef)) continue;
                return iface.TypeArguments![0];
            }
            env.Error(span, $"Type '{BoundAnalysis.TypeDisplay(type)}' does not implement " +
                "core.collections.IEnumerable<T> (required by for loop)");
            return null;
        }
    }

    // 循环体壳填充（标签栈协议首验）：Enter 压循环标签栈、Exit 弹栈
    // （finally 配对——替代旧代码手工 try/finally）；for 形态体块作用域
    // 预声明循环变量（遮蔽检查经 DeclaresHere 自然生效）
    internal sealed class LoopBodyVisitor
        : BinderShellVisitor<LoopBodyVisitor, BoundLoop, BindContext>
    {
        protected override void Enter(ASTNode node, Scope scope, BoundLoop shell, BindContext ctx,
            BindEnvironment env)
        {
            ctx.Labels.PushLoop(shell);
        }

        protected override void Exit(ASTNode node, Scope scope, BoundLoop shell, BindContext ctx,
            BindEnvironment env)
        {
            ctx.Labels.PopLoop();
        }

        protected override void VisitCoreInto(ASTNode node, Scope scope, BoundLoop shell,
            BindContext ctx, BindEnvironment env)
        {
            var bodyNode = (CodeBlockASTNode)node;
            if (shell.Kind == LoopKind.For && shell.LoopVariable != null)
            {
                // for 循环体：体块作用域预声明循环变量（仿 BlockDispatcher）
                var forScope = new Scope(scope);
                forScope.Declare(shell.LoopVariable);
                var statements = new List<BoundStatement>();
                foreach (var statement in bodyNode.Statements)
                {
                    var bound = StatementDispatcher.Visit(statement, forScope, ctx, env);
                    if (bound != null) statements.Add(bound);
                }
                shell.Body = new BoundBlock(bodyNode, statements);
                return;
            }
            shell.Body = BlockDispatcher.Visit(bodyNode, scope, ctx, env);
        }
    }

    // break/continue（S7c-1）：无标签命中循环标签栈栈顶（最内层），栈空即
    // 循环外使用（诊断）；有标签沿栈从内向外查 named 命中（穿透
    // 值块/嵌套块命中外层循环合法，BIL §16.5），未命中即未定义标签
    internal sealed class LoopControlVisitor
        : BinderVisitor<LoopControlVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var control = (LoopControlStatementASTNode)node;
            var target = ctx.Labels.FindLoop(control.Label);
            if (target == null)
            {
                var keyword = control.IsBreak ? "break" : "continue";
                env.Error(node.Span, control.Label == null
                    ? $"'{keyword}' outside of a loop"
                    : $"Undefined loop label: '{control.Label}'");
                return null;
            }
            return new BoundLoopControl(node, control.IsBreak, target);
        }
    }
}
