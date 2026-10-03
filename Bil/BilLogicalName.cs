namespace RigiCompiler.Bil;

/// <summary>模块私有后缀只参与链接身份；保留名判断读取逻辑名字。</summary>
public static class BilLogicalName
{
    public static string Of(string name)
    {
        var marker = name.LastIndexOf("__m_", StringComparison.Ordinal);
        return marker >= 0 && name.Length - marker == 68
            && name.AsSpan(marker + 4).ContainsAnyExcept("0123456789abcdef".AsSpan()) == false
            ? name[..marker] : name;
    }
    public static string Method(string canonical)
    {
        var dollar = canonical.IndexOf('$');
        if (dollar < 0) return "";
        var rest = canonical[(dollar + 1)..];
        if (rest.StartsWith('$')) rest = rest[1..];
        if (rest.StartsWith(".static.", StringComparison.Ordinal)) rest = rest[8..];
        var end = rest.IndexOfAny(['(', '@']);
        return Of(end < 0 ? rest : rest[..end]);
    }
    public static bool IsGlobalInitializer(string canonical) => IsGlobalInitializerName(Method(canonical));
    public static bool IsGlobalInitializerName(string name) => name == BilSpellings.GlobalsInitFunctionName
        || name.StartsWith(BilSpellings.GlobalsInitFunctionName + ".", StringComparison.Ordinal)
        && name.Length == BilSpellings.GlobalsInitFunctionName.Length + 65
        && !name.AsSpan(BilSpellings.GlobalsInitFunctionName.Length + 1).ContainsAnyExcept("0123456789abcdef".AsSpan());
}
