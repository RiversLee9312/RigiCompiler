using System.Collections.Generic;

namespace LatteCompiler
{
    // Bound 表达式节点（S5 最小集，SEMANTIC_ROADMAP S5）：
    // 字面量 / 值引用（局部变量与参数）/ 全局字段引用 / 二元与一元 intrinsic 运算 /
    // 直接调用（无重载）/ new 构造。
    // 字面量值不冗余存储——经 Syntax（LiteralExpressionASTNode.Literal）取。

    // 字面量（整/浮点/字符串/字符/bool/null；Type 由 P3 按字面量种类与上下文定型）
    public sealed class BoundLiteralExpression : BoundExpression
    {
        public BoundLiteralExpression(ASTNode syntax, TypeSymbol type) : base(syntax, type)
        {
        }
    }

    // 值引用：局部变量（LocalSymbol）或参数（ParameterSymbol）
    public sealed class BoundValueReferenceExpression : BoundExpression
    {
        public SemanticSymbol Symbol { get; }

        public BoundValueReferenceExpression(ASTNode syntax, SemanticSymbol symbol, TypeSymbol type)
            : base(syntax, type)
        {
            Symbol = symbol;
        }
    }

    // 全局字段引用（Owner == null 的 FieldSymbol；实例字段引用属后续里程碑）
    public sealed class BoundFieldReferenceExpression : BoundExpression
    {
        public FieldSymbol Field { get; }

        public BoundFieldReferenceExpression(ASTNode syntax, FieldSymbol field, TypeSymbol type)
            : base(syntax, type)
        {
            Field = field;
        }
    }

    // 二元 intrinsic 运算（BIL §11：两操作数类型严格相同，键 = opcode + 操作数类型
    // + 声明结果类型；比较运算 Type 为 bool，其余同操作数类型。
    // bool 的 and/or 在此仅定型——短路展开是 P4a 的职责，BIL §11.3）
    public sealed class BoundBinaryExpression : BoundExpression
    {
        public BilIntrinsicOp Op { get; }
        public BoundExpression Left { get; }
        public BoundExpression Right { get; }

        public BoundBinaryExpression(ASTNode syntax, BilIntrinsicOp op,
            BoundExpression left, BoundExpression right, TypeSymbol type)
            : base(syntax, type)
        {
            Op = op;
            Left = left;
            Right = right;
        }
    }

    // 一元 intrinsic 运算（opposite / not / bin.not）
    public sealed class BoundUnaryExpression : BoundExpression
    {
        public BilIntrinsicOp Op { get; }
        public BoundExpression Operand { get; }

        public BoundUnaryExpression(ASTNode syntax, BilIntrinsicOp op,
            BoundExpression operand, TypeSymbol type)
            : base(syntax, type)
        {
            Op = op;
            Operand = operand;
        }
    }

    // 直接函数调用（无重载，S8 才做 ranking）。实参已是绑定后的规范顺序
    // （具名实参已按形参名归位；默认参数填充属 S8）。
    // 仅用于有返回值的调用；void 调用作语句见 BoundCallStatement。
    public sealed class BoundCallExpression : BoundExpression
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundCallExpression(ASTNode syntax, MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments, TypeSymbol type)
            : base(syntax, type)
        {
            Method = method;
            Arguments = arguments;
        }
    }

    // new 构造（SYNTAX §9.3）：Init 为匹配到的构造函数符号；
    // 无显式 init 的零参构造 Init 为 null
    public sealed class BoundNewExpression : BoundExpression
    {
        public MethodSymbol? Init { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundNewExpression(ASTNode syntax, TypeSymbol constructedType, MethodSymbol? init,
            IReadOnlyList<BoundExpression> arguments)
            : base(syntax, constructedType)
        {
            Init = init;
            Arguments = arguments;
        }
    }
}
