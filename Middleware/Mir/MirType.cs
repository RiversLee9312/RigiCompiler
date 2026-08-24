using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    // MIR 类型（MIDDLEWARE_ARCHITECTURE §3 MW3：MIR 保留 BIL 类型与符号身份，
    // 直到 MW6 发射前不做类型擦除）。此处仅是 BIL canonical 类型引用的
    // 驻留包装；归一经 BilVerificationContext.NormalizeTypeRef（§6.4 别名表
    // 同口径：.i32 ↔ core::i32、.f64 ↔ core::double、.string ↔ core::String），
    // 结构化类型知识（布局/ABI）归 Layout 层。
    public sealed class MirType
    {
        // 归一化后的 canonical（内建别名恒投影 core:: 形态）
        public string Canonical { get; }

        private MirType(string canonical)
        {
            Canonical = canonical;
        }

        // 类型键：Binding/Layout 分流的比较基。内建类型取 core:: 后段
        //（core::String → string）；.void → void；其余（用户类型/构造
        // 类型）原样
        public string Key
        {
            get
            {
                const string corePrefix = "core::";
                if (Canonical.StartsWith(corePrefix, System.StringComparison.Ordinal))
                {
                    var rest = Canonical.Substring(corePrefix.Length);
                    if (!rest.Contains('<') && !rest.Contains("::"))
                    {
                        return rest;
                    }
                }
                if (Canonical.Length > 1 && Canonical[0] == '.')
                {
                    return Canonical.Substring(1);
                }
                return Canonical;
            }
        }

        public bool IsVoid => Key == "void";
        public bool IsString => Key == "String";

        private static readonly Dictionary<string, MirType> Interned = new(System.StringComparer.Ordinal);

        public static MirType Of(string typeRef)
        {
            var canonical = BilVerificationContext.NormalizeTypeRef(typeRef);
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
