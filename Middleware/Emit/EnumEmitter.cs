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
    /// L1：new.wrapped.case（§14.4.2）在判别与 init 之间插入有参
    /// ..init.wrapper 直调（VM PushConstructorTail 的 wrapper→init
    /// 序同口径；receiver = 槽地址，与 EmitInitValueOnSlot 同形态）。
    /// </summary>
    internal static class EnumEmitter
    {
        internal sealed class NewCase : LlvmEmitVisitor<NewCase, MirNewCase>
        {
            protected override void VisitCore(MirNewCase inst, ModuleBuilder.Session session) =>
                EmitNewCase(session, session.Builder, session.Slots, inst);
        }

        internal sealed class IsCase : LlvmEmitVisitor<IsCase, MirIsCase>
        {
            protected override void VisitCore(MirIsCase inst, ModuleBuilder.Session session) =>
                EmitIsCase(session, session.Builder, session.Slots, inst);
        }

        private static void EmitNewCase(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewCase inst)
        {
            var slot = slots[inst.Target].Slot;
            // G1：构造 enum 的宿主 canonical（类级 typeid 合成代入用；
            // 非泛型 enum 合成面返回 null 零开销）
            var hostRef = slots[inst.Target].Local.Type.Canonical;
            // 隐藏判别字段（u32 @ 偏移 0）
            builder.BuildStore(
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, inst.Case.Discriminant, false),
                slot);
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            // new.wrapped.case（L1）：有参 ..init.wrapper 先于 enum init
            if (inst.InitWrapper != null
                && session.TryGetFunction(inst.InitWrapper.Canonical, out var wrapperFn))
            {
                // G1：泛型 enum 的 wrapper 形参可为占位（胖值槽）——实参
                // 加工与 init 同口径（ExpectedCallParams 已剔类级 typeid，
                // 下标 +1 跳过 .this）
                var wrapperExpected = CallEmitter.ExpectedCallParams(session.Symbols, wrapperFn.Mir);
                var wrapperArgs = new LLVMValueRef[inst.WrapperArgs.Count + 1];
                wrapperArgs[0] = slot;
                for (var i = 0; i < inst.WrapperArgs.Count; i++)
                {
                    var expectType = i + 1 < wrapperExpected.Count
                        ? wrapperExpected[i + 1].Type : null;
                    wrapperArgs[i + 1] = CallEmitter.CoerceArg(session, builder, slots,
                        inst.WrapperArgs[i], expectType, aliasThis: false, temps, boxed);
                }
                wrapperArgs = CallEmitter.MergeClassTypeIds(session, builder, slots,
                    wrapperFn.Mir, hostRef, wrapperArgs);
                builder.BuildCall2(wrapperFn.Type, wrapperFn.Value, wrapperArgs, "");
            }
            if (inst.Init == null)
            {
                // 无 init 声明 + 零实参的 enum：仅写判别（VM NewCase 同口径）
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                return;
            }
            var init = session.FunctionOf(inst.Init.Canonical);
            var initExpected = CallEmitter.ExpectedCallParams(session.Symbols, init.Mir);
            var initArgs = new LLVMValueRef[inst.Args.Count + 1];
            initArgs[0] = slot;
            for (var i = 0; i < inst.Args.Count; i++)
            {
                var expectType = i + 1 < initExpected.Count ? initExpected[i + 1].Type : null;
                initArgs[i + 1] = CallEmitter.CoerceArg(session, builder, slots,
                    inst.Args[i], expectType, aliasThis: false, temps, boxed);
            }
            initArgs = CallEmitter.MergeClassTypeIds(session, builder, slots,
                init.Mir, hostRef, initArgs);
            builder.BuildCall2(init.Type, init.Value, initArgs, "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
        }

        private static void EmitIsCase(ModuleBuilder.Session session, LLVMBuilderRef builder,
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
