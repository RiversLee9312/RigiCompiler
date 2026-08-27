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
            // TypeId 装箱视图 = Type<payload> 构造 sheet（运行期边界，对齐 VM TypeRef）
            if (TypeLayout.IsTypeId(sourceType))
            {
                var described = session.LoadLocal(builder, slots, new MirLocalOperand(sourceName));
                var view = ResolveTypeIdViewSheet(session, builder, described, sourceType);
                var bits = builder.BuildPtrToInt(described, LLVMTypeRef.Int64, "box.tid");
                return PackFat(session, builder, view, TagInline, bits, "box");
            }
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
                slots[inst.Target].Local.Type, inst.Target,
                slots[((MirLocalOperand)inst.Source).Name].Local.Type, inst.ExcTarget);
        }

        // MW9b-G：类型不符由 abort 改抛 core.CastException（fromType =
        // 静态源类型名常量，toType = 目标 sheet 运行期 TypeInfo.name），
        // 沿 excTarget 异常边传播（调用点 StoreCoercedResult 同穿）
        internal static void UnboxToLocal(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef fat, MirType targetType, string target, MirType fromType,
            MirBlock? excTarget)
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
            EmitThrowOnMismatch(session, builder, mismatch, sheet, fromType, excTarget);

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

        internal static LLVMValueRef PackInlinePayload(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            string sourceName, MirType sourceType, int size)
        {
            if (session.IsInlineValueType(sourceType, out _))
            {
                return BitsFromSlot(session, builder, slots[sourceName].Slot, size);
            }
            var value = session.LoadLocal(builder, slots, new MirLocalOperand(sourceName));
            // .typeid 布局 = ptr；payload = TypeSheet* 位模式
            if (TypeLayout.IsTypeId(sourceType))
            {
                return builder.BuildPtrToInt(value, LLVMTypeRef.Int64, "box.tid");
            }
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
            if (TypeLayout.IsTypeId(targetType))
            {
                builder.BuildStore(builder.BuildIntToPtr(payload, PointerType(), "unbox.tid"),
                    slots[target].Slot);
                return;
            }
            builder.BuildStore(UnboxScalarBits(builder, payload, targetType),
                slots[target].Slot);
        }

        // 动态 new 实参：引用/.any/占位走胖槽直通；值类型走 tag 编解码
        internal static bool IsFatPassthrough(ModuleBuilder.Session session, MirType type)
        {
            if (TypeLayout.IsGenericPlaceholder(type) || type.IsAny || type.IsObject)
            {
                return true;
            }
            if (TypeLayout.IsNullable(type) || TypeLayout.IsArray(type)
                || TypeLayout.IsSpanLike(type))
            {
                return true;
            }
            if (MirBuilder.IsScalarOrString(type) || TypeLayout.IsTypeId(type))
            {
                return false;
            }
            return !session.IsInlineValueType(type, out _);
        }

        // ctor thunk：按声明类型从胖槽拆出 init 实参（借来不 release）。
        // 值类型 >8B：memcpy 自 payload（栈/堆指针对称）；静态 new 的
        // CoerceArg 对 struct 亦是「副本指针交 init」，隔离由 callee 自取。
        internal static LLVMValueRef UnpackCtorArg(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef fat, MirType declared)
        {
            if (IsFatPassthrough(session, declared))
            {
                return fat;
            }
            var payload = builder.BuildExtractValue(fat, 1, "ctor.pl");
            if (TypeLayout.IsTypeId(declared))
            {
                return builder.BuildIntToPtr(payload, PointerType(), "ctor.tid");
            }
            if (session.IsInlineValueType(declared, out var plan))
            {
                var tmp = builder.BuildAlloca(
                    LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)plan.Size), "ctor.val");
                tmp.Alignment = (uint)plan.Alignment;
                if (plan.Size <= InlineLimit)
                {
                    var bits = builder.BuildAlloca(LLVMTypeRef.Int64, "ctor.bits");
                    builder.BuildStore(payload, bits);
                    session.EmitMemCopy(builder, tmp, bits, plan.Size);
                }
                else
                {
                    var block = builder.BuildIntToPtr(payload, PointerType(), "ctor.block");
                    session.EmitMemCopy(builder, tmp, block, plan.Size);
                }
                return tmp;
            }
            if (declared.IsString)
            {
                var tmp = builder.BuildAlloca(StringAbi.ValueType(session.Context), "ctor.str");
                var block = builder.BuildIntToPtr(payload, PointerType(), "ctor.sblk");
                session.EmitMemCopy(builder, tmp, block, 16);
                return builder.BuildLoad2(StringAbi.ValueType(session.Context), tmp, "ctor.sld");
            }
            return UnboxScalarBits(builder, payload, declared);
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

        // MW9b-G：不符抛 CastException（取代 rigi_abort_invalid_cast）
        private static void EmitThrowOnMismatch(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef mismatch, LLVMValueRef targetSheet,
            MirType fromType, MirBlock? excTarget)
        {
            var fn = session.CurrentFunction;
            var throwBlock = fn.AppendBasicBlock("unbox.throw");
            var okBlock = fn.AppendBasicBlock("unbox.ok");
            builder.BuildCondBr(mismatch, throwBlock, okBlock);
            builder.PositionAtEnd(throwBlock);
            var fromName = ExceptionEmitter.StaticTypeName(session, fromType);
            var toName = ExceptionEmitter.LoadTypeDisplayNameFromSheet(session, builder,
                targetSheet);
            ExceptionEmitter.EmitThrowNewException(session, builder, "core::CastException",
                "fromType", new[] { fromName, toName }, excTarget);
            builder.PositionAtEnd(okBlock);
        }

        internal static int ValueByteSize(ModuleBuilder.Session session, MirType type)
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

        // TypeId 运行期类型 = Type<payload>：按已收集的构造 sheet 选视图。
        // VM TypeRef = .typeid<ActualType>，不变（无 Type 协变）。
        internal static LLVMValueRef ResolveTypeIdViewSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef describedSheet, MirType staticType)
        {
            var selected = builder.BuildBitCast(TypeSheetOf(session, staticType),
                PointerType(), "tid.view.fb");
            var described = builder.BuildBitCast(describedSheet, PointerType(), "tid.desc");
            if (session.Layout == null)
            {
                return selected;
            }
            foreach (var plan in session.Layout.Plans)
            {
                if (!TypeLayout.IsTypeIdCanonical(plan.Symbol.Canonical))
                {
                    continue;
                }
                var args = ConstructedTypeCollector.TypeArgumentsOf(plan.Symbol.Canonical);
                if (args.Count != 1
                    || !TryDescribedSheet(session, MirType.Of(args[0]), out var want)
                    || !session.TryGetTypeSheet(plan.Symbol.Canonical, out var view))
                {
                    continue;
                }
                var wantPtr = builder.BuildBitCast(want, PointerType(), "tid.want");
                var viewPtr = builder.BuildBitCast(view, PointerType(), "tid.view");
                var match = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, described, wantPtr,
                    "tid.eq");
                selected = builder.BuildSelect(match, viewPtr, selected, "tid.sel");
            }
            return selected;
        }

        private static bool TryDescribedSheet(ModuleBuilder.Session session, MirType type,
            out LLVMValueRef sheet)
        {
            var key = TypeLayout.IsArray(type)
                ? TypeLayout.ArrayTypeCanonical
                : TypeLayout.BuiltinSheetCanonical(type);
            return session.TryGetTypeSheet(key, out sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet);
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
