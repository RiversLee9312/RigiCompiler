using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// ARC 机制层唯一知识点：按 ClassifySlot 分派 acquire/release，
    /// 并提供字段/数组/调用等拷贝点的聚合原语。面对 LLVM 的声明一律
    /// 经 RuntimeFaces + CallEmitter.DeclareArcFace。
    /// </summary>
    internal static class ArcEmitter
    {
        internal readonly struct RichTemp
        {
            public LLVMValueRef Addr { get; }
            public LLVMValueRef Sheet { get; }
            public int Size { get; }

            public RichTemp(LLVMValueRef addr, LLVMValueRef sheet, int size)
            {
                Addr = addr;
                Sheet = sheet;
                Size = size;
            }
        }

        internal readonly struct FatTemp
        {
            public LLVMValueRef TypeId { get; }
            public LLVMValueRef Payload { get; }

            public FatTemp(LLVMValueRef typeId, LLVMValueRef payload)
            {
                TypeId = typeId;
                Payload = payload;
            }
        }

        internal sealed class Acquire : LlvmEmitVisitor<Acquire, MirAcquireSlot>
        {
            protected override void VisitCore(MirAcquireSlot inst, ModuleBuilder.Session session) =>
                EmitAcquireSlot(session, session.Builder, session.Slots, inst.Local);
        }

        internal sealed class Release : LlvmEmitVisitor<Release, MirReleaseSlot>
        {
            protected override void VisitCore(MirReleaseSlot inst, ModuleBuilder.Session session) =>
                EmitReleaseSlot(session, session.Builder, session.Slots, inst.Local);
        }

        // RcInjection 的托管局部拷贝固定展开为 release → copy → acquire。
        // 三条 MIR 不得各自独立发射，否则 macroGC 可在中间态观察到槽位边与
        // RC 不一致；ModuleBuilder 识别该三元组后走本入口一次性包裹。
        internal static void EmitManagedCopy(ModuleBuilder.Session session,
            MirReleaseSlot release, MirCopyLocal copy, MirAcquireSlot acquire)
        {
            if (release.Local != copy.Target || acquire.Local != copy.Target)
            {
                throw new CompilerInternalException(
                    $"托管局部拷贝序列不匹配: release ${release.Local}, copy ${copy.Target}, acquire ${acquire.Local}");
            }
            var builder = session.Builder;
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            EmitReleaseSlot(session, builder, session.Slots, release.Local);
            CopyLocalEmitter.Visit(copy, session);
            EmitAcquireSlot(session, builder, session.Slots, acquire.Local);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        internal static void EmitAcquireSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, string name)
        {
            var (slot, local) = slots[name];
            switch (TypeLayout.ClassifySlot(session.Layout, local.Type))
            {
                case ManagedSlotKind.FatReference:
                {
                    var fat = LoadFat(session, builder, slot, name + ".acq");
                    var typeId = builder.BuildExtractValue(fat, 0, name + ".acq.tid");
                    var payload = builder.BuildExtractValue(fat, 1, name + ".acq.pl");
                    var newPayload = CallRefAcquire(session, builder, typeId, payload,
                        name + ".acq.new");
                    builder.BuildStore(PackFat(session, builder, fat, newPayload, name + ".acq"),
                        slot);
                    break;
                }
                case ManagedSlotKind.String:
                {
                    var data = LoadStringData(session, builder, slot, name + ".acq");
                    CallStringFace(session, builder, RuntimeFaces.StringAcquire, data);
                    break;
                }
                case ManagedSlotKind.RichValue:
                    CallValueFace(session, builder, RuntimeFaces.ValueAcquire, slot,
                        SheetOf(session, local.Type));
                    break;
            }
        }

        internal static void EmitReleaseSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, string name)
        {
            var (slot, local) = slots[name];
            switch (TypeLayout.ClassifySlot(session.Layout, local.Type))
            {
                case ManagedSlotKind.FatReference:
                {
                    var fat = LoadFat(session, builder, slot, name + ".rel");
                    var typeId = builder.BuildExtractValue(fat, 0, name + ".rel.tid");
                    var payload = builder.BuildExtractValue(fat, 1, name + ".rel.pl");
                    CallRefRelease(session, builder, typeId, payload);
                    break;
                }
                case ManagedSlotKind.String:
                {
                    var data = LoadStringData(session, builder, slot, name + ".rel");
                    CallStringFace(session, builder, RuntimeFaces.StringRelease, data);
                    break;
                }
                case ManagedSlotKind.RichValue:
                    CallValueFace(session, builder, RuntimeFaces.ValueRelease, slot,
                        SheetOf(session, local.Type));
                    break;
            }
        }

        // 先 acquire 新值再 release 旧值（自赋值安全），写回 {typeid, newPayload}。
        // 整段必须对 macroGC 呈现为一次原子 ownership mutation：各 ARC 面内部
        // region 会嵌套，最外层直到槽位写回后才退出。
        internal static void EmitAssignFatRef(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef typeid,
            LLVMValueRef payload)
        {
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            var newPayload = CallRefAcquire(session, builder, typeid, payload, "aref.acq");
            ReleaseFatAt(session, builder, dstAddr, "aref.old");
            builder.BuildStore(MakeFat(session, builder, typeid, newPayload), dstAddr);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        // 已持有 +1 的胖值写入（盒/alloc 产物）：只 release 旧值再 store
        internal static void EmitMoveFatRef(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef typeid,
            LLVMValueRef payload)
        {
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            ReleaseFatAt(session, builder, dstAddr, "mref.old");
            builder.BuildStore(MakeFat(session, builder, typeid, payload), dstAddr);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        internal static (LLVMValueRef TypeId, LLVMValueRef Payload) EmitProduceFatRef(
            ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef typeid, LLVMValueRef payload)
        {
            return (typeid, CallRefAcquire(session, builder, typeid, payload, "pref"));
        }

        internal static void EmitAssignString(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef data,
            LLVMValueRef len)
        {
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            CallStringFace(session, builder, RuntimeFaces.StringAcquire, data);
            var old = builder.BuildLoad2(StringAbi.ValueType(session.Context), dstAddr,
                "astr.old");
            CallStringFace(session, builder, RuntimeFaces.StringRelease,
                builder.BuildExtractValue(old, 0, "astr.old.data"));
            builder.BuildStore(PackString(session, builder, data, len), dstAddr);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        internal static void EmitMoveString(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef data,
            LLVMValueRef len)
        {
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            var old = builder.BuildLoad2(StringAbi.ValueType(session.Context), dstAddr,
                "mstr.old");
            CallStringFace(session, builder, RuntimeFaces.StringRelease,
                builder.BuildExtractValue(old, 0, "mstr.old.data"));
            builder.BuildStore(PackString(session, builder, data, len), dstAddr);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        internal static LLVMValueRef EmitProduceString(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef data)
        {
            CallStringFace(session, builder, RuntimeFaces.StringAcquire, data);
            return data;
        }

        internal static void EmitCopyRichValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef srcAddr,
            LLVMValueRef sheet, int size)
        {
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            CallValueFace(session, builder, RuntimeFaces.ValueAcquire, srcAddr, sheet);
            CallValueFace(session, builder, RuntimeFaces.ValueRelease, dstAddr, sheet);
            session.EmitMemCopy(builder, dstAddr, srcAddr, size);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        internal static void EmitInitRichValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef srcAddr,
            LLVMValueRef sheet, int size)
        {
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            CallValueFace(session, builder, RuntimeFaces.ValueAcquire, srcAddr, sheet);
            session.EmitMemCopy(builder, dstAddr, srcAddr, size);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        internal static void EmitInitRichValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef srcAddr,
            MirType type)
        {
            if (!session.IsInlineValueType(type, out var plan))
            {
                return;
            }
            if (plan.RefMapCount > 0)
            {
                EmitInitRichValue(session, builder, dstAddr, srcAddr, SheetOf(session, type),
                    plan.Size);
            }
            else
            {
                session.EmitMemCopy(builder, dstAddr, srcAddr, plan.Size);
            }
        }

        internal static void EmitCopyRichValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef srcAddr,
            MirType type)
        {
            if (!session.IsInlineValueType(type, out var plan))
            {
                return;
            }
            if (plan.RefMapCount > 0)
            {
                EmitCopyRichValue(session, builder, dstAddr, srcAddr, SheetOf(session, type),
                    plan.Size);
            }
            else
            {
                session.EmitMemCopy(builder, dstAddr, srcAddr, plan.Size);
            }
        }

        internal static void EmitDestroyRichValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef addr, LLVMValueRef sheet)
        {
            CallValueFace(session, builder, RuntimeFaces.ValueRelease, addr, sheet);
        }

        internal static void EmitDestroyRichValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef addr, MirType type)
        {
            if (!session.IsInlineValueType(type, out var plan) || plan.RefMapCount == 0)
            {
                return;
            }
            EmitDestroyRichValue(session, builder, addr, SheetOf(session, type));
        }

        internal static void EmitValueAcquire(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef addr, MirType type)
        {
            var key = TypeLayout.BuiltinSheetCanonical(type);
            if (session.TryGetTypeSheet(key, out var sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet))
            {
                CallValueFace(session, builder, RuntimeFaces.ValueAcquire, addr, sheet);
            }
        }

        internal static void DestroyRichTemps(ModuleBuilder.Session session,
            LLVMBuilderRef builder, List<RichTemp> temps)
        {
            foreach (var temp in temps)
            {
                EmitDestroyRichValue(session, builder, temp.Addr, temp.Sheet);
            }
        }

        internal static void DestroyFatTemps(ModuleBuilder.Session session,
            LLVMBuilderRef builder, List<FatTemp> temps)
        {
            foreach (var temp in temps)
            {
                CallRefRelease(session, builder, temp.TypeId, temp.Payload);
            }
        }

        internal static void EmitReleaseFatValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef fat)
        {
            CallRefRelease(session, builder,
                builder.BuildExtractValue(fat, 0, "relfat.tid"),
                builder.BuildExtractValue(fat, 1, "relfat.pl"));
        }

        internal static LLVMValueRef PackProducedFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef fat, LLVMValueRef newPayload, string prefix)
        {
            return PackFat(session, builder, fat, newPayload, prefix);
        }

        internal static LLVMValueRef ProduceFatValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef fat, string prefix)
        {
            var typeId = builder.BuildExtractValue(fat, 0, prefix + ".tid");
            var payload = builder.BuildExtractValue(fat, 1, prefix + ".pl");
            var (newTypeId, newPayload) = EmitProduceFatRef(session, builder, typeId, payload);
            return PackFat(session, builder, fat, newTypeId, newPayload, prefix);
        }

        internal static LLVMValueRef ProduceStringValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef str)
        {
            var data = builder.BuildExtractValue(str, 0, "pstr.data");
            EmitProduceString(session, builder, data);
            return str;
        }

        internal static void AssignFatFromSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef srcSlot)
        {
            var typeid = builder.BuildLoad2(LLVMTypeRef.Int64, srcSlot, "aslot.tid");
            var plAddr = builder.BuildGEP2(LLVMTypeRef.Int8, srcSlot,
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 8, false) },
                "aslot.pl.gep");
            var payload = builder.BuildLoad2(LLVMTypeRef.Int64, plAddr, "aslot.pl");
            EmitAssignFatRef(session, builder, dstAddr, typeid, payload);
        }

        internal static void AssignStringFromSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef srcSlot)
        {
            var str = builder.BuildLoad2(StringAbi.ValueType(session.Context), srcSlot, "aslot.str");
            AssignStringValue(session, builder, dstAddr, str);
        }

        internal static void AssignFatValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef fat)
        {
            EmitAssignFatRef(session, builder, dstAddr,
                builder.BuildExtractValue(fat, 0, "afat.tid"),
                builder.BuildExtractValue(fat, 1, "afat.pl"));
        }

        internal static void MoveFatValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef fat)
        {
            EmitMoveFatRef(session, builder, dstAddr,
                builder.BuildExtractValue(fat, 0, "mfat.tid"),
                builder.BuildExtractValue(fat, 1, "mfat.pl"));
        }

        internal static void AssignStringValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef str)
        {
            EmitAssignString(session, builder, dstAddr,
                builder.BuildExtractValue(str, 0, "astr.data"),
                builder.BuildExtractValue(str, 1, "astr.len"));
        }

        internal static LLVMValueRef SheetOf(ModuleBuilder.Session session, MirType type)
        {
            var key = TypeLayout.BuiltinSheetCanonical(type);
            if (session.TryGetTypeSheet(key, out var sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet))
            {
                return sheet;
            }
            return session.TypeSheetFor(type.Canonical);
        }

        private static void ReleaseFatAt(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef addr, string prefix)
        {
            var oldTid = builder.BuildLoad2(LLVMTypeRef.Int64, addr, prefix + ".tid");
            var plAddr = builder.BuildGEP2(LLVMTypeRef.Int8, addr,
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 8, false) },
                prefix + ".pl.gep");
            var oldPl = builder.BuildLoad2(LLVMTypeRef.Int64, plAddr, prefix + ".pl");
            CallRefRelease(session, builder, oldTid, oldPl);
        }

        private static LLVMValueRef MakeFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeid, LLVMValueRef payload)
        {
            var fat = LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context));
            fat = builder.BuildInsertValue(fat, typeid, 0, "fat.tid");
            return builder.BuildInsertValue(fat, payload, 1, "fat.pl");
        }

        private static LLVMValueRef LoadFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef addr, string name) =>
            builder.BuildLoad2(TypeLayout.FatReferenceType(session.Context), addr, name);

        private static LLVMValueRef LoadStringData(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef slot, string prefix)
        {
            var str = builder.BuildLoad2(StringAbi.ValueType(session.Context), slot, prefix);
            return builder.BuildExtractValue(str, 0, prefix + ".data");
        }

        private static LLVMValueRef PackFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef proto, LLVMValueRef newPayload, string prefix)
        {
            return builder.BuildInsertValue(proto, newPayload, 1, prefix + ".pl");
        }

        private static LLVMValueRef PackFat(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef proto, LLVMValueRef typeid,
            LLVMValueRef payload, string prefix)
        {
            var withType = builder.BuildInsertValue(proto, typeid, 0, prefix + ".tid");
            return builder.BuildInsertValue(withType, payload, 1, prefix + ".pl");
        }

        private static LLVMValueRef PackString(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef data, LLVMValueRef len)
        {
            var str = LLVMValueRef.CreateConstNull(StringAbi.ValueType(session.Context));
            str = builder.BuildInsertValue(str, data, 0, "str.data");
            return builder.BuildInsertValue(str, len, 1, "str.len");
        }

        private static LLVMValueRef CallRefAcquire(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeid, LLVMValueRef payload, string name)
        {
            var (fn, fnType) = CallEmitter.DeclareArcFace(session, RuntimeFaces.RefAcquire);
            return builder.BuildCall2(fnType, fn, new[] { typeid, payload }, name);
        }

        private static void CallRefRelease(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeid, LLVMValueRef payload)
        {
            var (fn, fnType) = CallEmitter.DeclareArcFace(session, RuntimeFaces.RefRelease);
            builder.BuildCall2(fnType, fn, new[] { typeid, payload }, "");
        }

        private static void CallStringFace(ModuleBuilder.Session session,
            LLVMBuilderRef builder, string face, LLVMValueRef data)
        {
            var (fn, fnType) = CallEmitter.DeclareArcFace(session, face);
            builder.BuildCall2(fnType, fn, new[] { data }, "");
        }

        private static void CallValueFace(ModuleBuilder.Session session,
            LLVMBuilderRef builder, string face, LLVMValueRef addr, LLVMValueRef sheet)
        {
            var (fn, fnType) = CallEmitter.DeclareArcFace(session, face);
            builder.BuildCall2(fnType, fn, new[] { addr, sheet }, "");
        }

        private static void CallRegionFace(ModuleBuilder.Session session,
            LLVMBuilderRef builder, string face)
        {
            var (fn, fnType) = CallEmitter.DeclareArcFace(session, face);
            builder.BuildCall2(fnType, fn, System.Array.Empty<LLVMValueRef>(), "");
        }
    }
}
