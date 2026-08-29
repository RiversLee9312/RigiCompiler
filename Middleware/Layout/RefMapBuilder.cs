using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// refMap 扁平化：u16 = (kind<<14)|hop，内嵌值类型折算拼入。
    /// </summary>
    internal static class RefMapBuilder
    {
        // ===== refMap 构建（u16 = (kind<<14)|hop；内嵌值类型折算拼入） =====

        internal readonly struct RefSite
        {
            public int Offset { get; }
            public int Kind { get; }
            public TypeLayoutPlan? Embedded { get; }

            public RefSite(int offset, int kind, TypeLayoutPlan? embedded)
            {
                Offset = offset;
                Kind = kind;
                Embedded = embedded;
            }
        }

        internal static void CollectRefSite(List<RefSite> refEntries, LayoutEngine.FieldTypeInfo info, int offset)
        {
            if (info.IsReferenceSlot)
            {
                refEntries.Add(new RefSite(offset, TypeLayout.RefMapKindFatRef, null));
            }
            else if (info.IsStringSlot)
            {
                refEntries.Add(new RefSite(offset, TypeLayout.RefMapKindString, null));
            }
            else if (info.EmbeddedPlan is { } embedded && embedded.RefMap.Length > 0)
            {
                refEntries.Add(new RefSite(offset, 0, embedded));
            }
        }

        internal static ushort[] BuildRefMap(IReadOnlyList<FieldPlan> fields,
            List<RefSite> refEntries, int scanStart)
        {
            var map = new List<ushort>();
            long cursor = scanStart;
            foreach (var site in refEntries)
            {
                if (site.Embedded == null)
                {
                    var hop = checked((int)((site.Offset - cursor) / LayoutEngine.ReferenceSlotSize));
                    map.Add(TypeLayout.EncodeRefMap(site.Kind, hop));
                    cursor = site.Offset + LayoutEngine.ReferenceSlotSize;
                    continue;
                }
                // 内嵌值类型：回放其 refMap（保留 kind，重算外层 hop）
                // 值类型扫描起点 = 字段偏移
                long innerPos = site.Offset;
                foreach (var entry in site.Embedded.RefMap)
                {
                    innerPos += (long)TypeLayout.RefMapHopOf(entry) * LayoutEngine.ReferenceSlotSize;
                    var hop = checked((int)((innerPos - cursor) / LayoutEngine.ReferenceSlotSize));
                    map.Add(TypeLayout.EncodeRefMap(TypeLayout.RefMapKindOf(entry), hop));
                    cursor = innerPos + LayoutEngine.ReferenceSlotSize;
                    innerPos += LayoutEngine.ReferenceSlotSize;
                }
            }
            return map.ToArray();
        }
    }
}
