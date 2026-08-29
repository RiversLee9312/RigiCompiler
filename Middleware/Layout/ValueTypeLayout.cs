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
            var fields = new List<FieldPlan>();
            // enum：偏移 0 恒 u32 隐藏判别字段（占 4B），实例字段续排
            var offset = isEnum ? 4 : 0;
            var alignment = isEnum ? 4 : 1;
            var refEntries = new List<RefMapBuilder.RefSite>();
            foreach (var member in LayoutEngine.InstanceFields(type))
            {
                var info = LayoutEngine.ClassifyFieldType(LayoutEngine.FieldTypeOf(member), symbols, table, visiting);
                offset = LayoutEngine.AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(member.Canonical, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan, isStringSlot: info.IsStringSlot));
                RefMapBuilder.CollectRefSite(refEntries, info, offset);
                offset += info.Size;
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
                enumCases, null,
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
            // 偏移 0：宿主回指（16B 胖值位，不进 refMap——安装时原样
            // 写入、get.self 再 ProduceFatValue；避免宿主↔隐藏槽循环 RC）
            fields.Add(new FieldPlan(WrapperAbi.HostFieldSymbol(GenericAbi.PlanKey(type)),
                0, LayoutEngine.ReferenceSlotSize, LayoutEngine.ReferenceSlotSize,
                isReferenceSlot: false, embeddedPlan: null));
            offset = LayoutEngine.ReferenceSlotSize;
            foreach (var member in LayoutEngine.InstanceFields(type))
            {
                var info = LayoutEngine.ClassifyFieldType(LayoutEngine.FieldTypeOf(member),
                    symbols, table, visiting);
                offset = LayoutEngine.AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(member.Canonical, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan, isStringSlot: info.IsStringSlot));
                RefMapBuilder.CollectRefSite(refEntries, info, offset);
                offset += info.Size;
                if (info.Alignment > alignment)
                {
                    alignment = info.Alignment;
                }
            }
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
