using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 虚调用 / 接口调用 / invoke.indirect：vtable 槽、iMap 段、$$call。
    /// 槽序来自 Layout 计划；本类只填 fnptr 与调用。
    /// </summary>
    internal static class VirtualCallEmitter
    {
        // 虚调用：接收者胖引用 payload → 对象头 [0] 实际 TypeSheet →
        // rigi_vtable_entry 取槽 fnptr 间接调用。fn 类型优先用静态目标的
        // fn 体；抽象静态目标（如 core.Exception.getMessage）无 fn 体，
        // 按 canonical 签名合成（与 interface 调用同法）
        internal static void EmitVirtual(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirCall call, MwMemberSymbol target)
        {
            var slot = VirtualSlotOf(session, target);
            var entry = EmitVTableEntry(session, builder, "rigi_vtable_entry",
                new[] { ObjectPointer(session, builder, slots, call.Args[0]),
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)slot, false) });
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            var cellSlot = FatValueSlotAbi.Applies(session, target);
            if (!cellSlot && session.TryGetFunction(target.Canonical, out var callee))
            {
                var callResult = builder.BuildCall2(callee.Type, entry,
                    CallEmitter.MarshalArgs(session, builder, slots, callee.Mir, call.Args,
                        call.Result, temps, boxed), "");
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
                CallEmitter.StoreCoercedResult(session, builder, slots, callee.Mir.ReturnType,
                    callResult, call.Result, call.ExcTarget);
                return;
            }
            var signature = cellSlot ? FatValueSlotAbi.Signature(target)
                : SubstituteForReceiver(session, slots, call.Args[0], target,
                    CanonicalSignature.Parse(target.Canonical));
            var fnType = MethodFunctionTypeOf(session, target, signature);
            var abstractResult = builder.BuildCall2(fnType, entry,
                CallEmitter.MarshalArgs(session, builder, slots, signature, call.Args, call.Result,
                    temps, boxed, coerceParameters: cellSlot), "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
            ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
            CallEmitter.StoreCoercedResult(session, builder, slots,
                MirType.Of(signature.ReturnTypeRef), abstractResult, call.Result, call.ExcTarget);
        }

        // interface 调用：rigi_imap_entry(obj, @typesheet.Iface, slot) 查
        // base offset 后取槽 fnptr。接口方法带默认体时 fn 存在——泛型
        // 方法的 §7.2 方法级 typeid 隐藏参数只登记在 fn .args（成员符号
        // 参数段不含），合成签名会漏掉它们致 thunk 调用约定错配，故优先
        // 取 fn 体类型编组（与 EmitVirtual 同口径）；无 fn 体（抽象）时
        // 按 canonical 签名合成
        internal static void EmitInterface(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirCall call, MwMemberSymbol target)
        {
            var values = session.Layout!.ValueInterfaceImplementations(target.Owner!.Canonical, target.SignatureKey);
            if (values.Count == 0)
            {
                EmitObjectInterface(session, builder, slots, call, target);
                return;
            }
            // 源码 struct 仍不可 implements；编译器合成接口可承载值能力。
            // 值胖槽的 payload 是值块，不是对象头，不能交给 object iMap helper。
            // 按已具化 TypeSheet 分流后复用普通调用的 this 解箱/泛型 ABI。
            var fat = session.LoadLocal(builder, slots, call.Args[0]);
            var typeId = builder.BuildAnd(builder.BuildExtractValue(fat, 0, "iface.value.type"),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, BoxEmitter.SheetMask, false), "iface.value.sheet.bits");
            var sheet = builder.BuildIntToPtr(typeId, PointerType(), "iface.value.sheet");
            var done = session.CurrentFunction.AppendBasicBlock("iface.value.done");
            foreach (var (host, method) in values)
            {
                var plan = session.Layout.Find(host)!;
                var match = session.CurrentFunction.AppendBasicBlock("iface.value.match");
                var next = session.CurrentFunction.AppendBasicBlock("iface.value.next");
                builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sheet,
                    session.TypeSheetFor(plan.Symbol.Canonical), "iface.value.is"), match, next);
                builder.PositionAtEnd(match);
                var callee = session.FunctionOf(method);
                var temps = new List<ArcEmitter.RichTemp>();
                var boxed = new List<ArcEmitter.FatTemp>();
                var result = builder.BuildCall2(callee.Type, callee.Value,
                    CallEmitter.MarshalArgs(session, builder, slots, callee.Mir, call.Args,
                        call.Result, temps, boxed, hostConstructedRef: plan.Symbol.Canonical,
                        excTarget: call.ExcTarget), "");
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
                CallEmitter.StoreCoercedResult(session, builder, slots, callee.Mir.ReturnType,
                    result, call.Result, call.ExcTarget);
                builder.BuildBr(done);
                builder.PositionAtEnd(next);
            }
            EmitObjectInterface(session, builder, slots, call, target);
            builder.BuildBr(done);
            builder.PositionAtEnd(done);
        }

        private static void EmitObjectInterface(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirCall call, MwMemberSymbol target)
        {
            var slot = InterfaceSlotOf(session, target);
            var ifaceSheet = InterfaceSheetOf(session, target);
            var entry = EmitVTableEntry(session, builder, "rigi_imap_entry",
                new[] { ObjectPointer(session, builder, slots, call.Args[0]), ifaceSheet,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)slot, false) });
            var temps = new List<ArcEmitter.RichTemp>();
            var boxed = new List<ArcEmitter.FatTemp>();
            if (FatValueSlotAbi.IsGenericInterfaceSlot(target))
            {
                var slotSignature = FatValueSlotAbi.InterfaceSignature(session, target);
                var result = builder.BuildCall2(MethodFunctionTypeOf(session, target, slotSignature), entry,
                    CallEmitter.MarshalArgs(session, builder, slots, slotSignature, call.Args, call.Result,
                        temps, boxed, coerceParameters: true), "");
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
                CallEmitter.StoreCoercedResult(session, builder, slots,
                    MirType.Of(slotSignature.ReturnTypeRef), result, call.Result, call.ExcTarget);
                return;
            }
            if (session.TryGetFunction(target.Canonical, out var callee))
            {
                var callResult = builder.BuildCall2(callee.Type, entry,
                    CallEmitter.MarshalArgs(session, builder, slots, callee.Mir, call.Args,
                        call.Result, temps, boxed), "");
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
                CallEmitter.StoreCoercedResult(session, builder, slots, callee.Mir.ReturnType,
                    callResult, call.Result, call.ExcTarget);
                return;
            }
            var signature = CanonicalSignature.Parse(target.Canonical);
            var fnType = MethodFunctionTypeOf(session, target, signature);
            var abstractResult = builder.BuildCall2(fnType, entry,
                CallEmitter.MarshalArgs(session, builder, slots, signature, call.Args, call.Result,
                    temps, boxed), "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
            ExceptionEmitter.EmitPendingCheck(session, builder, call.ExcTarget);
            CallEmitter.StoreCoercedResult(session, builder, slots,
                MirType.Of(signature.ReturnTypeRef), abstractResult, call.Result, call.ExcTarget);
        }

        // callable 协议：对 CallTarget 虚调用 $$call（实参列表不含 receiver，此处补上）
        internal static void EmitIndirect(ModuleBuilder.Session session, LLVMBuilderRef builder,
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
            var interfaceSlot = FatValueSlotAbi.IsGenericInterfaceSlot(callOperator);
            var signature = interfaceSlot ? FatValueSlotAbi.InterfaceSignature(session, callOperator)
                : FatValueSlotAbi.IsCallable(session, callOperator)
                ? FatValueSlotAbi.Signature(callOperator)
                : CanonicalSignature.Create(siteParams, siteReturn);
            var fnType = MethodFunctionTypeOf(session, callOperator, signature);
            var slot = VirtualSlotOf(session, callOperator);
            LLVMValueRef entry;
            if (owner.Declaration.Kind == BilTypeKind.Interface)
            {
                var ifaceSheet = InterfaceSheetOf(session, callOperator);
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
                CallEmitter.MarshalArgs(session, builder, slots, signature, callArgs, inst.Result,
                    temps, boxed, coerceParameters: interfaceSlot || FatValueSlotAbi.IsCallable(session, callOperator)), "");
            ArcEmitter.DestroyRichTemps(session, builder, temps);
            ArcEmitter.DestroyFatTemps(session, builder, boxed);
            ExceptionEmitter.EmitPendingCheck(session, builder, inst.ExcTarget);
            CallEmitter.StoreCoercedResult(session, builder, slots,
                MirType.Of(signature.ReturnTypeRef), callResult, inst.Result, inst.ExcTarget);
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
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, faceSymbol, PointerType(),
                paramTypes);
            return builder.BuildCall2(fnType, fn, faceArgs, "dispatch.entry");
        }

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
            throw new CompilerInternalException($"虚槽缺失: {target.Canonical}; "
                + $"owner={target.Owner?.Canonical}; key={GenericAbi.PlanKey(target.Owner!)}; "
                + $"plan={(plan == null ? "null" : string.Join(",", plan.VTableSlots))}");
        }

        // 接口 TypeSheet：恒取模板键 sheet。iMap 每个接口段都补模板
        // 别名键（VTablePlanner.AppendInterfaceSegment），构造对象与泛型
        // 空壳对象按模板键全称命中；具化空壳 sheet 与模板空壳 sheet 不同
        // 地址，rigi_imap_entry 按裸指针相等查表会永不命中
        private static LLVMValueRef InterfaceSheetOf(ModuleBuilder.Session session,
            MwMemberSymbol target) => session.TypeSheetFor(target.Owner!.Canonical);

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

        // 抽象 generic 基类槽（如 core::Cell<T>.getValue/setValue）的
        // canonical 签名含宿主泛型占位：槽内 override 的签名按接收者构造
        // 具化，占位直映胖引用会与具化槽型不符——按接收者静态类型沿
        // extends 链找目标宿主的构造实参并代入（invoke.indirect 的
        // $$call 具化先例同语义）；无法定位构造实参时原样返回（保守回退）
        private static CanonicalSignature SubstituteForReceiver(ModuleBuilder.Session session,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand receiver,
            MwMemberSymbol target, CanonicalSignature signature)
        {
            if (target.Owner is not { } owner
                || owner.Declaration.GenericParameters.Count == 0
                || receiver is not MirLocalOperand local)
            {
                return signature;
            }
            var needs = signature.ReturnTypeRef.Contains(".generic<",
                System.StringComparison.Ordinal);
            for (var i = 0; !needs && i < signature.Parameters.Count; i++)
            {
                needs = signature.Parameters[i].TypeRef.Contains(".generic<",
                    System.StringComparison.Ordinal);
            }
            if (!needs)
            {
                return signature;
            }
            // 沿 extends 链走：构造实参只在声明的 extends 引用文本上
            //（构造类型未入符号表），符号仅用于继续上溯
            string? constructed = null;
            for (var currentRef = slots[local.Name].Local.Type.Canonical;
                currentRef != null;
                currentRef = session.Symbols.FindTypeByRef(currentRef)
                    ?.Declaration.ExtendsType)
            {
                var normalized = MwTypeKey.Normalize(currentRef);
                if (BilVerificationContext.StripTypeArguments(normalized) == owner.Canonical)
                {
                    constructed = normalized;
                    break;
                }
            }
            var subst = constructed == null
                ? null
                : ConstructedTypeCollector.BuildSubstitution(constructed, owner.Declaration);
            if (subst == null)
            {
                return signature;
            }
            var parameters = new List<(string, string)>(signature.Parameters.Count);
            foreach (var parameter in signature.Parameters)
            {
                parameters.Add((parameter.Name,
                    ConstructedTypeCollector.Substitute(parameter.TypeRef, subst)));
            }
            return CanonicalSignature.Create(parameters,
                ConstructedTypeCollector.Substitute(signature.ReturnTypeRef, subst));
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
