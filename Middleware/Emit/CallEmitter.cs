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
    /// 调用发射（Emit 分面）：MirCall 经 ImplBinder 绑定的三种形态——
    /// NativeDirectBinding（native 面声明 + String/胖引用 C 边界 out 首参
    /// 编组）、DirectCallBinding（模块内直接调用）、RuntimeFaceBinding
    /// （rigi_rt 运行时面调用，StringIn/StringOut 形状由 RuntimeFaces
    /// 回答）。面声明按需登记并经 Session 面缓存查重。
    /// </summary>
    internal static class CallEmitter
    {
        internal static void EmitCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCall call)
        {
            switch (ImplBinder.BindCall(call.Target))
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
                {
                    var signature = CanonicalSignature.Parse(call.Target.Canonical);
                    var cSymbol = RuntimeFaces.MapNativeSymbol(native.Library, native.Symbol);
                    var (fn, fnType) = DeclareNativeFace(session, cSymbol, signature);
                    var returnType = MirType.Of(signature.ReturnTypeRef);
                    var hasOut = TryNativeOutSlot(session, returnType, out var outType,
                        out var outName, out var outAlign);
                    var args = new LLVMValueRef[call.Args.Count + (hasOut ? 1 : 0)];
                    LLVMValueRef? outSlot = null;
                    if (hasOut)
                    {
                        var slot = builder.BuildAlloca(outType, outName);
                        if (outAlign != 0)
                        {
                            slot.Alignment = outAlign;
                        }
                        outSlot = slot;
                        args[0] = slot;
                    }
                    for (var i = 0; i < call.Args.Count; i++)
                    {
                        var argValue = session.LoadLocal(builder, slots, call.Args[i]);
                        var paramType = MirType.Of(signature.Parameters[i].TypeRef);
                        args[i + (hasOut ? 1 : 0)] =
                            MarshalNativeArg(session, builder, argValue, paramType);
                    }
                    var result = builder.BuildCall2(fnType, fn, args, "");
                    if (call.Result != null)
                    {
                        var value = outSlot != null
                            ? builder.BuildLoad2(outType, outSlot.Value, "native.result")
                            : result;
                        builder.BuildStore(value, slots[call.Result].Slot);
                    }
                    break;
                }
                case DirectCallBinding direct:
                {
                    EmitDirectCall(session, builder, slots,
                        session.FunctionOf(direct.Target.Canonical), call.Args, call.Result,
                        call.ExcTarget);
                    break;
                }
                case VirtualCallBinding virtualCall:
                {
                    EmitVirtualCall(session, builder, slots, call, virtualCall.Target);
                    break;
                }
                case InterfaceCallBinding interfaceCall:
                {
                    EmitInterfaceCall(session, builder, slots, call, interfaceCall.Target);
                    break;
                }
                case IndirectCallBinding:
                    throw new CompilerInternalException("IndirectCallBinding 须经 EmitIndirectInvoke");
                default:
                    throw new CompilerInternalException("调用的非预期绑定形态");
            }
        }

        // invoke.indirect：发射期 BindIndirectCall → EmitIndirectCall
        internal static void EmitIndirectInvoke(ModuleBuilder.Session session, LLVMBuilderRef builder,
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
                    EmitIndirectCall(session, builder, slots, inst, indirect.CallOperator);
                    break;
                default:
                    throw new CompilerInternalException("invoke.indirect 的非预期绑定形态");
            }
        }

        // super 直调（MIR 构建期已解析基类实现符号；不经 BindCall/虚派发）
        internal static void EmitSuperCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSuperCall call)
        {
            EmitDirectCall(session, builder, slots,
                session.FunctionOf(call.Target.Canonical), call.Args, call.Result, call.ExcTarget);
        }

        // new type(T)（class）：rigi_alloc → 隐藏 typeid → wrapper → init
        internal static void EmitNew(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewObject inst)
        {
            var init = session.FunctionOf(inst.Init.Canonical);
            var expected = ExpectedCallParams(init.Mir);
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            var userArgs = new LLVMValueRef[inst.Args.Count];
            for (var i = 0; i < inst.Args.Count; i++)
            {
                var expectType = i + 1 < expected.Count ? expected[i + 1].Type : null;
                userArgs[i] = CoerceArg(session, builder, slots, inst.Args[i],
                    expectType, aliasThis: false, temps, boxed);
            }
            var fat = EmitAllocAndInit(session, builder, slots, inst.Type.Canonical,
                inst.InitWrapper, inst.Init, userArgs);
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
            builder.BuildStore(fat, slots[inst.Target].Slot);
        }

        // 静态 new 与动态 ctor thunk 共用：alloc → 隐藏 typeid → wrapper → init
        internal static LLVMValueRef EmitAllocAndInit(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            string typeCanonical, MwMemberSymbol? initWrapper, MwMemberSymbol init,
            LLVMValueRef[] userArgs)
        {
            var sheet = session.TypeSheetFor(typeCanonical);
            var (allocFn, allocType) = DeclareHelperFace(session, "rigi_alloc",
                PointerType(), new[] { PointerType() });
            var obj = builder.BuildCall2(allocType, allocFn, new[] { sheet }, "new.obj");
            var fat = BuildFatReference(session, builder, sheet, obj);
            WriteHiddenTypeIds(session, builder, slots, obj, typeCanonical);
            if (initWrapper != null && session.TryGetFunction(initWrapper.Canonical, out var wrapper))
            {
                builder.BuildCall2(wrapper.Type, wrapper.Value, new[] { fat }, "");
            }
            var emitted = session.FunctionOf(init.Canonical);
            var initArgs = new LLVMValueRef[userArgs.Length + 1];
            initArgs[0] = fat;
            for (var i = 0; i < userArgs.Length; i++)
            {
                initArgs[i + 1] = userArgs[i];
            }
            if (emitted.Value.ParamsCount != (uint)initArgs.Length)
            {
                throw new CompilerInternalException(
                    $"ctor thunk 调 init 参数个数不符: {init.Canonical} " +
                    $"llvm={emitted.Value.ParamsCount} 传入={initArgs.Length} " +
                    $"sheet={typeCanonical}");
            }
            builder.BuildCall2(emitted.Type, emitted.Value, initArgs, "");
            return fat;
        }

        // ===== 直接/虚/接口调用 =====

        // 直接调用（含 init/super/struct 方法/全局 fn）。excTarget =
        // MW9a 异常边（Rigi 调用全族带 pending 检查；EmitAllocAndInit 等
        // 运行时面临时调用不经本路径）
        private static void EmitDirectCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            ModuleBuilder.Session.EmittedFunction callee,
            IReadOnlyList<MirOperand> args, string? result, MirBlock? excTarget)
        {
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            var callArgs = MarshalArgs(session, builder, slots, callee.Mir, args, result, temps,
                boxed);
            var callResult = builder.BuildCall2(callee.Type, callee.Value, callArgs, "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
            ExceptionEmitter.EmitPendingCheck(session, builder, excTarget);
            StoreCoercedResult(session, builder, slots, callee.Mir.ReturnType, callResult, result,
                excTarget);
        }

        // 标量/String/胖引用结果回存（值类型返回经 out 槽直写，无需回存）
        private static void StoreScalarResult(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirType returnType, LLVMValueRef callResult, string? result)
        {
            if (result != null && !session.IsInlineValueType(returnType, out _))
            {
                builder.BuildStore(callResult, slots[result].Slot);
            }
        }

        private static void StoreCoercedResult(ModuleBuilder.Session session,
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
                // 静态返回类型名（泛型占位 canonical）
                BoxEmitter.UnboxToLocal(session, builder, slots, callResult, actual, result,
                    returnType, excTarget);
                ArcEmitter.EmitReleaseFatValue(session, builder, callResult);
                return;
            }
            builder.BuildStore(callResult, slots[result].Slot);
        }

        // new type(V)（struct/enum）：目标局部的内联槽物化——整槽清零
        //（VM ZeroOf 语义）→ 可选 ..init.wrapper → init（.this 传槽地址，
        // 原地生效）；不装箱、不上堆
        internal static void EmitNewValue(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewValue inst)
        {
            var temps = new List<ArcEmitter.RichTemp>();
            var userArgs = new LLVMValueRef[inst.Args.Count];
            for (var i = 0; i < inst.Args.Count; i++)
            {
                userArgs[i] = MarshalArg(session, builder, slots, inst.Args[i],
                    aliasThis: false, temps);
            }
            EmitInitValueOnSlot(session, builder, slots[inst.Target].Slot,
                inst.Type.Canonical, inst.InitWrapper, inst.Init, userArgs);
            ArcEmitter.DestroyRichTemps(session, builder, temps);
        }

        // 静态 new 与动态 struct ctor thunk 共用：零初始化 → wrapper →
        // init（.this = 槽地址原地生效）。泛型 struct 无对象头隐藏槽，
        // 类级 typeid 按 GenericAbi 从 LLVM 约定剔除；闭合构造下 TypeSheet
        // 即构造身份，thunk 侧以 TypeSheet 常量作为构造目标（init 不接收
        // 类级 typeid，与 class「被调方自取」对偶）。
        internal static void EmitInitValueOnSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef slot, string typeCanonical,
            MwMemberSymbol? initWrapper, MwMemberSymbol init, LLVMValueRef[] userArgs)
        {
            var plan = session.Layout?.Find(typeCanonical)
                ?? throw new CompilerInternalException($"值类型无布局计划: {typeCanonical}");
            session.EmitMemSetZero(builder, slot, plan.Size);
            if (initWrapper != null && session.TryGetFunction(initWrapper.Canonical, out var wrapper))
            {
                builder.BuildCall2(wrapper.Type, wrapper.Value, new[] { slot }, "");
            }
            var emitted = session.FunctionOf(init.Canonical);
            var initArgs = new LLVMValueRef[userArgs.Length + 1];
            initArgs[0] = slot;
            for (var i = 0; i < userArgs.Length; i++)
            {
                initArgs[i + 1] = userArgs[i];
            }
            if (emitted.Value.ParamsCount != (uint)initArgs.Length)
            {
                throw new CompilerInternalException(
                    $"struct ctor 调 init 参数个数不符: {init.Canonical} " +
                    $"llvm={emitted.Value.ParamsCount} 传入={initArgs.Length} " +
                    $"sheet={typeCanonical}");
            }
            builder.BuildCall2(emitted.Type, emitted.Value, initArgs, "");
        }

        // 虚调用：接收者胖引用 payload → 对象头 [0] 实际 TypeSheet →
        // rigi_vtable_entry 取槽 fnptr 间接调用。fn 类型优先用静态目标的
        // fn 体；抽象静态目标（如 core.Exception.getMessage）无 fn 体，
        // 按 canonical 签名合成（与 interface 调用同法）
        private static void EmitVirtualCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirCall call, MwMemberSymbol target)
        {
            var slot = VirtualSlotOf(session, target);
            var entry = EmitVTableEntry(session, builder, "rigi_vtable_entry",
                new[] { ObjectPointer(session, builder, slots, call.Args[0]),
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)slot, false) });
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            if (session.TryGetFunction(target.Canonical, out var callee))
            {
                var callResult = builder.BuildCall2(callee.Type, entry,
                    MarshalArgs(session, builder, slots, callee.Mir, call.Args, call.Result, temps,
                        boxed), "");
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
                StoreCoercedResult(session, builder, slots, callee.Mir.ReturnType,
                    callResult, call.Result, call.ExcTarget);
                return;
            }
            var signature = CanonicalSignature.Parse(target.Canonical);
            var fnType = MethodFunctionTypeOf(session, target, signature);
            var abstractResult = builder.BuildCall2(fnType, entry,
                MarshalArgs(session, builder, slots, signature, call.Args, call.Result, temps,
                    boxed), "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
            ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
            StoreScalarResult(session, builder, slots, MirType.Of(signature.ReturnTypeRef),
                abstractResult, call.Result);
        }

        // interface 调用：rigi_imap_entry(obj, @typesheet.Iface, slot) 查
        // base offset 后取槽 fnptr（fn 类型由 canonical 签名合成——接口
        // 符号无 fn 体）
        private static void EmitInterfaceCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirCall call, MwMemberSymbol target)
        {
            var slot = InterfaceSlotOf(session, target);
            var ifaceSheet = InterfaceSheetOf(session, target, slots, call.Args[0]);
            var signature = CanonicalSignature.Parse(target.Canonical);
            var fnType = MethodFunctionTypeOf(session, target, signature);
            var entry = EmitVTableEntry(session, builder, "rigi_imap_entry",
                new[] { ObjectPointer(session, builder, slots, call.Args[0]), ifaceSheet,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)slot, false) });
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            var callResult = builder.BuildCall2(fnType, entry,
                MarshalArgs(session, builder, slots, signature, call.Args, call.Result, temps,
                    boxed), "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
            ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
            StoreScalarResult(session, builder, slots, MirType.Of(signature.ReturnTypeRef),
                callResult, call.Result);
        }

        // callable 协议：对 CallTarget 虚调用 $$call（实参列表不含 receiver，此处补上）
        private static void EmitIndirectCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirInvokeIndirect inst, MwMemberSymbol callOperator)
        {
            var owner = callOperator.Owner
                ?? throw new CompilerInternalException($"$$call 无宿主: {callOperator.Canonical}");
            if (owner.Declaration.Kind is not (BilTypeKind.Class or BilTypeKind.Interface))
            {
                throw new MwNotSupportedException(
                    $"invoke.indirect 宿主仅支持 class/interface: {owner.Canonical}");
            }
            var callArgs = new MirOperand[inst.Args.Count + 1];
            callArgs[0] = inst.CallTarget;
            for (var i = 0; i < inst.Args.Count; i++)
            {
                callArgs[i + 1] = inst.Args[i];
            }
            // fn 类型按调用点实参/返回合成（$$call 在 Func 上 abstract 无 fn 体；
            // 泛型 typeid 前缀已平铺在实参列表，与普通实参同法编组）
            var siteParams = new (string, string)[inst.Args.Count];
            for (var i = 0; i < inst.Args.Count; i++)
            {
                if (inst.Args[i] is not MirLocalOperand argLocal)
                {
                    throw new CompilerInternalException("invoke.indirect 实参非局部");
                }
                siteParams[i] = ("a" + i, slots[argLocal.Name].Local.Type.Canonical);
            }
            var siteReturn = inst.Result != null
                ? slots[inst.Result].Local.Type.Canonical
                : ".void";
            var signature = CanonicalSignature.Create(siteParams, siteReturn);
            var fnType = MethodFunctionTypeOf(session, callOperator, signature);
            var slot = VirtualSlotOf(session, callOperator);
            LLVMValueRef entry;
            if (owner.Declaration.Kind == BilTypeKind.Interface)
            {
                var ifaceSheet = InterfaceSheetOf(session, callOperator, slots, inst.CallTarget);
                entry = EmitVTableEntry(session, builder, "rigi_imap_entry",
                    new[] { ObjectPointer(session, builder, slots, inst.CallTarget), ifaceSheet,
                            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)slot, false) });
            }
            else
            {
                entry = EmitVTableEntry(session, builder, "rigi_vtable_entry",
                    new[] { ObjectPointer(session, builder, slots, inst.CallTarget),
                            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)slot, false) });
            }
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            var callResult = builder.BuildCall2(fnType, entry,
                MarshalArgs(session, builder, slots, signature, callArgs, inst.Result, temps,
                    boxed), "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
            ExceptionEmitter.EmitPendingCheck(session, builder, inst.ExcTarget);
            StoreScalarResult(session, builder, slots, MirType.Of(signature.ReturnTypeRef),
                callResult, inst.Result);
        }

        // ===== 调用辅助 =====

        // 调用实参编组（MW4 批 3 值类型 ABI）：值类型返回 → 隐藏 out 首参
        //（调用方供槽；noret 丢弃则开临时槽）；值类型参数 → memcpy 副本传
        // 指针（callee 改参数不影响调用方，VM Copy 同口径）；值类型 .this
        // 首参 → 直接传槽地址（别名语义，字段写原地生效）
        private static LLVMValueRef[] MarshalArgs(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirFunction calleeMir, IReadOnlyList<MirOperand> args, string? result,
            List<ArcEmitter.RichTemp> temps, List<ArcEmitter.FatTemp> boxed)
        {
            var hasOut = session.IsInlineValueType(calleeMir.ReturnType, out var outPlan);
            var thisAliases = calleeMir.Parameters.Count > 0
                && calleeMir.Parameters[0].Name == ".this";
            var expected = ExpectedCallParams(calleeMir);
            var values = new LLVMValueRef[args.Count + (hasOut ? 1 : 0)];
            if (hasOut)
            {
                values[0] = result != null
                    ? slots[result].Slot
                    : builder.BuildAlloca(
                        LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)outPlan.Size), "call.out");
                if (result == null && outPlan.RefMapCount > 0)
                {
                    temps.Add(new ArcEmitter.RichTemp(values[0],
                        ArcEmitter.SheetOf(session, calleeMir.ReturnType), outPlan.Size));
                }
            }
            for (var i = 0; i < args.Count; i++)
            {
                var expectType = i < expected.Count ? expected[i].Type : null;
                values[i + (hasOut ? 1 : 0)] = CoerceArg(session, builder, slots, args[i],
                    expectType, thisAliases && i == 0, temps, boxed);
            }
            return values;
        }

        // canonical 签名形态（interface 调用：无 fn 体，按签名编组；接收
        // 者恒 class 胖引用，无 .this 别名）
        private static LLVMValueRef[] MarshalArgs(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            CanonicalSignature signature, IReadOnlyList<MirOperand> args, string? result,
            List<ArcEmitter.RichTemp> temps, List<ArcEmitter.FatTemp> boxed)
        {
            var returnType = MirType.Of(signature.ReturnTypeRef);
            var hasOut = session.IsInlineValueType(returnType, out var outPlan);
            return MarshalArgsCore(session, builder, slots, args, result, hasOut, outPlan,
                thisAliases: false, temps, boxed, returnType);
        }

        private static LLVMValueRef[] MarshalArgsCore(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            IReadOnlyList<MirOperand> args, string? result,
            bool hasOut, Layout.TypeLayoutPlan outPlan, bool thisAliases,
            List<ArcEmitter.RichTemp> temps, List<ArcEmitter.FatTemp> boxed,
            MirType returnType)
        {
            var values = new LLVMValueRef[args.Count + (hasOut ? 1 : 0)];
            if (hasOut)
            {
                values[0] = result != null
                    ? slots[result].Slot
                    : builder.BuildAlloca(
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
                    null, thisAliases && i == 0, temps, boxed);
            }
            return values;
        }

        // 类级 typeid 已从 LLVM 调用约定剔除；实参列表与 BIL 调用点同形
        internal static List<MirLocal> ExpectedCallParams(MirFunction callee)
        {
            var list = new List<MirLocal>();
            foreach (var parameter in callee.Parameters)
            {
                if (!GenericAbi.IsClassLevelTypeId(callee.Symbol, parameter.Name))
                {
                    list.Add(parameter);
                }
            }
            return list;
        }

        private static LLVMValueRef CoerceArg(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand arg, MirType? expected, bool aliasThis,
            List<ArcEmitter.RichTemp> temps, List<ArcEmitter.FatTemp> boxed)
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
            }
            return MarshalArg(session, builder, slots, arg, aliasThis, temps);
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
            var temp = builder.BuildAlloca(
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

        // 类级 typeid 写入隐藏字段（闭合构造经 TypeSheetFor；外层泛型参数
        // 取当前 fn 的 .generic.* 局部——与 prologue 自取对偶）
        private static void WriteHiddenTypeIds(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef obj, string typeRef)
        {
            var plan = session.Layout?.Find(typeRef);
            if (plan == null)
            {
                return;
            }
            WriteHiddenTypeIds(session, builder, slots, obj, plan, typeRef);
        }

        private static void WriteHiddenTypeIds(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef obj, TypeLayoutPlan plan, string typeRef, int depth = 0)
        {
            if (depth > 32)
            {
                throw new CompilerInternalException("WriteHiddenTypeIds 基类链过深: " + typeRef);
            }
            var template = session.Symbols.FindTypeByRef(typeRef);
            if (plan.BasePlan != null && template?.Declaration.ExtendsType is { } baseRef)
            {
                var substBase = ConstructedTypeCollector.BuildSubstitution(typeRef,
                    template.Declaration);
                var substituted = MwTypeKey.Normalize(
                    ConstructedTypeCollector.Substitute(baseRef, substBase));
                WriteHiddenTypeIds(session, builder, slots, obj, plan.BasePlan, substituted,
                    depth + 1);
            }
            var substitution = template != null
                ? ConstructedTypeCollector.BuildSubstitution(typeRef, template.Declaration)
                : null;
            foreach (var (paramName, offset) in plan.HiddenTypeIdSlots)
            {
                LLVMValueRef sheetPtr;
                if (substitution != null
                    && substitution.TryGetValue(paramName, out var arg)
                    && GenericAbi.TryPlaceholderName(arg, out var placeholder)
                    && slots.ContainsKey(".generic." + placeholder))
                {
                    sheetPtr = session.LoadLocal(builder, slots,
                        new MirLocalOperand(".generic." + placeholder));
                }
                else if (substitution != null && substitution.TryGetValue(paramName, out arg))
                {
                    var sheet = session.TypeSheetFor(arg);
                    sheetPtr = LLVMValueRef.CreateConstBitCast(sheet,
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
                }
                else
                {
                    continue;
                }
                var bits = builder.BuildPtrToInt(sheetPtr, LLVMTypeRef.Int64, "tid.store");
                var gep = builder.BuildGEP2(LLVMTypeRef.Int8, obj,
                    new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)offset, false) },
                    "tid.wgep");
                builder.BuildStore(bits, gep);
            }
        }

        // 接收者胖引用 → 对象指针（payload 段）
        private static LLVMValueRef ObjectPointer(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand receiver)
        {
            var fat = session.LoadLocal(builder, slots, receiver);
            return builder.BuildIntToPtr(builder.BuildExtractValue(fat, 1, "recv.payload"),
                PointerType(), "recv.obj");
        }

        // 派发 helper 面调用（rigi_vtable_entry/rigi_imap_entry：返回
        // fnptr；参数类型按实参推导）。reporter 的 getMessage 虚派发同用
        internal static LLVMValueRef EmitVTableEntry(ModuleBuilder.Session session,
            LLVMBuilderRef builder, string faceSymbol, LLVMValueRef[] faceArgs)
        {
            var paramTypes = new LLVMTypeRef[faceArgs.Length];
            for (var i = 0; i < faceArgs.Length; i++)
            {
                paramTypes[i] = faceArgs[i].TypeOf;
            }
            var (fn, fnType) = DeclareHelperFace(session, faceSymbol, PointerType(), paramTypes);
            return builder.BuildCall2(fnType, fn, faceArgs, "dispatch.entry");
        }

        // class 对象胖引用：tag 编码归 BoxEmitter（消双实现漂移）
        internal static LLVMValueRef BuildFatReference(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeSheet, LLVMValueRef objectPointer)
        {
            return BoxEmitter.PackObject(session, builder, typeSheet, objectPointer);
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

        // 虚槽序号（Layout 计划的宿主 vtable 内索引；同偏移不变量保证
        // 基类槽位在派生类同位）。构造类型具化后计划必在；声明序回退已移除。
        // reporter 的 getMessage 槽位查询同用
        internal static int VirtualSlotOf(ModuleBuilder.Session session, MwMemberSymbol target)
        {
            var plan = session.Layout?.Find(GenericAbi.PlanKey(target.Owner!));
            if (plan != null)
            {
                for (var i = 0; i < plan.VTableSlots.Count; i++)
                {
                    if (plan.VTableSlots[i] == target.Canonical)
                    {
                        return i;
                    }
                }
                // 同槽签名键回退（泛型宿主模板符号与计划槽 canonical 可能不一致）
                var key = target.SignatureKey;
                for (var i = 0; i < plan.VTableSlots.Count; i++)
                {
                    if (session.Symbols.FindMember(plan.VTableSlots[i])?.SignatureKey == key)
                    {
                        return i;
                    }
                }
            }
            throw new CompilerInternalException($"虚槽缺失: {target.Canonical}");
        }

        // 接口 TypeSheet：构造接口用具化空壳（与 iMap 键同地址）；否则本类
        private static LLVMValueRef InterfaceSheetOf(ModuleBuilder.Session session,
            MwMemberSymbol target, Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand receiver)
        {
            var owner = target.Owner!;
            if (receiver is MirLocalOperand local)
            {
                var recv = MwTypeKey.Normalize(slots[local.Name].Local.Type.Canonical);
                if (session.TryGetTypeSheet(recv, out var sheet)
                    && session.Symbols.FindTypeByRef(recv) == owner)
                {
                    return sheet;
                }
                var plan = session.Layout?.Find(recv);
                if (plan != null)
                {
                    foreach (var (iface, _) in plan.IMap)
                    {
                        if ((iface == owner.Canonical || session.Symbols.FindTypeByRef(iface) == owner)
                            && session.TryGetTypeSheet(iface, out sheet))
                        {
                            return sheet;
                        }
                    }
                }
            }
            return session.TypeSheetFor(owner.Canonical);
        }

        // 接口内槽序（接口计划的 VTableSlots = 接口虚成员序）
        private static int InterfaceSlotOf(ModuleBuilder.Session session, MwMemberSymbol target)
        {
            var plan = session.Layout?.Find(target.Owner!.Canonical)
                ?? throw new CompilerInternalException($"接口无布局计划: {target.Canonical}");
            for (var i = 0; i < plan.VTableSlots.Count; i++)
            {
                if (plan.VTableSlots[i] == target.Canonical)
                {
                    return i;
                }
            }
            throw new CompilerInternalException($"接口槽缺失: {target.Canonical}");
        }

        // interface 调用的 fn 类型合成（接口符号无 fn 体）：值类型返回 →
        // 隐藏 out 首参 + void；.this 首参胖引用；值类型参数 → 指针
        private static LLVMTypeRef MethodFunctionTypeOf(ModuleBuilder.Session session,
            MwMemberSymbol target, CanonicalSignature signature)
        {
            var hasOut = session.IsInlineValueType(MirType.Of(signature.ReturnTypeRef), out _);
            var paramTypes = new LLVMTypeRef[signature.Parameters.Count + 1 + (hasOut ? 1 : 0)];
            var index = 0;
            if (hasOut)
            {
                paramTypes[index++] = PointerType();
            }
            paramTypes[index++] = TypeLayout.FatReferenceType(session.Context);
            for (var i = 0; i < signature.Parameters.Count; i++)
            {
                var paramType = MirType.Of(signature.Parameters[i].TypeRef);
                paramTypes[index++] = session.IsInlineValueType(paramType, out _)
                    ? PointerType()
                    : TypeLayout.MapType(session.Context, paramType);
            }
            return LLVMTypeRef.CreateFunction(
                hasOut ? LLVMTypeRef.Void
                    : TypeLayout.MapType(session.Context, MirType.Of(signature.ReturnTypeRef)),
                paramTypes, false);
        }

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

        // 值语义四面族 / String ARC 声明（裸 i64/指针，不走 StringIn/StringOut）
        internal static (LLVMValueRef Fn, LLVMTypeRef Type) DeclareArcFace(
            ModuleBuilder.Session session, string symbol)
        {
            return symbol switch
            {
                RuntimeFaces.RefAcquire => DeclareHelperFace(session, symbol, LLVMTypeRef.Int64,
                    new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 }),
                RuntimeFaces.RefRelease => DeclareHelperFace(session, symbol, LLVMTypeRef.Void,
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
                    outSlot = builder.BuildAlloca(StringAbi.ValueType(session.Context), "face.out");
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

        private static (LLVMValueRef Fn, LLVMTypeRef Type) DeclareNativeFace(ModuleBuilder.Session session,
            string cSymbol, CanonicalSignature signature)
        {
            if (session.TryGetFace(cSymbol, out var cached))
            {
                return cached;
            }
            var returnType = MirType.Of(signature.ReturnTypeRef);
            var hasOut = TryNativeOutSlot(session, returnType, out var outType, out _, out _);
            var paramTypes = new LLVMTypeRef[signature.Parameters.Count + (hasOut ? 1 : 0)];
            var index = 0;
            if (hasOut)
            {
                paramTypes[index++] = LLVMTypeRef.CreatePointer(outType, 0);
            }
            for (var i = 0; i < signature.Parameters.Count; i++)
            {
                var paramType = MirType.Of(signature.Parameters[i].TypeRef);
                paramTypes[index++] = MapNativeParamType(session, paramType);
            }
            var type = LLVMTypeRef.CreateFunction(
                hasOut ? LLVMTypeRef.Void : TypeLayout.MapType(session.Context, returnType),
                paramTypes, false);
            var fn = session.Module.AddFunction(cSymbol, type);
            session.AddFace(cSymbol, fn, type);
            return (fn, type);
        }

        // C 边界 out 首参：String → rigi_string*；用户引用 → 16B 对齐胖引用槽指针。
        // 形状描述复用 StringAbi / TypeLayout（唯一事实源）。
        private static bool TryNativeOutSlot(ModuleBuilder.Session session, MirType returnType,
            out LLVMTypeRef slotType, out string slotName, out uint alignment)
        {
            if (returnType.IsString)
            {
                slotType = StringAbi.ValueType(session.Context);
                slotName = "native.out";
                alignment = 0;
                return true;
            }
            if (returnType.IsVoid || IsNativeByValue(returnType) || TypeLayout.IsTypeId(returnType))
            {
                slotType = default;
                slotName = "";
                alignment = 0;
                return false;
            }
            slotType = TypeLayout.FatReferenceType(session.Context);
            slotName = "native.ref.out";
            alignment = (uint)TypeLayout.ReferenceSlotAlignment;
            return true;
        }

        private static bool IsNativeByValue(MirType type) => type.Key is
            "bool" or "char" or "i8" or "u8" or "i16" or "u16"
            or "i32" or "u32" or "i64" or "u64" or "float" or "double";

        // String → rigi_string*；Any → 16B 对齐胖引用槽指针（D6：C 边界
        // 16B 胖值一律指针）；bool → i8（C _Bool/int8 槽，调用点 zext i1）
        private static LLVMTypeRef MapNativeParamType(ModuleBuilder.Session session, MirType paramType)
        {
            if (paramType.IsString)
            {
                return StringAbi.PointerType(session.Context);
            }
            if (paramType.IsAny)
            {
                return LLVMTypeRef.CreatePointer(
                    TypeLayout.FatReferenceType(session.Context), 0);
            }
            if (paramType.Key == "bool")
            {
                return LLVMTypeRef.Int8;
            }
            return TypeLayout.MapType(session.Context, paramType);
        }

        private static LLVMValueRef MarshalNativeArg(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef argValue, MirType paramType)
        {
            if (paramType.IsString)
            {
                return session.StoreToTemp(builder, argValue);
            }
            if (paramType.IsAny)
            {
                var slot = builder.BuildAlloca(TypeLayout.FatReferenceType(session.Context),
                    "native.any");
                slot.Alignment = (uint)TypeLayout.ReferenceSlotAlignment;
                builder.BuildStore(argValue, slot);
                return slot;
            }
            if (paramType.Key == "bool")
            {
                return builder.BuildZExt(argValue, LLVMTypeRef.Int8, "native.bool");
            }
            return argValue;
        }
    }
}
