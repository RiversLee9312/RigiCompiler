using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// class 实例布局：对象头、基类字段、隐藏 typeid、本类字段、vtable/iMap/refMap。
    /// </summary>
    internal static class ClassLayout
    {
        internal static TypeLayoutPlan LayoutClass(MwTypeSymbol type, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting, IReadOnlySet<string>? bodies,
            IReadOnlyDictionary<string, IReadOnlyList<(string Method, string Wrapper)>>?
                methodSlots = null)
        {
            // 基类计划（本地 class 基类可解析时；内建/外部基类无字段布局）。
            // 泛型继承 extends B<T> 经 FindTypeByRef 命中模板 B
            TypeLayoutPlan? basePlan = null;
            if (type.Declaration.ExtendsType is { } baseRef
                && symbols.FindTypeByRef(baseRef) is { IsExternal: false } baseType
                && baseType.Declaration.Kind == BilTypeKind.Class)
            {
                basePlan = LayoutEngine.Resolve(baseType, symbols, table, visiting, bodies,
                    methodSlots);
            }

            // 字段：基类字段在前，本类字段从基类 Size（含头、已 16 对齐）续排
            var fields = new List<FieldPlan>();
            var offset = LayoutEngine.ObjectHeaderSize;
            if (basePlan != null)
            {
                fields.AddRange(basePlan.Fields);
                offset = basePlan.Size;
            }
            // 类级隐藏 typeid：紧随对象头/基类之后、用户字段之前；不进 refMap
            var hiddenSlots = new List<(string, int)>();
            foreach (var param in type.Declaration.GenericParameters)
            {
                offset = LayoutEngine.AlignUp(offset, GenericAbi.TypeIdSlotAlign);
                fields.Add(new FieldPlan(GenericAbi.HiddenFieldSymbol(type.Canonical, param),
                    offset, GenericAbi.TypeIdSlotSize, GenericAbi.TypeIdSlotAlign,
                    isReferenceSlot: false, embeddedPlan: null, isHiddenTypeId: true));
                hiddenSlots.Add((param, offset));
                offset += GenericAbi.TypeIdSlotSize;
            }
            var refEntries = new List<RefMapBuilder.RefSite>();
            // MW9b-G：基类托管位点一并回放进本类 refMap——rigi_destruct
            // 只扫对象自身 sheet 的 refMap（不走 baseTypeId 链），继承的
            // String/胖引用字段漏收会在析构时泄漏（core 异常子类继承
            // Exception.message 是首个触发者）。基类 refMap 的内嵌值类型
            // 已折算平坦，按绝对偏移重建位点即可
            if (basePlan != null)
            {
                var basePos = (long)LayoutEngine.ObjectHeaderSize;
                foreach (var entry in basePlan.RefMap)
                {
                    basePos += (long)TypeLayout.RefMapHopOf(entry) * LayoutEngine.ReferenceSlotSize;
                    refEntries.Add(new RefMapBuilder.RefSite((int)basePos, TypeLayout.RefMapKindOf(entry), null));
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
                offset += info.Size;
            }
            // MW10：本类 Entity / 字段-Value / Method 隐藏存储（基类槽已随
            // basePlan.Fields 拷入）
            HiddenStoragePlanner.AppendHostSlots(type, symbols, table, visiting, fields,
                refEntries, ref offset, methodSlots);
            var size = LayoutEngine.AlignUp(offset, LayoutEngine.ReferenceSlotSize);

            // vtable：槽 0 分发器 → 基类槽继承（override 复用基槽）→
            // 本类自有槽 → interface 实现段
            var slots = new List<string>();
            if (basePlan != null)
            {
                slots.AddRange(basePlan.VTableSlots);
            }
            foreach (var member in LayoutEngine.InstanceMethods(type))
            {
                var key = member.SignatureKey;
                var inherited = slots.FindIndex(s => VTablePlanner.CompatibleSignature(symbols, s, key));
                // 运算符不可标 override（SYNTAX §9.2.1）但子类可同名再定义
                //（静默 hiding）；对齐 VM vtable「有体成员替换同签名槽」
                //（VmTypeSheet.cs 自有槽段），运算符同签名恒覆盖基槽——
                // intrinsic 运算符的实际类型派发（VM FindOperator 口径）
                // 依赖槽实现随派生链更新
                if (inherited >= 0 && (member.HasKeyword(BilKeyword.Override)
                    || member.IsOperatorMember))
                {
                    slots[inherited] = member.Canonical;
                }
                else
                {
                    slots.Add(member.Canonical);
                }
            }
            VTablePlanner.EnsureInitDispatchSlot(slots);
            var iMap = new List<(string, int)>();
            foreach (var ifaceRef in type.Declaration.ImplementsTypes)
            {
                VTablePlanner.AppendInterfaceSegment(slots, iMap, ifaceRef, symbols, table, type.Canonical,
                    bodies);
            }

            return new TypeLayoutPlan(type, TypeLayoutKind.Class, size, LayoutEngine.ReferenceSlotSize,
                LayoutEngine.TypeFlagsOf(type), fields, slots, iMap,
                RefMapBuilder.BuildRefMap(fields, refEntries, LayoutEngine.ObjectHeaderSize),
                System.Array.Empty<(MwCaseSymbol, uint)>(), basePlan, hiddenSlots,
                VTablePlanner.CollectIfaceClosure(type.Canonical, type, symbols));
        }
    }
}
