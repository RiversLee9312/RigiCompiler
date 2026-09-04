using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// wrapper 隐藏槽发射（MW10 内存路径）：get/set/new.wrapper.* →
    /// <see cref="WrapperAbi"/> 字段 GEP；get.self 读宿主回指（不进 refMap）。
    /// invoke fn(..inner) 由 ProxyBaking 改写为 MirCall 后再发射。
    /// </summary>
    internal static class WrapperEmitter
    {
        internal sealed class Get : LlvmEmitVisitor<Get, MirGetWrapper>
        {
            protected override void VisitCore(MirGetWrapper inst, ModuleBuilder.Session session)
            {
                CopyHidden(session, inst.Host, ResolveEntitySlotSymbol(session,
                    HostCanonical(session, inst.Host), inst.WrapperType), inst.WrapperType,
                    inst.Target);
            }
        }

        internal sealed class GetField : LlvmEmitVisitor<GetField, MirGetWrapperField>
        {
            protected override void VisitCore(MirGetWrapperField inst,
                ModuleBuilder.Session session)
            {
                CopyHidden(session, inst.Host, ResolveFieldSlotSymbol(session,
                    HostCanonical(session, inst.Host), inst.FieldSymbol, inst.WrapperType),
                    inst.WrapperType, inst.Target);
            }
        }

        internal sealed class GetAddr : LlvmEmitVisitor<GetAddr, MirGetWrapperAddr>
        {
            protected override void VisitCore(MirGetWrapperAddr inst, ModuleBuilder.Session session)
            {
                AliasHidden(session, inst.Host, ResolveEntitySlotSymbol(session,
                    HostCanonical(session, inst.Host), inst.WrapperType), inst.Target);
            }
        }

        internal sealed class GetFieldAddr : LlvmEmitVisitor<GetFieldAddr, MirGetWrapperFieldAddr>
        {
            protected override void VisitCore(MirGetWrapperFieldAddr inst,
                ModuleBuilder.Session session)
            {
                AliasHidden(session, inst.Host, ResolveFieldSlotSymbol(session,
                    HostCanonical(session, inst.Host), inst.FieldSymbol, inst.WrapperType),
                    inst.Target);
            }
        }

        internal sealed class GetMethodAddr
            : LlvmEmitVisitor<GetMethodAddr, MirGetWrapperMethodAddr>
        {
            protected override void VisitCore(MirGetWrapperMethodAddr inst,
                ModuleBuilder.Session session)
            {
                AliasHidden(session, inst.Host, ResolveMethodSlotSymbol(session,
                    HostCanonical(session, inst.Host), inst.MethodSymbol, inst.WrapperType),
                    inst.Target);
            }
        }

        internal sealed class SetField : LlvmEmitVisitor<SetField, MirSetWrapperField>
        {
            protected override void VisitCore(MirSetWrapperField inst,
                ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                var pointer = FieldEmitter.HostBasePointer(session, builder, slots, inst.Host);
                var currentType = HostCanonical(session, inst.Host);
                var index = 0;
                while (index < inst.Chain.Count)
                {
                    if (inst.Chain[index] is MirWrapperChainField field
                        && index + 1 < inst.Chain.Count
                        && inst.Chain[index + 1] is MirWrapperChainWrapper wrapper)
                    {
                        var hidden = FieldEmitter.Resolve(session,
                            WrapperAbi.FieldValueSymbol(currentType, field.FieldSymbol,
                                wrapper.WrapperType));
                        pointer = FieldEmitter.ByteGep(builder, pointer, hidden.Offset,
                            "wrap.field");
                        currentType = wrapper.WrapperType;
                        index += 2;
                        continue;
                    }
                    if (inst.Chain[index] is MirWrapperChainField nested)
                    {
                        var nestedField = FieldEmitter.Resolve(session, nested.FieldSymbol);
                        pointer = FieldEmitter.ByteGep(builder, pointer, nestedField.Offset,
                            "wrap.nested");
                        currentType = MwTypeKey.Normalize(
                            FieldEmitter.FieldMirType(nested.FieldSymbol).Canonical);
                        index++;
                        continue;
                    }
                    if (inst.Chain[index] is MirWrapperChainWrapper entity)
                    {
                        var hidden = FieldEmitter.Resolve(session,
                            WrapperAbi.EntityFieldSymbol(currentType, entity.WrapperType));
                        pointer = FieldEmitter.ByteGep(builder, pointer, hidden.Offset,
                            "wrap.entity");
                        currentType = entity.WrapperType;
                        index++;
                        continue;
                    }
                    throw new CompilerInternalException(
                        $"set.wrapper.field 链元素未覆盖: {inst.Chain[index].GetType().Name}");
                }
                var inner = FieldEmitter.Resolve(session, inst.InnerField);
                pointer = FieldEmitter.ByteGep(builder, pointer, inner.Offset, "wrap.inner");
                FieldEmitter.StoreAt(session, builder, slots, inner, inst.InnerField, pointer,
                    inst.Source);
            }
        }

        internal sealed class New : LlvmEmitVisitor<New, MirNewWrapper>
        {
            protected override void VisitCore(MirNewWrapper inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                var hostType = HostCanonical(session, inst.Host);
                var symbol = inst.Kind switch
                {
                    MirWrapperInstallKind.Entity =>
                        ResolveEntitySlotSymbol(session, hostType, inst.WrapperType),
                    MirWrapperInstallKind.Field => ResolveFieldSlotSymbol(session, hostType,
                        inst.FieldSymbol
                        ?? throw new CompilerInternalException("new.wrapper.field 缺字段符号"),
                        inst.WrapperType),
                    MirWrapperInstallKind.Method => ResolveMethodSlotSymbol(session, hostType,
                        inst.MethodSymbol
                        ?? throw new CompilerInternalException("new.wrapper.method 缺方法符号"),
                        inst.WrapperType),
                    _ => throw new CompilerInternalException(
                        $"未覆盖的 new.wrapper 形态: {inst.Kind}"),
                };
                var field = FieldEmitter.Resolve(session, symbol);
                var pointer = FieldEmitter.FieldPointer(session, builder, slots, inst.Host,
                    field.Offset);
                var temps = new List<ArcEmitter.RichTemp>();
                var userArgs = new LLVMValueRef[inst.Args.Count];
                for (var i = 0; i < inst.Args.Count; i++)
                {
                    userArgs[i] = CallEmitter.MarshalArg(session, builder, slots, inst.Args[i],
                        aliasThis: false, temps);
                }
                NewEmitter.EmitInitValueOnSlot(session, builder, slots, pointer, inst.WrapperType,
                    inst.InitWrapper, inst.Init, userArgs);
                WriteHostBackref(session, builder, slots, inst.Host, hostType, inst.WrapperType,
                    pointer);
                ArcEmitter.DestroyRichTemps(session, builder, temps);
            }
        }

        internal sealed class GetSelf : LlvmEmitVisitor<GetSelf, MirGetSelf>
        {
            protected override void VisitCore(MirGetSelf inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                if (!slots.ContainsKey(".this"))
                {
                    throw new CompilerInternalException("get.self 要求当前 fn 有 .this");
                }
                var thisType = slots[".this"].Local.Type.Canonical;
                var field = FieldEmitter.Resolve(session, HostFieldSymbolOf(session, thisType));
                var pointer = FieldEmitter.FieldPointer(session, builder, slots,
                    new MirLocalOperand(".this"), field.Offset);
                var fat = builder.BuildLoad2(TypeLayout.FatReferenceType(session.Context),
                    pointer, "self.host");
                var targetType = slots[inst.Target].Local.Type;
                if (session.IsInlineValueType(targetType, out _))
                {
                    var payload = builder.BuildExtractValue(fat, 1, "self.pl");
                    var src = builder.BuildIntToPtr(payload,
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "self.src");
                    ArcEmitter.EmitInitRichValue(session, builder, slots[inst.Target].Slot,
                        src, targetType);
                    return;
                }
                builder.BuildStore(
                    ArcEmitter.ProduceFatValue(session, builder, fat, "self"),
                    slots[inst.Target].Slot);
            }
        }

        private static void WriteHostBackref(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand host,
            string hostType, string wrapperType, LLVMValueRef wrapperPointer)
        {
            var field = FieldEmitter.Resolve(session, HostFieldSymbolOf(session, wrapperType));
            var dest = FieldEmitter.ByteGep(builder, wrapperPointer, field.Offset, "wrap.host");
            LLVMValueRef fat;
            if (host is MirLocalOperand local
                && session.IsInlineValueType(slots[local.Name].Local.Type, out _))
            {
                fat = CallEmitter.BuildFatReference(session, builder,
                    session.TypeSheetFor(hostType), slots[local.Name].Slot);
            }
            else
            {
                fat = session.LoadLocal(builder, slots, host);
            }
            builder.BuildStore(fat, dest);
        }

        private static string HostFieldSymbolOf(ModuleBuilder.Session session, string wrapperCanonical)
        {
            if (session.Symbols.FindTypeByRef(wrapperCanonical) is { } type)
            {
                return WrapperAbi.HostFieldSymbol(GenericAbi.PlanKey(type));
            }
            return WrapperAbi.HostFieldSymbol(wrapperCanonical);
        }

        private static void CopyHidden(ModuleBuilder.Session session, MirOperand host,
            string hiddenSymbol, string wrapperType, string target)
        {
            var field = FieldEmitter.Resolve(session, hiddenSymbol);
            var pointer = FieldEmitter.FieldPointer(session, session.Builder, session.Slots,
                host, field.Offset);
            ArcEmitter.EmitInitRichValue(session, session.Builder,
                session.Slots[target].Slot, pointer, MirType.Of(wrapperType));
        }

        // 环 receiver 槽别名：目标局部的槽就地重定向为隐藏槽 GEP 地址
        //（沿用值类型 .this「槽即传入指针」纪律——环 fn 经 aliasThis 拿到
        // 的即此地址，写原地生效；原 alloca 空置，无值拷贝，无 acquire/
        // release 义务——RcInjection 已将别名目标排除出 release 序列）
        private static void AliasHidden(ModuleBuilder.Session session, MirOperand host,
            string hiddenSymbol, string target)
        {
            var field = FieldEmitter.Resolve(session, hiddenSymbol);
            var pointer = FieldEmitter.FieldPointer(session, session.Builder, session.Slots,
                host, field.Offset);
            session.Slots[target] = (pointer, session.Slots[target].Local);
        }

        private static string HostCanonical(ModuleBuilder.Session session, MirOperand host)
        {
            if (host is not MirLocalOperand local)
            {
                throw new CompilerInternalException(
                    $"wrapper 宿主不是局部: {host.GetType().Name}");
            }
            return MwTypeKey.Normalize(session.Slots[local.Name].Local.Type.Canonical);
        }

        // Entity 槽符号解析（遗3）：槽身份对齐 VM HiddenEntityKey（键仅
        // wrapper TypeRef）——物理槽恒归首次声明（最基类）名下，随
        // basePlan.Fields 原名逐层拷入子类布局（偏移一致）；子类重申同
        // ref 不再另开槽（HiddenStoragePlanner 去重），手写 BIL 未重申的
        // 祖先-only 应用同归祖先槽——沿 extends 链下探取首个命中。未
        // 命中回退本类名钥匙（FieldEmitter.Resolve 抛清晰内部错误）
        private static string ResolveEntitySlotSymbol(ModuleBuilder.Session session,
            string hostCanonical, string wrapperType)
        {
            return ResolveSlotWithDescent(session, hostCanonical,
                current => WrapperAbi.EntityFieldSymbol(current, wrapperType));
        }

        // 字段-Value 槽符号解析（刀6）：子类 ..init.wrapper 闭包缝合安装
        // 继承字段的 wrapper（§9.7），槽钥匙归字段声明类名下（随
        // basePlan.Fields 拷入子类布局）——沿 extends 链下探取首个命中
        private static string ResolveFieldSlotSymbol(ModuleBuilder.Session session,
            string hostCanonical, string fieldSymbol, string wrapperType)
        {
            return ResolveSlotWithDescent(session, hostCanonical,
                current => WrapperAbi.FieldValueSymbol(current, fieldSymbol, wrapperType));
        }

        // Method 槽符号解析（刀6）：子类 ..init.wrapper 闭包缝合安装继承
        // 方法的 wrapper（o2 形态——实现槽符号键，槽钥匙归方法声明类名
        // 下），沿 extends 链下探取首个命中
        private static string ResolveMethodSlotSymbol(ModuleBuilder.Session session,
            string hostCanonical, string methodSymbol, string wrapperType)
        {
            return ResolveSlotWithDescent(session, hostCanonical,
                current => WrapperAbi.MethodFieldSymbol(current, methodSymbol, wrapperType));
        }

        // 沿 extends 链下探取首个含候选槽符号的布局计划（环保护）；
        // 未命中回退本类名钥匙（FieldEmitter.Resolve 抛清晰内部错误）
        private static string ResolveSlotWithDescent(ModuleBuilder.Session session,
            string hostCanonical, Func<string, string> candidateOf)
        {
            var current = hostCanonical;
            var guard = new HashSet<string>(System.StringComparer.Ordinal);
            while (guard.Add(current))
            {
                var candidate = candidateOf(current);
                var plan = session.Layout?.Find(current);
                if (plan != null)
                {
                    foreach (var field in plan.Fields)
                    {
                        if (field.Symbol == candidate)
                        {
                            return candidate;
                        }
                    }
                }
                if (session.Symbols.FindTypeByRef(current)?.Declaration.ExtendsType
                        is not { } baseRef)
                {
                    break;
                }
                current = MwTypeKey.Normalize(baseRef);
            }
            return candidateOf(hostCanonical);
        }
    }
}
