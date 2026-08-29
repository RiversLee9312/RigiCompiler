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
