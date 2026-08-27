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
    /// - 引用类型字段/nullable 占 16B 胖引用槽（进 refMap kind0）；String
    ///   按 {i8*,i64} 16B/16 对齐内联并进 refMap kind1；本地 struct/enum 字段按
    ///   自身布局内联（递归，内层 refMap 折算拼入）；其余未解析/构造类型
    ///   一律按胖引用槽（泛型具化布局随 MW4 后续批）；
    /// - vtable：槽 0 = $mw.init.dispatch（per-类型 init 分发器）→
    ///   基类继承槽 → 本类自有槽 → 各 interface 实现段；
    ///   override（签名键命中）复用基槽；构造类型复用模板槽序，槽 0
    ///   内容按具化 canonical 各自填分发器（发射期）；
    ///   struct（非 enum）同样挂槽 0 一元表、无 iMap；enum 不挂；
    /// - refMap：u16 = (kind<<14)|hop，跳数单位 16B；内嵌值类型字段的
    ///   引用/String 发射期折算拼入（§8 加载期扁平化的编译期等价）。
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
            IReadOnlyList<string> constructed, IReadOnlySet<string>? functionsWithBody)
        {
            var table = new LayoutPlanTable();
            foreach (var type in symbols.Types)
            {
                if (!type.IsExternal)
                {
                    Resolve(type, symbols, table, new HashSet<string>(System.StringComparer.Ordinal),
                        functionsWithBody);
                }
            }
            foreach (var typeRef in constructed)
            {
                ResolveConstructed(typeRef, symbols, table,
                    new HashSet<string>(System.StringComparer.Ordinal), functionsWithBody);
            }
            return table;
        }

        private static TypeLayoutPlan? Resolve(MwTypeSymbol type, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting, IReadOnlySet<string>? bodies)
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
                BilTypeKind.Class => LayoutClass(type, symbols, table, visiting, bodies),
                BilTypeKind.Struct => LayoutValueType(type, symbols, table, visiting, isEnum: false),
                BilTypeKind.EnumStruct => LayoutValueType(type, symbols, table, visiting, isEnum: true),
                BilTypeKind.Interface => LayoutInterfaceShell(type, symbols),
                BilTypeKind.Wrapper => LayoutWrapperShell(type),
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
            LayoutPlanTable table, HashSet<string> visiting, IReadOnlySet<string>? bodies)
        {
            // 基类计划（本地 class 基类可解析时；内建/外部基类无字段布局）。
            // 泛型继承 extends B<T> 经 FindTypeByRef 命中模板 B
            TypeLayoutPlan? basePlan = null;
            if (type.Declaration.ExtendsType is { } baseRef
                && symbols.FindTypeByRef(baseRef) is { IsExternal: false } baseType
                && baseType.Declaration.Kind == BilTypeKind.Class)
            {
                basePlan = Resolve(baseType, symbols, table, visiting, bodies);
            }

            // 字段：基类字段在前，本类字段从基类 Size（含头、已 16 对齐）续排
            var fields = new List<FieldPlan>();
            var offset = ObjectHeaderSize;
            if (basePlan != null)
            {
                fields.AddRange(basePlan.Fields);
                offset = basePlan.Size;
            }
            // 类级隐藏 typeid：紧随对象头/基类之后、用户字段之前；不进 refMap
            var hiddenSlots = new List<(string, int)>();
            foreach (var param in type.Declaration.GenericParameters)
            {
                offset = AlignUp(offset, GenericAbi.TypeIdSlotAlign);
                fields.Add(new FieldPlan(GenericAbi.HiddenFieldSymbol(type.Canonical, param),
                    offset, GenericAbi.TypeIdSlotSize, GenericAbi.TypeIdSlotAlign,
                    isReferenceSlot: false, embeddedPlan: null, isHiddenTypeId: true));
                hiddenSlots.Add((param, offset));
                offset += GenericAbi.TypeIdSlotSize;
            }
            var refEntries = new List<RefSite>();
            // MW9b-G：基类托管位点一并回放进本类 refMap——rigi_destruct
            // 只扫对象自身 sheet 的 refMap（不走 baseTypeId 链），继承的
            // String/胖引用字段漏收会在析构时泄漏（core 异常子类继承
            // Exception.message 是首个触发者）。基类 refMap 的内嵌值类型
            // 已折算平坦，按绝对偏移重建位点即可
            if (basePlan != null)
            {
                var basePos = (long)ObjectHeaderSize;
                foreach (var entry in basePlan.RefMap)
                {
                    basePos += (long)TypeLayout.RefMapHopOf(entry) * ReferenceSlotSize;
                    refEntries.Add(new RefSite((int)basePos, TypeLayout.RefMapKindOf(entry), null));
                    basePos += ReferenceSlotSize;
                }
            }
            foreach (var member in InstanceFields(type))
            {
                var info = ClassifyFieldType(FieldTypeOf(member), symbols, table, visiting);
                offset = AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(member.Canonical, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan, isStringSlot: info.IsStringSlot));
                CollectRefSite(refEntries, info, offset);
                offset += info.Size;
            }
            var size = AlignUp(offset, ReferenceSlotSize);

            // vtable：槽 0 分发器 → 基类槽继承（override 复用基槽）→
            // 本类自有槽 → interface 实现段
            var slots = new List<string>();
            if (basePlan != null)
            {
                slots.AddRange(basePlan.VTableSlots);
            }
            foreach (var member in InstanceMethods(type))
            {
                var key = member.SignatureKey;
                var inherited = slots.FindIndex(s => CompatibleSignature(symbols, s, key));
                if (inherited >= 0 && member.HasKeyword(BilKeyword.Override))
                {
                    slots[inherited] = member.Canonical;
                }
                else
                {
                    slots.Add(member.Canonical);
                }
            }
            EnsureInitDispatchSlot(slots);
            var iMap = new List<(string, int)>();
            foreach (var ifaceRef in type.Declaration.ImplementsTypes)
            {
                AppendInterfaceSegment(slots, iMap, ifaceRef, symbols, table, type.Canonical,
                    bodies);
            }

            return new TypeLayoutPlan(type, TypeLayoutKind.Class, size, ReferenceSlotSize,
                TypeFlagsOf(type), fields, slots, iMap,
                BuildRefMap(fields, refEntries, ObjectHeaderSize),
                System.Array.Empty<(MwCaseSymbol, uint)>(), basePlan, hiddenSlots,
                CollectIfaceClosure(type.Canonical, type, symbols));
        }

        // 闭合构造类型具化计划：字段/vtable 前缀/refMap 复用模板；iMap 按
        // 代入后的构造接口重生；Symbol 与基类链按构造 canonical 独立入表
        private static TypeLayoutPlan? ResolveConstructed(string typeRef, MwSymbolTable symbols,
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
            var templatePlan = Resolve(template, symbols, table,
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
                    CollectIfaceClosure(canonical, template, symbols));
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
                    basePlan = Resolve(baseType, symbols, table,
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
            EnsureInitDispatchSlot(slots);
            var iMap = new List<(string, int)>();
            foreach (var ifaceRef in template.Declaration.ImplementsTypes)
            {
                var substituted = MwTypeKey.Normalize(
                    ConstructedTypeCollector.Substitute(ifaceRef, subst));
                AppendInterfaceSegment(slots, iMap, substituted, symbols, table, canonical,
                    bodies);
            }
            visiting.Remove(canonical);
            var constructed = new MwTypeSymbol(canonical, template);
            var plan = new TypeLayoutPlan(constructed,
                TypeLayoutKind.Class, templatePlan.Size, templatePlan.Alignment,
                templatePlan.TypeFlags, templatePlan.Fields, slots,
                iMap, templatePlan.RefMap, templatePlan.EnumCases,
                basePlan, templatePlan.HiddenTypeIdSlots,
                CollectIfaceClosure(canonical, constructed, symbols));
            table.Add(plan);
            return plan;
        }

        // .typeid<X> 构造 sheet：值类型、typeSize 8、FlagInlineValue、
        // 各表空。VM TypesAssignable 对 Type<X>/Type<Any> 不变（无 core::Type
        // 声明、无协变）→ baseTypeId 不指向无界成员。
        private static TypeLayoutPlan? SynthesizeTypeIdPlan(string canonical,
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
        private static TypeLayoutPlan? SynthesizeSpanPlan(string canonical,
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
                TypeLayoutKind.Class, TypeLayout.ArrayPrefixSize, ReferenceSlotSize,
                flags, System.Array.Empty<FieldPlan>(),
                new[] { InitDispatchSlot },
                System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                System.Array.Empty<(MwCaseSymbol, uint)>(), null);
            table.Add(plan);
            visiting.Remove(canonical);
            return plan;
        }

        // ===== interface（空壳：iMap 键地址 + 接口内槽序表） =====

        private static TypeLayoutPlan LayoutInterfaceShell(MwTypeSymbol type, MwSymbolTable symbols)
        {
            var slots = new List<string>();
            foreach (var member in InstanceMethods(type))
            {
                slots.Add(member.Canonical);
            }
            return new TypeLayoutPlan(type, TypeLayoutKind.Interface, 0, 1,
                TypeFlagsOf(type), System.Array.Empty<FieldPlan>(), slots,
                System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                System.Array.Empty<(MwCaseSymbol, uint)>(), null,
                ifaceClosure: CollectIfaceClosure(type.Canonical, type, symbols));
        }

        // wrapper 空壳：仅 TypeSheet 地址身份（type.with / TypeInfo.wrappers）
        private static TypeLayoutPlan LayoutWrapperShell(MwTypeSymbol type)
        {
            return new TypeLayoutPlan(type, TypeLayoutKind.Wrapper, 0, 1,
                TypeFlagsOf(type), System.Array.Empty<FieldPlan>(),
                System.Array.Empty<string>(), System.Array.Empty<(string, int)>(),
                System.Array.Empty<ushort>(), System.Array.Empty<(MwCaseSymbol, uint)>(),
                null);
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
            foreach (var member in InstanceFields(type))
            {
                var info = ClassifyFieldType(FieldTypeOf(member), symbols, table, visiting);
                offset = AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(member.Canonical, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan, isStringSlot: info.IsStringSlot));
                CollectRefSite(refEntries, info, offset);
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
            // struct 挂 vtable 槽 0 = init 分发器（无继承、无 iMap，槽表
            // 恒一元）；enum 不插槽 0——调用点 vTable null 兜底 abort
            var slots = new List<string>();
            if (!isEnum)
            {
                EnsureInitDispatchSlot(slots);
            }
            return new TypeLayoutPlan(type, isEnum ? TypeLayoutKind.Enum : TypeLayoutKind.Struct,
                size, alignment, TypeFlagsOf(type), fields,
                slots, System.Array.Empty<(string, int)>(),
                BuildRefMap(fields, refEntries, 0),
                enumCases, null,
                ifaceClosure: CollectIfaceClosure(type.Canonical, type, symbols));
        }

        // ===== 字段类型归类 =====

        private readonly struct FieldTypeInfo
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
                // String 槽 {i8*,i64} 内联 16B/16 对齐，进 refMap kind1
                //（跳数单位 16B，必须落在槽边界，避免 enum/i32 前缀把 data 扫到判别字）
                case "String": return new FieldTypeInfo(16, 16, false, null, isStringSlot: true);
            }
            // 本地值类型（struct/enum）按自身布局内联（递归）
            if (symbols.FindType(type.Canonical) is { IsExternal: false } local
                && local.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct)
            {
                var plan = Resolve(local, symbols, table, visiting, null)!;
                return new FieldTypeInfo(plan.Size, plan.Alignment, false, plan);
            }
            // 其余一律胖引用槽：class/interface 引用、nullable、外部/构造
            // 类型（泛型具化与外部值类型布局随 MW4 后续批）
            return new FieldTypeInfo(ReferenceSlotSize, ReferenceSlotSize, true, null);
        }

        // ===== refMap 构建（u16 = (kind<<14)|hop；内嵌值类型折算拼入） =====

        private readonly struct RefSite
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

        private static void CollectRefSite(List<RefSite> refEntries, FieldTypeInfo info, int offset)
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

        private static ushort[] BuildRefMap(IReadOnlyList<FieldPlan> fields,
            List<RefSite> refEntries, int scanStart)
        {
            var map = new List<ushort>();
            long cursor = scanStart;
            foreach (var site in refEntries)
            {
                if (site.Embedded == null)
                {
                    var hop = checked((int)((site.Offset - cursor) / ReferenceSlotSize));
                    map.Add(TypeLayout.EncodeRefMap(site.Kind, hop));
                    cursor = site.Offset + ReferenceSlotSize;
                    continue;
                }
                // 内嵌值类型：回放其 refMap（保留 kind，重算外层 hop）
                // 值类型扫描起点 = 字段偏移
                long innerPos = site.Offset;
                foreach (var entry in site.Embedded.RefMap)
                {
                    innerPos += (long)TypeLayout.RefMapHopOf(entry) * ReferenceSlotSize;
                    var hop = checked((int)((innerPos - cursor) / ReferenceSlotSize));
                    map.Add(TypeLayout.EncodeRefMap(TypeLayout.RefMapKindOf(entry), hop));
                    cursor = innerPos + ReferenceSlotSize;
                    innerPos += ReferenceSlotSize;
                }
            }
            return map.ToArray();
        }

        // ===== 辅助 =====

        // 构造/非构造接口 iMap 段：闭合构造以具化 canonical 为键并确保
        // 接口空壳计划入表；开放构造（模板上的 I<T>）仍跳过
        private static void AppendInterfaceSegment(List<string> slots, List<(string, int)> iMap,
            string ifaceRef, MwSymbolTable symbols, LayoutPlanTable table, string ownerCanonical,
            IReadOnlySet<string>? bodies)
        {
            var normalized = MwTypeKey.Normalize(ifaceRef);
            if (ConstructedTypeCollector.IsConstructed(normalized)
                && !GenericAbi.IsClosedConstructed(normalized))
            {
                return;
            }
            if (symbols.FindTypeByRef(normalized) is not { IsExternal: false } ifaceType
                || ifaceType.Declaration.Kind != BilTypeKind.Interface)
            {
                return;
            }
            var imapKey = GenericAbi.IsClosedConstructed(normalized)
                ? normalized
                : ifaceType.Canonical;
            if (GenericAbi.IsClosedConstructed(normalized))
            {
                EnsureConstructedInterfacePlan(normalized, ifaceType, symbols, table);
            }
            var subst = ConstructedTypeCollector.BuildSubstitution(normalized, ifaceType.Declaration);
            var baseOffset = slots.Count;
            foreach (var ifaceMethod in InstanceMethods(ifaceType))
            {
                var impl = FindInterfaceImpl(slots, symbols, ifaceMethod, subst)
                    ?? DefaultMethodOf(ifaceMethod, bodies)
                    ?? throw new MwNotSupportedException(
                        $"MW4 接口方法未实现: {ifaceMethod.Canonical}（{ownerCanonical}）");
                slots.Add(impl);
            }
            iMap.Add((imapKey, baseOffset));
            // 前端 invoke 常把 I<i32> 擦成模板 I；补模板键别名使
            // TypeSheetFor(I) 与具化键同槽（VM 亦按声明键而非具化键）
            if (imapKey != ifaceType.Canonical)
            {
                iMap.Add((ifaceType.Canonical, baseOffset));
            }
        }

        private static void EnsureConstructedInterfacePlan(string canonical, MwTypeSymbol template,
            MwSymbolTable symbols, LayoutPlanTable table)
        {
            if (table.Find(canonical) != null)
            {
                return;
            }
            var templatePlan = table.Find(GenericAbi.PlanKey(template));
            if (templatePlan == null || templatePlan.Kind != TypeLayoutKind.Interface)
            {
                return;
            }
            table.Add(new TypeLayoutPlan(new MwTypeSymbol(canonical, template),
                TypeLayoutKind.Interface, 0, 1, templatePlan.TypeFlags,
                templatePlan.Fields, templatePlan.VTableSlots,
                System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                templatePlan.EnumCases, null, ifaceClosure:
                CollectIfaceClosure(canonical, template, symbols)));
        }

        // 接口默认方法：模块内有 fn 体则 iMap 槽指向接口方法自身（VM 同口径）
        private static string? DefaultMethodOf(MwMemberSymbol ifaceMethod,
            IReadOnlySet<string>? bodies) =>
            bodies != null && bodies.Contains(ifaceMethod.Canonical)
                ? ifaceMethod.Canonical
                : null;

        // 传递 implements 闭包（前端 OverrideChecker.InterfaceClosure 同口径）：
        // 沿宿主/基类链收 implements，再沿接口 ExtendsType+ImplementsTypes 展开。
        // 开放构造（I<T>）跳过；闭合构造保留具化键。
        private static IReadOnlyList<string> CollectIfaceClosure(string typeRef,
            MwTypeSymbol type, MwSymbolTable symbols)
        {
            var result = new List<string>();
            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            var stack = new Stack<(string Ref, MwTypeSymbol Type)>();
            ConsiderHosts(typeRef, type, symbols, visited, result, stack);
            while (stack.Count > 0)
            {
                var (ifaceRef, ifaceType) = stack.Pop();
                var subst = ConstructedTypeCollector.BuildSubstitution(ifaceRef,
                    ifaceType.Declaration);
                if (ifaceType.Declaration.ExtendsType is { } extends)
                {
                    ConsiderIface(ConstructedTypeCollector.Substitute(extends, subst),
                        symbols, visited, result, stack);
                }
                foreach (var parent in ifaceType.Declaration.ImplementsTypes)
                {
                    ConsiderIface(ConstructedTypeCollector.Substitute(parent, subst),
                        symbols, visited, result, stack);
                }
            }
            return result;
        }

        private static void ConsiderHosts(string typeRef, MwTypeSymbol type,
            MwSymbolTable symbols, HashSet<string> visited, List<string> result,
            Stack<(string, MwTypeSymbol)> stack)
        {
            var currentRef = typeRef;
            var current = type;
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            while (current != null && seen.Add(MwTypeKey.Normalize(currentRef)))
            {
                var subst = ConstructedTypeCollector.BuildSubstitution(currentRef,
                    current.Declaration);
                if (current.Declaration.Kind == BilTypeKind.Interface
                    && current.Declaration.ExtendsType is { } selfExtends)
                {
                    ConsiderIface(ConstructedTypeCollector.Substitute(selfExtends, subst),
                        symbols, visited, result, stack);
                }
                foreach (var iface in current.Declaration.ImplementsTypes)
                {
                    ConsiderIface(ConstructedTypeCollector.Substitute(iface, subst),
                        symbols, visited, result, stack);
                }
                if (current.Declaration.Kind == BilTypeKind.Interface
                    || current.Declaration.ExtendsType is not { } baseRef)
                {
                    break;
                }
                currentRef = MwTypeKey.Normalize(
                    ConstructedTypeCollector.Substitute(baseRef, subst));
                current = symbols.FindTypeByRef(currentRef);
            }
        }

        private static void ConsiderIface(string typeRef, MwSymbolTable symbols,
            HashSet<string> visited, List<string> result,
            Stack<(string, MwTypeSymbol)> stack)
        {
            var normalized = MwTypeKey.Normalize(typeRef);
            if (ConstructedTypeCollector.IsConstructed(normalized)
                && !GenericAbi.IsClosedConstructed(normalized))
            {
                return;
            }
            if (symbols.FindTypeByRef(normalized) is not { } ifaceType
                || ifaceType.Declaration.Kind != BilTypeKind.Interface)
            {
                return;
            }
            var key = GenericAbi.IsClosedConstructed(normalized)
                ? normalized
                : ifaceType.Canonical;
            if (!visited.Add(key))
            {
                return;
            }
            result.Add(key);
            stack.Push((key, ifaceType));
        }

        // 先模板签名直中（G<T>:I<T> 槽仍是 .generic 占位）；再代入+归一
        // （C:I<i32> 的 pick(x:.i32) 对 I.pick(x:T)）
        private static string? FindInterfaceImpl(List<string> slots, MwSymbolTable symbols,
            MwMemberSymbol ifaceMethod, Dictionary<string, string>? subst)
        {
            var raw = ifaceMethod.SignatureKey;
            var exact = slots.Find(s => s != InitDispatchSlot && KeyOf(symbols, s) == raw);
            if (exact != null)
            {
                return exact;
            }
            var want = NormalizeSignatureKey(ConstructedTypeCollector.Substitute(raw, subst));
            return slots.Find(s => s != InitDispatchSlot
                && NormalizeSignatureKey(KeyOf(symbols, s)) == want);
        }

        private static string NormalizeSignatureKey(string key)
        {
            var open = key.IndexOf('(');
            var close = key.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                return key;
            }
            var inner = key.Substring(open + 1, close - open - 1);
            if (inner.Length == 0)
            {
                return key;
            }
            var parts = BilVerificationContext.SplitTopLevel(inner);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var colon = part.IndexOf(':');
                parts[i] = colon < 0
                    ? MwTypeKey.Normalize(part)
                    : part.Substring(0, colon + 1) + MwTypeKey.Normalize(part.Substring(colon + 1));
            }
            return key.Substring(0, open + 1) + string.Join(",", parts) + key.Substring(close);
        }

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

        // 槽 0 插入合成标记；基类已带则不重复（发射期按本类型填分发器）
        private static void EnsureInitDispatchSlot(List<string> slots)
        {
            if (slots.Count > 0 && slots[0] == InitDispatchSlot)
            {
                return;
            }
            slots.Insert(0, InitDispatchSlot);
        }

        // 槽内 canonical 的签名键（经符号表成员反查；槽内符号恒已登记）
        private static string KeyOf(MwSymbolTable symbols, string canonical) =>
            symbols.FindMember(canonical)!.SignatureKey;

        // override 匹配：精确签名键，或名+顶层参数个数（具化 $$call(x:.i32)
        // 对 Func$$call(arg0:.generic<T0>)）
        private static bool CompatibleSignature(MwSymbolTable symbols, string slotCanonical,
            string memberKey)
        {
            if (slotCanonical == InitDispatchSlot)
            {
                return false;
            }
            var slotKey = KeyOf(symbols, slotCanonical);
            if (slotKey == memberKey)
            {
                return true;
            }
            var slotParen = slotKey.IndexOf('(');
            var memParen = memberKey.IndexOf('(');
            if (slotParen < 0 || memParen < 0
                || slotKey.Substring(0, slotParen) != memberKey.Substring(0, memParen))
            {
                return false;
            }
            return ParameterCount(slotKey) == ParameterCount(memberKey);
        }

        private static int ParameterCount(string signatureKey)
        {
            var open = signatureKey.IndexOf('(');
            var close = signatureKey.LastIndexOf(')');
            if (open < 0 || close <= open + 1)
            {
                return 0;
            }
            return BilVerificationContext.SplitTopLevel(
                signatureKey.Substring(open + 1, close - open - 1)).Count;
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
