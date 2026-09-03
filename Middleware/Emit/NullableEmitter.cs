using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// Nullable 值类型装拆箱（typeid = sheet | tag&lt;&lt;56）。数组元素
    /// wrap 路径复用 WrapScalar / WrapFromSlot。
    /// </summary>
    internal static class NullableEmitter
    {
        internal sealed class Wrap : LlvmEmitVisitor<Wrap, MirWrapNullable>
        {
            protected override void VisitCore(MirWrapNullable inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                var value = session.LoadLocal(builder, slots, inst.Source);
                builder.BuildStore(WrapValue(session, builder, slots, inst.Source, value,
                    inst.InnerType), slots[inst.Target].Slot);
            }
        }

        internal sealed class Unwrap : LlvmEmitVisitor<Unwrap, MirUnwrapNullable>
        {
            protected override void VisitCore(MirUnwrapNullable inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                var fat = session.LoadLocal(builder, slots, inst.Source);
                UnwrapToSlot(session, builder, slots, fat, inst.InnerType, inst.Target);
            }
        }

        internal static LLVMValueRef WrapFromSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef slot, MirType inner)
        {
            var abi = ArrayEmitter.ElementAbi(session, inner);
            var sheet = BoxEmitter.TypeSheetOf(session, inner);
            if (IsTag0Wrap(abi))
            {
                var bits = BoxEmitter.BitsFromSlot(session, builder, slot, abi.Stride);
                return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline,
                    bits, "opt");
            }
            var box = Malloc(session, builder, abi.Stride);
            session.EmitMemCopy(builder, box, slot, abi.Stride);
            ArcEmitter.EmitValueAcquire(session, builder, box, inner);
            var payload = builder.BuildPtrToInt(box, LLVMTypeRef.Int64, "opt.box");
            return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagHeapValue,
                payload, "opt");
        }

        internal static LLVMValueRef WrapScalar(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef value, MirType inner)
        {
            var sheet = BoxEmitter.TypeSheetOf(session, inner);
            var bits = ScalarToI64(builder, value, inner);
            return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline,
                bits, "opt");
        }

        private static LLVMValueRef WrapValue(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand source,
            LLVMValueRef value, MirType inner)
        {
            var srcType = source is MirLocalOperand loc
                ? slots[loc.Name].Local.Type
                : inner;
            // extraSubst 把源烤成标量但 inner 仍是占位：按源 ABI 包装，
            // 禁止对 i32 做 ExtractValue。
            if (MirBuilder.IsScalarOrString(srcType))
            {
                if (srcType.IsString && source is MirLocalOperand str)
                {
                    return WrapFromSlot(session, builder, slots[str.Name].Slot, srcType);
                }
                return WrapScalar(session, builder, value, srcType);
            }
            if (IsReferenceElement(session, inner) || IsReferenceElement(session, srcType))
            {
                return ArcEmitter.ProduceFatValue(session, builder, value, "opt.wrap");
            }
            var abi = ArrayEmitter.ElementAbi(session, inner);
            if (IsTag0Wrap(abi))
            {
                if (abi.Kind == ArrayElementKind.InlineValue)
                {
                    if (source is not MirLocalOperand local)
                    {
                        throw new CompilerInternalException("Nullable 装箱源必须是局部");
                    }
                    return WrapFromSlot(session, builder, slots[local.Name].Slot, inner);
                }
                return WrapScalar(session, builder, value, inner);
            }
            if (source is not MirLocalOperand boxed)
            {
                throw new CompilerInternalException("Nullable 装箱源必须是局部");
            }
            return WrapFromSlot(session, builder, slots[boxed.Name].Slot, inner);
        }

        private static void UnwrapToSlot(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef fat, MirType inner, string target)
        {
            var destType = slots[target].Local.Type;
            if (MirBuilder.IsScalarOrString(destType))
            {
                inner = destType;
            }
            else if (IsReferenceElement(session, inner) || IsReferenceElement(session, destType))
            {
                builder.BuildStore(ArcEmitter.ProduceFatValue(session, builder, fat, "opt.unwrap"),
                    slots[target].Slot);
                return;
            }
            var typeId = builder.BuildExtractValue(fat, 0, "opt.typeid");
            var payload = builder.BuildExtractValue(fat, 1, "opt.payload");
            var tag = builder.BuildLShr(typeId,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, BoxEmitter.TagShift, false),
                "opt.tag");
            var isTag0 = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tag,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, BoxEmitter.TagInline, false),
                "opt.tag0");
            var fn = session.CurrentFunction;
            var tag0Block = fn.AppendBasicBlock("opt.tag0");
            var tag1Block = fn.AppendBasicBlock("opt.tag1");
            var joinBlock = fn.AppendBasicBlock("opt.join");
            builder.BuildCondBr(isTag0, tag0Block, tag1Block);

            var abi = ArrayEmitter.ElementAbi(session, inner);
            builder.PositionAtEnd(tag0Block);
            if (abi.Kind is ArrayElementKind.InlineValue or ArrayElementKind.String)
            {
                var tmp = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "opt.bits");
                builder.BuildStore(payload, tmp);
                session.EmitMemCopy(builder, slots[target].Slot, tmp,
                    System.Math.Min(abi.Stride, 8));
            }
            else
            {
                builder.BuildStore(UnboxScalarBits(builder, payload, inner),
                    slots[target].Slot);
            }
            builder.BuildBr(joinBlock);

            builder.PositionAtEnd(tag1Block);
            var box = builder.BuildIntToPtr(payload, PointerType(), "opt.box");
            session.EmitMemCopy(builder, slots[target].Slot, box, abi.Stride);
            ArcEmitter.EmitValueAcquire(session, builder, slots[target].Slot, inner);
            builder.BuildBr(joinBlock);

            builder.PositionAtEnd(joinBlock);
        }

        private static bool IsReferenceElement(ModuleBuilder.Session session, MirType element) =>
            ArrayEmitter.ElementAbi(session, element).Kind == ArrayElementKind.Reference;

        private static bool IsTag0Wrap(ArrayElementAbi abi)
        {
            if (abi.Kind == ArrayElementKind.String)
            {
                return false;
            }
            if (abi.Kind == ArrayElementKind.InlineValue)
            {
                var rich = abi.Plan is { } plan
                    && (plan.TypeFlags & TypeLayoutPlan.FlagRich) != 0;
                return !rich && abi.Stride <= BoxEmitter.InlineLimit;
            }
            return abi.Kind == ArrayElementKind.Scalar;
        }

        private static LLVMValueRef ScalarToI64(LLVMBuilderRef builder, LLVMValueRef value,
            MirType inner)
        {
            if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                return builder.BuildPtrToInt(value, LLVMTypeRef.Int64, "opt.tid");
            }
            switch (inner.Key)
            {
                case "float":
                    return builder.BuildZExt(
                        builder.BuildBitCast(value, LLVMTypeRef.Int32, "opt.fbits"),
                        LLVMTypeRef.Int64, "opt.z");
                case "double":
                    return builder.BuildBitCast(value, LLVMTypeRef.Int64, "opt.dbits");
                case "bool":
                    return builder.BuildZExt(value, LLVMTypeRef.Int64, "opt.z");
                default:
                    return value.TypeOf.IntWidth < 64
                        ? builder.BuildZExt(value, LLVMTypeRef.Int64, "opt.z")
                        : value;
            }
        }

        private static LLVMValueRef UnboxScalarBits(LLVMBuilderRef builder, LLVMValueRef bits,
            MirType inner)
        {
            switch (inner.Key)
            {
                case "float":
                    return builder.BuildBitCast(
                        builder.BuildTrunc(bits, LLVMTypeRef.Int32, "opt.t"),
                        LLVMTypeRef.Float, "opt.f");
                case "double":
                    return builder.BuildBitCast(bits, LLVMTypeRef.Int64, "opt.d");
                case "bool":
                    return builder.BuildTrunc(bits, LLVMTypeRef.Int1, "opt.b");
                default:
                    if (TypeLayout.IsTypeId(inner))
                    {
                        return builder.BuildIntToPtr(bits, PointerType(), "opt.tid");
                    }
                    var width = inner.Key switch
                    {
                        "i8" or "u8" => LLVMTypeRef.Int8,
                        "char" or "i16" or "u16" => LLVMTypeRef.Int16,
                        "i32" or "u32" => LLVMTypeRef.Int32,
                        "i64" or "u64" => LLVMTypeRef.Int64,
                        _ => throw new CompilerInternalException(
                            $"未覆盖的 Nullable 标量拆箱: {inner.Canonical}"),
                    };
                    return inner.Key is "i64" or "u64"
                        ? bits
                        : builder.BuildTrunc(bits, width, "opt.t");
            }
        }

        private static LLVMValueRef Malloc(ModuleBuilder.Session session, LLVMBuilderRef builder,
            int size)
        {
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.Malloc,
                PointerType(), new[] { LLVMTypeRef.Int32 });
            return builder.BuildCall2(fnType, fn,
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)size, false) },
                "box.mem");
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
