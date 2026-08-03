namespace LatteCompiler
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
                var condition = ExpressionDispatcher.Visit(loop.Condition.Expression, scope, ctx, env);
                Conditions.CheckBool(loop.Condition, loop.Span, condition, "loop", env);
                // while 体入口收窄（S8b，SMART_CAST_DESIGN §5）：条件真边事实
                // + before 中「体内不赋值」的键——先剔除体赋值根再覆盖真边；
                // 循环后 = before（出口不收窄）；do-while 体不带（v1 简化）
                var facts = ConditionFactsExtractor.Extract(condition, ctx);
                var beforeNarrowed = ctx.Flow.SnapshotNarrowed();
                foreach (var root in CollectAssignedRoots(loop.Body, scope, ctx))
                {
                    ctx.Flow.ClearRoot(root);
                }
                ctx.Flow.ApplyNarrow(facts.True);
                LoopBodyVisitor.VisitInto(loop.Body, scope, shell, ctx, env);
                ctx.Flow.Restore(before);
                ctx.Flow.RestoreNarrowed(beforeNarrowed);
                if (condition == null) return null;
                shell.Condition = condition;
                return shell;
            }
            // DoWhile：体先行（至少一次），条件在体后。体入口收窄不带条件
            // 真边（v1 简化），但剔除体赋值根（回边保守，同 while）
            var doBeforeNarrowed = ctx.Flow.SnapshotNarrowed();
            foreach (var root in CollectAssignedRoots(loop.Body, scope, ctx))
            {
                ctx.Flow.ClearRoot(root);
            }
            LoopBodyVisitor.VisitInto(loop.Body, scope, shell, ctx, env);
            ctx.Flow.RestoreNarrowed(doBeforeNarrowed);
            var bodyAssigned = ctx.Flow.Snapshot();
            var revCondition = ExpressionDispatcher.Visit(loop.Condition.Expression, scope, ctx, env);
            Conditions.CheckBool(loop.Condition, loop.Span, revCondition, "loop", env);
            ctx.Flow.Restore(bodyAssigned);
            if (revCondition == null) return null;
            shell.Condition = revCondition;
            return shell;
        }

        // while 体收窄的保守剔除（S8b）：收集循环体 AST 全子树的赋值目标
        // 根符号（局部/参数——含嵌套块/if/内层循环/值块内的赋值）；
        // 字段/全局目标不产生根（var 字段不可收窄；const 字段不可赋值已拦截）
        private static IEnumerable<SemanticSymbol> CollectAssignedRoots(ASTNode node, Scope scope,
            BindContext ctx)
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
                var parameter = ctx.Method.Parameters.FirstOrDefault(p => p.Name == name);
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

        // - 范围循环 `for (i in a to b)`（RangeTo 非 null）：a、b 类型
        //   一致后在 a 的类型上解析实例 operator EnumerateInRange（含
        //   ext 注册——P2 已挂目标类型成员表），Iterable =
        //   BoundInstanceCallExpression{a, op, [b]}；
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
                if (!ReferenceEquals(from.Type, to.Type))
                {
                    env.Error(node.RangeTo.Span ?? node.Span,
                        $"Range bounds must have the same type " +
                        $"(got '{BoundAnalysis.TypeDisplay(from.Type)}' and " +
                        $"'{BoundAnalysis.TypeDisplay(to.Type)}')");
                    ctx.Flow.Restore(before);
                    return null;
                }
                var op = SymbolLookup.FindInstanceOperator(from.Type, "EnumerateInRange");
                if (op == null)
                {
                    env.Error(node.Span,
                        $"Type '{BoundAnalysis.TypeDisplay(from.Type)}' has no " +
                        "EnumerateInRange operator (required by range for loop)");
                    ctx.Flow.Restore(before);
                    return null;
                }
                if (op.ReturnType is not TypeSymbol enumerableType
                    || SymbolLookup.ContainsGenericParameter(enumerableType))
                {
                    env.Error(node.Span,
                        "P3: generic type parameters are not supported yet (S9)");
                    ctx.Flow.Restore(before);
                    return null;
                }
                iterable = new BoundInstanceCallExpression(node.Iterable, from, op,
                    new List<BoundExpression> { to }, enumerableType);
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
            var itemType = ResolveEnumerableElement(iterable.Type, enumerableDef, node.Span, env);
            if (itemType == null) { ctx.Flow.Restore(before); return null; }
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
            // （回边保守，同 while）
            var forBeforeNarrowed = ctx.Flow.SnapshotNarrowed();
            foreach (var root in CollectAssignedRoots(node.Body, scope, ctx))
            {
                ctx.Flow.ClearRoot(root);
            }
            LoopBodyVisitor.VisitInto(node.Body, scope, shell, ctx, env);
            ctx.Flow.Restore(before);
            ctx.Flow.RestoreNarrowed(forBeforeNarrowed);
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
        // EnumerateInRange 的返回类型），或沿自身与 BaseType 链的接口
        // 表找该定义的构造；取实参 TItem（实参含未替换泛型参数归 S9）；
        // 未实现即诊断
        private static TypeSymbol? ResolveEnumerableElement(TypeSymbol type,
            TypeSymbol enumerableDef, CharRange? span, BindEnvironment env)
        {
            if (ReferenceEquals(type.ConstructedFrom, enumerableDef))
            {
                if (type.TypeArguments![0] is TypeSymbol selfElement
                    && !SymbolLookup.ContainsGenericParameter(selfElement))
                {
                    return selfElement;
                }
                env.Error(span, "P3: generic type parameters are not supported yet (S9)");
                return null;
            }
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var iface in t.Interfaces)
                {
                    if (!ReferenceEquals(iface.ConstructedFrom, enumerableDef)) continue;
                    if (iface.TypeArguments![0] is TypeSymbol element
                        && !SymbolLookup.ContainsGenericParameter(element))
                    {
                        return element;
                    }
                    env.Error(span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
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
            ctx.Loops.Push(shell);
        }

        protected override void Exit(ASTNode node, Scope scope, BoundLoop shell, BindContext ctx,
            BindEnvironment env)
        {
            ctx.Loops.Pop();
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
            BoundLoop? target = null;
            if (control.Label == null)
            {
                if (ctx.Loops.Count > 0) target = ctx.Loops.Peek();
            }
            else
            {
                foreach (var loop in ctx.Loops)
                {
                    if (loop.Label == control.Label)
                    {
                        target = loop;
                        break;
                    }
                }
            }
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
