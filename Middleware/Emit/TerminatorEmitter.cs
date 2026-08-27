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
    /// selector 直对应 LLVM switch）/MirUnreachable。
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
                    else if (session.IsInlineValueType(fn.ReturnType, out _))
                    {
                        // 值类型返回：InitRichValue 到隐藏 out 首参（调用方
                        // 槽已零初始化）；归还 $mw.ret / 源槽的 +1
                        if (ret.Value is not MirLocalOperand returned)
                        {
                            throw new CompilerInternalException(
                                $"未覆盖的返回值形态: {ret.Value.GetType().Name}");
                        }
                        var src = slots[returned.Name].Slot;
                        ArcEmitter.EmitInitRichValue(session, builder,
                            llvmFunction.GetParam(0), src, fn.ReturnType);
                        ArcEmitter.EmitDestroyRichValue(session, builder, src, fn.ReturnType);
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
        // 对应；float/double/string 的非常量指令匹配待比较链降级）
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
            if (!isBool && !isChar && !isSigned && !isUnsigned)
            {
                throw new MwNotSupportedException(
                    $"MW3 switch 暂支持整数族/bool/char selector: {sw.Table.SelectorTypeRef}");
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
    }
}
