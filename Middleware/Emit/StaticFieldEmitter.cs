using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 静态字段发射（Emit 分面，MW4 批 4）：静态/全局字段 = 模块级 LLVM
    /// 全局槽（canonical 字段符号键，零初始化）——标量/String 按
    /// TypeLayout 映射、class 引用 = 16B 胖引用槽、值类型 = 内联布局尺寸
    /// 全局。初值由 ..globals.init 在 main 前执行（rigi_entry stub）；
    /// 静态写入的 ARC 注入属 MW7，本批不注入。静态域类型闸门（shared
    /// Object ∪ shared rich ValueType ∪ 非 rich ValueType）由 verifier
    /// §21.8 把关，本层直接信任。
    /// </summary>
    internal static class StaticFieldEmitter
    {
        // 全部本地静态/全局字段的槽发射（外部声明跳过——其定义在模块外，
        // 随 stdlib 实体化批处理）
        internal static void EmitAll(ModuleBuilder.Session session)
        {
            foreach (var member in session.Symbols.Members)
            {
                if (member.IsExternal || !IsStaticField(member))
                {
                    continue;
                }
                var fieldType = MirType.Of(FieldTypeOf(member.Canonical));
                LLVMTypeRef slotType;
                if (session.IsInlineValueType(fieldType, out var plan))
                {
                    // 值类型 = 内联布局尺寸全局（对齐 = 计划对齐）
                    slotType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)plan.Size);
                }
                else
                {
                    slotType = TypeLayout.MapType(session.Context, fieldType);
                }
                var global = session.Module.AddGlobal(slotType, "static." + member.Canonical);
                global.Linkage = LLVMLinkage.LLVMInternalLinkage;
                global.Initializer = LLVMValueRef.CreateConstNull(slotType);
                if (session.IsInlineValueType(fieldType, out plan))
                {
                    global.Alignment = (uint)plan.Alignment;
                }
                session.RegisterStaticField(member.Canonical, global);
            }
        }

        internal static void EmitGet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirGetStatic inst)
        {
            var global = session.StaticFieldFor(inst.FieldSymbol);
            var fieldType = MirType.Of(FieldTypeOf(inst.FieldSymbol));
            if (session.IsInlineValueType(fieldType, out var plan))
            {
                // 值类型静态槽：memcpy 读出（VM Copy 同口径）
                session.EmitMemCopy(builder, slots[inst.Target].Slot, global, plan.Size);
                return;
            }
            var value = builder.BuildLoad2(TypeLayout.MapType(session.Context, fieldType),
                global, "static.get");
            builder.BuildStore(value, slots[inst.Target].Slot);
        }

        internal static void EmitSet(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSetStatic inst)
        {
            var global = session.StaticFieldFor(inst.FieldSymbol);
            var fieldType = MirType.Of(FieldTypeOf(inst.FieldSymbol));
            if (session.IsInlineValueType(fieldType, out var plan))
            {
                if (inst.Source is not MirLocalOperand source)
                {
                    throw new CompilerInternalException(
                        $"未覆盖的 set.field.static 源形态: {inst.Source.GetType().Name}");
                }
                session.EmitMemCopy(builder, global, slots[source.Name].Slot, plan.Size);
                return;
            }
            var value = session.LoadLocal(builder, slots, inst.Source);
            builder.BuildStore(value, global);
        }

        private static bool IsStaticField(Symbols.MwMemberSymbol member) =>
            member.Declaration.Kind == BilMemberKind.StaticField
            || (member.Owner == null && member.Declaration.Kind == BilMemberKind.Field);

        // 字段符号的类型段（X#f@.i32 / #g@.i32 → .i32）
        private static string FieldTypeOf(string fieldSymbol)
        {
            var at = fieldSymbol.IndexOf('@');
            return at < 0
                ? throw new CompilerInternalException($"字段符号缺类型段: {fieldSymbol}")
                : fieldSymbol.Substring(at + 1);
        }
    }
}
