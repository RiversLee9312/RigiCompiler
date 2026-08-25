using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 数组发射（MW4）：alloc_array / new type(.array) / get.array / set.array
    /// / getid.type / Nullable 值类型装拆箱。布局见 TypeLayout 数组前缀与
    /// 元素 ABI；越界读得 null、越界写/负长度走 abort 面对齐 VM。
    /// </summary>
    internal static class ArrayEmitter
    {
        internal static void EmitAllocArrayCall(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCall call)
        {
            if (call.Args.Count != 2)
            {
                throw new CompilerInternalException("alloc_array 需要 typeid + size");
            }
            if (call.Result == null)
            {
                throw new CompilerInternalException("alloc_array 缺结果槽");
            }
            var elemSheet = session.LoadLocal(builder, slots, call.Args[0]);
            var length = session.LoadLocal(builder, slots, call.Args[1]);
            var obj = EmitAlloc(session, builder, elemSheet, length);
            builder.BuildStore(WrapArrayRef(session, builder, obj), slots[call.Result].Slot);
        }

        internal static void EmitNew(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewArray inst)
        {
            if (!TypeLayout.TryGetArrayElement(inst.Type, out var elementType))
            {
                throw new CompilerInternalException($"new 数组缺元素类型: {inst.Type.Canonical}");
            }
            var abi = ElementAbi(session, elementType);
            var elemSheet = TypeSheetPointer(session, builder, elementType);
            var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32,
                (ulong)inst.Elements.Count, true);
            var obj = EmitAlloc(session, builder, elemSheet, length);
            for (var i = 0; i < inst.Elements.Count; i++)
            {
                var slot = ElementPointer(builder, obj, abi,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)i, true));
                StoreElement(session, builder, slots, inst.Elements[i], slot, abi);
            }
            builder.BuildStore(WrapArrayRef(session, builder, obj), slots[inst.Target].Slot);
        }

        internal static void EmitGet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirGetArray inst)
        {
            if (!TypeLayout.TryGetArrayElement(inst.CollectionType, out var elementType))
            {
                throw new CompilerInternalException($"get.array 缺元素类型: {inst.CollectionType.Canonical}");
            }
            var abi = ElementAbi(session, elementType);
            var obj = ObjectPointer(session, builder, slots, inst.Collection);
            var index = session.LoadLocal(builder, slots, inst.Index);
            var length = builder.BuildLoad2(LLVMTypeRef.Int32,
                OffsetPointer(builder, obj, TypeLayout.ArrayLengthOffset), "arr.len");
            var oob = builder.BuildOr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, index,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "arr.neg"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, index, length, "arr.hi"),
                "arr.oob");
            var fn = session.CurrentFunction;
            var oobBlock = fn.AppendBasicBlock("arr.get.oob");
            var hitBlock = fn.AppendBasicBlock("arr.get.hit");
            var joinBlock = fn.AppendBasicBlock("arr.get.join");
            builder.BuildCondBr(oob, oobBlock, hitBlock);

            builder.PositionAtEnd(oobBlock);
            builder.BuildStore(
                LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context)),
                slots[inst.Target].Slot);
            builder.BuildBr(joinBlock);

            builder.PositionAtEnd(hitBlock);
            var loaded = LoadElement(session, builder,
                ElementPointer(builder, obj, abi, index), elementType, abi);
            StoreWrapped(session, builder, slots, loaded, elementType, inst.Target);
            builder.BuildBr(joinBlock);

            builder.PositionAtEnd(joinBlock);
        }

        internal static void EmitSet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSetArray inst)
        {
            if (!TypeLayout.TryGetArrayElement(inst.CollectionType, out var elementType))
            {
                throw new CompilerInternalException($"set.array 缺元素类型: {inst.CollectionType.Canonical}");
            }
            var abi = ElementAbi(session, elementType);
            var obj = ObjectPointer(session, builder, slots, inst.Collection);
            var index = session.LoadLocal(builder, slots, inst.Index);
            var length = builder.BuildLoad2(LLVMTypeRef.Int32,
                OffsetPointer(builder, obj, TypeLayout.ArrayLengthOffset), "arr.len");
            var oob = builder.BuildOr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, index,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "arr.neg"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, index, length, "arr.hi"),
                "arr.oob");
            EmitAbortOob(session, builder, oob, index, length);
            StoreElement(session, builder, slots, inst.Element,
                ElementPointer(builder, obj, abi, index), abi);
        }

        internal static void MaterializeU8Array(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef destSlot, byte[] bytes, string name)
        {
            var u8 = MirType.Of(".u8");
            var elemSheet = TypeSheetPointer(session, builder, u8);
            var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)bytes.Length, true);
            var obj = EmitAlloc(session, builder, elemSheet, length);
            if (bytes.Length > 0)
            {
                var arrayType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)bytes.Length);
                var elements = new LLVMValueRef[bytes.Length];
                for (var i = 0; i < bytes.Length; i++)
                {
                    elements[i] = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, bytes[i], false);
                }
                var global = session.Module.AddGlobal(arrayType, "raw." + name);
                global.Linkage = LLVMLinkage.LLVMInternalLinkage;
                global.IsGlobalConstant = true;
                global.Initializer = LLVMValueRef.CreateConstArray(LLVMTypeRef.Int8, elements);
                var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false);
                var data = LLVMValueRef.CreateConstInBoundsGEP2(arrayType, global, new[] { zero, zero });
                session.EmitMemCopy(builder, OffsetPointer(builder, obj, TypeLayout.ArrayPrefixSize),
                    data, bytes.Length);
            }
            builder.BuildStore(WrapArrayRef(session, builder, obj), destSlot);
        }

        internal static void EmitGetTypeId(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirGetTypeId inst)
        {
            builder.BuildStore(TypeSheetPointer(session, builder, MirType.Of(inst.TypeRef)),
                slots[inst.Target].Slot);
        }

        internal static void EmitWrap(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirWrapNullable inst)
        {
            var value = session.LoadLocal(builder, slots, inst.Source);
            builder.BuildStore(WrapValue(session, builder, slots, inst.Source, value, inst.InnerType),
                slots[inst.Target].Slot);
        }

        internal static void EmitUnwrap(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirUnwrapNullable inst)
        {
            var fat = session.LoadLocal(builder, slots, inst.Source);
            UnwrapToSlot(session, builder, slots, fat, inst.InnerType, inst.Target);
        }

        // ===== 分配 / 胖引用 =====

        private static LLVMValueRef EmitAlloc(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef elemSheet, LLVMValueRef length)
        {
            var arraySheet = session.TypeSheetFor(TypeLayout.ArrayTypeCanonical);
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.AllocArray,
                PointerType(), new[] { PointerType(), PointerType(), LLVMTypeRef.Int32 });
            return builder.BuildCall2(fnType, fn,
                new[] { CastToBytePtr(builder, arraySheet), elemSheet, length }, "arr.obj");
        }

        private static LLVMValueRef WrapArrayRef(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef objectPointer)
        {
            return CallEmitter.BuildFatReference(session, builder,
                session.TypeSheetFor(TypeLayout.ArrayTypeCanonical), objectPointer);
        }

        // ===== 元素地址 / 读写 =====

        private static ArrayElementAbi ElementAbi(ModuleBuilder.Session session, MirType element) =>
            TypeLayout.ClassifyElement(element, session.Layout?.Find(element.Canonical));

        private static LLVMValueRef ElementPointer(LLVMBuilderRef builder, LLVMValueRef obj,
            ArrayElementAbi abi, LLVMValueRef index)
        {
            var basePointer = OffsetPointer(builder, obj, TypeLayout.ArrayPrefixSize);
            var offset = builder.BuildMul(
                builder.BuildSExt(index, LLVMTypeRef.Int64, "arr.idx64"),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)abi.Stride, false),
                "arr.off");
            return builder.BuildGEP2(LLVMTypeRef.Int8, basePointer, new[] { offset }, "arr.elem");
        }

        private static void StoreElement(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand source,
            LLVMValueRef dest, ArrayElementAbi abi)
        {
            if (abi.Kind is ArrayElementKind.InlineValue or ArrayElementKind.String)
            {
                if (source is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("数组元素写源必须是局部");
                }
                session.EmitMemCopy(builder, dest, slots[local.Name].Slot, abi.Stride);
                return;
            }
            var value = session.LoadLocal(builder, slots, source);
            builder.BuildStore(value, dest);
        }

        private static LLVMValueRef LoadElement(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef source, MirType elementType, ArrayElementAbi abi)
        {
            if (abi.Kind is ArrayElementKind.InlineValue or ArrayElementKind.String)
            {
                return source;
            }
            return builder.BuildLoad2(TypeLayout.MapType(session.Context, elementType),
                source, "arr.load");
        }

        private static void StoreWrapped(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef loaded, MirType elementType, string target)
        {
            if (IsReferenceElement(session, elementType))
            {
                builder.BuildStore(loaded, slots[target].Slot);
                return;
            }
            if (loaded.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                builder.BuildStore(BoxPointer(session, builder, loaded), slots[target].Slot);
                return;
            }
            builder.BuildStore(BoxScalar(session, builder, loaded, elementType),
                slots[target].Slot);
        }

        // ===== Nullable 装拆箱 =====

        private static LLVMValueRef WrapValue(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand source,
            LLVMValueRef value, MirType inner)
        {
            if (IsReferenceElement(session, inner))
            {
                return value;
            }
            var abi = ElementAbi(session, inner);
            if (abi.Kind is ArrayElementKind.InlineValue or ArrayElementKind.String)
            {
                if (source is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("Nullable 装箱源必须是局部");
                }
                var box = Malloc(session, builder, abi.Stride);
                session.EmitMemCopy(builder, box, slots[local.Name].Slot, abi.Stride);
                return BoxPointer(session, builder, box);
            }
            return BoxScalar(session, builder, value, inner);
        }

        private static void UnwrapToSlot(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef fat, MirType inner, string target)
        {
            if (IsReferenceElement(session, inner))
            {
                builder.BuildStore(fat, slots[target].Slot);
                return;
            }
            var payload = builder.BuildExtractValue(fat, 1, "opt.payload");
            var abi = ElementAbi(session, inner);
            if (abi.Kind is ArrayElementKind.InlineValue or ArrayElementKind.String)
            {
                var box = builder.BuildIntToPtr(payload, PointerType(), "opt.box");
                session.EmitMemCopy(builder, slots[target].Slot, box, abi.Stride);
                return;
            }
            var bits = UnboxScalarBits(builder, payload, inner);
            builder.BuildStore(bits, slots[target].Slot);
        }

        private static bool IsReferenceElement(ModuleBuilder.Session session, MirType element) =>
            ElementAbi(session, element).Kind == ArrayElementKind.Reference;

        private static LLVMValueRef BoxScalar(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef value, MirType inner)
        {
            var bits = ScalarToI64(builder, value, inner);
            return InsertFat(session, builder,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, TypeLayout.NullableSentinel, false),
                bits);
        }

        private static LLVMValueRef BoxPointer(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef pointer)
        {
            return InsertFat(session, builder,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, TypeLayout.NullableSentinel, false),
                builder.BuildPtrToInt(pointer, LLVMTypeRef.Int64, "box.ptr"));
        }

        private static LLVMValueRef InsertFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeId, LLVMValueRef payload)
        {
            var fat = LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context));
            fat = builder.BuildInsertValue(fat, typeId, 0, "opt.t");
            return builder.BuildInsertValue(fat, payload, 1, "opt.v");
        }

        private static LLVMValueRef ScalarToI64(LLVMBuilderRef builder, LLVMValueRef value,
            MirType inner)
        {
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
                    return builder.BuildBitCast(bits, LLVMTypeRef.Double, "opt.d");
                case "bool":
                    return builder.BuildTrunc(bits, LLVMTypeRef.Int1, "opt.b");
                default:
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

        // ===== TypeSheet / 地址 =====

        private static LLVMValueRef TypeSheetPointer(ModuleBuilder.Session session,
            LLVMBuilderRef builder, MirType type)
        {
            var key = TypeLayout.IsArray(type)
                ? TypeLayout.ArrayTypeCanonical
                : TypeLayout.BuiltinSheetCanonical(type);
            if (session.TryGetTypeSheet(key, out var sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet))
            {
                return CastToBytePtr(builder, sheet);
            }
            return LLVMValueRef.CreateConstPointerNull(PointerType());
        }

        private static LLVMValueRef ObjectPointer(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand receiver)
        {
            var fat = session.LoadLocal(builder, slots, receiver);
            return builder.BuildIntToPtr(builder.BuildExtractValue(fat, 1, "arr.payload"),
                PointerType(), "arr.obj");
        }

        private static LLVMValueRef OffsetPointer(LLVMBuilderRef builder, LLVMValueRef basePointer,
            int offset)
        {
            return builder.BuildGEP2(LLVMTypeRef.Int8, basePointer,
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)offset, false) },
                "arr.gep");
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

        private static void EmitAbortOob(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef condition, LLVMValueRef index, LLVMValueRef length)
        {
            var fn = session.CurrentFunction;
            var abortBlock = fn.AppendBasicBlock("arr.set.oob");
            var okBlock = fn.AppendBasicBlock("arr.set.ok");
            builder.BuildCondBr(condition, abortBlock, okBlock);
            builder.PositionAtEnd(abortBlock);
            var (abortFn, abortType) = CallEmitter.DeclareHelperFace(session,
                RuntimeFaces.AbortArrayOob, LLVMTypeRef.Void,
                new[] { LLVMTypeRef.Int32, LLVMTypeRef.Int32 });
            builder.BuildCall2(abortType, abortFn, new[] { index, length }, "");
            builder.BuildUnreachable();
            builder.PositionAtEnd(okBlock);
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

        private static LLVMValueRef CastToBytePtr(LLVMBuilderRef builder, LLVMValueRef value) =>
            builder.BuildBitCast(value, PointerType(), "arr.p");
    }
}
