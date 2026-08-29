using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 布局引擎瘦驱动（MW4）：MwSymbolTable → LayoutPlanTable。算法按职责
    /// 分文件：ClassLayout / ValueTypeLayout / VTablePlanner / RefMapBuilder /
    /// ConstructedLayout / LayoutShells / HiddenStoragePlanner。规则见各文件与 RUNTIME §2/§6–§9。
    /// </summary>
    public static class LayoutEngine
    {
        // 对象头 16B（TypeSheet* 8 + RC u32 + 位打包域 u32）
        public const int ObjectHeaderSize = 16;
        // 胖引用槽 16B/16B（与 TypeLayout.ReferenceSlotSize 同值）
        public const int ReferenceSlotSize = 16;
        // vtable 槽 0 合成标记（TypeSheetEmitter 按类型填 mw.init.dispatch.*）
        public const string InitDispatchSlot = "$mw.init.dispatch";

        public static LayoutPlanTable Build(MwSymbolTable symbols) =>
            Build(symbols, System.Array.Empty<string>(), null);

        public static LayoutPlanTable Build(MwSymbolTable symbols,
            IReadOnlyList<string> constructed) =>
            Build(symbols, constructed, null);

        public static LayoutPlanTable Build(MwSymbolTable symbols,
            IReadOnlyList<string> constructed, IReadOnlySet<string>? functionsWithBody,
            IReadOnlyDictionary<string, IReadOnlyList<(string Method, string Wrapper)>>?
                methodSlots = null)
        {
            var table = new LayoutPlanTable();
            foreach (var type in symbols.Types)
            {
                if (!type.IsExternal)
                {
                    Resolve(type, symbols, table, new HashSet<string>(System.StringComparer.Ordinal),
                        functionsWithBody, methodSlots);
                }
            }
            foreach (var typeRef in constructed)
            {
                ConstructedLayout.ResolveConstructed(typeRef, symbols, table,
                    new HashSet<string>(System.StringComparer.Ordinal), functionsWithBody);
            }
            return table;
        }

        internal static TypeLayoutPlan? Resolve(MwTypeSymbol type, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting, IReadOnlySet<string>? bodies,
            IReadOnlyDictionary<string, IReadOnlyList<(string Method, string Wrapper)>>?
                methodSlots = null)
        {
            if (table.Find(GenericAbi.PlanKey(type)) is { } existing)
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
                BilTypeKind.Class => ClassLayout.LayoutClass(type, symbols, table, visiting, bodies,
                    methodSlots),
                BilTypeKind.Struct => ValueTypeLayout.LayoutValueType(type, symbols, table, visiting,
                    isEnum: false, methodSlots),
                BilTypeKind.EnumStruct => ValueTypeLayout.LayoutValueType(type, symbols, table,
                    visiting, isEnum: true, methodSlots),
                BilTypeKind.Interface => LayoutShells.LayoutInterfaceShell(type, symbols),
                BilTypeKind.Wrapper => ValueTypeLayout.LayoutWrapper(type, symbols, table, visiting),
                _ => null,
            };
            visiting.Remove(type.Canonical);
            if (plan != null)
            {
                table.Add(plan);
            }
            return plan;
        }

        internal readonly struct FieldTypeInfo
        {
            public int Size { get; }
            public int Alignment { get; }
            public bool IsReferenceSlot { get; }
            public bool IsStringSlot { get; }
            public TypeLayoutPlan? EmbeddedPlan { get; }

            public FieldTypeInfo(int size, int alignment, bool isReferenceSlot,
                TypeLayoutPlan? embeddedPlan, bool isStringSlot = false)
            {
                Size = size;
                Alignment = alignment;
                IsReferenceSlot = isReferenceSlot;
                EmbeddedPlan = embeddedPlan;
                IsStringSlot = isStringSlot;
            }
        }

        internal static FieldTypeInfo ClassifyFieldType(string typeRef, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting)
        {
            var type = MirType.Of(typeRef);
            switch (type.Key)
            {
                case "bool" or "i8" or "u8": return new FieldTypeInfo(1, 1, false, null);
                case "char" or "i16" or "u16": return new FieldTypeInfo(2, 2, false, null);
                case "i32" or "u32" or "float": return new FieldTypeInfo(4, 4, false, null);
                case "i64" or "u64" or "double": return new FieldTypeInfo(8, 8, false, null);
                case "String": return new FieldTypeInfo(16, 16, false, null, isStringSlot: true);
            }
            if (symbols.FindType(type.Canonical) is { IsExternal: false } local
                && local.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct
                    or BilTypeKind.Wrapper)
            {
                var plan = Resolve(local, symbols, table, visiting, null)!;
                return new FieldTypeInfo(plan.Size, plan.Alignment, false, plan);
            }
            return new FieldTypeInfo(ReferenceSlotSize, ReferenceSlotSize, true, null);
        }

        internal static IEnumerable<MwMemberSymbol> InstanceFields(MwTypeSymbol type)
        {
            foreach (var member in type.Members)
            {
                if (member.Declaration.Kind == BilMemberKind.Field)
                {
                    yield return member;
                }
            }
        }

        internal static IEnumerable<MwMemberSymbol> InstanceMethods(MwTypeSymbol type)
        {
            foreach (var member in type.Members)
            {
                if (member.IsVirtualMember)
                {
                    yield return member;
                }
            }
        }

        internal static string FieldTypeOf(MwMemberSymbol field)
        {
            var at = field.Canonical.IndexOf('@');
            return at < 0
                ? throw new CompilerInternalException($"字段符号缺类型段: {field.Canonical}")
                : field.Canonical.Substring(at + 1);
        }

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

        internal static uint TypeFlagsOf(MwTypeSymbol type)
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
            if (type.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct
                or BilTypeKind.Wrapper)
            {
                flags |= TypeLayoutPlan.FlagInlineValue;
            }
            return flags;
        }

        internal static int AlignUp(int offset, int alignment) =>
            (offset + alignment - 1) / alignment * alignment;
    }
}
