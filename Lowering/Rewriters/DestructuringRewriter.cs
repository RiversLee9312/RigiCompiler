namespace LatteCompiler
{
    // 解构声明脱糖（S7f，SYNTAX §18；BIL §3.4「精确字段读取」）：
    //   var (a, b) = pair ⇒ Block[ s_pair = pair'（只求值一次）；
    //                               Decl(a, s_pair.key)；Decl(b, s_pair.value) ]
    // 物化赋值与分量声明同块保序；字段类型取分量局部类型
    // （声明处是泛型参数，P3 已按构造实参替换定型）。
    // 自旧 LowerSession.LowerDestructuring 迁移，行为不变。
    internal sealed class DestructuringRewriter
        : LoweredVisitor<DestructuringRewriter, LoweredStatement, LowerContext>
    {
        protected override LoweredStatement? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var destructuring = (BoundDestructuringDeclarationStatement)node;
            var pair = LowerExpressionDispatcher.Visit(destructuring.Initializer, ctx, env);
            if (pair == null) return null;
            var pairLocal = ctx.NewSynthLocal(pair.Type);
            var statements = new List<LoweredStatement>
            {
                new LoweredAssignmentStatement(destructuring,
                    LowerContext.ReferenceTo(destructuring, pairLocal), pair),
            };
            foreach (var (local, field) in destructuring.Entries)
            {
                statements.Add(new LoweredLocalDeclarationStatement(destructuring, local,
                    new LoweredFieldAccessExpression(destructuring,
                        LowerContext.ReferenceTo(destructuring, pairLocal), field, local.Type!)));
            }
            return new LoweredBlock(destructuring, statements);
        }
    }
}
