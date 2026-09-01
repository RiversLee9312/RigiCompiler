using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    // MW4 布局计划（数据类，LayoutEngine 产出；RUNTIME §2/§6–§9）。
    // 对象头 16B：[0..8) TypeSheet* 实际类型 + [8..12) RC u32 +
    // [12..16) 位打包域（颜色 2bit + 候选索引/标志位，MW12 前恒 0）；
    // class 对象 16B 对齐。

    public enum TypeLayoutKind
    {
        Class,
        Struct,
        Enum,
        // 空壳计划（MW4 批 2）：无实例布局，仅为 TypeSheet 地址身份
        //（iMap 键）与接口内槽序表（VTableSlots = 接口虚成员序）
        Interface,
        // wrapper 空壳（MW5 c3）：TypeInfo.wrappers / type.with 的地址身份
        Wrapper,
    }

    // 字段计划：canonical 字段符号 → 字节偏移与类型形态
    public sealed class FieldPlan
    {
        public string Symbol { get; }
        public int Offset { get; }
        public int Size { get; }
        public int Alignment { get; }
        // 16B 胖引用槽（class 引用/nullable/未解析引用类型字段；进 refMap kind0）
        public bool IsReferenceSlot { get; }
        // String 槽（{i8* data, i64 len}；进 refMap kind1）
        public bool IsStringSlot { get; }
        // 内联值类型字段的内层计划（struct/enum 内联时非空；其 refMap
        // 条目折算拼入外层）
        public TypeLayoutPlan? EmbeddedPlan { get; }
        // 类级隐藏 typeid（i64 TypeSheet*，不进 refMap）
        public bool IsHiddenTypeId { get; }

        internal FieldPlan(string symbol, int offset, int size, int alignment,
            bool isReferenceSlot, TypeLayoutPlan? embeddedPlan, bool isHiddenTypeId = false,
            bool isStringSlot = false)
        {
            Symbol = symbol;
            Offset = offset;
            Size = size;
            Alignment = alignment;
            IsReferenceSlot = isReferenceSlot;
            EmbeddedPlan = embeddedPlan;
            IsHiddenTypeId = isHiddenTypeId;
            IsStringSlot = isStringSlot;
        }
    }

    // 单类型布局计划（class 的 Size 含 16B 对象头；值类型从 0 起）
    public sealed class TypeLayoutPlan
    {
        // typeFlags 位（与 rigi_rt/arc.h 的 RIGI_TYPE_* 常量一致）
        public const uint FlagRich = 0x1;
        public const uint FlagShared = 0x2;
        public const uint FlagDisposable = 0x4;
        // 数组元素按 typeSize 内联（标量/String/struct/enum）；缺位则
        // 元素为 16B 胖引用槽（class/interface/nullable/array）
        public const uint FlagInlineValue = 0x8;
        // 数组对象（前缀 32B + 变长元素；与 arc.h RIGI_TYPE_ARRAY 对齐）
        public const uint FlagArray = 0x10u;
        // String 值（槽内 data = ARC 块 + 8；与 arc.h RIGI_TYPE_STRING 对齐）
        public const uint FlagString = 0x20u;

        public MwTypeSymbol Symbol { get; }
        public TypeLayoutKind Kind { get; }
        public int Size { get; }
        public int Alignment { get; }
        public uint TypeFlags { get; }
        // 全部实例字段（基类字段在前，保偏移序）
        public IReadOnlyList<FieldPlan> Fields { get; }
        // vtable 槽序 → 方法 canonical（槽 0 = $mw.init.dispatch；其后
        // 基类继承槽 → 本类自有槽 → 各 interface 实现段；override 复用
        // 基槽、同方法同偏移不变量）
        public IReadOnlyList<string> VTableSlots { get; }
        // iMap：接口 canonical → 本类 vtable 段 base offset（仅本类直接
        // implements 的接口；基类条目沿 baseTypeId 链上查，不复制）
        public IReadOnlyList<(string InterfaceType, int BaseOffset)> IMap { get; }
        // refMap：u16 = (kind<<14)|hop；kind0 胖引用 / kind1 String；
        // class 与值类型均计算（全标量则空）
        public ushort[] RefMap { get; }
        public int RefMapCount => RefMap.Length;
        // enum 判别值表（case → u32；仅 enum 非空）
        public IReadOnlyList<(MwCaseSymbol Case, uint Discriminant)> EnumCases { get; }
        // 基类计划（本地 class 基类可解析时；否则 null）
        public TypeLayoutPlan? BasePlan { get; }
        // 本类自有类级隐藏 typeid（不含基类；参数名 → 字节偏移）
        public IReadOnlyList<(string ParamName, int Offset)> HiddenTypeIdSlots { get; }
        // 传递 implements 闭包（含接口的父接口；TypeInfo.ifaceClosure）
        public IReadOnlyList<string> IfaceClosure { get; }

        internal TypeLayoutPlan(MwTypeSymbol symbol, TypeLayoutKind kind, int size,
            int alignment, uint typeFlags, IReadOnlyList<FieldPlan> fields,
            IReadOnlyList<string> vTableSlots,
            IReadOnlyList<(string, int)> iMap, ushort[] refMap,
            IReadOnlyList<(MwCaseSymbol, uint)> enumCases, TypeLayoutPlan? basePlan,
            IReadOnlyList<(string, int)>? hiddenTypeIdSlots = null,
            IReadOnlyList<string>? ifaceClosure = null)
        {
            Symbol = symbol;
            Kind = kind;
            Size = size;
            Alignment = alignment;
            TypeFlags = typeFlags;
            Fields = fields;
            VTableSlots = vTableSlots;
            IMap = iMap;
            RefMap = refMap;
            EnumCases = enumCases;
            BasePlan = basePlan;
            HiddenTypeIdSlots = hiddenTypeIdSlots ?? System.Array.Empty<(string, int)>();
            IfaceClosure = ifaceClosure ?? System.Array.Empty<string>();
        }
    }

    // 全部本地类型的布局计划表（登记序 = LayoutEngine 解析序，确定）
    public sealed class LayoutPlanTable : IMwDispatchQuery
    {
        private readonly Dictionary<string, TypeLayoutPlan> _plans = new(System.StringComparer.Ordinal);
        private readonly List<TypeLayoutPlan> _order = new();

        public IReadOnlyList<TypeLayoutPlan> Plans => _order;

        public TypeLayoutPlan? Find(string canonical)
        {
            if (_plans.TryGetValue(canonical, out var plan))
            {
                return plan;
            }
            var normalized = MwTypeKey.Normalize(canonical);
            if (normalized != canonical && _plans.TryGetValue(normalized, out plan))
            {
                return plan;
            }
            // 仅声明形（Task<TReturn>）回退到模板键 Task<1>；闭合构造
            // Task<core::i32> 不得命中模板，否则 ConstructedLayout 被短路
            if (!LooksLikeOpenGenericQuery(normalized))
            {
                return null;
            }
            var declKey = BilVerificationContext.DeclarationKeyOf(normalized);
            return declKey != normalized && _plans.TryGetValue(declKey, out plan)
                ? plan
                : null;
        }

        private static bool LooksLikeOpenGenericQuery(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0 || !typeRef.EndsWith(">", System.StringComparison.Ordinal))
            {
                return false;
            }
            var inner = typeRef.Substring(angle + 1, typeRef.Length - angle - 2);
            if (inner.Length == 0 || int.TryParse(inner, out _))
            {
                return false;
            }
            return inner.IndexOf(':') < 0 && inner.IndexOf('.') < 0
                && inner.IndexOf('<') < 0;
        }

        internal void Add(TypeLayoutPlan plan)
        {
            var key = GenericAbi.PlanKey(plan.Symbol);
            _plans.Add(key, plan);
            if (key != plan.Symbol.Canonical && !_plans.ContainsKey(plan.Symbol.Canonical))
            {
                _plans.Add(plan.Symbol.Canonical, plan);
            }
            _order.Add(plan);
        }

        // ===== IMwDispatchQuery（Mir 消费的派发闭包适配，不泄漏本层类型） =====

        public IReadOnlyList<string>? GetVTableSlots(string typeCanonical) =>
            Find(typeCanonical)?.VTableSlots;

        public bool IsClass(string typeCanonical) =>
            Find(typeCanonical) is { Kind: TypeLayoutKind.Class };

        public bool DerivesFrom(string derivedCanonical, string baseCanonical)
        {
            for (var current = Find(derivedCanonical); current != null; current = current.BasePlan)
            {
                if (current.Symbol.Canonical == baseCanonical)
                {
                    return true;
                }
            }
            return false;
        }

        public IReadOnlyList<(string IfaceType, int BaseOffset)>? GetIMap(string typeCanonical) =>
            Find(typeCanonical)?.IMap;

        public IReadOnlyList<string> AllClassCanonicals()
        {
            var list = new List<string>();
            foreach (var plan in _order)
            {
                if (plan.Kind == TypeLayoutKind.Class)
                {
                    list.Add(plan.Symbol.Canonical);
                }
            }
            return list;
        }
    }
}
