using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 直接调用 + 分派 + 共享编组 / 运行时面声明。native 归
    /// <see cref="NativeCallEmitter"/>；虚/接口/indirect 归
    /// <see cref="VirtualCallEmitter"/>；new 归 <see cref="NewEmitter"/>。
    /// </summary>
    internal static class CallEmitter
    {
        internal sealed class Invoke : LlvmEmitVisitor<Invoke, MirCall>
        {
            protected override void VisitCore(MirCall call, ModuleBuilder.Session session)
            {
                EmitCall(session, session.Builder, session.Slots, call);
            }
        }

        internal sealed class Indirect : LlvmEmitVisitor<Indirect, MirInvokeIndirect>
        {
            protected override void VisitCore(MirInvokeIndirect inst, ModuleBuilder.Session session)
            {
                EmitIndirectInvoke(session, session.Builder, session.Slots, inst);
            }
        }

        internal sealed class Super : LlvmEmitVisitor<Super, MirSuperCall>
        {
            protected override void VisitCore(MirSuperCall call, ModuleBuilder.Session session)
            {
                EmitSuperCall(session, session.Builder, session.Slots, call);
            }
        }

        private static void EmitCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCall call)
        {
            ImplBinding binding;
            if (call.OperatorDispatch)
            {
                // 遗1 intrinsic 运算符直译：实际类型派发（class 虚/
                // interface iMap/值类型直调），不经 BindCall 的显式
                // invoke 直调特判
                binding = ImplBinder.BindOperatorCall(call.Target);
            }
            else
            {
                binding = ImplBinder.BindCall(call.Target);
            }
            switch (binding)
            {
                case NativeDirectBinding native
                    when native.Library == "rigi_rt" && native.Symbol == "alloc_array":
                    ArrayEmitter.EmitAllocArrayCall(session, builder, slots, call);
                    break;
                case NativeDirectBinding native
                    when native.Library == "rigi_rt" && native.Symbol == "span_alloc":
                    ArrayEmitter.EmitAllocSpanCall(session, builder, slots, call);
                    break;
                case NativeDirectBinding native:
                    NativeCallEmitter.Emit(session, builder, slots, call, native);
                    break;
                case DirectCallBinding direct:
                {
                    EmitDirectCall(session, builder, slots,
                        session.FunctionOf(direct.Target.Canonical), call.Args, call.Result,
                        call.ExcTarget, call.HostConstructedRef);
                    break;
                }
                case VirtualCallBinding virtualCall:
                    VirtualCallEmitter.EmitVirtual(session, builder, slots, call,
                        virtualCall.Target);
                    break;
                case InterfaceCallBinding interfaceCall:
                    VirtualCallEmitter.EmitInterface(session, builder, slots, call,
                        interfaceCall.Target);
                    break;
                case IndirectCallBinding:
                    throw new CompilerInternalException("IndirectCallBinding 须经 EmitIndirectInvoke");
                default:
                    throw new CompilerInternalException("调用的非预期绑定形态");
            }
        }

        // invoke.indirect：发射期 BindIndirectCall → EmitIndirectCall
        //（含 async $$call：虚派发 spawn stub，结果槽为 Task/Task<T>）
        private static void EmitIndirectInvoke(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirInvokeIndirect inst)
        {
            var argTypes = new List<string>(inst.Args.Count);
            foreach (var arg in inst.Args)
            {
                if (arg is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("invoke.indirect 实参非局部");
                }
                argTypes.Add(slots[local.Name].Local.Type.Canonical);
            }
            string? resultType = null;
            if (inst.Result != null)
            {
                resultType = slots[inst.Result].Local.Type.Canonical;
            }
            switch (ImplBinder.BindIndirectCall(session.Symbols, inst.CallTargetType.Canonical,
                argTypes, resultType, session.BilFunctions))
            {
                case IndirectCallBinding indirect:
                    VirtualCallEmitter.EmitIndirect(session, builder, slots, inst,
                        indirect.CallOperator);
                    break;
                default:
                    throw new CompilerInternalException("invoke.indirect 的非预期绑定形态");
            }
        }

        // super 直调（MIR 构建期已解析基类实现符号；不经 BindCall/虚派发）
        private static void EmitSuperCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSuperCall call)
        {
            EmitDirectCall(session, builder, slots,
                session.FunctionOf(call.Target.Canonical), call.Args, call.Result, call.ExcTarget);
        }

        // ===== 直接/虚/接口调用 =====

        // 直接调用（含 init/super/struct 方法/全局 fn）。excTarget =
        // MW9a 异常边（Rigi 调用全族带 pending 检查；NewEmitter.EmitAllocAndInit 等
        // 运行时面临时调用不经本路径）
        private static void EmitDirectCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            EmittedFunction callee,
            IReadOnlyList<MirOperand> args, string? result, MirBlock? excTarget,
            string? hostConstructedRef = null)
        {
            if (callee.Value == session.CurrentFunction)
            {
                EmitSelfRecursionStackGuard(session, builder, excTarget);
            }
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            var receiverWritebacks = new List<ReceiverWriteback>();
            var callArgs = MarshalArgs(session, builder, slots, callee.Mir, args, result, temps,
                boxed, hostConstructedRef, excTarget, receiverWritebacks);
            var callResult = builder.BuildCall2(callee.Type, callee.Value, callArgs, "");
            EmitPendingBeforeWriteback(session, builder, excTarget, temps, boxed);
            // 占位接收者拆箱临时槽的 .this 写回（别名语义：callee 对
            // this 的修改重装箱回源占位槽——VM 占位槽原地生效同口径；
            // callee 抛异常时经 pending 检查跳过写回，与调用方写回链
            // 语句不执行同口径）
            foreach (var writeback in receiverWritebacks)
            {
                if (writeback.SubtypeIdentity)
                {
                    // R3：open struct 子类型盒身份保留写回（原地补丁，
                    // 不重装箱切片）
                    BoxEmitter.EmitSubtypeBoxWriteback(session, builder, slots,
                        writeback.SourceName, writeback.TempSlot, writeback.ValueType);
                    continue;
                }
                ArcEmitter.MoveFatValue(session, builder, slots[writeback.SourceName].Slot,
                    BoxEmitter.BoxFromSlot(session, builder, writeback.TempSlot,
                        writeback.ValueType));
            }
            ArcEmitter.DestroyTemps(session, builder, temps, boxed);
            StoreCoercedResult(session, builder, slots, callee.Mir.ReturnType, callResult, result,
                excTarget);
        }

        private static void EmitSelfRecursionStackGuard(ModuleBuilder.Session session,
            LLVMBuilderRef builder, MirBlock? excTarget)
        {
            var (checkFn, checkType) = DeclareHelperFace(session,
                RuntimeFaces.StackHasRoom, LLVMTypeRef.Int32,
                System.Array.Empty<LLVMTypeRef>());
            var hasRoom = builder.BuildCall2(checkType, checkFn,
                System.Array.Empty<LLVMValueRef>(), "stack.room");
            var fail = session.CurrentFunction.AppendBasicBlock("stack.exhausted");
            var cont = session.CurrentFunction.AppendBasicBlock("stack.ok");
            builder.BuildCondBr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, hasRoom,
                    LLVMValueRef.CreateConstNull(LLVMTypeRef.Int32), "stack.room.ok"),
                cont, fail);
            builder.PositionAtEnd(fail);
            ExceptionEmitter.EmitThrowNewException(session, builder,
                "core::RuntimeException", "text",
                new[] { session.InternStringConstant("原生递归调用栈余量不足") },
                excTarget);
            builder.PositionAtEnd(cont);
        }

        private static void EmitPendingBeforeWriteback(ModuleBuilder.Session session,
            LLVMBuilderRef builder, MirBlock? excTarget, List<ArcEmitter.RichTemp> temps,
            List<ArcEmitter.FatTemp> boxed)
        {
            if (excTarget == null)
            {
                ExceptionEmitter.EmitPendingCheck(session, builder, null);
                return;
            }
            var blocks = session.CurrentBlocks
                ?? throw new CompilerInternalException("调用异常边缺少当前函数块映射");
            var (pendingFn, pendingType) = DeclareHelperFace(session,
                RuntimeFaces.ExcPending, PointerType(), System.Array.Empty<LLVMTypeRef>());
            var pending = builder.BuildCall2(pendingType, pendingFn,
                System.Array.Empty<LLVMValueRef>(), "call.pending");
            var fail = session.CurrentFunction.AppendBasicBlock("call.fail");
            var cont = session.CurrentFunction.AppendBasicBlock("call.cont");
            builder.BuildCondBr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pending,
                    LLVMValueRef.CreateConstNull(PointerType()), "call.has.pending"),
                fail, cont);
            builder.PositionAtEnd(fail);
            ArcEmitter.DestroyTemps(session, builder, temps, boxed);
            builder.BuildBr(blocks[excTarget.Id]);
            builder.PositionAtEnd(cont);
        }

        // 标量/String/胖引用结果回存（值类型返回经 out 槽直写，无需回存）
        internal static void StoreScalarResult(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirType returnType, LLVMValueRef callResult, string? result)
        {
            if (result != null && !session.IsInlineValueType(returnType, out _))
            {
                builder.BuildStore(callResult, slots[result].Slot);
            }
        }

        internal static void StoreCoercedResult(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirType returnType, LLVMValueRef callResult, string? result, MirBlock? excTarget)
        {
            if (result == null || session.IsInlineValueType(returnType, out _))
            {
                return;
            }
            var actual = slots[result].Local.Type;
            if (BoxEmitter.NeedsUnbox(session, returnType, actual))
            {
                // 拆箱不符守卫（MW9b-G 抛 CastException）：fromType 用
                // 静态返回类型名（泛型占位 canonical）。allowNullSource：
                // VM 侧返回值赋槽无 cast 检查（null 直传），String 闭合
                // 接收点对齐放行（for-in 零槽元素语义）
                BoxEmitter.UnboxToLocal(session, builder, slots, callResult, actual, result,
                    returnType, excTarget, allowNullSource: true);
                ArcEmitter.EmitReleaseFatValue(session, builder, callResult);
                return;
            }
            builder.BuildStore(callResult, slots[result].Slot);
        }

        // ===== 调用辅助 =====

        // 占位接收者拆箱调用的 this 写回登记（SourceName = 源占位槽，
        // TempSlot = 拆箱临时槽，ValueType = 具体值类型；SubtypeIdentity
        // = open struct 协变拆箱（R3）——写回原地补丁保子类型身份，
        // 不重装箱切片）
        internal sealed record ReceiverWriteback(string SourceName, LLVMValueRef TempSlot,
            MirType ValueType, bool SubtypeIdentity = false);

        // 调用实参编组（MW4 批 3 值类型 ABI）：值类型返回 → 隐藏 out 首参
        //（调用方供槽；noret 丢弃则开临时槽）；值类型参数 → memcpy 副本传
        // 指针（callee 改参数不影响调用方，VM Copy 同口径）；值类型 .this
        // 首参 → 直接传槽地址（别名语义，字段写原地生效）
        internal static LLVMValueRef[] MarshalArgs(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirFunction calleeMir, IReadOnlyList<MirOperand> args, string? result,
            List<ArcEmitter.RichTemp> temps, List<ArcEmitter.FatTemp> boxed,
            string? hostConstructedRef = null, MirBlock? excTarget = null,
            List<ReceiverWriteback>? receiverWritebacks = null)
        {
            var hasOut = session.IsInlineValueType(calleeMir.ReturnType, out var outPlan);
            var thisAliases = calleeMir.Parameters.Count > 0
                && calleeMir.Parameters[0].Name == ".this";
            var expected = ExpectedCallParams(session.Symbols, calleeMir);
            // G1：值类型泛型宿主的类级 typeid 保留在 LLVM 调用约定内——
            // MIR 实参表（BIL 调用点同形）不含它们，按 fn 参数位序合并插入
            var classIds = SynthesizeClassTypeIds(session, builder, slots, calleeMir,
                hostConstructedRef, args);
            var values = new List<LLVMValueRef>(
                args.Count + (classIds?.Length ?? 0) + (hasOut ? 1 : 0));
            if (hasOut)
            {
                var outSlot = result != null
                    ? slots[result].Slot
                    : LlvmEmitEnvironment.BuildEntryAlloca(builder,
                        LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)outPlan.Size), "call.out");
                if (result == null && outPlan.RefMapCount > 0)
                {
                    temps.Add(new ArcEmitter.RichTemp(outSlot,
                        ArcEmitter.SheetOf(session, calleeMir.ReturnType), outPlan.Size));
                }
                values.Add(outSlot);
            }
            if (classIds == null)
            {
                for (var i = 0; i < args.Count; i++)
                {
                    var expectType = i < expected.Count ? expected[i].Type : null;
                    values.Add(CoerceArg(session, builder, slots, args[i],
                        expectType, (thisAliases && i == 0)
                            || (i < expected.Count && expected[i].Name == Passes.WrapperSelfParameterPass.SelfParameter), temps, boxed, excTarget,
                        receiverWritebacks));
                }
                return values.ToArray();
            }
            var argIndex = 0;
            var idIndex = 0;
            foreach (var parameter in calleeMir.Parameters)
            {
                if (GenericAbi.IsClassLevelTypeId(session.Symbols, calleeMir.Symbol, parameter.Name))
                {
                    values.Add(classIds[idIndex++]);
                    continue;
                }
                var expectType = argIndex < expected.Count ? expected[argIndex].Type : null;
                // 值接口分流已确定闭合宿主；this 解箱必须核对该构造 sheet，
                // 不能退回方法声明的裸模板 sheet。
                if (thisAliases && argIndex == 0 && hostConstructedRef != null)
                    expectType = MirType.Of(hostConstructedRef);
                values.Add(CoerceArg(session, builder, slots, args[argIndex],
                    expectType, (thisAliases && argIndex == 0)
                        || parameter.Name == Passes.WrapperSelfParameterPass.SelfParameter, temps, boxed, excTarget,
                    receiverWritebacks));
                argIndex++;
            }
            return values.ToArray();
        }

        // canonical 签名形态（interface 调用：无 fn 体，按签名编组；接收
        // 者恒 class 胖引用，无 .this 别名）
        internal static LLVMValueRef[] MarshalArgs(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            CanonicalSignature signature, IReadOnlyList<MirOperand> args, string? result,
            List<ArcEmitter.RichTemp> temps, List<ArcEmitter.FatTemp> boxed,
            bool coerceParameters = false)
        {
            var returnType = MirType.Of(signature.ReturnTypeRef);
            var hasOut = session.IsInlineValueType(returnType, out var outPlan);
            return MarshalArgsCore(session, builder, slots, args, result, hasOut, outPlan,
                thisAliases: false, temps, boxed, returnType, coerceParameters ? signature : null);
        }

        private static LLVMValueRef[] MarshalArgsCore(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            IReadOnlyList<MirOperand> args, string? result,
            bool hasOut, Layout.TypeLayoutPlan outPlan, bool thisAliases,
            List<ArcEmitter.RichTemp> temps, List<ArcEmitter.FatTemp> boxed,
            MirType returnType, CanonicalSignature? signature = null)
        {
            var values = new LLVMValueRef[args.Count + (hasOut ? 1 : 0)];
            if (hasOut)
            {
                values[0] = result != null
                    ? slots[result].Slot
                    : LlvmEmitEnvironment.BuildEntryAlloca(builder, 
                        LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)outPlan.Size), "call.out");
                if (result == null && outPlan.RefMapCount > 0)
                {
                    temps.Add(new ArcEmitter.RichTemp(values[0],
                        ArcEmitter.SheetOf(session, returnType), outPlan.Size));
                }
            }
            for (var i = 0; i < args.Count; i++)
            {
                values[i + (hasOut ? 1 : 0)] = CoerceArg(session, builder, slots, args[i],
                    signature != null && i > 0 && i <= signature.Parameters.Count
                        ? MirType.Of(signature.Parameters[i - 1].TypeRef) : null,
                    thisAliases && i == 0, temps, boxed);
            }
            return values;
        }

        // 类级 typeid 已从 LLVM 调用约定剔除（class 宿主；值类型宿主除外——
        // 见 SynthesizeClassTypeIds）；实参列表与 BIL 调用点同形。symbols
        // 供嵌套类外层宿主链 GP 判定（review-20260910 #02）
        internal static List<MirLocal> ExpectedCallParams(
            MwSymbolTable symbols, MirFunction callee)
        {
            var list = new List<MirLocal>();
            foreach (var parameter in callee.Parameters)
            {
                if (!GenericAbi.IsClassLevelTypeId(symbols, callee.Symbol, parameter.Name))
                {
                    list.Add(parameter);
                }
            }
            return list;
        }

        // ===== G1：泛型值类型宿主的类级 typeid 直传 =====

        // 值类型无对象头隐藏槽，泛型值类型 fn 的类级 .generic.* 参数保留在
        // LLVM 调用约定内（ModuleBuilder 声明/prologue 配合），调用点按
        // fn 参数位序合成直传（与 class「被调方自取」对偶）。hostConstructedRef
        // = 接收者/构造目标的构造形态 canonical（闭合或含外层占位）；null 时
        // 依次回退：接收者静态类型（构造形）→ 当前 fn 同名 .generic.* 局部
        //（裸模板形态：this 内自调/手写 BIL）。静态方法无类级参数（§9.2.3）
        // 与非值类型宿主一律返回 null（调用侧零开销）。
        internal static LLVMValueRef[]? SynthesizeClassTypeIds(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirFunction calleeMir, string? hostConstructedRef,
            IReadOnlyList<MirOperand>? callArgs = null)
        {
            var owner = calleeMir.Symbol.Owner;
            if (!GenericAbi.IsValueTypeOwner(owner)
                || owner!.Declaration.GenericParameters.Count == 0
                || calleeMir.Parameters.Count == 0
                || calleeMir.Parameters[0].Name != ".this")
            {
                // 值类型静态成员无类级 typeid 实参（§9.2.3；调用约定剔除）
                return null;
            }
            var names = new List<string>();
            foreach (var parameter in calleeMir.Parameters)
            {
                if (GenericAbi.IsClassLevelTypeId(session.Symbols, calleeMir.Symbol, parameter.Name))
                {
                    names.Add(parameter.Name.Substring(".generic.".Length));
                }
            }
            if (names.Count == 0)
            {
                return null;
            }
            var hostRef = hostConstructedRef;
            if (hostRef == null && callArgs is { Count: > 0 }
                && callArgs[0] is MirLocalOperand receiver
                && slots.ContainsKey(receiver.Name))
            {
                var receiverType = slots[receiver.Name].Local.Type.Canonical;
                if (ConstructedTypeCollector.IsConstructed(receiverType)
                    && BilVerificationContext.StripTypeArguments(
                        MwTypeKey.Normalize(receiverType)) == owner.Canonical)
                {
                    hostRef = MwTypeKey.Normalize(receiverType);
                }
            }
            var substitution = hostRef != null
                ? ConstructedTypeCollector.BuildSubstitution(hostRef, owner.Declaration)
                : null;
            var values = new LLVMValueRef[names.Count];
            for (var i = 0; i < names.Count; i++)
            {
                values[i] = MaterializeClassTypeId(session, builder, slots,
                    names[i], substitution, calleeMir);
            }
            return values;
        }

        private static LLVMValueRef MaterializeClassTypeId(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            string paramName, Dictionary<string, string>? substitution, MirFunction calleeMir)
        {
            if (substitution != null && substitution.TryGetValue(paramName, out var arg))
            {
                if (GenericAbi.TryPlaceholderName(arg, out var placeholder))
                {
                    if (slots.ContainsKey(".generic." + placeholder))
                    {
                        return session.LoadLocal(builder, slots,
                            new MirLocalOperand(".generic." + placeholder));
                    }
                    throw new MwNotSupportedException(
                        $"泛型值类型方法的类级 typeid 实参静态不可解析: {calleeMir.Symbol.Canonical}"
                        + $" 的 .generic.{paramName}（外层占位 .generic.{placeholder} 无当前 fn 局部）");
                }
                if (arg.Contains(".generic<", System.StringComparison.Ordinal))
                {
                    throw new MwNotSupportedException(
                        $"泛型值类型方法的类级 typeid 实参为嵌套开放构造: {calleeMir.Symbol.Canonical}"
                        + $" 的 .generic.{paramName} = {arg}");
                }
                return LLVMValueRef.CreateConstBitCast(session.TypeSheetFor(arg), PointerType());
            }
            // 裸模板形态：取当前 fn 同名 .generic.* 局部（WriteHiddenTypeIds 先例）
            if (slots.ContainsKey(".generic." + paramName))
            {
                return session.LoadLocal(builder, slots,
                    new MirLocalOperand(".generic." + paramName));
            }
            throw new MwNotSupportedException(
                $"泛型值类型方法的类级 typeid 实参静态不可解析: {calleeMir.Symbol.Canonical}"
                + $" 的 .generic.{paramName}");
        }

        // MIR 实参（BIL 调用点同形，不含类级 typeid）与合成 typeid 按 fn
        // 参数位序合并（receiver/userArgs 已编组完成，仅做位序交织）
        internal static LLVMValueRef[] MergeClassTypeIds(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirFunction calleeMir, string? hostConstructedRef, LLVMValueRef[] marshalledArgs)
        {
            var ids = SynthesizeClassTypeIds(session, builder, slots, calleeMir,
                hostConstructedRef);
            if (ids == null)
            {
                return marshalledArgs;
            }
            var merged = new LLVMValueRef[marshalledArgs.Length + ids.Length];
            var argIndex = 0;
            var idIndex = 0;
            var outIndex = 0;
            foreach (var parameter in calleeMir.Parameters)
            {
                if (GenericAbi.IsClassLevelTypeId(session.Symbols, calleeMir.Symbol, parameter.Name))
                {
                    merged[outIndex++] = ids[idIndex++];
                }
                else
                {
                    merged[outIndex++] = marshalledArgs[argIndex++];
                }
            }
            return merged;
        }

        internal static LLVMValueRef CoerceArg(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand arg, MirType? expected, bool aliasThis,
            List<ArcEmitter.RichTemp> temps, List<ArcEmitter.FatTemp> boxed,
            MirBlock? excTarget = null, List<ReceiverWriteback>? receiverWritebacks = null)
        {
            if (arg is MirLocalOperand local && expected != null)
            {
                var actual = slots[local.Name].Local.Type;
                if (BoxEmitter.NeedsBox(session, actual, expected))
                {
                    var fat = BoxEmitter.BoxFromLocal(session, builder, slots, local.Name);
                    boxed.Add(new ArcEmitter.FatTemp(
                        builder.BuildExtractValue(fat, 0, "boxarg.tid"),
                        builder.BuildExtractValue(fat, 1, "boxarg.pl")));
                    return fat;
                }
                // 占位 → 具体值（B-2 遗留：值类型方法 + 泛型占位构造接收
                // 者——place 链物化的接收者槽保持占位类型，前端不打
                // cast；此前胖值位模式直传 callee 当内联值指针用，读
                // 出垃圾/AV）。拆箱到临时槽适配；.this 别名语义额外登
                // 记调用后重装箱写回（VM 占位槽原地生效同口径）；不符
                // 抛 CastException（MW9b-G 口径）
                if (BoxEmitter.NeedsUnbox(session, actual, expected))
                {
                    return UnboxArg(session, builder, slots, local, expected, actual,
                        aliasThis, temps, excTarget, receiverWritebacks);
                }
            }
            return MarshalArg(session, builder, slots, arg, aliasThis, temps);
        }

        // 占位实参拆箱编组：值类型 → 拆到入口临时槽传指针（含引用内容
        // 时登记 RichTemp 随调用收尾销毁）；标量/String → 拆到临时槽
        // 后装载传值
        private static LLVMValueRef UnboxArg(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirLocalOperand local, MirType expected, MirType actual, bool aliasThis,
            List<ArcEmitter.RichTemp> temps, MirBlock? excTarget,
            List<ReceiverWriteback>? receiverWritebacks)
        {
            var fat = session.LoadLocal(builder, slots, local);
            if (session.IsInlineValueType(expected, out var plan))
            {
                var temp = LlvmEmitEnvironment.BuildEntryAlloca(builder,
                    LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)plan.Size), "call.unbox");
                temp.Alignment = (uint)plan.Alignment;
                // R3：open struct 目标可含子类型盒——UnboxToSlot 中枢
                // 已转协变链守卫（VM 动态解析同口径）；此处只需为写回
                // 登记身份保留标记（原地补丁，不重装箱切片）
                var subtypeIdentity = BoxEmitter.IsOpenStructType(session, expected);
                BoxEmitter.UnboxToSlot(session, builder, fat, expected, temp, actual,
                    excTarget);
                if (plan.RefMapCount > 0)
                {
                    temps.Add(new ArcEmitter.RichTemp(temp, ArcEmitter.SheetOf(session, expected),
                        plan.Size));
                }
                if (aliasThis)
                {
                    receiverWritebacks?.Add(new ReceiverWriteback(local.Name, temp, expected,
                        subtypeIdentity));
                }
                return temp;
            }
            var scalarTemp = LlvmEmitEnvironment.BuildEntryAlloca(builder,
                TypeLayout.MapType(session.Context, expected), "call.unbox.s");
            BoxEmitter.UnboxToSlot(session, builder, fat, expected, scalarTemp, actual, excTarget);
            // String 解箱与含引用值类型一样获取所有权；调用临时槽必须配对销毁。
            if (expected.IsString)
                temps.Add(new ArcEmitter.RichTemp(scalarTemp,
                    ArcEmitter.SheetOf(session, expected), TypeLayout.ReferenceSlotSize));
            return builder.BuildLoad2(TypeLayout.MapType(session.Context, expected), scalarTemp,
                "call.unbox.ld");
        }

        // 单实参编组（值类型 → InitRichValue 副本传指针；aliasThis = 值类型
        // .this 别名传槽地址；其余装载求值）。init/new 的实参加工共用
        internal static LLVMValueRef MarshalArg(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand arg, bool aliasThis, List<ArcEmitter.RichTemp>? temps = null)
        {
            if (arg is not MirLocalOperand local
                || !session.IsInlineValueType(slots[local.Name].Local.Type, out var plan))
            {
                return session.LoadLocal(builder, slots, arg);
            }
            var slot = slots[local.Name].Slot;
            if (aliasThis)
            {
                return slot;
            }
            var temp = LlvmEmitEnvironment.BuildEntryAlloca(builder, 
                LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)plan.Size), "call.arg");
            temp.Alignment = (uint)plan.Alignment;
            var argType = slots[local.Name].Local.Type;
            ArcEmitter.EmitInitRichValue(session, builder, temp, slot, argType);
            if (plan.RefMapCount > 0)
            {
                temps?.Add(new ArcEmitter.RichTemp(temp, ArcEmitter.SheetOf(session, argType),
                    plan.Size));
            }
            return temp;
        }

        // class 对象胖引用：tag 编码归 BoxEmitter（消双实现漂移）
        internal static LLVMValueRef BuildFatReference(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeSheet, LLVMValueRef objectPointer)
        {
            return BoxEmitter.PackObject(session, builder, typeSheet, objectPointer);
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

        // 通用 helper 面声明（rigi_alloc/rigi_vtable_entry/rigi_imap_entry）
        internal static (LLVMValueRef Fn, LLVMTypeRef Type) DeclareHelperFace(
            ModuleBuilder.Session session, string symbol, LLVMTypeRef returnType,
            LLVMTypeRef[] paramTypes)
        {
            if (session.TryGetFace(symbol, out var cached))
            {
                return cached;
            }
            var type = LLVMTypeRef.CreateFunction(returnType, paramTypes, false);
            var fn = session.Module.AddFunction(symbol, type);
            session.AddFace(symbol, fn, type);
            return (fn, type);
        }

        // ownership region / 值语义四面族 / String ARC 声明（裸 i64/指针，
        // 不走 StringIn/StringOut）
        internal static (LLVMValueRef Fn, LLVMTypeRef Type) DeclareArcFace(
            ModuleBuilder.Session session, string symbol)
        {
            return symbol switch
            {
                RuntimeFaces.RegionEnter or RuntimeFaces.RegionExit =>
                    DeclareHelperFace(session, symbol, LLVMTypeRef.Void,
                        System.Array.Empty<LLVMTypeRef>()),
                RuntimeFaces.RefAcquire => DeclareHelperFace(session, symbol, LLVMTypeRef.Int64,
                    new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 }),
                RuntimeFaces.RefRelease => DeclareHelperFace(session, symbol, LLVMTypeRef.Void,
                    new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 }),
                RuntimeFaces.RefCheck => DeclareHelperFace(session, symbol, LLVMTypeRef.Void,
                    new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 }),
                RuntimeFaces.ValueAcquire or RuntimeFaces.ValueRelease =>
                    DeclareHelperFace(session, symbol, LLVMTypeRef.Void,
                        new[] { PointerType(), PointerType() }),
                RuntimeFaces.StringAcquire or RuntimeFaces.StringRelease =>
                    DeclareHelperFace(session, symbol, LLVMTypeRef.Void,
                        new[] { PointerType() }),
                _ => throw new MwNotSupportedException($"未知 ARC 面: {symbol}"),
            };
        }

        // 运行时面调用：StringIn 取下一个输入值存临时槽传指针，
        // StringOut 开出参槽、调用后读回为结果值（MW1 面恒 void 返回）
        internal static LLVMValueRef EmitFaceCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            string faceSymbol, IReadOnlyList<LLVMValueRef> inputs)
        {
            var shape = RuntimeFaces.ShapeOf(faceSymbol);
            var (fn, fnType) = DeclareFace(session, faceSymbol, shape);
            var args = new List<LLVMValueRef>(shape.Count);
            var inputIndex = 0;
            LLVMValueRef? outSlot = null;
            foreach (var param in shape)
            {
                if (param == RuntimeFaceParam.StringOut)
                {
                    outSlot = LlvmEmitEnvironment.BuildEntryAlloca(builder, StringAbi.ValueType(session.Context), "face.out");
                    args.Add(outSlot.Value);
                }
                else
                {
                    args.Add(session.StoreToTemp(builder, inputs[inputIndex++]));
                }
            }
            builder.BuildCall2(fnType, fn, args.ToArray(), "");
            if (outSlot == null)
            {
                throw new CompilerInternalException($"运行时面 {faceSymbol} 无出参却被求值");
            }
            return builder.BuildLoad2(StringAbi.ValueType(session.Context), outSlot.Value, "face.result");
        }

        // ===== 声明登记（经 Session 面缓存查重） =====

        // void(void) 简单面声明（abort 检查面；noreturn 语义由调用方在
        // 调用后补 unreachable 表达）
        internal static (LLVMValueRef Fn, LLVMTypeRef Type) DeclareVoidFace(
            ModuleBuilder.Session session, string faceSymbol)
        {
            if (session.TryGetFace(faceSymbol, out var cached))
            {
                return cached;
            }
            var type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                System.Array.Empty<LLVMTypeRef>(), false);
            var fn = session.Module.AddFunction(faceSymbol, type);
            session.AddFace(faceSymbol, fn, type);
            return (fn, type);
        }

        // string 比较面调用（RuntimeFaces 唯一带返回值的面）：两输入经
        // rigi_string* 临时槽传入，i32 三态结果直接传出，次序判定归
        // ScalarEmitter
        internal static LLVMValueRef EmitStringCompareCall(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef leftValue, LLVMValueRef rightValue)
        {
            var (fn, fnType) = DeclareStringCompareFace(session);
            var args = new[]
            {
                session.StoreToTemp(builder, leftValue),
                session.StoreToTemp(builder, rightValue),
            };
            return builder.BuildCall2(fnType, fn, args, "string.compare");
        }

        // i32 rigi_string_compare(const rigi_string*, const rigi_string*)
        private static (LLVMValueRef Fn, LLVMTypeRef Type) DeclareStringCompareFace(
            ModuleBuilder.Session session)
        {
            if (session.TryGetFace(RuntimeFaces.StringCompare, out var cached))
            {
                return cached;
            }
            var stringPointer = StringAbi.PointerType(session.Context);
            var type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32,
                new[] { stringPointer, stringPointer }, false);
            var fn = session.Module.AddFunction(RuntimeFaces.StringCompare, type);
            session.AddFace(RuntimeFaces.StringCompare, fn, type);
            return (fn, type);
        }

        private static (LLVMValueRef Fn, LLVMTypeRef Type) DeclareFace(ModuleBuilder.Session session,
            string faceSymbol, IReadOnlyList<RuntimeFaceParam> shape)
        {
            if (session.TryGetFace(faceSymbol, out var cached))
            {
                return cached;
            }
            var paramTypes = new LLVMTypeRef[shape.Count];
            for (var i = 0; i < shape.Count; i++)
            {
                paramTypes[i] = StringAbi.PointerType(session.Context);
            }
            var type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, paramTypes, false);
            var fn = session.Module.AddFunction(faceSymbol, type);
            session.AddFace(faceSymbol, fn, type);
            return (fn, type);
        }
    }
}
