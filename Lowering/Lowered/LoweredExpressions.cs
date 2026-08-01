using System.Collections.Generic;

namespace LatteCompiler
{
    // Lowered 表达式节点（S6 最小集 + S7a 补齐 + S7b 脱糖 + S7c-2 实例成员，
    // SEMANTIC_ROADMAP）：字面量 / 值引用 / 全局字段引用 / 二元与一元
    // intrinsic 运算 / 带返回值调用 / new 构造 / 编译期常量（短路脱糖产物）/
    // this / 实例方法调用 / 实例字段访问。
    // 字面量值不冗余存储——经 Origin.Syntax（LiteralExpressionASTNode.Literal）取。
    // S7b 起部分节点构造的 origin 参数放宽为 BoundNode：脱糖合成节点无逐一
    // 对应的 Bound 节点，Origin 按 ARCH §5.1 约定指向最近的语法来源。

    // 字面量（Type 由 P3 定型、值在 Origin 链上；无额外字段）
    public sealed class LoweredLiteralExpression : LoweredExpression
    {
        public LoweredLiteralExpression(BoundLiteralExpression origin) : base(origin)
        {
        }
    }

    // 编译期常量（P4a 合成节点，S7b）：bool 短路展开（BIL §11.3）的 true/false
    // 是唯一来源，S7b 仅 bool。Origin 约定 = 最近的语法来源（and/or 表达式本身
    // 的 Bound 节点）；Type 自带（语义上常量类型由自身携带，不走 Origin 透传）
    public sealed class LoweredConstantExpression : LoweredExpression
    {
        public object Value { get; }
        private readonly TypeSymbol type;

        public override TypeSymbol Type => type;

        public LoweredConstantExpression(BoundNode origin, object value, TypeSymbol type)
            : base(origin)
        {
            Value = value;
            this.type = type;
        }
    }

    // 值引用：局部变量（LocalSymbol）或参数（ParameterSymbol）。
    // Type 取符号自身类型（P3 构造 Bound 值引用时 Type 恒等于符号类型，
    // 恒等与合成两途一致——合成引用的 Origin 可能是语句节点，不能透传）
    public sealed class LoweredValueReferenceExpression : LoweredExpression
    {
        public SemanticSymbol Symbol { get; }

        public override TypeSymbol Type => Symbol switch
        {
            // .breakid 局部（Type null）不作值引用——capability 不可读
            // （BIL §9.3），LoweredLoop/LoweredLoopControl 直接持有符号
            LocalSymbol local => local.Type ?? throw new CompilerInternalException(
                ".breakid 局部不能作值引用: " + local.Name),
            ParameterSymbol parameter => (TypeSymbol)parameter.Type!,
            _ => throw new CompilerInternalException("未知值引用符号: " + Symbol.GetType().Name),
        };

        public LoweredValueReferenceExpression(BoundNode origin,
            SemanticSymbol symbol) : base(origin)
        {
            Symbol = symbol;
        }
    }

    // 全局/static 字段引用（实例字段引用属后续里程碑）
    public sealed class LoweredFieldReferenceExpression : LoweredExpression
    {
        public FieldSymbol Field { get; }

        public LoweredFieldReferenceExpression(BoundFieldReferenceExpression origin,
            FieldSymbol field) : base(origin)
        {
            Field = field;
        }
    }

    // 二元 intrinsic 运算（BIL §11；bool 短路 and/or 在 S7b 已脱糖为
    // LoweredIfStatement 展开，此节点不再承载 And/Or）
    public sealed class LoweredBinaryExpression : LoweredExpression
    {
        public BilIntrinsicOp Op { get; }
        public LoweredExpression Left { get; }
        public LoweredExpression Right { get; }

        public LoweredBinaryExpression(BoundNode origin, BilIntrinsicOp op,
            LoweredExpression left, LoweredExpression right) : base(origin)
        {
            Op = op;
            Left = left;
            Right = right;
        }
    }

    // 一元 intrinsic 运算（opposite / not / bin.not）
    public sealed class LoweredUnaryExpression : LoweredExpression
    {
        public BilIntrinsicOp Op { get; }
        public LoweredExpression Operand { get; }

        public LoweredUnaryExpression(BoundUnaryExpression origin, BilIntrinsicOp op,
            LoweredExpression operand) : base(origin)
        {
            Op = op;
            Operand = operand;
        }
    }

    // 带返回值直接调用（void 调用作语句见 LoweredCallStatement）
    public sealed class LoweredCallExpression : LoweredExpression
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }

        public LoweredCallExpression(BoundCallExpression origin, MethodSymbol method,
            IReadOnlyList<LoweredExpression> arguments) : base(origin)
        {
            Method = method;
            Arguments = arguments;
        }
    }

    // new 构造（SYNTAX §9.3）：Init 为匹配到的构造函数符号；
    // 无显式 init 的零参构造 Init 为 null
    public sealed class LoweredNewExpression : LoweredExpression
    {
        public MethodSymbol? Init { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }

        public LoweredNewExpression(BoundNewExpression origin, MethodSymbol? init,
            IReadOnlyList<LoweredExpression> arguments) : base(origin)
        {
            Init = init;
            Arguments = arguments;
        }
    }

    // this 引用（S7c-2；emitter 映射 $.this 变量操作数，零指令）
    public sealed class LoweredThisExpression : LoweredExpression
    {
        public LoweredThisExpression(BoundThisExpression origin) : base(origin)
        {
        }
    }

    // 实例方法调用（S7c-2；BIL §7.3/§15.1：receiver 求值作首实参）。
    // 接口方法符号引用时分派归 Middleware（注释约定）。
    // Type 自带不走 Origin 透传：for 脱糖（S7c-2）合成节点的 Origin 是
    // BoundLoop（语句而非表达式，无法透传）；恒等降级路径由调用方传入
    // 与 Origin 相同的类型（同一来源两形态统一）
    public sealed class LoweredInstanceCallExpression : LoweredExpression
    {
        public LoweredExpression Receiver { get; }
        public MethodSymbol Method { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }
        private readonly TypeSymbol type;

        public override TypeSymbol Type => type;

        public LoweredInstanceCallExpression(BoundNode origin, LoweredExpression receiver,
            MethodSymbol method, IReadOnlyList<LoweredExpression> arguments, TypeSymbol type)
            : base(origin)
        {
            Receiver = receiver;
            Method = method;
            Arguments = arguments;
            this.type = type;
        }
    }

    // 实例字段访问（S7c-2；BIL §13.3 get.field/set.field）
    public sealed class LoweredFieldAccessExpression : LoweredExpression
    {
        public LoweredExpression Receiver { get; }
        public FieldSymbol Field { get; }

        public LoweredFieldAccessExpression(BoundFieldAccessExpression origin,
            LoweredExpression receiver, FieldSymbol field) : base(origin)
        {
            Receiver = receiver;
            Field = field;
        }
    }
}
