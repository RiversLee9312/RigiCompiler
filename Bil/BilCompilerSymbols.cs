using System.Security.Cryptography;
using System.Text;

namespace RigiCompiler.Bil;

/// <summary>编译器标准库的源逻辑协议名到准确链接身份；普通接口不能授予这些绑定。</summary>
public static class BilCompilerSymbols
{
    public const string MetadataPrefix = "compiler.runtime.";
    private static string Key(string logical) => MetadataPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logical))).ToLowerInvariant();
    public static string Logical(string canonical)
    {
        var result = new StringBuilder();
        for (int i = 0; i < canonical.Length;)
        {
            if (canonical.AsSpan(i).StartsWith("__m_", StringComparison.Ordinal) && canonical.Length - i >= 68
                && !canonical.AsSpan(i + 4, 64).ContainsAnyExcept("0123456789abcdef".AsSpan())
                && (i + 68 == canonical.Length || "(.<#@$>,".Contains(canonical[i + 68]))) { i += 68; continue; }
            result.Append(canonical[i++]);
        }
        return result.ToString();
    }
    public static void Register(BilModule module, string canonical)
    {
        var logical = Logical(canonical); var key = Key(logical);
        if (module.Metadata.Any(m => m.Key == key)) return;
        module.Metadata.Add(new BilMetadataEntry(key, BilScalarType.String, "\"" + canonical + "\""));
    }
    public static string Resolve(BilModule module, string logical)
    {
        var entry = module.Metadata.SingleOrDefault(m => m.Key == Key(logical));
        if (entry == null) return logical;
        if (entry.Type != BilScalarType.String) throw new BilLinkException("compiler runtime 绑定要求 string");
        var canonical = BilScalarLiteral.DecodeString(entry.LiteralText);
        if (Logical(canonical) != logical) throw new BilLinkException("compiler runtime 绑定的逻辑 ABI 不符");
        return canonical;
    }
    // 前缀协议仅遍历已经由可信标准库导入的准确绑定，不扫描普通声明的逻辑名。
    public static string? ResolvePrefix(BilModule module, string logicalPrefix)
    {
        string? result = null;
        foreach (var entry in module.Metadata.Where(m => m.Key.StartsWith(MetadataPrefix, StringComparison.Ordinal)))
        {
            if (entry.Type != BilScalarType.String) throw new BilLinkException("compiler runtime 绑定要求 string");
            var canonical = BilScalarLiteral.DecodeString(entry.LiteralText);
            var logical = Logical(canonical);
            if (entry.Key != Key(logical)) throw new BilLinkException("compiler runtime 绑定键与准确 ABI 不符");
            if (!logical.StartsWith(logicalPrefix, StringComparison.Ordinal)) continue;
            if (result != null) throw new BilLinkException("compiler runtime 前缀不唯一: " + logicalPrefix);
            result = canonical;
        }
        return result;
    }
    public static string ResolveField(BilModule module, string logical)
    {
        var exact = Resolve(module, logical);
        if (exact != logical) return exact;
        var at = logical.LastIndexOf('@');
        var declaration = at < 0 ? null : ResolvePrefix(module, logical[..(at + 1)]);
        // 字段槽的闭合值类型由调用方代入；声明宿主和字段链接名来自可信绑定。
        return declaration == null ? logical : declaration[..declaration.LastIndexOf('@')] + logical[at..];
    }
}
