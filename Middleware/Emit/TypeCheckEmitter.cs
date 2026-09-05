using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// type.is / type.supers / type.with 发射（MW5 c3）：经 rigi_type_* helper。
    /// 目标 sheet = TypeSheetFor(静态 canonical) 或 typeid 局部；值类型操作数
    /// 合成 tag0 胖引用（实际 sheet = 静态类型 sheet）。
    /// </summary>
    internal sealed class TypeCheckEmitter : LlvmEmitVisitor<TypeCheckEmitter, MirTypeCheck>
    {
        protected override void VisitCore(MirTypeCheck inst, ModuleBuilder.Session session)
        {
            var builder = session.Builder;
            var slots = session.Slots;
            LLVMValueRef typeId;
            LLVMValueRef payload;
            if (inst.Kind == MirTypeCheckKind.IsTypeId)
            {
                // R2-c：.typeid 原值直判（rigi_type_is 的 tag0/掩码路径
                // 对裸 sheet 指针恒取自身）——不包 Type<X> 元类型视图；
                // .typeid 槽的 LLVM 形态是 ptr，转 i64 入面
                if (inst.Value is not MirLocalOperand typeIdLocal)
                {
                    throw new CompilerInternalException("type.check.istypeid 操作数必须是局部");
                }
                var rawTypeId = session.LoadLocal(builder, slots, typeIdLocal);
                typeId = rawTypeId.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind
                    ? builder.BuildPtrToInt(rawTypeId, LLVMTypeRef.Int64, "ck.tid.bits")
                    : rawTypeId;
                payload = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false);
            }
            else
            {
                var fat = LoadAsFat(session, builder, slots, inst.Value);
                typeId = builder.BuildExtractValue(fat, 0, "ck.typeid");
                payload = builder.BuildExtractValue(fat, 1, "ck.payload");
            }
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
            // .typeid：运行期视图 = Type<payload>，对齐 VM TypeRef 边界
            if (TypeLayout.IsTypeId(type))
            {
                var described = session.LoadLocal(builder, slots, operand);
                var view = BoxEmitter.ResolveTypeIdViewSheet(session, builder, described, type);
                return BoxEmitter.PackFat(session, builder, view, BoxEmitter.TagInline,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), "ck.tid");
            }
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
            return (inst.Kind, inst.IsIndirect) switch
            {
                (MirTypeCheckKind.Is, false) => RuntimeFaces.TypeIs,
                (MirTypeCheckKind.Is, true) => RuntimeFaces.TypeIsIndirect,
                (MirTypeCheckKind.IsTypeId, false) => RuntimeFaces.TypeIs,
                (MirTypeCheckKind.IsTypeId, true) => RuntimeFaces.TypeIsIndirect,
                (MirTypeCheckKind.Supers, false) => RuntimeFaces.TypeSupers,
                (MirTypeCheckKind.Supers, true) => RuntimeFaces.TypeSupersIndirect,
                (MirTypeCheckKind.With, false) => RuntimeFaces.TypeWith,
                (MirTypeCheckKind.With, true) => RuntimeFaces.TypeWithIndirect,
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
