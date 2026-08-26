using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// Any/Box 胖值物化（MW5 切片 c1，RUNTIME §2/§4）：tag 编码与 pack/
    /// unpack 的唯一 LLVM 发射点。CallEmitter 的 class 胖引用构造消费
    /// PackObject，避免 tag2 双实现漂移。
    /// </summary>
    internal static class BoxEmitter
    {
        // RUNTIME §2：typeid 最高字节分类 tag
        internal const ulong TagInline = 0UL;
        internal const ulong TagHeapValue = 1UL;
        internal const ulong TagObject = 2UL;
        internal const int InlineLimit = 8;
        internal const int TagShift = 56;
        internal const ulong SheetMask = 0x00FFFFFFFFFFFFFFUL;

        // class 对象胖引用：{typeid = ptrtoint(sheet) | tag2<<56, payload=对象指针}
        internal static LLVMValueRef PackObject(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeSheet, LLVMValueRef objectPointer)
        {
            var payload = builder.BuildPtrToInt(objectPointer, LLVMTypeRef.Int64, "new.payload");
            return PackFat(session, builder, typeSheet, TagObject, payload, "new");
        }

        internal static bool NeedsBox(ModuleBuilder.Session session, MirType from, MirType to) =>
            TypeLayout.IsGenericPlaceholder(to)
            && !TypeLayout.IsGenericPlaceholder(from)
            && IsBoxableValue(session, from);

        internal static bool NeedsUnbox(ModuleBuilder.Session session, MirType from, MirType to) =>
            TypeLayout.IsGenericPlaceholder(from)
            && !TypeLayout.IsGenericPlaceholder(to)
            && IsBoxableValue(session, to);

        private static bool IsBoxableValue(ModuleBuilder.Session session, MirType type) =>
            MirBuilder.IsScalarOrString(type) || session.IsInlineValueType(type, out _);

        internal static void EmitBox(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirBoxAny inst)
        {
            if (inst.Source is not MirLocalOperand local)
            {
                throw new CompilerInternalException("Box 源必须是局部");
            }
            builder.BuildStore(BoxFromLocal(session, builder, slots, local.Name),
                slots[inst.Target].Slot);
        }

        // 值类型/String → 16B 胖值（泛型槽 / .any 共用）
        internal static LLVMValueRef BoxFromLocal(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, string sourceName)
        {
            var sourceType = slots[sourceName].Local.Type;
            var size = ValueByteSize(session, sourceType);
            var sheet = TypeSheetOf(session, sourceType);
            var tag = size <= InlineLimit ? TagInline : TagHeapValue;
            LLVMValueRef payload;
            if (tag == TagInline)
            {
                payload = PackInlinePayload(session, builder, slots, sourceName, sourceType, size);
            }
            else
            {
                var block = Malloc(session, builder, size);
                session.EmitMemCopy(builder, block, slots[sourceName].Slot, size);
                ArcEmitter.EmitValueAcquire(session, builder, block, sourceType);
                payload = builder.BuildPtrToInt(block, LLVMTypeRef.Int64, "box.payload");
            }
            return PackFat(session, builder, sheet, tag, payload, "box");
        }

        internal static void EmitUnbox(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirUnboxAny inst)
        {
            UnboxToLocal(session, builder, slots, session.LoadLocal(builder, slots, inst.Source),
                slots[inst.Target].Local.Type, inst.Target);
        }

        internal static void UnboxToLocal(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef fat, MirType targetType, string target)
        {
            var size = ValueByteSize(session, targetType);
            var expectedTag = size <= InlineLimit ? TagInline : TagHeapValue;
            var sheet = TypeSheetOf(session, targetType);
            var typeId = builder.BuildExtractValue(fat, 0, "unbox.typeid");
            var payload = builder.BuildExtractValue(fat, 1, "unbox.payload");
            var tag = builder.BuildLShr(typeId,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, TagShift, false), "unbox.tag");
            var sheetBits = builder.BuildAnd(typeId,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, SheetMask, false), "unbox.sheet");
            var wantSheet = builder.BuildPtrToInt(sheet, LLVMTypeRef.Int64, "unbox.want");
            var mismatch = builder.BuildOr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, tag,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, expectedTag, false),
                    "unbox.tag.bad"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sheetBits, wantSheet,
                    "unbox.sheet.bad"),
                "unbox.bad");
            EmitAbortOnMismatch(session, builder, mismatch, sheet);

            if (expectedTag == TagInline)
            {
                UnpackInline(session, builder, slots, payload, targetType, size, target);
            }
            else
            {
                var block = builder.BuildIntToPtr(payload, PointerType(), "unbox.block");
                session.EmitMemCopy(builder, slots[target].Slot, block, size);
                ArcEmitter.EmitValueAcquire(session, builder, slots[target].Slot, targetType);
            }
        }

        // ===== pack / unpack =====

        internal static LLVMValueRef PackFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeSheet, ulong tag, LLVMValueRef payload,
            string prefix)
        {
            var typeId = builder.BuildPtrToInt(typeSheet, LLVMTypeRef.Int64, prefix + ".typeid");
            var tagged = builder.BuildOr(typeId,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, tag << TagShift, false),
                prefix + ".tagged");
            var fat = LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context));
            fat = builder.BuildInsertValue(fat, tagged, 0, prefix + ".t0");
            return builder.BuildInsertValue(fat, payload, 1, prefix + ".ref");
        }

        private static LLVMValueRef PackInlinePayload(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            string sourceName, MirType sourceType, int size)
        {
            if (session.IsInlineValueType(sourceType, out _))
            {
                return BitsFromSlot(session, builder, slots[sourceName].Slot, size);
            }
            var value = session.LoadLocal(builder, slots, new MirLocalOperand(sourceName));
            return ScalarToI64(builder, value, sourceType);
        }

        private static void UnpackInline(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef payload, MirType targetType, int size, string target)
        {
            if (session.IsInlineValueType(targetType, out _))
            {
                var tmp = builder.BuildAlloca(LLVMTypeRef.Int64, "unbox.bits");
                builder.BuildStore(payload, tmp);
                session.EmitMemCopy(builder, slots[target].Slot, tmp, size);
                return;
            }
            builder.BuildStore(UnboxScalarBits(builder, payload, targetType),
                slots[target].Slot);
        }

        internal static LLVMValueRef BitsFromSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef slot, int size)
        {
            var tmp = builder.BuildAlloca(LLVMTypeRef.Int64, "box.bits");
            builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), tmp);
            if (size > 0)
            {
                session.EmitMemCopy(builder, tmp, slot, size);
            }
            return builder.BuildLoad2(LLVMTypeRef.Int64, tmp, "box.payload");
        }

        private static LLVMValueRef ScalarToI64(LLVMBuilderRef builder, LLVMValueRef value,
            MirType inner)
        {
            switch (inner.Key)
            {
                case "float":
                    return builder.BuildZExt(
                        builder.BuildBitCast(value, LLVMTypeRef.Int32, "box.fbits"),
                        LLVMTypeRef.Int64, "box.z");
                case "double":
                    return builder.BuildBitCast(value, LLVMTypeRef.Int64, "box.dbits");
                case "bool":
                    return builder.BuildZExt(value, LLVMTypeRef.Int64, "box.z");
                default:
                    return value.TypeOf.IntWidth < 64
                        ? builder.BuildZExt(value, LLVMTypeRef.Int64, "box.z")
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
                        builder.BuildTrunc(bits, LLVMTypeRef.Int32, "unbox.t"),
                        LLVMTypeRef.Float, "unbox.f");
                case "double":
                    return builder.BuildBitCast(bits, LLVMTypeRef.Double, "unbox.d");
                case "bool":
                    return builder.BuildTrunc(bits, LLVMTypeRef.Int1, "unbox.b");
                default:
                    var width = inner.Key switch
                    {
                        "i8" or "u8" => LLVMTypeRef.Int8,
                        "char" or "i16" or "u16" => LLVMTypeRef.Int16,
                        "i32" or "u32" => LLVMTypeRef.Int32,
                        "i64" or "u64" => LLVMTypeRef.Int64,
                        _ => throw new CompilerInternalException(
                            $"未覆盖的 Box 标量拆箱: {inner.Canonical}"),
                    };
                    return inner.Key is "i64" or "u64"
                        ? bits
                        : builder.BuildTrunc(bits, width, "unbox.t");
            }
        }

        // ===== 检查 / 尺寸 / sheet =====

        private static void EmitAbortOnMismatch(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef mismatch, LLVMValueRef targetSheet)
        {
            var fn = session.CurrentFunction;
            var abortBlock = fn.AppendBasicBlock("unbox.abort");
            var okBlock = fn.AppendBasicBlock("unbox.ok");
            builder.BuildCondBr(mismatch, abortBlock, okBlock);
            builder.PositionAtEnd(abortBlock);
            var (face, faceType) = CallEmitter.DeclareHelperFace(session,
                RuntimeFaces.AbortInvalidCast, LLVMTypeRef.Void,
                new[] { PointerType() });
            builder.BuildCall2(faceType, face, new[] { targetSheet }, "");
            builder.BuildUnreachable();
            builder.PositionAtEnd(okBlock);
        }

        private static int ValueByteSize(ModuleBuilder.Session session, MirType type)
        {
            if (session.IsInlineValueType(type, out var plan))
            {
                return plan.Size;
            }
            var (size, _) = TypeLayout.BuiltinSheetLayout(TypeLayout.BuiltinSheetCanonical(type));
            if (size <= 0)
            {
                throw new CompilerInternalException($"Box 无法确定值类型尺寸: {type.Canonical}");
            }
            return size;
        }

        internal static LLVMValueRef TypeSheetOf(ModuleBuilder.Session session, MirType type)
        {
            var key = TypeLayout.BuiltinSheetCanonical(type);
            if (session.TryGetTypeSheet(key, out var sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet))
            {
                return sheet;
            }
            throw new CompilerInternalException($"Box 缺 TypeSheet: {key}");
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
