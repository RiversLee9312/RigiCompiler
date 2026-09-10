using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 闭合构造类型具化计划 + .typeid / Span 空壳合成。
    /// </summary>
    internal static class ConstructedLayout
    {
        // 闭合构造类型具化计划：字段/vtable 前缀/refMap 复用模板；iMap 按
        // 代入后的构造接口重生；Symbol 与基类链按构造 canonical 独立入表
        internal static TypeLayoutPlan? ResolveConstructed(string typeRef, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting, IReadOnlySet<string>? bodies)
        {
            var canonical = MwTypeKey.Normalize(typeRef);
            if (table.Find(canonical) is { } existing)
            {
                return existing;
            }
            if (TypeLayout.IsArray(MirType.Of(canonical)))
            {
                var arrayTemplate = symbols.FindTypeByRef(canonical);
                if (arrayTemplate == null)
                {
                    var declaration = new BilTypeDeclaration(TypeLayout.ArrayTypeCanonical, BilTypeKind.Class);
                    declaration.GenericParameters.Add("T");
                    arrayTemplate = new MwTypeSymbol(declaration, true,
                        System.Array.Empty<MwMemberSymbol>(), System.Array.Empty<MwCaseSymbol>());
                }
                SynthesizeArrayPlan(arrayTemplate, symbols, table, bodies);
                return GenericAbi.IsClosedConstructed(canonical)
                    ? SynthesizeArrayPlan(new MwTypeSymbol(canonical, arrayTemplate), symbols, table, bodies)
                    : table.Find(GenericAbi.PlanKey(arrayTemplate));
            }
            if (TypeLayout.IsNullable(MirType.Of(canonical)))
            {
                if (!GenericAbi.IsClosedConstructed(canonical)) return null;
                var declaration = new BilTypeDeclaration(TypeLayout.NullableTypeCanonical, BilTypeKind.Class);
                declaration.GenericParameters.Add("T");
                var nullableTemplate = new MwTypeSymbol(declaration, isExternal: true,
                    System.Array.Empty<MwMemberSymbol>(), System.Array.Empty<MwCaseSymbol>());
                // 仅类型身份壳；Nullable 值仍沿现有胖值表示，不增加堆对象。
                var nullable = new TypeLayoutPlan(new MwTypeSymbol(canonical, nullableTemplate),
                    TypeLayoutKind.Class, TypeLayout.ReferenceSlotSize, TypeLayout.ReferenceSlotSize,
                    0, System.Array.Empty<FieldPlan>(), System.Array.Empty<string>(),
                    System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                    System.Array.Empty<(MwCaseSymbol, uint)>(), null);
                table.Add(nullable);
                return nullable;
            }
            // Span/SharedSpan 为内建 External class，无 BIL 字段可排；
            // 按「类数组动态元素对象」特判合成（前缀 32B + FlagArray）
            if (TypeLayout.IsSpanCanonical(canonical)
                || TypeLayout.IsSharedSpanCanonical(canonical))
            {
                return GenericAbi.IsClosedConstructed(canonical)
                    ? SynthesizeSpanPlan(canonical, table, visiting)
                    : null;
            }
            // core::Type 无 stdlib 声明：按 Span 先例合成值类型构造 sheet
            if (TypeLayout.IsTypeIdCanonical(canonical))
            {
                return GenericAbi.IsClosedConstructed(canonical)
                    ? SynthesizeTypeIdPlan(canonical, table, visiting)
                    : null;
            }
            if (!GenericAbi.IsClosedConstructed(canonical)
                || symbols.FindTypeByRef(canonical) is not { } template
                || !GenericAbi.ShouldMaterialize(template))
            {
                // MW11d-D：开放构造（AsyncAction<TMessage> 等 GP 在内的
                // 形态）不具化自身计划，但模板计划必须在——模板只按
                // 「裸符号首个元数」进 LayoutEngine.Build 首批（0 元
                // AsyncAction 在列、1 元不在），而 invoke.indirect 的
                // 静态接收者是开放构造时 callOperator 归模板成员，
                // VirtualSlotOf 按模板 PlanKey 查槽（虚槽缺失实证）。
                // 此处把模板计划补进表（不改构造具化语义）。
                if (!GenericAbi.IsClosedConstructed(canonical)
                    && symbols.FindTypeByRef(canonical) is { IsExternal: false } openTemplate)
                {
                    LayoutEngine.Resolve(openTemplate, symbols, table,
                        new HashSet<string>(System.StringComparer.Ordinal), bodies);
                }
                return null;
            }
            if (!visiting.Add(canonical))
            {
                return null;
            }
            var templatePlan = LayoutEngine.Resolve(template, symbols, table,
                new HashSet<string>(System.StringComparer.Ordinal), bodies);
            if (templatePlan == null)
            {
                visiting.Remove(canonical);
                return null;
            }
            if (templatePlan.Kind == TypeLayoutKind.Interface)
            {
                visiting.Remove(canonical);
                var shell = new TypeLayoutPlan(new MwTypeSymbol(canonical, template),
                    TypeLayoutKind.Interface, 0, 1, templatePlan.TypeFlags,
                    templatePlan.Fields, templatePlan.VTableSlots,
                    System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                    templatePlan.EnumCases, null, ifaceClosure:
                    VTablePlanner.CollectIfaceClosure(canonical, template, symbols));
                table.Add(shell);
                return shell;
            }
            if (templatePlan.Kind != TypeLayoutKind.Class)
            {
                visiting.Remove(canonical);
                // G1 泛型值类型具化：字段/vtable 槽 0/refMap/enum 判别复用
                // 模板计划（占位字段恒 16B 胖值槽，构造与模板布局同构）；
                // 无对象头隐藏 typeid 槽、无 iMap（值类型不参与虚/接口
                // 派发）；ifaceClosure 按构造 canonical 重生（type.is 用）
                if (templatePlan.Kind is TypeLayoutKind.Struct or TypeLayoutKind.Enum)
                {
                    var constructedValue = new MwTypeSymbol(canonical, template);
                    var valuePlan = new TypeLayoutPlan(constructedValue,
                        templatePlan.Kind, templatePlan.Size, templatePlan.Alignment,
                        templatePlan.TypeFlags, templatePlan.Fields,
                        templatePlan.VTableSlots,
                        System.Array.Empty<(string, int)>(), templatePlan.RefMap,
                        templatePlan.EnumCases, null,
                        ifaceClosure: VTablePlanner.CollectIfaceClosure(
                            canonical, constructedValue, symbols));
                    table.Add(valuePlan);
                    return valuePlan;
                }
                return null;
            }
            TypeLayoutPlan? basePlan = templatePlan.BasePlan;
            var subst = ConstructedTypeCollector.BuildSubstitution(canonical, template.Declaration);
            if (template.Declaration.ExtendsType is { } baseRef)
            {
                var substituted = MwTypeKey.Normalize(
                    ConstructedTypeCollector.Substitute(baseRef, subst));
                if (GenericAbi.IsClosedConstructed(substituted))
                {
                    basePlan = ResolveConstructed(substituted, symbols, table, visiting, bodies)
                        ?? basePlan;
                }
                else if (symbols.FindTypeByRef(substituted) is { IsExternal: false } baseType
                    && baseType.Declaration.Kind == BilTypeKind.Class)
                {
                    basePlan = LayoutEngine.Resolve(baseType, symbols, table,
                        new HashSet<string>(System.StringComparer.Ordinal), bodies) ?? basePlan;
                }
            }
            var classSlotCount = templatePlan.IMap.Count > 0
                ? templatePlan.IMap[0].BaseOffset
                : templatePlan.VTableSlots.Count;
            var slots = new List<string>(classSlotCount);
            for (var i = 0; i < classSlotCount; i++)
            {
                slots.Add(templatePlan.VTableSlots[i]);
            }
            // 槽 0 标记与模板相同；发射期按具化 canonical 填分发器
            VTablePlanner.EnsureInitDispatchSlot(slots);
            var iMap = new List<(string, int)>();
            foreach (var ifaceRef in template.Declaration.ImplementsTypes)
            {
                var substituted = MwTypeKey.Normalize(
                    ConstructedTypeCollector.Substitute(ifaceRef, subst));
                VTablePlanner.AppendInterfaceSegment(slots, iMap, substituted, symbols, table, canonical,
                    bodies);
            }
            visiting.Remove(canonical);
            var constructed = new MwTypeSymbol(canonical, template);
            var plan = new TypeLayoutPlan(constructed,
                TypeLayoutKind.Class, templatePlan.Size, templatePlan.Alignment,
                templatePlan.TypeFlags, templatePlan.Fields, slots,
                iMap, templatePlan.RefMap, templatePlan.EnumCases,
                basePlan, templatePlan.HiddenTypeIdSlots,
                VTablePlanner.CollectIfaceClosure(canonical, constructed, symbols));
            table.Add(plan);
            return plan;
        }

        // 数组共享固定前缀布局，但绝不共享闭合类型身份。偏移 16 已有的
        // 元素 sheet 同时为源码实例方法提供类级 T，不新增隐藏存储。
        private static TypeLayoutPlan SynthesizeArrayPlan(MwTypeSymbol type,
            MwSymbolTable symbols, LayoutPlanTable table, IReadOnlySet<string>? bodies)
        {
            if (table.Find(GenericAbi.PlanKey(type)) is { } existing) return existing;
            var slots = new List<string> { LayoutEngine.InitDispatchSlot };
            slots.AddRange(LayoutEngine.InstanceMethods(type).Select(member => member.Canonical));
            var iMap = new List<(string, int)>();
            foreach (var iface in type.Declaration.ImplementsTypes)
                VTablePlanner.AppendInterfaceSegment(slots, iMap, iface, symbols, table,
                    type.Canonical, bodies);
            var plan = new TypeLayoutPlan(type, TypeLayoutKind.Class,
                TypeLayout.ArrayPrefixSize, LayoutEngine.ReferenceSlotSize, TypeLayoutPlan.FlagArray,
                System.Array.Empty<FieldPlan>(), slots, iMap, System.Array.Empty<ushort>(),
                System.Array.Empty<(MwCaseSymbol, uint)>(), null,
                new[] { ("T", 16) }, VTablePlanner.CollectIfaceClosure(type.Canonical, type, symbols));
            table.Add(plan);
            return plan;
        }

        // .typeid<X> 构造 sheet：值类型、typeSize 8、FlagInlineValue、
        // 各表空。VM TypesAssignable 对 Type<X>/Type<Any> 不变（无 core::Type
        // 声明、无协变）→ baseTypeId 不指向无界成员。
        internal static TypeLayoutPlan? SynthesizeTypeIdPlan(string canonical,
            LayoutPlanTable table, HashSet<string> visiting)
        {
            if (!visiting.Add(canonical))
            {
                return null;
            }
            var declaration = new BilTypeDeclaration("core::Type", BilTypeKind.Struct);
            declaration.GenericParameters.Add("T");
            var template = new MwTypeSymbol(declaration, isExternal: true,
                System.Array.Empty<MwMemberSymbol>(),
                System.Array.Empty<MwCaseSymbol>());
            var plan = new TypeLayoutPlan(new MwTypeSymbol(canonical, template),
                TypeLayoutKind.Struct, GenericAbi.TypeIdSlotSize, GenericAbi.TypeIdSlotAlign,
                TypeLayoutPlan.FlagInlineValue, System.Array.Empty<FieldPlan>(),
                System.Array.Empty<string>(),
                System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                System.Array.Empty<(MwCaseSymbol, uint)>(), null);
            table.Add(plan);
            visiting.Remove(canonical);
            return plan;
        }

        // Span/SharedSpan 具化计划：无声明字段；前缀 32B；refMap 空
        //（元素走查靠头内 elemSheet）；baseType 经 ExtendsType = Object
        internal static TypeLayoutPlan? SynthesizeSpanPlan(string canonical,
            LayoutPlanTable table, HashSet<string> visiting)
        {
            if (!visiting.Add(canonical))
            {
                return null;
            }
            var shared = TypeLayout.IsSharedSpanCanonical(canonical);
            var head = shared ? TypeLayout.SharedSpanTypeCanonical : TypeLayout.SpanTypeCanonical;
            var declaration = new BilTypeDeclaration(head, BilTypeKind.Class);
            declaration.ExtendsType = "core::Object";
            declaration.GenericParameters.Add("T");
            if (shared)
            {
                declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Shared));
            }
            var template = new MwTypeSymbol(declaration, isExternal: true,
                System.Array.Empty<MwMemberSymbol>(),
                System.Array.Empty<MwCaseSymbol>());
            var flags = TypeLayoutPlan.FlagArray;
            if (shared)
            {
                flags |= TypeLayoutPlan.FlagShared;
            }
            var plan = new TypeLayoutPlan(new MwTypeSymbol(canonical, template),
                TypeLayoutKind.Class, TypeLayout.ArrayPrefixSize, LayoutEngine.ReferenceSlotSize,
                flags, System.Array.Empty<FieldPlan>(),
                new[] { LayoutEngine.InitDispatchSlot },
                System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                System.Array.Empty<(MwCaseSymbol, uint)>(), null);
            table.Add(plan);
            visiting.Remove(canonical);
            return plan;
        }
    }
}
