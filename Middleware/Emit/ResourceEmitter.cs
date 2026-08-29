using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 资源物化发射（Emit 分面）：MirLoadResource 的 BilScalarResource 标量
    /// 字面量 → LLVM 常量并落目标局部槽。字符串常量构建委托 StringAbi
    /// （表示的唯一事实源）。
    /// </summary>
    internal sealed class ResourceEmitter : LlvmEmitVisitor<ResourceEmitter, MirLoadResource>
    {
        protected override void VisitCore(MirLoadResource load, ModuleBuilder.Session session)
        {
            var builder = session.Builder;
            var slots = session.Slots;
            var targetType = slots[load.Target].Local.Type;
            if (load.Resource is BilScalarResource scalar
                && scalar.Type is BilScalarType.RawHex or BilScalarType.RawBin)
            {
                EmitRawArray(session, builder, slots, load.Target, targetType, scalar);
                return;
            }
            builder.BuildStore(BuildResourceValue(session, load.Resource, targetType),
                slots[load.Target].Slot);
        }

        private static void EmitRawArray(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, string target,
            MirType targetType, BilScalarResource scalar)
        {
            if (!TypeLayout.IsArray(targetType)
                || !TypeLayout.TryGetArrayElement(targetType, out var element)
                || element.Key != "u8")
            {
                throw new MwNotSupportedException(
                    $"raw.hex/raw.bin 仅支持物化为 .array<u8>（当前目标 {targetType.Canonical}）: {scalar.Name}");
            }
            ArrayEmitter.MaterializeU8Array(session, builder, slots[target].Slot,
                DecodeRaw(scalar), scalar.Name);
        }

        private static byte[] DecodeRaw(BilScalarResource scalar)
        {
            var text = scalar.LiteralText;
            if (scalar.Type == BilScalarType.RawHex)
            {
                var hex = text.StartsWith("x", System.StringComparison.Ordinal)
                    || text.StartsWith("X", System.StringComparison.Ordinal)
                    ? text.Substring(1) : text;
                if ((hex.Length & 1) != 0)
                {
                    throw new CompilerInternalException($"raw.hex 长度非偶: {scalar.Name}");
                }
                var bytes = new byte[hex.Length / 2];
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }
                return bytes;
            }
            var bits = text.StartsWith("b", System.StringComparison.Ordinal)
                || text.StartsWith("B", System.StringComparison.Ordinal)
                ? text.Substring(1) : text;
            if (bits.Length == 0 || bits.Length % 8 != 0)
            {
                throw new CompilerInternalException($"raw.bin 位长须为 8 的倍数: {scalar.Name}");
            }
            var data = new byte[bits.Length / 8];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = System.Convert.ToByte(bits.Substring(i * 8, 8), 2);
            }
            return data;
        }

        private static LLVMValueRef BuildResourceValue(ModuleBuilder.Session session,
            BilResource resource, MirType targetType)
        {
            // null type(T)（§19.1）：资源类型 .nullable<T>——RUNTIME §3
            // Nullable 是 Object 子类，null = 胖引用双段零 → 目标槽的零常量
            if (resource is BilNullResource)
            {
                return LLVMValueRef.CreateConstNull(TypeLayout.MapType(session.Context, targetType));
            }
            if (resource is not BilScalarResource scalar)
            {
                throw new MwNotSupportedException(
                    $"MW2 暂不支持资源形态（array/pair/map 随 MW4 对象系统、catch-table 随 MW9）: {resource.Name}");
            }
            var context = session.Context;
            var text = scalar.LiteralText;
            switch (scalar.Type)
            {
                case BilScalarType.RawHex or BilScalarType.RawBin:
                    throw new MwNotSupportedException(
                        $"raw.hex/raw.bin 仅支持物化为 .array<u8>（当前目标 {targetType.Canonical}）: {resource.Name}");
                case BilScalarType.String:
                    return StringAbi.BuildConstant(session.Module, BilScalarLiteral.DecodeString(text), resource.Name);
                case BilScalarType.Bool:
                    return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, text == "true" ? 1u : 0u, false);
                case BilScalarType.Char:
                    return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, BilScalarLiteral.DecodeChar(text), false);
                case BilScalarType.I8 or BilScalarType.I16 or BilScalarType.I32 or BilScalarType.I64:
                    return LLVMValueRef.CreateConstInt(TypeLayout.MapType(context, targetType),
                        unchecked((ulong)BilScalarLiteral.ParseSigned(text)), true);
                case BilScalarType.U8 or BilScalarType.U16 or BilScalarType.U32 or BilScalarType.U64:
                    return LLVMValueRef.CreateConstInt(TypeLayout.MapType(context, targetType),
                        BilScalarLiteral.ParseUnsigned(text), false);
                case BilScalarType.F32:
                    return LLVMValueRef.CreateConstRealOfStringAndSize(LLVMTypeRef.Float, text, (uint)text.Length);
                case BilScalarType.F64:
                    return LLVMValueRef.CreateConstRealOfStringAndSize(LLVMTypeRef.Double, text, (uint)text.Length);
                default:
                    throw new MwNotSupportedException($"MW1 不支持资源类型 {scalar.Type}: {resource.Name}");
            }
        }
    }
}
