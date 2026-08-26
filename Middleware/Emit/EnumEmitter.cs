using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// enum 构造与判别发射（Emit 分面，MW4 批 3）：new.case = 槽内偏移 0
    /// 写 u32 隐藏判别常量 + init 直调（.this 传槽地址）；type.is.case =
    /// 读判别 + icmp eq 判别常量（非子类型检查、不比较 payload）。
    /// enum 无零值：不写零初始化（DA/verifier 兜底）。
    /// </summary>
    internal static class EnumEmitter
    {
        internal static void EmitNewCase(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewCase inst)
        {
            var slot = slots[inst.Target].Slot;
            // 隐藏判别字段（u32 @ 偏移 0）
            builder.BuildStore(
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, inst.Case.Discriminant, false),
                slot);
            var init = session.FunctionOf(inst.Init.Canonical);
            var temps = new List<ArcEmitter.RichTemp>();
            var initArgs = new LLVMValueRef[inst.Args.Count + 1];
            initArgs[0] = slot;
            for (var i = 0; i < inst.Args.Count; i++)
            {
                initArgs[i + 1] = CallEmitter.MarshalArg(session, builder, slots,
                    inst.Args[i], aliasThis: false, temps);
            }
            builder.BuildCall2(init.Type, init.Value, initArgs, "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
        }

        internal static void EmitIsCase(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirIsCase inst)
        {
            if (inst.Value is not MirLocalOperand local
                || !session.IsInlineValueType(slots[local.Name].Local.Type, out _))
            {
                throw new MwNotSupportedException(
                    $"type.is.case 仅限 enum 值类型宿主: {inst.Case.Canonical}");
            }
            var discriminant = builder.BuildLoad2(LLVMTypeRef.Int32,
                slots[local.Name].Slot, "case.disc");
            var matched = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, discriminant,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, inst.Case.Discriminant, false),
                "case.eq");
            builder.BuildStore(matched, slots[inst.Target].Slot);
        }
    }
}
