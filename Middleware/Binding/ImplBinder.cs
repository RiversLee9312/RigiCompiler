using RigiCompiler.Bil;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Binding
{
    /// <summary>
    /// 实现绑定查询（MIDDLEWARE_ARCHITECTURE §3 MW2）：操作类别 + 操作数严格
    /// 类型 + 结果严格类型 + 已解析符号身份 → 唯一实现形态。不含隐式转换、
    /// 候选排序或最佳匹配（source-level overload ranking 已在 frontend 完成，
    /// BIL §3.3）。MW1 覆盖：标量/string 内建运算 + native/直接调用分流。
    /// 查询键为 BIL 类型引用（任意别名形态，内部经 MwTypeKey 归一），
    /// 本层不依赖 MIR——MIR 保留类型身份（MirType），调用方以其 Canonical
    /// 或原始 typeRef 入查均可。
    /// </summary>
    public static class ImplBinder
    {
        public static ImplBinding BindBinary(BilBinaryOp op, string leftType, string rightType, string resultType)
        {
            var left = MwTypeKey.Normalize(leftType);
            var right = MwTypeKey.Normalize(rightType);
            // 内建字符串拼接（BIL §11.2：op 级内建，非 invoke 路径）
            if (MwTypeKey.IsString(left) && MwTypeKey.IsString(right) && op == BilBinaryOp.Add)
            {
                return new RuntimeFaceBinding(RuntimeFaces.StringConcat);
            }
            // string 比较（§11.5：eq/ne 内容相等，排序字典序三态）→ 比较面
            if (MwTypeKey.IsString(left) && MwTypeKey.IsString(right)
                && op is >= BilBinaryOp.CmpEq and <= BilBinaryOp.CmpGe)
            {
                return new StringCompareBinding();
            }

            var key = MwTypeKey.Of(left);
            // .nullable<T> 相等检查（§3.4 nullable 检查标准形态：与 null
            // 资源 cmp.eq/ne）：胖引用恒等（typeid+payload 双段全等；
            // null 双段零天然成立）。排序比较与算术不适用
            if (key.StartsWith("core::Nullable<", System.StringComparison.Ordinal))
            {
                return op switch
                {
                    BilBinaryOp.CmpEq => new PrimitiveOpBinding(PrimitiveOpKind.RefCmpEq),
                    BilBinaryOp.CmpNe => new PrimitiveOpBinding(PrimitiveOpKind.RefCmpNe),
                    _ => throw new MwNotSupportedException(
                        $"MW2 不支持二元运算 {op} 作用于 {leftType}"),
                };
            }
            var isFloat = key is "float" or "double";
            var isBool = key == "bool";
            // char 只有比较（VM 同口径：无 char 算术/位/移位）；比较按
            // UTF-16 码元无符号序（VM 的 char 比较即码元数值序）
            var isChar = key == "char";
            var isUnsigned = key is "u8" or "u16" or "u32" or "u64";
            var isSignedInt = key is "i8" or "i16" or "i32" or "i64";

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
                // §11.4：内建位运算仅整数族（bool/char/float 不落绑定；
                // 此类 BIL 已过不了 Gate，此处为纵深防御）
                BilBinaryOp.BinAnd when isSignedInt || isUnsigned => PrimitiveOpKind.BitAnd,
                BilBinaryOp.BinOr when isSignedInt || isUnsigned => PrimitiveOpKind.BitOr,
                BilBinaryOp.BinXor when isSignedInt || isUnsigned => PrimitiveOpKind.BitXor,
                BilBinaryOp.ShiftLeft when isSignedInt || isUnsigned => PrimitiveOpKind.ShiftLeft,
                BilBinaryOp.ShiftRight when isSignedInt => PrimitiveOpKind.ShiftRightSigned,
                BilBinaryOp.ShiftRight when isUnsigned => PrimitiveOpKind.ShiftRightUnsigned,
                BilBinaryOp.ShiftRightUnsigned when isSignedInt || isUnsigned => PrimitiveOpKind.ShiftRightUnsigned,
                BilBinaryOp.CmpEq when isSignedInt || isUnsigned || isBool || isChar => PrimitiveOpKind.IntCmpEq,
                BilBinaryOp.CmpNe when isSignedInt || isUnsigned || isBool || isChar => PrimitiveOpKind.IntCmpNe,
                BilBinaryOp.CmpLt when isSignedInt => PrimitiveOpKind.IntCmpSLt,
                BilBinaryOp.CmpLe when isSignedInt => PrimitiveOpKind.IntCmpSLe,
                BilBinaryOp.CmpGt when isSignedInt => PrimitiveOpKind.IntCmpSGt,
                BilBinaryOp.CmpGe when isSignedInt => PrimitiveOpKind.IntCmpSGe,
                BilBinaryOp.CmpLt when isUnsigned || isChar => PrimitiveOpKind.IntCmpULt,
                BilBinaryOp.CmpLe when isUnsigned || isChar => PrimitiveOpKind.IntCmpULe,
                BilBinaryOp.CmpGt when isUnsigned || isChar => PrimitiveOpKind.IntCmpUGt,
                BilBinaryOp.CmpGe when isUnsigned || isChar => PrimitiveOpKind.IntCmpUGe,
                BilBinaryOp.CmpEq when isFloat => PrimitiveOpKind.FloatCmpEq,
                BilBinaryOp.CmpNe when isFloat => PrimitiveOpKind.FloatCmpNe,
                BilBinaryOp.CmpLt when isFloat => PrimitiveOpKind.FloatCmpLt,
                BilBinaryOp.CmpLe when isFloat => PrimitiveOpKind.FloatCmpLe,
                BilBinaryOp.CmpGt when isFloat => PrimitiveOpKind.FloatCmpGt,
                BilBinaryOp.CmpGe when isFloat => PrimitiveOpKind.FloatCmpGe,
                _ => throw new MwNotSupportedException(
                    $"MW1 不支持二元运算 {op} 作用于 {leftType} × {rightType}"),
            };
            return new PrimitiveOpBinding(kind);
        }

        public static ImplBinding BindUnary(BilUnaryOp op, string operandType, string resultType)
        {
            var key = MwTypeKey.Of(MwTypeKey.Normalize(operandType));
            var isFloat = key is "float" or "double";
            var isBool = key == "bool";
            // char 无一元运算（VM 同口径）
            var isInt = key is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64";

            var kind = op switch
            {
                BilUnaryOp.Opposite when isInt => PrimitiveOpKind.IntNeg,
                BilUnaryOp.Opposite when isFloat => PrimitiveOpKind.FloatNeg,
                BilUnaryOp.Not when isBool => PrimitiveOpKind.LogicNot,
                BilUnaryOp.BinNot when isInt => PrimitiveOpKind.BitNot,
                _ => throw new MwNotSupportedException(
                    $"MW1 不支持一元运算 {op} 作用于 {operandType}"),
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
            // 实例方法按 VM 同口径细分（MW4）：class → vtable 虚调用；
            // interface → iMap 派发；init/ext/static/struct/enum 方法与全局
            // fn 直调（struct/enum 的 .this 形态随批 3）
            if (target.Owner != null && target.IsVirtualMember)
            {
                return target.Owner.Declaration.Kind switch
                {
                    BilTypeKind.Class => new VirtualCallBinding(target),
                    BilTypeKind.Interface => new InterfaceCallBinding(target),
                    _ => new DirectCallBinding(target),
                };
            }
            return new DirectCallBinding(target);
        }
    }
}
