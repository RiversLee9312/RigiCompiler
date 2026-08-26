using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// type.is / type.supers / type.with 发射（MW5 c3）：经 rigi_type_* helper。
    /// 目标 sheet = TypeSheetFor(静态 canonical) 或 typeid 局部；值类型操作数
    /// 合成 tag0 胖引用（实际 sheet = 静态类型 sheet）。
    /// </summary>
    internal static class TypeCheckEmitter
    {
        internal static void Emit(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirTypeCheck inst)
        {
            var fat = LoadAsFat(session, builder, slots, inst.Value);
            var typeId = builder.BuildExtractValue(fat, 0, "ck.typeid");
            var payload = builder.BuildExtractValue(fat, 1, "ck.payload");
            var target = TargetSheet(session, builder, slots, inst);
            var face = FaceNameOf(inst);
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, face, LLVMTypeRef.Int32,
                new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64, PointerType() });
            var raw = builder.BuildCall2(fnType, fn, new[] { typeId, payload, target }, "ck.raw");
            var flag = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, raw,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "ck.bool");
            builder.BuildStore(flag, slots[inst.Target].Slot);
        }

        private static LLVMValueRef LoadAsFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand operand)
        {
            if (operand is not MirLocalOperand local)
            {
                throw new CompilerInternalException("type.check 操作数必须是局部");
            }
            var type = slots[local.Name].Local.Type;
            if (MirBuilder.IsScalarOrString(type) || session.IsInlineValueType(type, out _))
            {
                var sheet = TypeSheetOf(session, type);
                return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), "ck.syn");
            }
            return session.LoadLocal(builder, slots, operand);
        }

        private static LLVMValueRef TargetSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirTypeCheck inst)
        {
            if (inst.TargetTypeId != null)
            {
                return session.LoadLocal(builder, slots, inst.TargetTypeId);
            }
            if (inst.TargetTypeRef == null)
            {
                throw new CompilerInternalException("type.check 缺目标类型");
            }
            return session.TypeSheetFor(inst.TargetTypeRef);
        }

        private static string FaceNameOf(MirTypeCheck inst)
        {
            var tail = inst.IsIndirect ? "_indirect" : "";
            return inst.Kind switch
            {
                MirTypeCheckKind.Is => "rigi_type_is" + tail,
                MirTypeCheckKind.Supers => "rigi_type_supers" + tail,
                MirTypeCheckKind.With => "rigi_type_with" + tail,
                _ => throw new CompilerInternalException("未知 MirTypeCheckKind: " + inst.Kind),
            };
        }

        private static LLVMValueRef TypeSheetOf(ModuleBuilder.Session session, MirType type)
        {
            var key = TypeLayout.BuiltinSheetCanonical(type);
            if (session.TryGetTypeSheet(key, out var sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet))
            {
                return sheet;
            }
            return session.TypeSheetFor(type.Canonical);
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
