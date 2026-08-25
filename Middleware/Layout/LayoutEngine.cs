using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 布局引擎（MW4 批 1）：MwSymbolTable → LayoutPlanTable。每非
    /// External 的 class/struct/enum-struct 一份计划；interface/wrapper
    /// 无实例不布局。规则（RUNTIME §2/§6–§9 与 MW4 定稿）：
    /// - class：16B 对象头起排实例字段，基类字段在前；16B 对齐；
    /// - 值类型：偏移 0 起（enum 偏移 0 恒 u32 隐藏判别字段），字段
    ///   自然对齐，尾 padding 到最大对齐；
    /// - 引用类型字段/nullable 占 16B 胖引用槽（进 refMap）；String 按
    ///   过渡 ABI {i8*,i64} 16B 内联（非 rich，不进 refMap）；本地
    ///   struct/enum 字段按自身布局内联（递归）；其余未解析/构造类型
    ///   一律按胖引用槽（泛型具化布局随 MW4 后续批）；
    /// - vtable：基类继承槽 → 本类自有槽 → 各 interface 实现段；
    ///   override（签名键命中）复用基槽；
    /// - refMap：128-bit 槽粒度跳数；内嵌 rich 值类型字段的引用发射期
    ///   折算拼入（§8 加载期扁平化的编译期等价）。
    /// </summary>
    public static class LayoutEngine
    {
        // 对象头 16B（TypeSheet* 8 + RC u32 + 位打包域 u32）
        public const int ObjectHeaderSize = 16;
        // 胖引用槽 16B/16B（与 TypeLayout.ReferenceSlotSize 同值）
        public const int ReferenceSlotSize = 16;

        public static LayoutPlanTable Build(MwSymbolTable symbols)
        {
            var table = new LayoutPlanTable();
            foreach (var type in symbols.Types)
            {
                if (!type.IsExternal)
                {
                    Resolve(type, symbols, table, new HashSet<string>(System.StringComparer.Ordinal));
                }
            }
            return table;
        }

        private static TypeLayoutPlan? Resolve(MwTypeSymbol type, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting)
        {
            if (table.Find(type.Canonical) is { } existing)
            {
                return existing;
            }
            // struct 内联递归的循环包含属生成方违约（值类型不能自含）；防御
            if (!visiting.Add(type.Canonical))
            {
                throw new CompilerInternalException($"类型布局循环依赖: {type.Canonical}");
            }
            TypeLayoutPlan? plan = type.Declaration.Kind switch
            {
                BilTypeKind.Class => LayoutClass(type, symbols, table, visiting),
                BilTypeKind.Struct => LayoutValueType(type, symbols, table, visiting, isEnum: false),
                BilTypeKind.EnumStruct => LayoutValueType(type, symbols, table, visiting, isEnum: true),
                BilTypeKind.Interface => LayoutInterfaceShell(type),
                _ => null,
            };
            visiting.Remove(type.Canonical);
            if (plan != null)
            {
                table.Add(plan);
            }
            return plan;
        }

        // ===== class =====

        private static TypeLayoutPlan LayoutClass(MwTypeSymbol type, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting)
        {
            // 基类计划（本地 class 基类可解析时；内建/外部基类无字段布局）
            TypeLayoutPlan? basePlan = null;
            if (type.Declaration.ExtendsType is { } baseRef
                && symbols.FindType(baseRef) is { IsExternal: false } baseType
                && baseType.Declaration.Kind == BilTypeKind.Class)
            {
                basePlan = Resolve(baseType, symbols, table, visiting);
            }

            // 字段：基类字段在前，本类字段从基类 Size（含头、已 16 对齐）续排
            var fields = new List<FieldPlan>();
            var offset = ObjectHeaderSize;
            if (basePlan != null)
            {
                fields.AddRange(basePlan.Fields);
                offset = basePlan.Size;
            }
            var refEntries = new List<RefSite>();
            foreach (var member in InstanceFields(type))
            {
                var info = ClassifyFieldType(FieldTypeOf(member), symbols, table, visiting);
                offset = AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(member.Canonical, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan));
                if (info.IsReferenceSlot)
                {
                    refEntries.Add(new RefSite(offset, null));
                }
                else if (info.EmbeddedPlan is { TypeFlags: var flags } embedded
                    && (flags & TypeLayoutPlan.FlagRich) != 0 && embedded.RefMap.Length > 0)
                {
                    refEntries.Add(new RefSite(offset, embedded));
                }
                offset += info.Size;
            }
            var size = AlignUp(offset, ReferenceSlotSize);

            // vtable：基类槽继承（override 复用基槽）→ 本类自有槽 →
            // interface 实现段
            var slots = new List<string>();
            if (basePlan != null)
            {
                slots.AddRange(basePlan.VTableSlots);
            }
            foreach (var member in InstanceMethods(type))
            {
                var key = member.SignatureKey;
                var inherited = slots.FindIndex(s => KeyOf(symbols, s) == key);
                if (inherited >= 0 && member.HasKeyword(BilKeyword.Override))
                {
                    slots[inherited] = member.Canonical;
                }
                else
                {
                    slots.Add(member.Canonical);
                }
            }
            var iMap = new List<(string, int)>();
            foreach (var ifaceRef in type.Declaration.ImplementsTypes)
            {
                // 外部/泛型构造接口：其 TypeSheet 随 stdlib sheet 实体化与
                // 泛型具化后补；本批只为可解析的本地非泛型接口生成 iMap 段
                if (ifaceRef.Contains('<')
                    || symbols.FindType(ifaceRef) is not { IsExternal: false } ifaceType
                    || ifaceType.Declaration.Kind != BilTypeKind.Interface)
                {
                    continue;
                }
                var baseOffset = slots.Count;
                foreach (var ifaceMethod in InstanceMethods(ifaceType))
                {
                    var key = ifaceMethod.SignatureKey;
                    var impl = slots.Find(s => KeyOf(symbols, s) == key)
                        ?? throw new MwNotSupportedException(
                            $"MW4 接口方法未实现: {ifaceMethod.Canonical}（{type.Canonical}）");
                    slots.Add(impl);
                }
                iMap.Add((ifaceType.Canonical, baseOffset));
            }

            return new TypeLayoutPlan(type, TypeLayoutKind.Class, size, ReferenceSlotSize,
                TypeFlagsOf(type), fields, slots, iMap,
                BuildRefMap(fields, refEntries, ObjectHeaderSize),
                System.Array.Empty<(MwCaseSymbol, uint)>(), basePlan);
        }

        // ===== interface（空壳：iMap 键地址 + 接口内槽序表） =====

        private static TypeLayoutPlan LayoutInterfaceShell(MwTypeSymbol type)
        {
            var slots = new List<string>();
            foreach (var member in InstanceMethods(type))
            {
                slots.Add(member.Canonical);
            }
            return new TypeLayoutPlan(type, TypeLayoutKind.Interface, 0, 1,
                TypeFlagsOf(type), System.Array.Empty<FieldPlan>(), slots,
                System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                System.Array.Empty<(MwCaseSymbol, uint)>(), null);
        }

        // ===== struct / enum-struct =====

        private static TypeLayoutPlan LayoutValueType(MwTypeSymbol type, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting, bool isEnum)
        {
            var fields = new List<FieldPlan>();
            // enum：偏移 0 恒 u32 隐藏判别字段（占 4B），实例字段续排
            var offset = isEnum ? 4 : 0;
            var alignment = isEnum ? 4 : 1;
            var refEntries = new List<RefSite>();
            var rich = HasKeyword(type, BilKeyword.Rich);
            foreach (var member in InstanceFields(type))
            {
                var info = ClassifyFieldType(FieldTypeOf(member), symbols, table, visiting);
                offset = AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(member.Canonical, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan));
                if (rich && info.IsReferenceSlot)
                {
                    refEntries.Add(new RefSite(offset, null));
                }
                else if (rich && info.EmbeddedPlan is { TypeFlags: var flags } embedded
                    && (flags & TypeLayoutPlan.FlagRich) != 0 && embedded.RefMap.Length > 0)
                {
                    refEntries.Add(new RefSite(offset, embedded));
                }
                offset += info.Size;
                if (info.Alignment > alignment)
                {
                    alignment = info.Alignment;
                }
            }
            var size = AlignUp(offset, alignment);

            var enumCases = new List<(MwCaseSymbol, uint)>();
            if (isEnum)
            {
                foreach (var caseSymbol in type.Cases)
                {
                    enumCases.Add((caseSymbol, caseSymbol.Discriminant));
                }
            }
            return new TypeLayoutPlan(type, isEnum ? TypeLayoutKind.Enum : TypeLayoutKind.Struct,
                size, alignment, TypeFlagsOf(type), fields,
                System.Array.Empty<string>(), System.Array.Empty<(string, int)>(),
                rich ? BuildRefMap(fields, refEntries, 0) : System.Array.Empty<ushort>(),
                enumCases, null);
        }

        // ===== 字段类型归类 =====

        private readonly struct FieldTypeInfo
        {
            public int Size { get; }
            public int Alignment { get; }
            public bool IsReferenceSlot { get; }
            public TypeLayoutPlan? EmbeddedPlan { get; }

            public FieldTypeInfo(int size, int alignment, bool isReferenceSlot,
                TypeLayoutPlan? embeddedPlan)
            {
                Size = size;
                Alignment = alignment;
                IsReferenceSlot = isReferenceSlot;
                EmbeddedPlan = embeddedPlan;
            }
        }

        private static FieldTypeInfo ClassifyFieldType(string typeRef, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting)
        {
            var type = MirType.Of(typeRef);
            switch (type.Key)
            {
                case "bool" or "i8" or "u8": return new FieldTypeInfo(1, 1, false, null);
                case "char" or "i16" or "u16": return new FieldTypeInfo(2, 2, false, null);
                case "i32" or "u32" or "float": return new FieldTypeInfo(4, 4, false, null);
                case "i64" or "u64" or "double": return new FieldTypeInfo(8, 8, false, null);
                // String 过渡 ABI {i8*,i64} 内联（非 rich，不进 refMap）
                case "String": return new FieldTypeInfo(16, 8, false, null);
            }
            // 本地值类型（struct/enum）按自身布局内联（递归）
            if (symbols.FindType(type.Canonical) is { IsExternal: false } local
                && local.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct)
            {
                var plan = Resolve(local, symbols, table, visiting)!;
                return new FieldTypeInfo(plan.Size, plan.Alignment, false, plan);
            }
            // 其余一律胖引用槽：class/interface 引用、nullable、外部/构造
            // 类型（泛型具化与外部值类型布局随 MW4 后续批）
            return new FieldTypeInfo(ReferenceSlotSize, ReferenceSlotSize, true, null);
        }

        // ===== refMap 构建（128-bit 槽粒度跳数；内嵌 rich 折算拼入） =====

        private readonly struct RefSite
        {
            public int Offset { get; }
            public TypeLayoutPlan? Embedded { get; }

            public RefSite(int offset, TypeLayoutPlan? embedded)
            {
                Offset = offset;
                Embedded = embedded;
            }
        }

        private static ushort[] BuildRefMap(IReadOnlyList<FieldPlan> fields,
            List<RefSite> refEntries, int scanStart)
        {
            var map = new List<ushort>();
            long cursor = scanStart;
            foreach (var site in refEntries)
            {
                if (site.Embedded == null)
                {
                    map.Add(checked((ushort)((site.Offset - cursor) / ReferenceSlotSize)));
                    cursor = site.Offset + ReferenceSlotSize;
                    continue;
                }
                // 内嵌 rich 值类型：回放其 refMap 跳数，把每个内层引用
                // 折算为外层槽距（值类型扫描起点 = 字段偏移）
                long innerPos = site.Offset;
                foreach (var skip in site.Embedded.RefMap)
                {
                    innerPos += (long)skip * ReferenceSlotSize;
                    map.Add(checked((ushort)((innerPos - cursor) / ReferenceSlotSize)));
                    cursor = innerPos + ReferenceSlotSize;
                    innerPos += ReferenceSlotSize;
                }
            }
            return map.ToArray();
        }

        // ===== 辅助 =====

        private static IEnumerable<MwMemberSymbol> InstanceFields(MwTypeSymbol type)
        {
            foreach (var member in type.Members)
            {
                if (member.Declaration.Kind == BilMemberKind.Field)
                {
                    yield return member;
                }
            }
        }

        // 实例虚成员（init/ext/static 与除 $$call 外的运算符不进 vtable，
        // VM 同口径）
        private static IEnumerable<MwMemberSymbol> InstanceMethods(MwTypeSymbol type)
        {
            foreach (var member in type.Members)
            {
                if (member.IsVirtualMember)
                {
                    yield return member;
                }
            }
        }

        // 字段 canonical 的类型段：X#x@.i32 → .i32
        private static string FieldTypeOf(MwMemberSymbol field)
        {
            var at = field.Canonical.IndexOf('@');
            return at < 0
                ? throw new CompilerInternalException($"字段符号缺类型段: {field.Canonical}")
                : field.Canonical.Substring(at + 1);
        }

        // 槽内 canonical 的签名键（经符号表成员反查；槽内符号恒已登记）
        private static string KeyOf(MwSymbolTable symbols, string canonical) =>
            symbols.FindMember(canonical)!.SignatureKey;

        private static bool HasKeyword(MwTypeSymbol type, BilKeyword keyword)
        {
            foreach (var modifier in type.Declaration.Modifiers)
            {
                if (modifier is BilKeywordModifier keywordModifier
                    && keywordModifier.Keyword == keyword)
                {
                    return true;
                }
            }
            return false;
        }

        // RICH/SHARED 从声明修饰符映射；DISPOSABLE 需 stdlib IDisposable
        // 接口知识，本批恒 0（随批 2/3 判定）
        private static uint TypeFlagsOf(MwTypeSymbol type)
        {
            var flags = 0u;
            if (HasKeyword(type, BilKeyword.Rich))
            {
                flags |= TypeLayoutPlan.FlagRich;
            }
            if (HasKeyword(type, BilKeyword.Shared))
            {
                flags |= TypeLayoutPlan.FlagShared;
            }
            if (type.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct)
            {
                flags |= TypeLayoutPlan.FlagInlineValue;
            }
            return flags;
        }

        private static int AlignUp(int offset, int alignment) =>
            (offset + alignment - 1) / alignment * alignment;
    }
}
