using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 块终结符发射（Emit 分面）：MirRet（含 entrypoint 的 i32 包装）/
    /// MirBranch/MirCondBranch/MirSwitch（常量表匹配，整数族/bool/char
    /// selector 直对应 LLVM switch；f32/f64/String selector 降级为按表序
    /// 相等比较链）/MirUnreachable。
    /// </summary>
    internal static class TerminatorEmitter
    {
        internal static void Emit(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            Dictionary<string, LLVMBasicBlockRef> blockRefs,
            LLVMValueRef llvmFunction, MirFunction fn, MirTerminator terminator)
        {
            switch (terminator)
            {
                case MirRet ret:
                    if (ret.Value == null)
                    {
                        builder.BuildRetVoid();
                    }
                    else if (session.IsInlineValueType(fn.ReturnType, out var retPlan))
                    {
                        // 值类型返回 = 交付即移动契约：RcInjectionPass 三段
                        // 式（release/copy/acquire）已让 $mw.ret 独立持有返
                        // 回值（借用返回 fn 的 $mw.ret 零义务，3b-δ1 C4），
                        // 这里对隐藏 out 首参（调用方供槽）纯 memcpy 交付，
                        // out 接管 $mw.ret 的 +1（借用形态接管零义务裸值），
                        // 交付后立即 ret，$mw.ret 不再任何 release。
                        // 禁止改回「acquire + memcpy + release」三段交付：
                        // tag1 堆盒槽（Nullable<T> 装箱，NullableEmitter
                        // WrapFromSlot）的 rigi_ref_acquire 有深拷回写副作
                        // 用（rigi_rt/arc.c rigi_value_walk 回写槽 payload），
                        // memcpy 会拷出「回写后的新块」而紧随的 release 又
                        // 将该块 free——out 拿到悬垂块，返回含 Nullable 字
                        // 段的 rich struct 必现 UAF（rich_return_nullable
                        // 语料定点）。tag1 是唯一有回写副作用的引用槽种类，
                        // 交付即移动对 STRING/tag2/tag0 槽同样正确（tag2/
                        // STRING 的 acquire+release 本相互抵消）。
                        if (ret.Value is not MirLocalOperand returned)
                        {
                            throw new CompilerInternalException(
                                $"未覆盖的返回值形态: {ret.Value.GetType().Name}");
                        }
                        var src = slots[returned.Name].Slot;
                        session.EmitMemCopy(builder, llvmFunction.GetParam(0), src,
                            retPlan.Size);
                        builder.BuildRetVoid();
                    }
                    else
                    {
                        builder.BuildRet(session.LoadLocal(builder, slots, ret.Value));
                    }
                    break;
                case MirBranch branch:
                    builder.BuildBr(blockRefs[branch.Target]);
                    break;
                case MirCondBranch condBranch:
                    builder.BuildCondBr(session.LoadLocal(builder, slots, condBranch.Condition),
                        blockRefs[condBranch.ThenTarget], blockRefs[condBranch.ElseTarget]);
                    break;
                case MirSwitch sw:
                    EmitSwitch(session, builder, slots, blockRefs, sw);
                    break;
                case MirUnreachable:
                    builder.BuildUnreachable();
                    break;
                case MirRetThrow:
                    // 异常出口（MW9a）：pending 已在 TLS，直接返回调用方；
                    // sret/void → ret void，值返回 → ret undef（值无定义）
                    if (fn.ReturnType.IsVoid || session.IsInlineValueType(fn.ReturnType, out _))
                    {
                        builder.BuildRetVoid();
                    }
                    else
                    {
                        builder.BuildRet(LlvmBitcode.UndefOf(
                            TypeLayout.MapType(session.Context, fn.ReturnType)));
                    }
                    break;
                default:
                    throw new CompilerInternalException($"未覆盖的 MIR 终结符: {terminator.GetType().Name}");
            }
        }

        // switch → LLVM switch 指令（整数族/bool/char selector 直接
        // 对应；f32/f64/String selector 走比较链降级 EmitSwitchCompareChain）
        private static void EmitSwitch(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            Dictionary<string, LLVMBasicBlockRef> blockRefs, MirSwitch sw)
        {
            var selectorType = MirType.Of(sw.Table.SelectorTypeRef);
            var key = selectorType.Key;
            var isBool = key == "bool";
            var isChar = key == "char";
            var isSigned = key is "i8" or "i16" or "i32" or "i64";
            var isUnsigned = key is "u8" or "u16" or "u32" or "u64";
            if (key is "float" or "double" or "String")
            {
                EmitSwitchCompareChain(session, builder, slots, blockRefs, sw, key == "String");
                return;
            }
            if (!isBool && !isChar && !isSigned && !isUnsigned)
            {
                throw new MwNotSupportedException(
                    $"MW3 switch 暂支持整数族/bool/char/f32/f64/String selector: {sw.Table.SelectorTypeRef}");
            }
            var llvmType = TypeLayout.MapType(session.Context, selectorType);
            var selector = session.LoadLocal(builder, slots, sw.Selector);
            var switchInst = builder.BuildSwitch(selector, blockRefs[sw.DefaultTarget],
                (uint)sw.ItemTargets.Count);
            for (var i = 0; i < sw.ItemTargets.Count; i++)
            {
                var text = sw.Table.Elements[i];
                LLVMValueRef caseValue;
                if (isBool)
                {
                    caseValue = LLVMValueRef.CreateConstInt(llvmType, text == "true" ? 1u : 0u, false);
                }
                else if (isChar)
                {
                    caseValue = LLVMValueRef.CreateConstInt(llvmType, BilScalarLiteral.DecodeChar(text), false);
                }
                else if (isSigned)
                {
                    caseValue = LLVMValueRef.CreateConstInt(llvmType,
                        unchecked((ulong)BilScalarLiteral.ParseSigned(text)), true);
                }
                else
                {
                    caseValue = LLVMValueRef.CreateConstInt(llvmType,
                        BilScalarLiteral.ParseUnsigned(text), false);
                }
                switchInst.AddCase(caseValue, blockRefs[sw.ItemTargets[i]]);
            }
        }

        // f32/f64/String selector 的比较链降级（LLVM switch 仅支持整数）：
        // 按表序逐元素 cmp.eq，首个命中项进对应 item、全不命中落 default
        // ——与 VM §16.6 ValuesEqual 逐项同口径。float 走 OEQ：NaN 与任何
        // 值不等（VM C# == 同口径）、-0.0 与 +0.0 相等；String 复用
        // rigi_string_compare 三态 == 0 的按值相等（ScalarEmitter 同面）
        private static void EmitSwitchCompareChain(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            Dictionary<string, LLVMBasicBlockRef> blockRefs, MirSwitch sw, bool isString)
        {
            var selector = session.LoadLocal(builder, slots, sw.Selector);
            var defaultTarget = blockRefs[sw.DefaultTarget];
            if (sw.ItemTargets.Count == 0)
            {
                // 空表：无项可比，直落 default（VM 同口径）
                builder.BuildBr(defaultTarget);
                return;
            }
            for (var i = 0; i < sw.ItemTargets.Count; i++)
            {
                var text = sw.Table.Elements[i];
                LLVMValueRef condition;
                if (isString)
                {
                    var caseValue = StringAbi.BuildConstant(session.Module,
                        BilScalarLiteral.DecodeString(text), sw.Table.Name + "." + i);
                    var cmp = CallEmitter.EmitStringCompareCall(session, builder, selector, caseValue);
                    condition = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cmp,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false), "sw.seq");
                }
                else
                {
                    var caseValue = LLVMValueRef.CreateConstRealOfStringAndSize(
                        selector.TypeOf, text, (uint)text.Length);
                    condition = builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, selector,
                        caseValue, "sw.feq");
                }
                LLVMBasicBlockRef next = default;
                if (i + 1 < sw.ItemTargets.Count)
                {
                    next = session.CurrentFunction.AppendBasicBlock("sw.chain." + i);
                }
                builder.BuildCondBr(condition, blockRefs[sw.ItemTargets[i]],
                    i + 1 < sw.ItemTargets.Count ? next : defaultTarget);
                if (i + 1 < sw.ItemTargets.Count)
                {
                    builder.PositionAtEnd(next);
                }
            }
        }
    }
}
