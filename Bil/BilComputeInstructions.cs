using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // §11 运算指令与 §12 转换/运行时类型指令（M57 强类型化）。
    // 操作数全为变量（§10.1）；结果变量一律为指令最后一个操作数（§10.3）。

    // §11 二元运算维度（拼写见 BilSpellings；and/or 为不短路形态——
    // 内建 bool 短路已在 P4a 展开）
    public enum BilBinaryOp
    {
        Add, Sub, Mul, Div,             // §11.2 算术
        And, Or,                        // §11.3 逻辑（不短路）
        BinAnd, BinOr, BinXor,          // §11.4 位运算
        ShiftLeft, ShiftRight, ShiftRightUnsigned,
        CmpEq, CmpNe, CmpLt, CmpLe, CmpGt, CmpGe,   // §11.5 比较
    }

    // §11 一元运算维度
    public enum BilUnaryOp
    {
        Opposite,                       // §11.2 取负
        Not,                            // §11.3 逻辑非
        BinNot,                         // §11.4 位非
    }

    // §11 二元 intrinsic：op LEFT RIGHT RESULT
    public sealed class BinaryIntrinsicInstruction : BilInstruction
    {
        public BilBinaryOp Op { get; }
        public BilVariableOperand Left { get; }
        public BilVariableOperand Right { get; }
        public BilVariableOperand Target { get; }

        public BinaryIntrinsicInstruction(BilBinaryOp op, BilVariableOperand left,
            BilVariableOperand right, BilVariableOperand target)
        {
            Op = op;
            Left = left;
            Right = right;
            Target = target;
        }

        internal override string Opcode => BilSpellings.Of(Op);
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Left, Right, Target };
    }

    // §11 一元 intrinsic：op OPERAND RESULT
    public sealed class UnaryIntrinsicInstruction : BilInstruction
    {
        public BilUnaryOp Op { get; }
        public BilVariableOperand Operand { get; }
        public BilVariableOperand Target { get; }

        public UnaryIntrinsicInstruction(BilUnaryOp op, BilVariableOperand operand,
            BilVariableOperand target)
        {
            Op = op;
            Operand = operand;
            Target = target;
        }

        internal override string Opcode => BilSpellings.Of(Op);
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Operand, Target };
    }

    // §12.1/§12.2 转换：cast|cast.safe SOURCE RESULT type(TARGET_TYPE)
    public sealed class CastInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand Target { get; }
        public BilTypeOperand TargetType { get; }
        // false = §12.1 强制转换（失败抛 CastException）；
        // true = §12.2 安全转换（失败产 null）
        public bool IsSafe { get; }

        public CastInstruction(BilVariableOperand source, BilVariableOperand target,
            BilTypeOperand targetType, bool isSafe)
        {
            Source = source;
            Target = target;
            TargetType = targetType;
            IsSafe = isSafe;
        }

        internal override string Opcode => IsSafe ? "cast.safe" : "cast";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, Target, TargetType };
    }

    // §12.3 类型检查种类
    public enum BilTypeCheckKind
    {
        Is,         // type.is：运行时类型是目标类型或其子类型
        Supers,     // type.supers：运行时类型是目标类型的超类型
        With,       // type.with：运行时类型附着的 wrapper 链含目标 wrapper
    }

    // §12.3 类型检查基类：type.X VALUE ... RESULT（双形态见子类）
    public abstract class TypeCheckInstruction : BilInstruction
    {
        public BilTypeCheckKind Kind { get; }
        public BilVariableOperand Value { get; }
        public BilVariableOperand Target { get; }

        protected TypeCheckInstruction(BilTypeCheckKind kind, BilVariableOperand value,
            BilVariableOperand target)
        {
            Kind = kind;
            Value = value;
            Target = target;
        }

        // 动态形态追加 .indirect 后缀
        internal abstract bool IsIndirect { get; }
        internal override string Opcode =>
            BilSpellings.Of(Kind) + (IsIndirect ? ".indirect" : "");
    }

    // §12.3 静态形态：type.X VALUE type(TARGET_TYPE) RESULT
    public sealed class DirectTypeCheckInstruction : TypeCheckInstruction
    {
        public BilTypeOperand TargetType { get; }

        public DirectTypeCheckInstruction(BilTypeCheckKind kind, BilVariableOperand value,
            BilTypeOperand targetType, BilVariableOperand target)
            : base(kind, value, target)
        {
            TargetType = targetType;
        }

        internal override bool IsIndirect => false;
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, TargetType, Target };
    }

    // §12.3 动态形态：type.X.indirect VALUE TYPEID_VAR RESULT
    public sealed class IndirectTypeCheckInstruction : TypeCheckInstruction
    {
        public BilVariableOperand TypeId { get; }

        public IndirectTypeCheckInstruction(BilTypeCheckKind kind, BilVariableOperand value,
            BilVariableOperand typeId, BilVariableOperand target)
            : base(kind, value, target)
        {
            TypeId = typeId;
        }

        internal override bool IsIndirect => true;
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, TypeId, Target };
    }
    // §12.3 enum case 判别检查（S11，语义由 RUNTIME §16.3 定义）：
    // type.is.case VALUE case(ENUM_TYPE.CaseName) RESULT——隐藏判别字段与
    // case 编译期判别常量的整数比较；非子类型检查、不比较 payload、
    // 不改变 VALUE 静态类型；判别宽度 u16/u32 是布局内部细节
    public sealed class IsCaseInstruction : BilInstruction
    {
        public BilVariableOperand Value { get; }
        public BilCaseOperand Case { get; }
        public BilVariableOperand Target { get; }

        public IsCaseInstruction(BilVariableOperand value, BilCaseOperand caseOperand,
            BilVariableOperand target)
        {
            Value = value;
            Case = caseOperand;
            Target = target;
        }

        internal override string Opcode => "type.is.case";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, Case, Target };
    }

    // §12.4 取得 wrapper 值：get.wrapper VALUE type(WRAPPER_TYPE) RESULT
    public sealed class GetWrapperInstruction : BilInstruction
    {
        public BilVariableOperand Value { get; }
        public BilTypeOperand WrapperType { get; }
        public BilVariableOperand Target { get; }

        public GetWrapperInstruction(BilVariableOperand value, BilTypeOperand wrapperType,
            BilVariableOperand target)
        {
            Value = value;
            WrapperType = wrapperType;
            Target = target;
        }

        internal override string Opcode => "get.wrapper";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, WrapperType, Target };
    }

    // §12.5 取得宿主实例（proxy 模板）：get.self RESULT
    // 仅 wrapper-proxy 标记的 fn 体内合法；RESULT = 模板所属 wrapper 的
    // TTarget（Entity 恰一泛型参数时的代入结果）
    public sealed class GetSelfInstruction : BilInstruction
    {
        public BilVariableOperand Target { get; }

        public GetSelfInstruction(BilVariableOperand target)
        {
            Target = target;
        }

        internal override string Opcode => "get.self";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Target };
    }

    // §12.6 取得值的 typeid：getid.var VALUE RESULT
    public sealed class GetIdVarInstruction : BilInstruction
    {
        public BilVariableOperand Value { get; }
        public BilVariableOperand Target { get; }

        public GetIdVarInstruction(BilVariableOperand value, BilVariableOperand target)
        {
            Value = value;
            Target = target;
        }

        internal override string Opcode => "getid.var";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, Target };
    }

    // §12.6 取得类型的 typeid：getid.type type(TYPE_SYMBOL) RESULT
    public sealed class GetIdTypeInstruction : BilInstruction
    {
        public BilTypeOperand TargetType { get; }
        public BilVariableOperand Target { get; }

        public GetIdTypeInstruction(BilTypeOperand targetType, BilVariableOperand target)
        {
            TargetType = targetType;
            Target = target;
        }

        internal override string Opcode => "getid.type";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { TargetType, Target };
    }
}
