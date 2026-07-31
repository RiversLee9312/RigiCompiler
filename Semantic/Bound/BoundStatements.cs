using System.Collections.Generic;

namespace LatteCompiler
{
    // Bound 语句节点（S5 最小集，SEMANTIC_ROADMAP S5）：
    // 块 / 局部变量声明 / 表达式语句 / void 调用语句 / 赋值 / return。

    // 块（绑定期每块一个作用域；作用域本身是分析期结构，不落树）
    public sealed class BoundBlock : BoundStatement
    {
        public IReadOnlyList<BoundStatement> Statements { get; }

        public BoundBlock(ASTNode syntax, IReadOnlyList<BoundStatement> statements) : base(syntax)
        {
            Statements = statements;
        }
    }

    // 局部变量声明（var/const；类型已定型在 LocalSymbol.Type 上——
    // 显式标注或经初始化器推断）
    public sealed class BoundLocalDeclarationStatement : BoundStatement
    {
        public LocalSymbol Local { get; }
        public BoundExpression? Initializer { get; }

        public BoundLocalDeclarationStatement(ASTNode syntax, LocalSymbol local,
            BoundExpression? initializer) : base(syntax)
        {
            Local = local;
            Initializer = initializer;
        }
    }

    // 表达式语句（表达式求值后结果被丢弃）
    public sealed class BoundExpressionStatement : BoundStatement
    {
        public BoundExpression Expression { get; }

        public BoundExpressionStatement(ASTNode syntax, BoundExpression expression) : base(syntax)
        {
            Expression = expression;
        }
    }

    // void 调用语句（无结果方法调用只能作语句，SYNTAX §4：无隐式返回值利用）
    public sealed class BoundCallStatement : BoundStatement
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundCallStatement(ASTNode syntax, MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments) : base(syntax)
        {
            Method = method;
            Arguments = arguments;
        }
    }

    // 赋值（Target 限 BoundValueReferenceExpression / BoundFieldReferenceExpression
    // 这类 place——Binder 强制；const 目标已在 P3 拒绝）
    public sealed class BoundAssignmentStatement : BoundStatement
    {
        public BoundExpression Target { get; }
        public BoundExpression Value { get; }

        public BoundAssignmentStatement(ASTNode syntax, BoundExpression target,
            BoundExpression value) : base(syntax)
        {
            Target = target;
            Value = value;
        }
    }

    // return（Value 为 null = 裸 return，仅 void 函数合法）
    public sealed class BoundReturnStatement : BoundStatement
    {
        public BoundExpression? Value { get; }

        public BoundReturnStatement(ASTNode syntax, BoundExpression? value) : base(syntax)
        {
            Value = value;
        }
    }
}
