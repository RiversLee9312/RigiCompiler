using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 标量运算发射（Emit 分面）：MirBinaryIntrinsic/MirUnaryIntrinsic 经
    /// ImplBinder 绑定后的 PrimitiveOpKind → LLVM builder 指令选择（机械
    /// 映射全表在此）。string + 的运行时面形态转 CallEmitter 面调用。
    /// </summary>
    internal static class ScalarEmitter
    {
        internal sealed class Binary : LlvmEmitVisitor<Binary, MirBinaryIntrinsic>
        {
            protected override void VisitCore(MirBinaryIntrinsic inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                var left = session.LoadLocal(builder, slots, inst.Left);
                var right = session.LoadLocal(builder, slots, inst.Right);
                LLVMValueRef value;
                switch (ImplBinder.BindBinary(inst.Op, inst.LeftType.Canonical, inst.RightType.Canonical, inst.ResultType.Canonical))
                {
                    case PrimitiveOpBinding primitive:
                        // 整数除法：先经策略注入点发射运行时检查（MW9b-G 抛
                        // DividedByZeroException；异常边 = 本指令 ExcTarget）
                        if (primitive.Kind is PrimitiveOpKind.IntSDiv or PrimitiveOpKind.IntUDiv)
                        {
                            session.Checks.EmitDivGuard(session, builder, left, right,
                                isSigned: primitive.Kind == PrimitiveOpKind.IntSDiv,
                                inst.ExcTarget);
                        }
                        // 整数取模（mod-3）：同一除零异常面；无 MIN/-1 abort
                        // 臂（BuildSignedMod select 消毒后 srem 无 UB 面）
                        else if (primitive.Kind is PrimitiveOpKind.IntSMod or PrimitiveOpKind.IntUMod)
                        {
                            session.Checks.EmitModGuard(session, builder, right,
                                inst.ExcTarget);
                        }
                        value = SelectPrimitive(builder, primitive.Kind, left, right);
                        break;
                    case RuntimeFaceBinding face:
                        value = CallEmitter.EmitFaceCall(session, builder, face.FaceSymbol, new[] { left, right });
                        break;
                    case StringCompareBinding:
                        value = EmitStringCompare(session, builder, inst.Op, left, right);
                        break;
                    default:
                        throw new CompilerInternalException("二元运算的非预期绑定形态");
                }
                builder.BuildStore(value, slots[inst.Target].Slot);
            }
        }

        internal sealed class Unary : LlvmEmitVisitor<Unary, MirUnaryIntrinsic>
        {
            protected override void VisitCore(MirUnaryIntrinsic inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                var operand = session.LoadLocal(builder, slots, inst.Operand);
                if (ImplBinder.BindUnary(inst.Op, inst.OperandType.Canonical, inst.ResultType.Canonical) is not PrimitiveOpBinding primitive)
                {
                    throw new CompilerInternalException("一元运算的非预期绑定形态");
                }
                var value = primitive.Kind switch
                {
                    PrimitiveOpKind.IntNeg => builder.BuildNeg(operand, "neg"),
                    PrimitiveOpKind.FloatNeg => builder.BuildFNeg(operand, "fneg"),
                    PrimitiveOpKind.LogicNot => builder.BuildNot(operand, "not"),
                    PrimitiveOpKind.BitNot => builder.BuildNot(operand, "binnot"),
                    _ => throw new CompilerInternalException($"未覆盖的一元指令选择: {primitive.Kind}"),
                };
                builder.BuildStore(value, slots[inst.Target].Slot);
            }
        }

        // string 比较降级（VM 基准：eq/ne 内容相等；排序为字典序三态）：
        // rigi_string_compare(a, b) 的 i32 三态结果与 0 做次序判定
        internal static LLVMValueRef EmitStringCompare(ModuleBuilder.Session session,
            LLVMBuilderRef builder, BilBinaryOp op, LLVMValueRef left, LLVMValueRef right)
        {
            var cmp = CallEmitter.EmitStringCompareCall(session, builder, left, right);
            var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false);
            var predicate = op switch
            {
                BilBinaryOp.CmpEq => LLVMIntPredicate.LLVMIntEQ,
                BilBinaryOp.CmpNe => LLVMIntPredicate.LLVMIntNE,
                BilBinaryOp.CmpLt => LLVMIntPredicate.LLVMIntSLT,
                BilBinaryOp.CmpLe => LLVMIntPredicate.LLVMIntSLE,
                BilBinaryOp.CmpGt => LLVMIntPredicate.LLVMIntSGT,
                BilBinaryOp.CmpGe => LLVMIntPredicate.LLVMIntSGE,
                _ => throw new CompilerInternalException($"string 比较的非预期运算: {op}"),
            };
            return builder.BuildICmp(predicate, cmp, zero, "string.cmp");
        }

        // G4 复用给 GenericOpEmitter 的内建臂
        internal static LLVMValueRef SelectPrimitive(LLVMBuilderRef builder, PrimitiveOpKind kind,
            LLVMValueRef left, LLVMValueRef right)
        {
            return kind switch
            {
                PrimitiveOpKind.IntAdd => builder.BuildAdd(left, right, "add"),
                PrimitiveOpKind.IntSub => builder.BuildSub(left, right, "sub"),
                PrimitiveOpKind.IntMul => builder.BuildMul(left, right, "mul"),
                PrimitiveOpKind.IntSDiv => BuildSignedDiv(builder, left, right),
                PrimitiveOpKind.IntUDiv => builder.BuildUDiv(left, right, "udiv"),
                PrimitiveOpKind.IntSMod => BuildSignedMod(builder, left, right),
                PrimitiveOpKind.IntUMod => builder.BuildURem(left, right, "urem"),
                PrimitiveOpKind.FloatAdd => builder.BuildFAdd(left, right, "fadd"),
                PrimitiveOpKind.FloatSub => builder.BuildFSub(left, right, "fsub"),
                PrimitiveOpKind.FloatMul => builder.BuildFMul(left, right, "fmul"),
                PrimitiveOpKind.FloatDiv => builder.BuildFDiv(left, right, "fdiv"),
                // frem = IEEE 754 截断余数：模零得 NaN（无 guard，与
                // FloatDiv 无除零检查同款）
                PrimitiveOpKind.FloatMod => builder.BuildFRem(left, right, "frem"),
                PrimitiveOpKind.LogicAnd => builder.BuildAnd(left, right, "and"),
                PrimitiveOpKind.LogicOr => builder.BuildOr(left, right, "or"),
                PrimitiveOpKind.BitAnd => builder.BuildAnd(left, right, "bitand"),
                PrimitiveOpKind.BitOr => builder.BuildOr(left, right, "bitor"),
                PrimitiveOpKind.BitXor => builder.BuildXor(left, right, "bitxor"),
                PrimitiveOpKind.ShiftLeft => builder.BuildShl(left, MaskShiftCount(builder, left, right), "shl"),
                PrimitiveOpKind.ShiftRightSigned => builder.BuildAShr(left, MaskShiftCount(builder, left, right), "ashr"),
                PrimitiveOpKind.ShiftRightUnsigned => builder.BuildLShr(left, MaskShiftCount(builder, left, right), "lshr"),
                PrimitiveOpKind.IntCmpEq => builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, "eq"),
                PrimitiveOpKind.IntCmpNe => builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, "ne"),
                PrimitiveOpKind.IntCmpSLt => builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right, "slt"),
                PrimitiveOpKind.IntCmpSLe => builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right, "sle"),
                PrimitiveOpKind.IntCmpSGt => builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right, "sgt"),
                PrimitiveOpKind.IntCmpSGe => builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right, "sge"),
                PrimitiveOpKind.IntCmpULt => builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, left, right, "ult"),
                PrimitiveOpKind.IntCmpULe => builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, left, right, "ule"),
                PrimitiveOpKind.IntCmpUGt => builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, left, right, "ugt"),
                PrimitiveOpKind.IntCmpUGe => builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, left, right, "uge"),
                PrimitiveOpKind.FloatCmpEq => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right, "feq"),
                // NaN 语义对齐 VM（C# !=）：无序或不等为真——ONE 对 NaN
                // 操作数得 false 会背离 C# 的 NaN != NaN == true
                PrimitiveOpKind.FloatCmpNe => builder.BuildFCmp(LLVMRealPredicate.LLVMRealUNE, left, right, "fne"),
                PrimitiveOpKind.FloatCmpLt => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right, "flt"),
                PrimitiveOpKind.FloatCmpLe => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right, "fle"),
                PrimitiveOpKind.FloatCmpGt => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right, "fgt"),
                PrimitiveOpKind.FloatCmpGe => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right, "fge"),
                PrimitiveOpKind.RefCmpEq => BuildRefCompare(builder, left, right, negate: false),
                PrimitiveOpKind.RefCmpNe => BuildRefCompare(builder, left, right, negate: true),
                _ => throw new CompilerInternalException($"未覆盖的二元指令选择: {kind}"),
            };
        }

        // 有符号除法（VM 基准）：i8/i16/i32 的 MIN/-1 回绕——以「divisor
        // == -1 时改取负」消 LLVM sdiv 的溢出 UB（取负对 MIN 回绕得 MIN，
        // 与 VM 的 long 提升后截断同值；divisor 消毒为 1 使 sdiv 永不
        // 溢出）；i64 MIN/-1 已由策略面拦截，此处纯 sdiv
        private static LLVMValueRef BuildSignedDiv(LLVMBuilderRef builder,
            LLVMValueRef left, LLVMValueRef right)
        {
            if (left.TypeOf.IntWidth >= 64)
            {
                return builder.BuildSDiv(left, right, "sdiv");
            }
            var one = LLVMValueRef.CreateConstInt(right.TypeOf, 1, false);
            var isNegOne = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right,
                LLVMValueRef.CreateConstAllOnes(right.TypeOf), "sdiv.negone");
            var safeDivisor = builder.BuildSelect(isNegOne, one, right, "sdiv.safe");
            var raw = builder.BuildSDiv(left, safeDivisor, "sdiv");
            var negated = builder.BuildSub(LLVMValueRef.CreateConstNull(left.TypeOf), left, "sdiv.neg");
            return builder.BuildSelect(isNegOne, negated, raw, "sdiv.wrap");
        }

        // 有符号取模（mod-3，VM 基准 x % ±1 == 0）：全位宽统一处理——
        // divisor == -1 时消毒为 1，srem 永不命中 MIN/-1 UB，命中臂结果
        // 恒 0 以 select 直接给出（与 VM 的「模 ±1 得 0」同值）。取模的
        // 消毒形态与除法不同：div 是改取负（结果非零），mod 恒 0，且
        // i64 同样走此路径——不引入 MIN/-1 abort 臂
        private static LLVMValueRef BuildSignedMod(LLVMBuilderRef builder,
            LLVMValueRef left, LLVMValueRef right)
        {
            var isNegOne = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right,
                LLVMValueRef.CreateConstAllOnes(right.TypeOf), "smod.negone");
            var one = LLVMValueRef.CreateConstInt(right.TypeOf, 1, false);
            var safeDivisor = builder.BuildSelect(isNegOne, one, right, "smod.safe");
            var raw = builder.BuildSRem(left, safeDivisor, "srem");
            return builder.BuildSelect(isNegOne,
                LLVMValueRef.CreateConstNull(left.TypeOf), raw, "smod.wrap");
        }

        // 胖引用恒等：{typeid, payload} 双段各自相等取与（null 双段零天然
        // 成立——null 与 null 相等、null 与非空不等）
        private static LLVMValueRef BuildRefCompare(LLVMBuilderRef builder,
            LLVMValueRef left, LLVMValueRef right, bool negate)
        {
            var tagEq = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
                builder.BuildExtractValue(left, 0, "ref.l.tag"),
                builder.BuildExtractValue(right, 0, "ref.r.tag"), "ref.tag.eq");
            var payloadEq = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
                builder.BuildExtractValue(left, 1, "ref.l.payload"),
                builder.BuildExtractValue(right, 1, "ref.r.payload"), "ref.payload.eq");
            var both = builder.BuildAnd(tagEq, payloadEq, "ref.eq");
            return negate ? builder.BuildNot(both, "ref.ne") : both;
        }

        // 移位量按位宽掩码（count & (width-1)，VM 参考实现同口径——§11.4
        // 未规定 overshift，以 VM 为对拍基准；LLVM 原生 overshift 是 poison，
        // 必须显式掩码对齐）
        private static LLVMValueRef MaskShiftCount(LLVMBuilderRef builder, LLVMValueRef value,
            LLVMValueRef count)
        {
            var mask = LLVMValueRef.CreateConstInt(value.TypeOf, value.TypeOf.IntWidth - 1, false);
            return builder.BuildAnd(count, mask, "shift.count");
        }
    }
}
