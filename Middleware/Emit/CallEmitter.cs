using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 调用发射（Emit 分面）：MirCall 经 ImplBinder 绑定的三种形态——
    /// NativeDirectBinding（native 面声明 + String 的 rigi_string* 边界
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
                case NativeDirectBinding native:
                {
                    var signature = CanonicalSignature.Parse(call.Target.Canonical);
                    var cSymbol = RuntimeFaces.MapNativeSymbol(native.Library, native.Symbol);
                    var (fn, fnType) = DeclareNativeFace(session, cSymbol, signature);
                    var args = new LLVMValueRef[call.Args.Count];
                    for (var i = 0; i < call.Args.Count; i++)
                    {
                        var argValue = session.LoadLocal(builder, slots, call.Args[i]);
                        var paramType = MirType.Of(signature.Parameters[i].TypeRef);
                        // String 的 C 边界传递约定：rigi_string*（见 RuntimeFaces 注释）
                        args[i] = paramType.IsString ? session.StoreToTemp(builder, argValue) : argValue;
                    }
                    var result = builder.BuildCall2(fnType, fn, args, "");
                    if (call.Result != null)
                    {
                        builder.BuildStore(result, slots[call.Result].Slot);
                    }
                    break;
                }
                case DirectCallBinding direct:
                {
                    EmitDirectCall(session, builder, slots,
                        session.FunctionOf(direct.Target.Canonical), call.Args, call.Result);
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
                default:
                    throw new CompilerInternalException("调用的非预期绑定形态");
            }
        }

        // super 直调（MIR 构建期已解析基类实现符号；不经 BindCall/虚派发）
        internal static void EmitSuperCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirSuperCall call)
        {
            EmitDirectCall(session, builder, slots,
                session.FunctionOf(call.Target.Canonical), call.Args, call.Result);
        }

        // new type(T)（class）：rigi_alloc(@typesheet.T) → 胖引用（typeid
        // 高字节 tag=2，payload=对象指针）→ 可选 ..init.wrapper（字段初始
        // 值缝合，VM 同序）→ init（.this 首参胖引用）→ 结果入槽
        internal static void EmitNew(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewObject inst)
        {
            var sheet = session.TypeSheetFor(inst.Type.Canonical);
            var (allocFn, allocType) = DeclareHelperFace(session, "rigi_alloc",
                PointerType(), new[] { PointerType() });
            var obj = builder.BuildCall2(allocType, allocFn, new[] { sheet }, "new.obj");
            var fat = BuildFatReference(session, builder, sheet, obj);
            if (inst.InitWrapper != null)
            {
                var wrapper = session.FunctionOf(inst.InitWrapper.Canonical);
                builder.BuildCall2(wrapper.Type, wrapper.Value, new[] { fat }, "");
            }
            var init = session.FunctionOf(inst.Init.Canonical);
            var initArgs = new LLVMValueRef[inst.Args.Count + 1];
            initArgs[0] = fat;
            for (var i = 0; i < inst.Args.Count; i++)
            {
                initArgs[i + 1] = MarshalArg(session, builder, slots, inst.Args[i], aliasThis: false);
            }
            builder.BuildCall2(init.Type, init.Value, initArgs, "");
            builder.BuildStore(fat, slots[inst.Target].Slot);
        }

        // ===== 直接/虚/接口调用 =====

        // 直接调用（含 init/super/struct 方法/全局 fn）
        private static void EmitDirectCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            ModuleBuilder.Session.EmittedFunction callee,
            IReadOnlyList<MirOperand> args, string? result)
        {
            var callArgs = MarshalArgs(session, builder, slots, callee.Mir, args, result);
            var callResult = builder.BuildCall2(callee.Type, callee.Value, callArgs, "");
            StoreScalarResult(session, builder, slots, callee.Mir.ReturnType, callResult, result);
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

        // new type(V)（struct/enum）：目标局部的内联槽物化——整槽清零
        //（VM ZeroOf 语义）→ 可选 ..init.wrapper → init（.this 传槽地址，
        // 原地生效）；不装箱、不上堆
        internal static void EmitNewValue(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewValue inst)
        {
            var plan = session.Layout?.Find(inst.Type.Canonical)
                ?? throw new CompilerInternalException($"值类型无布局计划: {inst.Type.Canonical}");
            var slot = slots[inst.Target].Slot;
            session.EmitMemSetZero(builder, slot, plan.Size);
            if (inst.InitWrapper != null)
            {
                var wrapper = session.FunctionOf(inst.InitWrapper.Canonical);
                builder.BuildCall2(wrapper.Type, wrapper.Value, new[] { slot }, "");
            }
            var init = session.FunctionOf(inst.Init.Canonical);
            var initArgs = new LLVMValueRef[inst.Args.Count + 1];
            initArgs[0] = slot;
            for (var i = 0; i < inst.Args.Count; i++)
            {
                initArgs[i + 1] = MarshalArg(session, builder, slots, inst.Args[i], aliasThis: false);
            }
            builder.BuildCall2(init.Type, init.Value, initArgs, "");
        }

        // 虚调用：接收者胖引用 payload → 对象头 [0] 实际 TypeSheet →
        // rigi_vtable_entry 取槽 fnptr 间接调用（fn 类型用静态目标的——
        // 静态目标恒有 fn 体且恒可达）
        private static void EmitVirtualCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirCall call, MwMemberSymbol target)
        {
            var slot = VirtualSlotOf(session, target);
            var callee = session.FunctionOf(target.Canonical);
            var entry = EmitVTableEntry(session, builder, "rigi_vtable_entry",
                new[] { ObjectPointer(session, builder, slots, call.Args[0]),
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)slot, false) });
            var callResult = builder.BuildCall2(callee.Type, entry,
                MarshalArgs(session, builder, slots, callee.Mir, call.Args, call.Result), "");
            StoreScalarResult(session, builder, slots, callee.Mir.ReturnType,
                callResult, call.Result);
        }

        // interface 调用：rigi_imap_entry(obj, @typesheet.Iface, slot) 查
        // base offset 后取槽 fnptr（fn 类型由 canonical 签名合成——接口
        // 符号无 fn 体）
        private static void EmitInterfaceCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirCall call, MwMemberSymbol target)
        {
            var slot = InterfaceSlotOf(session, target);
            var ifaceSheet = session.TypeSheetFor(target.Owner!.Canonical);
            var signature = CanonicalSignature.Parse(target.Canonical);
            var fnType = MethodFunctionTypeOf(session, target, signature);
            var entry = EmitVTableEntry(session, builder, "rigi_imap_entry",
                new[] { ObjectPointer(session, builder, slots, call.Args[0]), ifaceSheet,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)slot, false) });
            var callResult = builder.BuildCall2(fnType, entry,
                MarshalArgs(session, builder, slots, signature, call.Args, call.Result), "");
            StoreScalarResult(session, builder, slots, MirType.Of(signature.ReturnTypeRef),
                callResult, call.Result);
        }

        // ===== 调用辅助 =====

        // 调用实参编组（MW4 批 3 值类型 ABI）：值类型返回 → 隐藏 out 首参
        //（调用方供槽；noret 丢弃则开临时槽）；值类型参数 → memcpy 副本传
        // 指针（callee 改参数不影响调用方，VM Copy 同口径）；值类型 .this
        // 首参 → 直接传槽地址（别名语义，字段写原地生效）
        private static LLVMValueRef[] MarshalArgs(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirFunction calleeMir, IReadOnlyList<MirOperand> args, string? result)
        {
            var hasOut = session.IsInlineValueType(calleeMir.ReturnType, out var outPlan);
            var thisAliases = calleeMir.Parameters.Count > 0
                && calleeMir.Parameters[0].Name == ".this";
            return MarshalArgsCore(session, builder, slots, args, result, hasOut, outPlan, thisAliases);
        }

        // canonical 签名形态（interface 调用：无 fn 体，按签名编组；接收
        // 者恒 class 胖引用，无 .this 别名）
        private static LLVMValueRef[] MarshalArgs(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            CanonicalSignature signature, IReadOnlyList<MirOperand> args, string? result)
        {
            var hasOut = session.IsInlineValueType(MirType.Of(signature.ReturnTypeRef), out var outPlan);
            return MarshalArgsCore(session, builder, slots, args, result, hasOut, outPlan,
                thisAliases: false);
        }

        private static LLVMValueRef[] MarshalArgsCore(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            IReadOnlyList<MirOperand> args, string? result,
            bool hasOut, Layout.TypeLayoutPlan outPlan, bool thisAliases)
        {
            var values = new LLVMValueRef[args.Count + (hasOut ? 1 : 0)];
            if (hasOut)
            {
                values[0] = result != null
                    ? slots[result].Slot
                    : builder.BuildAlloca(
                        LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)outPlan.Size), "call.out");
            }
            for (var i = 0; i < args.Count; i++)
            {
                values[i + (hasOut ? 1 : 0)] = MarshalArg(session, builder, slots,
                    args[i], thisAliases && i == 0);
            }
            return values;
        }

        // 单实参编组（值类型 → memcpy 副本传指针；aliasThis = 值类型
        // .this 别名传槽地址；其余装载求值）。init/new 的实参加工共用
        internal static LLVMValueRef MarshalArg(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirOperand arg, bool aliasThis)
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
            session.EmitMemCopy(builder, temp, slot, plan.Size);
            return temp;
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
        // fnptr；参数类型按实参推导）
        private static LLVMValueRef EmitVTableEntry(ModuleBuilder.Session session,
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

        // class 对象胖引用：{typeid = ptrtoint(sheet) | tag<<56, payload}
        internal static LLVMValueRef BuildFatReference(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeSheet, LLVMValueRef objectPointer)
        {
            const ulong classTag = 2UL;
            var typeId = builder.BuildPtrToInt(typeSheet, LLVMTypeRef.Int64, "new.typeid");
            var tagged = builder.BuildOr(typeId,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, classTag << 56, false), "new.tagged");
            var payload = builder.BuildPtrToInt(objectPointer, LLVMTypeRef.Int64, "new.payload");
            // 两段均被覆写，零常量起手即可（LLVMValueRef.GetUndef 的静态
            // 形式是裸指针 API，安全上下文不可用）
            var fat = LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context));
            fat = builder.BuildInsertValue(fat, tagged, 0, "new.t0");
            return builder.BuildInsertValue(fat, payload, 1, "new.ref");
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

        // 虚槽序号（Layout 计划的宿主 vtable 内索引；同偏移不变量保证
        // 基类槽位在派生类同位）
        private static int VirtualSlotOf(ModuleBuilder.Session session, MwMemberSymbol target)
        {
            var plan = session.Layout?.Find(target.Owner!.Canonical)
                ?? throw new CompilerInternalException($"虚调用宿主无布局计划: {target.Canonical}");
            for (var i = 0; i < plan.VTableSlots.Count; i++)
            {
                if (plan.VTableSlots[i] == target.Canonical)
                {
                    return i;
                }
            }
            throw new CompilerInternalException($"虚槽缺失: {target.Canonical}");
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
            var paramTypes = new LLVMTypeRef[signature.Parameters.Count];
            for (var i = 0; i < signature.Parameters.Count; i++)
            {
                var paramType = MirType.Of(signature.Parameters[i].TypeRef);
                paramTypes[i] = paramType.IsString
                    ? StringAbi.PointerType(session.Context)
                    : TypeLayout.MapType(session.Context, paramType);
            }
            var returnType = MirType.Of(signature.ReturnTypeRef);
            if (returnType.IsString)
            {
                throw new MwNotSupportedException($"MW1 不支持 native 返回 .string: {cSymbol}");
            }
            var type = LLVMTypeRef.CreateFunction(TypeLayout.MapType(session.Context, returnType), paramTypes, false);
            var fn = session.Module.AddFunction(cSymbol, type);
            session.AddFace(cSymbol, fn, type);
            return (fn, type);
        }
    }
}
