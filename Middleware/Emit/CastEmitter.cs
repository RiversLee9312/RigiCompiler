using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// cast / cast.safe 发射（MW8c-2）：静态数值走 LLVM 转换；占位目标
    /// 走 rigi_try_cast；不相容则抛 core.CastException（MW9b-G，与 VM
    /// 同型同消息）或产 null（cast.safe）。
    /// </summary>
    internal sealed class CastEmitter : LlvmEmitVisitor<CastEmitter, MirCast>
    {
        // 与 typecheck.c rigi_cast_f64_to_int 的 kind 对齐
        private const int KindI32Sat = 0;
        private const int KindU32Sat = 1;
        private const int KindI64Sat = 2;
        private const int KindU64Sat = 3;

        protected override void VisitCore(MirCast inst, ModuleBuilder.Session session)
        {
            var builder = session.Builder;
            var slots = session.Slots;
            if (inst.Source is not MirLocalOperand source)
            {
                throw new CompilerInternalException("cast 源必须是局部");
            }
            var sourceType = slots[source.Name].Local.Type;
            var resultType = slots[inst.Target].Local.Type;
            var targetType = inst.TargetTypeRef != null
                ? MirType.Of(inst.TargetTypeRef)
                : resultType;
            // 标量/String 装入 T?（含占位 Nullable\<T\>）：走装箱包装，不是 try_cast。
            // String 16B 超 InlineLimit，必须 WrapFromSlot（tag1 堆盒），
            // 不可走 CastEmitter.WrapScalar 的 tag0 截 8B（否则 Mix.s 往返丢串）。
            // indirect 形态目标类型运行期才知，不得吃此静态捷径（VM
            // TryCast 按运行期 typeid 走数值转换/可空解包，统一归
            // EmitDynamic 的 rigi_try_cast）
            if (!inst.IsIndirect && MirBuilder.IsScalarOrString(sourceType)
                && TypeLayout.IsNullable(resultType))
            {
                var wrapped = sourceType.IsString
                    ? NullableEmitter.WrapFromSlot(session, builder, slots[source.Name].Slot,
                        sourceType)
                    : WrapScalar(session, builder, session.LoadLocal(builder, slots, source),
                        sourceType);
                builder.BuildStore(wrapped, slots[inst.Target].Slot);
                return;
            }
            // 占位源或占位目标（`boxed as T`）须走 try_cast，不得当静态
            // 不相容落入 EmitFail。
            if (inst.IsIndirect || TypeLayout.IsGenericPlaceholder(sourceType)
                || TypeLayout.IsGenericPlaceholder(targetType)
                || TypeLayout.IsGenericPlaceholder(resultType))
            {
                EmitDynamic(session, builder, slots, inst, source, sourceType, resultType);
                return;
            }
            if (MirBuilder.IsNumericScalar(sourceType)
                && MirBuilder.IsNumericScalar(targetType))
            {
                EmitNumeric(session, builder, slots, inst, source, sourceType, targetType,
                    resultType);
                return;
            }
            // 具体值 → Any/Object：CastLowering 在源仍为占位时留下 MirCast
            //（非 MirBoxAny、非 Indirect）；烘焙 extraSubst 把源改成 i32 后
            // 落入此支。按装箱发射，避免 EmitFail 抛 .i32 → .any。
            if (targetType.IsAnyOrObject)
            {
                var fat = LoadSourceFat(session, builder, slots, source.Name, sourceType);
                StoreConverted(session, builder, slots, inst, sourceType, resultType, fat);
                return;
            }
            // Any/Object → 具体：数组快照还原 `Any as i32` 等。原先落入
            // EmitFail；getElement\<T\> 能过是因为 T 占位走了 EmitDynamic。
            if (sourceType.IsAnyOrObject)
            {
                EmitDynamic(session, builder, slots, inst, source, sourceType, resultType);
                return;
            }
            // R3：值类型沿 open struct 继承链向上转换（Child→Base）——
            // VM TryCast TypesAssignable → Copy 恒过；native 内联槽按
            // 静态类型定尺寸，结构性保留前缀（继承布局前缀式切片）。
            // 此前落 EmitFail 无条件抛 CastException。已知残留差：经
            // 具体类型航点后运行期身份（is 判定）不保留（胖 ABI 改造
            // 超出本修复范围）
            if (IsStructUpcast(session, sourceType, targetType))
            {
                ArcEmitter.EmitCopyRichValue(session, builder, slots[inst.Target].Slot,
                    slots[source.Name].Slot, targetType);
                return;
            }
            EmitFail(session, builder, slots, inst, sourceType, targetType, resultType);
        }

        // 占位目标 / 占位源：源装箱为胖引用后 rigi_try_cast
        private static void EmitDynamic(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCast inst,
            MirLocalOperand source, MirType sourceType, MirType resultType)
        {
            var fat = LoadSourceFat(session, builder, slots, source.Name, sourceType);
            var typeId = builder.BuildExtractValue(fat, 0, "cast.tid");
            var payload = builder.BuildExtractValue(fat, 1, "cast.pl");
            var targetSheet = TargetSheet(session, builder, slots, inst);
            var outTid = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "cast.otid");
            var outPl = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "cast.opl");
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.TryCast,
                LLVMTypeRef.Int32,
                new[]
                {
                    LLVMTypeRef.Int64, LLVMTypeRef.Int64, PointerType(),
                    PointerType(), PointerType(),
                });
            var raw = builder.BuildCall2(fnType, fn,
                new[] { typeId, payload, targetSheet, outTid, outPl }, "cast.ok");
            var ok = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, raw,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "cast.hit");
            var current = session.CurrentFunction;
            var hit = current.AppendBasicBlock("cast.hit");
            var miss = current.AppendBasicBlock("cast.miss");
            var join = current.AppendBasicBlock("cast.join");
            builder.BuildCondBr(ok, hit, miss);

            builder.PositionAtEnd(hit);
            var hitTid = builder.BuildLoad2(LLVMTypeRef.Int64, outTid, "cast.htid");
            var hitPl = builder.BuildLoad2(LLVMTypeRef.Int64, outPl, "cast.hpl");
            var hitFat = PackBits(session, builder, hitTid, hitPl, "cast.hf");
            // 已是胖引用的源：结果共享，需 acquire；值类型新装箱：唯一所有权
            if (SourceIsFat(session, sourceType))
            {
                hitFat = ArcEmitter.ProduceFatValue(session, builder, hitFat, "cast.acq");
            }
            StoreConverted(session, builder, slots, inst, sourceType, resultType, hitFat);
            builder.BuildBr(join);

            builder.PositionAtEnd(miss);
            if (inst.IsSafe)
            {
                builder.BuildStore(
                    LLVMValueRef.CreateConstNull(
                        TypeLayout.MapType(session.Context, resultType)),
                    slots[inst.Target].Slot);
                builder.BuildBr(join);
            }
            else
            {
                // MW9b-G：try_cast 落空 → 抛 CastException（fromType =
                // 静态源类型名常量，toType = 目标 sheet 的 TypeInfo.name）
                EmitCastThrow(session, builder, inst, sourceType, targetSheet);
            }

            builder.PositionAtEnd(join);
        }

        private static void EmitNumeric(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCast inst,
            MirLocalOperand source, MirType sourceType, MirType targetType, MirType resultType)
        {
            var value = session.LoadLocal(builder, slots, source);
            var converted = ConvertNumeric(session, builder, value, sourceType, targetType);
            if (TypeLayout.TryGetNullableInner(resultType, out var inner)
                && inner.Canonical == targetType.Canonical)
            {
                builder.BuildStore(WrapScalar(session, builder, converted, targetType),
                    slots[inst.Target].Slot);
                return;
            }
            builder.BuildStore(converted, slots[inst.Target].Slot);
        }

        private static void EmitFail(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCast inst,
            MirType sourceType, MirType targetType, MirType resultType)
        {
            var sheet = TypeSheetOf(session, targetType);
            EmitMiss(session, builder, slots, inst, sourceType, sheet, resultType);
        }

        private static void EmitMiss(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCast inst,
            MirType sourceType, LLVMValueRef targetSheet, MirType resultType)
        {
            if (inst.IsSafe)
            {
                builder.BuildStore(
                    LLVMValueRef.CreateConstNull(TypeLayout.MapType(session.Context, resultType)),
                    slots[inst.Target].Slot);
                return;
            }
            // MW9b-G：静态不相容 → 抛 CastException（fromType = 静态源
            // 类型名常量，toType = 目标 sheet 的 TypeInfo.name）
            EmitCastThrow(session, builder, inst, sourceType, targetSheet);
            // 后续 MIR 指令落到无前驱死块，避免终结后再插指令
            builder.PositionAtEnd(session.CurrentFunction.AppendBasicBlock("cast.dead"));
        }

        // 抛 core.CastException(fromType, toType)：fromType = 发射期静态
        // 源类型名字符串常量；toType = 目标 sheet 运行期显示名
        private static void EmitCastThrow(ModuleBuilder.Session session, LLVMBuilderRef builder,
            MirCast inst, MirType sourceType, LLVMValueRef targetSheet)
        {
            var fromName = ExceptionEmitter.StaticTypeName(session, sourceType);
            var toName = ExceptionEmitter.LoadTypeDisplayNameFromSheet(session, builder,
                targetSheet);
            ExceptionEmitter.EmitThrowNewException(session, builder, "core::CastException",
                "fromType", new[] { fromName, toName }, inst.ExcTarget);
        }

        private static LLVMValueRef ConvertNumeric(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef value, MirType from, MirType to)
        {
            var destTy = TypeLayout.MapType(session.Context, to);
            if (IsFloatKey(from.Key) && IsIntKey(to.Key))
            {
                return FloatToInt(session, builder, value, from, to, destTy);
            }
            if (IsIntKey(from.Key) && IsFloatKey(to.Key))
            {
                return IsSignedInt(from)
                    ? builder.BuildSIToFP(value, destTy, "cast.sitofp")
                    : builder.BuildUIToFP(value, destTy, "cast.uitofp");
            }
            if (IsFloatKey(from.Key) && IsFloatKey(to.Key))
            {
                if (from.Key == to.Key)
                {
                    return value;
                }
                return from.Key == "float"
                    ? builder.BuildFPExt(value, destTy, "cast.fpext")
                    : builder.BuildFPTrunc(value, destTy, "cast.fptrunc");
            }
            var srcWidth = value.TypeOf.IntWidth;
            var dstWidth = destTy.IntWidth;
            if (srcWidth < dstWidth)
            {
                return IsSignedInt(from)
                    ? builder.BuildSExt(value, destTy, "cast.sext")
                    : builder.BuildZExt(value, destTy, "cast.zext");
            }
            if (srcWidth > dstWidth)
            {
                return builder.BuildTrunc(value, destTy, "cast.trunc");
            }
            return value;
        }

        private static LLVMValueRef FloatToInt(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef value, MirType from, MirType to,
            LLVMTypeRef destTy)
        {
            var asF64 = from.Key == "float"
                ? builder.BuildFPExt(value, LLVMTypeRef.Double, "cast.fpext")
                : value;
            var kind = FloatToIntKind(to.Key);
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session,
                RuntimeFaces.CastF64ToInt, LLVMTypeRef.Int64,
                new[] { LLVMTypeRef.Double, LLVMTypeRef.Int32 });
            var bits = builder.BuildCall2(fnType, fn,
                new[]
                {
                    asF64,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)kind, true),
                }, "cast.f2i");
            if (destTy.IntWidth == 64)
            {
                return bits;
            }
            return builder.BuildTrunc(bits, destTy, "cast.f2i.t");
        }

        // R3：值类型向上转换判定——source 声明沿 extends 链可达 target
        //（两侧剥构造实参取模板；同型/非值类型不算）
        private static bool IsStructUpcast(ModuleBuilder.Session session, MirType source,
            MirType target)
        {
            if (!session.IsInlineValueType(source, out _)
                || !session.IsInlineValueType(target, out _))
            {
                return false;
            }
            var targetKey = Bil.BilVerificationContext.StripTypeArguments(
                Symbols.MwTypeKey.Normalize(target.Canonical));
            var sourceKey = Bil.BilVerificationContext.StripTypeArguments(
                Symbols.MwTypeKey.Normalize(source.Canonical));
            if (sourceKey == targetKey)
            {
                return false;
            }
            var sym = session.Symbols.FindTypeByRef(sourceKey);
            for (var depth = 0; sym != null && depth < 64; depth++)
            {
                if (sym.Declaration.ExtendsType is not { } extendsRef)
                {
                    return false;
                }
                var baseKey = Bil.BilVerificationContext.StripTypeArguments(
                    Symbols.MwTypeKey.Normalize(extendsRef));
                if (baseKey == targetKey)
                {
                    return true;
                }
                sym = session.Symbols.FindTypeByRef(baseKey);
            }
            return false;
        }

        private static int FloatToIntKind(string key) => key switch
        {
            "u32" => KindU32Sat,
            "i64" => KindI64Sat,
            "u64" => KindU64Sat,
            _ => KindI32Sat,
        };

        private static LLVMValueRef LoadSourceFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            string sourceName, MirType sourceType)
        {
            if (SourceIsFat(session, sourceType))
            {
                return session.LoadLocal(builder, slots, new MirLocalOperand(sourceName));
            }
            return BoxEmitter.BoxFromLocal(session, builder, slots, sourceName);
        }

        private static bool SourceIsFat(ModuleBuilder.Session session, MirType type) =>
            !MirBuilder.IsScalarOrString(type)
            && !TypeLayout.IsTypeId(type)
            && !session.IsInlineValueType(type, out _);

        private static LLVMValueRef TargetSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCast inst)
        {
            if (inst.TargetTypeId != null)
            {
                return session.LoadLocal(builder, slots, inst.TargetTypeId);
            }
            if (inst.TargetTypeRef == null)
            {
                throw new CompilerInternalException("cast 缺目标类型");
            }
            return TypeSheetOf(session, MirType.Of(inst.TargetTypeRef));
        }

        private static void StoreConverted(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCast inst,
            MirType sourceType, MirType resultType, LLVMValueRef fat)
        {
            if (IsFatResult(session, resultType))
            {
                builder.BuildStore(fat, slots[inst.Target].Slot);
                return;
            }
            var inner = inst.TargetTypeRef != null
                ? MirType.Of(inst.TargetTypeRef)
                : resultType;
            // try_cast 命中路径：sheet 已核验，拆箱不符理论上不可达；
            // 守卫仍挂同一异常边（防御）
            BoxEmitter.UnboxToLocal(session, builder, slots, fat, inner, inst.Target,
                sourceType, inst.ExcTarget);
        }

        private static bool IsFatResult(ModuleBuilder.Session session, MirType type) =>
            TypeLayout.IsGenericPlaceholder(type)
            || TypeLayout.IsNullable(type)
            || type.IsAny || type.IsObject
            || (!MirBuilder.IsScalarOrString(type) && !TypeLayout.IsTypeId(type)
                && !session.IsInlineValueType(type, out _));

        private static LLVMValueRef WrapScalar(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef value, MirType inner)
        {
            var tmp = LlvmEmitEnvironment.BuildEntryAlloca(builder, TypeLayout.MapType(session.Context, inner), "cast.nv");
            builder.BuildStore(value, tmp);
            var size = BoxEmitter.ValueByteSize(session, inner);
            var bits = BoxEmitter.BitsFromSlot(session, builder, tmp, size);
            var sheet = BoxEmitter.TypeSheetOf(session, inner);
            return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline, bits,
                "cast.nw");
        }

        private static LLVMValueRef PackBits(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeId, LLVMValueRef payload, string prefix)
        {
            var fat = LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context));
            fat = builder.BuildInsertValue(fat, typeId, 0, prefix + ".t0");
            return builder.BuildInsertValue(fat, payload, 1, prefix + ".pl");
        }

        private static LLVMValueRef TypeSheetOf(ModuleBuilder.Session session, MirType type)
        {
            var key = TypeLayout.BuiltinSheetCanonical(type);
            if (session.TryGetTypeSheet(key, out var sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet))
            {
                return sheet;
            }
            return session.TypeSheetFor(type.Canonical);
        }

        private static bool IsSignedInt(MirType type) =>
            type.Key is "i8" or "i16" or "i32" or "i64";

        private static bool IsIntKey(string key) =>
            key is "char" or "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64";

        private static bool IsFloatKey(string key) => key is "float" or "double";

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
