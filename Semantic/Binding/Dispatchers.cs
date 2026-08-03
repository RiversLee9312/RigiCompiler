namespace LatteCompiler
{
    // 类别分派器（VISITOR_REWRITE.md §2）：AST 节点 → 结构 visitor 的唯一
    // switch 所在（对应旧 BindSession 的 BindExpression/BindStatement/BindBlock
    // 分派）。结构 visitor 之间不直接互调，一律经此。
    //
    // 停线重写期间：未迁移的节点形态落 default——诊断并返回 null
    // （可恢复模型；全量测试在迁移完成前预期红）。
    internal static class ExpressionDispatcher
    {
        public static BoundExpression? Visit(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType = null)
        {
            return node switch
            {
                LiteralExpressionASTNode => LiteralVisitor.Visit(node, scope, ctx, env, expectedType),
                IfExpressionASTNode => IfExpressionVisitor.Visit(node, scope, ctx, env, expectedType),
                SwitchExpressionASTNode => SwitchExpressionVisitor.Visit(node, scope, ctx, env, expectedType),
                SeqBlockExpressionASTNode => SeqExpressionVisitor.Visit(node, scope, ctx, env, expectedType),
                CastExpressionASTNode => CastVisitor.Visit(node, scope, ctx, env, expectedType),
                BinaryExpressionASTNode => BinaryVisitor.Visit(node, scope, ctx, env, expectedType),
                UnaryExpressionASTNode => UnaryVisitor.Visit(node, scope, ctx, env, expectedType),
                CompoundAssignmentExpressionASTNode => CompoundAssignmentVisitor.Visit(node, scope,
                    ctx, env, expectedType),
                PathExpressionASTNode => PathVisitor.Visit(node, scope, ctx, env, expectedType),
                TypeCheckExpressionASTNode => TypeCheckVisitor.Visit(node, scope, ctx, env,
                    expectedType),
                TypeOfExpressionASTNode => TypeOfVisitor.Visit(node, scope, ctx, env, expectedType),
                NewExpressionASTNode => NewVisitor.Visit(node, scope, ctx, env, expectedType),
                // 括号是透明分组（Latte 无优先级，括号只定结构），不落 bound 节点
                GroupExpressionASTNode group => Visit(group.InnerExpression.Expression, scope, ctx,
                    env, expectedType),
                ExpressionRootASTNode => throw new CompilerInternalException(
                    "ExpressionRootASTNode 应在调用方解包"),
                _ => NotMigrated(node, env),
            };
        }

        private static BoundExpression? NotMigrated(ASTNode node, BindEnvironment env)
        {
            env.Error(node.Span, $"P3: expression kind not supported yet: {node.GetType().Name}");
            return null;
        }
    }

    internal static class StatementDispatcher
    {
        public static BoundStatement? Visit(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            return node switch
            {
                VariableDeclarationASTNode => LocalDeclarationVisitor.Visit(node, scope, ctx, env),
                ExpressionStatementASTNode => ExpressionStatementVisitor.Visit(node, scope, ctx, env),
                ReturnStatementASTNode => ReturnVisitor.Visit(node, scope, ctx, env),
                IfStatementASTNode => IfStatementVisitor.Visit(node, scope, ctx, env),
                LoopStatementASTNode => LoopVisitor.Visit(node, scope, ctx, env),
                LoopControlStatementASTNode => LoopControlVisitor.Visit(node, scope, ctx, env),
                SwitchStatementASTNode => SwitchStatementVisitor.Visit(node, scope, ctx, env),
                ThrowStatementASTNode => ThrowVisitor.Visit(node, scope, ctx, env),
                TryCatchFinallyStatementASTNode => TryVisitor.Visit(node, scope, ctx, env),
                // seq 语句（S7e）：语句位置的 seq 是裸 SeqBlockExpressionASTNode
                // 直接进块（CodeBlockParserLayer 施工形态，非表达式语句包装）
                SeqBlockExpressionASTNode => SeqStatementVisitor.Visit(node, scope, ctx, env),
                CodeBlockASTNode block => BlockDispatcher.Visit(block, scope, ctx, env),
                _ => NotMigrated(node, env),
            };
        }

        private static BoundStatement? NotMigrated(ASTNode node, BindEnvironment env)
        {
            env.Error(node.Span, $"P3: statement kind not supported yet: {node.GetType().Name}");
            return null;
        }
    }

    internal static class BlockDispatcher
    {
        // 块绑定（迁移自旧 BindBlock）：新作用域 + 逐语句分派；
        // 失败语句落诊断后跳过（可恢复模型）
        public static BoundBlock Visit(CodeBlockASTNode node, Scope? parentScope, BindContext ctx,
            BindEnvironment env)
        {
            var scope = new Scope(parentScope);
            var statements = new List<BoundStatement>();
            foreach (var statement in node.Statements)
            {
                var bound = StatementDispatcher.Visit(statement, scope, ctx, env);
                if (bound != null) statements.Add(bound);
            }
            return new BoundBlock(node, statements);
        }
    }
}
