using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 字段访问发射（Emit 分面，MW4 批 2）：胖引用 extractvalue payload →
    /// 字节 GEP（Layout 计划偏移）→ load/store。标量/String 16B/胖引用槽
    /// 按 TypeLayout 映射；内联值类型字段随批 3（rich 值类型字段的写随
    /// MW7——memcpy 会漏 ARC）；ARC 注入属 MW7，本批不注入。
    /// </summary>
    internal static class FieldEmitter
    {
        internal static void EmitGet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirGetField inst)
        {
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
            var value = builder.BuildLoad2(FieldType(session, inst.FieldSymbol), pointer, "field.get");
            var targetType = slots[inst.Target].Local.Type;
            if (BoxEmitter.NeedsUnbox(session, fieldType, targetType))
            {
                BoxEmitter.UnboxToLocal(session, builder, slots, value, targetType, inst.Target);
                return;
            }
            switch (TypeLayout.ClassifySlot(session.Layout, fieldType))
            {
                case ManagedSlotKind.FatReference:
                    builder.BuildStore(ArcEmitter.ProduceFatValue(session, builder, value, "field.get"),
                        slots[inst.Target].Slot);
                    return;
                case ManagedSlotKind.String:
                    builder.BuildStore(ArcEmitter.ProduceStringValue(session, builder, value),
                        slots[inst.Target].Slot);
                    return;
            }
            builder.BuildStore(value, slots[inst.Target].Slot);
        }

        internal static void EmitSet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSetField inst)
        {
            var field = Resolve(session, inst.FieldSymbol);
            var pointer = FieldPointer(session, builder, slots, inst.Object, field.Offset);
            var fieldType = FieldMirType(inst.FieldSymbol);
            if (field.EmbeddedPlan != null)
            {
                if (inst.Source is not MirLocalOperand source)
                {
                    throw new CompilerInternalException(
                        $"未覆盖的 set.field 源形态: {inst.Source.GetType().Name}");
                }
                ArcEmitter.EmitCopyRichValue(session, builder, pointer,
                    slots[source.Name].Slot, fieldType);
                return;
            }
            if (inst.Source is MirLocalOperand sourceLocal
                && BoxEmitter.NeedsBox(session, slots[sourceLocal.Name].Local.Type, fieldType))
            {
                ArcEmitter.MoveFatValue(session, builder, pointer,
                    BoxEmitter.BoxFromLocal(session, builder, slots, sourceLocal.Name));
                return;
            }
            var value = session.LoadLocal(builder, slots, inst.Source);
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

        // 宿主计划 + 字段计划（无布局计划的宿主 = 外部/特殊字段，随 MW 后续）
        private static FieldPlan Resolve(ModuleBuilder.Session session, string fieldSymbol)
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
            throw new CompilerInternalException($"字段不在宿主布局计划内: {fieldSymbol}");
        }

        // 宿主地址 + 字节偏移 GEP：值类型宿主 = alloca 槽地址（内联存储）；
        // class 宿主 = 胖引用 payload → 对象指针
        private static LLVMValueRef FieldPointer(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand objectOperand, int offset)
        {
            LLVMValueRef basePointer;
            if (objectOperand is MirLocalOperand local
                && session.IsInlineValueType(slots[local.Name].Local.Type, out _))
            {
                basePointer = slots[local.Name].Slot;
            }
            else
            {
                var fat = session.LoadLocal(builder, slots, objectOperand);
                var payload = builder.BuildExtractValue(fat, 1, "field.obj");
                basePointer = builder.BuildIntToPtr(payload,
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "field.ptr");
            }
            return builder.BuildGEP2(LLVMTypeRef.Int8, basePointer,
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)offset, false) },
                "field.gep");
        }

        // 字段符号的类型段（X#f@.i32 → .i32）→ LLVM 类型
        private static LLVMTypeRef FieldType(ModuleBuilder.Session session, string fieldSymbol)
        {
            return TypeLayout.MapType(session.Context, FieldMirType(fieldSymbol));
        }

        private static MirType FieldMirType(string fieldSymbol)
        {
            var at = fieldSymbol.IndexOf('@');
            if (at < 0)
            {
                throw new CompilerInternalException($"字段符号缺类型段: {fieldSymbol}");
            }
            return MirType.Of(fieldSymbol.Substring(at + 1));
        }
    }
}
