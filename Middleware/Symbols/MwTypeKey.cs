using RigiCompiler.Bil;

namespace RigiCompiler.Middleware.Symbols
{
    /// <summary>
    /// canonical 类型引用的查询键（Binding 唯一实现查询的判别基；Mir/Layout
    /// 同用此键）。归一经 BilVerificationContext.NormalizeTypeRef（§6.4
    /// 别名表同口径：.i32 ↔ core::i32、.f64 ↔ core::double、.string ↔
    /// core::String）；键投影：内建类型取 core:: 后段（core::String →
    /// String）；.void → void；其余（用户类型/构造类型）原样。结构化类型
    /// 知识（布局/ABI）归 Layout 层，不在此。
    /// </summary>
    public static class MwTypeKey
    {
        // BIL 类型引用 → canonical（内建别名恒投影 core:: 形态）
        public static string Normalize(string typeRef)
        {
            return BilVerificationContext.NormalizeTypeRef(typeRef);
        }

        // canonical → 查询键
        public static string Of(string canonical)
        {
            const string corePrefix = "core::";
            if (canonical.StartsWith(corePrefix, System.StringComparison.Ordinal))
            {
                var rest = canonical.Substring(corePrefix.Length);
                if (!rest.Contains('<') && !rest.Contains("::"))
                {
                    return rest;
                }
            }
            if (canonical.Length > 1 && canonical[0] == '.')
            {
                return canonical.Substring(1);
            }
            return canonical;
        }

        public static bool IsVoid(string canonical) => Of(canonical) == "void";

        public static bool IsString(string canonical) => Of(canonical) == "String";
    }
}
