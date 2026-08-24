namespace RigiCompiler.Middleware
{
    // MW2 实现绑定（MIDDLEWARE_ARCHITECTURE §3）：类型驱动操作的唯一实现查询。
    // 查询结果是与 LLVM 无关的实现形态描述（指令选择归 Emit）；本层不知道
    // LLVM 的存在。

    public abstract record ImplBinding;

    // primitive 运算 → Emit 指令选择
    public sealed record PrimitiveOpBinding(PrimitiveOpKind Kind) : ImplBinding;

    // primitive 指令选择键（Emit 映射到 LLVM builder 调用）
    public enum PrimitiveOpKind
    {
        IntAdd, IntSub, IntMul, IntSDiv, IntUDiv,
        FloatAdd, FloatSub, FloatMul, FloatDiv,
        LogicAnd, LogicOr, LogicNot,
        BitAnd, BitOr, BitXor, BitNot,
        ShiftLeft, ShiftRightSigned, ShiftRightUnsigned,
        IntCmpEq, IntCmpNe, IntCmpSLt, IntCmpSLe, IntCmpSGt, IntCmpSGe,
        IntCmpULt, IntCmpULe, IntCmpUGt, IntCmpUGe,
        FloatCmpEq, FloatCmpNe, FloatCmpLt, FloatCmpLe, FloatCmpGt, FloatCmpGe,
        IntNeg, FloatNeg,
    }

    // 内建运行时面调用（如 add(.string,.string) → rigi_string_concat）；
    // 面的 C 符号与调用形状由 Emit 的 RuntimeFaces 表描述
    public sealed record RuntimeFaceBinding(string FaceSymbol) : ImplBinding;

    // native 声明 → 对 (lib, symbol) 的直接 C 调用（RUNTIME §26）
    public sealed record NativeDirectBinding(string Library, string Symbol) : ImplBinding;

    // 本地普通 fn → 模块内直接调用
    public sealed record DirectCallBinding(MwMemberSymbol Target) : ImplBinding;
}
