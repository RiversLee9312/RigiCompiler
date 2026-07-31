namespace LatteCompiler
{
    // Lowered 表达式节点（S6 最小集，SEMANTIC_ROADMAP S6）：字面量 / 值引用。
    // 字面量值不冗余存储——经 Origin.Syntax（LiteralExpressionASTNode.Literal）取。

    // 字面量（Type 由 P3 定型、值在 Origin 链上；无额外字段）
    public sealed class LoweredLiteralExpression : LoweredExpression
    {
        public LoweredLiteralExpression(BoundLiteralExpression origin) : base(origin)
        {
        }
    }

    // 值引用：局部变量（LocalSymbol）或参数（ParameterSymbol）
    public sealed class LoweredValueReferenceExpression : LoweredExpression
    {
        public SemanticSymbol Symbol { get; }

        public LoweredValueReferenceExpression(BoundValueReferenceExpression origin,
            SemanticSymbol symbol) : base(origin)
        {
            Symbol = symbol;
        }
    }
}
