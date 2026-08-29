using LLVMSharp.Interop;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 局部拷贝发射：值类型 = memcpy 深拷贝（VM Copy 同口径）；
    /// 标量/胖引用 = 装载转存。
    /// </summary>
    internal sealed class CopyLocalEmitter : LlvmEmitVisitor<CopyLocalEmitter, MirCopyLocal>
    {
        protected override void VisitCore(MirCopyLocal copy, ModuleBuilder.Session session)
        {
            var builder = session.Builder;
            var slots = session.Slots;
            if (copy.Source is MirLocalOperand source
                && session.IsInlineValueType(slots[source.Name].Local.Type, out var plan))
            {
                session.EmitMemCopy(builder, slots[copy.Target].Slot,
                    slots[source.Name].Slot, plan.Size);
                return;
            }
            builder.BuildStore(session.LoadLocal(builder, slots, copy.Source),
                slots[copy.Target].Slot);
        }
    }
}
