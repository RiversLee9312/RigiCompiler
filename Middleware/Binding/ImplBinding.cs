using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Binding
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
        IntAdd, IntSub, IntMul, IntSDiv, IntUDiv, IntSMod, IntUMod,
        FloatAdd, FloatSub, FloatMul, FloatDiv, FloatMod,
        LogicAnd, LogicOr, LogicNot,
        BitAnd, BitOr, BitXor, BitNot,
        ShiftLeft, ShiftRightSigned, ShiftRightUnsigned,
        IntCmpEq, IntCmpNe, IntCmpSLt, IntCmpSLe, IntCmpSGt, IntCmpSGe,
        IntCmpULt, IntCmpULe, IntCmpUGt, IntCmpUGe,
        FloatCmpEq, FloatCmpNe, FloatCmpLt, FloatCmpLe, FloatCmpGt, FloatCmpGe,
        IntNeg, FloatNeg,
        RefCmpEq, RefCmpNe,
    }

    // 内建运行时面调用（如 add(.string,.string) → rigi_string_concat）；
    // 面的 C 符号与调用形状由 Runtime 的 RuntimeFaces 表描述
    public sealed record RuntimeFaceBinding(string FaceSymbol) : ImplBinding;

    // string 排序/相等比较（§11.5：eq/ne 内容相等 equals 语义，排序为
    // compareTo 三态语义）→ rigi_string_compare 面；六种比较对 i32 三态
    // 结果的次序判定归 Emit（ScalarEmitter 以运算种类选谓词）
    public sealed record StringCompareBinding : ImplBinding;

    // native 声明 → 对 (lib, symbol) 的直接 C 调用（RUNTIME §26）
    public sealed record NativeDirectBinding(string Library, string Symbol) : ImplBinding;

    // 本地普通 fn → 模块内直接调用
    public sealed record DirectCallBinding(MwMemberSymbol Target) : ImplBinding;

    // class 实例方法（VM 同口径：全部实例方法经 vtable 派发）→ 对象头
    // 实际 TypeSheet → vTable[slot] 间接调用；slot 由 Layout 计划回答
    public sealed record VirtualCallBinding(MwMemberSymbol Target) : ImplBinding;

    // interface 实例方法 → iMap 查 base offset + 接口内槽序间接调用
    //（接口符号无 fn 体，fn 类型由 canonical 签名合成）
    public sealed record InterfaceCallBinding(MwMemberSymbol Target) : ImplBinding;

    // invoke.indirect（§15.3 callable 协议）：静态类型上唯一匹配的 $$call
    // 虚成员。与 Virtual/Interface 并列——语义独立、来源不同（BindIndirectCall
    // 沿 extends 链解析，而非 BindCall 的已解析符号分流）
    public sealed record IndirectCallBinding(MwMemberSymbol CallOperator) : ImplBinding;
}
