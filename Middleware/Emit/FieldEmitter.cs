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
                if (TypeLayout.IsStringLengthField(inst.FieldSymbol))
                {
                    // String 无布局计划（native 16B 内联值 { i8* data, i64 len }，
                    // StringAbi）：宿主槽直载值后 extractvalue 第 1 成员即长度。
                    // 字面量常量槽（MirLoadResource 物化）与局部 ARC 槽两形态
                    // 宿主同为此形态；只读长度，不触碰 ARC
                    var host = session.LoadLocal(builder, slots, inst.Object);
                    var stringLength = builder.BuildExtractValue(host, 1, "str.len");
                    builder.BuildStore(stringLength, slots[inst.Target].Slot);
                    return;
                }
                if (TypeLayout.IsStringCharacterCountField(inst.FieldSymbol))
                {
                    var host = session.LoadLocal(builder, slots, inst.Object);
                    var hostPointer = session.StoreToTemp(builder, host);
                    var faceName = "rigi_string_character_count";
                    LLVMValueRef face;
                    LLVMTypeRef faceType;
                    if (session.TryGetFace(faceName, out var cached))
                    {
                        (face, faceType) = cached;
                    }
                    else
                    {
                        faceType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int64,
                            new[] { StringAbi.PointerType(session.Context) }, false);
                        face = session.Module.AddFunction(faceName, faceType);
                        session.AddFace(faceName, face, faceType);
                    }
                    var count = builder.BuildCall2(faceType, face,
                        new[] { hostPointer }, "str.characterCount");
                    builder.BuildStore(count, slots[inst.Target].Slot);
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
                // 所有托管字段读取都建立结果槽的拥有边，与 RcInjection 出口释放配平。
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
                if (inst.FieldSymbol.EndsWith("#failureNodeId@.i64", System.StringComparison.Ordinal))
                {
                    var owner = inst.FieldSymbol.Substring(0, inst.FieldSymbol.LastIndexOf('#'));
                    var plan = session.Layout?.Find(owner);
                    if (plan != null && ClassLayout.IsTask(plan.Symbol.Declaration.Symbol))
                    {
                        // 节点发布前绑定隐藏拥有槽；其释放统一交给 refMap，
                        // 使 Exception→Task 环能按普通对象图收集。
                        var hidden = FieldPointer(session, session.Builder, slots, inst.Object,
                            plan.Size - LayoutEngine.ReferenceSlotSize);
                        var node = session.Builder.BuildLoad2(LLVMTypeRef.Int64, pointer, "failure.bind.id");
                        var ptr = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                        var (bind, bindType) = CallEmitter.DeclareHelperFace(session,
                            "rigi_failure_bind", LLVMTypeRef.Void, new[] { LLVMTypeRef.Int64, ptr });
                        session.Builder.BuildCall2(bindType, bind, new[] { node, hidden }, "");
                    }
                }
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
            switch (TypeLayout.ClassifySlot(session.Layout, fieldType))
            {
                case ManagedSlotKind.FatReference:
                    ArcEmitter.AssignFatValue(session, builder, pointer, value);
                    return;
                case ManagedSlotKind.String:
                    ArcEmitter.AssignStringValue(session, builder, pointer, value);
                    return;
            }
            // typefix 防御：非标量槽（int/ptr）拒绝聚合值。胖值 {i64,i64}
            // 被误编组进 8B 字段时，opaque pointer 下 BuildStore 无点类型
            // 检查，会整 16B 写穿并静默截断第 0 字段（协程帧编组 ABI 错配
            // 曾借此存活为 "core::Type<X>" 缺陷）——尺寸/表示不符必须在
            // 编组侧响亮失败，不允许落 IR。
            var fieldLlType = FieldType(session, fieldSymbol);
            if (value.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind
                && (fieldLlType.Kind == LLVMTypeKind.LLVMIntegerTypeKind
                    || fieldLlType.Kind == LLVMTypeKind.LLVMPointerTypeKind))
            {
                throw new CompilerInternalException(
                    "set.field 编组尺寸不符：聚合值写入标量字段 "
                    + fieldSymbol + "（源 LLVM 类型 " + value.TypeOf.PrintToString()
                    + "，字段 LLVM 类型 " + fieldLlType.PrintToString() + "）");
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
