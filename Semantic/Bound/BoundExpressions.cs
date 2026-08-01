using System.Collections.Generic;

namespace LatteCompiler
{
    // Bound 表达式节点（S5 最小集 + S7b 首批 + S7c-2 实例成员，SEMANTIC_ROADMAP）：
    // 字面量 / 值引用（局部变量与参数）/ 全局字段引用 / 二元与一元 intrinsic 运算 /
    // 直接调用（无重载）/ new 构造 / if 表达式 / 复合赋值 /
    // this / 实例方法调用 / 实例字段访问。
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

    // if 表达式（SYNTAX §7.1）：必须有 else 分支；分支体是值块（取值规则在 P3
    // 绑定值块时判定）。Type = 两分支共同产值类型（P3 统一检查）
    public sealed class BoundIfExpression : BoundExpression
    {
        public BoundExpression Condition { get; }
        public BoundValueBlock TrueBranch { get; }
        public BoundValueBlock FalseBranch { get; }

        public BoundIfExpression(ASTNode syntax, BoundExpression condition,
            BoundValueBlock trueBranch, BoundValueBlock falseBranch, TypeSymbol type)
            : base(syntax, type)
        {
            Condition = condition;
            TrueBranch = trueBranch;
            FalseBranch = falseBranch;
        }
    }

    // 复合赋值（SYNTAX §13.2）：a += b 即 a = a + b 的语义糖；节点本身是表达式，
    // 值为写回后的值。Op 是基础运算符对应的 intrinsic（10 个：+ - * / << >> >>> & | ^，
    // 无 and/or）；Target 限定 place（局部/字段引用，P3 强制，读前须已赋值——读语义）
    public sealed class BoundCompoundAssignmentExpression : BoundExpression
    {
        public BoundExpression Target { get; }
        public BilIntrinsicOp Op { get; }
        public BoundExpression Value { get; }

        public BoundCompoundAssignmentExpression(ASTNode syntax, BoundExpression target,
            BilIntrinsicOp op, BoundExpression value, TypeSymbol type)
            : base(syntax, type)
        {
            Target = target;
            Op = op;
            Value = value;
        }
    }

    // this 引用（S7c-2，SYNTAX §9）：Type = 宿主类型（普通成员为声明类型；
    // ext 成员为目标类型——method.Owner 统一承载）。静态上下文（static
    // 方法/全局函数）中的 this 在 P3 拒绝，不落树
    public sealed class BoundThisExpression : BoundExpression
    {
        public BoundThisExpression(ASTNode syntax, TypeSymbol type) : base(syntax, type)
        {
        }
    }

    // 实例方法调用（S7c-2）：Receiver 静态类型上色查找（沿 BaseType 链，
    // 接口 receiver 查接口自身成员；ext 注册成员同路径——P2 已挂目标类型
    // 成员表）。接口方法的调用以接口方法符号引用（分派归 Middleware，
    // BIL §15.1 注释约定）。Arguments 已是规范参数序
    public sealed class BoundInstanceCallExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundInstanceCallExpression(ASTNode syntax, BoundExpression receiver,
            MethodSymbol method, IReadOnlyList<BoundExpression> arguments, TypeSymbol type)
            : base(syntax, type)
        {
            Receiver = receiver;
            Method = method;
            Arguments = arguments;
        }
    }

    // 实例字段访问（S7c-2）：Receiver 静态类型上色查找（沿 BaseType 链；
    // ext 注册成员同路径）。裸名实例字段在实例方法体内解析为 this.field
    public sealed class BoundFieldAccessExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public FieldSymbol Field { get; }

        public BoundFieldAccessExpression(ASTNode syntax, BoundExpression receiver,
            FieldSymbol field, TypeSymbol type) : base(syntax, type)
        {
            Receiver = receiver;
            Field = field;
        }
    }
}
