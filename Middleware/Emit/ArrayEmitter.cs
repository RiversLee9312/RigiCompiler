using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 数组 / Span 发射（MW4/MW7b）：alloc_array / span_alloc /
    /// new type(.array) / get.array / set.array。getid 归 TypeIdEmitter，
    /// Nullable 装拆箱归 NullableEmitter。
    /// </summary>
    internal static class ArrayEmitter
    {
        internal sealed class Get : LlvmEmitVisitor<Get, MirGetArray>
        {
            protected override void VisitCore(MirGetArray inst, ModuleBuilder.Session session) =>
                EmitGet(session, session.Builder, session.Slots, inst);
        }

        internal sealed class Set : LlvmEmitVisitor<Set, MirSetArray>
        {
            protected override void VisitCore(MirSetArray inst, ModuleBuilder.Session session) =>
                EmitSet(session, session.Builder, session.Slots, inst);
        }

        internal sealed class New : LlvmEmitVisitor<New, MirNewArray>
        {
            protected override void VisitCore(MirNewArray inst, ModuleBuilder.Session session) =>
                EmitNew(session, session.Builder, session.Slots, inst);
        }

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
            // args[0] = T 的运行时 TypeSheet*（hidden typeid / getid.type）
            var elemSheet = session.LoadLocal(builder, slots, call.Args[0]);
            var length = session.LoadLocal(builder, slots, call.Args[1]);
            var obj = EmitAlloc(session, builder, elemSheet, length);
            builder.BuildStore(WrapArrayRef(session, builder, obj), slots[call.Result].Slot);
        }

        // span_alloc / shared_span_alloc 同 NativeSymbol：由结果类型头
        // 区分 Span vs SharedSpan。具化 sheet 优先；泛型包装体内按
        // elemSheet 在已收集的闭合具化中选择。
        internal static void EmitAllocSpanCall(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCall call)
        {
            if (call.Args.Count != 2)
            {
                throw new CompilerInternalException("span_alloc 需要 typeid + size");
            }
            if (call.Result == null)
            {
                throw new CompilerInternalException("span_alloc 缺结果槽");
            }
            var elemSheet = session.LoadLocal(builder, slots, call.Args[0]);
            var length = session.LoadLocal(builder, slots, call.Args[1]);
            var resultType = slots[call.Result].Local.Type;
            var spanSheet = ResolveSpanSheet(session, builder, resultType, elemSheet);
            var obj = EmitSpanAlloc(session, builder, spanSheet, elemSheet, length);
            builder.BuildStore(
                CallEmitter.BuildFatReference(session, builder, spanSheet, obj),
                slots[call.Result].Slot);
        }

        private static void EmitNew(ModuleBuilder.Session session, LLVMBuilderRef builder,
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

        private static void EmitGet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirGetArray inst)
        {
            if (!TypeLayout.TryGetContiguousElement(inst.CollectionType, out var elementType))
            {
                throw new CompilerInternalException($"get.array 缺元素类型: {inst.CollectionType.Canonical}");
            }
            // 泛型占位元素：运行时 stride 分派（数组头 elemSheet），
            // 禁止按 16B 胖引用硬读（写路径按 typeSize 动态 stride）
            if (IsBareElementParameter(elementType))
            {
                EmitPlaceholderGet(session, builder, slots, inst);
                return;
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

        private static void EmitSet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSetArray inst)
        {
            if (!TypeLayout.TryGetContiguousElement(inst.CollectionType, out var elementType))
            {
                throw new CompilerInternalException($"set.array 缺元素类型: {inst.CollectionType.Canonical}");
            }
            if (IsBareElementParameter(elementType))
            {
                if (TryStoreBakedScalar(session, builder, slots, inst)
                    || TryStoreViaTypeId(session, builder, slots, inst))
                {
                    return;
                }
                // 无 .generic.T 槽的泛型上下文（如形参传入的 Array<T>）：
                // 按数组头 elemSheet 运行时 stride 写，禁止落回 Reference ABI
                EmitPlaceholderSet(session, builder, slots, inst);
                return;
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
            EmitThrowOob(session, builder, oob, index, length, inst.ExcTarget);
            StoreElement(session, builder, slots, inst.Element,
                ElementPointer(builder, obj, abi, index), abi);
        }

        // 只有裸 T 的槽宽需要运行时决定；Nullable<T>/Array<T>/Box<T>
        // 即使含有泛型参数，也始终按引用槽读写，不能借用 T 的标量宽度。
        private static bool IsBareElementParameter(MirType type) =>
            type.Canonical.StartsWith(".generic<", System.StringComparison.Ordinal)
            || type.Canonical.StartsWith("$.generic.", System.StringComparison.Ordinal);

        private static bool TryStoreBakedScalar(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSetArray inst)
        {
            if (inst.Element is not MirLocalOperand srcLocal)
            {
                return false;
            }
            var srcType = slots[srcLocal.Name].Local.Type;
            if (!MirBuilder.IsScalarOrString(srcType)
                || TypeLayout.IsGenericPlaceholder(srcType))
            {
                return false;
            }
            var stride = srcType.Key switch
            {
                "bool" or "i8" or "u8" => 1,
                "char" or "i16" or "u16" => 2,
                "i32" or "u32" or "float" => 4,
                "i64" or "u64" or "double" => 8,
                "String" => TypeLayout.ReferenceSlotSize,
                _ => 0,
            };
            if (stride == 0)
            {
                return false;
            }
            var obj = ObjectPointer(session, builder, slots, inst.Collection);
            var index = session.LoadLocal(builder, slots, inst.Index);
            var length = builder.BuildLoad2(LLVMTypeRef.Int32,
                OffsetPointer(builder, obj, TypeLayout.ArrayLengthOffset), "arr.len");
            var oob = builder.BuildOr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, index,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "arr.neg"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, index, length, "arr.hi"),
                "arr.oob");
            EmitThrowOob(session, builder, oob, index, length, inst.ExcTarget);
            var off = builder.BuildMul(
                builder.BuildSExt(index, LLVMTypeRef.Int64, "arr.idx64"),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)stride, false), "arr.off");
            var ptr = builder.BuildGEP2(LLVMTypeRef.Int8,
                OffsetPointer(builder, obj, TypeLayout.ArrayPrefixSize), new[] { off }, "arr.elem");
            if (srcType.IsString)
            {
                ArcEmitter.AssignStringFromSlot(session, builder, ptr, slots[srcLocal.Name].Slot);
            }
            else
            {
                builder.BuildStore(session.LoadLocal(builder, slots, srcLocal), ptr);
            }
            return true;
        }

        private static bool TryStoreViaTypeId(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSetArray inst)
        {
            if (!slots.ContainsKey(".generic.T") || inst.Element is not MirLocalOperand srcLocal)
            {
                return false;
            }
            var sheet = session.LoadLocal(builder, slots, new MirLocalOperand(".generic.T"));
            var sheetOk = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sheet,
                LLVMValueRef.CreateConstPointerNull(PointerType()), "arr.tid.ok");
            var safeSheet = builder.BuildSelect(sheetOk, sheet,
                CastToBytePtr(builder, session.TypeSheetFor("core::Any")), "arr.tid.sheet");
            var size = builder.BuildLoad2(LLVMTypeRef.Int32,
                OffsetPointer(builder, safeSheet, 16), "arr.tid.sz");
            var stride = builder.BuildSelect(
                builder.BuildAnd(
                    builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, size,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "arr.tid.lo"),
                    builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, size,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 8, true), "arr.tid.hi"),
                    "arr.tid.ok"),
                builder.BuildZExt(size, LLVMTypeRef.Int64, "arr.tid.st"),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 16, false), "arr.tid.stride");
            var obj = ObjectPointer(session, builder, slots, inst.Collection);
            var index = session.LoadLocal(builder, slots, inst.Index);
            var length = builder.BuildLoad2(LLVMTypeRef.Int32,
                OffsetPointer(builder, obj, TypeLayout.ArrayLengthOffset), "arr.len");
            var oob = builder.BuildOr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, index,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "arr.neg"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, index, length, "arr.hi"),
                "arr.oob");
            EmitThrowOob(session, builder, oob, index, length, inst.ExcTarget);
            var off = builder.BuildMul(
                builder.BuildSExt(index, LLVMTypeRef.Int64, "arr.idx64"), stride, "arr.off");
            var ptr = builder.BuildGEP2(LLVMTypeRef.Int8,
                OffsetPointer(builder, obj, TypeLayout.ArrayPrefixSize), new[] { off }, "arr.elem");
            var value = session.LoadLocal(builder, slots, srcLocal);
            if (value.TypeOf.Kind != LLVMTypeKind.LLVMStructTypeKind)
            {
                builder.BuildStore(value, ptr);
                return true;
            }
            var small = builder.BuildAnd(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, size,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "arr.tid.slo"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, size,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 8, true), "arr.tid.shi"),
                "arr.tid.small");
            var fn = session.CurrentFunction;
            var scBlock = fn.AppendBasicBlock("arr.tid.sc");
            var fatBlock = fn.AppendBasicBlock("arr.tid.fat");
            var join = fn.AppendBasicBlock("arr.tid.join");
            builder.BuildCondBr(small, scBlock, fatBlock);
            builder.PositionAtEnd(scBlock);
            var payload = builder.BuildExtractValue(value, 1, "arr.tid.pl");
            var bits = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "arr.tid.bits");
            builder.BuildStore(payload, bits);
            session.EmitMemCopyN(builder, ptr, bits,
                builder.BuildZExt(size, LLVMTypeRef.Int64, "arr.tid.n"));
            builder.BuildBr(join);
            builder.PositionAtEnd(fatBlock);
            ArcEmitter.AssignFatValue(session, builder, ptr, value);
            builder.BuildBr(join);
            builder.PositionAtEnd(join);
            return true;
        }

        private static void EmitPlaceholderGet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirGetArray inst)
        {
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
            var oobBlock = fn.AppendBasicBlock("arr.pg.oob");
            var hitBlock = fn.AppendBasicBlock("arr.pg.hit");
            var joinBlock = fn.AppendBasicBlock("arr.pg.join");
            builder.BuildCondBr(oob, oobBlock, hitBlock);
            builder.PositionAtEnd(oobBlock);
            builder.BuildStore(
                LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context)),
                slots[inst.Target].Slot);
            builder.BuildBr(joinBlock);
            builder.PositionAtEnd(hitBlock);
            var (elemPtr, sheet, flags, size) = RuntimeElem(session, builder, obj, index);
            var inline = IsInlineElem(builder, flags, size);
            var fat = LoadRuntimeElemAsFat(session, builder, elemPtr, sheet, inline, size);
            builder.BuildStore(ArcEmitter.ProduceFatValue(session, builder, fat, "arr.pg"),
                slots[inst.Target].Slot);
            builder.BuildBr(joinBlock);
            builder.PositionAtEnd(joinBlock);
        }

        private static void EmitPlaceholderSet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSetArray inst)
        {
            var obj = ObjectPointer(session, builder, slots, inst.Collection);
            var index = session.LoadLocal(builder, slots, inst.Index);
            var length = builder.BuildLoad2(LLVMTypeRef.Int32,
                OffsetPointer(builder, obj, TypeLayout.ArrayLengthOffset), "arr.len");
            var oob = builder.BuildOr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, index,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "arr.neg"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, index, length, "arr.hi"),
                "arr.oob");
            EmitThrowOob(session, builder, oob, index, length, inst.ExcTarget);
            var (elemPtr, sheet, flags, size) = RuntimeElem(session, builder, obj, index);
            var inline = IsInlineElem(builder, flags, size);
            if (inst.Element is not MirLocalOperand srcLocal)
            {
                throw new CompilerInternalException("set.array 源必须是局部");
            }
            var srcType = slots[srcLocal.Name].Local.Type;
            var value = session.LoadLocal(builder, slots, srcLocal);
            if (value.TypeOf.Kind != LLVMTypeKind.LLVMStructTypeKind)
            {
                builder.BuildStore(value, elemPtr);
                return;
            }
            LLVMValueRef fat;
            if (MirBuilder.IsScalarOrString(srcType) && !TypeLayout.IsGenericPlaceholder(srcType))
            {
                fat = BoxEmitter.BoxFromLocal(session, builder, slots, srcLocal.Name);
            }
            else
            {
                fat = value;
            }
            StoreRuntimeElemFromFat(session, builder, elemPtr, fat, inline, size);
        }

        private static (LLVMValueRef Ptr, LLVMValueRef Sheet, LLVMValueRef Flags, LLVMValueRef Size)
            RuntimeElem(ModuleBuilder.Session session, LLVMBuilderRef builder, LLVMValueRef obj,
            LLVMValueRef index)
        {
            var sheet = builder.BuildLoad2(PointerType(),
                OffsetPointer(builder, obj, TypeLayout.ArrayElemSheetOffset), "arr.es");
            var nullSheet = LLVMValueRef.CreateConstPointerNull(PointerType());
            var hasSheet = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sheet, nullSheet,
                "arr.es.ok");
            var anySheet = CastToBytePtr(builder, session.TypeSheetFor("core::Any"));
            sheet = builder.BuildSelect(hasSheet, sheet, anySheet, "arr.es.use");
            var size = builder.BuildLoad2(LLVMTypeRef.Int32,
                OffsetPointer(builder, sheet, 16), "arr.es.sz");
            var flags = builder.BuildLoad2(LLVMTypeRef.Int32,
                OffsetPointer(builder, sheet, 20), "arr.es.fl");
            var inline = IsInlineElem(builder, flags, size);
            var stride = builder.BuildSelect(inline,
                builder.BuildZExt(size, LLVMTypeRef.Int64, "arr.st.i"),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 16, false), "arr.st");
            var basePtr = OffsetPointer(builder, obj, TypeLayout.ArrayPrefixSize);
            var off = builder.BuildMul(
                builder.BuildSExt(index, LLVMTypeRef.Int64, "arr.idx64"), stride, "arr.off");
            var ptr = builder.BuildGEP2(LLVMTypeRef.Int8, basePtr, new[] { off }, "arr.elem");
            return (ptr, sheet, flags, size);
        }

        // 小内联元素（≤8B，payload 装 i64 bits）：String 虽带
        // FlagInlineValue 但 size=16，其槽位与胖引用布局兼容，必须走
        // 16B 胖槽直读直写（与写路径 TryStoreViaTypeId 的 ≤8 判定同口径）
        private static LLVMValueRef IsInlineElem(LLVMBuilderRef builder, LLVMValueRef flags,
            LLVMValueRef size)
        {
            var hasInline = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE,
                builder.BuildAnd(flags,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, TypeLayoutPlan.FlagInlineValue,
                        false), "arr.fl.in"),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false), "arr.in");
            var lo = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, size,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "arr.sz.lo");
            var hi = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, size,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 8, true), "arr.sz.hi");
            var nz = builder.BuildAnd(lo, hi, "arr.sz");
            return builder.BuildAnd(hasInline, nz, "arr.inline");
        }

        private static LLVMValueRef LoadRuntimeElemAsFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef elemPtr, LLVMValueRef sheet, LLVMValueRef inline,
            LLVMValueRef size)
        {
            var fn = session.CurrentFunction;
            var slot = LlvmEmitEnvironment.BuildEntryAlloca(builder, TypeLayout.FatReferenceType(session.Context),
                "arr.ld.slot");
            var inBlock = fn.AppendBasicBlock("arr.ld.in");
            var fatBlock = fn.AppendBasicBlock("arr.ld.fat");
            var join = fn.AppendBasicBlock("arr.ld.join");
            builder.BuildCondBr(inline, inBlock, fatBlock);
            builder.PositionAtEnd(inBlock);
            var bitsTmp = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "arr.ld.bits");
            builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), bitsTmp);
            MemCopyN(session, builder, bitsTmp, elemPtr, size);
            var bits = builder.BuildLoad2(LLVMTypeRef.Int64, bitsTmp, "arr.ld.b");
            builder.BuildStore(BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline,
                bits, "arr.ld.nf"), slot);
            builder.BuildBr(join);
            builder.PositionAtEnd(fatBlock);
            var fat = builder.BuildLoad2(TypeLayout.FatReferenceType(session.Context), elemPtr,
                "arr.ld.f");
            builder.BuildStore(fat, slot);
            builder.BuildBr(join);
            builder.PositionAtEnd(join);
            return builder.BuildLoad2(TypeLayout.FatReferenceType(session.Context), slot,
                "arr.ld.out");
        }

        private static void StoreRuntimeElemFromFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef elemPtr, LLVMValueRef fat, LLVMValueRef inline,
            LLVMValueRef size)
        {
            var fn = session.CurrentFunction;
            var inBlock = fn.AppendBasicBlock("arr.st.in");
            var fatBlock = fn.AppendBasicBlock("arr.st.fat");
            var join = fn.AppendBasicBlock("arr.st.join");
            builder.BuildCondBr(inline, inBlock, fatBlock);
            builder.PositionAtEnd(inBlock);
            var payload = builder.BuildExtractValue(fat, 1, "arr.st.pl");
            var bitsTmp = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "arr.st.bits");
            builder.BuildStore(payload, bitsTmp);
            MemCopyN(session, builder, elemPtr, bitsTmp, size);
            builder.BuildBr(join);
            builder.PositionAtEnd(fatBlock);
            ArcEmitter.AssignFatValue(session, builder, elemPtr, fat);
            builder.BuildBr(join);
            builder.PositionAtEnd(join);
        }

        private static void MemCopyN(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef dest, LLVMValueRef src, LLVMValueRef size)
        {
            session.EmitMemCopyN(builder, dest, src,
                builder.BuildZExt(size, LLVMTypeRef.Int64, "arr.cpy.n"));
        }

        internal static void MaterializeU8Array(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef destSlot, byte[] bytes, string name)
        {
            var u8 = MirType.Of(".u8");
            var elemSheet = TypeSheetPointer(session, builder, u8);
            var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)bytes.Length, true);
            var obj = EmitAlloc(session, builder, elemSheet, length);
            CopyStaticBytes(session, builder, obj, bytes, name);
            builder.BuildStore(WrapArrayRef(session, builder, obj), destSlot);
        }

        // raw.hex/raw.bin → Span<u8>/SharedSpan<u8>（§19.3）：对象布局与
        // 数组同构（RUNTIME §5），span_alloc 填头后同律拷入静态字节常量；
        // Span vs SharedSpan 由目标类型头经 ResolveSpanSheet 区分
        internal static void MaterializeU8Span(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef destSlot, MirType targetType,
            byte[] bytes, string name)
        {
            var elemSheet = TypeSheetPointer(session, builder, MirType.Of(".u8"));
            var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)bytes.Length, true);
            var spanSheet = ResolveSpanSheet(session, builder, targetType, elemSheet);
            var obj = EmitSpanAlloc(session, builder, spanSheet, elemSheet, length);
            CopyStaticBytes(session, builder, obj, bytes, name);
            builder.BuildStore(
                CallEmitter.BuildFatReference(session, builder, spanSheet, obj), destSlot);
        }

        // 静态字节常量 → 连续缓冲区数据区（数组/Span 前缀同 ArrayPrefixSize）
        private static void CopyStaticBytes(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef obj, byte[] bytes, string name)
        {
            if (bytes.Length == 0)
            {
                return;
            }
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

        private static LLVMValueRef EmitSpanAlloc(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef spanSheet, LLVMValueRef elemSheet,
            LLVMValueRef length)
        {
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.SpanAlloc,
                PointerType(), new[] { PointerType(), PointerType(), LLVMTypeRef.Int32 });
            return builder.BuildCall2(fnType, fn,
                new[] { spanSheet, elemSheet, length }, "span.obj");
        }

        // 闭合具化直接取全局；开放（spanOf 泛型体）按 elemSheet 在已
        // 收集的同族具化中选择，无一则回退 Object sheet。
        private static LLVMValueRef ResolveSpanSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder, MirType resultType, LLVMValueRef elemSheet)
        {
            if (GenericAbi.IsClosedConstructed(resultType.Canonical)
                && session.TryGetTypeSheet(resultType.Canonical, out var closed))
            {
                return CastToBytePtr(builder, closed);
            }
            var wantShared = TypeLayout.IsSharedSpan(resultType);
            var fallback = CastToBytePtr(builder, session.TypeSheetFor("core::Object"));
            var selected = fallback;
            if (session.Layout == null)
            {
                return selected;
            }
            foreach (var plan in session.Layout.Plans)
            {
                var planType = MirType.Of(plan.Symbol.Canonical);
                if (!TypeLayout.IsSpanLike(planType)
                    || TypeLayout.IsSharedSpan(planType) != wantShared
                    || !GenericAbi.IsClosedConstructed(plan.Symbol.Canonical)
                    || !TypeLayout.TryGetContiguousElement(planType, out var element)
                    || !TryTypeSheetPointer(session, builder, element, out var wantElem)
                    || !session.TryGetTypeSheet(plan.Symbol.Canonical, out var planSheet))
                {
                    continue;
                }
                var candidate = CastToBytePtr(builder, planSheet);
                var match = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, elemSheet, wantElem,
                    "span.elem.eq");
                selected = builder.BuildSelect(match, candidate, selected, "span.sheet.sel");
            }
            return selected;
        }

        private static bool TryTypeSheetPointer(ModuleBuilder.Session session,
            LLVMBuilderRef builder, MirType type, out LLVMValueRef pointer)
        {
            var key = TypeLayout.IsArray(type)
                ? TypeLayout.ArrayTypeCanonical
                : TypeLayout.BuiltinSheetCanonical(type);
            if (session.TryGetTypeSheet(key, out var sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet))
            {
                pointer = CastToBytePtr(builder, sheet);
                return true;
            }
            pointer = default;
            return false;
        }

        // ===== 元素地址 / 读写 =====

        internal static ArrayElementAbi ElementAbi(ModuleBuilder.Session session, MirType element) =>
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
            if (abi.Kind == ArrayElementKind.Reference)
            {
                if (source is not MirLocalOperand srcLocal)
                {
                    throw new CompilerInternalException("数组元素写源必须是局部");
                }
                var srcType = slots[srcLocal.Name].Local.Type;
                if (session.IsInlineValueType(srcType, out _))
                {
                    ArcEmitter.EmitCopyRichValue(session, builder, dest,
                        slots[srcLocal.Name].Slot, srcType);
                    return;
                }
                // extraSubst 把源烤成标量/String 但槽 ABI 仍是胖引用：
                // 装箱后 move 进槽，禁止按 String ABI 写入胖槽。
                if (MirBuilder.IsScalarOrString(srcType)
                    && !TypeLayout.IsGenericPlaceholder(srcType))
                {
                    var boxed = BoxEmitter.BoxFromLocal(session, builder, slots, srcLocal.Name);
                    ArcEmitter.MoveFatValue(session, builder, dest, boxed);
                    return;
                }
                ArcEmitter.AssignFatFromSlot(session, builder, dest,
                    slots[srcLocal.Name].Slot);
                return;
            }
            if (abi.Kind == ArrayElementKind.String)
            {
                if (source is not MirLocalOperand strLocal)
                {
                    throw new CompilerInternalException("数组元素写源必须是局部");
                }
                ArcEmitter.AssignStringValue(session, builder, dest,
                    session.LoadLocal(builder, slots, strLocal));
                return;
            }
            if (abi.Kind == ArrayElementKind.InlineValue)
            {
                if (source is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("数组元素写源必须是局部");
                }
                var srcType = slots[local.Name].Local.Type;
                if (abi.Plan is { RefMapCount: > 0 })
                {
                    ArcEmitter.EmitCopyRichValue(session, builder, dest,
                        slots[local.Name].Slot, srcType);
                }
                else
                {
                    session.EmitMemCopy(builder, dest, slots[local.Name].Slot, abi.Stride);
                }
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
                builder.BuildStore(ArcEmitter.ProduceFatValue(session, builder, loaded, "arr.wrap"),
                    slots[target].Slot);
                return;
            }
            // typeid 元素加载后是 ptr 值（TypeSheet*），不是槽地址，不能走
            // WrapFromSlot；位模式即 payload，tag0 内联包装
            if (TypeLayout.IsTypeId(elementType))
            {
                builder.BuildStore(NullableEmitter.WrapScalar(session, builder, loaded, elementType),
                    slots[target].Slot);
                return;
            }
            if (loaded.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                builder.BuildStore(NullableEmitter.WrapFromSlot(session, builder, loaded, elementType),
                    slots[target].Slot);
                return;
            }
            builder.BuildStore(NullableEmitter.WrapScalar(session, builder, loaded, elementType),
                slots[target].Slot);
        }

        private static bool IsReferenceElement(ModuleBuilder.Session session, MirType element) =>
            ElementAbi(session, element).Kind == ArrayElementKind.Reference;

        // ===== TypeSheet / 地址 =====

        internal static LLVMValueRef TypeSheetPointer(ModuleBuilder.Session session,
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

        // MW9b-G：写越界抛可捕获 core.OutOfBoundException（取代
        // rigi_abort_array_oob；读越界仍按空安全得 null，不抛）。
        // init(index: i64, length: i64)：i32 槽值 sext 直传
        private static void EmitThrowOob(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef condition, LLVMValueRef index, LLVMValueRef length, MirBlock? excTarget)
        {
            var fn = session.CurrentFunction;
            var throwBlock = fn.AppendBasicBlock("arr.set.throw");
            var okBlock = fn.AppendBasicBlock("arr.set.ok");
            builder.BuildCondBr(condition, throwBlock, okBlock);
            builder.PositionAtEnd(throwBlock);
            ExceptionEmitter.EmitThrowNewException(session, builder, "core::OutOfBoundException",
                "index", new[]
                {
                    builder.BuildSExt(index, LLVMTypeRef.Int64, "oob.idx64"),
                    builder.BuildSExt(length, LLVMTypeRef.Int64, "oob.len64"),
                }, excTarget);
            builder.PositionAtEnd(okBlock);
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

        private static LLVMValueRef CastToBytePtr(LLVMBuilderRef builder, LLVMValueRef value) =>
            builder.BuildBitCast(value, PointerType(), "arr.p");
    }
}
