using System.Collections.Generic;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    // MIR 类型（MIDDLEWARE_ARCHITECTURE §3 MW3：MIR 保留 BIL 类型与符号身份，
    // 直到 MW6 发射前不做类型擦除）。此处仅是 BIL canonical 类型引用的
    // 驻留包装；归一与查询键投影归 MwTypeKey（Symbols 层），结构化类型
    // 知识（布局/ABI）归 Layout 层。
    public sealed class MirType
    {
        // 归一化后的 canonical（内建别名恒投影 core:: 形态）
        public string Canonical { get; }

        private MirType(string canonical)
        {
            Canonical = canonical;
        }

        // 类型键：Binding/Layout 分流的比较基（MwTypeKey.Of 同口径）
        public string Key => MwTypeKey.Of(Canonical);

        public bool IsVoid => MwTypeKey.IsVoid(Canonical);
        public bool IsString => MwTypeKey.IsString(Canonical);
        public bool IsAny => MwTypeKey.IsAny(Canonical);
        public bool IsObject => MwTypeKey.IsObject(Canonical);
        public bool IsAnyOrObject => IsAny || IsObject;

        private static readonly Dictionary<string, MirType> Interned = new(System.StringComparer.Ordinal);

        public static MirType Of(string typeRef)
        {
            var canonical = MwTypeKey.Normalize(typeRef);
            if (!Interned.TryGetValue(canonical, out var type))
            {
                type = new MirType(canonical);
                Interned.Add(canonical, type);
            }
            return type;
        }

        public override string ToString() => Canonical;
    }
}
