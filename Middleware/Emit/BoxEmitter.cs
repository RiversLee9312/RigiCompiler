using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

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
        internal sealed class Box : LlvmEmitVisitor<Box, MirBoxAny>
        {
            protected override void VisitCore(MirBoxAny inst, ModuleBuilder.Session session) =>
                EmitBox(session, session.Builder, session.Slots, inst);
        }

        internal sealed class Unbox : LlvmEmitVisitor<Unbox, MirUnboxAny>
        {
            protected override void VisitCore(MirUnboxAny inst, ModuleBuilder.Session session) =>
                EmitUnbox(session, session.Builder, session.Slots, inst);
        }

        internal static LLVMValueRef PackObject(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeSheet, LLVMValueRef objectPointer)
        {
            var payload = builder.BuildPtrToInt(objectPointer, LLVMTypeRef.Int64, "new.payload");
            return PackFat(session, builder, typeSheet, TagObject, payload, "new");
        }

        // 占位槽或 Any/Object：值类型实参须装箱（调用点 i32 → .any 与
        // extraSubst 后静态 MirCast 同源）
        internal static bool NeedsBox(ModuleBuilder.Session session, MirType from, MirType to) =>
            (TypeLayout.IsGenericPlaceholder(to) || to.IsAnyOrObject)
            && !TypeLayout.IsGenericPlaceholder(from)
            && !from.IsAnyOrObject
            && IsBoxableValue(session, from)
            // G1：开放构造值类型形参（WPair<.generic<$.generic.T>>）仍是
            // 内联值 ABI（指针），不是胖盒——不得装箱
            && !session.IsInlineValueType(to, out _);

        internal static bool NeedsUnbox(ModuleBuilder.Session session, MirType from, MirType to) =>
            // 合成接口可承载值接收者；其胖槽解箱与泛型槽使用同一编组路径。
            (TypeLayout.IsGenericPlaceholder(from)
                || session.Layout?.Find(from.Canonical)?.Kind == TypeLayoutKind.Interface)
            && !TypeLayout.IsGenericPlaceholder(to)
            && IsBoxableValue(session, to)
            // 开放构造值类型（WPair<.generic<…>>）仍是内联值 ABI（指
            // 针），不是胖盒——canonical 含占位但槽非 16B 胖值
            && !session.IsInlineValueType(from, out _);

        private static bool IsBoxableValue(ModuleBuilder.Session session, MirType type) =>
            MirBuilder.IsScalarOrString(type) || session.IsInlineValueType(type, out _);

        private static void EmitBox(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirBoxAny inst)
        {
            if (inst.Source is not MirLocalOperand local)
            {
                throw new CompilerInternalException("Box 源必须是局部");
            }
            // typefix 防御：MirBoxAny 产物是 16B 胖值，目标槽必须是胖引用
            // 表示（.any / 泛型占位）。typeid 等 8B 裸指针槽若被误作装箱
            // 目标，opaque pointer 下 BuildStore 无点类型检查，16B 直接写
            // 穿 8B 槽、读取端再静默截断第 0 字段（协程帧编组 ABI 错配
            // 曾借此存活为 "core::Type<X>" 缺陷）——编组错误在此处响亮
            // 失败，不允许落 IR。
            var targetMirType = slots[inst.Target].Local.Type;
            if (TypeLayout.ClassifySlot(session.Layout, targetMirType)
                != ManagedSlotKind.FatReference)
            {
                throw new CompilerInternalException(
                    "MirBoxAny 目标槽必须是胖引用槽（.any/泛型占位），实际: "
                    + targetMirType.Canonical);
            }
            builder.BuildStore(BoxFromLocal(session, builder, slots, local.Name),
                slots[inst.Target].Slot);
        }

        // 值类型/String → 16B 胖值（泛型槽 / .any 共用）
        internal static LLVMValueRef BoxFromLocal(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, string sourceName) =>
            BoxFromSlot(session, builder, slots[sourceName].Slot,
                slots[sourceName].Local.Type);

        // BoxFromLocal 的槽指针形态（无 MIR 局部的临时槽装箱用）
        internal static LLVMValueRef BoxFromSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef sourceSlot, MirType sourceType)
        {
            // TypeId 装箱视图 = Type<payload> 构造 sheet（运行期边界，对齐 VM TypeRef）
            if (TypeLayout.IsTypeId(sourceType))
            {
                var described = builder.BuildLoad2(
                    TypeLayout.MapType(session.Context, sourceType), sourceSlot, "box.tidld");
                var view = ResolveTypeIdViewSheet(session, builder, described, sourceType);
                EmitThrowOnMismatch(session, builder, builder.BuildIsNull(view, "box.tid.missing"),
                    described, sourceType, null);
                var bits = builder.BuildPtrToInt(described, LLVMTypeRef.Int64, "box.tid");
                return PackFat(session, builder, view, TagInline, bits, "box");
            }
            var size = ValueByteSize(session, sourceType);
            var sheet = ResolveValueSheet(session, builder, sourceType);
            var tag = size <= InlineLimit ? TagInline : TagHeapValue;
            LLVMValueRef payload;
            if (tag == TagInline)
            {
                payload = PackInlinePayload(session, builder, sourceSlot, sourceType, size);
            }
            else
            {
                var block = Malloc(session, builder, size);
                session.EmitMemCopy(builder, block, sourceSlot, size);
                ArcEmitter.EmitValueAcquire(session, builder, block, sourceType);
                payload = builder.BuildPtrToInt(block, LLVMTypeRef.Int64, "box.payload");
            }
            return PackFat(session, builder, sheet, tag, payload, "box");
        }

        private static void EmitUnbox(ModuleBuilder.Session session, LLVMBuilderRef builder,
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
            MirBlock? excTarget, bool allowNullSource = false) =>
            UnboxToSlot(session, builder, fat, targetType, slots[target].Slot, fromType,
                excTarget, allowNullSource);

        // UnboxToLocal 的槽指针形态（无 MIR 局部的临时槽拆箱用：占位
        // 接收者/占位值类型宿主的调用点适配）
        internal static void UnboxToSlot(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef fat, MirType targetType, LLVMValueRef targetSlot, MirType fromType,
            MirBlock? excTarget, bool allowNullSource = false)
        {
            // R3：open struct 目标可含子类型盒（VM 动态解析可过）——
            // 中枢转协变链守卫 + 前缀拷贝（所有调用点同口径）
            if (IsOpenStructType(session, targetType))
            {
                UnboxSubtypeToTemp(session, builder, fat, targetType, targetSlot, fromType,
                    excTarget);
                return;
            }
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
            var tagBad = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, tag,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, expectedTag, false),
                "unbox.tag.bad");
            var mismatch = builder.BuildOr(tagBad,
                    builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sheetBits, wantSheet,
                        "unbox.sheet.bad"),
                    "unbox.bad");
            if (TypeLayout.IsTypeId(targetType))
            {
                var (check, checkType) = CallEmitter.DeclareHelperFace(session,
                    RuntimeFaces.TypeIs, LLVMTypeRef.Int32,
                    new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64, PointerType() });
                var hit = builder.BuildCall2(checkType, check, new[] { typeId, payload, sheet }, "unbox.type.bound");
                mismatch = builder.BuildOr(tagBad, builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
                    hit, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), "unbox.type.bad"));
            }
            // null 短路（VM 同口径，VmTypeOps.TryCast：null → 引用型目标
            // 放行）：String 槽 null = 零 {data,len} 位形。仅放行「非显式
            // cast」调用点（StoreCoercedResult 的泛型返回值闭合接收——VM
            // 侧返回值赋槽无 cast 检查）；显式 as cast（CastEmitter）维持
            // VM TryCast 的 null 拒绝口径，防止 String? null as String 被
            // 宽放行造成双宿主分歧。值类型目标恒 CastException。
            if (targetType.IsString && allowNullSource)
            {
                var fn = session.CurrentFunction;
                var nullBits = builder.BuildOr(typeId, payload, "unbox.nullbits");
                var isNull = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, nullBits,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), "unbox.isnull");
                var nullBlock = fn.AppendBasicBlock("unbox.null");
                var checkBlock = fn.AppendBasicBlock("unbox.check");
                var endBlock = fn.AppendBasicBlock("unbox.end");
                builder.BuildCondBr(isNull, nullBlock, checkBlock);
                builder.PositionAtEnd(nullBlock);
                builder.BuildStore(
                    LLVMValueRef.CreateConstNull(StringAbi.ValueType(session.Context)),
                    targetSlot);
                builder.BuildBr(endBlock);
                builder.PositionAtEnd(checkBlock);
                EmitThrowOnMismatch(session, builder, mismatch, sheet, fromType, excTarget);
                if (expectedTag == TagInline)
                {
                    UnpackInline(session, builder, payload, targetType, size, targetSlot);
                }
                else
                {
                    var blk = builder.BuildIntToPtr(payload, PointerType(), "unbox.block");
                    session.EmitMemCopy(builder, targetSlot, blk, size);
                    ArcEmitter.EmitValueAcquire(session, builder, targetSlot, targetType);
                }
                builder.BuildBr(endBlock);
                builder.PositionAtEnd(endBlock);
                return;
            }
            EmitThrowOnMismatch(session, builder, mismatch, sheet, fromType, excTarget);

            if (expectedTag == TagInline)
            {
                UnpackInline(session, builder, payload, targetType, size, targetSlot);
            }
            else
            {
                var block = builder.BuildIntToPtr(payload, PointerType(), "unbox.block");
                session.EmitMemCopy(builder, targetSlot, block, size);
                ArcEmitter.EmitValueAcquire(session, builder, targetSlot, targetType);
            }
        }

        // ===== R3：open struct 子类型盒的协变拆箱 =====

        // open struct 判定（可含子类型 → 占位拆箱守卫须走协变链而非
        // 精确 sheet）：构造形先剥实参取模板
        internal static bool IsOpenStructType(ModuleBuilder.Session session, MirType type)
        {
            if (!session.IsInlineValueType(type, out _))
            {
                return false;
            }
            var sym = session.Symbols.FindTypeByRef(
                Bil.BilVerificationContext.StripTypeArguments(
                    MwTypeKey.Normalize(type.Canonical)));
            if (sym == null || sym.Declaration.Kind != Bil.BilTypeKind.Struct)
            {
                return false;
            }
            foreach (var modifier in sym.Declaration.Modifiers)
            {
                if (modifier is Bil.BilKeywordModifier keyword
                    && keyword.Keyword == Bil.BilKeyword.Open)
                {
                    return true;
                }
            }
            return false;
        }

        // 子类型盒拆到目标类型尺寸的临时槽（占位接收者 .this 适配用）：
        // 盒保实际子类型 sheet，精确匹配会误抛 CastException（VM 动态
        // 解析可过）——守卫 = tag 值盒 + rigi_type_is 协变链；temp 取
        // 目标类型尺寸前缀（继承布局前缀式，ClassLayout 基类字段在前）。
        // inline 盒（≤8B）payload 低位字节即值位（rich 前缀含 ref 必
        // 超 8B 走 heap，inline 恒无引用计数字段）；heap 盒 payload 即
        // 堆块指针。heap 路径对 temp 登记前缀引用（RichTemp 销毁配平）
        internal static void UnboxSubtypeToTemp(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef fat, MirType targetType,
            LLVMValueRef targetSlot, MirType fromType, MirBlock? excTarget)
        {
            var size = ValueByteSize(session, targetType);
            var sheet = TypeSheetOf(session, targetType);
            var typeId = builder.BuildExtractValue(fat, 0, "unbox.sub.typeid");
            var payload = builder.BuildExtractValue(fat, 1, "unbox.sub.payload");
            var tag = builder.BuildLShr(typeId,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, TagShift, false), "unbox.sub.tag");
            var (isFn, isType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.TypeIs,
                LLVMTypeRef.Int32, new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64, PointerType() });
            var isHit = builder.BuildCall2(isType, isFn, new[] { typeId, payload, sheet },
                "unbox.sub.is");
            var mismatch = builder.BuildOr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, tag,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, TagHeapValue, false),
                    "unbox.sub.tagbad"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, isHit,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "unbox.sub.isbad"),
                "unbox.sub.bad");
            EmitThrowOnMismatch(session, builder, mismatch, sheet, fromType, excTarget);
            var fn = session.CurrentFunction;
            var inlineBlock = fn.AppendBasicBlock("unbox.sub.inline");
            var heapBlock = fn.AppendBasicBlock("unbox.sub.heap");
            var mergeBlock = fn.AppendBasicBlock("unbox.sub.merge");
            builder.BuildCondBr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tag,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, TagInline, false),
                    "unbox.sub.isinline"),
                inlineBlock, heapBlock);
            builder.PositionAtEnd(inlineBlock);
            var bits = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64,
                "unbox.sub.bits");
            builder.BuildStore(payload, bits);
            session.EmitMemCopy(builder, targetSlot, bits, size);
            builder.BuildBr(mergeBlock);
            builder.PositionAtEnd(heapBlock);
            var block = builder.BuildIntToPtr(payload, PointerType(), "unbox.sub.block");
            session.EmitMemCopy(builder, targetSlot, block, size);
            ArcEmitter.EmitValueAcquire(session, builder, targetSlot, targetType);
            builder.BuildBr(mergeBlock);
            builder.PositionAtEnd(mergeBlock);
        }

        // 子类型盒的 .this 写回（身份保留，VM 占位槽原地生效同口径）：
        // 不重装箱（重装箱会切成目标类型、丢子类型尾字段与运行期身份），
        // 原地补丁——inline 盒覆写 payload 低位字节（无引用）；heap 盒
        // 前缀「旧引用归还 → 拷入 → 新引用登记」（RC 配平：temp 的拆箱
        // 时 acquire 随 RichTemp 销毁，盒对新前缀自持一份）。异常路径
        // 随 pending/展开绕过本写回（VM copy 语义——部分变异不可见）
        internal static void EmitSubtypeBoxWriteback(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            string sourceName, LLVMValueRef tempSlot, MirType valueType)
        {
            var size = ValueByteSize(session, valueType);
            var fat = session.LoadLocal(builder, slots, new MirLocalOperand(sourceName));
            var typeId = builder.BuildExtractValue(fat, 0, "wb.sub.typeid");
            var payload = builder.BuildExtractValue(fat, 1, "wb.sub.payload");
            var tag = builder.BuildLShr(typeId,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, TagShift, false), "wb.sub.tag");
            var fn = session.CurrentFunction;
            var inlineBlock = fn.AppendBasicBlock("wb.sub.inline");
            var heapBlock = fn.AppendBasicBlock("wb.sub.heap");
            var mergeBlock = fn.AppendBasicBlock("wb.sub.merge");
            builder.BuildCondBr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tag,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, TagInline, false),
                    "wb.sub.isinline"),
                inlineBlock, heapBlock);
            builder.PositionAtEnd(inlineBlock);
            var mask = size >= 8
                ? unchecked((ulong)-1)
                : (1UL << (8 * size)) - 1UL;
            var tempBits = BitsFromSlot(session, builder, tempSlot, size);
            var kept = builder.BuildAnd(payload,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, ~mask, false), "wb.sub.keep");
            var patch = builder.BuildAnd(tempBits,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, mask, false), "wb.sub.patch");
            var newPayload = builder.BuildOr(kept, patch, "wb.sub.pl");
            var newFat = LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context));
            newFat = builder.BuildInsertValue(newFat, typeId, 0, "wb.sub.t0");
            newFat = builder.BuildInsertValue(newFat, newPayload, 1, "wb.sub.t1");
            builder.BuildStore(newFat, slots[sourceName].Slot);
            builder.BuildBr(mergeBlock);
            builder.PositionAtEnd(heapBlock);
            var block = builder.BuildIntToPtr(payload, PointerType(), "wb.sub.block");
            if (session.IsInlineValueType(valueType, out var plan) && plan.RefMapCount > 0)
            {
                ArcEmitter.EmitDestroyRichValue(session, builder, block,
                    ArcEmitter.SheetOf(session, valueType));
            }
            session.EmitMemCopy(builder, block, tempSlot, size);
            ArcEmitter.EmitValueAcquire(session, builder, block, valueType);
            builder.BuildBr(mergeBlock);
            builder.PositionAtEnd(mergeBlock);
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
            LLVMBuilderRef builder, LLVMValueRef sourceSlot, MirType sourceType, int size)
        {
            if (session.IsInlineValueType(sourceType, out _))
            {
                return BitsFromSlot(session, builder, sourceSlot, size);
            }
            var value = builder.BuildLoad2(
                TypeLayout.MapType(session.Context, sourceType), sourceSlot, "box.val");
            // .typeid 布局 = ptr；payload = TypeSheet* 位模式
            if (TypeLayout.IsTypeId(sourceType))
            {
                return builder.BuildPtrToInt(value, LLVMTypeRef.Int64, "box.tid");
            }
            return ScalarToI64(builder, value, sourceType);
        }

        private static void UnpackInline(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef payload, MirType targetType, int size, LLVMValueRef targetSlot)
        {
            if (session.IsInlineValueType(targetType, out _))
            {
                var tmp = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "unbox.bits");
                builder.BuildStore(payload, tmp);
                session.EmitMemCopy(builder, targetSlot, tmp, size);
                return;
            }
            if (TypeLayout.IsTypeId(targetType))
            {
                builder.BuildStore(builder.BuildIntToPtr(payload, PointerType(), "unbox.tid"),
                    targetSlot);
                return;
            }
            builder.BuildStore(UnboxScalarBits(builder, payload, targetType),
                targetSlot);
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
                var tmp = LlvmEmitEnvironment.BuildEntryAlloca(builder, 
                    LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)plan.Size), "ctor.val");
                tmp.Alignment = (uint)plan.Alignment;
                if (plan.Size <= InlineLimit)
                {
                    var bits = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "ctor.bits");
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
                var tmp = LlvmEmitEnvironment.BuildEntryAlloca(builder, StringAbi.ValueType(session.Context), "ctor.str");
                var block = builder.BuildIntToPtr(payload, PointerType(), "ctor.sblk");
                session.EmitMemCopy(builder, tmp, block, 16);
                return builder.BuildLoad2(StringAbi.ValueType(session.Context), tmp, "ctor.sld");
            }
            return UnboxScalarBits(builder, payload, declared);
        }

        internal static LLVMValueRef BitsFromSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef slot, int size)
        {
            var tmp = LlvmEmitEnvironment.BuildEntryAlloca(builder, LLVMTypeRef.Int64, "box.bits");
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
                        "char" => LLVMTypeRef.Int32,   // 32 位 Unicode 标量
                        "i16" or "u16" => LLVMTypeRef.Int16,
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
        internal static void EmitThrowOnMismatch(ModuleBuilder.Session session,
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
            return session.TypeSheetFor(type.Canonical);
        }

        // 泛型值没有对象头，实例方法里的 this 仍是模板静态类型。
        // 装箱时利用现有类级 typeid 参数选择已具化 sheet，保留值的真实类型。
        private static LLVMValueRef ResolveValueSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder, MirType type)
        {
            var selected = builder.BuildBitCast(TypeSheetOf(session, type), PointerType(), "box.sheet");
            var template = session.Symbols.FindTypeByRef(type.Canonical);
            if (template == null || !GenericAbi.IsValueTypeOwner(template)
                || template.Declaration.GenericParameters.Count == 0
                || GenericAbi.IsClosedConstructed(type.Canonical)) return selected;
            var sourceArgs = ConstructedTypeCollector.TypeArgumentsOf(type.Canonical);
            foreach (var plan in session.Layout!.Plans)
            {
                if (!GenericAbi.IsClosedConstructed(plan.Symbol.Canonical)
                    || plan.Symbol.Declaration != template.Declaration) continue;
                var actualArgs = ConstructedTypeCollector.TypeArgumentsOf(plan.Symbol.Canonical);
                LLVMValueRef match = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1, false);
                var usable = true;
                for (var i = 0; i < actualArgs.Count; i++)
                {
                    var name = template.Declaration.GenericParameters[i];
                    if (sourceArgs.Count > i && !GenericAbi.TryPlaceholderName(sourceArgs[i], out name))
                    {
                        usable = false;
                        break;
                    }
                    if (!session.Slots.ContainsKey(".generic." + name)) { usable = false; break; }
                    var current = session.LoadLocal(builder, session.Slots, new MirLocalOperand(".generic." + name));
                    var want = builder.BuildBitCast(session.TypeSheetFor(actualArgs[i]), PointerType(), "box.arg.sheet");
                    match = builder.BuildAnd(match, builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
                        current, want, "box.arg.eq"), "box.args.eq");
                }
                if (usable) selected = builder.BuildSelect(match,
                    builder.BuildBitCast(session.TypeSheetFor(plan.Symbol.Canonical), PointerType(), "box.actual.sheet"),
                    selected, "box.sheet.select");
            }
            return selected;
        }

        // TypeId 运行期类型 = Type<payload>：按已收集的构造 sheet 选视图。
        // VM TypeRef = .typeid<ActualType>，不变（无 Type 协变）。
        // 完备性依赖收集侧：ConstructedTypeCollector 为全部 typeof 可达
        // 类型（已收集构造 + 非 external 声明类型 + 内建标量/String）补
        // `.typeid<X>` 视图（b4-2）；此处只按表选择，缺失即抛
        // CastException（防御——漏收集时响亮失败，不静默退化）。
        internal static LLVMValueRef ResolveTypeIdViewSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef describedSheet, MirType staticType)
        {
            // Type<X> 不变；未匹配时不能用静态边界或开放模板冒充真实身份。
            var selected = LLVMValueRef.CreateConstPointerNull(PointerType());
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
            var key = TypeLayout.BuiltinSheetCanonical(type);
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

        // Malloc 的运行时尺寸变体：泛型占位数组读路径的元素 size 来自
        // elemSheet 运行时读数，编译期拿不到常量（size 为 i32）。
        internal static LLVMValueRef MallocDynamic(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef size)
        {
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.Malloc,
                PointerType(), new[] { LLVMTypeRef.Int32 });
            return builder.BuildCall2(fnType, fn, new[] { size }, "box.mem");
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
