using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

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
                UnwrapToSlot(session, builder, slots, fat, inst.InnerType, inst.Target,
                    inst.ExcTarget);
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
            // typefix：TypeId 值（含开放 Type<.generic<T>>）ABI 恒为 8B 裸
            // sheet 指针——wrap 即把指针位形打包成 tag0 胖值。必须显式
            // 先于 IsReferenceElement：inner 为占位时旧口径会把开放
            // typeid 判成 Reference，落 ProduceFatValue 对 8B 槽
            // extractvalue（native 0xC0000005，echoType 形态）。
            // sheet 字取 Type<X> 的视图 sheet：闭合 X 用 core::Type<X>
            // （b4-2 收集保证存在）；开放占位按文档化擦除锚定
            // core::Type<core::Any>（MIDDLEWARE_ARCHITECTURE：无界 =
            // core::Type<core::Any>）。unwrap 对偶不读 sheet 字，仅作帧
            // 保存胖值的类型锚点，不参与运行期类型判断。
            if (TypeLayout.IsTypeId(srcType) || TypeLayout.IsTypeId(inner))
            {
                if (source is not MirLocalOperand tidLocal)
                {
                    throw new CompilerInternalException("Nullable 装箱源必须是局部");
                }
                var tidMir = TypeLayout.IsTypeId(srcType) ? srcType : inner;
                var bits = BoxEmitter.BitsFromSlot(session, builder,
                    slots[tidLocal.Name].Slot, 8);
                var viewArg = TypeLayout.IsGenericPlaceholder(tidMir)
                    ? "core::Any"
                    : ConstructedTypeCollector.TypeArgumentsOf(tidMir.Canonical)
                        is { Count: 1 } args ? args[0] : "core::Any";
                var viewSheet = session.TypeSheetFor("core::Type<" + viewArg + ">");
                return BoxEmitter.PackFat(session, builder, viewSheet,
                    BoxEmitter.TagInline, bits, "opt");
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
            LLVMValueRef fat, MirType inner, string target, MirBlock? excTarget)
        {
            var destType = slots[target].Local.Type;
            if (MirBuilder.IsScalarOrString(destType))
            {
                inner = destType;
            }
            // typefix：TypeId unwrap = wrap 的对偶——零位形还原 null sheet，
            // 非零 payload 直写 8B 裸指针槽。禁止落 IsReferenceElement
            // （对 8B 槽 extractvalue 崩溃）也禁止落 UnboxToSlot 的
            // rigi_type_is 支（开放 .typeid<.generic<T>> 无边界 sheet，
            // 合成 sheet 会被误拒为 CastException）。
            else if (TypeLayout.IsTypeId(destType) || TypeLayout.IsTypeId(inner))
            {
                var tid = builder.BuildExtractValue(fat, 0, "opt.tid.typeid");
                var pay = builder.BuildExtractValue(fat, 1, "opt.tid.payload");
                var bits = builder.BuildOr(tid, pay, "opt.tid.bits");
                var isNull = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bits,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), "opt.tid.null");
                var ptr = builder.BuildIntToPtr(pay,
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "opt.tid.ptr");
                var zero = LLVMValueRef.CreateConstPointerNull(
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
                var sheet = builder.BuildSelect(isNull, zero, ptr, "opt.tid.sheet");
                builder.BuildStore(sheet, slots[target].Slot);
                return;
            }
            else if (IsReferenceElement(session, inner) || IsReferenceElement(session, destType))
            {
                var typeId = builder.BuildExtractValue(fat, 0, "opt.ref.typeid");
                var payload = builder.BuildExtractValue(fat, 1, "opt.ref.payload");
                // Any 接受任意非空类型；其它目标（包括开放泛型）必须
                // 与当前调用帧的真实具化 sheet 核验，禁止退化为裸模板。
                if (destType.IsAnyOrObject)
                {
                    // nullablefix：null（typeId/payload 双零胖值）在 Any/
                    // Object 目标合法——与 VM TryCast 口径一致（VmTypeOps：
                    // null 可空 → 引用形态目标得 VmNull），零胖值入槽即
                    // native 的 null Any，不得抛 CastException（
                    // MapEnumerator<K,Any> 类 unwrap 双宿主分歧根因）。
                    // 非空值 Any 本就全接受，EmitThrowOnMismatch 整体移除。
                    builder.BuildStore(ArcEmitter.ProduceFatValue(session, builder, fat,
                        "opt.unwrap"), slots[target].Slot);
                    return;
                }
                var targetSheet = NewEmitter.MaterializeClassSheet(session, builder,
                    slots, destType.Canonical);
                var (isFn, isType) = CallEmitter.DeclareHelperFace(session,
                    RuntimeFaces.TypeIs, LLVMTypeRef.Int32,
                    new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64, PointerType() });
                var hit = builder.BuildCall2(isType, isFn,
                    new[] { typeId, payload, targetSheet }, "opt.ref.is");
                var mismatch = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hit,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false),
                    "opt.ref.bad");
                // Task<T?> 完成时 result 的内层值可以合法为 null；Nullable
                // 共用零胖值表示。目标本身仍可空时只检查非空值的元素身份。
                if (TypeLayout.IsNullable(destType) || TypeLayout.IsGenericPlaceholder(destType))
                {
                    var nullBits = builder.BuildOr(typeId, payload, "opt.ref.bits");
                    var isNull = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, nullBits,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), "opt.ref.empty");
                    // 与 VM 同口径（VmTypeOps.IsReferenceLike）：泛型占位
                    // 目标在共享体执行时未闭合（非值类型 → 引用型），null
                    // 放行，由调用方以引用语义持有；Nullable 目标恒放行。
                    // 不得按闭合计数 sheet 的 nullableElement 拒绝——那会
                    // 把占位 null 收紧成 CastException，与 VM 分歧（借用
                    // 适配器 for-in 零槽元素的语义分歧来源）。
                    var allowsNull = TypeLayout.IsNullable(destType)
                        || TypeLayout.IsGenericPlaceholder(destType)
                        ? LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1, false)
                        : IsNullableSheet(session, builder, targetSheet);
                    var validNull = builder.BuildAnd(isNull, allowsNull, "opt.ref.valid.null");
                    mismatch = builder.BuildAnd(mismatch, builder.BuildNot(validNull), "opt.ref.nonnull.bad");
                }
                BoxEmitter.EmitThrowOnMismatch(session, builder, mismatch, targetSheet,
                    MirType.Of(".nullable<" + destType.Canonical + ">"), excTarget);
                builder.BuildStore(ArcEmitter.ProduceFatValue(session, builder, fat, "opt.unwrap"),
                    slots[target].Slot);
                return;
            }
            BoxEmitter.UnboxToSlot(session, builder, fat, inner, slots[target].Slot,
                MirType.Of(".nullable<" + inner.Canonical + ">"), excTarget);
        }

        private static bool IsReferenceElement(ModuleBuilder.Session session, MirType element) =>
            ArrayEmitter.ElementAbi(session, element).Kind == ArrayElementKind.Reference;

        private static LLVMValueRef IsNullableSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef sheet)
        {
            var result = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int1, "opt.nullable");
            builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false), result);
            var loadInfo = session.CurrentFunction.AppendBasicBlock("opt.nullable.sheet");
            var loadElement = session.CurrentFunction.AppendBasicBlock("opt.nullable.info");
            var done = session.CurrentFunction.AppendBasicBlock("opt.nullable.done");
            builder.BuildCondBr(builder.BuildIsNull(sheet), done, loadInfo);
            builder.PositionAtEnd(loadInfo);
            var infoField = builder.BuildStructGEP2(TypeSheetEmitter.SheetStructType(session.Context),
                sheet, (uint)TypeSheetAbi.FieldTypeInfoId, "opt.info.ptr");
            var info = builder.BuildLoad2(PointerType(), infoField, "opt.info");
            builder.BuildCondBr(builder.BuildIsNull(info), done, loadElement);
            builder.PositionAtEnd(loadElement);
            var elementField = builder.BuildStructGEP2(TypeInfoEmitter.InfoStructType(session.Context),
                info, (uint)TypeSheetAbi.InfoFieldNullableElement, "opt.element.ptr");
            var element = builder.BuildLoad2(PointerType(), elementField, "opt.element");
            builder.BuildStore(builder.BuildIsNotNull(element), result);
            builder.BuildBr(done);
            builder.PositionAtEnd(done);
            return builder.BuildLoad2(LLVMTypeRef.Int1, result, "opt.nullable.value");
        }

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
                        "char" => LLVMTypeRef.Int32,   // 32 位 Unicode 标量
                        "i16" or "u16" => LLVMTypeRef.Int16,
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
