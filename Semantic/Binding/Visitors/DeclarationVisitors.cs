namespace LatteCompiler
{
    // 局部变量声明（S5）与解构声明（S7f，SYNTAX §18）。
    // 自旧 BindSession.BindLocalDeclaration/BindDestructuring/FindCorePairDefinition
    // 迁移，行为不变。
    internal sealed class LocalDeclarationVisitor
        : BinderVisitor<LocalDeclarationVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var decl = (VariableDeclarationASTNode)node;
            // 解构声明（S7f，SYNTAX §18）：与单名形态互斥
            if (decl.DestructureNames != null)
            {
                return BindDestructuring(decl, scope, ctx, env);
            }
            // 局部变量访问器（S8e 归口，§9.4.1：栈上访问器暂未实现）——
            // 降级不中断，继续按普通局部绑定
            if (decl.Getter != null || decl.Setter != null)
            {
                env.Error(decl.Span,
                    "P3: local variable accessors are not supported yet (S11)");
            }
            // S9a：声明类型可为泛型参数（引用相等身份）
            SemanticSymbol? declaredType = null;
            if (decl.TypeAnnotation != null)
            {
                declaredType = TypeReferences.Resolve(decl.TypeAnnotation, decl.Span, ctx.Frame,
                    env);
            }
            // 初始化表达式先于变量入作用域绑定（var x = x 报未定义而非自引用）
            var init = decl.Initializer == null
                ? null
                : ExpressionDispatcher.Visit(decl.Initializer.Expression, scope, ctx, env,
                    declaredType as TypeSymbol);
            var type = declaredType ?? init?.Type;
            if (type == null)
            {
                env.Error(decl.Span,
                    $"Variable '{decl.Name}' requires a type annotation or an initializer");
                return null;
            }
            if (decl.IsConst && init == null)
            {
                env.Error(decl.Span, $"Const '{decl.Name}' must have an initializer");
                return null;
            }
            if (declaredType != null && init != null
                && !SymbolLookup.IsAssignable(init.Type, declaredType, env))
            {
                env.Error(decl.Initializer!.Span ?? decl.Span,
                    $"Cannot assign '{BoundAnalysis.TypeDisplay(init.Type)}' to " +
                    $"'{BoundAnalysis.TypeDisplay(declaredType)}'");
            }
            if (scope.DeclaresHere(decl.Name))
            {
                env.Error(decl.Span, $"Duplicate local variable '{decl.Name}'");
                return null;
            }
            var local = new LocalSymbol(decl.Name, type, decl.IsConst);
            scope.Declare(local);
            ctx.Locals.Add(local);
            if (init != null) ctx.Flow.MarkAssigned(local);
            return new BoundLocalDeclarationStatement(node, local, init);
        }

        // 解构声明（S7f，SYNTAX §18）：var (a, b) = pair——初始化器类型
        // 必须沿 BaseType 链达到 core.Pair\<TKey, TValue\> 构造；每个名字
        // 绑定为对应分量类型的局部（字段读取由 P4a 脱糖）。core.Pair 是
        // .bootstrap.latte 自举声明，编译器按 canonical 名硬编码参照
        // （同 M48 core.collections 协议先例）
        private static BoundStatement? BindDestructuring(VariableDeclarationASTNode node,
            Scope scope, BindContext ctx, BindEnvironment env)
        {
            // Parser 已强制 =；此处为防御性检查
            if (node.Initializer == null)
            {
                env.Error(node.Span, "Destructuring declaration requires an initializer");
                return null;
            }
            var init = ExpressionDispatcher.Visit(node.Initializer.Expression, scope, ctx, env);
            if (init == null) return null;
            if (init.Type is ErrorTypeSymbol) return null;
            var pairDef = FindCorePairDefinition(node.Span, env);
            if (pairDef == null) return null;
            // S9a：泛型参数类型判型后不参与 Pair 查找（自然报非 Pair 诊断）
            TypeSymbol? constructed = null;
            for (var t = init.Type as TypeSymbol; t != null; t = t.BaseType)
            {
                if (ReferenceEquals(t.ConstructedFrom, pairDef))
                {
                    constructed = t;
                    break;
                }
            }
            if (constructed == null)
            {
                env.Error(node.Span,
                    $"Destructuring requires a subtype of core.Pair\\<TKey, TValue\\> " +
                    $"(got '{BoundAnalysis.TypeDisplay(init.Type)}')");
                return null;
            }
            if (node.DestructureNames!.Count != 2)
            {
                env.Error(node.Span,
                    $"Destructuring of core.Pair requires exactly 2 names " +
                    $"(got {node.DestructureNames.Count})");
                return null;
            }
            var entries = new List<(LocalSymbol, FieldSymbol)>();
            var componentNames = new[] { "key", "value" };
            for (int i = 0; i < 2; i++)
            {
                // 分量类型 = 构造实参（S9a 放行：实参可为泛型参数，引用相等身份）
                var componentType = constructed.TypeArguments![i];
                var name = node.DestructureNames[i];
                if (scope.DeclaresHere(name))
                {
                    env.Error(node.Span, $"Duplicate local variable '{name}'");
                    return null;
                }
                var field = pairDef.Fields.First(f => f.Name == componentNames[i]);
                var local = new LocalSymbol(name, componentType, node.IsConst);
                scope.Declare(local);
                ctx.Locals.Add(local);
                ctx.Flow.MarkAssigned(local);
                entries.Add((local, field));
            }
            return new BoundDestructuringDeclarationStatement(node, init, entries);
        }

        // core.Pair 定义查找（.bootstrap.latte 自举提供；缺席即诊断——
        // BindUnit 类不带 stdlib 的驱动触不到解构绑定）
        private static TypeSymbol? FindCorePairDefinition(CharRange? span, BindEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            var pair = core?.Types.FirstOrDefault(t => t.Name == "Pair"
                && t.GenericParameters.Count == 2);
            if (pair == null)
            {
                env.Error(span, "P3: core.Pair not found " +
                    "(required by destructuring declaration; stdlib missing)");
            }
            return pair;
        }
    }

    // 表达式语句与赋值（S5；S8c 增补索引写入 place）。自旧
    // BindSession.BindExpressionStatement/BindAssignment 迁移，行为不变。
    internal sealed class ExpressionStatementVisitor
        : BinderVisitor<ExpressionStatementVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var stmt = (ExpressionStatementASTNode)node;
            if (stmt.AssignValue != null)
            {
                return BindAssignment(stmt, scope, ctx, env);
            }
            // void 调用落成 BoundCallStatement，非 void 调用仍是表达式语句
            if (stmt.Expression.Expression is PathExpressionASTNode path
                && CallForm.TryGet(path, out var calleeSegments, out var callArguments,
                    out var genericArguments))
            {
                var binding = CallFacility.BindCall(stmt, calleeSegments, callArguments!, scope,
                    ctx, env, genericArguments);
                if (binding == null) return null;
                if (binding.IsVoid)
                {
                    return new BoundCallStatement(stmt, binding.Method, binding.Arguments,
                        binding.Receiver, binding.TypeArguments);
                }
                if (binding.Receiver != null)
                {
                    return new BoundExpressionStatement(stmt,
                        new BoundInstanceCallExpression(path, binding.Receiver,
                            binding.Method, binding.Arguments, binding.ResultType!,
                            binding.TypeArguments));
                }
                return new BoundExpressionStatement(stmt, new BoundCallExpression(path,
                    binding.Method, binding.Arguments, binding.ResultType!,
                    binding.TypeArguments));
            }
            var expr = ExpressionDispatcher.Visit(stmt.Expression.Expression, scope, ctx, env);
            return expr == null ? null : new BoundExpressionStatement(stmt, expr);
        }

        private static BoundStatement? BindAssignment(ExpressionStatementASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            // 赋值目标是定义而非「使用」：符号引用不经 unassigned 检查
            var target = node.Expression.Expression is PathExpressionASTNode targetPath
                ? PathFacility.VisitForAssignment(targetPath, scope, ctx, env)
                : ExpressionDispatcher.Visit(node.Expression.Expression, scope, ctx, env);
            var value = ExpressionDispatcher.Visit(node.AssignValue!.Expression, scope, ctx, env,
                target?.Type as TypeSymbol);
            if (target == null || value == null) return null;
            switch (target)
            {
                case BoundValueReferenceExpression { Symbol: LocalSymbol local }:
                    if (local.IsConst)
                    {
                        env.Error(node.Span, $"Cannot assign to const '{local.Name}'");
                        return null;
                    }
                    ctx.Flow.MarkAssigned(local);
                    // S8b：var 局部重新赋值 → 收窄失效（含以其为根的字段链）
                    ctx.Flow.ClearRoot(local);
                    break;
                case BoundValueReferenceExpression { Symbol: ParameterSymbol parameter }:
                    // S8b：参数赋值 → 收窄失效（同 var 局部规则）
                    ctx.Flow.ClearRoot(parameter);
                    break;
                case BoundFieldReferenceExpression fieldReference:
                    // 字段写入统一检查（S8e）：带访问器字段查 setter，
                    // 无访问器字段走 const 规则
                    if (!ConstFieldRules.CheckWritable(fieldReference.Field, node.Span, ctx.Frame,
                        env))
                    {
                        return null;
                    }
                    break;
                case BoundFieldAccessExpression fieldAccess:
                    if (!ConstFieldRules.CheckWritable(fieldAccess.Field, node.Span, ctx.Frame,
                        env))
                    {
                        return null;
                    }
                    break;
                case BoundIndexExpression:
                    // S8c 索引写入：setAtIndex 已在目标绑定时解析（写模式）
                    // ——索引写入不改变量本身：无 const 检查、无
                    // MarkAssigned、无收窄失效
                    break;
                default:
                    env.Error(node.Expression.Span ?? node.Span,
                        "Assignment target must be a variable");
                    return null;
            }
            if (!SymbolLookup.IsAssignable(value.Type, target.Type, env))
            {
                env.Error(node.AssignValue.Span ?? node.Span,
                    $"Cannot assign '{BoundAnalysis.TypeDisplay(value.Type)}' to " +
                    $"'{BoundAnalysis.TypeDisplay(target.Type)}'");
            }
            return new BoundAssignmentStatement(node, target, value);
        }
    }

    // return 语句（S5）与 return@标签 值块产出（S7b，SYNTAX §6.1）。
    // 自旧 BindSession.BindReturn 迁移，行为不变。
    internal sealed class ReturnVisitor : BinderVisitor<ReturnVisitor, BoundStatement, BindContext>
    {
        protected override BoundStatement? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            var ret = (ReturnStatementASTNode)node;
            // return@标签（SYNTAX §6.1）：终止标签对应值块的路径并把值作为该块
            // 产值；沿值块标签栈从内向外查找，未命中再查语句 seq 标签栈
            // （M61：return@语句seq 提前结束该块，必须不携带值）
            if (ret.Label != null)
            {
                var valueBlockEntry = ctx.Labels.FindValueBlock(ret.Label);
                var target = valueBlockEntry?.Block;
                if (target == null)
                {
                    // 语句 seq 目标（M61）：隔循环拦截（同值块——P4a 编织无法
                    // 表达跳出中间循环）；隔值块拦截（值块 continuation 无法
                    // 表达「跳到外层 seq」）；不携带值（语句 seq 无产值消费者）
                    var seqEntry = ctx.Labels.FindSeqLabel(ret.Label);
                    if (seqEntry != null)
                    {
                        if (ctx.Labels.LoopDepth > seqEntry.Value.LoopDepth)
                        {
                            env.Error(ret.Span, $"P3: return@{ret.Label} across a loop " +
                                "boundary not supported yet (S7c)");
                            return null;
                        }
                        if (ctx.Labels.ValueBlockDepth > seqEntry.Value.ValueBlockDepth)
                        {
                            env.Error(ret.Span, $"P3: return@{ret.Label} across a value " +
                                "block boundary not supported yet");
                            return null;
                        }
                        if (ret.Value != null)
                        {
                            env.Error(ret.Value.Span ?? ret.Span,
                                $"return@{ret.Label} cannot carry a value " +
                                "(target is a statement seq)");
                            return null;
                        }
                        return new BoundSeqExitStatement(node, seqEntry.Value.Seq);
                    }
                    env.Error(ret.Span, $"Undefined value block label: '{ret.Label}'");
                    return null;
                }
                // return@ 隔循环边界（S7c-1 拦截，S7c 技术债）：脱糖产物
                // 只是「写值块局部」，无法表达「跳出中间循环」，P3 拒绝
                if (ctx.Labels.LoopDepth > valueBlockEntry!.Value.LoopDepth)
                {
                    env.Error(ret.Span, $"P3: return@{ret.Label} across a loop " +
                        "boundary not supported yet (S7c)");
                    return null;
                }
                if (ret.Value == null)
                {
                    env.Error(ret.Span, $"return@{ret.Label} requires a value");
                    return null;
                }
                var labelValue = ExpressionDispatcher.Visit(ret.Value.Expression, scope, ctx, env);
                if (labelValue == null) return null;
                return new BoundReturnValueStatement(node, target, labelValue);
            }
            if (ret.Value == null)
            {
                if (ctx.Frame.Method.ReturnType != null)
                {
                    env.Error(ret.Span, $"Function '{ctx.Frame.Method.Name}' must return a value");
                    return null;
                }
                return new BoundReturnStatement(node, null);
            }
            var value = ExpressionDispatcher.Visit(ret.Value.Expression, scope, ctx, env,
                ctx.Frame.Method.ReturnType as TypeSymbol);
            if (value == null) return null;
            if (ctx.Frame.Method.ReturnType == null)
            {
                env.Error(ret.Value.Span ?? ret.Span,
                    $"Void function '{ctx.Frame.Method.Name}' cannot return a value");
                return null;
            }
            // 返回类型为泛型参数时兼容判定归 S9
            if (ctx.Frame.Method.ReturnType is TypeSymbol returnType
                && !SymbolLookup.IsAssignable(value.Type, returnType, env))
            {
                env.Error(ret.Value.Span ?? ret.Span,
                    $"Cannot return '{BoundAnalysis.TypeDisplay(value.Type)}' from function " +
                    $"returning '{BoundAnalysis.TypeDisplay(returnType)}'");
            }
            return new BoundReturnStatement(node, value);
        }
    }
}
