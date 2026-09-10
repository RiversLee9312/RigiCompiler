using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// struct / enum-struct 值类型布局：偏移 0 起（enum 隐藏 u32 判别），字段自然对齐。
    /// </summary>
    internal static class ValueTypeLayout
    {
        internal static TypeLayoutPlan LayoutValueType(MwTypeSymbol type, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting, bool isEnum,
            IReadOnlyDictionary<string, IReadOnlyList<(string Method, string Wrapper)>>?
                methodSlots = null)
        {
            // R3：open rich struct 继承（SYNTAX §8：可继承的 struct 须
            // open rich）——基类字段前缀式排布（与 ClassLayout 同口径：
            // 基类字段在前、本类字段自基类 Size 续排、基类 refMap 位点
            // 按绝对偏移回放——值类型无对象头，basePos 从 0 起），
            // basePlan 链接入（TypeSheet.baseTypeId 链供 rigi_type_is）。
            // enum struct 固定链不参与用户继承，不走此支
            TypeLayoutPlan? basePlan = null;
            if (!isEnum && type.Declaration.ExtendsType is { } baseRef
                && symbols.FindTypeByRef(baseRef) is { IsExternal: false } baseType
                && baseType.Declaration.Kind == BilTypeKind.Struct)
            {
                basePlan = LayoutEngine.Resolve(baseType, symbols, table, visiting, null,
                    methodSlots);
            }
            var fields = new List<FieldPlan>();
            // enum：偏移 0 恒 u32 隐藏判别字段（占 4B），实例字段续排
            var offset = isEnum ? 4 : 0;
            var alignment = isEnum ? 4 : 1;
            var refEntries = new List<RefMapBuilder.RefSite>();
            if (basePlan != null)
            {
                fields.AddRange(basePlan.Fields);
                offset = basePlan.Size;
                alignment = basePlan.Alignment;
                var basePos = 0L;
                foreach (var entry in basePlan.RefMap)
                {
                    basePos += (long)TypeLayout.RefMapHopOf(entry) * LayoutEngine.ReferenceSlotSize;
                    refEntries.Add(new RefMapBuilder.RefSite((int)basePos,
                        TypeLayout.RefMapKindOf(entry), null));
                    basePos += LayoutEngine.ReferenceSlotSize;
                }
            }
            foreach (var member in LayoutEngine.InstanceFields(type))
            {
                var info = LayoutEngine.ClassifyFieldType(LayoutEngine.FieldTypeOf(member), symbols, table, visiting);
                offset = LayoutEngine.AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(member.Canonical, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan, isStringSlot: info.IsStringSlot));
                RefMapBuilder.CollectRefSite(refEntries, info, offset);
                offset = LayoutEngine.CheckedAdd(offset, info.Size,
                    $"类型 {type.Canonical} 的字段 {member.Canonical}");
                if (info.Alignment > alignment)
                {
                    alignment = info.Alignment;
                }
            }
            HiddenStoragePlanner.AppendHostSlots(type, symbols, table, visiting, fields,
                refEntries, ref offset, methodSlots);
            var size = LayoutEngine.AlignUp(offset, alignment);

            var enumCases = new List<(MwCaseSymbol, uint)>();
            if (isEnum)
            {
                foreach (var caseSymbol in type.Cases)
                {
                    enumCases.Add((caseSymbol, caseSymbol.Discriminant));
                }
            }
            // struct 挂 vtable 槽 0 = init 分发器（无继承、无 iMap，槽表
            // 恒一元）；enum 不插槽 0——调用点 vTable null 兜底 abort
            var slots = new List<string>();
            if (!isEnum)
            {
                VTablePlanner.EnsureInitDispatchSlot(slots);
            }
            return new TypeLayoutPlan(type, isEnum ? TypeLayoutKind.Enum : TypeLayoutKind.Struct,
                size, alignment, LayoutEngine.TypeFlagsOf(type), fields,
                slots, System.Array.Empty<(string, int)>(),
                RefMapBuilder.BuildRefMap(fields, refEntries, 0),
                enumCases, basePlan,
                ifaceClosure: VTablePlanner.CollectIfaceClosure(type.Canonical, type, symbols));
        }

        // wrapper 实例布局：与 struct 同构（偏移 0、无对象头），Kind 保持
        // Wrapper（TypeSheet 地址身份 + 内联嵌入宿主）。无 vtable 槽 0——
        // 安装走 new.wrapper.* 直调 init，不走 dynnew 分发器。
        internal static TypeLayoutPlan LayoutWrapper(MwTypeSymbol type, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting)
        {
            var fields = new List<FieldPlan>();
            var offset = 0;
            var alignment = LayoutEngine.ReferenceSlotSize;
            var refEntries = new List<RefMapBuilder.RefSite>();
            // self 是调用参数；实例仅含实际状态，空 wrapper 的布局大小为零。
            foreach (var member in LayoutEngine.InstanceFields(type))
            {
                var info = LayoutEngine.ClassifyFieldType(LayoutEngine.FieldTypeOf(member),
                    symbols, table, visiting);
                offset = LayoutEngine.AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(member.Canonical, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan, isStringSlot: info.IsStringSlot));
                RefMapBuilder.CollectRefSite(refEntries, info, offset);
                offset = LayoutEngine.CheckedAdd(offset, info.Size,
                    $"wrapper {type.Canonical} 的字段 {member.Canonical}");
                if (info.Alignment > alignment)
                {
                    alignment = info.Alignment;
                }
            }
            HiddenStoragePlanner.AppendHostSlots(type, symbols, table, visiting, fields,
                refEntries, ref offset);
            var size = LayoutEngine.AlignUp(offset, alignment);
            return new TypeLayoutPlan(type, TypeLayoutKind.Wrapper, size, alignment,
                LayoutEngine.TypeFlagsOf(type), fields,
                System.Array.Empty<string>(), System.Array.Empty<(string, int)>(),
                RefMapBuilder.BuildRefMap(fields, refEntries, 0),
                System.Array.Empty<(MwCaseSymbol, uint)>(), null,
                ifaceClosure: VTablePlanner.CollectIfaceClosure(type.Canonical, type, symbols));
        }
    }
}
