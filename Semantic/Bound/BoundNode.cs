using System.Collections.Generic;

namespace LatteCompiler
{
    // BoundTree（P3 产物，SEMANTIC_ARCHITECTURE §5）：带类型的语义树。
    // 基类 BoundNode 回指 Syntax: ASTNode（必填；合成节点指向最近的语法来源）；
    // 表达式基类 BoundExpression 额外携带分析定型后的严格类型 Type: SemanticSymbol。
    // AST 只读：P3 不修改 AST 任何字段、不重挂 Parent；
    // ExpressionRootASTNode 是透明容器，BoundTree 不为它建节点。
    // 节点按语义命名，子类集合以 P3 的分析需要为准，不与 AST 节点一一对应。

    public abstract class BoundNode
    {
        // 语法来源（必填）：调试链 BoundNode.Syntax → ASTNode.Span
        public ASTNode Syntax { get; }

        protected BoundNode(ASTNode syntax)
        {
            Syntax = syntax;
        }
    }

    // 表达式基类：Type 是分析定型后的严格类型（BIL §6.4 类型严格相等）；
    // S9 起为 SemanticSymbol——泛型参数（GenericParameterSymbol）按引用
    // 相等身份出现在函数体内表达式的定型类型中
    public abstract class BoundExpression : BoundNode
    {
        public SemanticSymbol Type { get; }

        protected BoundExpression(ASTNode syntax, SemanticSymbol type) : base(syntax)
        {
            Type = type;
        }
    }

    // 语句基类
    public abstract class BoundStatement : BoundNode
    {
        protected BoundStatement(ASTNode syntax) : base(syntax)
        {
        }
    }

    // P3 分析单位（ARCHITECTURE §5.1）：一个函数体的完整分析结果。
    // 非 BoundNode：调试链经 Method（符号）与 Body.Syntax（函数体块）回指。
    public sealed class BoundFunctionBody
    {
        public MethodSymbol Method { get; }
        // 函数体内声明的全部局部变量（声明顺序；参数在 Method.Parameters）
        public IReadOnlyList<LocalSymbol> Locals { get; }
        public BoundBlock Body { get; }

        public BoundFunctionBody(MethodSymbol method, IReadOnlyList<LocalSymbol> locals, BoundBlock body)
        {
            Method = method;
            Locals = locals;
            Body = body;
        }
    }
}
