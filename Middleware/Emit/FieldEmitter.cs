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
                var pointer = FieldPointer(session, builder, slots, inst.Object, field.Offset);
                var fieldType = FieldMirType(inst.FieldSymbol);
                if (field.EmbeddedPlan != null)
                {
                    ArcEmitter.EmitInitRichValue(session, builder, slots[inst.Target].Slot,
                        pointer, fieldType);
                    return;
                }
                var value = builder.BuildLoad2(FieldType(session, inst.FieldSymbol), pointer,
                    "field.get");
                var targetType = slots[inst.Target].Local.Type;
                if (BoxEmitter.NeedsUnbox(session, fieldType, targetType))
                {
                    BoxEmitter.UnboxToLocal(session, builder, slots, value, targetType,
                        inst.Target, fieldType, inst.ExcTarget);
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
                        return;
                    case ManagedSlotKind.String:
                        builder.BuildStore(ArcEmitter.ProduceStringValue(session, builder, value),
                            slots[inst.Target].Slot);
                        return;
                }
                builder.BuildStore(value, slots[inst.Target].Slot);
            }
        }

        internal sealed class Set : LlvmEmitVisitor<Set, MirSetField>
        {
            protected override void VisitCore(MirSetField inst, ModuleBuilder.Session session)
            {
                var field = Resolve(session, inst.FieldSymbol);
                var pointer = FieldPointer(session, session.Builder, session.Slots, inst.Object,
                    field.Offset);
                StoreAt(session, session.Builder, session.Slots, field, inst.FieldSymbol,
                    pointer, inst.Source);
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
            if (at < 0)
            {
                throw new CompilerInternalException($"字段符号缺类型段: {fieldSymbol}");
            }
            return MirType.Of(fieldSymbol.Substring(at + 1));
        }
    }
}
