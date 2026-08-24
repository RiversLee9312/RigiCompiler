using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// 实现绑定查询（MIDDLEWARE_ARCHITECTURE §3 MW2）：操作类别 + 操作数严格
    /// 类型 + 结果严格类型 + 已解析符号身份 → 唯一实现形态。不含隐式转换、
    /// 候选排序或最佳匹配（source-level overload ranking 已在 frontend 完成，
    /// BIL §3.3）。MW1 覆盖：标量/string 内建运算 + native/直接调用分流。
    /// </summary>
    public static class ImplBinder
    {
        public static ImplBinding BindBinary(BilBinaryOp op, MirType left, MirType right, MirType result)
        {
            // 内建字符串拼接（BIL §11.2：op 级内建，非 invoke 路径）
            if (left.IsString && right.IsString && op == BilBinaryOp.Add)
            {
                return new RuntimeFaceBinding(RuntimeFaces.StringConcat);
            }

            var key = left.Key;
            var isFloat = key is "float" or "double";
            var isBool = key == "bool";
            var isUnsigned = key is "u8" or "u16" or "u32" or "u64";
            var isSignedInt = key is "i8" or "i16" or "i32" or "i64" or "char";

            var kind = op switch
            {
                BilBinaryOp.Add when isSignedInt || isUnsigned => PrimitiveOpKind.IntAdd,
                BilBinaryOp.Sub when isSignedInt || isUnsigned => PrimitiveOpKind.IntSub,
                BilBinaryOp.Mul when isSignedInt || isUnsigned => PrimitiveOpKind.IntMul,
                BilBinaryOp.Div when isSignedInt => PrimitiveOpKind.IntSDiv,
                BilBinaryOp.Div when isUnsigned => PrimitiveOpKind.IntUDiv,
                BilBinaryOp.Add when isFloat => PrimitiveOpKind.FloatAdd,
                BilBinaryOp.Sub when isFloat => PrimitiveOpKind.FloatSub,
                BilBinaryOp.Mul when isFloat => PrimitiveOpKind.FloatMul,
                BilBinaryOp.Div when isFloat => PrimitiveOpKind.FloatDiv,
                BilBinaryOp.And when isBool => PrimitiveOpKind.LogicAnd,
                BilBinaryOp.Or when isBool => PrimitiveOpKind.LogicOr,
                BilBinaryOp.BinAnd when isSignedInt || isUnsigned || isBool => PrimitiveOpKind.BitAnd,
                BilBinaryOp.BinOr when isSignedInt || isUnsigned || isBool => PrimitiveOpKind.BitOr,
                BilBinaryOp.BinXor when isSignedInt || isUnsigned || isBool => PrimitiveOpKind.BitXor,
                BilBinaryOp.ShiftLeft when isSignedInt || isUnsigned => PrimitiveOpKind.ShiftLeft,
                BilBinaryOp.ShiftRight when isSignedInt => PrimitiveOpKind.ShiftRightSigned,
                BilBinaryOp.ShiftRight when isUnsigned => PrimitiveOpKind.ShiftRightUnsigned,
                BilBinaryOp.ShiftRightUnsigned when isSignedInt || isUnsigned => PrimitiveOpKind.ShiftRightUnsigned,
                BilBinaryOp.CmpEq when isSignedInt || isUnsigned || isBool => PrimitiveOpKind.IntCmpEq,
                BilBinaryOp.CmpNe when isSignedInt || isUnsigned || isBool => PrimitiveOpKind.IntCmpNe,
                BilBinaryOp.CmpLt when isSignedInt => PrimitiveOpKind.IntCmpSLt,
                BilBinaryOp.CmpLe when isSignedInt => PrimitiveOpKind.IntCmpSLe,
                BilBinaryOp.CmpGt when isSignedInt => PrimitiveOpKind.IntCmpSGt,
                BilBinaryOp.CmpGe when isSignedInt => PrimitiveOpKind.IntCmpSGe,
                BilBinaryOp.CmpLt when isUnsigned => PrimitiveOpKind.IntCmpULt,
                BilBinaryOp.CmpLe when isUnsigned => PrimitiveOpKind.IntCmpULe,
                BilBinaryOp.CmpGt when isUnsigned => PrimitiveOpKind.IntCmpUGt,
                BilBinaryOp.CmpGe when isUnsigned => PrimitiveOpKind.IntCmpUGe,
                BilBinaryOp.CmpEq when isFloat => PrimitiveOpKind.FloatCmpEq,
                BilBinaryOp.CmpNe when isFloat => PrimitiveOpKind.FloatCmpNe,
                BilBinaryOp.CmpLt when isFloat => PrimitiveOpKind.FloatCmpLt,
                BilBinaryOp.CmpLe when isFloat => PrimitiveOpKind.FloatCmpLe,
                BilBinaryOp.CmpGt when isFloat => PrimitiveOpKind.FloatCmpGt,
                BilBinaryOp.CmpGe when isFloat => PrimitiveOpKind.FloatCmpGe,
                _ => throw new MwNotSupportedException(
                    $"MW1 不支持二元运算 {op} 作用于 {left.Canonical} × {right.Canonical}"),
            };
            return new PrimitiveOpBinding(kind);
        }

        public static ImplBinding BindUnary(BilUnaryOp op, MirType operand, MirType result)
        {
            var key = operand.Key;
            var isFloat = key is "float" or "double";
            var isBool = key == "bool";
            var isInt = key is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64" or "char";

            var kind = op switch
            {
                BilUnaryOp.Opposite when isInt => PrimitiveOpKind.IntNeg,
                BilUnaryOp.Opposite when isFloat => PrimitiveOpKind.FloatNeg,
                BilUnaryOp.Not when isBool => PrimitiveOpKind.LogicNot,
                BilUnaryOp.BinNot when isInt => PrimitiveOpKind.BitNot,
                _ => throw new MwNotSupportedException(
                    $"MW1 不支持一元运算 {op} 作用于 {operand.Canonical}"),
            };
            return new PrimitiveOpBinding(kind);
        }

        // native 声明 → NativeDirectBinding；其余 → 模块内直接调用
        public static ImplBinding BindCall(MwMemberSymbol target)
        {
            string? library = null;
            string? nativeSymbol = null;
            var isNative = false;
            foreach (var modifier in target.Declaration.Modifiers)
            {
                switch (modifier)
                {
                    case BilKeywordModifier { Keyword: BilKeyword.Native }:
                        isNative = true;
                        break;
                    case BilNativeLibraryModifier lib:
                        library = lib.Library;
                        break;
                    case BilNativeSymbolModifier sym:
                        nativeSymbol = sym.Symbol;
                        break;
                }
            }
            if (isNative)
            {
                // verifier §21 已强制 symbol(...)/lib(...) 与 native 同现且恰一次；防御
                if (library == null || nativeSymbol == null)
                {
                    throw new CompilerInternalException($"native 声明缺 symbol/lib 修饰符: {target.Canonical}");
                }
                return new NativeDirectBinding(library, nativeSymbol);
            }
            return new DirectCallBinding(target);
        }
    }
}
