using System.Collections.Generic;

namespace LatteCompiler
{
    // Lowered 表达式节点（S6 最小集 + S7a 补齐 + S7b 脱糖 + S7c-2 实例成员
    // + S7e cast + S8a 类型谓词/typeOf + S8c 索引访问，SEMANTIC_ROADMAP）：
    // 字面量 / 值引用 / 全局字段引用 / 二元与一元 intrinsic 运算 / 带返回值调用 /
    // new 构造 / 编译期常量（短路脱糖产物）/ this / 实例方法调用 / 实例字段访问 /
    // cast / is·supers·with / typeOf / 索引访问。
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
    // LoweredIfStatement 展开，此节点不再承载 And/Or）。
    // S7d 增补显式 Type：脱糖合成节点（pattern switch 值分支的 cmp.eq 条件）
    // 无类型相符的 Bound 节点可透传——Origin 指 case 匹配表达式（常量，
    // 类型与结果 bool 不同），显式 Type 优先于透传（先例：
    // LoweredConstantExpression/LoweredInstanceCallExpression）
    public sealed class LoweredBinaryExpression : LoweredExpression
    {
        public BilIntrinsicOp Op { get; }
        public LoweredExpression Left { get; }
        public LoweredExpression Right { get; }
        private readonly TypeSymbol? type;

        public override TypeSymbol Type => type ?? base.Type;

        public LoweredBinaryExpression(BoundNode origin, BilIntrinsicOp op,
            LoweredExpression left, LoweredExpression right, TypeSymbol? type = null) : base(origin)
        {
            Op = op;
            Left = left;
            Right = right;
            this.type = type;
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
        // Type 默认走 Origin 透传（恒等降级路径，P3 已含替换后类型）；
        // 显式传入 = 合成路径（S7f 解构脱糖等 Origin 非表达式节点的场景，
        // 先例：LoweredCastExpression 的 Type 自带）
        private readonly TypeSymbol? type;

        public override TypeSymbol Type => type ?? base.Type;

        public LoweredFieldAccessExpression(BoundNode origin,
            LoweredExpression receiver, FieldSymbol field, TypeSymbol? type = null) : base(origin)
        {
            Receiver = receiver;
            Field = field;
            this.type = type;
        }
    }

    // 索引访问（S8c；BIL §13.6 get.array/set.array）：读形态与赋值 place
    // 形态共用（指令选择归 P4b 按所在位置——值位置 get.array /
    // 赋值目标 set.array）。不携带 Operator 符号——§13.6 指令无符号
    // 操作数，验证器按「collection + index + result/element」严格
    // 三元组重查实现。Type 走 Origin 透传（读 = getAtIndex 返回类型，
    // 写 = setAtIndex 元素形参类型，P3 已定型）
    public sealed class LoweredIndexExpression : LoweredExpression
    {
        public LoweredExpression Receiver { get; }
        public LoweredExpression Index { get; }

        public LoweredIndexExpression(BoundIndexExpression origin,
            LoweredExpression receiver, LoweredExpression index) : base(origin)
        {
            Receiver = receiver;
            Index = index;
        }
    }

    // cast（S7e；BIL §12.1/§12.2 直接对应）：as → cast、as? → cast.safe。
    // TargetType 是转换目标类型（指令的 type 操作数）；Type 是表达式结果
    // 类型（as 即 TargetType，as? 为 Nullable<TargetType>——P3 已定型）。
    // Type 自带不走 Origin 透传：命名 catch 体头的合成节点 Origin 是
    // BoundCatchClause（语句节点无法透传）；恒等降级路径传入
    // BoundCastExpression.Type（同一来源两形态统一，先例：
    // LoweredInstanceCallExpression）
    public sealed class LoweredCastExpression : LoweredExpression
    {
        public LoweredExpression Source { get; }
        public TypeSymbol TargetType { get; }
        // true = as?（cast.safe，失败产 null）；false = as（cast，失败抛异常）
        public bool IsSafe { get; }
        private readonly TypeSymbol type;

        public override TypeSymbol Type => type;

        public LoweredCastExpression(BoundNode origin, LoweredExpression source,
            TypeSymbol targetType, bool isSafe, TypeSymbol type) : base(origin)
        {
            Source = source;
            TargetType = targetType;
            IsSafe = isSafe;
            this.type = type;
        }
    }

    // is / supers / with（S8a；BIL §12.3 直接对应）：静态形态
    // type.is/type.supers/type.with，动态形态（TargetValue）加 .indirect。
    // 双形态互斥同 Bound 侧（构造时恰一个非 null）；Kind 复用 Bound 侧
    // 枚举（Lowering → Semantic 单向依赖，与构造参数回指 Bound 节点同理）。
    // Type 自带不走 Origin 透传（恒等降级路径传入 Bound.Type，先例：
    // LoweredCastExpression）
    public sealed class LoweredTypeCheckExpression : LoweredExpression
    {
        public BoundTypeCheckKind Kind { get; }
        public LoweredExpression Operand { get; }
        public TypeSymbol? TargetType { get; }
        public LoweredExpression? TargetValue { get; }
        private readonly TypeSymbol type;

        public override TypeSymbol Type => type;

        public LoweredTypeCheckExpression(BoundNode origin, BoundTypeCheckKind kind,
            LoweredExpression operand, TypeSymbol? targetType,
            LoweredExpression? targetValue, TypeSymbol type) : base(origin)
        {
            // 双形态互斥不变量：静态/动态恰居其一
            if ((targetType == null) == (targetValue == null))
            {
                throw new CompilerInternalException(
                    "LoweredTypeCheckExpression 的 TargetType/TargetValue 必须恰一个非 null");
            }
            Kind = kind;
            Operand = operand;
            TargetType = targetType;
            TargetValue = targetValue;
            this.type = type;
        }
    }

    // typeOf（S8a；BIL §12.5 直接对应）：Operand（值形态 → getid.var）/
    // TargetType（类型形态 → getid.type）互斥（构造时恰一个非 null）。
    // Type 自带不走 Origin 透传（Type\<T\> 构造类型，恒等降级路径传入
    // Bound.Type）
    public sealed class LoweredTypeOfExpression : LoweredExpression
    {
        public LoweredExpression? Operand { get; }
        public TypeSymbol? TargetType { get; }
        private readonly TypeSymbol type;

        public override TypeSymbol Type => type;

        public LoweredTypeOfExpression(BoundNode origin, LoweredExpression? operand,
            TypeSymbol? targetType, TypeSymbol type) : base(origin)
        {
            // 双形态互斥不变量：值/类型恰居其一
            if ((operand == null) == (targetType == null))
            {
                throw new CompilerInternalException(
                    "LoweredTypeOfExpression 的 Operand/TargetType 必须恰一个非 null");
            }
            Operand = operand;
            TargetType = targetType;
            this.type = type;
        }
    }
}
