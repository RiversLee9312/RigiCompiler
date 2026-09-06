using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// MW11a 合成类型通道（MIDDLEWARE_ARCHITECTURE §6 协程降级）：协程
    /// frame 是堆对象（tag2），按 D3-A 走 TypeSheet/refMap 体系。
    /// LayoutStage 在 MirBuild 之前、frame 类型 split 时才诞生，故由本
    /// 通道在 pass 期按需注册：合成内部 class（名 $mw.frame.&lt;fn
    /// canonical&gt;，无基类、字段平铺、无 vtable/iMap——vtable 仅槽 0
    /// init 分发器占位，托管字段进 refMap），注册进 Symbols 驻留 +
    /// Layout 计划表。Emit 的 TypeSheet/refMap 发射与 TypeLayout 映射
    /// 零特例消费（均遍历 Layout.Plans / 按 canonical 查表）。
    /// frame 字段符号 = frameCanonical + "#" + 槽名 + "@" + 槽类型
    /// canonical（槽名逐字嵌入，含 .this/.generic.* 形态——Method
    /// wrapper 隐藏槽已在字段符号内嵌方法 canonical 的先例同口径，
    /// WrapperAbi.MethodFieldSymbol）。
    /// </summary>
    public static class SyntheticTypePlanner
    {
        // frame 状态字段名（i32：0=原入口，N=挂起点恢复 state）
        public const string StateFieldName = "state";

        // 函数返回类型可能含多个泛型实参；MirType 会归一逗号空格，
        // 注册时必须同口径，否则 frame 局部与布局表出现两个名字。
        public static string FrameCanonicalOf(string fnCanonical) =>
            MwTypeKey.Normalize("$mw.frame." + fnCanonical);

        public static string FrameFieldSymbol(string frameCanonical, string slotName,
            string typeCanonical) => frameCanonical + "#" + slotName + "@" + typeCanonical;

        // frame 空 init canonical（合成体 = 裸 ret；字段零值由 rigi_alloc
        // 清零承担，singleton 空 init 同口径）
        public static string FrameInitCanonicalOf(string frameCanonical) =>
            frameCanonical + "$init()@.void";

        // 注册/复用合成 frame class：slots = state 之外的保存槽（槽名 +
        // 精确 MirType，调用方保序——frame 字段序确定性由其保证）。
        // 幂等：同 canonical 直返既有符号（同一 fn 重 split 属内部 bug，
        // 但多次注册请求须安全）
        public static MwTypeSymbol EnsureFrameType(MwContext context, string frameCanonical,
            IReadOnlyList<(string SlotName, MirType Type)> slots)
        {
            if (context.Layout == null)
            {
                throw new CompilerInternalException("合成 frame 类型要求 Layout 已挂载");
            }
            var existing = context.Symbols.FindType(frameCanonical);
            if (existing != null)
            {
                return existing;
            }

            // 声明：class + 字段成员 + 空 init 成员（init 供 MirNewObject /
            // 动态 new 分发器的 init 族扫描命中，singleton 同形态）
            var declaration = new BilTypeDeclaration(frameCanonical, BilTypeKind.Class);
            var i32 = MirType.Of(".i32");
            var fieldTypes = new List<(string Symbol, MirType Type)>(slots.Count + 1)
            {
                (FrameFieldSymbol(frameCanonical, StateFieldName, i32.Canonical), i32),
            };
            foreach (var (slotName, type) in slots)
            {
                fieldTypes.Add((FrameFieldSymbol(frameCanonical, slotName, type.Canonical), type));
            }
            foreach (var (symbol, _) in fieldTypes)
            {
                declaration.Members.Add(new BilSimpleMemberDeclaration(
                    BilMemberKind.Field, symbol));
            }
            declaration.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                FrameInitCanonicalOf(frameCanonical),
                new BilModifier[] { new BilKeywordModifier(BilKeyword.Init) }));
            var typeSymbol = context.Symbols.RegisterSyntheticType(declaration);

            // 布局：对象头 16B 起字段平铺（ClassLayout 同口径，无基类/
            // 隐藏 typeid/隐藏存储）；refMap 经 RefMapBuilder 折算
            var fields = new List<FieldPlan>(fieldTypes.Count);
            var refEntries = new List<RefMapBuilder.RefSite>();
            var offset = LayoutEngine.ObjectHeaderSize;
            foreach (var (symbol, type) in fieldTypes)
            {
                var info = ClassifyFrameField(context, type);
                offset = LayoutEngine.AlignUp(offset, info.Alignment);
                fields.Add(new FieldPlan(symbol, offset, info.Size, info.Alignment,
                    info.IsReferenceSlot, info.EmbeddedPlan, isStringSlot: info.IsStringSlot));
                RefMapBuilder.CollectRefSite(refEntries, info, offset);
                offset += info.Size;
            }
            var size = LayoutEngine.AlignUp(offset, LayoutEngine.ReferenceSlotSize);
            var vTableSlots = new List<string>();
            VTablePlanner.EnsureInitDispatchSlot(vTableSlots);
            var plan = new TypeLayoutPlan(typeSymbol, TypeLayoutKind.Class, size,
                LayoutEngine.ReferenceSlotSize, 0u, fields, vTableSlots,
                new List<(string, int)>(),
                RefMapBuilder.BuildRefMap(fields, refEntries, LayoutEngine.ObjectHeaderSize),
                System.Array.Empty<(MwCaseSymbol, uint)>(), basePlan: null);
            context.Layout.Add(plan);
            return typeSymbol;
        }

        // frame 字段分类：类级 typeid（.typeid）是 8B TypeSheet* 内联槽
        //（ClassifyFieldType 无此 case——类级隐藏 typeid 槽由 GenericAbi
        // 专道处理，不进通用字段分类）；其余复用通用字段分类（托管引用
        // 16B 胖槽/String 槽/内联值类型折算进 refMap）
        private static LayoutEngine.FieldTypeInfo ClassifyFrameField(MwContext context,
            MirType type)
        {
            if (TypeLayout.IsTypeId(type))
            {
                return new LayoutEngine.FieldTypeInfo(GenericAbi.TypeIdSlotSize,
                    GenericAbi.TypeIdSlotAlign, false, null);
            }
            return LayoutEngine.ClassifyFieldType(type.Canonical, context.Symbols,
                context.Layout!, new HashSet<string>(System.StringComparer.Ordinal));
        }
    }
}
