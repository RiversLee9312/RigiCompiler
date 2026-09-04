using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// getid.type / getid.var 发射（TypeOps 簇）：闭合值类型直接 store
    /// TypeSheet*；class / .any / 泛型占位走 rigi_typeof。
    /// </summary>
    internal static class TypeIdEmitter
    {
        internal sealed class OfType : LlvmEmitVisitor<OfType, MirGetTypeId>
        {
            protected override void VisitCore(MirGetTypeId inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                builder.BuildStore(ArrayEmitter.TypeSheetPointer(session, builder,
                    MirType.Of(inst.TypeRef)), slots[inst.Target].Slot);
            }
        }

        internal sealed class OfVar : LlvmEmitVisitor<OfVar, MirGetTypeIdVar>
        {
            protected override void VisitCore(MirGetTypeIdVar inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                if (inst.Value is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("getid.var 操作数必须是局部");
                }
                var type = slots[local.Name].Local.Type;
                // 非开放值类型：静态类型即实际类型，直接 store TypeSheet*
                if (IsClosedValueForTypeOf(session, type))
                {
                    builder.BuildStore(ArrayEmitter.TypeSheetPointer(session, builder, type),
                        slots[inst.Target].Slot);
                    return;
                }
                // class / .any / 泛型占位胖槽：运行时取实际 sheet
                var fat = session.LoadLocal(builder, slots, inst.Value);
                var typeId = builder.BuildExtractValue(fat, 0, "typeof.typeid");
                var payload = builder.BuildExtractValue(fat, 1, "typeof.payload");
                var ptr = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.TypeOf,
                    ptr, new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 });
                var sheet = builder.BuildCall2(fnType, fn, new[] { typeId, payload }, "typeof.raw");
                // C 侧 null 胖引用仍返 NULL；内部链接的 .null sheet 在此替换
                var nullSheet = builder.BuildBitCast(
                    session.TypeSheetFor(TypeLayout.NullSheetCanonical), ptr, "arr.p");
                var isNull = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sheet,
                    LLVMValueRef.CreateConstPointerNull(ptr), "typeof.isnull");
                sheet = builder.BuildSelect(isNull, nullSheet, sheet, "typeof.sheet");
                builder.BuildStore(sheet, slots[inst.Target].Slot);
            }
        }

        // getid.field（L1）：fieldid 槽 = 8B 占位指针，运行时值无消费面
        //（indirect 族 lowering 期已静态解析回字段符号）——写 null 占位，
        // 保持「先写后读」槽纪律
        internal sealed class OfField : LlvmEmitVisitor<OfField, MirGetFieldId>
        {
            protected override void VisitCore(MirGetFieldId inst, ModuleBuilder.Session session)
            {
                session.Builder.BuildStore(
                    LLVMValueRef.CreateConstNull(
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)),
                    session.Slots[inst.Target].Slot);
            }
        }

        private static bool IsClosedValueForTypeOf(ModuleBuilder.Session session, MirType type)
        {
            if (TypeLayout.IsGenericPlaceholder(type))
            {
                return false;
            }
            return MirBuilder.IsScalarOrString(type)
                || TypeLayout.IsTypeId(type)
                || session.IsInlineValueType(type, out _);
        }
    }
}
