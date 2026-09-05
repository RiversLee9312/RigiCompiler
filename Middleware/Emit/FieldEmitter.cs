using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 字段访问发射（Emit 分面，MW4 批 2）：CRTP visitor。胖引用 extractvalue payload →
    /// 字节 GEP（Layout 计划偏移）→ load/store。标量/String 16B/胖引用槽
    /// 按 TypeLayout 映射；内联值类型字段随批 3（rich 值类型字段的写随
    /// MW7——memcpy 会漏 ARC）；ARC 注入属 MW7，本批不注入。
    /// </summary>
    internal static class FieldEmitter
    {
        internal sealed class Get : LlvmEmitVisitor<Get, MirGetField>
        {
            protected override void VisitCore(MirGetField inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                if (TypeLayout.IsLengthField(inst.FieldSymbol))
                {
                    var lengthPointer = FieldPointer(session, builder, slots, inst.Object,
                        TypeLayout.ArrayLengthOffset);
                    var length = builder.BuildLoad2(LLVMTypeRef.Int32, lengthPointer, "array.len");
                    builder.BuildStore(length, slots[inst.Target].Slot);
                    return;
                }
                var field = Resolve(session, inst.FieldSymbol);
                var placeholderHost = MaterializePlaceholderValueHost(session, builder, slots,
                    inst.Object, inst.FieldSymbol, inst.ExcTarget);
                var pointer = placeholderHost != null
                    ? ByteGep(builder, placeholderHost.HostSlot, field.Offset)
                    : FieldPointer(session, builder, slots, inst.Object, field.Offset);
                var fieldType = FieldMirType(inst.FieldSymbol);
                if (field.EmbeddedPlan != null)
                {
                    ArcEmitter.EmitInitRichValue(session, builder, slots[inst.Target].Slot,
                        pointer, fieldType);
                    placeholderHost?.Destroy(session, builder);
                    return;
                }
                var value = builder.BuildLoad2(FieldType(session, inst.FieldSymbol), pointer,
                    "field.get");
                var targetType = slots[inst.Target].Local.Type;
                if (BoxEmitter.NeedsUnbox(session, fieldType, targetType))
                {
                    BoxEmitter.UnboxToLocal(session, builder, slots, value, targetType,
                        inst.Target, fieldType, inst.ExcTarget);
                    placeholderHost?.Destroy(session, builder);
                    return;
                }
                // MW12 清偿：借用字段（.capture.this / #.host@）的读侧不再
                // 裸取——目标是 MIR 托管槽，RcInjection 出口恒 release，
                // 裸取 = 净 -1（宿主提前析构/UAF，macroGC 上线后暴露为
                // 崩溃）。借用设计只豁免「字段本身不计数」（写侧 raw store
                // + 不进 refMap 破环），读侧的临时 +1/-1 配平不破坏该设计。
                switch (TypeLayout.ClassifySlot(session.Layout, fieldType))
                {
                    case ManagedSlotKind.FatReference:
                        builder.BuildStore(ArcEmitter.ProduceFatValue(session, builder, value,
                            "field.get"), slots[inst.Target].Slot);
                        break;
                    case ManagedSlotKind.String:
                        builder.BuildStore(ArcEmitter.ProduceStringValue(session, builder, value),
                            slots[inst.Target].Slot);
                        break;
                    default:
                        builder.BuildStore(value, slots[inst.Target].Slot);
                        break;
                }
                placeholderHost?.Destroy(session, builder);
            }
        }

        internal sealed class Set : LlvmEmitVisitor<Set, MirSetField>
        {
            protected override void VisitCore(MirSetField inst, ModuleBuilder.Session session)
            {
                var field = Resolve(session, inst.FieldSymbol);
                var slots = session.Slots;
                var placeholderHost = MaterializePlaceholderValueHost(session, session.Builder,
                    slots, inst.Object, inst.FieldSymbol, null);
                var pointer = placeholderHost != null
                    ? ByteGep(session.Builder, placeholderHost.HostSlot, field.Offset)
                    : FieldPointer(session, session.Builder, slots, inst.Object, field.Offset);
                StoreAt(session, session.Builder, slots, field, inst.FieldSymbol,
                    pointer, inst.Source);
                // 占位宿主写回：临时槽（含本次写入）重装箱回源占位槽——
                // VM 对占位接收者的字段写原地生效（调用方写回链依赖）
                placeholderHost?.Writeback(session, session.Builder, slots);
            }
        }

        // 占位槽作值类型宿主（.generic.* 槽装的内联值类型盒，如
        // b.item.v 读写链——此前胖值 payload 直当对象指针寻址，tag0
        // 内联盒必 AV）：拆箱到入口临时槽再按值类型宿主寻址；不符抛
        // CastException（MW9b-G 口径，同 UnboxToLocal）。非此形态返回
        // null（class 宿主/内联值类型槽走原路径）
        private static PlaceholderValueHost? MaterializePlaceholderValueHost(
            ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand objectOperand, string fieldSymbol, MirBlock? excTarget)
        {
            if (objectOperand is not MirLocalOperand objectLocal
                || !TypeLayout.IsGenericPlaceholder(slots[objectLocal.Name].Local.Type)
                // 开放构造值类型（WPair<.generic<…>>）槽本身是内联值
                // ABI（指针），不是胖盒——canonical 含占位但无需拆箱
                || session.IsInlineValueType(slots[objectLocal.Name].Local.Type, out _))
            {
                return null;
            }
            var hash = fieldSymbol.IndexOf('#');
            if (hash < 0)
            {
                return null;
            }
            var hostType = MirType.Of(fieldSymbol.Substring(0, hash));
            if (!session.IsInlineValueType(hostType, out var plan))
            {
                return null;
            }
            var temp = LlvmEmitEnvironment.BuildEntryAlloca(builder,
                LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)plan.Size), "field.ph");
            temp.Alignment = (uint)plan.Alignment;
            var fat = session.LoadLocal(builder, slots, objectOperand);
            BoxEmitter.UnboxToSlot(session, builder, fat, hostType, temp,
                slots[objectLocal.Name].Local.Type, excTarget);
            return new PlaceholderValueHost(objectLocal.Name, temp, hostType);
        }

        // 占位值类型宿主的临时槽载体：Get 读毕销毁；Set 写后重装箱
        // 写回源占位槽再销毁
        private sealed record PlaceholderValueHost(string SourceName, LLVMValueRef HostSlot,
            MirType HostType)
        {
            internal void Destroy(ModuleBuilder.Session session, LLVMBuilderRef builder) =>
                ArcEmitter.EmitDestroyRichValue(session, builder, HostSlot, HostType);

            internal void Writeback(ModuleBuilder.Session session, LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots)
            {
                // R3：open struct 宿主——盒可能保子类型身份，原地补丁
                // 写回（不重装箱切片；VM 占位槽原地生效同口径）
                if (BoxEmitter.IsOpenStructType(session, HostType))
                {
                    BoxEmitter.EmitSubtypeBoxWriteback(session, builder, slots, SourceName,
                        HostSlot, HostType);
                    Destroy(session, builder);
                    return;
                }
                ArcEmitter.MoveFatValue(session, builder, slots[SourceName].Slot,
                    BoxEmitter.BoxFromSlot(session, builder, HostSlot, HostType));
                Destroy(session, builder);
            }
        }

        // 宿主计划 + 字段计划（无布局计划的宿主 = 外部/特殊字段，随 MW 后续）
        internal static FieldPlan Resolve(ModuleBuilder.Session session, string fieldSymbol)
        {
            var hash = fieldSymbol.IndexOf('#');
            if (hash < 0)
            {
                throw new CompilerInternalException($"字段符号缺宿主段: {fieldSymbol}");
            }
            var plan = session.Layout?.Find(fieldSymbol.Substring(0, hash))
                ?? throw new MwNotSupportedException(
                    $"MW4 暂不支持的字段宿主（无布局计划）: {fieldSymbol}");
            foreach (var field in plan.Fields)
            {
                if (field.Symbol == fieldSymbol)
                {
                    return field;
                }
            }
            // 泛型模板计划复用字段表：成员 canonical 是声明形
            // Task<TReturn>#result@.nullable<.generic<…>>，调用点符号
            // 带构造类型段 Task<TReturn>#result@.nullable<core::i32>。
            // 偏移由模板决定，LLVM 类型仍走调用点符号（FieldMirType）
            var instName = FieldNameOf(fieldSymbol);
            if (instName != null)
            {
                foreach (var field in plan.Fields)
                {
                    if (FieldNameOf(field.Symbol) == instName)
                    {
                        return field;
                    }
                }
            }
            throw new CompilerInternalException($"字段不在宿主布局计划内: {fieldSymbol}");
        }

        private static string? FieldNameOf(string fieldSymbol)
        {
            var hash = fieldSymbol.LastIndexOf('#');
            var at = fieldSymbol.LastIndexOf('@');
            if (hash < 0 || at < 0 || at <= hash)
            {
                return null;
            }
            return fieldSymbol.Substring(hash + 1, at - hash - 1);
        }

        private static bool IsBorrowField(string fieldSymbol) =>
            fieldSymbol.Contains(".capture.this", System.StringComparison.Ordinal)
            || fieldSymbol.Contains(WrapperAbi.HostFieldInfix, System.StringComparison.Ordinal);

        // 宿主地址：值类型宿主 = alloca 槽地址（内联存储）；class 宿主 =
        // 胖引用 payload → 对象指针
        internal static LLVMValueRef HostBasePointer(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand objectOperand)
        {
            if (objectOperand is MirLocalOperand local
                && session.IsInlineValueType(slots[local.Name].Local.Type, out _))
            {
                return slots[local.Name].Slot;
            }
            var fat = session.LoadLocal(builder, slots, objectOperand);
            var payload = builder.BuildExtractValue(fat, 1, "field.obj");
            return builder.BuildIntToPtr(payload,
                LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "field.ptr");
        }

        internal static LLVMValueRef ByteGep(LLVMBuilderRef builder, LLVMValueRef basePointer,
            int offset, string name = "field.gep")
        {
            return builder.BuildGEP2(LLVMTypeRef.Int8, basePointer,
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)offset, false) },
                name);
        }

        // 宿主地址 + 字节偏移 GEP
        internal static LLVMValueRef FieldPointer(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand objectOperand, int offset)
        {
            return ByteGep(builder, HostBasePointer(session, builder, slots, objectOperand),
                offset);
        }

        // 已定位的字段指针上写入（set.field 与 set.wrapper.field 内层共用）
        internal static void StoreAt(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, FieldPlan field,
            string fieldSymbol, LLVMValueRef pointer, MirOperand source)
        {
            var fieldType = FieldMirType(fieldSymbol);
            if (field.EmbeddedPlan != null)
            {
                if (source is not MirLocalOperand sourceLocal)
                {
                    throw new CompilerInternalException(
                        $"未覆盖的 set.field 源形态: {source.GetType().Name}");
                }
                ArcEmitter.EmitCopyRichValue(session, builder, pointer,
                    slots[sourceLocal.Name].Slot, fieldType);
                return;
            }
            if (source is MirLocalOperand boxedLocal
                && BoxEmitter.NeedsBox(session, slots[boxedLocal.Name].Local.Type, fieldType))
            {
                ArcEmitter.MoveFatValue(session, builder, pointer,
                    BoxEmitter.BoxFromLocal(session, builder, slots, boxedLocal.Name));
                return;
            }
            var value = session.LoadLocal(builder, slots, source);
            if (IsBorrowField(field.Symbol))
            {
                builder.BuildStore(value, pointer);
                return;
            }
            switch (TypeLayout.ClassifySlot(session.Layout, fieldType))
            {
                case ManagedSlotKind.FatReference:
                    ArcEmitter.AssignFatValue(session, builder, pointer, value);
                    return;
                case ManagedSlotKind.String:
                    ArcEmitter.AssignStringValue(session, builder, pointer, value);
                    return;
            }
            builder.BuildStore(value, pointer);
        }

        // 字段符号的类型段（X#f@.i32 → .i32）→ LLVM 类型
        private static LLVMTypeRef FieldType(ModuleBuilder.Session session, string fieldSymbol)
        {
            return TypeLayout.MapType(session.Context, FieldMirType(fieldSymbol));
        }

        internal static MirType FieldMirType(string fieldSymbol)
        {
            // 末位 '@'：MW11a frame 字段符号内嵌 fn canonical（自带 '@'，
            // 如 $mw.frame.$work(n:.i32)@.i32#n@core::i32），首 '@' 会截错
            var at = fieldSymbol.LastIndexOf('@');
            // B-1：frame 槽类型本身也可以是 frame canonical（tainted
            // 调用点的 $mw.callee.N 槽，类型 $mw.frame.<callee fn
            // canonical> 同样内嵌 '@'）——末位 '@' 会截出 callee fn 的
            // 返回段。frame 槽名恒不含 '@'（fn 局部名与 state/$mw.task/
            // $mw.result），故 frame 字段符号取 '#' 后首个 '@' 起全尾
            if (fieldSymbol.StartsWith("$mw.frame.", System.StringComparison.Ordinal))
            {
                var hash = fieldSymbol.IndexOf('#');
                if (hash >= 0)
                {
                    var firstAt = fieldSymbol.IndexOf('@', hash + 1);
                    if (firstAt >= 0)
                    {
                        at = firstAt;
                    }
                }
            }
            if (at < 0)
            {
                throw new CompilerInternalException($"字段符号缺类型段: {fieldSymbol}");
            }
            return MirType.Of(fieldSymbol.Substring(at + 1));
        }
    }
}
