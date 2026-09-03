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
    /// native / FFI 调用发射：C 边界编组（String / 胖引用 out 首参、
    /// bool→i8）。形状由 <see cref="CallAbi"/> 回答，本类只填 LLVM 类型。
    /// alloc_array / span_alloc 仍由 ArrayEmitter 接管。
    /// </summary>
    internal static class NativeCallEmitter
    {
        internal static void Emit(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCall call,
            NativeDirectBinding native)
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
                var slot = LlvmEmitEnvironment.BuildEntryAlloca(builder, outType, outName);
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
        }

        private static (LLVMValueRef Fn, LLVMTypeRef Type) DeclareNativeFace(
            ModuleBuilder.Session session, string cSymbol, CanonicalSignature signature)
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

        // C 边界 out 首参：形状由 CallAbi 回答；LLVM 类型复用 StringAbi / TypeLayout
        private static bool TryNativeOutSlot(ModuleBuilder.Session session, MirType returnType,
            out LLVMTypeRef slotType, out string slotName, out uint alignment)
        {
            if (!CallAbi.NeedsNativeOutSlot(returnType))
            {
                slotType = default;
                slotName = "";
                alignment = 0;
                return false;
            }
            if (returnType.IsString)
            {
                slotType = StringAbi.ValueType(session.Context);
                slotName = "native.out";
                alignment = 0;
                return true;
            }
            slotType = TypeLayout.FatReferenceType(session.Context);
            slotName = "native.ref.out";
            alignment = (uint)TypeLayout.ReferenceSlotAlignment;
            return true;
        }

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
                var slot = LlvmEmitEnvironment.BuildEntryAlloca(builder, TypeLayout.FatReferenceType(session.Context),
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
