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
            // Phase 1.2：批量段（ModuleBuilder region 合并）在调用前已进入
            // 外层 region，单槽发射免自带的显式进出（C 侧天然无包裹）。
            // 单发路径无外层，仍由 ARC 面自身 region 进出兜底，语义不变。
            AcquireSlotBody(session, builder, slots, name);
        }

        // 单槽 acquire 的发射体（无显式 region 进出；胖引用克隆回写、
        // String/rich acquire 均为纯运行时调用）。
        internal static void AcquireSlotBody(ModuleBuilder.Session session,
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
            // Phase 1.2：批量段在调用前已进入外层 region，槽清零与释放自动
            // 处于该 region 内（不变量「清空与释放同处一个 region」由外层
            // 满足）；单发路径仍自带显式进出。
            ReleaseSlotBody(session, builder, slots, name);
        }

        // 单槽 release 的发射体（无显式 region 进出）：§23.3「托管局部槽
        // 释放旧值时必须在同一 region 内清零并撤销所有权」——调用方保证
        // 当前处于 region 内（单发 = 自带的显式对；批量段 = 外层对）。
        internal static void ReleaseSlotBody(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, string name)
        {
            var (slot, local) = slots[name];
            // 产出指令可在释放旧值之后抛异常；必须同步撤销槽位所有权，
            // 否则传播垫会再次释放旧值。清空与释放同处一个 region，
            // 防止 macroGC 观察到已经释放但仍挂在槽内的引用。
            switch (TypeLayout.ClassifySlot(session.Layout, local.Type))
            {
                case ManagedSlotKind.FatReference:
                {
                    var fat = LoadFat(session, builder, slot, name + ".rel");
                    var typeId = builder.BuildExtractValue(fat, 0, name + ".rel.tid");
                    var payload = builder.BuildExtractValue(fat, 1, name + ".rel.pl");
                    CallRefRelease(session, builder, typeId, payload);
                    builder.BuildStore(LLVMValueRef.CreateConstNull(
                        TypeLayout.FatReferenceType(session.Context)), slot);
                    break;
                }
                case ManagedSlotKind.String:
                {
                    var data = LoadStringData(session, builder, slot, name + ".rel");
                    CallStringFace(session, builder, RuntimeFaces.StringRelease, data);
                    builder.BuildStore(LLVMValueRef.CreateConstNull(
                        StringAbi.ValueType(session.Context)), slot);
                    break;
                }
                case ManagedSlotKind.RichValue:
                    CallValueFace(session, builder, RuntimeFaces.ValueRelease, slot,
                        SheetOf(session, local.Type));
                    if (session.IsInlineValueType(local.Type, out var plan))
                    {
                        session.EmitMemSetZero(builder, slot, plan.Size);
                    }
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
            // 序 = copy 语义正确序（paramfix）：release(dst) 先清旧值 →
            // memcpy → acquire(dst)。acquire 必须作用于 dst：tag1 盒槽
            // acquire 有深拷回写副作用（arc.c rigi_value_walk），旧序
            // acquire(src) 会把 src 槽改指向克隆——src 原块泄漏、dst 与
            // src 共持克隆（双侧 release 即双释放）。tag2/STRING 两序等价。
            if (dstAddr.Equals(srcAddr))
            {
                return;
            }
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            CallValueFace(session, builder, RuntimeFaces.ValueRelease, dstAddr, sheet);
            session.EmitMemCopy(builder, dstAddr, srcAddr, size);
            CallValueFace(session, builder, RuntimeFaces.ValueAcquire, dstAddr, sheet);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        // EmitCopyRichValue 的运行时尺寸变体：泛型占位数组路径的元素
        // size 来自 elemSheet 运行时读数（.generic.T / 数组头 elemSheet），
        // 编译期拿不到常量。调用方须已确认源是按值内联字节（FlagInlineValue
        // 且非 String 特化槽）；sheet 为运行时 sheet 值（safeSheet）。
        // 序 = copy 语义正确序（paramfix）：release(dst) 先清旧值 →
        // memcpy → acquire(dst)。acquire 必须作用于 dst：tag1 盒槽的
        // rigi_ref_acquire 有深拷回写副作用（arc.c rigi_value_walk 回写
        // 被 walk 的槽），旧序 acquire(src) 会把 src 槽改指向克隆——src
        // 原块泄漏、dst 与 src 共持克隆（双侧 release 即双释放）。
        internal static void EmitCopyRichValueRuntimeSize(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef srcAddr,
            LLVMValueRef sheet, LLVMValueRef size)
        {
            if (dstAddr.Equals(srcAddr))
            {
                return;
            }
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            CallValueFace(session, builder, RuntimeFaces.ValueRelease, dstAddr, sheet);
            session.EmitMemCopyN(builder, dstAddr, srcAddr, size);
            CallValueFace(session, builder, RuntimeFaces.ValueAcquire, dstAddr, sheet);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        // 运行时 sheet 值的 value_acquire（读侧槽→盒重打包用）：
        // BoxEmitter.BoxFromSlot 的运行时 sheet 镜像（refMapSize=0 时
        // rigi_value_walk 为空转，无害）。
        internal static void EmitValueAcquireSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef addr, LLVMValueRef sheet)
        {
            CallValueFace(session, builder, RuntimeFaces.ValueAcquire, addr, sheet);
        }

        // init 语义（paramfix 契约）：dst 为空/死槽（调用点须保证，无需
        // release 旧值），序 = memcpy → acquire(dst)——dst 拿到独立持有
        // 的副本，src 槽不被回写不被动。禁止改回「acquire(src) → memcpy」
        // 旧序：tag1 盒槽（Nullable 装箱）acquire 有深拷回写副作用
        // （arc.c rigi_value_walk 回写被 walk 的槽），会把 src 槽改指向
        // 克隆——src 原块泄漏、src 与 dst 共持克隆（双侧 release 即双
        // 释放；按值传参 UAF 即此，回归语料 rich_return_nullable 传参
        // 形态）。tag1 是唯一有回写副作用的引用槽种类；tag2/STRING 的
        // acquire 对 src/dst 均为纯 +1，两序等价。
        internal static void EmitInitRichValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef dstAddr, LLVMValueRef srcAddr,
            LLVMValueRef sheet, int size)
        {
            if (dstAddr.Equals(srcAddr))
            {
                return;
            }
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            session.EmitMemCopy(builder, dstAddr, srcAddr, size);
            CallValueFace(session, builder, RuntimeFaces.ValueAcquire, dstAddr, sheet);
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

        // ⚠ 交付即移动契约（richretrfix）：返回值交付（TerminatorEmitter
        // MirRet 值类型分支）对 $mw.ret 只做纯 memcpy，本入口不得用于
        // 「交付后的 $mw.ret」——tag1 盒槽（Nullable 装箱）acquire 有深
        // 拷回写副作用（arc.c rigi_value_walk 回写槽 payload），对交付
        // 后的 $mw.ret 再 release 会 free 掉 out 已接管的块（UAF）。
        // 局部临时槽/调用 out 临时槽的生命周期终值清理不受此限。
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
            // 开放泛型没有独立的静态 sheet，必须用运行期实参具化。
            // 找不到静态缓存不能跳过获取：副本中的嵌套引用仍需要独立所有权。
            var sheet = NewEmitter.MaterializeClassSheet(session, builder, session.Slots,
                type.Canonical);
            CallValueFace(session, builder, RuntimeFaces.ValueAcquire, addr, sheet);
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

        // Phase 1.2 region 合并：调用点表达式求值后的 temps 级联析构（rich
        // 值 + 胖引用两组连续销毁）是同一复合操作的收尾——§23.5 把「ARC 归零
        // 后的级联字段 release」纳入 fence 覆盖操作，§23.3 明文允许级联
        // release 复用当前 region。两组均为纯运行时调用（rigi_value_release /
        // rigi_ref_release；C 侧 enter/exit 退化为 TLS 嵌套计数），销毁序列
        // 之间无挂起点与可抛 Rigi 异常调用，两条 region 不变量静态成立。
        // 总数 < 2 不外包：单个析构 C 侧本来就只有一对，外包白付嵌套计数。
        internal static void DestroyTemps(ModuleBuilder.Session session,
            LLVMBuilderRef builder, List<RichTemp> temps, List<FatTemp> boxed)
        {
            if (temps.Count + boxed.Count < 2)
            {
                DestroyRichTemps(session, builder, temps);
                DestroyFatTemps(session, builder, boxed);
                return;
            }
            CallRegionFace(session, builder, RuntimeFaces.RegionEnter);
            DestroyRichTemps(session, builder, temps);
            DestroyFatTemps(session, builder, boxed);
            CallRegionFace(session, builder, RuntimeFaces.RegionExit);
        }

        // 单 rich temps 列表形态（无 boxed 组的调用点）
        internal static void DestroyTemps(ModuleBuilder.Session session,
            LLVMBuilderRef builder, List<RichTemp> temps) =>
            DestroyTemps(session, builder, temps, new List<FatTemp>());

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

        // 泛型胖引用形态守卫（数组元素 ABI 归一防御，RUNTIME §5）：
        // Reference ABI 槽/泛型局部只允许 null{0,0} / tag1 盒 / tag2 对象，
        // tag0 且 payload 非零即发射层 ABI 错配 → rigi_check_fat_ref abort
        internal static void EmitCheckFatRef(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef fat)
        {
            var typeId = builder.BuildExtractValue(fat, 0, "chk.tid");
            var payload = builder.BuildExtractValue(fat, 1, "chk.pl");
            var (fn, fnType) = CallEmitter.DeclareArcFace(session, RuntimeFaces.RefCheck);
            builder.BuildCall2(fnType, fn, new[] { typeId, payload }, "");
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

        // Phase 1.2 起 ModuleBuilder 的连续 ARC 段合并也经此发射 region 边界
        internal static void CallRegionFace(ModuleBuilder.Session session,
            LLVMBuilderRef builder, string face)
        {
            var (fn, fnType) = CallEmitter.DeclareArcFace(session, face);
            builder.BuildCall2(fnType, fn, System.Array.Empty<LLVMValueRef>(), "");
        }
    }
}
